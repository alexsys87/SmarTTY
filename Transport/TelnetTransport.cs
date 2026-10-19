using System;
using System.IO;
using System.Net.Sockets;
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

    public string ConnectingMessage => "Connecting to " + _telnet.Host + ":" + _telnet.Port + " (telnet)...";

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
            stream = new TelnetStream(client.GetStream(), new TelnetOptions
            {
                TerminalType = _telnet.TerminalType,
                Columns = columns,
                Rows = rows,
                ActiveNegotiation = _telnet.ActiveNegotiation,
                AllowLineMode = _telnet.LineMode,
                AutoLogin = CreateAutoLogin()
            });
            stream.RemoteEchoChanged += OnStreamRemoteEchoChanged;
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
        string name = _telnet.Host + ":" + _telnet.Port;
        return includeParameters ? name + " (telnet)" : name;
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
