using System;
using System.Text;
using SmarTTY.Terminal;

// Regression harness for how the SSH terminal turns remote bytes into text:
//  * DEC Special Graphics (ESC ( 0 line drawing used by mc & co. without a UTF-8 locale),
//  * UTF-8 stream decoding with characters split between reads and a non-zero buffer offset.
// Run with: dotnet run --project tests/TerminalTextTests
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

    private static void Equal(string actual, string expected, string name)
    {
        Check(actual == expected, name + (actual == expected ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private static DecCharsets Graphics()
    {
        DecCharsets charsets = new DecCharsets();
        charsets.Designate('(', '0');
        return charsets;
    }

    private static void DecGraphicsChecks()
    {
        Equal(new DecCharsets().Translate("lqkxj mqj"), "lqkxj mqj", "ASCII by default: text is untouched");

        DecCharsets g0 = Graphics();
        Check(g0.GraphicsActive, "ESC ( 0 activates graphics in G0");
        Equal(g0.Translate("lqqk"), "\u250C\u2500\u2500\u2510", "top frame row  lqqk -> box");
        Equal(g0.Translate("x  x"), "\u2502  \u2502", "vertical bars, spaces untouched");
        Equal(g0.Translate("mqqj"), "\u2514\u2500\u2500\u2518", "bottom frame row  mqqj -> box");
        Equal(g0.Translate("tqqu"), "\u251C\u2500\u2500\u2524", "tees  tqqu");
        Equal(g0.Translate("wqqv"), "\u252C\u2500\u2500\u2534", "tees  wqqv");
        Equal(g0.Translate("n"), "\u253C", "cross");
        Equal(g0.Translate("aa"), "\u2592\u2592", "checkerboard fill (window background)");

        // the complete table for 0x5F..0x7E, in order
        StringBuilder all = new StringBuilder();
        for (char ch = '\u005F'; ch <= '\u007E'; ch++)
        {
            all.Append(ch);
        }
        Equal(g0.Translate(all.ToString()),
            "\u00A0\u25C6\u2592\u2409\u240C\u240D\u240A\u00B0\u00B1\u2591\u240B\u2518\u2510\u250C\u2514\u253C" +
            "\u23BA\u23BB\u2500\u23BC\u23BD\u251C\u2524\u2534\u252C\u2502\u2264\u2265\u03C0\u2260\u00A3\u00B7",
            "full 0x5F-0x7E table");

        Equal(g0.Translate("0123456789 ABCXYZ[\\]^ +,-./"), "0123456789 ABCXYZ[\\]^ +,-./", "characters outside 0x5F-0x7E are never translated");
        Equal(g0.Translate("\u043F\u0440\u0438\u0432\u0435\u0442 q"), "\u043F\u0440\u0438\u0432\u0435\u0442 \u2500", "non-ASCII text passes through, ASCII letters in range still map");
        Equal(g0.Translate(""), "", "empty string");
        Check(ReferenceEquals(new DecCharsets().Translate("abc"), "abc"), "nothing to translate returns the same string");

        g0.Designate('(', 'B');
        Check(!g0.GraphicsActive, "ESC ( B returns to ASCII");
        Equal(g0.Translate("lqk"), "lqk", "text is plain again after ESC ( B");

        DecCharsets alt = new DecCharsets();
        alt.Designate('(', '2');
        Check(alt.GraphicsActive, "ESC ( 2 (alternate special graphics) also selects graphics");
        alt.Designate('(', 'A');
        Check(!alt.GraphicsActive, "ESC ( A (UK) is not graphics");
        alt.Designate('(', '0');
        alt.Designate('(', 'U');
        Check(!alt.GraphicsActive, "ESC ( U is not graphics");
    }

    private static void ShiftChecks()
    {
        DecCharsets charsets = new DecCharsets();
        Check(charsets.Designate(')', '0'), "ESC ) 0 designates G1");
        Check(!charsets.GraphicsActive, "G1 graphics does not change the active G0 set");
        Equal(charsets.Translate("lqk"), "lqk", "text stays ASCII while shifted in");
        charsets.ShiftOut();
        Check(charsets.GraphicsActive, "SO makes G1 (graphics) active");
        Equal(charsets.Translate("lqk"), "\u250C\u2500\u2510", "text is translated while shifted out");
        charsets.ShiftIn();
        Check(!charsets.GraphicsActive, "SI returns to G0");
        Equal(charsets.Translate("lqk"), "lqk", "text plain after SI");

        DecCharsets both = Graphics();
        both.Designate(')', 'B');
        both.ShiftOut();
        Check(!both.GraphicsActive, "SO with ASCII in G1 is not graphics even though G0 is");
        both.ShiftIn();
        Check(both.GraphicsActive, "SI brings G0 graphics back");

        DecCharsets ignored = new DecCharsets();
        Check(!ignored.Designate('*', '0'), "G2 designation is ignored");
        Check(!ignored.Designate('+', '0'), "G3 designation is ignored");
        Check(!ignored.GraphicsActive, "G2/G3 designations change nothing");

        DecCharsets reset = Graphics();
        reset.Designate(')', '0');
        reset.ShiftOut();
        reset.Reset();
        Check(!reset.GraphicsActive, "Reset: back to ASCII, shifted in");
        reset.ShiftOut();
        Check(!reset.GraphicsActive, "Reset also cleared G1");
    }

    private static string DecodeInChunks(byte[] data, int[] cuts, int padding)
    {
        Utf8StreamDecoder decoder = new Utf8StreamDecoder();
        StringBuilder result = new StringBuilder();
        int position = 0;
        foreach (int cut in cuts)
        {
            int length = cut - position;
            byte[] buffer = new byte[padding + length + padding];
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = 0x41;
            }
            Array.Copy(data, position, buffer, padding, length);
            result.Append(decoder.Decode(buffer, padding, length));
            position = cut;
        }
        return result.ToString();
    }

    private static void Utf8DecoderChecks()
    {
        string text = "\u041F\u0440\u0438\u0432\u0435\u0442, \u043C\u0438\u0440! \u2713 \u65E5\u672C\u8A9E \U0001F600 ls";
        byte[] data = Encoding.UTF8.GetBytes(text);
        Equal(DecodeInChunks(data, new[] { data.Length }, 0), text, "whole buffer, offset 0");
        Equal(DecodeInChunks(data, new[] { data.Length }, 7), text, "whole buffer, non-zero offset");
        bool allSplits = true;
        for (int cut = 1; cut < data.Length; cut++)
        {
            allSplits &= DecodeInChunks(data, new[] { cut, data.Length }, 0) == text;
            allSplits &= DecodeInChunks(data, new[] { cut, data.Length }, 5) == text;
        }
        Check(allSplits, "a character split between two reads is rejoined at every position, with and without offset");

        Utf8StreamDecoder decoder = new Utf8StreamDecoder();
        StringBuilder bytewise = new StringBuilder();
        for (int i = 0; i < data.Length; i++)
        {
            bytewise.Append(decoder.Decode(new byte[] { 0x41, data[i], 0x41 }, 1, 1));
        }
        Equal(bytewise.ToString(), text, "byte-by-byte delivery at offset 1");
    }

    private static int Main()
    {
        DecGraphicsChecks();
        ShiftChecks();
        Utf8DecoderChecks();
        Console.WriteLine($"All {_passed} checks passed.");
        return 0;
    }
}
