using System;
using VisualTTY.DeviceTerminal;

// Regression checks for the serial port rules: baud rate input, frame validation, speed arithmetic, the write timeout,
// the modem line flags and the line error counters.
// Run with: dotnet run --project tests/SerialPortRulesTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static bool Near(double a, double b)
    {
        return Math.Abs(a - b) < 0.0001;
    }

    private static int Main()
    {
        // ---- baud rate input
        foreach (string good in new[] { "115200", " 9600 ", "50", "16000000", "921600" })
        {
            Check(SerialPortRules.TryParseBaudRate(good, out int rate, out string err) && err == null && rate == int.Parse(good.Trim()), "baud accepted: '" + good + "'");
        }
        foreach (string bad in new[] { "", "   ", null, "abc", "-9600", "0", "49", "16000001", "11520.0", "1 15200", "99999999999", "+9600", "9,600" })
        {
            Check(!SerialPortRules.TryParseBaudRate(bad, out int rate, out string err) && rate == 0 && !string.IsNullOrEmpty(err), "baud refused with a message: '" + (bad ?? "null") + "'");
        }

        // ---- stop bits mapping (framework StopBits: None 0, One 1, Two 2, OnePointFive 3)
        Check(SerialPortRules.StopBitsKindFrom(1) == SerialStopBitsKind.One, "StopBits.One -> one");
        Check(SerialPortRules.StopBitsKindFrom(0) == SerialStopBitsKind.One, "StopBits.None is not a real setting -> one");
        Check(SerialPortRules.StopBitsKindFrom(2) == SerialStopBitsKind.Two, "StopBits.Two -> two");
        Check(SerialPortRules.StopBitsKindFrom(3) == SerialStopBitsKind.OnePointFive, "StopBits.OnePointFive -> 1.5");
        Check(SerialPortRules.StopBitsKindFrom(99) == SerialStopBitsKind.One, "unknown value -> one");
        foreach (SerialStopBitsKind kind in new[] { SerialStopBitsKind.One, SerialStopBitsKind.OnePointFive, SerialStopBitsKind.Two })
        {
            Check(SerialPortRules.StopBitsKindFrom(SerialPortRules.FrameworkStopBitsFrom(kind)) == kind, "stop bits round trip: " + kind);
        }
        // The settings form lists 1, 1.5, 2 and uses the list index as the enum value.
        Check((int)SerialStopBitsKind.One == 0 && (int)SerialStopBitsKind.OnePointFive == 1 && (int)SerialStopBitsKind.Two == 2, "enum order matches the form's list");

        // ---- frame validation
        foreach (int data in new[] { 5, 6, 7, 8 })
        {
            Check(SerialPortRules.ValidateFrame(data, SerialStopBitsKind.One) == null, data + " data bits with 1 stop bit is allowed");
        }
        Check(SerialPortRules.ValidateFrame(5, SerialStopBitsKind.OnePointFive) == null, "5 data bits with 1.5 stop bits is allowed");
        foreach (int data in new[] { 6, 7, 8 })
        {
            Check(SerialPortRules.ValidateFrame(data, SerialStopBitsKind.Two) == null, data + " data bits with 2 stop bits is allowed");
            string problem = SerialPortRules.ValidateFrame(data, SerialStopBitsKind.OnePointFive);
            Check(problem != null && problem.Contains("1.5"), data + " data bits with 1.5 stop bits is refused with an explanation");
        }
        string five2 = SerialPortRules.ValidateFrame(5, SerialStopBitsKind.Two);
        Check(five2 != null && five2.Contains("5 data bits"), "5 data bits with 2 stop bits is refused with an explanation");
        Check(SerialPortRules.ValidateFrame(4, SerialStopBitsKind.One) != null && SerialPortRules.ValidateFrame(9, SerialStopBitsKind.One) != null, "data bits outside 5..8 are refused");

        // ---- speed arithmetic
        Check(Near(SerialPortRules.BitsPerFrame(8, false, SerialStopBitsKind.One), 10), "8N1 is 10 bits per character");
        Check(Near(SerialPortRules.BitsPerFrame(8, true, SerialStopBitsKind.One), 11), "8E1 is 11 bits");
        Check(Near(SerialPortRules.BitsPerFrame(7, true, SerialStopBitsKind.Two), 11), "7E2 is 11 bits");
        Check(Near(SerialPortRules.BitsPerFrame(5, false, SerialStopBitsKind.OnePointFive), 7.5), "5N1.5 is 7.5 bits");
        Check(Near(SerialPortRules.BytesPerSecond(115200, 8, false, SerialStopBitsKind.One), 11520), "115200 baud 8N1 is 11520 bytes/s");
        Check(Near(SerialPortRules.BytesPerSecond(9600, 8, true, SerialStopBitsKind.Two), 9600 / 12.0), "9600 baud 8E2 is 800 bytes/s");
        Check(SerialPortRules.BytesPerSecond(0, 8, false, SerialStopBitsKind.One) == 0 && SerialPortRules.BytesPerSecond(-5, 8, false, SerialStopBitsKind.One) == 0, "no speed for a nonsensical baud rate");
        Check(SerialPortRules.FormatSpeed(11520) == "11.3 KB/s", "11520 bytes/s is shown as 11.3 KB/s: " + SerialPortRules.FormatSpeed(11520));
        Check(SerialPortRules.FormatSpeed(800) == "800.0 bytes/s", "800 bytes/s: " + SerialPortRules.FormatSpeed(800));
        Check(SerialPortRules.FormatSpeed(2.5 * 1048576) == "2.5 MB/s", "2.5 MB/s: " + SerialPortRules.FormatSpeed(2.5 * 1048576));

        // ---- write timeout
        int fast = SerialPortRules.WriteTimeoutMs(115200, 8, false, SerialStopBitsKind.One, 512, 5000);
        Check(fast >= 5000 && fast < 5100, "115200 baud: the stall allowance dominates: " + fast);
        int slow = SerialPortRules.WriteTimeoutMs(300, 8, false, SerialStopBitsKind.One, 512, 5000);
        Check(slow > 5000 + 17000 && slow < 5000 + 18000, "300 baud: a chunk needs 17 s on the wire, so the timeout grows: " + slow);
        Check(SerialPortRules.WriteTimeoutMs(0, 8, false, SerialStopBitsKind.One, 512, 5000) > 5000, "unknown baud rate falls back to a sane timeout");
        Check(SerialPortRules.WriteTimeoutMs(1, 8, true, SerialStopBitsKind.Two, int.MaxValue / 4, 5000) > 0, "huge chunks do not overflow");

        // ---- frame names
        Check(SerialPortRules.FrameName(8, 0, SerialStopBitsKind.One) == "8N1", "8N1");
        Check(SerialPortRules.FrameName(7, 2, SerialStopBitsKind.Two) == "7E2", "7E2");
        Check(SerialPortRules.FrameName(5, 1, SerialStopBitsKind.OnePointFive) == "5O1.5", "5O1.5");

        // ---- modem lines
        SerialModemLines none = SerialModemLines.FromFlags(0);
        Check(!none.Cts && !none.Dsr && !none.Ring && !none.Dcd, "no flags: all lines low");
        SerialModemLines cts = SerialModemLines.FromFlags(0x10);
        Check(cts.Cts && !cts.Dsr && !cts.Ring && !cts.Dcd, "0x10 is CTS");
        SerialModemLines all = SerialModemLines.FromFlags(0x10 | 0x20 | 0x40 | 0x80);
        Check(all.Cts && all.Dsr && all.Ring && all.Dcd, "all four flags");
        Check(SerialModemLines.FromFlags(0x20).Dsr && SerialModemLines.FromFlags(0x40).Ring && SerialModemLines.FromFlags(0x80).Dcd, "DSR 0x20, RING 0x40, RLSD 0x80");
        Check(SerialModemLines.FromFlags(0x0F).ToText() == none.ToText(), "unrelated bits are ignored");
        Check(none.ToText() == "CTS○ DSR○ DCD○ RI○", "text with all lines low");
        Check(cts.ToText() == "CTS● DSR○ DCD○ RI○", "text with CTS high");

        // ---- error counters
        SerialErrorCounters errors = new SerialErrorCounters();
        Check(!errors.Add(0) && errors.Total == 0 && errors.Summary() == "" && errors.Hint() == "", "a clean check reports nothing");
        Check(errors.Add(SerialErrorCounters.FramingError) && errors.FramingErrors == 1, "framing error counted");
        Check(errors.Add(SerialErrorCounters.FramingError | SerialErrorCounters.ParityError) && errors.FramingErrors == 2 && errors.ParityErrors == 1, "two flags in one check");
        Check(errors.Summary() == "framing, parity", "summary names the kinds: " + errors.Summary());
        Check(errors.Hint().Contains("baud rate"), "framing/parity hint points at the settings");
        Check(errors.Add(SerialErrorCounters.HardwareOverrun | SerialErrorCounters.ReceiveBufferOverflow | SerialErrorCounters.BreakCondition) && errors.Total == 6, "overrun, overflow and break counted");
        Check(errors.Summary() == "framing, parity, overrun, buffer overflow, break", "summary order: " + errors.Summary());
        Check(errors.Hint().Contains("flow control") && errors.Hint().Contains("break"), "hints for overrun and break");
        Check(!errors.Add(0x0100), "an unrelated flag (TXFULL) is not a line error");
        errors.Reset();
        Check(errors.Total == 0 && errors.Summary() == "", "reset clears everything");

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
