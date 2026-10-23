using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace SmarTTY.Ssh;

/// <summary>
/// The SOCKS5 proxy of one SSH session (Tools > SOCKS5 proxy over SSH): the equivalent of
/// <c>ssh -N -D 127.0.0.1:1080 -o ServerAliveInterval=30 user@host</c>. The proxy listens on this computer; every connection
/// that another program makes to it is carried through the SSH connection and leaves from the SSH server (SOCKS4/4a/5, TCP
/// CONNECT, no authentication; host names are resolved by the server, as with <c>socks5h://</c>). It lives as long as the SSH
/// connection: after a reconnect <see cref="Reattach"/> brings it back, when the session ends <see cref="Stop"/> closes it.
/// </summary>
public sealed class SocksProxyHost
{
    private readonly object _Sync = new object();

    private SshSession _Session;

    private bool _Running;

    private string _BindAddress = "127.0.0.1";

    private int _Port = 1080;

    private int _KeepAliveSeconds;

    private string _LastError = "";

    private DateTime _LastErrorTime = DateTime.MinValue;

    private readonly List<string> _History = new List<string>();

    private const int MaxHistoryLines = 500;

    private const int ReattachAttempts = 4;

    /// <summary>Raised from any thread with a time-stamped line: what the host does and the connections that failed.</summary>
    public event Action<string> Log;

    /// <summary>True from Start until Stop, also while the SSH connection is down and waits to be re-established.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_Sync)
            {
                return _Running;
            }
        }
    }

    /// <summary>True when the proxy is started and its listener works on the current SSH connection.</summary>
    public bool IsListening
    {
        get
        {
            SshSession session;
            lock (_Sync)
            {
                session = _Running ? _Session : null;
            }
            return session != null && !session.IsConnectionClosed && session.IsDynamicForwardActive;
        }
    }

    public string BindAddress
    {
        get
        {
            lock (_Sync)
            {
                return _BindAddress;
            }
        }
    }

    public int Port
    {
        get
        {
            lock (_Sync)
            {
                return _Port;
            }
        }
    }

    /// <summary>The lines logged so far (the last 500), for a window that is opened while the proxy is already running.</summary>
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

    /// <summary>
    /// Starts the proxy on <paramref name="bindAddress"/>:<paramref name="port"/> of this computer through the SSH connection
    /// <paramref name="session"/>. Throws when the address is not usable (for example the port is taken by another program).
    /// </summary>
    /// <param name="keepAliveSeconds">Seconds between keepalive messages of the connection (like ServerAliveInterval); 0 leaves it as it is.</param>
    public void Start(SshSession session, string bindAddress, int port, int keepAliveSeconds)
    {
        if (session == null)
        {
            throw new ArgumentNullException("session");
        }
        if (!IPAddress.TryParse((bindAddress ?? "").Trim(), out IPAddress address))
        {
            throw new ArgumentException("\"" + bindAddress + "\" is not an IP address.");
        }
        if (port < 1 || port > 65535)
        {
            throw new ArgumentException("The port must be between 1 and 65535.");
        }
        Stop();
        if (keepAliveSeconds > 0)
        {
            session.ConfigureKeepAlive(keepAliveSeconds);
        }
        session.DynamicForwardError += OnForwardError;
        try
        {
            session.StartDynamicForward(address.ToString(), (uint)port);
        }
        catch
        {
            session.DynamicForwardError -= OnForwardError;
            throw;
        }
        lock (_Sync)
        {
            _Session = session;
            _Running = true;
            _BindAddress = address.ToString();
            _Port = port;
            _KeepAliveSeconds = keepAliveSeconds;
        }
        Report("SOCKS5 proxy is listening on " + Format(address.ToString(), port) + ". Connections made to it leave from the SSH server.");
    }

    /// <summary>Closes the listener and forgets the proxy; connections that are already open are closed with it.</summary>
    public void Stop()
    {
        SshSession session;
        bool wasRunning;
        lock (_Sync)
        {
            session = _Session;
            wasRunning = _Running;
            _Session = null;
            _Running = false;
        }
        if (session != null)
        {
            session.DynamicForwardError -= OnForwardError;
            try
            {
                session.StopDynamicForward();
            }
            catch
            {
            }
        }
        if (wasRunning)
        {
            Report("SOCKS5 proxy stopped.");
        }
    }

    /// <summary>
    /// Opens the listener again on a new SSH connection after a reconnect (the old connection took its listener with it).
    /// Does nothing when the proxy is not running. Blocks for a moment, so it must not be called on the UI thread.
    /// </summary>
    public void Reattach(SshSession session)
    {
        string address;
        int port;
        int keepAlive;
        lock (_Sync)
        {
            if (!_Running || ReferenceEquals(_Session, session))
            {
                return;
            }
            address = _BindAddress;
            port = _Port;
            keepAlive = _KeepAliveSeconds;
        }
        Exception last = null;
        for (int attempt = 0; attempt < ReattachAttempts; attempt++)
        {
            try
            {
                if (keepAlive > 0)
                {
                    session.ConfigureKeepAlive(keepAlive);
                }
                session.DynamicForwardError += OnForwardError;
                try
                {
                    session.StartDynamicForward(address, (uint)port);
                }
                catch
                {
                    session.DynamicForwardError -= OnForwardError;
                    throw;
                }
                lock (_Sync)
                {
                    if (!_Running)
                    {
                        // Stopped while the connection was being restored.
                        session.StopDynamicForward();
                        session.DynamicForwardError -= OnForwardError;
                        return;
                    }
                    _Session = session;
                }
                Report("The SSH connection was re-established: the SOCKS5 proxy listens on " + Format(address, port) + " again.");
                return;
            }
            catch (Exception exception)
            {
                last = exception;
                // The listener of the old connection may need a moment to release the port.
                Thread.Sleep(500);
            }
        }
        lock (_Sync)
        {
            _Session = null;
        }
        Report("Could not restore the SOCKS5 proxy after reconnecting: " + OneLine(last) + " Start it again from Tools > SOCKS5 proxy over SSH.");
    }

    private void OnForwardError(string message)
    {
        lock (_Sync)
        {
            // A client that keeps retrying a refused target would fill the log with the same line.
            DateTime now = DateTime.UtcNow;
            if (message == _LastError && now - _LastErrorTime < TimeSpan.FromSeconds(2))
            {
                return;
            }
            _LastError = message;
            _LastErrorTime = now;
        }
        Report("Connection failed: " + message.Replace('\r', ' ').Replace('\n', ' '));
    }

    private static string Format(string address, int port)
    {
        return (address.Contains(":") ? "[" + address + "]" : address) + ":" + port;
    }

    private static string OneLine(Exception exception)
    {
        return (exception == null) ? "" : exception.Message.Replace('\r', ' ').Replace('\n', ' ');
    }
}
