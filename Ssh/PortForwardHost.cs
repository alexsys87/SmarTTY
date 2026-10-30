using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SmarTTY.Ssh;

/// <summary>
/// The port forwardings of one SSH session (Tools > Port forwarding): local ("ssh -L") and remote ("ssh -R") forwardings that
/// the user adds, starts and stops. A forwarding that is started stays wanted until it is stopped: after a reconnect
/// <see cref="OnConnected"/> opens it again on the new connection, and the forwardings marked "start on connect" are started
/// there the first time. The methods that start or stop forwardings block (the server answers a remote forwarding, a
/// stopped listener waits for its connections), so they must not be called on the UI thread.
/// </summary>
public sealed class PortForwardHost
{
    /// <summary>One forwarding in the list, with its state on the current connection.</summary>
    public sealed class Entry
    {
        internal Entry(PortForwardRule rule)
        {
            Rule = rule;
        }

        public PortForwardRule Rule { get; internal set; }

        internal SshPortForward Forward;

        internal SshSession Session;

        // The "start on connect" of the profile has worked once; after that a forwarding stopped by hand stays stopped.
        internal bool AutoStartDone;

        /// <summary>Started and not stopped since: restored after a reconnect.</summary>
        public bool Wanted { get; internal set; }

        /// <summary>Why the last start failed (cleared by a successful start).</summary>
        public string LastError { get; internal set; }

        public bool IsActive => Forward != null && Forward.IsStarted;

        /// <summary>The port that listens while active (the one chosen by the system for a local port 0).</summary>
        public uint ActivePort
        {
            get
            {
                SshPortForward forward = Forward;
                return forward == null ? 0 : forward.BoundPort;
            }
        }

        public string StatusText
        {
            get
            {
                if (IsActive)
                {
                    uint port = ActivePort;
                    return Rule.Kind == PortForwardKind.Local && Rule.BindPort == 0 ? "Active on port " + port : "Active";
                }
                if (Wanted)
                {
                    return "Waiting for the connection";
                }
                return string.IsNullOrEmpty(LastError) ? "Stopped" : "Failed: " + LastError;
            }
        }
    }

    private readonly object _Sync = new object();

    private readonly List<Entry> _Entries = new List<Entry>();

    private readonly List<string> _History = new List<string>();

    private const int MaxHistoryLines = 500;

    // The listener of the old connection may need a moment to release its port after a reconnect.
    private const int ReattachAttempts = 8;

    private bool _Loaded;

    /// <summary>Raised from any thread with a time-stamped line: what the host does and the forwarded connections that failed.</summary>
    public event Action<string> Log;

    /// <summary>Raised from any thread when the list or the state of an entry changed.</summary>
    public event Action Changed;

    public Entry[] Entries
    {
        get
        {
            lock (_Sync)
            {
                return _Entries.ToArray();
            }
        }
    }

    /// <summary>The rules in list order, to be saved with the profile.</summary>
    public List<PortForwardRule> Rules
    {
        get
        {
            lock (_Sync)
            {
                return _Entries.Select(entry => entry.Rule.Clone()).ToList();
            }
        }
    }

    public bool HasActive
    {
        get
        {
            lock (_Sync)
            {
                return _Entries.Any(entry => entry.Wanted);
            }
        }
    }

    /// <summary>Fills the list from the profile once (later connections keep the list as edited).</summary>
    public void Load(IEnumerable<PortForwardRule> rules)
    {
        lock (_Sync)
        {
            if (_Loaded)
            {
                return;
            }
            _Loaded = true;
            if (rules != null)
            {
                _Entries.AddRange(rules.Where(rule => rule != null).Select(rule => new Entry(rule.Clone())));
            }
        }
        RaiseChanged();
    }

    public string[] GetHistory()
    {
        lock (_Sync)
        {
            return _History.ToArray();
        }
    }

