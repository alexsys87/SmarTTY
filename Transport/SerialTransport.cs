using SmarTTY.Native;
using System;
using System.IO;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Transport;

internal class SerialTransport : IDeviceTransport, IDisposable
{
    private readonly DeviceTerminalSettings.ConnectionSettings.Serial _serial;

    public SerialPortStream SerialStream { get; private set; }

    public string PortName => _serial.ComPortName;

    public string ConnectingMessage => "Connecting to " + _serial.ComPortName + "...";

    public SerialTransport(DeviceTerminalSettings.ConnectionSettings.Serial serial)
    {
        this._serial = serial;
    }

    public void Open(IHostNameResolver hostNameResolver)
    {
        if (SerialStream != null)
        {
            SerialStream.Dispose();
        }
        SerialStream = new SerialPortStream(hostNameResolver?.ResolveHostName(this._serial.ComPortName) ?? this._serial.ComPortName, this._serial.AdvancedSettings);
    }

    public void Close()
    {
        if (SerialStream != null)
        {
            SerialStream.Dispose();
            SerialStream = null;
        }
    }

    public Stream GetStream()
    {
        return SerialStream;
    }

    public void Dispose()
    {
        Close();
    }

    public string GetDisplayName(bool includeParameters)
    {
        if ((this._serial.AdvancedSettings != null) & includeParameters)
        {
            return $"{this._serial.ComPortName} ({this._serial.AdvancedSettings.BaudRate} bps)";
        }
        return this._serial.ComPortName;
    }
}
