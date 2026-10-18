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
