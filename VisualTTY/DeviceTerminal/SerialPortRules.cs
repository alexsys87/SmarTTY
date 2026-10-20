using System;
using System.Globalization;

namespace VisualTTY.DeviceTerminal;

/// <summary>How many stop bits a serial frame has.</summary>
public enum SerialStopBitsKind
{
    One,
    OnePointFive,
    Two
}

/// <summary>
/// The arithmetic and validation rules of serial port settings, kept free of Windows and Windows Forms so they can be
/// tested. The framework enums are passed as numbers: Parity (0 none, 1 odd, 2 even, 3 mark, 4 space) and StopBits
/// (0 none, 1 one, 2 two, 3 one and a half).
/// </summary>
public static class SerialPortRules
{
    public const int MinBaudRate = 50;

    public const int MaxBaudRate = 16000000;

    public const int MinDataBits = 5;

    public const int MaxDataBits = 8;

    /// <summary>The most bytes handed to the driver in one write; a stalled port is detected per chunk.</summary>
    public const int WriteChunkBytes = 512;

    /// <summary>How long a port may refuse data (flow control stop, dead device) before a write fails.</summary>
    public const int WriteStallTimeoutMs = 5000;

    /// <summary>Reads a baud rate typed by the user. The error text is meant to be shown as it is.</summary>
    public static bool TryParseBaudRate(string text, out int baudRate, out string error)
    {
        baudRate = 0;
        string trimmed = text == null ? string.Empty : text.Trim();
        if (trimmed.Length == 0)
        {
            error = "Enter the baud rate.";
            return false;
        }
        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            error = "The baud rate must be a whole number, for example 115200.";
            return false;
        }
        if (value < MinBaudRate || value > MaxBaudRate)
        {
            error = "The baud rate must be between " + MinBaudRate + " and " + MaxBaudRate + ".";
            return false;
        }
        baudRate = value;
        error = null;
        return true;
    }

    /// <summary>Maps the framework's StopBits number to what a UART can do; "none" is not a real setting and means one.</summary>
    public static SerialStopBitsKind StopBitsKindFrom(int frameworkStopBits)
    {
        switch (frameworkStopBits)
        {
            case 2:
                return SerialStopBitsKind.Two;
            case 3:
                return SerialStopBitsKind.OnePointFive;
            default:
                return SerialStopBitsKind.One;
        }
    }

    /// <summary>The framework's StopBits number for a kind.</summary>
    public static int FrameworkStopBitsFrom(SerialStopBitsKind kind)
    {
        switch (kind)
        {
            case SerialStopBitsKind.Two:
                return 2;
            case SerialStopBitsKind.OnePointFive:
                return 3;
            default:
                return 1;
        }
    }

    /// <summary>Null when the frame is allowed, otherwise the reason it is not.</summary>
    public static string ValidateFrame(int dataBits, SerialStopBitsKind stopBits)
    {
        if (dataBits < MinDataBits || dataBits > MaxDataBits)
        {
            return "The number of data bits must be from " + MinDataBits + " to " + MaxDataBits + ".";
        }
        if (dataBits == 5 && stopBits == SerialStopBitsKind.Two)
        {
            return "5 data bits cannot be combined with 2 stop bits. Use 1 or 1.5 stop bits, or more data bits.";
        }
        if (dataBits > 5 && stopBits == SerialStopBitsKind.OnePointFive)
        {
            return "1.5 stop bits can only be used with 5 data bits. Use 1 or 2 stop bits.";
        }
        return null;
    }

    /// <summary>Bits on the wire for one character: start bit, data, optional parity, stop bits.</summary>
    public static double BitsPerFrame(int dataBits, bool hasParity, SerialStopBitsKind stopBits)
    {
        double stop = stopBits == SerialStopBitsKind.Two ? 2.0 : stopBits == SerialStopBitsKind.OnePointFive ? 1.5 : 1.0;
        return 1.0 + dataBits + (hasParity ? 1.0 : 0.0) + stop;
    }

    /// <summary>The fastest the port can carry characters at the given settings; 0 for a nonsensical baud rate.</summary>
    public static double BytesPerSecond(int baudRate, int dataBits, bool hasParity, SerialStopBitsKind stopBits)
    {
        if (baudRate <= 0)
        {
            return 0;
        }
        return baudRate / BitsPerFrame(dataBits, hasParity, stopBits);
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond > 1048576.0)
        {
            return OneDecimal(bytesPerSecond / 1048576.0) + " MB/s";
        }
        if (bytesPerSecond > 1024.0)
        {
            return OneDecimal(bytesPerSecond / 1024.0) + " KB/s";
        }
        return OneDecimal(bytesPerSecond) + " bytes/s";
    }

    // Rounded explicitly: .NET Framework and .NET Core differ on exact halves (11.25 is "11.3" on one and "11.2" on the other).
    private static string OneDecimal(double value)
    {
        return Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("f1", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// How long one write call may take: the time the bytes need on the wire plus a stall allowance, so a slow port
    /// (300 baud) is not mistaken for a stalled one and a stalled one is noticed in seconds instead of never.
    /// </summary>
    public static int WriteTimeoutMs(int baudRate, int dataBits, bool hasParity, SerialStopBitsKind stopBits, int chunkBytes, int stallMs)
    {
        if (baudRate <= 0)
        {
            baudRate = 9600;
        }
        double wireMs = chunkBytes * BitsPerFrame(dataBits, hasParity, stopBits) * 1000.0 / baudRate;
        double total = Math.Ceiling(wireMs) + stallMs;
        return total > int.MaxValue / 2 ? int.MaxValue / 2 : (int)total;
    }

    /// <summary>A short name of the frame format such as 8N1 or 7E2, for messages.</summary>
    public static string FrameName(int dataBits, int parity, SerialStopBitsKind stopBits)
    {
        string p = parity == 1 ? "O" : parity == 2 ? "E" : parity == 3 ? "M" : parity == 4 ? "S" : "N";
        string s = stopBits == SerialStopBitsKind.Two ? "2" : stopBits == SerialStopBitsKind.OnePointFive ? "1.5" : "1";
        return dataBits.ToString(CultureInfo.InvariantCulture) + p + s;
    }
}
