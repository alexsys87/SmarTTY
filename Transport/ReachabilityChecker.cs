using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SmarTTY.Transport;

/// <summary>
/// Whether the TCP port of a connection answers, checked in the background so that the list of connections never waits for it. A
/// check opens the TCP connection and closes it at once (it cannot tell whether a port listens without connecting, so a device that
/// serves one client at a time may be busy for a moment). The results are kept by host and port for the life of the checker.
/// </summary>
/// <remarks>
/// Two ways to ask: <see cref="Check"/> with <c>recheck</c> starts a new check when the last one is older than
/// <see cref="ResultLifetimeMs"/> (the "Probe all connections in the background" option of the launcher), and without it a port is
/// checked once only, the first time it is asked for (the mark of the connections that did not answer when the launcher opened).
/// It was the nested <c>MachineConnectionTester</c> of <c>ConnectionSelectionWindow</c>.
/// </remarks>
internal sealed class ReachabilityChecker : IDisposable
{
    /// <summary>What is known of a port.</summary>
    public enum State
    {
        /// <summary>No answer yet: the first check of the port is under way.</summary>
        Checking,

        /// <summary>The port accepted a TCP connection.</summary>
        Succeeded,

        /// <summary>The port refused the connection, or nothing answered in <see cref="CheckTimeoutMs"/>.</summary>
        Failed
    }

    /// <summary>A result is trusted this long when rechecks are asked for: a device that appears later (or goes away) shows up then.</summary>
    public const long ResultLifetimeMs = 30000;

    /// <summary>A host that does not answer is given up after this long instead of waiting for the operating system.</summary>
    public const int CheckTimeoutMs = 5000;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private sealed class Context
    {
        // The result being shown; the previous one stays until a new check has an answer, so the list does not flicker.
        public State State = State.Checking;

        public long CheckedAtMs;

        public bool InProgress;

        public TcpConnector Connector;
    }

    private struct Key : IEquatable<Key>
    {
        public string Host;

        public int Port;

        public bool Equals(Key other) => Port == other.Port && string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object obj) => obj is Key other && Equals(other);

        public override int GetHashCode() => (Host == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Host)) * 31 + Port;
    }

    private readonly object _lock = new object();

    private readonly Dictionary<Key, Context> _contexts = new Dictionary<Key, Context>();

    private readonly Action<Action> _startWork;

    private readonly Func<string, int, int, bool> _connect;

    private bool _disposed;

    /// <summary>A check finished with a result that differs from the one shown (or with the first one); raised on the thread of the check.</summary>
    public event Action BackgroundCheckCompleted;

    /// <summary>A checker that connects with <see cref="TcpConnector"/> on the thread pool.</summary>
    public ReachabilityChecker()
        : this(work => ThreadPool.QueueUserWorkItem(_ => work()), null)
    {
    }

    /// <summary>
    /// A checker that runs its checks through <paramref name="startWork"/> and connects with <paramref name="connect"/> (host, port, timeout
    /// in milliseconds; true when the port answered); null for the real connect. For the tests.
    /// </summary>
    internal ReachabilityChecker(Action<Action> startWork, Func<string, int, int, bool> connect)
    {
        _startWork = startWork;
        _connect = connect;
    }

    /// <summary>
    /// The last known state of the port. A check is started in the background when the port was never checked, and with
    /// <paramref name="recheck"/> also when the last result is older than <see cref="ResultLifetimeMs"/>; without it a port is checked once.
    /// </summary>
    public State Check(string host, int port, bool recheck)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
        {
            return State.Failed;
        }
        Key key = new Key { Host = host.Trim(), Port = port };
        Context context;
        lock (_lock)
        {
            if (_disposed)
            {
                return State.Failed;
            }
            if (!_contexts.TryGetValue(key, out context))
            {
                context = _contexts[key] = new Context();
            }
            bool due = context.CheckedAtMs == 0 || (recheck && Clock.ElapsedMilliseconds - context.CheckedAtMs >= ResultLifetimeMs);
            if (context.InProgress || !due)
            {
                return context.State;
            }
            context.InProgress = true;
        }
        _startWork(() => RunCheck(context, key.Host, key.Port));
        return context.State;
    }

    private void RunCheck(Context context, string host, int port)
    {
        TcpConnector connector = _connect == null ? new TcpConnector() : null;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            context.Connector = connector;
        }
        State result;
        try
        {
            if (_connect != null)
            {
                result = _connect(host, port, CheckTimeoutMs) ? State.Succeeded : State.Failed;
            }
            else
            {
                connector.Connect(host, port, CheckTimeoutMs).Close();
                result = State.Succeeded;
            }
        }
        catch
        {
            result = State.Failed;
        }
        bool changed;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            changed = context.CheckedAtMs == 0 || context.State != result;
            context.State = result;
            context.CheckedAtMs = Math.Max(1, Clock.ElapsedMilliseconds);
            context.InProgress = false;
            context.Connector = null;
        }
        if (changed)
        {
            BackgroundCheckCompleted?.Invoke();
        }
    }

    /// <summary>Ends the checks that are still waiting for an answer; none of them reports back after this, and nothing is checked any more.</summary>
    public void Dispose()
    {
        List<TcpConnector> running = new List<TcpConnector>();
        lock (_lock)
        {
            _disposed = true;
            foreach (Context context in _contexts.Values)
            {
                if (context.Connector != null)
                {
                    running.Add(context.Connector);
                }
            }
        }
        foreach (TcpConnector connector in running)
        {
            connector.Abort();
        }
    }
}
