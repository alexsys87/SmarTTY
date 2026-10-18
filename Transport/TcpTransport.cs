using System;
using System.IO;
using System.Net.Sockets;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Transport;

internal class TcpTransport : IDeviceTransport, IDisposable
{
    private DeviceTerminalSettings.ConnectionSettings.Tcp _tcp;

    private TcpClient _tcpClient = new TcpClient();

    public string ConnectingMessage => "Connecting to " + _tcp.Host + ":" + _tcp.Port + "...";

    public TcpTransport(DeviceTerminalSettings.ConnectionSettings.Tcp tcp)
    {
        _tcp = tcp;
    }

    public void Open(IHostNameResolver hostNameResolver)
    {
        this._tcpClient.Connect(hostNameResolver?.ResolveHostName(_tcp.Host) ?? _tcp.Host, _tcp.Port);
        // When keep-alive is wanted, ask the operating system to detect half-open TCP connections by probing an idle
        // connection (the same socket option the telnet transport uses). No bytes are written to the wire -- that
        // would corrupt the device protocol -- so an idle raw-TCP session is not disturbed.
        if (_tcp.KeepAliveSeconds > 0)
        {
            _tcpClient.NoDelay = true;
            _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
    }

    public void Close()
    {
        _tcpClient.Close();
    }

    public Stream GetStream()
    {
        return _tcpClient.GetStream();
    }

    public string GetDisplayName(bool includeParameters)
    {
        return _tcp.Host + ":" + _tcp.Port;
    }

    public void Dispose()
    {
        _tcpClient.Close();
    }
}
