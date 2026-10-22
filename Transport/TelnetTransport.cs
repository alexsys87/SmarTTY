using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using VisualTTY.DeviceTerminal;

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

    private TcpClient _tcpClient;

    private TelnetStream _stream;

    private Timer _keepAliveTimer;

    private int _columns = TelnetStream.DefaultColumns;

    private int _rows = TelnetStream.DefaultRows;

    public string ConnectingMessage => "Connecting to " + _telnet.Host + ":" + _telnet.Port + " (" + DescribeKind() + ")...";

    /// <summary>
    /// The character set of the terminal ("utf-8", "koi8-r", ...), accepted when the server proposes it (option
    /// CHARSET). Set before the connection is opened; empty: the option is refused.
    /// </summary>
    public string Charset { get; set; }

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

    public TelnetTransport(DeviceTerminalSettings.ConnectionSettings.Telnet telnet)
    {
        _telnet = telnet;
    }

    public void Open(IHostNameResolver hostNameResolver)
    {
        // A fresh client for every attempt: the caller retries Open() after a failure.
        TcpClient client = new TcpClient();
        TelnetStream stream;
        try
        {
            client.NoDelay = true;
            client.Connect(hostNameResolver?.ResolveHostName(_telnet.Host) ?? _telnet.Host, _telnet.Port);
            if (_telnet.KeepAliveSeconds > 0)
            {
                // The operating system's own keep-alive would help with a half-dead connection, too.
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            int columns;
            int rows;
            lock (_sizeLock)
            {
                columns = _columns;
                rows = _rows;
            }
            // Loaded for every attempt, before anything is sent: a missing certificate is reported as the reason the
            // connection cannot be made.
            X509Certificate2 clientCertificate = _telnet.UsesTls ? TelnetTls.LoadClientCertificate(_telnet.TlsClientCertificate) : null;
            Stream wire = client.GetStream();
            if (_telnet.Tls)
            {
                // The certificate is checked against the name the user typed, not against the resolved address.
                wire = TelnetTls.Authenticate(wire, _telnet.Host, _telnet.TlsAllowInvalidCertificate, clientCertificate: clientCertificate);
            }
            stream = new TelnetStream(wire, new TelnetOptions
            {
                TerminalType = _telnet.TerminalType,
                Columns = columns,
                Rows = rows,
                ActiveNegotiation = _telnet.ActiveNegotiation,
                AllowLineMode = _telnet.LineMode,
                AutoLogin = CreateAutoLogin(),
                TerminalSpeed = _telnet.EffectiveTerminalSpeed(),
                XDisplayLocation = _telnet.EffectiveXDisplayLocation(System.Environment.GetEnvironmentVariable("DISPLAY")),
                Environment = TelnetEnvironment.Parse(_telnet.EffectiveEnvironmentVariables()),
                AcceptEnvironmentRequests = _telnet.SendAutomaticValues,
                Charset = Charset,
                // telnets is TLS already: the in-band option is not needed (and refused).
                StartTls = _telnet.Tls ? TelnetStartTlsMode.Off : ToStreamMode(_telnet.StartTls),
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
        lock (_sizeLock)
        {
            StopKeepAlive();
            _tcpClient?.Close();
            _tcpClient = client;
            _stream = stream;
            StartKeepAlive(stream);
        }
    }

    public void Close()
    {
        lock (_sizeLock)
        {
            StopKeepAlive();
            _tcpClient?.Close();
        }
    }

    public Stream GetStream()
    {
        return _stream;
    }

    public string GetDisplayName(bool includeParameters)
    {
        // An IPv6 address is put in brackets, so the port can be told from it.
        string name = (_telnet.Host != null && _telnet.Host.Contains(":") ? "[" + _telnet.Host + "]" : _telnet.Host) + ":" + _telnet.Port;
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
        string password = null;
        if (_telnet.EncryptedSavedPassword != null)
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
        return new TelnetAutoLogin(_telnet.AutoLoginUserName, password, _telnet.LoginPrompts, _telnet.PasswordPrompts);
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
                // A NOP is ignored by every telnet server but keeps NAT tables and idle timers alive.
                stream.SendCommand(TelnetStream.NOP);
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
