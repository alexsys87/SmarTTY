using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SmarTTY.Terminal;

// Regression harness for the xterm protocol pieces of the SSH terminal (Terminal/XtermProtocol.cs):
//  * modified cursor/function keys (Ctrl+Left must be ESC [ 1 ; 5 D, not ESC [ ; 5 D),
//  * mouse reports in the X10 and SGR (mode 1006) encodings,
//  * paste preparation (line breaks, bracketed paste),
//  * SGR parsing with 256-colour and 24-bit colours.
// Run with: dotnet run --project tests/XtermProtocolTests
internal static class Program
{
    private static int _passed;

    private const string Esc = "\u001b";

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static string Show(string text)
    {
        return text?.Replace(Esc, "ESC").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static void Equal(string actual, string expected, string name)
    {
        Check(actual == expected, name + (actual == expected ? "" : $" (expected '{Show(expected)}', got '{Show(actual)}')"));
    }

    private static void KeyChecks()
    {
        Equal(XtermProtocol.ApplyKeyModifiers(Esc + "[A", false, false, false), Esc + "[A", "no modifiers: unchanged");
        Equal(XtermProtocol.ApplyKeyModifiers(Esc + "[D", false, false, true), Esc + "[1;5D", "Ctrl+Left (normal cursor mode)");
        Equal(XtermProtocol.ApplyKeyModifiers(Esc + "OD", false, false, true), Esc + "[1;5D", "Ctrl+Left (application cursor mode)");
        Equal(XtermProtocol.ApplyKeyModifiers(Esc + "[A", true, false, false), Esc + "[1;2A", "Shift+Up");
        Equal(XtermProtocol.ApplyKeyModifiers(Esc + "[C", false, true, false), Esc + "[1;3C", "Alt+Right");
        Equal(XtermProtocol.ApplyKeyModifiers(Esc + "[H", true, false, true), Esc + "[1;6H", "Ctrl+Shift+Home");
        Equal(XtermProtocol.ApplyKeyModifiers(Esc + "[F", false, false, true), Esc + "[1;5F", "Ctrl+End");
        Equal(XtermProtocol.ApplyKeyModifiers(Esc + "OP", true, false, false), Esc + "[1;2P", "Shift+F1");
        Equal(XtermProtocol.ApplyKeyModifiers(Esc + "[5~", false, false, true), Esc + "[5;5~", "Ctrl+PageUp");
        Equal(XtermProtocol.ApplyKeyModifiers(Esc + "[3~", true, true, true), Esc + "[3;8~", "Ctrl+Alt+Shift+Delete");
        Equal(XtermProtocol.ApplyKeyModifiers(Esc + "[15~", false, false, true), Esc + "[15;5~", "Ctrl+F5");
        Equal(XtermProtocol.ApplyKeyModifiers(Esc + "Op", false, false, true), Esc + "Op", "application keypad keys have no modified form");
    }

    private static string Ascii(byte[] bytes)
    {
        return Encoding.ASCII.GetString(bytes);
    }

    private static void MouseChecks()
    {
        Check(XtermProtocol.EncodeMouseReport(0, 0, 0, release: false, sgr: false).SequenceEqual(new byte[] { 27, (byte)'[', (byte)'M', 32, 33, 33 }), "X10: left press at the top-left cell");
        Check(XtermProtocol.EncodeMouseReport(2, 9, 4, release: true, sgr: false).SequenceEqual(new byte[] { 27, (byte)'[', (byte)'M', 35, 42, 37 }), "X10: a release is reported as button 3");
        Check(XtermProtocol.EncodeMouseReport(16 | 1, 0, 0, release: true, sgr: false)[3] == 32 + (16 | 3), "X10: release keeps the modifier bits");
        Check(XtermProtocol.EncodeMouseReport(0, 400, 300, release: false, sgr: false).Skip(4).All(b => b == 255), "X10: coordinates beyond 222 are clamped to 255");
        Check(XtermProtocol.EncodeMouseReport(0, -3, -1, release: false, sgr: false).Skip(4).All(b => b == 33), "negative coordinates (scrolled back) are clamped to the first cell");
        Equal(Ascii(XtermProtocol.EncodeMouseReport(0, 0, 0, release: false, sgr: true)), Esc + "[<0;1;1M", "SGR: left press");
        Equal(Ascii(XtermProtocol.EncodeMouseReport(2, 9, 4, release: true, sgr: true)), Esc + "[<2;10;5m", "SGR: release keeps the button and ends with 'm'");
        Equal(Ascii(XtermProtocol.EncodeMouseReport(64, 299, 99, release: false, sgr: true)), Esc + "[<64;300;100M", "SGR: wheel up, coordinates above 222");
        Equal(Ascii(XtermProtocol.EncodeMouseReport(32, 4, 5, release: false, sgr: true)), Esc + "[<32;5;6M", "SGR: drag with the left button");
    }

    private static void PasteChecks()
    {
        Equal(XtermProtocol.PreparePaste("a\r\nb\nc\rd", "\r", bracketed: false), "a\rb\rc\rd", "line breaks become CR (what Enter sends)");
        Equal(XtermProtocol.PreparePaste("a\r\nb", "\n", bracketed: false), "a\nb", "LF line breaks on request");
        Equal(XtermProtocol.PreparePaste("ls\n", "\r", bracketed: true), Esc + "[200~ls\r" + Esc + "[201~", "bracketed paste wraps the text");
        Equal(XtermProtocol.PreparePaste("x" + Esc + "[201~rm -rf ~\n", "\r", bracketed: true), Esc + "[200~xrm -rf ~\r" + Esc + "[201~", "an end marker inside the text cannot end the paste early");
        Check(XtermProtocol.PreparePaste(null, "\r", bracketed: true) == null, "null clipboard text stays null");
        Equal(XtermProtocol.PreparePaste("", "\r", bracketed: true), "", "empty clipboard text sends nothing");
    }

    private static string Sgr(string parameters)
    {
        return string.Join(" ", XtermProtocol.ParseSgr(parameters.Split(';')).Select(a => a.ToString()));
    }

    private static void SgrChecks()
    {
        Equal(Sgr(""), "0", "ESC [ m is a reset");
        Equal(Sgr("0;1;31;42"), "0 1 31 42", "plain attributes");
        Equal(Sgr("38;5;1"), "38:1", "38;5;1 is palette red, not bold");
        Equal(Sgr("38;5;196;1"), "38:C4 1", "256-colour foreground followed by bold");
        Equal(Sgr("48;5;45"), "48:2D", "48;5;45 is a background colour, not magenta 45");
        Equal(Sgr("38;2;255;100;0"), "38:" + (XtermProtocol.RgbColorFlag | 0xFF6400).ToString("X"), "24-bit foreground");
        Equal(Sgr("38;2;40;45;100;4"), "38:" + (XtermProtocol.RgbColorFlag | 0x282D64).ToString("X") + " 4", "24-bit colour components are not attributes; underline after it");
        Equal(Sgr("38:5:208"), "38:D0", "colon form 38:5:N");
        Equal(Sgr("48:2::10:20:30"), "48:" + (XtermProtocol.RgbColorFlag | 0x0A141E).ToString("X"), "colon form with an empty colour space id");
        Equal(Sgr("38:2:10:20:30"), "38:" + (XtermProtocol.RgbColorFlag | 0x0A141E).ToString("X"), "colon form without colour space id");
        Equal(Sgr("38;2;999;0;5"), "38:" + (XtermProtocol.RgbColorFlag | 0xFF0005).ToString("X"), "out of range colour components are clamped");
        Equal(Sgr("38;2;1;-1;5;1"), "1", "an invalid 24-bit colour is dropped together with its components");
        Equal(Sgr("38;5"), "", "an incomplete extended colour is ignored");
        Equal(Sgr("38;5;300"), "38:FF", "palette index is clamped to 255");
        Equal(Sgr("4:0;4:3"), "24 4", "4:0 is no underline, 4:3 (curly) is underline");
        Equal(Sgr("1;x;22"), "1 22", "a non-numeric parameter is skipped, not treated as a reset");
    }

    private static void PaletteChecks()
    {
        Check(XtermProtocol.Xterm256ToRgb(16) == 0x000000, "16 is black");
        Check(XtermProtocol.Xterm256ToRgb(196) == 0xFF0000, "196 is pure red");
        Check(XtermProtocol.Xterm256ToRgb(21) == 0x0000FF, "21 is pure blue");
        Check(XtermProtocol.Xterm256ToRgb(231) == 0xFFFFFF, "231 is white");
        Check(XtermProtocol.Xterm256ToRgb(208) == 0xFF8700, "208 is orange (ff8700)");
        Check(XtermProtocol.Xterm256ToRgb(232) == 0x080808, "232 is the darkest grey");
        Check(XtermProtocol.Xterm256ToRgb(255) == 0xEEEEEE, "255 is the lightest grey");
        bool threw = false;
        try
        {
            XtermProtocol.Xterm256ToRgb(15);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }
        Check(threw, "the 16 base colours come from the user's palette");
    }

    private static int Main()
    {
        KeyChecks();
        MouseChecks();
        PasteChecks();
        SgrChecks();
        PaletteChecks();
        Console.WriteLine($"All {_passed} checks passed.");
        return 0;
    }
}
