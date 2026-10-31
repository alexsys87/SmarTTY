using System;
using System.IO;
using System.Net.Sockets;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Transport;

/// <summary>
/// A raw TCP connection: every byte is passed on as it is, in both directions. <see cref="Open"/> may be called again
/// after it failed (the caller retries while a device is still starting); each attempt uses a socket of its own.
/// </summary>
internal class TcpTransport : IDeviceTransport, IDisposable
{
    private readonly DeviceTerminalSettings.ConnectionSettings.Tcp _tcp;

    private readonly object _lock = new object();

    private Socket _socket;

    private NetworkStream _stream;

    // The connect in progress, so Close() from the UI thread can abort it.
    private TcpConnector _connector;

    private bool _closed;

    public string ConnectingMessage => "Connecting to " + _tcp + "...";

    /// <summary>How long one <see cref="Open"/> may take, in milliseconds; 0 leaves it to the operating system.</summary>
    public int ConnectTimeoutMs { get; set; }

    public TcpTransport(DeviceTerminalSettings.ConnectionSettings.Tcp tcp)
    {
        _tcp = tcp ?? throw new ArgumentNullException(nameof(tcp));
        ConnectTimeoutMs = _tcp.ConnectTimeoutMs;
    }

    public void Open(IHostNameResolver hostNameResolver)
    {
        TcpConnector connector = new TcpConnector();
        lock (_lock)
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(TcpTransport), "The connection is closed.");
            }
            _connector = connector;
        }
        Socket socket;
        try
        {
            socket = connector.Connect(hostNameResolver?.ResolveHostName(_tcp.Host) ?? _tcp.Host, _tcp.Port, ConnectTimeoutMs);
        }
        finally
        {
            lock (_lock)
            {
                if (_connector == connector)
                {
                    _connector = null;
                }
            }
        }
        try
        {
            try
            {
                // Only the operating system probes an idle connection: nothing is written into the data stream, which
                // would corrupt the device protocol, so an idle raw TCP session is not disturbed.
                TcpConnector.EnableKeepAlive(socket, _tcp.KeepAliveSeconds);
            }
            catch (SocketException)
            {
                // A connection without keep-alive is still usable.
            }
            NetworkStream stream = new NetworkStream(socket, ownsSocket: true);
            lock (_lock)
            {
                if (_closed)
                {
                    stream.Dispose();
                    throw new ObjectDisposedException(nameof(TcpTransport), "The connection was closed while it was being made.");
                }
                _stream?.Dispose();
                _socket = socket;
                _stream = stream;
            }
        }
        catch
        {
            // Whatever went wrong after the connect, nobody else holds this socket.
            socket.Close();
            throw;
        }
    }

    public void Close()
    {
        TcpConnector connector;
        NetworkStream stream;
        Socket socket;
        lock (_lock)
        {
            _closed = true;
            connector = _connector;
            stream = _stream;
            socket = _socket;
            _connector = null;
            // A writer that asks later is told that the connection is closed instead of getting a disposed stream.
            _stream = null;
            _socket = null;
        }
        connector?.Abort();
        try
        {
            // Shutting the socket down first wakes up a read that is waiting on another thread at once.
            socket?.Shutdown(SocketShutdown.Both);
        }
        catch
        {
        }
        stream?.Dispose();
    }

    public Stream GetStream()
    {
        lock (_lock)
        {
            return _stream;
        }
    }

    public string GetDisplayName(bool includeParameters)
    {
        return _tcp.ToString();
    }

    public void Dispose()
    {
        Close();
    }
}
