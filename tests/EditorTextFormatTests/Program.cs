using System;
using System.Text;
using SmarTTY.TextEditing;

// Regression harness for the search (plain, whole word, regular expression, replace), the encodings and the line ending
// detection of the remote file editor.
// Run with: dotnet run --project tests/EditorTextFormatTests
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

    private static void CheckEqual(string expected, string actual, string name)
    {
        if (expected != actual)
        {
            throw new Exception("FAIL: " + name + " (expected \"" + expected + "\", got \"" + actual + "\")");
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static string Find(TextMatcher matcher, string text, int from, int to, bool backward)
    {
        return matcher.Match(text, from, to, backward, allowEmpty: false, out int index, out int length) ? index + ":" + length : "none";
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static int Main()
    {
        PlainSearch();
        WholeWord();
        RegularExpressions();
        Replacing();
        Encodings();
        LineEndings();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " checks passed.");
        return 0;
    }

    private static void PlainSearch()
    {
        TextMatcher foo = TextMatcher.Literal("foo", ignoreCase: false);
        string text = "foo bar foo baz FOO";
        CheckEqual("0:3", Find(foo, text, 0, text.Length, false), "plain: first match");
        CheckEqual("8:3", Find(foo, text, 1, text.Length, false), "plain: next match from column 1");
        CheckEqual("none", Find(foo, text, 9, text.Length, false), "plain: case sensitive skips FOO");
        CheckEqual("8:3", Find(foo, text, 0, text.Length, true), "plain: last match when searching backward");
        CheckEqual("0:3", Find(foo, text, 0, 8, true), "plain: backward match must end before the limit");
        CheckEqual("none", Find(foo, text, 0, 2, true), "plain: backward, the limit cuts the only match");
        CheckEqual("16:3", Find(TextMatcher.Literal("foo", ignoreCase: true), text, 9, text.Length, false), "plain: ignore case finds FOO");
        CheckEqual("none", Find(foo, text, 0, 2, false), "plain: forward match must end inside the limit");
        // A match on the current selection must not be found again when going up from its start.
        TextMatcher a = TextMatcher.Literal("a", ignoreCase: false);
        CheckEqual("none", Find(a, "xay", 0, 1, true), "plain: one character, backward from its start");
        CheckEqual("1:1", Find(a, "xay", 0, 2, true), "plain: one character, backward from its end");
        CheckEqual("2:2", Find(TextMatcher.Literal("aa", false), "xaaay", 0, 5, true), "plain: overlapping occurrences are found backward");
        Check(Throws(() => TextMatcher.Literal("", false)), "plain: an empty query is refused");
    }

    private static void WholeWord()
    {
        TextMatcher word = TextMatcher.Create("cat", matchCase: true, wholeWord: true, useRegex: false);
        string text = "concat cat cats cat_x cat";
        CheckEqual("7:3", Find(word, text, 0, text.Length, false), "whole word: skips concat");
        CheckEqual("22:3", Find(word, text, 8, text.Length, false), "whole word: skips cats and cat_x");
        TextMatcher symbols = TextMatcher.Create("$x", matchCase: true, wholeWord: true, useRegex: false);
        CheckEqual("3:2", Find(symbols, "a  $x b", 0, 7, false), "whole word: a query that starts with a symbol");
        TextMatcher unicode = TextMatcher.Create("кот", matchCase: false, wholeWord: true, useRegex: false);
        CheckEqual("6:3", Find(unicode, "котик Кот", 0, 9, false), "whole word: letters of any alphabet, ignoring case");
    }

    private static void RegularExpressions()
    {
        TextMatcher number = TextMatcher.Create(@"\d+", matchCase: true, wholeWord: false, useRegex: true);
        string text = "a1 b22 c333";
        CheckEqual("1:1", Find(number, text, 0, text.Length, false), "regex: first match");
        CheckEqual("4:2", Find(number, text, 2, text.Length, false), "regex: next match");
        CheckEqual("8:3", Find(number, text, 0, text.Length, true), "regex: last match backward");
        CheckEqual("4:2", Find(number, text, 0, 7, true), "regex: backward, the limit is respected");
        CheckEqual("none", Find(number, text, 0, 1, false), "regex: forward, the limit is respected");
        TextMatcher anchor = TextMatcher.Create("^#", matchCase: true, wholeWord: false, useRegex: true);
        CheckEqual("0:1", Find(anchor, "# comment", 0, 9, false), "regex: ^ matches at the start of the line");
        CheckEqual("none", Find(anchor, "a # comment", 0, 11, false), "regex: ^ does not match inside the line");
        CheckEqual("none", Find(TextMatcher.Create("x*", true, false, true), "abc", 0, 3, false), "regex: an empty match is not a result");
        Check(Throws(() => TextMatcher.Create("(", true, false, true)), "regex: an invalid expression is refused");
        Check(Throws(() => TextMatcher.Create("[a-", true, false, true)), "regex: an unclosed class is refused");
        TextMatcher ignoreCase = TextMatcher.Create("HELLO", matchCase: false, wholeWord: true, useRegex: true);
        CheckEqual("3:5", Find(ignoreCase, "a  hello b", 0, 10, false), "regex: whole word and ignore case together");
        TextMatcher plainDots = TextMatcher.Create("a.c", matchCase: true, wholeWord: false, useRegex: false);
        CheckEqual("none", Find(plainDots, "abc", 0, 3, false), "plain: the dot is not a wildcard");
    }

    private static void Replacing()
    {
        TextMatcher foo = TextMatcher.Literal("foo", ignoreCase: false);
        string result = foo.ReplaceAll("foo bar foo", 0, 11, "x", out int count, out int newTo);
        CheckEqual("x bar x", result, "replace all: plain");
        Check(count == 2, "replace all: counts the occurrences");
        Check(newTo == 7, "replace all: tells where the end moved");
        result = foo.ReplaceAll("foo bar foo", 4, 11, "xy", out count, out newTo);
        CheckEqual("foo bar xy", result, "replace all: only inside the range");
        Check(count == 1 && newTo == 10, "replace all: range count and end");
        result = foo.ReplaceAll("foo bar foo", 0, 6, "-", out count, out newTo);
        CheckEqual("- bar foo", result, "replace all: a match that crosses the end of the range is left alone");
        result = foo.ReplaceAll("nothing", 0, 7, "x", out count, out newTo);
        Check(count == 0 && result == "nothing", "replace all: no match changes nothing");

        TextMatcher swap = TextMatcher.Create(@"(\w+)=(\w+)", true, false, true);
        result = swap.ReplaceAll("a=1 b=2", 0, 7, "$2=$1", out count, out newTo);
        CheckEqual("1=a 2=b", result, "replace all: regex groups");
        TextMatcher prefix = TextMatcher.Create("^", true, false, true);
        result = prefix.ReplaceAll("line", 0, 4, "# ", out count, out newTo);
        CheckEqual("# line", result, "replace all: an empty match inserts text");
        Check(count == 1, "replace all: one empty match at the start");
        TextMatcher dollar = TextMatcher.Literal("a", false);
        result = dollar.ReplaceAll("a", 0, 1, "$1", out count, out newTo);
        CheckEqual("$1", result, "replace all: plain search takes $1 literally");
        TextMatcher delete = TextMatcher.Create(@"\s+$", true, false, true);
        result = delete.ReplaceAll("text   ", 0, 7, "", out count, out newTo);
        CheckEqual("text", result, "replace all: trailing spaces removed by a regex");

        // The current match (a selection) is recognised exactly, and Expand then fits it.
        TextMatcher kv = TextMatcher.Create(@"(\w+)=(\d+)", true, false, true);
        Check(kv.MatchesExactly("x a=1 y", 2, 5), "exact match: the selection is a match");
        CheckEqual("1:a", kv.Expand("$2:$1"), "exact match: Expand uses the match of the selection");
        Check(!kv.MatchesExactly("x a=1 y", 2, 4), "exact match: a shorter selection is not a match");
        Check(!kv.MatchesExactly("x a=1 y", 3, 5), "exact match: a shifted selection is not a match");
    }

    private static void Encodings()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("hello привет");
        Check(TextFileEncodings.Detect(utf8) == TextFileEncodings.Utf8, "detect: valid UTF-8");
        Check(TextFileEncodings.Detect(new byte[0]) == TextFileEncodings.Utf8, "detect: an empty file is UTF-8");
        byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'#', (byte)'!' };
        Check(TextFileEncodings.Detect(bom) == TextFileEncodings.Utf8Bom, "detect: UTF-8 with BOM");
        CheckEqual("#!", TextFileEncodings.Decode(bom, TextFileEncodings.Utf8Bom), "decode: the BOM is dropped");
        Check(TextFileEncodings.Detect(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }) == TextFileEncodings.Utf16Le, "detect: UTF-16 LE");
        Check(TextFileEncodings.Detect(new byte[] { 0xFE, 0xFF, 0x00, 0x41 }) == TextFileEncodings.Utf16Be, "detect: UTF-16 BE");
        CheckEqual("A", TextFileEncodings.Decode(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, TextFileEncodings.Utf16Le), "decode: UTF-16 LE");

        byte[] windows1251 = Encoding.GetEncoding(1251).GetBytes("Привет");
        Check(TextFileEncodings.Detect(windows1251) == TextFileEncodings.Latin1, "detect: not UTF-8 falls back to a lossless encoding");
        string asLatin = TextFileEncodings.Decode(windows1251, TextFileEncodings.Latin1);
        byte[] back = TextFileEncodings.Encode(asLatin, TextFileEncodings.Latin1);
        Check(Convert.ToBase64String(back) == Convert.ToBase64String(windows1251), "latin1: an unknown file survives a read and a save unchanged");
        CheckEqual("Привет", TextFileEncodings.Decode(windows1251, TextFileEncodings.Windows1251), "reopen: Windows-1251");
        CheckEqual("Привет", TextFileEncodings.Decode(Encoding.GetEncoding(20866).GetBytes("Привет"), TextFileEncodings.Koi8R), "reopen: KOI8-R");
        CheckEqual("Привет", TextFileEncodings.Decode(Encoding.GetEncoding(866).GetBytes("Привет"), TextFileEncodings.Cp866), "reopen: CP866");

        byte[] saved = TextFileEncodings.Encode("#!/bin/sh", TextFileEncodings.Utf8Bom);
        Check(saved.Length == 12 && saved[0] == 0xEF && saved[1] == 0xBB && saved[2] == 0xBF, "encode: UTF-8 with BOM writes the mark");
        saved = TextFileEncodings.Encode("#!/bin/sh", TextFileEncodings.Utf8);
        Check(saved.Length == 9 && saved[0] == (byte)'#', "encode: UTF-8 writes no mark");
        saved = TextFileEncodings.Encode("A", TextFileEncodings.Utf16Be);
        Check(saved.Length == 4 && saved[0] == 0xFE && saved[1] == 0xFF && saved[2] == 0 && saved[3] == 0x41, "encode: UTF-16 BE");

        bool refused = false;
        try
        {
            TextFileEncodings.Encode("日本", TextFileEncodings.Windows1251);
        }
        catch (InvalidOperationException exception)
        {
            refused = exception.Message.Contains("Windows-1251");
        }
        Check(refused, "encode: a character the encoding cannot store is an error, not a \"?\"");
        CheckEqual("日本", TextFileEncodings.Decode(TextFileEncodings.Encode("日本", TextFileEncodings.Utf8), TextFileEncodings.Utf8), "round trip: UTF-8");
        Check(TextFileEncodings.All.Count == 8, "the status bar offers eight encodings");
    }

    private static void LineEndings()
    {
        Check(LineEndingDetector.Detect("") == LineEndingKind.None, "eol: empty text");
        Check(LineEndingDetector.Detect("one line") == LineEndingKind.None, "eol: no line break");
        Check(LineEndingDetector.Detect("a\nb\n") == LineEndingKind.Lf, "eol: LF");
        Check(LineEndingDetector.Detect("a\r\nb\r\n") == LineEndingKind.CrLf, "eol: CRLF");
        Check(LineEndingDetector.Detect("a\rb\r") == LineEndingKind.Cr, "eol: CR");
        Check(LineEndingDetector.Detect("a\r\nb\nc") == LineEndingKind.Mixed, "eol: CRLF and LF");
        Check(LineEndingDetector.Detect("a\r\nb\r") == LineEndingKind.Mixed, "eol: CRLF and CR");
        Check(LineEndingDetector.Detect("a\r\n") == LineEndingKind.CrLf, "eol: a single CRLF is not CR plus LF");
        CheckEqual("CRLF", LineEndingDetector.GetName(LineEndingKind.CrLf), "eol: name of CRLF");
        CheckEqual("LF", LineEndingDetector.GetName(LineEndingKind.None), "eol: a new file shows LF");
    }
}
