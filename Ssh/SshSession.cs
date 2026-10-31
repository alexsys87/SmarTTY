using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Compression;
using SmarTTY.IO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;

namespace SmarTTY.Ssh;

/// <summary>
/// An SSH connection based on SSH.NET.
/// The constructor connects and performs the key exchange; authentication is then driven by the caller
/// (possibly with several attempts) on the same connection, matching the flow of the connection UI.
/// </summary>
public class SshSession : IDisposable
{
    [Flags]
    public enum AuthMethods
    {
        None = 0,
        Password = 1,
        KeyboardInteractive = 2,
        PublicKey = 4
    }

    public struct KeyboardInteractivePrompt
    {
        public string Text;

        public bool Echo;

        public override string ToString()
        {
            return Text;
        }
    }

    public delegate void KeyboardInteractiveHandler(string name, string instruction, KeyboardInteractivePrompt[] prompts, string[] responses);

    public class HostKeyInfo
    {
        /// <summary>
        /// Key type as stored in the public key blob (e.g. "ssh-rsa", "ssh-ed25519").
        /// </summary>
        public string Algorithm;

        public byte[] KeyData;
    }

    public struct SessionStats
    {
        public long BytesReceived;

        public long BytesSent;
    }

    /// <summary>
    /// Lets the caller run authentication attempts inside SSH.NET's connect sequence.
    /// SSH.NET authenticates as part of SshClient.Connect(); the methods registered there only hand the
    /// SSH.NET session over to the attempts requested via <see cref="Run"/>, until one of them succeeds
    /// or the gate is aborted.
    /// </summary>
    private sealed class AuthenticationGate
    {
        private sealed class DeferredMethod : AuthenticationMethod
        {
            private readonly string _name;

            private readonly AuthenticationGate _gate;

            public override string Name => _name;

            public DeferredMethod(string userName, string name, AuthenticationGate gate)
                : base(userName)
            {
                _name = name;
                _gate = gate;
            }

            public override AuthenticationResult Authenticate(Session session)
            {
                return _gate.Serve(session);
            }
        }

        private readonly object _sync = new object();

        private readonly object _runLock = new object();

        private Func<Session, AuthenticationResult> _request;

        private AuthenticationResult _result;

        private Exception _error;

        private bool _requestDone;

        private bool _serving;

        private bool _served;

        private bool _stopped;

        public readonly ManualResetEvent Ready = new ManualResetEvent(initialState: false);

        // One placeholder per method type: SSH.NET only calls methods the server allows,
        // and the first one called serves all attempts.
        public AuthenticationMethod[] CreateMethods(string userName)
        {
            return new AuthenticationMethod[3]
            {
                new DeferredMethod(userName, "publickey", this),
                new DeferredMethod(userName, "keyboard-interactive", this),
                new DeferredMethod(userName, "password", this)
            };
        }

        private AuthenticationResult Serve(Session session)
        {
            lock (_sync)
            {
                if (_served || _stopped)
                {
                    return AuthenticationResult.Failure;
                }
                _served = true;
                _serving = true;
                Ready.Set();
            }
            try
            {
                while (true)
                {
                    Func<Session, AuthenticationResult> request;
                    lock (_sync)
                    {
                        while (_request == null && !_stopped)
                        {
                            Monitor.Wait(_sync);
                        }
                        if (_request == null)
                        {
                            return AuthenticationResult.Failure;
                        }
                        request = _request;
                    }
                    AuthenticationResult failure = AuthenticationResult.Failure;
                    Exception error = null;
                    try
                    {
                        failure = request(session);
                    }
                    catch (Exception exception)
                    {
                        error = exception;
                    }
                    lock (_sync)
                    {
                        _request = null;
                        _result = failure;
                        _error = error;
                        _requestDone = true;
                        Monitor.PulseAll(_sync);
                    }
                    if (failure == AuthenticationResult.Success)
                    {
                        return AuthenticationResult.Success;
                    }
                }
            }
            finally
            {
                lock (_sync)
                {
                    _serving = false;
                    Monitor.PulseAll(_sync);
                }
            }
        }

        public AuthenticationResult Run(Func<Session, AuthenticationResult> request)
        {
            lock (_runLock)
            {
                lock (_sync)
                {
                    if (!_serving)
                    {
                        if (_stopped)
                        {
                            // The connection went away (or the session was closed) while nobody was authenticating.
                            throw new IOException("The SSH connection was closed before authentication was completed");
                        }
                        throw new InvalidOperationException("The SSH connection is not waiting for authentication");
                    }
                    _request = request;
                    _requestDone = false;
                    Monitor.PulseAll(_sync);
                    while (!_requestDone && _serving)
                    {
                        Monitor.Wait(_sync);
                    }
                    if (!_requestDone)
                    {
                        _request = null;
                        throw new IOException("The SSH connection was closed during authentication");
                    }
                    if (_error != null)
                    {
                        ExceptionDispatchInfo.Capture(_error).Throw();
                    }
                    return _result;
                }
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                _stopped = true;
                Monitor.PulseAll(_sync);
            }
        }
    }

