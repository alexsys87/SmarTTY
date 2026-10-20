using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SmarTTY.Terminal;

/// <summary>
/// Pure (UI-independent) pieces of the xterm protocol used by the terminal emulator: modified key sequences,
/// mouse reports, paste preparation and SGR (colour/attribute) parsing. Kept free of WinForms so that the
/// regression harness in tests/XtermProtocolTests can run them on any platform.
/// </summary>
public static class XtermProtocol
{
    public const char Escape = '\u001b';

    /// <summary>Marks a 24-bit colour in an encoded colour value; the low 24 bits hold 0xRRGGBB.</summary>
    public const int RgbColorFlag = 0x1000000;

    public const string BracketedPasteStart = "\u001b[200~";

    public const string BracketedPasteEnd = "\u001b[201~";

    /// <summary>
    /// One SGR attribute: <see cref="Code"/> is the attribute number (0, 1, 31, 38, ...), and for the extended
    /// colour attributes 38, 48 and 58 <see cref="Color"/> holds the colour (a palette index 0..255, or
    /// <see cref="RgbColorFlag"/> | 0xRRGGBB); it is -1 for all other attributes.
    /// </summary>
    public struct SgrAttribute
    {
        public int Code;

        public int Color;

        public SgrAttribute(int code, int color = -1)
        {
            Code = code;
            Color = color;
        }

        public override string ToString()
        {
            return (Color < 0) ? Code.ToString(CultureInfo.InvariantCulture) : $"{Code}:{Color:X}";
        }
    }

    /// <summary>
    /// Adds the xterm modifier parameter to the sequence of a cursor, editing or function key:
    /// ESC [ A / ESC O A become ESC [ 1 ; m A, and ESC [ 5 ~ becomes ESC [ 5 ; m ~, where m = 1 + (Shift 1 | Alt 2 | Ctrl 4).
    /// Sequences that have no modified form are returned unchanged.
    /// </summary>
    public static string ApplyKeyModifiers(string sequence, bool shift, bool alt, bool control)
    {
        int modifiers = (shift ? 1 : 0) | (alt ? 2 : 0) | (control ? 4 : 0);
        if (modifiers == 0 || sequence == null || sequence.Length < 3 || sequence[0] != Escape)
        {
            return sequence;
        }
        string parameter = (modifiers + 1).ToString(CultureInfo.InvariantCulture);
        char final = sequence[sequence.Length - 1];
        if (sequence.Length == 3 && (sequence[1] == '[' || (sequence[1] == 'O' && "ABCDHFPQRS".IndexOf(final) != -1)) && char.IsLetter(final))
        {
            return Escape + "[1;" + parameter + final;
        }
        if (sequence[1] == '[' && (final == '~' || char.IsLetter(final)))
        {
            return sequence.Substring(0, sequence.Length - 1) + ";" + parameter + final;
        }
        return sequence;
    }

    /// <summary>
    /// Encodes a mouse report. <paramref name="buttonCode"/> is the button (0 left, 1 middle, 2 right, 64/65 wheel)
    /// with the modifier bits (4 Shift, 8 Alt, 16 Ctrl) and the motion bit (32) already added; column and row are
    /// zero-based. The legacy X10 form reports a release as button 3 and cannot express coordinates above 222;
    /// the SGR form (mode 1006) keeps the button, ends a release with 'm' and has no coordinate limit.
    /// </summary>
    public static byte[] EncodeMouseReport(int buttonCode, int column, int row, bool release, bool sgr)
    {
        column = Math.Max(column, 0);
        row = Math.Max(row, 0);
        if (sgr)
        {
            string report = string.Format(CultureInfo.InvariantCulture, "{0}[<{1};{2};{3}{4}", Escape, buttonCode, column + 1, row + 1, release ? 'm' : 'M');
            return Encoding.ASCII.GetBytes(report);
        }
        if (release)
        {
            buttonCode = (buttonCode & ~3) | 3;
        }
        return new byte[6]
        {
            27,
            (byte)'[',
            (byte)'M',
            EncodeX10Value(buttonCode),
            EncodeX10Value(column + 1),
            EncodeX10Value(row + 1)
        };
    }

    private static byte EncodeX10Value(int value)
    {
        int encoded = value + 32;
        if (encoded >= 255)
        {
            return byte.MaxValue;
        }
        return (byte)encoded;
    }

