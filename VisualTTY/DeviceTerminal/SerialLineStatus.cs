using System;
using System.Collections.Generic;

namespace VisualTTY.DeviceTerminal;

/// <summary>The input modem lines of a serial port as reported by GetCommModemStatus.</summary>
public struct SerialModemLines
{
    private const uint MsCtsOn = 0x10;

    private const uint MsDsrOn = 0x20;

    private const uint MsRingOn = 0x40;

    private const uint MsRlsdOn = 0x80;

    /// <summary>Clear to send: the device is ready to accept data.</summary>
    public bool Cts;

    /// <summary>Data set ready: the device is powered and present.</summary>
    public bool Dsr;

    public bool Ring;

    /// <summary>Data carrier detect (RLSD).</summary>
    public bool Dcd;

    public static SerialModemLines FromFlags(uint modemStatus)
    {
        return new SerialModemLines
        {
            Cts = (modemStatus & MsCtsOn) != 0,
            Dsr = (modemStatus & MsDsrOn) != 0,
            Ring = (modemStatus & MsRingOn) != 0,
            Dcd = (modemStatus & MsRlsdOn) != 0
        };
    }

    /// <summary>For example "CTS● DSR○ DCD○ RI○": a filled dot is an active line.</summary>
    public string ToText()
    {
        return "CTS" + Dot(Cts) + " DSR" + Dot(Dsr) + " DCD" + Dot(Dcd) + " RI" + Dot(Ring);
    }

    private static string Dot(bool on)
    {
        return on ? "●" : "○";
    }
}

/// <summary>
/// The line errors the driver reports through ClearCommError. The driver only says which kinds happened since the last
/// call, not how many characters were affected, so a count here is the number of checks that found the error.
/// </summary>
public sealed class SerialErrorCounters
{
    public const uint ReceiveBufferOverflow = 0x0001;

    public const uint HardwareOverrun = 0x0002;

    public const uint ParityError = 0x0004;

    public const uint FramingError = 0x0008;

    public const uint BreakCondition = 0x0010;

    public long BufferOverflows { get; private set; }

    public long Overruns { get; private set; }

    public long ParityErrors { get; private set; }

    public long FramingErrors { get; private set; }

    public long Breaks { get; private set; }

    public long Total => BufferOverflows + Overruns + ParityErrors + FramingErrors + Breaks;

    /// <summary>Adds the flags of one check; true when it reported anything.</summary>
    public bool Add(uint flags)
    {
        bool any = false;
        if ((flags & ReceiveBufferOverflow) != 0)
        {
            BufferOverflows++;
            any = true;
        }
        if ((flags & HardwareOverrun) != 0)
        {
            Overruns++;
            any = true;
        }
        if ((flags & ParityError) != 0)
        {
            ParityErrors++;
            any = true;
        }
        if ((flags & FramingError) != 0)
        {
            FramingErrors++;
            any = true;
        }
        if ((flags & BreakCondition) != 0)
        {
            Breaks++;
            any = true;
        }
        return any;
    }

    public void Reset()
    {
        BufferOverflows = 0;
        Overruns = 0;
        ParityErrors = 0;
        FramingErrors = 0;
        Breaks = 0;
    }

    /// <summary>The kinds seen so far, for example "framing, parity"; empty when none.</summary>
    public string Summary()
    {
        List<string> parts = new List<string>();
        if (FramingErrors > 0)
        {
            parts.Add("framing");
        }
        if (ParityErrors > 0)
        {
            parts.Add("parity");
        }
        if (Overruns > 0)
        {
            parts.Add("overrun");
        }
        if (BufferOverflows > 0)
        {
            parts.Add("buffer overflow");
        }
        if (Breaks > 0)
        {
            parts.Add("break");
        }
        return string.Join(", ", parts);
    }

    /// <summary>What the person should try, based on the kinds seen; empty when none.</summary>
    public string Hint()
    {
        List<string> lines = new List<string>();
        if (FramingErrors > 0 || ParityErrors > 0)
        {
            lines.Add("Framing/parity errors usually mean the baud rate, data bits, parity or stop bits differ from the device's.");
        }
        if (Overruns > 0 || BufferOverflows > 0)
        {
            lines.Add("Overrun/overflow means data arrives faster than it is read: lower the baud rate or turn on flow control.");
        }
        if (Breaks > 0)
        {
            lines.Add("A break on the line means the device is in reset, powered off or its cable is unplugged.");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
