using System;

namespace VisualTTY.DeviceTerminal;

/// <summary>
/// Identity of a device connection for deciding whether two sessions would fight over the same physical resource.
/// A COM port has one owner, so "COM3", "com3" and "\\.\COM3" are the same port; a TCP endpoint is not exclusive,
/// a local UDP port (netconsole) is.
/// </summary>
public static class DeviceSessionKey
{
    private const string DevicePathPrefix = @"\\.\";

    /// <summary>Returns the key of a serial port, or null when the name is empty and cannot be compared.</summary>
    public static string ForSerialPort(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return null;
        }
        string name = portName.Trim();
        if (name.StartsWith(DevicePathPrefix, StringComparison.Ordinal))
        {
            name = name.Substring(DevicePathPrefix.Length);
        }
        return "serial:" + name.ToUpperInvariant();
    }

    /// <summary>
    /// Returns the key of a local UDP port a netconsole receiver listens on: only one session can have it open, whatever
    /// address it listens on.
    /// </summary>
    public static string ForUdpPort(int port)
    {
        return port > 0 && port <= 65535 ? "udp:" + port : null;
    }
}