    public static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, 0);

    private readonly SshClient _client;

    // For the proxies SSH.NET does not offer (SOCKS4, HTTPS); null otherwise.
    private ProxyRelay _proxyRelay;

    private readonly AuthenticationGate _authGate = new AuthenticationGate();

    private readonly ManualResetEvent _connectDone = new ManualResetEvent(initialState: false);

    private Exception _connectError;

    private HostKeyInfo _hostKey;

    /// <summary>What the session was opened with, to open a second connection to the same server (see <see cref="CheckPublicKeyLogin"/>).</summary>
    private readonly SshConnectionParameters _parameters;

    private long _bytesReceived;

    private long _bytesSent;

    private readonly BlockingCollection<Action> _events = new BlockingCollection<Action>();

    private readonly HashSet<SshShellChannel> _channels = new HashSet<SshShellChannel>();

    // Keeps helper commands without a PTY from overlapping with setting up a PTY channel (Dropbear on OpenWRT).
    internal readonly PtySetupGate PtyGate = new PtySetupGate(TimeSpan.FromSeconds(5));

    private bool _connectionClosed;

    private readonly Thread _eventThread;

    private int _disposed;

    private int _connectionLostRaised;

    public readonly string HostName;

    public readonly int Port;

    public readonly bool EnableOnTheFlyTar;

    internal string Tag;

    public Func<string> tempDirectoryProvider;

    public string UserName { get; private set; }

    public bool IsAuthenticated => _connectDone.WaitOne(0) && _connectError == null;

    public bool KeepAliveEnabled => _client.KeepAliveInterval > TimeSpan.Zero;

    /// <summary>
    /// True once the connection has gone away or the session was disposed. <see cref="IsAuthenticated"/> says nothing
    /// about it: it stays true for the whole life of the object.
    /// </summary>
    public bool IsConnectionClosed
    {
        get
        {
            lock (_channels)
            {
                return _connectionClosed;
            }
        }
    }

    /// <summary>Software version of the server from its identification string (e.g. "OpenSSH_9.6p1").</summary>
    public string ServerVersion => _client.ConnectionInfo?.ServerVersion;

    private RemoteOsKind? _remoteOs;

    /// <summary>
    /// What the server runs on: what its identification string says (Win32-OpenSSH calls itself OpenSSH_for_Windows), or what
    /// was learned by asking it and set here. Unknown means that nothing says it is not a Unix host.
    /// </summary>
    public RemoteOsKind RemoteOs
    {
        get => _remoteOs ?? RemoteOsDetector.FromServerVersion(ServerVersion);
        set => _remoteOs = value == RemoteOsKind.Unknown ? (RemoteOsKind?)null : value;
    }

    /// <summary>X11 forwarding of this connection; null unless <see cref="EnableX11Forwarding"/> was called.</summary>
    public X11Forwarding X11 { get; private set; }

    public SessionStats Statistics => new SessionStats
    {
        BytesReceived = Interlocked.Read(ref _bytesReceived),
        BytesSent = Interlocked.Read(ref _bytesSent)
    };

    public static string UserSshDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    private readonly object _connectionLostSync = new object();

    private EventHandler _connectionLost;

    /// <summary>
    /// Raised once, on a thread pool thread, when the connection goes away (not when the session is disposed).
    /// The loss is latched: a handler added after it happened is called at once, so a loss in the window before the
    /// subscription (while the owner is still setting the session up, or while the password prompt is open) is
    /// not missed.
    /// </summary>
    public event EventHandler ConnectionLost
    {
        add
        {
            bool lostAlready;
            lock (_connectionLostSync)
            {
                _connectionLost += value;
                lostAlready = _connectionLostRaised != 0;
            }
            if (lostAlready && Volatile.Read(ref _disposed) == 0)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    value(this, EventArgs.Empty);
                });
            }
        }
        remove
        {
            lock (_connectionLostSync)
            {
                _connectionLost -= value;
            }
        }
    }

    /// <summary>
    /// Connects to the server and completes the key exchange. Returns once the server waits for
    /// authentication (or has accepted the connection without any). Setting <paramref name="cancelEvent"/>
    /// aborts the attempt with <see cref="OperationCanceledException"/>.
    /// </summary>
    public SshSession(SshConnectionParameters parameters, WaitHandle cancelEvent = null)
    {
        _parameters = parameters.Clone();
        HostName = parameters.DisplayHostName ?? parameters.Host;
        Port = parameters.Port;
        EnableOnTheFlyTar = parameters.EnableOnTheFlyTar;
        _client = new SshClient(CreateConnectionInfo(parameters));
        _client.HostKeyReceived += OnHostKeyReceived;
        _client.ErrorOccurred += (object sender, ExceptionEventArgs e) => RaiseConnectionLost();
        _eventThread = new Thread(EventThreadProc)
        {
            IsBackground = true,
            Name = "SSH events: " + HostName
        };
        _eventThread.Start();
        Thread connectThread = new Thread(ConnectThreadProc)
        {
            IsBackground = true,
            Name = "SSH connect: " + HostName
        };
        connectThread.Start();
        List<WaitHandle> handles = new List<WaitHandle> { _authGate.Ready, _connectDone };
        if (cancelEvent != null)
        {
            handles.Add(cancelEvent);
        }
        int signaled = WaitHandle.WaitAny(handles.ToArray());
        if (signaled == 2)
        {
            Dispose();
            throw new OperationCanceledException();
        }
        if (_connectDone.WaitOne(0) && _connectError != null)
        {
            Dispose();
            ExceptionDispatchInfo.Capture(_connectError).Throw();
        }
        if (IsAuthenticated)
        {
            // The server accepted the connection without authentication.
            UserName = parameters.UserName;
        }
    }

    /// <summary>
    /// Whether the proxy refused the connection for missing or wrong credentials (HTTP or HTTPS "407 Proxy Authentication
    /// Required", a SOCKS5 proxy that rejected the user name and password or wants them, a SOCKS4 proxy that rejected the
    /// user ID), so asking for them can help.
    /// </summary>
    public static bool IsProxyAuthenticationFailure(Exception exception)
    {
        if (!(exception is ProxyException))
        {
            return false;
        }
        string message = exception.Message ?? "";
        return message.Contains("407") ||
               message.IndexOf("authentication failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("No acceptable authentication methods", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private ConnectionInfo CreateConnectionInfo(SshConnectionParameters parameters)
    {
        AuthenticationMethod[] methods = _authGate.CreateMethods(parameters.UserName);
        ConnectionInfo connectionInfo;
        if (string.IsNullOrEmpty(parameters.ProxyHost))
        {
            connectionInfo = new ConnectionInfo(parameters.Host, parameters.Port, parameters.UserName, methods);
        }
        else if (ProxyRelay.IsNeeded(parameters.ProxyType))
        {
            // SSH.NET connects to the loopback end of the relay, which goes through the proxy to the server.
            _proxyRelay = new ProxyRelay(parameters);
            connectionInfo = new ConnectionInfo(IPAddress.Loopback.ToString(), _proxyRelay.LocalPort, parameters.UserName, methods);
        }
        else if (parameters.ProxyType == SshProxyType.Socks5)
        {
            // SSH.NET fails with a NullReferenceException when a SOCKS5 proxy asks for a login and there is no user name;
            // with empty ones the proxy refuses the login, which is reported as such and makes SmarTTY ask for it.
            connectionInfo = new ConnectionInfo(parameters.Host, parameters.Port, parameters.UserName, ProxyTypes.Socks5, parameters.ProxyHost, parameters.ProxyPort, parameters.ProxyUserName ?? "", parameters.ProxyPassword ?? "", methods);
        }
        else
        {
            connectionInfo = new ConnectionInfo(parameters.Host, parameters.Port, parameters.UserName, ProxyTypes.Http, parameters.ProxyHost, parameters.ProxyPort, parameters.ProxyUserName, parameters.ProxyPassword, methods);
        }
        connectionInfo.Timeout = parameters.ConnectTimeout;
        // Let the server limit concurrent channels (as with libssh2) instead of blocking in SSH.NET.
        connectionInfo.MaxSessions = 100;
        ConfigureCompression(connectionInfo.CompressionAlgorithms, parameters.EnableCompression);
        if (!string.IsNullOrEmpty(parameters.PreferredHostKeyType))
        {
            PreferHostKeyType(connectionInfo.HostKeyAlgorithms, parameters.PreferredHostKeyType);
        }
        return connectionInfo;
    }

    private static void ConfigureCompression(IOrderedDictionary<string, Func<Compressor>> algorithms, bool enable)
    {
        if (enable)
        {
            algorithms["zlib@openssh.com"] = () => new ZlibOpenSsh();
            algorithms["zlib"] = () => new Zlib();
            algorithms.SetPosition("zlib@openssh.com", 0);
            algorithms.SetPosition("zlib", 1);
            return;
        }
        foreach (string name in algorithms.Keys.Where((string name) => name != "none").ToList())
        {
            algorithms.Remove(name);
        }
    }

    private static void PreferHostKeyType<T>(IOrderedDictionary<string, T> algorithms, string keyType)
    {
        string[] names = (keyType == "ssh-rsa") ? new string[3] { "rsa-sha2-512", "rsa-sha2-256", "ssh-rsa" } : new string[1] { keyType };
        int position = 0;
        foreach (string name in names)
        {
            if (algorithms.ContainsKey(name))
            {
                algorithms.SetPosition(name, position++);
            }
        }
    }

    private void ConnectThreadProc()
    {
        try
        {
            // The proxy handshake comes first, so that its refusal is the error of the connection.
            _proxyRelay?.Open();
            _client.Connect();
            Session session = SshNetChannel.GetSession(_client);
            if (session != null)
            {
                session.Disconnected += (object sender, EventArgs e) => RaiseConnectionLost();
            }
        }
        catch (Exception exception)
        {
            _connectError = exception;
        }
        finally
        {
            _authGate.Stop();
            _connectDone.Set();
        }
    }

    private void OnHostKeyReceived(object sender, HostKeyEventArgs e)
    {
        _hostKey = new HostKeyInfo
        {
            Algorithm = ReadKeyType(e.HostKey) ?? e.HostKeyName,
            KeyData = e.HostKey
        };
        // The key is verified by the caller against its own store (see GetHostKey()).
        e.CanTrust = true;
    }

    // The public key blob starts with the key format name as an SSH string.
    private static string ReadKeyType(byte[] keyBlob)
    {
        if (keyBlob == null || keyBlob.Length < 4)
        {
            return null;
        }
        int length = (keyBlob[0] << 24) | (keyBlob[1] << 16) | (keyBlob[2] << 8) | keyBlob[3];
        if (length <= 0 || length > keyBlob.Length - 4)
        {
            return null;
        }
        return Encoding.ASCII.GetString(keyBlob, 4, length);
    }

    private void EventThreadProc()
    {
        foreach (Action action in _events.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Runs a channel event handler on the session's event thread, keeping the order of arrival
    /// and keeping slow handlers off SSH.NET's receive thread.
    /// </summary>
    internal void Dispatch(Action action)
    {
        try
        {
            _events.Add(action);
        }
        catch (InvalidOperationException)
        {
            // The session is being disposed; late events are dropped.
        }
    }

    internal void AddReceivedBytes(long count)
    {
        Interlocked.Add(ref _bytesReceived, count);
    }

    internal void AddSentBytes(long count)
    {
        Interlocked.Add(ref _bytesSent, count);
    }

    internal void RegisterChannel(SshShellChannel channel)
    {
        lock (_channels)
        {
            if (_connectionClosed)
            {
                throw new IOException("The SSH connection is closed");
            }
            _channels.Add(channel);
        }
    }

    internal void UnregisterChannel(SshShellChannel channel)
    {
        lock (_channels)
        {
            _channels.Remove(channel);
        }
    }

    // SSH.NET does not report the closing of channels when the connection goes away, so it is done here.
    private void CloseAllChannels()
    {
        SshShellChannel[] channels;
        lock (_channels)
        {
            _connectionClosed = true;
            channels = _channels.ToArray();
        }
        foreach (SshShellChannel channel in channels)
        {
            channel.OnConnectionClosed();
        }
        // The X11 connections are not shell channels and SSH.NET's Disconnected does not fire for a dropped TCP
        // connection: without this the remote X programs' windows stay frozen and their reader threads wait.
        X11?.CloseConnections();
    }

    private void RaiseConnectionLost()
    {
        // Runs on SSH.NET's own threads (a server that drops the connection after a refused login): an exception here
        // would end the whole program.
        try
        {
            CloseAllChannels();
        }
        catch
        {
        }
        // Do not leave the connect thread waiting for credentials on a connection that is already gone.
        _authGate.Stop();
        EventHandler handlers;
        lock (_connectionLostSync)
        {
            if (Volatile.Read(ref _disposed) != 0 || _connectionLostRaised != 0)
            {
                return;
            }
            _connectionLostRaised = 1;
            handlers = _connectionLost;
        }
        if (handlers != null)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    handlers(this, EventArgs.Empty);
                }
                catch
                {
                    // A handler of the lost connection must not take the process down from the thread pool.
                }
            });
        }
    }

    public string GetTempPath(string fileName)
    {
        return (tempDirectoryProvider?.Invoke() ?? "/tmp") + "/" + fileName;
    }

    public HostKeyInfo GetHostKey()
    {
        return _hostKey ?? throw new InvalidOperationException("The server did not present a host key");
    }

    /// <summary>
    /// Signs in with the key on a new connection to the same server (same proxy, same user) and closes it again: the only
    /// way to know that the server will let the key in, as opposed to the key line being in authorized_keys. The new
    /// connection must present the host key of this one, so the key is only offered to the server that was approved.
    /// Returns null when the server accepted the key, otherwise the reason. Setting <paramref name="cancelEvent"/> ends
    /// the connecting with <see cref="OperationCanceledException"/>.
    /// </summary>
    public string CheckPublicKeyLogin(ISshKeyPair keyPair, WaitHandle cancelEvent = null)
    {
        HostKeyInfo approvedHostKey = GetHostKey();
        SshConnectionParameters parameters = _parameters.Clone();
        // The same host key algorithm as here, so that a server with several host keys presents the approved one.
        parameters.PreferredHostKeyType = approvedHostKey.Algorithm;
        SshSession verificationSession = null;
        try
        {
            verificationSession = new SshSession(parameters, cancelEvent);
            HostKeyInfo hostKey = verificationSession.GetHostKey();
            if (hostKey.Algorithm != approvedHostKey.Algorithm || !hostKey.KeyData.SequenceEqual(approvedHostKey.KeyData))
            {
                return "the server presented a different host key on the new connection";
            }
            return verificationSession.AcceptsPublicKey(parameters.UserName, keyPair) ? null : "the server rejected the key";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
        finally
        {
            try
            {
                verificationSession?.Dispose();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Enables periodic keepalive messages (sent by SSH.NET in the background); 0 disables them.
    /// </summary>
    public void ConfigureKeepAlive(int intervalSeconds)
    {
        _client.KeepAliveInterval = (intervalSeconds > 0) ? TimeSpan.FromSeconds(intervalSeconds) : Timeout.InfiniteTimeSpan;
        if (intervalSeconds > 0)
        {
            EnableTcpKeepAlive(intervalSeconds);
        }
    }

    /// <summary>
    /// SSH.NET's keep-alive messages are written without waiting for an answer, so with a pulled cable they only fill the
    /// send buffer and the receive thread keeps waiting. TCP keep-alive probes make the OS give up on a silent peer and fail
    /// the pending read, which raises <see cref="ConnectionLost"/> without any input from the user.
    /// </summary>
    private void EnableTcpKeepAlive(int intervalSeconds)
    {
        try
        {
            Session session = SshNetChannel.GetSession(_client);
            System.Reflection.FieldInfo socketField = session?.GetType()
                .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .FirstOrDefault((System.Reflection.FieldInfo field) => field.FieldType == typeof(System.Net.Sockets.Socket));
            if (!(socketField?.GetValue(session) is System.Net.Sockets.Socket socket))
            {
                return;
            }
            // Windows: on/off, idle time (ms) before the first probe, interval (ms) between probes (10 probes are sent).
            byte[] values = new byte[12];
            BitConverter.GetBytes(1).CopyTo(values, 0);
            BitConverter.GetBytes(intervalSeconds * 1000).CopyTo(values, 4);
            BitConverter.GetBytes(Math.Min(intervalSeconds, 5) * 1000).CopyTo(values, 8);
            socket.IOControl(System.Net.Sockets.IOControlCode.KeepAliveValues, values, null);
        }
        catch (Exception)
        {
            // Best effort: the SSH keep-alive messages still work, and the platform may not support the control code.
        }
    }


    /// <summary>
    /// Requests X11 forwarding on every PTY channel opened from now on (terminal tabs and Smart Terminal commands).
    /// <paramref name="connectToXServer"/> returns a socket connected to the local X server, or null if there is none.
    /// </summary>
    public X11Forwarding EnableX11Forwarding(Func<System.Net.Sockets.Socket> connectToXServer)
    {
        EnsureAuthenticated();
        if (X11 == null)
        {
            Session session = SshNetChannel.GetSession(_client) ?? throw new InvalidOperationException("The SSH session is not connected");
            X11 = new X11Forwarding(this, session, connectToXServer);
        }
        return X11;
    }

    #region Authentication

    private bool RunAuthentication(string userName, Func<Session, AuthenticationResult> attempt)
    {
        return RunAttempt(userName, attempt) == AuthenticationResult.Success;
    }

    /// <summary>
    /// Runs one attempt and tells how it ended. PartialSuccess means that the server took this step of a chain
    /// (AuthenticationMethods password,keyboard-interactive in sshd_config) and wants another method before it lets the
    /// user in.
    /// </summary>
    private AuthenticationResult RunAttempt(string userName, Func<Session, AuthenticationResult> attempt)
    {
        if (IsAuthenticated)
        {
            return AuthenticationResult.Success;
        }
        AuthenticationResult result = _authGate.Run(attempt);
        if (result != AuthenticationResult.Success)
        {
            return result;
        }
        // SshClient.Connect() completes once the successful attempt returns.
        _connectDone.WaitOne();
        if (_connectError != null)
        {
            ExceptionDispatchInfo.Capture(_connectError).Throw();
        }
        UserName = userName;
        return AuthenticationResult.Success;
    }

    public AuthMethods GetAuthMethods(string userName)
    {
        string[] allowed = null;
        if (RunAuthentication(userName, (Session session) =>
        {
            using NoneAuthenticationMethod method = new NoneAuthenticationMethod(userName);
            AuthenticationResult authResult = method.Authenticate(session);
            allowed = method.AllowedAuthentications;
            return authResult;
        }))
        {
            return AuthMethods.None;
        }
        return ParseAuthMethods(allowed);
    }

    private static AuthMethods ParseAuthMethods(string[] allowed)
    {
        AuthMethods authMethods = AuthMethods.None;
        foreach (string name in allowed ?? new string[0])
        {
            switch (name)
            {
                case "password":
                    authMethods |= AuthMethods.Password;
                    break;
                case "keyboard-interactive":
                    authMethods |= AuthMethods.KeyboardInteractive;
                    break;
                case "publickey":
                    authMethods |= AuthMethods.PublicKey;
                    break;
            }
        }
        return authMethods;
    }

    /// <summary>
    /// Authenticates with a password, using the password or keyboard-interactive method depending on what the server allows.
    /// </summary>
    public bool Authenticate(string userName, string password, KeyboardInteractiveHandler promptsForUser = null)
    {
        AuthMethods authMethods = GetAuthMethods(userName);
        if (IsAuthenticated)
        {
            return true;
        }
        if (authMethods == AuthMethods.None)
        {
            throw new Exception("The server reported no supported authentication protocols");
        }
        if ((authMethods & (AuthMethods.Password | AuthMethods.KeyboardInteractive)) == 0)
        {
            // Neither is announced (a server that lists only public keys may still take them): try both.
            if (!AuthenticateWithPassword(userName, password))
            {
                return AuthenticateKeyboardInteractiveWithPassword(userName, password, promptsForUser);
            }
            return true;
        }
        return ContinueWithPassword(userName, authMethods, () => password, promptsForUser);
    }

    /// <summary>
    /// Goes on with the password and keyboard-interactive steps among <paramref name="authMethods"/>. The server may ask
    /// for more than one method (AuthenticationMethods password,keyboard-interactive): a step that it accepts but that
    /// does not finish the login is a partial success, and the methods it names next are the ones to go on with. Each
    /// method is tried once. <paramref name="passwordSource"/> is asked when a step needs the password, so that a login
    /// that does not need it never asks for it.
    /// </summary>
    private bool ContinueWithPassword(string userName, AuthMethods authMethods, Func<string> passwordSource, KeyboardInteractiveHandler promptsForUser)
    {
        bool passwordTried = false;
        bool interactiveTried = false;
        bool passwordAccepted = false;
        while (true)
        {
            AuthenticationResult result;
            AuthMethods next;
            if ((authMethods & AuthMethods.Password) != 0 && !passwordTried)
            {
                passwordTried = true;
                result = TryPassword(userName, passwordSource(), out next);
                passwordAccepted = result == AuthenticationResult.PartialSuccess;
            }
            else if ((authMethods & AuthMethods.KeyboardInteractive) != 0 && !interactiveTried)
            {
                interactiveTried = true;
                result = TryKeyboardInteractive(userName, CreatePasswordPromptHandler(passwordSource(), promptsForUser, passwordAccepted), out next);
            }
            else
            {
                return false;
            }
            if (result == AuthenticationResult.Success)
            {
                return true;
            }
            if (result != AuthenticationResult.PartialSuccess)
            {
                return false;
            }
            authMethods = next;
        }
    }

    public bool AuthenticateWithPassword(string userName, string password)
    {
        return TryPassword(userName, password, out _) == AuthenticationResult.Success;
    }

    private AuthenticationResult TryPassword(string userName, string password, out AuthMethods allowedNext)
    {
        string[] allowed = null;
        AuthenticationResult result = RunAttempt(userName, (Session session) =>
        {
            using PasswordAuthenticationMethod method = new PasswordAuthenticationMethod(userName, password);
            AuthenticationResult authResult = method.Authenticate(session);
            allowed = method.AllowedAuthentications;
            return authResult;
        });
        allowedNext = ParseAuthMethods(allowed);
        return result;
    }

    /// <summary>
    /// Answers the password prompt with the password. The other prompts of the server (a verification code, a one-time
    /// password, the new password of an expired account) are handed to <paramref name="promptsForUser"/> with just those
    /// prompts, which fills their responses; without it they are answered with an empty text.
    /// </summary>
    public bool AuthenticateKeyboardInteractiveWithPassword(string userName, string password, KeyboardInteractiveHandler promptsForUser = null)
    {
        return AuthenticateKeyboardInteractive(userName, CreatePasswordPromptHandler(password, promptsForUser, passwordAlreadyAccepted: false));
    }

    // passwordAlreadyAccepted: an earlier step of the chain took the password, so a prompt that is not recognizable as
    // the password prompt is for the user and must not get the password as a guess.
    private static KeyboardInteractiveHandler CreatePasswordPromptHandler(string password, KeyboardInteractiveHandler promptsForUser, bool passwordAlreadyAccepted)
    {
        KeyboardInteractiveAnswers answers = new KeyboardInteractiveAnswers(password, passwordAlreadyAccepted);
        return (string name, string instruction, KeyboardInteractivePrompt[] prompts, string[] responses) =>
        {
            List<int> forUser = answers.Fill(prompts.Select((KeyboardInteractivePrompt prompt) => prompt.Text).ToArray(), responses);
            if (forUser.Count == 0 || promptsForUser == null)
            {
                return;
            }
            KeyboardInteractivePrompt[] asked = forUser.Select((int index) => prompts[index]).ToArray();
            string[] answered = new string[asked.Length];
            promptsForUser(name, instruction, asked, answered);
            for (int i = 0; i < asked.Length; i++)
            {
                responses[forUser[i]] = answered[i];
            }
        };
    }

    public bool AuthenticateKeyboardInteractive(string userName, KeyboardInteractiveHandler callback)
    {
        return TryKeyboardInteractive(userName, callback, out _) == AuthenticationResult.Success;
    }

    private AuthenticationResult TryKeyboardInteractive(string userName, KeyboardInteractiveHandler callback, out AuthMethods allowedNext)
    {
        string[] allowed = null;
        AuthenticationResult result = RunAttempt(userName, (Session session) =>
        {
            using KeyboardInteractiveAuthenticationMethod method = new KeyboardInteractiveAuthenticationMethod(userName);
            method.AuthenticationPrompt += (object sender, AuthenticationPromptEventArgs e) =>
            {
                KeyboardInteractivePrompt[] prompts = e.Prompts.Select((AuthenticationPrompt prompt) => new KeyboardInteractivePrompt
                {
                    Text = prompt.Request,
                    Echo = prompt.IsEchoed
                }).ToArray();
                string[] responses = new string[prompts.Length];
                callback("", e.Instruction, prompts, responses);
                for (int i = 0; i < prompts.Length; i++)
                {
                    e.Prompts[i].Response = responses[i] ?? "";
                }
            };
            AuthenticationResult authResult = method.Authenticate(session);
            allowed = method.AllowedAuthentications;
            return authResult;
        });
        allowedNext = ParseAuthMethods(allowed);
        return result;
    }

    /// <summary>
    /// Authenticates with the key. A server that wants another method after the key (AuthenticationMethods
    /// publickey,password) is answered with <paramref name="accountPassword"/> and <paramref name="promptsForUser"/> as
    /// <see cref="Authenticate"/> does; without <paramref name="accountPassword"/> such a login counts as refused.
    /// </summary>
    public bool AuthenticateWithPublicKey(string userName, ISshKeyPair keyPair, Func<string> accountPassword = null, KeyboardInteractiveHandler promptsForUser = null)
    {
        AuthenticationResult result = TryPublicKey(userName, keyPair, out AuthMethods next);
        return FinishChain(userName, result, next, accountPassword, promptsForUser);
    }

    /// <summary>
    /// Whether the server takes the key as the step of the login that it is: a server that wants a password or a code
    /// after the key answers with a partial success, and the key is as good as accepted. Used to check a freshly
    /// installed key, which must not be thrown away because the account has more than one factor.
    /// </summary>
    public bool AcceptsPublicKey(string userName, ISshKeyPair keyPair)
    {
        AuthenticationResult result = TryPublicKey(userName, keyPair, out _);
        return result == AuthenticationResult.Success || result == AuthenticationResult.PartialSuccess;
    }

    private AuthenticationResult TryPublicKey(string userName, ISshKeyPair keyPair, out AuthMethods allowedNext)
    {
        string[] allowed = null;
        AuthenticationResult result = RunAttempt(userName, (Session session) =>
        {
            IPrivateKeySource key = keyPair.CreatePrivateKeySource();
            try
            {
                using PrivateKeyAuthenticationMethod method = new PrivateKeyAuthenticationMethod(userName, key);
                AuthenticationResult authResult = method.Authenticate(session);
                allowed = method.AllowedAuthentications;
                return authResult;
            }
            finally
            {
                (key as IDisposable)?.Dispose();
            }
        });
        allowedNext = ParseAuthMethods(allowed);
        return result;
    }

    // The end of a login whose first step was a key: done, refused, or a partial success that goes on with the password.
    private bool FinishChain(string userName, AuthenticationResult result, AuthMethods next, Func<string> accountPassword, KeyboardInteractiveHandler promptsForUser)
    {
        if (result == AuthenticationResult.Success)
        {
            return true;
        }
        if (result != AuthenticationResult.PartialSuccess || accountPassword == null)
        {
            return false;
        }
        return ContinueWithPassword(userName, next, accountPassword, promptsForUser);
    }

    /// <summary>
    /// Authenticates with a private key file (RSA, ECDSA or Ed25519) in the OpenSSH, PEM, PKCS#8 or PuTTY (.ppk) format.
    /// If no file is specified, the first of id_ed25519, id_ecdsa and id_rsa found in <see cref="UserSshDirectory"/> is used.
    /// <paramref name="passphraseRequired"/> is set if the key is encrypted and could not be loaded with the given passphrase.
    /// A key that cannot be loaded for another reason (damaged, not a private key, unsupported format) is an error.
    /// </summary>
    public bool AuthenticateWithKeyFile(string userName, out bool passphraseRequired, string keyFile = null, string passphrase = null, Func<string> accountPassword = null, KeyboardInteractiveHandler promptsForUser = null)
    {
        passphraseRequired = false;
        if (string.IsNullOrEmpty(keyFile))
        {
            keyFile = new string[3] { "id_ed25519", "id_ecdsa", "id_rsa" }.Select((string name) => Path.Combine(UserSshDirectory, name)).FirstOrDefault(File.Exists) ?? throw new IOException("No OpenSSH private key found in " + UserSshDirectory);
        }
        if (!File.Exists(keyFile))
        {
            throw new IOException(keyFile + " does not exist.");
        }
        PrivateKeyFile key;
        try
        {
            key = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(keyFile) : new PrivateKeyFile(keyFile, passphrase);
        }
        catch (Exception exception)
        {
            // A passphrase helps only an encrypted key (no passphrase given, or a wrong one). A damaged key or a format
            // that SSH.NET does not read would otherwise be answered with the same prompt again and again.
            ThrowIfKeyFileCannotBeFixedByPassphrase(keyFile, exception);
            passphraseRequired = true;
            return false;
        }
        using (key)
        {
            string[] allowed = null;
            AuthenticationResult result = RunAttempt(userName, (Session session) =>
            {
                using PrivateKeyAuthenticationMethod method = new PrivateKeyAuthenticationMethod(userName, key);
                AuthenticationResult authResult = method.Authenticate(session);
                allowed = method.AllowedAuthentications;
                return authResult;
            });
            return FinishChain(userName, result, ParseAuthMethods(allowed), accountPassword, promptsForUser);
        }
    }

    /// <summary>
    /// Called after the key file could not be loaded: returns when it is an encrypted key (the passphrase is what is
    /// missing or wrong) and otherwise throws an error that says what is wrong with the file.
    /// </summary>
    private static void ThrowIfKeyFileCannotBeFixedByPassphrase(string keyFile, Exception loadError)
    {
        PrivateKeyFileKind kind;
        try
        {
            kind = PrivateKeyFileFormat.Inspect(keyFile);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            throw new IOException("Cannot read the private key file " + keyFile + ": " + exception.Message, exception);
        }
        switch (kind)
        {
            case PrivateKeyFileKind.Encrypted:
                return;
            case PrivateKeyFileKind.NotAPrivateKey:
                throw new InvalidDataException(keyFile + " is not a PEM, OpenSSH or PuTTY private key (" + loadError.Message + ")", loadError);
            default:
                throw new InvalidDataException("The private key " + keyFile + " cannot be loaded: " + loadError.Message, loadError);
        }
    }

    #endregion

    #region Channels and commands

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
        {
            throw new InvalidOperationException("The SSH session is not authenticated");
        }
        // IsAuthenticated stays true after the connection is gone; say so instead of failing somewhere inside SSH.NET.
        if (IsConnectionClosed)
        {
            throw new IOException("The SSH connection is closed");
        }
    }

    /// <param name="utf8Tty">
    /// With a PTY: whether the remote tty is told that input is UTF-8 (IUTF8), so the kernel line discipline erases
    /// a whole character on Backspace. Off for a shell that counts UTF-8 bytes (see Terminal/ByteColumns.cs).
    /// </param>
    public SshShellChannel OpenShellChannel(string command = null, string terminalType = null, ExtendedDataMode extendedDataMode = ExtendedDataMode.Normal, bool utf8Tty = true)
    {
        EnsureAuthenticated();
        return new SshShellChannel(this, SshNetChannel.Create(_client), command, terminalType, extendedDataMode, utf8Tty);
    }

    /// <summary>
    /// Opens a channel running the server's <paramref name="subsystem"/> (e.g. "sftp").
    /// </summary>
    public SshShellChannel OpenSubsystemChannel(string subsystem)
    {
        EnsureAuthenticated();
        return new SshShellChannel(this, SshNetChannel.Create(_client), null, null, ExtendedDataMode.Ignore, subsystem);
    }

    /// <summary>
    /// Runs a command and returns its output (stdout and stderr merged), or null if it could not be run.
    /// <paramref name="exitStatus"/> is null when the command was not run, timed out, the connection was lost, or
    /// the server closed the channel without reporting an exit status; a command terminated by a signal reports -1.
    /// With <paramref name="timeout"/> a command that has not finished by then (a hung <c>df</c> on a dead NFS mount) is
    /// abandoned: its channel is closed and the result is null, so that it does not keep the <see cref="PtyGate"/> busy.
    /// </summary>
    public List<string> RunCommand(string command, out int? exitStatus, string terminalType = null, TimeSpan? timeout = null)
    {
        bool plainHelper = terminalType == null;
        if (plainHelper)
        {
            PtyGate.BeginPlainHelper();
        }
        try
        {
            using SshShellChannel channel = OpenShellChannel(command, terminalType, ExtendedDataMode.Merge);
            int timedOut = 0;
            // Disposing the channel ends the reading below (it reads as the end of the output) and WaitClosed.
            using Timer watchdog = timeout == null ? null : new Timer((object _) =>
            {
                Interlocked.Exchange(ref timedOut, 1);
                try
                {
                    // Not Dispose: that waits for the server to confirm the close, and it does not while the command runs.
                    channel.Abandon();
                }
                catch
                {
                }
            }, null, timeout.Value, Timeout.InfiniteTimeSpan);
            List<string> lines = new List<string>();
            using (StreamReader reader = new StreamReader(channel.Output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                }
            }
            channel.WaitClosed();
            if (Volatile.Read(ref timedOut) != 0)
            {
                exitStatus = null;
                return null;
            }
            if (channel.ClosedByConnectionLoss)
            {
                // The connection went away while the command was running: the output is cut short and there is no
                // exit status, which must not read as "the command succeeded" (callers treat null as "not run").
                exitStatus = null;
                return null;
            }
            if (channel.HasExitStatus)
            {
                exitStatus = channel.ExitStatus;
            }
            else if (channel.ExitSignal != null)
            {
                // A signal is a failure, but the caller may still want to show which one ended the command.
                exitStatus = -1;
            }
            else
            {
                // The server closed the channel without an exit status (or the channel failed on its side), so the
                // command may not have run at all. Reporting 0 here used to turn "unknown" into "success".
                exitStatus = null;
            }
            return lines;
        }
        catch
        {
            exitStatus = null;
            return null;
        }
        finally
        {
            if (plainHelper)
            {
                PtyGate.EndPlainHelper();
            }
        }
    }

    /// <summary>
    /// Throws when the connection is gone. A null from <see cref="RunCommand"/> / <see cref="RunCommandChecked"/> means
    /// "the command failed" and also "there is no connection": callers that act on the first meaning (offer sudo,
    /// say that a directory is missing, say that no package manager exists) call this first.
    /// </summary>
    public void ThrowIfConnectionLost()
    {
        if (IsConnectionClosed)
        {
            throw new IOException("The connection to the server was lost.");
        }
    }

    /// <summary>
    /// Runs a command and returns its output; if <paramref name="requireSuccess"/> is set, returns null when the command fails.
    /// A null result is also returned when the connection is lost (see <see cref="ThrowIfConnectionLost"/>).
    /// </summary>
    public List<string> RunCommandChecked(string command, bool requireSuccess, string terminalType = null, TimeSpan? timeout = null)
    {
        List<string> output = RunCommand(command, out int? exitStatus, terminalType, timeout);
        if (requireSuccess && exitStatus != 0)
        {
            return null;
        }
        return output;
    }

    public ScpChannelStream OpenUpload(string path, long length, DateTime modifyTime = default(DateTime), int? mode = null)
    {
        EnsureAuthenticated();
        if (modifyTime.Equals(default(DateTime)))
        {
            modifyTime = DateTime.Now;
        }
        try
        {
            return ScpChannelStream.OpenUpload(this, path, length, modifyTime, mode);
        }
        catch (Exception exception) when (!(exception is RemotePathException))
        {
            throw DiagnoseUploadError(path) ?? new RemoteOperationException(exception.Message + " while trying to upload " + path);
        }
    }

    public ScpDownloadStream OpenDownload(string path)
    {
        EnsureAuthenticated();
        try
        {
            return ScpDownloadStream.Open(this, path);
        }
        catch (Exception exception)
        {
            throw new RemoteOperationException(exception.Message + " while trying to download " + path);
        }
    }

    /// <summary>
    /// Tells whether <paramref name="path"/> exists on the remote side and whether it is a directory.
    /// Returns <see cref="RemotePathKind.Unknown"/> if the check itself could not be run.
    /// </summary>
    public RemotePathKind GetRemotePathKind(string path)
    {
        return GetRemotePathKinds(new[] { path })[0];
    }

    /// <summary>
    /// <see cref="GetRemotePathKind"/> for several paths, with one remote command per batch instead of one per path.
    /// </summary>
    public RemotePathKind[] GetRemotePathKinds(IList<string> paths)
    {
        if (RemoteOs == RemoteOsKind.Windows)
        {
            // test -d / test -e do not exist there: ask SFTP.
            try
            {
                using SftpChannel sftp = SftpChannel.Open(this);
                return SftpFileSystem.PathKinds(sftp, paths);
            }
            catch (RemoteOperationException)
            {
                return paths.Select(path => RemotePathKind.Unknown).ToArray();
            }
        }
        const int BatchSize = 32;
        RemotePathKind[] kinds = new RemotePathKind[paths.Count];
        for (int start = 0; start < paths.Count; start += BatchSize)
        {
            int count = Math.Min(BatchSize, paths.Count - start);
            // "test" and ";" also work in fish and csh login shells, unlike "if ...; then ...; fi" or a loop.
            StringBuilder command = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                string quoted = RemotePathUtils.ShellQuote(paths[start + i]);
                command.Append("test -d ").Append(quoted).Append(" && echo d").Append(i).Append("; ");
                command.Append("test -e ").Append(quoted).Append(" && echo e").Append(i).Append("; ");
            }
            command.Append("echo .");
            List<string> output = RunCommand(command.ToString(), out _);
            bool ran = output != null && output.Contains(".");
            for (int i = 0; i < count; i++)
            {
                if (!ran)
                {
                    kinds[start + i] = RemotePathKind.Unknown;
                }
                else if (output.Contains("d" + i))
                {
                    kinds[start + i] = RemotePathKind.Directory;
                }
                else
                {
                    kinds[start + i] = output.Contains("e" + i) ? RemotePathKind.File : RemotePathKind.Missing;
                }
            }
        }
        return kinds;
    }

    private Exception DiagnoseUploadError(string path)
    {
        try
        {
            RemoteFileContext context = new RemoteFileContext(path, HostName, UserName);
            if (string.IsNullOrEmpty(path))
            {
                return new RemotePathException(context, RemotePathProblem.EmptyPath);
            }
            if (path.TrimEnd(' ').EndsWith("/"))
            {
                return new RemotePathException(context, RemotePathProblem.EndsWithSlash);
            }
            if (!path.StartsWith("/"))
            {
                return new RemotePathException(context, RemotePathProblem.NotAbsolute);
            }
            // A test that could not be run at all (the connection is gone) tells nothing about the path: no diagnosis,
            // so that the caller reports the original error instead of blaming the directory.
            bool? exists = RunRemoteTest("test -e", path);
            if (exists == null)
            {
                return null;
            }
            if (exists == true)
            {
                bool? writable = RunRemoteTest("test -w", path);
                if (writable == false)
                {
                    return new RemotePathException(context, RemotePathProblem.FileNotWritable);
                }
            }
            else
            {
                bool? directoryWritable = RunRemoteTest("test -w", RemotePathUtils.GetDirectoryName(path));
                if (directoryWritable == false)
                {
                    return new RemotePathException(context, RemotePathProblem.DirectoryNotWritable);
                }
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>
    /// Runs "<paramref name="test"/> path" remotely: true if it succeeded, false if the command ran and failed,
    /// null if it could not be run to the end (no connection, connection lost meanwhile).
    /// </summary>
    private bool? RunRemoteTest(string test, string path)
    {
        List<string> output = RunCommand(test + " " + RemotePathUtils.ShellQuote(path), out int? exitStatus);
        if (output == null || exitStatus == null)
        {
            return null;
        }
        return exitStatus == 0;
    }

    #endregion

    #region Reverse port forwarding

    private readonly object _forwardLock = new object();

    private ForwardedPortRemote _remoteForward;

    /// <summary>
    /// Asks the SSH server to listen on <paramref name="boundPort"/> of its own loopback interface and to tunnel every connection
    /// to <paramref name="host"/>:<paramref name="hostPort"/> as seen from this computer (the equivalent of "ssh -R").
    /// There is one such tunnel per session; starting another replaces it. Throws if the server refuses the request.
    /// </summary>
    public void StartRemoteForward(uint boundPort, string host, uint hostPort)
    {
        EnsureAuthenticated();
        StopRemoteForward();
        ForwardedPortRemote port = new ForwardedPortRemote("127.0.0.1", boundPort, host, hostPort);
        _client.AddForwardedPort(port);
        try
        {
            port.Start();
        }
        catch
        {
            try
            {
                _client.RemoveForwardedPort(port);
            }
            catch
            {
            }
            port.Dispose();
            throw;
        }
        lock (_forwardLock)
        {
            _remoteForward = port;
        }
    }

    public void StopRemoteForward()
    {
        ForwardedPortRemote port;
        lock (_forwardLock)
        {
            port = _remoteForward;
            _remoteForward = null;
        }
        if (port == null)
        {
            return;
        }
        try
        {
            port.Stop();
        }
        catch
        {
        }
        try
        {
            _client.RemoveForwardedPort(port);
        }
        catch
        {
        }
        try
        {
            port.Dispose();
        }
        catch
        {
        }
    }

    #endregion

    #region Port forwarding (Tools > Port forwarding)

    /// <summary>
    /// Listens on <paramref name="bindAddress"/>:<paramref name="bindPort"/> of this computer and carries every connection to
    /// <paramref name="host"/>:<paramref name="port"/> as the SSH server sees them (the equivalent of "ssh -L"). Port 0 lets
    /// the system choose a free port (see <see cref="SshPortForward.BoundPort"/>). Throws if the address cannot be bound.
    /// </summary>
    public SshPortForward OpenLocalForward(string bindAddress, uint bindPort, string host, uint port)
    {
        EnsureAuthenticated();
        return SshPortForward.Start(_client, new ForwardedPortLocal(bindAddress, bindPort, host, port));
    }

    /// <summary>
    /// Asks the SSH server to listen on <paramref name="bindAddress"/>:<paramref name="bindPort"/> and carries every connection
    /// to <paramref name="host"/>:<paramref name="port"/> as this computer sees them (the equivalent of "ssh -R"). An address
    /// other than the loopback one needs GatewayPorts on the server. Throws if the server refuses the request.
    /// </summary>
    public SshPortForward OpenRemoteForward(string bindAddress, uint bindPort, string host, uint port)
    {
        EnsureAuthenticated();
        return SshPortForward.Start(_client, new ForwardedPortRemote(bindAddress, bindPort, host, port));
    }

    #endregion

    #region Dynamic port forwarding (SOCKS)

    private ForwardedPortDynamic _dynamicForward;

    /// <summary>True while the SOCKS listener started by <see cref="StartDynamicForward"/> is listening on this connection.</summary>
    public bool IsDynamicForwardActive
    {
        get
        {
            lock (_forwardLock)
            {
                return _dynamicForward != null && _dynamicForward.IsStarted;
            }
        }
    }

    /// <summary>Raised from any thread with the message of a failure of one proxied connection (the target refused it, the server denied the channel ...).</summary>
    public event Action<string> DynamicForwardError;

    /// <summary>
    /// Listens on <paramref name="bindAddress"/>:<paramref name="port"/> of this computer as a SOCKS4/4a/5 proxy and carries every
    /// connection through the SSH server (the equivalent of "ssh -D"). There is one such proxy per session; starting another
    /// replaces it. Throws if the address cannot be bound or the connection is closed.
    /// </summary>
    public void StartDynamicForward(string bindAddress, uint port)
    {
        EnsureAuthenticated();
        StopDynamicForward();
        ForwardedPortDynamic forward = new ForwardedPortDynamic(bindAddress, port);
        forward.Exception += (object sender, ExceptionEventArgs e) => DynamicForwardError?.Invoke(e.Exception.Message);
        _client.AddForwardedPort(forward);
        try
        {
            forward.Start();
        }
        catch
        {
            try
            {
                _client.RemoveForwardedPort(forward);
            }
            catch
            {
            }
            forward.Dispose();
            throw;
        }
        lock (_forwardLock)
        {
            _dynamicForward = forward;
        }
    }

    public void StopDynamicForward()
    {
        ForwardedPortDynamic forward;
        lock (_forwardLock)
        {
            forward = _dynamicForward;
            _dynamicForward = null;
        }
        if (forward == null)
        {
            return;
        }
        try
        {
            forward.Stop();
        }
        catch
        {
        }
        try
        {
            _client.RemoveForwardedPort(forward);
        }
        catch
        {
        }
        try
        {
            forward.Dispose();
        }
        catch
        {
        }
    }

    #endregion

    public void Disconnect()
    {
        _client.Disconnect();
    }

    public virtual void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _authGate.Stop();
        X11?.Dispose();
        try
        {
            _client.Dispose();
        }
        catch
        {
        }
        _proxyRelay?.Dispose();
        CloseAllChannels();
        _events.CompleteAdding();
        if (Thread.CurrentThread != _eventThread)
        {
            _eventThread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
