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

    private int _columns = TelnetStream.DefaultColumns;

    private int _rows = TelnetStream.DefaultRows;

    public string ConnectingMessage => "Connecting to " + _telnet.Host + ":" + _telnet.Port + " (telnet)...";

    public TelnetTransport(DeviceTerminalSettings.ConnectionSettings.Telnet telnet)
    {
        _telnet = telnet;
    }

    public void Open(IHostNameResolver hostNameResolver)
    {
        // A fresh client for every attempt: the caller retries Open() after a failure.
        TcpClient client = new TcpClient();
        try
        {
            client.NoDelay = true;
            client.Connect(hostNameResolver?.ResolveHostName(_telnet.Host) ?? _telnet.Host, _telnet.Port);
        }
        catch
        {
            client.Close();
            throw;
        }
        lock (_sizeLock)
        {
            _tcpClient?.Close();
            _tcpClient = client;
            _stream = new TelnetStream(client.GetStream(), TelnetStream.DefaultTerminalType, _columns, _rows);
        }
    }

    public void Close()
    {
        lock (_sizeLock)
        {
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
}
