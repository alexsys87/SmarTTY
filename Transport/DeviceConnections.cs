using System;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Transport;

/// <summary>
/// The connections of a device tab (COM, TCP, telnet, netconsole, a local console) as the tab and the settings window use them: the name of a connection for a
/// tab header, the transport it opens, and a test that opens it once. They were static members of the WinForms device panel.
/// </summary>
public static class DeviceConnections
{
    // A test of a network connection waits as long as the connection itself would; when that is left to the operating
    // system (a timeout of 0), it gives up after this long instead.
    private const int TestConnectTimeoutMs = 10000;

    /// <summary>Short name of a connection for a tab header; usable before the control has been given its settings.</summary>
    public static string GetConnectionTitle(DeviceTerminalSettings.ConnectionSettings connection)
    {
        return connection switch
        {
            DeviceTerminalSettings.ConnectionSettings.Serial serial => serial.ComPortName,
            DeviceTerminalSettings.ConnectionSettings.Tcp tcp => tcp.ToString(),
            DeviceTerminalSettings.ConnectionSettings.Netconsole netconsole => netconsole.ToString(),
            DeviceTerminalSettings.ConnectionSettings.LocalConsole console => console.DisplayName,
            _ => "Raw Terminal",
        };
    }

    /// <summary>The function that opens the transport of a connection (null for a connection of no known kind); telnet logs in with <paramref name="enterSequence"/>.</summary>
    internal static CallbackTransportFactory.TransportCallback CreateTransportCallback(DeviceTerminalSettings.ConnectionSettings connectionSettings, string enterSequence = null)
    {
        if (connectionSettings is DeviceTerminalSettings.ConnectionSettings.Serial)
        {
            return () => new SerialTransport(connectionSettings as DeviceTerminalSettings.ConnectionSettings.Serial);
        }
        // Telnet derives from Tcp, so it has to be recognized first.
        if (connectionSettings is DeviceTerminalSettings.ConnectionSettings.Telnet telnet)
        {
            return () => new TelnetTransport(telnet, enterSequence);
        }
        if (connectionSettings is DeviceTerminalSettings.ConnectionSettings.Tcp)
        {
            return () => new TcpTransport(connectionSettings as DeviceTerminalSettings.ConnectionSettings.Tcp);
        }
        if (connectionSettings is DeviceTerminalSettings.ConnectionSettings.Netconsole netconsole)
        {
            return () => new NetconsoleTransport(netconsole);
        }
        if (connectionSettings is DeviceTerminalSettings.ConnectionSettings.LocalConsole console)
        {
            return () => new ConPtyTransport(console);
        }
        return null;
    }

    /// <summary>
    /// Opens the connection once and closes it again; throws with the reason when it cannot be made. It blocks, so the
    /// caller runs it on a worker thread.
    /// </summary>
    public static string TestConnection(DeviceTerminalSettings.ConnectionSettings connectionSettings)
    {
        using IDeviceTransport deviceTransport = new CallbackTransportFactory(CreateTransportCallback(connectionSettings) ?? throw new Exception("Connection type not specified")).CreateTransport();
        if (deviceTransport == null)
        {
            throw new Exception("Unspecified error");
        }
        switch (deviceTransport)
        {
            case TcpTransport tcpTransport:
                if (tcpTransport.ConnectTimeoutMs <= 0)
                {
                    tcpTransport.ConnectTimeoutMs = TestConnectTimeoutMs;
                }
                break;
            case TelnetTransport telnetTransport:
                if (telnetTransport.ConnectTimeoutMs <= 0)
                {
                    telnetTransport.ConnectTimeoutMs = TestConnectTimeoutMs;
                }
                break;
        }
        deviceTransport.Open(null);
        return deviceTransport.GetDisplayName(includeParameters: true);
    }
}