    public void Report(string message)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
        lock (_Sync)
        {
            _History.Add(line);
            if (_History.Count > MaxHistoryLines)
            {
                _History.RemoveRange(0, _History.Count - MaxHistoryLines);
            }
        }
        try
        {
            Log?.Invoke(line);
        }
        catch
        {
        }
    }

    public Entry Add(PortForwardRule rule)
    {
        Entry entry = new Entry(rule.Clone());
        lock (_Sync)
        {
            _Loaded = true;
            _Entries.Add(entry);
        }
        RaiseChanged();
        return entry;
    }

    /// <summary>Changes the rule of an entry. A running forwarding is stopped; the caller starts it again when it should run.</summary>
    public void Replace(Entry entry, PortForwardRule rule)
    {
        Stop(entry, report: false);
        lock (_Sync)
        {
            entry.Rule = rule.Clone();
            entry.LastError = null;
        }
        RaiseChanged();
    }

    /// <summary>Changes only whether the forwarding starts with the connection; a running forwarding keeps running.</summary>
    public void SetStartOnConnect(Entry entry, bool startOnConnect)
    {
        lock (_Sync)
        {
            PortForwardRule rule = entry.Rule.Clone();
            rule.StartOnConnect = startOnConnect;
            entry.Rule = rule;
        }
        RaiseChanged();
    }

    public void Remove(Entry entry)
    {
        Stop(entry, report: false);
        lock (_Sync)
        {
            _Entries.Remove(entry);
        }
        RaiseChanged();
    }

    /// <summary>Starts the forwarding on <paramref name="session"/>. Throws (and records the error) when it cannot be started.</summary>
    public void Start(SshSession session, Entry entry)
    {
        if (session == null)
        {
            throw new ArgumentNullException("session");
        }
        PortForwardRule rule;
        lock (_Sync)
        {
            rule = entry.Rule;
        }
        string problem = rule.Validate();
        if (problem != null)
        {
            throw new ArgumentException(problem);
        }
        Stop(entry, report: false);
        SshPortForward forward;
        try
        {
            forward = Open(session, rule);
        }
        catch (Exception exception)
        {
            lock (_Sync)
            {
                entry.LastError = OneLine(exception);
            }
            Report("Could not start " + rule.SshOption + ": " + OneLine(exception));
            RaiseChanged();
            throw;
        }
        Attach(entry, session, forward);
        Report("Started " + rule.SshOption + Describe(entry) + ".");
    }

    public void Stop(Entry entry)
    {
        Stop(entry, report: true);
    }

    /// <summary>Stops every forwarding (the session ends).</summary>
    public void StopAll()
    {
        foreach (Entry entry in Entries)
        {
            Stop(entry, report: false);
        }
    }

    /// <summary>
    /// A connection was established (the first one or after a reconnect): the forwardings that were running are opened again
    /// on it, and those marked "start on connect" are started the first time. Failures are logged, not thrown.
    /// </summary>
    public void OnConnected(SshSession session)
    {
        foreach (Entry entry in Entries)
        {
            bool restore;
            bool autoStart;
            PortForwardRule rule;
            lock (_Sync)
            {
                rule = entry.Rule;
                restore = entry.Wanted && !ReferenceEquals(entry.Session, session);
                autoStart = !entry.Wanted && rule.StartOnConnect && !entry.AutoStartDone;
            }
            if (restore)
            {
                Restore(session, entry, rule);
            }
            else if (autoStart)
            {
                try
                {
                    Start(session, entry);
                    lock (_Sync)
                    {
                        entry.AutoStartDone = true;
                    }
                }
                catch (Exception)
                {
                    // Logged by Start; tried again on the next connection.
                }
            }
        }
    }

    private void Restore(SshSession session, Entry entry, PortForwardRule rule)
    {
        DisposeForward(entry);
        Exception last = null;
        for (int attempt = 0; attempt < ReattachAttempts; attempt++)
        {
            try
            {
                SshPortForward forward = Open(session, rule);
                lock (_Sync)
                {
                    if (!entry.Wanted || !_Entries.Contains(entry))
                    {
                        // Stopped or removed while the connection was being restored.
                        forward.Dispose();
                        return;
                    }
                }
                Attach(entry, session, forward);
                Report("The SSH connection was re-established: " + rule.SshOption + " works again" + Describe(entry) + ".");
                return;
            }
            catch (Exception exception)
            {
                last = exception;
                Thread.Sleep(500);
            }
        }
        lock (_Sync)
        {
            entry.Wanted = false;
            entry.LastError = OneLine(last);
        }
        Report("Could not restore " + rule.SshOption + " after reconnecting: " + OneLine(last));
        RaiseChanged();
    }

    private static SshPortForward Open(SshSession session, PortForwardRule rule)
    {
        string bindAddress = rule.BindAddress.Trim();
        string targetHost = rule.TargetHost.Trim();
        return rule.Kind == PortForwardKind.Local
            ? session.OpenLocalForward(bindAddress, (uint)rule.BindPort, targetHost, (uint)rule.TargetPort)
            : session.OpenRemoteForward(bindAddress, (uint)rule.BindPort, targetHost, (uint)rule.TargetPort);
    }

    private void Attach(Entry entry, SshSession session, SshPortForward forward)
    {
        PortForwardRule rule = entry.Rule;
        forward.Error += message => Report(rule.SshOption + ": a connection failed: " + message.Replace('\r', ' ').Replace('\n', ' '));
        lock (_Sync)
        {
            entry.Forward = forward;
            entry.Session = session;
            entry.Wanted = true;
            entry.LastError = null;
        }
        RaiseChanged();
    }

    private void Stop(Entry entry, bool report)
    {
        bool wasWanted;
        lock (_Sync)
        {
            wasWanted = entry.Wanted;
            entry.Wanted = false;
        }
        DisposeForward(entry);
        if (report && wasWanted)
        {
            Report("Stopped " + entry.Rule.SshOption + ".");
        }
        RaiseChanged();
    }

    private void DisposeForward(Entry entry)
    {
        SshPortForward forward;
        lock (_Sync)
        {
            forward = entry.Forward;
            entry.Forward = null;
            entry.Session = null;
        }
        forward?.Dispose();
    }

    private static string Describe(Entry entry)
    {
        PortForwardRule rule = entry.Rule;
        if (rule.Kind == PortForwardKind.Local && rule.BindPort == 0)
        {
            return " (listening on port " + entry.ActivePort + ")";
        }
        return "";
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
        }
    }

    private static string OneLine(Exception exception)
    {
        return exception == null ? "" : exception.Message.Replace('\r', ' ').Replace('\n', ' ');
    }
}
