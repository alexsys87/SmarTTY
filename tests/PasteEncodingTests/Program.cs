using System;
using System.Text;
using SmarTTY.Terminal;

// Regression harness for the check of pasted text against the encoding of a session (Terminal/PasteEncoding.cs):
// Unicode encodings are left alone, characters a single-byte code page lacks are found, typographic ones are replaced
// with ASCII, decomposed letters are composed.
// Run with: dotnet run --project tests/PasteEncodingTests
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

    private static void Main()
    {
        Encoding ascii = Encoding.ASCII;
        Encoding latin1 = Encoding.GetEncoding("iso-8859-1");
        Encoding utf8 = Encoding.UTF8;

        PasteEncodingResult result = PasteEncoding.Analyze("hello \u043f\u0440\u0438\u0432\u0435\u0442 \U0001F600", utf8);
        Check(result.IsLossless && result.Text.Contains("\U0001F600"), "UTF-8 holds everything");
        Check(PasteEncoding.Analyze("x", Encoding.Unicode).IsLossless, "UTF-16 holds everything");
        Check(PasteEncoding.IsUnicode(utf8) && !PasteEncoding.IsUnicode(ascii) && PasteEncoding.IsUnicode(null), "which encodings are Unicode");

        result = PasteEncoding.Analyze("plain text 123", ascii);
        Check(result.IsLossless && result.Text == "plain text 123", "ASCII text in ASCII");
        result = PasteEncoding.Analyze("", ascii);
        Check(result.IsLossless && result.Text == "", "empty text");
        result = PasteEncoding.Analyze(null, ascii);
        Check(result.IsLossless && result.Text == "", "null text");

        result = PasteEncoding.Analyze("\u043f\u0440\u0438", ascii);
        Check(!result.IsLossless && result.UnmappableCount == 3 && result.Unmappable.Count == 3, "Cyrillic is not ASCII");

        result = PasteEncoding.Analyze("\u043f\u043f\u043f", ascii);
        Check(result.UnmappableCount == 3 && result.Unmappable.Count == 1, "every character is listed once, all are counted");

        result = PasteEncoding.Analyze("a \U0001F600 b", ascii);
        Check(result.UnmappableCount == 1 && result.Unmappable[0] == "\U0001F600", "a surrogate pair is one character");

        result = PasteEncoding.Analyze("\u201chello\u201d \u2014 it\u2019s", ascii);
        Check(result.IsLossless, "curly quotes and a dash have an ASCII form");
        Equal(result.Text, "\"hello\" -- it's", "typographic characters are replaced");

        result = PasteEncoding.Analyze("caf\u00e9", latin1);
        Check(result.IsLossless && result.Text == "caf\u00e9", "a Latin-1 letter in Latin-1");

        result = PasteEncoding.Analyze("cafe\u0301", latin1);
        Check(result.IsLossless && result.Text == "caf\u00e9", "a decomposed letter is composed for a legacy encoding");

        result = PasteEncoding.Analyze("cafe\u0301", utf8);
        Check(result.Text == "cafe\u0301", "UTF-8 text is not normalized");

        result = PasteEncoding.Analyze("a\u201cb \u043f", ascii);
        Check(result.UnmappableCount == 1 && result.Text.StartsWith("a\"b "), "a replaced quote next to a missing letter");

        result = PasteEncoding.Analyze("bad \ud800 lone", ascii);
        Check(result.UnmappableCount == 1, "a lone surrogate is reported, not thrown");

        string described = PasteEncoding.Describe(new[] { "\u043f", "\U0001F600" }, 10);
        Check(described.Contains("U+043F") && described.Contains("U+1F600"), "the description holds the code points");
        Check(PasteEncoding.Describe(new[] { "a", "b", "c" }, 2).EndsWith("..."), "a long list is cut");
        Check(!PasteEncoding.Describe(new[] { "\u0001" }, 3).Contains("\u0001"), "a control character is not printed");

        Console.WriteLine($"All {_passed} checks passed.");
    }
}
