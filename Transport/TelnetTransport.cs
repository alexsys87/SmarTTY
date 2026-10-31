using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using VisualTTY.DeviceTerminal;
using VisualTTY;

namespace SmarTTY.Transport;

/// <summary>
/// A telnet connection: a TCP connection whose stream goes through <see cref="TelnetStream"/>, so the terminal gets
/// clean text and the server's option negotiation is answered. Everything else (reconnecting, the writer queue,
/// the display name) works as for a raw TCP connection.
/// </summary>
internal class TelnetTransport : IDeviceTransport, ITerminalSizeAwareTransport, IDisposable
{
    private readonly DeviceTerminalSettings.ConnectionSettings.Telnet _telnet;

    private readonly object _sizeLock = new object();

    private Socket _socket;

    private TelnetStream _stream;

    // The connect in progress, so Close() from the UI thread can abort it.
    private TcpConnector _connector;

    private bool _closed;

    private Timer _keepAliveTimer;

    private int _columns = TelnetStream.DefaultColumns;

    private int _rows = TelnetStream.DefaultRows;

    public string ConnectingMessage => "Connecting to " + _telnet + " (" + DescribeKind() + ")...";

    /// <summary>How long one <see cref="Open"/> may take to reach the server, in milliseconds; 0 leaves it to the operating system.</summary>
    public int ConnectTimeoutMs { get; set; } = TcpConnector.DefaultConnectTimeoutMs;

    /// <summary>
    /// The character set of the terminal ("utf-8", "koi8-r", ...), accepted when the server proposes it (option
    /// CHARSET). Set before the connection is opened; empty: the option is refused.
    /// </summary>
    public string Charset { get; set; }

    /// <summary>How long the server may take to start STARTTLS before the connection is given up, in milliseconds.</summary>
    public int StartTlsTimeoutMs { get; set; } = 15000;

    /// <summary>The server answered a status request (<see cref="RequestRemoteStatus"/>). Raised on the reading thread.</summary>
    public event EventHandler RemoteStatusReceived;

    /// <summary>What the server reported about its options (option STATUS); null until it answered.</summary>
    public string RemoteStatus => _stream?.RemoteStatus;

    /// <summary>Asks the server to report its options; false when it did not offer the STATUS option.</summary>
    public bool RequestRemoteStatus()
    {
        TelnetStream stream = _stream;
        if (stream == null)
        {
            throw new InvalidOperationException("The connection is not open.");
        }
        return stream.RequestRemoteStatus();
    }

    /// <summary>The encryption and the options in force on both sides, as text; empty when there is no connection.</summary>
    public string DescribeOptions()
    {
        TelnetStream stream = _stream;
        if (stream == null)
        {
            return "";
        }
        string encryption = "none (the session, passwords included, travels in clear text)";
        if (stream.InnerStream is SslStream ssl)
        {
            encryption = (stream.StartTlsActive ? "TLS negotiated in the session (STARTTLS)" : "TLS from the first byte (telnets)") + ", " + ssl.SslProtocol
                + (ssl.IsMutuallyAuthenticated ? ", with a client certificate" : "");
        }
        return "Encryption: " + encryption + "\r\n\r\n" + stream.DescribeOptions();
    }

    /// <summary>The server started or stopped echoing what is typed. Raised on the reading thread.</summary>
    public event EventHandler RemoteEchoChanged;

    /// <summary>True while the server echoes what is typed (it announced the ECHO option).</summary>
    public bool RemoteEcho => _stream?.RemoteEcho ?? false;

    // What ends the lines of the automatic login: the Enter key of the terminal (null: CR LF).
    private readonly string _lineEnding;

    public TelnetTransport(DeviceTerminalSettings.ConnectionSettings.Telnet telnet, string lineEnding = null)
    {
        _telnet = telnet;
        _lineEnding = lineEnding;
    }

