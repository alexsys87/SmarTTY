using System;
using System.Collections.Generic;
using SmarTTY.Terminal;
using SmarTTY.Terminal.Emulation;

// Checks of the helpers of the terminal's escape sequence parser that need no screen (Terminal/Emulation/): what the SGR attributes do to
// a style, the numbers of a CSI sequence, the parts of an OSC string, and the state that the escape sequences set (TerminalModes).
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

    private static void Equal<T>(T actual, T expected, string name)
    {
        Check(EqualityComparer<T>.Default.Equals(actual, expected), name + (EqualityComparer<T>.Default.Equals(actual, expected) ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static CellStyle Apply(CellStyle style, params int[] codes)
    {
        List<XtermProtocol.SgrAttribute> attributes = new List<XtermProtocol.SgrAttribute>();
        foreach (int code in codes)
        {
            attributes.Add(new XtermProtocol.SgrAttribute(code));
        }
        SgrStyle.Apply(ref style, attributes);
        return style;
    }

    private static CellStyle ApplyColour(CellStyle style, int code, int colour)
    {
        SgrStyle.Apply(ref style, new List<XtermProtocol.SgrAttribute> { new XtermProtocol.SgrAttribute(code, colour) });
        return style;
    }

    private static void Main()
    {
        Sgr();
        Csi();
        Osc();
        Modes();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " terminal helper checks passed.");
    }

    private static void Sgr()
    {
        CellStyle plain = default;
        CellStyle red = Apply(plain, 31);
        Equal(red.Foreground, (byte)2, "31 is the second palette colour of the foreground");
        Equal(red.ExtendedForeground, 0, "...and no extended colour");
        Equal(Apply(plain, 30).Foreground, (byte)1, "30 is the first");
        Equal(Apply(plain, 37).Foreground, (byte)8, "37 is the eighth");
        Equal(Apply(plain, 44).Background, (byte)5, "44 is the fifth colour of the background");

        CellStyle bright = Apply(red, 91);
        Equal(bright.Foreground, (byte)0, "a bright colour clears the plain foreground");
        Equal(bright.ExtendedForeground, 10, "...and is the palette entry 8 + 1 (stored + 1)");
        Equal(Apply(plain, 97).ExtendedForeground, 16, "97 is the palette entry 15 (stored + 1)");
        CellStyle brightBack = Apply(Apply(plain, 41), 101);
        Equal(brightBack.Background, (byte)0, "a bright background clears the plain one");
        Equal(brightBack.ExtendedBackground, 10, "...and is the palette entry 8 (stored + 1)");
        Check(!Apply(plain, 91).Bold, "a bright colour does not turn bold on");

        Equal(Apply(bright, 31).ExtendedForeground, 0, "a plain colour after a bright one clears the extended colour");
        Equal(Apply(Apply(plain, 101), 42).ExtendedBackground, 0, "...for the background too");

        CellStyle palette = ApplyColour(plain, 38, 196);
        Equal(palette.ExtendedForeground, 197, "38 with a palette colour stores it + 1");
        Equal(palette.Foreground, (byte)0, "...and clears the plain foreground");
        CellStyle rgbBack = ApplyColour(plain, 48, XtermProtocol.RgbColorFlag | 0x112233);
        Equal(rgbBack.ExtendedBackground, XtermProtocol.RgbColorFlag | 0x112233, "48 with a true colour keeps it as it is");
        Equal(ApplyColour(palette, 38, XtermProtocol.RgbColorFlag | 0xABCDEF).ExtendedForeground, XtermProtocol.RgbColorFlag | 0xABCDEF, "...for the foreground too");

        CellStyle many = Apply(plain, 1, 4, 7, 31, 42);
        Check(many.Bold && many.Underline && many.Inverse && many.Foreground == 2 && many.Background == 3, "attributes add up in order");
        Check(!Apply(many, 22).Bold && Apply(many, 22).Underline, "22 turns bold off only");
        Check(!Apply(many, 21).Bold, "21 turns bold off too");
        Check(!Apply(many, 24).Underline && Apply(many, 24).Bold, "24 turns underline off only");
        Check(!Apply(many, 27).Inverse && Apply(many, 27).Underline, "27 turns inverse off only");
        Equal(Apply(many, 39).Foreground, (byte)0, "39 is the default foreground");
        Equal(Apply(ApplyColour(many, 38, 5), 39).ExtendedForeground, 0, "...also of an extended colour");
        Equal(Apply(many, 49).Background, (byte)0, "49 is the default background");
        Equal(Apply(ApplyColour(many, 48, 5), 49).ExtendedBackground, 0, "...also of an extended colour");
        Check(Apply(many, 0).IsDefault, "0 is the default style");
        Check(Apply(many, 1, 0, 4).Underline && !Apply(many, 1, 0, 4).Bold && !Apply(many, 1, 0, 4).Inverse, "0 in the middle drops what came before it");

        Check(Apply(many, 9, 5, 8, 53, 999).StyleEquals(many), "codes the terminal does not know change nothing");
        Check(Apply(many).StyleEquals(many), "no attributes change nothing");
    }

    private static void Csi()
    {
        Equal(string.Join(",", CsiParameters.Parse(new[] { "5" }, 1, 1)), "5,1", "a missing item is its default");
        Equal(string.Join(",", CsiParameters.Parse(new[] { "" }, 1, 1)), "1,1", "an empty item is the default");
        Equal(string.Join(",", CsiParameters.Parse(new string[0], 1, 1)), "1,1", "no items are the defaults");
        Equal(string.Join(",", CsiParameters.Parse(new[] { "x", "3" }, 1, 1)), "1,3", "an item that is not a number is the default");
        Equal(string.Join(",", CsiParameters.Parse(new[] { "0", "0" }, 1, 1)), "0,0", "a zero stays a zero");
        Equal(string.Join(",", CsiParameters.Parse(new[] { "-4", "12" }, 1, 1)), "-4,12", "a sign is read");
        Equal(string.Join(",", CsiParameters.Parse(new[] { "1", "2", "3" }, 1, 1, 1)), "1,2,3", "as many items as defaults");
        Equal(string.Join(",", CsiParameters.Parse(new[] { "7" })), "7", "no defaults and one number");
        Equal(CsiParameters.Parse(new string[0]).Length, 0, "no defaults and no items");
        Equal(CsiParameters.Parse(new[] { "99999999999" }, 4)[0], 4, "a number that does not fit is the default");

        Equal(string.Join(",", CsiParameters.Parse(new[] { "1", "2", "3" }, 1, 1)), "1,2,3", "more numbers than defaults are all read");
        Equal(string.Join(",", CsiParameters.Parse(new[] { "1", "2", "3" })), "1,2,3", "...also with no defaults at all");

        // A known fault, kept as it was: an item past the defaults that is empty or not a number wants a default that is not there
        // (ESC [ ; ; ; ; H, a cursor position with five empty items).
        foreach (string[] items in new[] { new[] { "", "", "", "", "" }, new[] { "1", "2", "x" }, new[] { "1", "2", "3", "" } })
        {
            bool threw = false;
            try
            {
                CsiParameters.Parse(items, 1, 1);
            }
            catch (IndexOutOfRangeException)
            {
                threw = true;
            }
            Check(threw, "an item past the defaults that is empty or not a number reads past the end: '" + string.Join(";", items) + "'");
        }
    }

    private static void Modes()
    {
        TerminalModes modes = new TerminalModes();
        Check(!modes.ApplicationKeypad && !modes.ApplicationCursorKeys && modes.IncompleteEscape == null && modes.SavedCursor.IsEmpty && modes.SavedStyle.IsDefault, "a new terminal has no modes set");
        Check(!modes.PrivateModes.IsSet(1049) && !modes.Charsets.GraphicsActive, "...no private modes and the plain character set");

        modes.ApplicationKeypad = true;
        modes.ApplicationCursorKeys = true;
        modes.PrivateModes.Set(1049, true);
        modes.PrivateModes.Set(2004, true);
        modes.PrivateModes.Save(2004);
        Check(modes.PrivateModes.TryGetSaved(2004, out bool wasSaved) && wasSaved, "a private mode can be saved");
        modes.Charsets.Designate('(', '0');
        modes.SavedCursor = new SmarTTY.TextEditing.GridPoint(3, 4);
        modes.SavedStyle = Apply(default, 31);
        modes.IncompleteEscape = "\u001b[3";
        Equal(modes.Charsets.Translate("q"), "\u2500", "the line drawing set is active once designated");

        modes.Reset();
        Check(!modes.ApplicationKeypad && !modes.ApplicationCursorKeys, "Reset: the keypad and the cursor keys are normal again");
        Check(!modes.PrivateModes.IsSet(1049) && !modes.PrivateModes.IsSet(2004), "Reset: the private modes are off");
        Check(modes.PrivateModes.TryGetSaved(2004, out bool saved) && !saved, "Reset: the saved private modes are gone (a saved value reads as off)");
        Equal(modes.Charsets.Translate("q"), "q", "Reset: the character set is plain again");
        Check(modes.SavedCursor.IsEmpty && modes.SavedStyle.IsDefault, "Reset: the saved cursor and style are forgotten");
        Equal(modes.IncompleteEscape, "\u001b[3", "Reset leaves the start of an escape sequence alone (it is not a mode)");
    }

    private static void Osc()
    {
        Check(OscString.TryParse("0;my title", out int command, out string text) && command == 0 && text == "my title", "command and text");
        Check(OscString.TryParse("2;a;b;c", out command, out text) && command == 2 && text == "a;b;c", "only the first ';' separates");
        Check(OscString.TryParse("7;", out command, out text) && command == 7 && text == "", "an empty text");
        Check(!OscString.TryParse("0", out command, out text) && text == null, "no ';' is no command");
        Check(!OscString.TryParse("x;y", out command, out text), "a command that is not a number");
        Check(!OscString.TryParse(";z", out command, out text), "an empty command");
        Check(!OscString.TryParse("", out command, out text), "an empty string");
        Check(OscString.TryParse(" 7 ;x", out command, out text) && command == 7, "blanks around the number are allowed (as int.TryParse does)");
        Check(OscString.TryParse("0;" + new string('t', 5000), out command, out text) && text.Length == 5000, "a long text is kept");

        Equal(OscString.TitleText("a\u0001b\tc\u007fd"), "abcd", "the control characters are taken out of a title");
        Equal(OscString.TitleText(""), "", "an empty title");
        Equal(OscString.TitleText("café 中文 \U0001F600"), "café 中文 \U0001F600", "other characters are kept");
        Equal(OscString.TitleText("a\u0085b\u009fc"), "abc", "the C1 control characters go too");
    }
}