    /// <summary>
    /// Prepares clipboard text for sending to the remote side: every line break (CR LF, LF or CR) becomes
    /// <paramref name="lineBreak"/>, and with bracketed paste (mode 2004) the text is wrapped in ESC [ 200 ~ ... ESC [ 201 ~.
    /// Paste markers inside the text are removed so that pasted data cannot end the bracket early and have the
    /// rest executed as typed input.
    /// </summary>
    public static string PreparePaste(string text, string lineBreak, bool bracketed)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        lineBreak = lineBreak ?? "\r";
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (lineBreak != "\n")
        {
            text = text.Replace("\n", lineBreak);
        }
        if (!bracketed)
        {
            return text;
        }
        text = text.Replace(BracketedPasteStart, "").Replace(BracketedPasteEnd, "");
        return BracketedPasteStart + text + BracketedPasteEnd;
    }

    /// <summary>
    /// Parses the parameters of an SGR sequence (ESC [ ... m) split at ';'. Extended colours are resolved in both
    /// notations: 38;5;N / 38;2;R;G;B and the colon forms 38:5:N, 38:2:R:G:B and 38:2::R:G:B (the same for 48 and 58),
    /// so that their sub-parameters are never mistaken for attributes of their own (38;5;1 is not "bold").
    /// An empty parameter means 0 (reset); a parameter that is not a number is skipped.
    /// </summary>
    public static List<SgrAttribute> ParseSgr(string[] items)
    {
        List<SgrAttribute> result = new List<SgrAttribute>();
        if (items == null || items.Length == 0)
        {
            result.Add(new SgrAttribute(0));
            return result;
        }
        for (int index = 0; index < items.Length; index++)
        {
            string item = items[index].Trim();
            if (item.IndexOf(':') != -1)
            {
                ParseColonAttribute(item, result);
                continue;
            }
            int code;
            if (item.Length == 0)
            {
                code = 0;
            }
            else if (!TryParseNumber(item, out code))
            {
                continue;
            }
            if (code == 38 || code == 48 || code == 58)
            {
                index += ParseSemicolonColor(items, index + 1, out int color);
                if (color >= 0)
                {
                    result.Add(new SgrAttribute(code, color));
                }
                continue;
            }
            result.Add(new SgrAttribute(code));
        }
        return result;
    }

    // Returns the number of items consumed after the 38/48/58 item; color is -1 when the colour is incomplete.
    private static int ParseSemicolonColor(string[] items, int start, out int color)
    {
        color = -1;
        if (start >= items.Length || !TryParseNumber(items[start], out int kind))
        {
            return 0;
        }
        if (kind == 5)
        {
            if (start + 1 < items.Length && TryParseNumber(items[start + 1], out int paletteIndex))
            {
                color = ToPaletteColor(paletteIndex);
                return 2;
            }
            return Math.Min(2, items.Length - start);
        }
        if (kind == 2)
        {
            if (start + 3 < items.Length && TryParseNumber(items[start + 1], out int red) && TryParseNumber(items[start + 2], out int green) && TryParseNumber(items[start + 3], out int blue))
            {
                color = ToRgbColor(red, green, blue);
                return 4;
            }
            return Math.Min(4, items.Length - start);
        }
        return 1;
    }

    private static void ParseColonAttribute(string item, List<SgrAttribute> result)
    {
        string[] parts = item.Split(':');
        if (!TryParseNumber(parts[0], out int code))
        {
            return;
        }
        if (code == 38 || code == 48 || code == 58)
        {
            if (parts.Length < 2 || !TryParseNumber(parts[1], out int kind))
            {
                return;
            }
            if (kind == 5 && parts.Length >= 3 && TryParseNumber(parts[2], out int paletteIndex))
            {
                result.Add(new SgrAttribute(code, ToPaletteColor(paletteIndex)));
            }
            else if (kind == 2 && parts.Length >= 5)
            {
                // 38:2:R:G:B or, per ITU T.416, 38:2:<colour space id>:R:G:B (the id is usually empty).
                int first = (parts.Length >= 6) ? 3 : 2;
                if (TryParseNumber(parts[first], out int red) && TryParseNumber(parts[first + 1], out int green) && TryParseNumber(parts[first + 2], out int blue))
                {
                    result.Add(new SgrAttribute(code, ToRgbColor(red, green, blue)));
                }
            }
            return;
        }
        if (code == 4 && parts.Length >= 2 && TryParseNumber(parts[1], out int underlineStyle))
        {
            // 4:0 is "no underline"; 4:1..4:5 are underline styles (single, double, curly, ...).
            result.Add(new SgrAttribute((underlineStyle == 0) ? 24 : 4));
            return;
        }
        result.Add(new SgrAttribute(code));
    }

    private static bool TryParseNumber(string text, out int value)
    {
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static int ToPaletteColor(int index)
    {
        return Math.Max(0, Math.Min(255, index));
    }

    private static int ToRgbColor(int red, int green, int blue)
    {
        red = Math.Max(0, Math.Min(255, red));
        green = Math.Max(0, Math.Min(255, green));
        blue = Math.Max(0, Math.Min(255, blue));
        return RgbColorFlag | (red << 16) | (green << 8) | blue;
    }

    /// <summary>
    /// RGB value (0xRRGGBB) of an entry of the xterm 256-colour palette above the 16 base colours:
    /// 16..231 is a 6x6x6 colour cube, 232..255 a grey ramp. Indices below 16 come from the user's palette.
    /// </summary>
    public static int Xterm256ToRgb(int index)
    {
        if (index < 16 || index > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        if (index >= 232)
        {
            int grey = 8 + (index - 232) * 10;
            return (grey << 16) | (grey << 8) | grey;
        }
        int cube = index - 16;
        return (CubeLevel(cube / 36) << 16) | (CubeLevel(cube / 6 % 6) << 8) | CubeLevel(cube % 6);
    }

    private static int CubeLevel(int step)
    {
        return (step == 0) ? 0 : (55 + step * 40);
    }
}
