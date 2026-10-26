using System;
using System.Linq;
using VisualTTY.DeviceTerminal;

// Regression checks for what the COM/TCP terminal sends: HEX input (separators, 0x prefixes, "send immediately"),
// line endings of pasted text, and the transfer rate shown in the status bar.
// Run with: dotnet run --project tests/DeviceInputTextTests
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

    private static bool Bytes(string text, params byte[] expected)
    {
        return DeviceInputText.ParseHex(text).SequenceEqual(expected);
    }

    private static bool Refused(string text)
    {
        try
        {
            DeviceInputText.ParseHex(text);
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }

    private static bool Pasted(string text, params byte[] expected)
    {
        return DeviceInputText.TryParsePastedHex(text, out byte[] bytes) && System.Linq.Enumerable.SequenceEqual(bytes, expected);
    }

    private static bool NotPasted(string text)
    {
        return !DeviceInputText.TryParsePastedHex(text, out byte[] bytes) && bytes == null;
    }

    private static bool Near(double a, double b)
    {
        return Math.Abs(a - b) < 0.5;
    }

    private static int Main()
    {
        // ---- HEX input: what worked before still works
        Check(Bytes("41 42 43", 0x41, 0x42, 0x43), "space separated");
        Check(Bytes("414243", 0x41, 0x42, 0x43), "digits paired from the left");
        Check(Bytes("123", 0x12, 0x03), "an odd last digit is a byte of its own");
        Check(Bytes("a", 0x0A), "a single digit");
        Check(Bytes("ff Fe", 0xFF, 0xFE), "both cases");
        Check(Bytes("", new byte[0]) && Bytes("   ", new byte[0]), "empty input sends nothing");

        // ---- HEX input: separators and prefixes
        Check(Bytes("41,42,43", 0x41, 0x42, 0x43), "commas");
        Check(Bytes("41;42; 43", 0x41, 0x42, 0x43), "semicolons");
        Check(Bytes("41:42:43", 0x41, 0x42, 0x43), "colons (MAC address style)");
        Check(Bytes("41-42-43", 0x41, 0x42, 0x43), "dashes");
        Check(Bytes("41\t42\r\n43", 0x41, 0x42, 0x43), "tabs and line breaks");
        Check(Bytes("0x41 0X42", 0x41, 0x42), "0x prefix");
        Check(Bytes("0x41,0x0d,0x0a", 0x41, 0x0D, 0x0A), "0x prefix with commas (C array style)");
        Check(Bytes("0x414243", 0x41, 0x42, 0x43), "one prefix for several bytes");
        Check(Bytes("0x7", 0x07), "prefix with one digit");
        Check(Refused("0x"), "a lone prefix is refused");
        Check(Refused("41 zz"), "an invalid character is refused");
        Check(Refused("4x1"), "x inside a group is refused");

        // ---- HEX input: send immediately
        Check(!DeviceInputText.IsHexReadyToSend(""), "nothing typed: not ready");
        Check(!DeviceInputText.IsHexReadyToSend("4"), "one digit: wait for the second");
        Check(DeviceInputText.IsHexReadyToSend("41"), "two digits: ready");
        Check(DeviceInputText.IsHexReadyToSend("4 "), "a digit and a space: ready");
        Check(DeviceInputText.IsHexReadyToSend("4,"), "a digit and a comma: ready");
        Check(!DeviceInputText.IsHexReadyToSend("0"), "0 may be the start of 0x");
        Check(!DeviceInputText.IsHexReadyToSend("0x"), "0x: wait for digits");
        Check(!DeviceInputText.IsHexReadyToSend("0x4"), "0x4: wait for the second digit");
        Check(DeviceInputText.IsHexReadyToSend("0x41"), "0x41: ready");
        Check(!DeviceInputText.IsHexReadyToSend(" "), "only a separator: not ready");
        Check(!DeviceInputText.IsHexReadyToSend("4g"), "invalid input is kept to be corrected");
        Check(DeviceInputText.IsHexReadyToSend("41 42 43"), "a pasted line: ready");

        // ---- HEX input: allowed characters
        Check("0123456789abcdefABCDEF ,;:-xX".All(DeviceInputText.IsHexInputCharacter), "digits, separators and x are accepted");
        Check(!"gGzZ.!".Any(DeviceInputText.IsHexInputCharacter), "other characters are not");

        // ---- pasted text: line endings
        Check(DeviceInputText.ConvertLineEndings("a\r\nb\nc\rd", "\r") == "a\rb\rc\rd", "every kind of line end becomes CR");
        Check(DeviceInputText.ConvertLineEndings("a\r\nb\nc\rd", "\r\n") == "a\r\nb\r\nc\r\nd", "every kind of line end becomes CR LF");
        Check(DeviceInputText.ConvertLineEndings("a\r\n\r\nb", "\n") == "a\n\nb", "an empty line stays an empty line");
        Check(DeviceInputText.ConvertLineEndings("a\n\rb", "\r\n") == "a\r\n\r\nb", "LF CR is two line ends");
        Check(DeviceInputText.ConvertLineEndings("line\r\n", "\r") == "line\r", "a trailing line end is kept");
        Check(DeviceInputText.ConvertLineEndings("no breaks", "\r") == "no breaks", "text without line ends is unchanged");
        Check(DeviceInputText.ConvertLineEndings("a\r\nb", null) == "a\r\nb", "no line ending chosen: unchanged");
        Check(DeviceInputText.ConvertLineEndings("", "\r") == "" && DeviceInputText.ConvertLineEndings(null, "\r") == null, "empty and null");

        // ---- transfer rate
        TransferRateMeter meter = new TransferRateMeter();
        Check(meter.Sample(0, 1000) == 0, "first sample: no rate yet");
        Check(Near(meter.Sample(250, 1250), 1000), "250 bytes in 250 ms is 1000 B/s");
        Check(Near(meter.Sample(500, 1500), 1000), "steady rate");
        for (long t = 1750; t <= 4000; t += 250)
        {
            meter.Sample(500, t);
        }
        Check(meter.Sample(500, 4250) == 0, "no traffic for longer than the window: 0 B/s");
        // 2000 bytes arrive after a quiet window: averaged over the window, not over the whole quiet time.
        Check(Near(meter.Sample(2500, 4500), 2000 * 1000.0 / TransferRateMeter.WindowMs), "traffic resumes: the rate rises at once");
        double afterBurst = 0;
        for (long t = 4750; t <= 7000; t += 250)
        {
            afterBurst = meter.Sample(2500, t);
        }
        Check(afterBurst == 0, "a burst leaves the window after WindowMs");
        Check(meter.Sample(10, 7250) == 0, "a counter reset (new connection) starts over instead of a negative rate");
        Check(Near(meter.Sample(110, 7350), 1000), "and measures the new connection");
        TransferRateMeter slow = new TransferRateMeter();
        slow.Sample(0, 0);
        Check(Near(slow.Sample(96, 100), 960), "9600 bps worth of data");

        Check(TransferRateMeter.FormatRate(0) == "0 B/s", "rate: idle");
        Check(TransferRateMeter.FormatRate(959.6) == "960 B/s", "rate: bytes");
        Check(TransferRateMeter.FormatRate(11571) == "11.3 KB/s", "rate: KB");
        Check(TransferRateMeter.FormatRate(1.5 * 1048576) == "1.5 MB/s", "rate: MB");

        // ---- pasted text as bytes (the HEX view of a COM/TCP terminal)
        Check(Pasted("DE AD BE EF", 0xDE, 0xAD, 0xBE, 0xEF), "paste: space separated bytes");
        Check(Pasted("DEADBEEF", 0xDE, 0xAD, 0xBE, 0xEF), "paste: one group of digits");
        Check(Pasted("0xDE, 0xAD,", 0xDE, 0xAD), "paste: 0x prefixes and a trailing comma");
        Check(Pasted("{0x41, 0x42, 0x43}", 0x41, 0x42, 0x43), "paste: a C array");
        Check(Pasted("[0x41,0x42]", 0x41, 0x42), "paste: brackets");
        Check(Pasted("\\x41\\x42\\x0d\\x0a", 0x41, 0x42, 0x0D, 0x0A), "paste: backslash-x escapes");
        Check(Pasted("de:ad:be:ef", 0xDE, 0xAD, 0xBE, 0xEF), "paste: colons, lower case");
        Check(Pasted("41\r\n42\r\n", 0x41, 0x42), "paste: one byte per line");
        Check(Pasted("1 2 a", 0x01, 0x02, 0x0A), "paste: single digits are bytes");
        Check(NotPasted("hello world"), "paste: ordinary text is not bytes");
        Check(NotPasted("123"), "paste: an odd group of several digits is not bytes");
        Check(NotPasted("DE AD XX"), "paste: a non-HEX character");
        Check(NotPasted("   "), "paste: blanks");
        Check(NotPasted(""), "paste: empty");
        Check(NotPasted(null), "paste: null");
        Check(NotPasted("0x"), "paste: a lone prefix");
        Check(NotPasted("{}"), "paste: empty braces");

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
