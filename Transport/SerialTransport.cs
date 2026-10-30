using SmarTTY.Native;
using System;
using System.IO;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Transport;

internal class SerialTransport : IDeviceTransport, IDisposable
{
    private readonly DeviceTerminalSettings.ConnectionSettings.Serial _serial;

    // Close() runs from the UI thread and from the reader thread at the same time. _stateLock guards the field; the
    // closing itself is done under _closeLock, so a second caller returns only when the port really is released.
    private readonly object _stateLock = new object();

    private readonly object _closeLock = new object();

    private SerialPortStream _serialStream;

    public SerialPortStream SerialStream
    {
        get
        {
            lock (_stateLock)
            {
                return _serialStream;
            }
        }
    }

    public string PortName => _serial.ComPortName;

    public string ConnectingMessage => "Connecting to " + _serial.ComPortName + "...";

    public SerialTransport(DeviceTerminalSettings.ConnectionSettings.Serial serial)
    {
        this._serial = serial;
    }

    public void Open(IHostNameResolver hostNameResolver)
    {
        Close();
        SerialPortStream stream = new SerialPortStream(hostNameResolver?.ResolveHostName(this._serial.ComPortName) ?? this._serial.ComPortName, this._serial.AdvancedSettings);
        lock (_stateLock)
        {
            _serialStream = stream;
        }
    }

    public void Close()
    {
        lock (_closeLock)
        {
            SerialPortStream stream;
            lock (_stateLock)
            {
                stream = _serialStream;
                _serialStream = null;
            }
            stream?.Dispose();
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
