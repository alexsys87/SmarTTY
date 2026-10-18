using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Compression;
using SmarTTY.IO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private readonly AuthenticationGate _authGate = new AuthenticationGate();

    private readonly ManualResetEvent _connectDone = new ManualResetEvent(initialState: false);

    private Exception _connectError;

    private HostKeyInfo _hostKey;

    private long _bytesReceived;

    private long _bytesSent;

    private readonly BlockingCollection<Action> _events = new BlockingCollection<Action>();

    private readonly HashSet<SshShellChannel> _channels = new HashSet<SshShellChannel>();

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

    public SessionStats Statistics => new SessionStats
    {
        BytesReceived = Interlocked.Read(ref _bytesReceived),
        BytesSent = Interlocked.Read(ref _bytesSent)
    };

    public static string UserSshDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    public event EventHandler ConnectionLost;

    /// <summary>
    /// Connects to the server and completes the key exchange. Returns once the server waits for
    /// authentication (or has accepted the connection without any). Setting <paramref name="cancelEvent"/>
    /// aborts the attempt with <see cref="OperationCanceledException"/>.
    /// </summary>
    public SshSession(SshConnectionParameters parameters, WaitHandle cancelEvent = null)
    {
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

    private ConnectionInfo CreateConnectionInfo(SshConnectionParameters parameters)
    {
        AuthenticationMethod[] methods = _authGate.CreateMethods(parameters.UserName);
        ConnectionInfo connectionInfo = string.IsNullOrEmpty(parameters.ProxyHost)
            ? new ConnectionInfo(parameters.Host, parameters.Port, parameters.UserName, methods)
            : new ConnectionInfo(parameters.Host, parameters.Port, parameters.UserName, ProxyTypes.Http, parameters.ProxyHost, parameters.ProxyPort, parameters.ProxyUserName, parameters.ProxyPassword, methods);
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
    }

    private void RaiseConnectionLost()
    {
        CloseAllChannels();
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _connectionLostRaised, 1) != 0)
        {
            return;
        }
        ThreadPool.QueueUserWorkItem(delegate
        {
            ConnectionLost?.Invoke(this, EventArgs.Empty);
        });
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
    /// Enables periodic keepalive messages (sent by SSH.NET in the background); 0 disables them.
    /// </summary>
    public void ConfigureKeepAlive(int intervalSeconds)
    {
        _client.KeepAliveInterval = (intervalSeconds > 0) ? TimeSpan.FromSeconds(intervalSeconds) : Timeout.InfiniteTimeSpan;
    }

    #region Authentication

    private bool RunAuthentication(string userName, Func<Session, AuthenticationResult> attempt)
    {
        if (IsAuthenticated)
        {
            return true;
        }
        if (_authGate.Run(attempt) != AuthenticationResult.Success)
        {
            return false;
        }
        // SshClient.Connect() completes once the successful attempt returns.
        _connectDone.WaitOne();
        if (_connectError != null)
        {
            ExceptionDispatchInfo.Capture(_connectError).Throw();
        }
        UserName = userName;
        return true;
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
    public bool Authenticate(string userName, string password)
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
        if ((authMethods & AuthMethods.Password) != 0)
        {
            return AuthenticateWithPassword(userName, password);
        }
        if ((authMethods & AuthMethods.KeyboardInteractive) != 0)
        {
            return AuthenticateKeyboardInteractiveWithPassword(userName, password);
        }
        if (!AuthenticateWithPassword(userName, password))
        {
            return AuthenticateKeyboardInteractiveWithPassword(userName, password);
        }
        return true;
    }

    public bool AuthenticateWithPassword(string userName, string password)
    {
        return RunAuthentication(userName, (Session session) =>
        {
            using PasswordAuthenticationMethod method = new PasswordAuthenticationMethod(userName, password);
            return method.Authenticate(session);
        });
    }

    public bool AuthenticateKeyboardInteractiveWithPassword(string userName, string password)
    {
        return AuthenticateKeyboardInteractive(userName, (string name, string instruction, KeyboardInteractivePrompt[] prompts, string[] responses) =>
        {
            if (responses.Length > 0)
            {
                responses[0] = password;
            }
        });
    }

    public bool AuthenticateKeyboardInteractive(string userName, KeyboardInteractiveHandler callback)
    {
        return RunAuthentication(userName, (Session session) =>
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
            return method.Authenticate(session);
        });
    }

    public bool AuthenticateWithPublicKey(string userName, ISshKeyPair keyPair)
    {
        return RunAuthentication(userName, (Session session) =>
        {
            IPrivateKeySource key = keyPair.CreatePrivateKeySource();
            try
            {
                using PrivateKeyAuthenticationMethod method = new PrivateKeyAuthenticationMethod(userName, key);
                return method.Authenticate(session);
            }
            finally
            {
                (key as IDisposable)?.Dispose();
            }
        });
    }

    /// <summary>
    /// Authenticates with an OpenSSH private key file (RSA, ECDSA or Ed25519).
    /// If no file is specified, the first of id_ed25519, id_ecdsa and id_rsa found in <see cref="UserSshDirectory"/> is used.
    /// <paramref name="passphraseRequired"/> is set if the key could not be loaded with the given passphrase.
    /// </summary>
    public bool AuthenticateWithKeyFile(string userName, out bool passphraseRequired, string keyFile = null, string passphrase = null)
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
        catch (Exception)
        {
            // Encrypted key without a passphrase, a wrong passphrase or an unreadable key.
            passphraseRequired = true;
            return false;
        }
        using (key)
        {
            return RunAuthentication(userName, (Session session) =>
            {
                using PrivateKeyAuthenticationMethod method = new PrivateKeyAuthenticationMethod(userName, key);
                return method.Authenticate(session);
            });
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
    }

    public SshShellChannel OpenShellChannel(string command = null, string terminalType = null, ExtendedDataMode extendedDataMode = ExtendedDataMode.Normal)
    {
        EnsureAuthenticated();
        return new SshShellChannel(this, SshNetChannel.Create(_client), command, terminalType, extendedDataMode);
    }

    /// <summary>
    /// Runs a command and returns its output (stdout and stderr merged), or null if it could not be run.
    /// </summary>
    public List<string> RunCommand(string command, out int? exitStatus, string terminalType = null)
    {
        try
        {
            using SshShellChannel channel = OpenShellChannel(command, terminalType, ExtendedDataMode.Merge);
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
            exitStatus = channel.ExitStatus;
            return lines;
        }
        catch
        {
            exitStatus = null;
            return null;
        }
    }

    /// <summary>
    /// Runs a command and returns its output; if <paramref name="requireSuccess"/> is set, returns null when the command fails.
    /// </summary>
    public List<string> RunCommandChecked(string command, bool requireSuccess, string terminalType = null)
    {
        List<string> output = RunCommand(command, out int? exitStatus, terminalType);
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
            return ScpChannelStream.OpenUpload(this, path, length, modifyTime, mode ?? Convert.ToInt32("644", 8));
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
            if (RunCommandChecked("test -e \"" + path + "\"", requireSuccess: true) != null)
            {
                if (RunCommandChecked("test -w \"" + path + "\"", requireSuccess: true) == null)
                {
                    return new RemotePathException(context, RemotePathProblem.FileNotWritable);
                }
            }
            else if (RunCommandChecked("test -w \"" + RemotePathUtils.GetDirectoryName(path) + "\"", requireSuccess: true) == null)
            {
                return new RemotePathException(context, RemotePathProblem.DirectoryNotWritable);
            }
        }
        catch
        {
        }
        return null;
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
        try
        {
            _client.Dispose();
        }
        catch
        {
        }
        CloseAllChannels();
        _events.CompleteAdding();
        if (Thread.CurrentThread != _eventThread)
        {
            _eventThread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