    public void Open(IHostNameResolver hostNameResolver)
    {
        // A fresh socket for every attempt: the caller retries Open() after a failure.
        TcpConnector connector = new TcpConnector();
        lock (_sizeLock)
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(TelnetTransport), "The connection is closed.");
            }
            _connector = connector;
        }
        Socket client;
        try
        {
            client = connector.Connect(hostNameResolver?.ResolveHostName(_telnet.Host) ?? _telnet.Host, _telnet.Port, ConnectTimeoutMs);
        }
        finally
        {
            lock (_sizeLock)
            {
                if (_connector == connector)
                {
                    _connector = null;
                }
            }
        }
        lock (_sizeLock)
        {
            if (_closed)
            {
                client.Close();
                throw new ObjectDisposedException(nameof(TelnetTransport), "The connection was closed while it was being made.");
            }
            // Known from now on, so Close() also ends a TLS handshake or a negotiation that hangs.
            _socket = client;
        }
        TelnetStream stream;
        int columns;
        int rows;
        try
        {
            try
            {
                // The operating system's own keep-alive finds a half-dead connection, too.
                TcpConnector.EnableKeepAlive(client, _telnet.KeepAliveSeconds);
            }
            catch (SocketException)
            {
                // The NOP timer below still keeps the connection busy.
            }
            // Loaded for every attempt, before anything is sent: a missing certificate is reported as the reason the
            // connection cannot be made.
            X509Certificate2 clientCertificate = _telnet.UsesTls ? TelnetTls.LoadClientCertificate(_telnet.TlsClientCertificate) : null;
            Stream wire = new NetworkStream(client, ownsSocket: true);
            if (_telnet.Tls)
            {
                // The certificate is checked against the name the user typed, not against the resolved address.
                wire = TelnetTls.Authenticate(wire, _telnet.Host, _telnet.TlsAllowInvalidCertificate, clientCertificate: clientCertificate);
            }
            TelnetAutoLogin autoLogin = CreateAutoLogin();
            // Read only now: the TLS handshake above may take a while, and the window may have been resized meanwhile.
            lock (_sizeLock)
            {
                columns = _columns;
                rows = _rows;
            }
            stream = new TelnetStream(wire, new TelnetOptions
            {
                TerminalType = _telnet.TerminalType,
                Columns = columns,
                Rows = rows,
                ActiveNegotiation = _telnet.ActiveNegotiation,
                AllowLineMode = _telnet.LineMode,
                AutoLogin = autoLogin,
                TerminalSpeed = _telnet.EffectiveTerminalSpeed(),
                XDisplayLocation = _telnet.EffectiveXDisplayLocation(System.Environment.GetEnvironmentVariable("DISPLAY")),
                Environment = TelnetEnvironment.Parse(_telnet.EffectiveEnvironmentVariables()),
                AcceptEnvironmentRequests = _telnet.SendAutomaticValues,
                Charset = Charset,
                // telnets is TLS already: the in-band option is not needed (and refused).
                StartTls = _telnet.Tls ? TelnetStartTlsMode.Off : ToStreamMode(_telnet.StartTls),
                StartTlsTimeoutMs = StartTlsTimeoutMs,
                UpgradeToTls = _telnet.Tls || _telnet.StartTls == DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.Off
                    ? null
                    : (Func<Stream, Stream>)(raw => TelnetTls.Authenticate(raw, _telnet.Host, _telnet.TlsAllowInvalidCertificate, clientCertificate: clientCertificate))
            });
            stream.RemoteEchoChanged += OnStreamRemoteEchoChanged;
            stream.RemoteStatusReceived += OnStreamRemoteStatusReceived;
            stream.Start();
        }
        catch
        {
            client.Close();
            throw;
        }
        int latestColumns;
        int latestRows;
        lock (_sizeLock)
        {
            if (_closed)
            {
                client.Close();
                throw new ObjectDisposedException(nameof(TelnetTransport), "The connection was closed while it was being made.");
            }
            StopKeepAlive();
            _stream = stream;
            StartKeepAlive(stream);
            latestColumns = _columns;
            latestRows = _rows;
        }
        if (latestColumns != columns || latestRows != rows)
        {
            // Resized after the size was read and before the stream was known to SetTerminalSize: the server was told
            // (or will be, by NAWS) the old size, and nothing else would correct it.
            try
            {
                stream.SetWindowSize(latestColumns, latestRows);
            }
            catch
            {
                // The connection is gone; the reading side reports that.
            }
        }
    }

    public void Close()
    {
        TcpConnector connector;
        lock (_sizeLock)
        {
            _closed = true;
            StopKeepAlive();
            connector = _connector;
            _connector = null;
            _socket?.Close();
        }
        connector?.Abort();
    }

    public Stream GetStream()
    {
        return _stream;
    }

    public string GetDisplayName(bool includeParameters)
    {
        // An IPv6 address is put in brackets, so the port can be told from it.
        string name = _telnet.ToString();
        return includeParameters ? name + " (" + DescribeKind() + ")" : name;
    }

    private string DescribeKind()
    {
        if (_telnet.Tls)
        {
            return "telnet over TLS";
        }
        switch (_telnet.StartTls)
        {
            case DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.Required:
                return "telnet, STARTTLS required";
            case DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.WhenOffered:
                return "telnet, STARTTLS when offered";
            default:
                return "telnet";
        }
    }

    private static TelnetStartTlsMode ToStreamMode(DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode mode)
    {
        switch (mode)
        {
            case DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.Required:
                return TelnetStartTlsMode.Require;
            case DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.WhenOffered:
                return TelnetStartTlsMode.Accept;
            default:
                return TelnetStartTlsMode.Off;
        }
    }

    /// <summary>
    /// Sends a Telnet command (see the constants of <see cref="TelnetStream"/>: Break, Interrupt Process,
    /// Are You There, NOP, ...). Throws when there is no connection.
    /// </summary>
    public void SendCommand(byte command)
    {
        TelnetStream stream = _stream;
        if (stream == null)
        {
            throw new InvalidOperationException("The connection is not open.");
        }
        stream.SendCommand(command);
    }

    public void SetTerminalSize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0)
        {
            return;
        }
        TelnetStream stream;
        lock (_sizeLock)
        {
            _columns = columns;
            _rows = rows;
            stream = _stream;
        }
        if (stream == null)
        {
            // Not connected yet: the size is used when the connection is made.
            return;
        }
        // Called on the UI thread; sending must not be able to block it.
        // Every queued job sends the latest known size, so jobs running out of order still end with the right one.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                int latestColumns;
                int latestRows;
                lock (_sizeLock)
                {
                    latestColumns = _columns;
                    latestRows = _rows;
                }
                stream.SetWindowSize(latestColumns, latestRows);
            }
            catch
            {
                // The connection is gone; the reading side reports that.
            }
        });
    }

    public void Dispose()
    {
        Close();
    }

    // The key container is the one of the saved SSH passwords.
    private const string PasswordContainerName = "com.sysprogs.smartty.passwords";

    private TelnetAutoLogin CreateAutoLogin()
    {
        if (!_telnet.HasAutoLogin)
        {
            return null;
        }
        // A password typed in a quick connection is kept in memory only; a saved one is stored encrypted.
        string password = _telnet.AutoLoginPassword;
        if (password == null && _telnet.EncryptedSavedPassword != null)
        {
            try
            {
                password = VisualTTY.PasswordProtector.Decrypt(_telnet.EncryptedSavedPassword, PasswordContainerName);
            }
            catch (Exception)
            {
                // The saved password can not be read by this user on this computer: it has to be typed by hand
                // (the user name, if there is one, is still sent).
            }
        }
        return new TelnetAutoLogin(_telnet.AutoLoginUserName, password, _telnet.LoginPrompts, _telnet.PasswordPrompts, lineEnding: _lineEnding);
    }

    private void OnStreamRemoteEchoChanged(object sender, EventArgs e)
    {
        RemoteEchoChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStreamRemoteStatusReceived(object sender, EventArgs e)
    {
        RemoteStatusReceived?.Invoke(this, EventArgs.Empty);
    }

    // The caller holds _sizeLock.
    private void StartKeepAlive(TelnetStream stream)
    {
        int seconds = Math.Min(_telnet.KeepAliveSeconds, 24 * 60 * 60);
        if (seconds <= 0)
        {
            return;
        }
        int interval = seconds * 1000;
        _keepAliveTimer = new Timer(_ =>
        {
            try
            {
                // A NOP is ignored by every telnet server but keeps NAT tables and idle timers alive. Not before a
                // required STARTTLS is done: nothing about the session is said in clear text until then.
                if (!stream.IsAwaitingRequiredTls)
                {
                    stream.SendCommand(TelnetStream.NOP);
                }
            }
            catch
            {
                // The connection is gone; the reading side reports that.
            }
        }, null, interval, interval);
    }

    // The caller holds _sizeLock.
    private void StopKeepAlive()
    {
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
    }
}
