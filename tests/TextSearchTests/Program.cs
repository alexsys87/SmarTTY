using System;
using System.Collections.Generic;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

// Checks of the search of a text view (CodeEditing/TextSearch.cs), the highlight of the pair of the bracket at the caret (BracketPair.cs) and the marks
// of the blanks (BlankMarks.cs): the next match forward and back, with a wrap, in a range, from a caret outside it; the column the view scrolls to;
// the matches of the highlight in a row (a wrapped row, a long line searched around the screen, the cap of the shown matches); the kind and the
// range of a pair; the mark of a character and the marks of a line. The answers are those of the code of ReadonlyTextBrowser and CodeEditorPP
// before it moved (the search run of tests/TextViewTests drives the real editor).
// Run with: dotnet run --project tests/TextSearchTests
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
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static TextDocument.TextPosition At(int line, int column) => new TextDocument.TextPosition(column, line);

    private static readonly TextDocument.TextPosition None = TextDocument.TextPosition.Invalid;

    private static EditableTextDocument Text(params string[] lines) => new EditableTextDocument(lines, TextDocument.LineEnding.Lf);

    private static string Find(TextDocument document, TextDocument.TextPosition from, TextMatcher matcher, bool back, bool wrap,
        TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd)
    {
        bool found = TextSearch.TryFind(document, from, matcher, back, wrap, scopeStart, scopeEnd, out int line, out int index, out int length);
        return found ? line + ":" + index + "+" + length : "-(" + line + ":" + index + "+" + length + ")";
    }

    private static void FindChecks()
    {
        EditableTextDocument text = Text("one two one", "two", "", "one");
        TextMatcher one = TextMatcher.Literal("one", true);
        Equal(Find(text, At(0, 0), one, false, false, None, None), "0:0+3", "forward from a match: that match");
        Equal(Find(text, At(0, 1), one, false, false, None, None), "0:8+3", "from inside it: the next one");
        Equal(Find(text, At(0, 9), one, false, false, None, None), "3:0+3", "on to the last line");
        Equal(Find(text, At(3, 1), one, false, false, None, None), "-(-1:-1+0)", "none past the last match without a wrap");
        Equal(Find(text, At(3, 1), one, false, true, None, None), "0:0+3", "the first with a wrap");
        Equal(Find(text, At(3, 0), one, true, false, None, None), "0:8+3", "back from the start of a line: the line before");
        Equal(Find(text, At(0, 3), one, true, false, None, None), "0:0+3", "back to one before the caret");
        Equal(Find(text, At(0, 0), one, true, false, None, None), "-(-1:-1+0)", "none back from the start without a wrap");
        Equal(Find(text, At(0, 0), one, true, true, None, None), "3:0+3", "the last with a wrap");
        Equal(Find(text, At(1, 0), TextMatcher.Literal("ONE", false), false, false, None, None), "-(-1:-1+0)", "the case of the text kept");
        Equal(Find(text, At(1, 0), TextMatcher.Literal("ONE", true), false, false, None, None), "3:0+3", "the case ignored");
        Equal(Find(text, At(0, 0), one, false, false, At(0, 2), At(1, 3)), "0:8+3", "a range: a match cut by its start is not one");
        Equal(Find(text, At(1, 0), one, false, false, At(0, 0), At(3, 2)), "-(-1:-1+0)", "nor one cut by its end");
        Equal(Find(text, At(1, 0), one, false, true, At(0, 2), At(3, 2)), "0:8+3", "a wrap goes round inside the range");
        Equal(Find(text, At(5, 0), one, true, false, At(0, 0), At(3, 3)), "3:0+3", "back from a caret below the range: from its end");
        Equal(Find(text, At(-1, 0), one, false, false, At(1, 0), At(3, 3)), "3:0+3", "forward from a caret above the range: from its start");
        Equal(Find(text, At(5, 0), one, false, false, None, None), "-(-1:-1+0)", "forward from a caret below the text: nothing");
        Equal(Find(text, At(-1, 0), one, true, false, None, None), "-(-1:-1+0)", "back from a caret above the text: nothing");
        Equal(Find(text, At(0, 0), one, false, false, At(0, 1), None), "0:0+3", "a range with an invalid end is no range: the start is not used");
        Equal(Find(text, At(0, 0), TextMatcher.Create("o.e", true, false, true), false, false, None, None), "0:0+3", "a regular expression");
        Equal(Find(text, At(0, 0), null, false, false, None, None), "-(-1:-1+0)", "no matcher");
        Equal(Find(text, At(0, 0), one, true, true, At(-2, 0), At(0, 5)), "0:0+3", "a range that starts above the text: from its first line");
        Equal(Find(text, At(3, 1), one, false, true, At(0, 0), At(9, 0)), "0:0+3", "a range that ends below the text: to its last line");
        Equal(Find(null, At(0, 0), one, false, false, None, None), "-(-1:-1+0)", "no text");
        Equal(Find(Text(""), At(0, 0), one, false, true, None, None), "-(-1:-1+0)", "an empty text");
        Equal(Find(text, At(0, 9), TextMatcher.Literal("one", true), true, false, At(0, 0), At(0, 11)), "0:0+3", "back from inside a match: a match must end before the caret, so the one before it");
    }

    private static void ScrollChecks()
    {
        Equal(TextSearch.ScrollColumnFor(0, 6, 6, 31), 0, "a match that ends in the first screen: the start of the line");
        Equal(TextSearch.ScrollColumnFor(25, 6, 100, 31), 0, "one that ends at the edge of it");
        Equal(TextSearch.ScrollColumnFor(26, 6, 100, 31), 14, "one past it: its middle in the middle");
        Equal(TextSearch.ScrollColumnFor(120, 6, 246, 31), 108, "far right: its middle in the middle");
        Equal(TextSearch.ScrollColumnFor(30, 6, 36, 31), 5, "near the end of its line: the end of the line at the right edge");
        Equal(TextSearch.ScrollColumnFor(300, 100, 400, 31), 300, "wider than the screen: its start");
        Equal(TextSearch.ScrollColumnFor(300, 31, 400, 31), 300, "as wide as the screen: its start");
        Equal(TextSearch.ScrollColumnFor(10, 30, 31, 31), 10, "a line as wide as the screen is not one past it: the middle of the match");
        Equal(TextSearch.ScrollColumnFor(40, 2, 60, 31), 29, "a line not past a screen after the match: its end at the edge");
    }

    private static string Spans(TextDocument document, int line, TextMatcher matcher, int tabSize, int rowFirst, int rowEnd, int lastColumn)
    {
        List<MatchSpan> spans = new List<MatchSpan> { new MatchSpan { StartColumn = 99, EndColumn = 99 } };
        TextSearch.RowMatches(document, line, matcher, tabSize, rowFirst, rowEnd, lastColumn, spans);
        List<string> items = new List<string>();
        foreach (MatchSpan span in spans)
        {
            items.Add(span.StartColumn + "-" + span.EndColumn);
        }
        return string.Join(" ", items);
    }

    private static void HighlightChecks()
    {
        EditableTextDocument text = Text("ab ab\tab", "xabx", "aaa", "\tab", "", "aab");
        TextMatcher ab = TextMatcher.Literal("ab", true);
        Equal(Spans(text, 0, ab, 4, 0, int.MaxValue, 32), "0-2 3-5 8-10", "the matches of a line in columns, a tab spread to its stop");
        Equal(Spans(text, 0, ab, 8, 0, int.MaxValue, 32), "0-2 3-5 8-10", "the same at tab size 8");
        Equal(Spans(text, 3, ab, 4, 0, int.MaxValue, 32), "4-6", "after a leading tab");
        Equal(Spans(text, 3, ab, 2, 0, int.MaxValue, 32), "2-4", "at tab size 2");
        Equal(Spans(text, 2, TextMatcher.Literal("aa", true), 4, 0, int.MaxValue, 32), "0-2", "matches do not overlap");
        Equal(Spans(text, 5, TextMatcher.Create("a+b", true, false, true), 4, 0, int.MaxValue, 32), "0-3", "a regular expression");
        Equal(Spans(text, 4, ab, 4, 0, int.MaxValue, 32), "", "an empty line has none (the list is cleared)");
        Equal(Spans(text, 0, ab, 4, 3, int.MaxValue, 32), "3-5 8-10", "a match left of the row is left out");
        Equal(Spans(text, 0, ab, 4, 4, int.MaxValue, 32), "3-5 8-10", "one that reaches into the row is kept");
        Equal(Spans(text, 0, ab, 4, 2, int.MaxValue, 32), "3-5 8-10", "one that ends where the row starts is left out");
        Equal(Spans(text, 0, ab, 4, 0, 4, 4), "0-2 3-4", "a wrapped row: a match at its end is cut there, one past it left out");
        Equal(Spans(text, 0, ab, 4, 4, 9, 9), "3-5 8-9", "the next row of the line");
        Equal(Spans(text, 0, ab, 4, 0, 3, 3), "0-2", "a match that starts at the end of the row is left out");

        string full = string.Concat(System.Linq.Enumerable.Repeat("ab", 3000));
        EditableTextDocument line = Text(full);
        TextMatcher eight = TextMatcher.Literal("abababab", true);
        string far = Spans(line, 0, eight, 4, 4001, int.MaxValue, 4033);
        Check(far.StartsWith("3994-4002 ", StringComparison.Ordinal), "a long line is searched from the screen less 2048 characters: the match across the left edge is there (" + far.Substring(0, 30) + ")");
        Check(far.EndsWith("6074-6082", StringComparison.Ordinal) == false && far.Contains("4058-4066"), "and on to 2048 past the right edge (" + far.Substring(far.Length - 20) + ")");
        string[] farItems = far.Split(' ');
        Equal(farItems[farItems.Length - 1], "5946-5954", "the 500th match from where the search starts is the last shown");
        Equal(Spans(line, 0, eight, 4, 0, int.MaxValue, 32).Split(' ').Length, 260, "from the start of a long line: up to 2048 past the right edge");
        Equal(Spans(line, 0, eight, 4, 0, int.MaxValue, 6000).Split(' ').Length, 500, "at most 500 matches are shown from where the search starts");
        Equal(Spans(line, 0, eight, 4, 0, int.MaxValue, 6000).Split(' ')[499], "3992-4000", "the last of them");
        EditableTextDocument tabbed = Text("\t" + full);
        Equal(Spans(tabbed, 0, eight, 4, 4001, int.MaxValue, 4033).Split(' ')[0], "3994-4002", "a long line after a tab: the columns from where the search starts count the tab");
        EditableTextDocument shortLine = Text(full.Substring(0, 4096));
        Equal(Spans(shortLine, 0, eight, 4, 4001, int.MaxValue, 4033), "", "a line of 4096 characters is searched whole from its start: the 500 shown end at column 4000, left of the row");
    }

    private static TokenCache Tokens(EditableTextDocument document)
    {
        TokenCache cache = new TokenCache();
        cache.SetDocument(document, SyntaxHighlighting.GetTokenizer(SyntaxHighlighting.Language.CLike));
        return cache;
    }

    private static string Pair(TokenCache cache, EditableTextDocument document, TextDocument.TextPosition caret)
    {
        BracketPairKind kind = BracketPair.Find(cache, document, caret, caret, out TextDocument.TextPosition start, out TextDocument.TextPosition end);
        return kind + (start.IsValid ? " " + start.Line + ":" + start.Column + "-" + end.Line + ":" + end.Column : "");
    }

    private static void PairChecks()
    {
        EditableTextDocument text = Text("f(a[1]) {", "  x = <y>; ) }", "(");
        TokenCache cache = Tokens(text);
        Equal(Pair(cache, text, At(0, 2)), "Paren 0:1-0:7", "after an opening parenthesis: the pair from it to the end of its partner");
        Equal(Pair(cache, text, At(0, 7)), "Paren 0:1-0:7", "after the closing one: the same range");
        Equal(Pair(cache, text, At(0, 4)), "Bracket 0:3-0:6", "a square bracket");
        Equal(Pair(cache, text, At(0, 6)), "Bracket 0:3-0:6", "its closing one");
        Equal(Pair(cache, text, At(0, 1)), "None", "before a bracket: nothing (the bracket must end at the caret)");
        Equal(Pair(cache, text, At(0, 3)), "None", "a letter");
        Equal(Pair(cache, text, At(0, 9)), "Unpaired 0:9-0:8", "a brace without its pair: its range from its end back to its start");
        Equal(Pair(cache, text, At(1, 12)), "Unpaired 1:12-1:11", "a closing parenthesis without one");
        Equal(Pair(cache, text, At(1, 14)), "Unpaired 1:14-1:13", "a closing brace without one");
        Equal(Pair(cache, text, At(2, 1)), "Unpaired 2:1-2:0", "an opening one at the end");
        Equal(Pair(cache, text, At(1, 9)), "None", "angle brackets are not pairs in C");
        Equal(Pair(cache, text, At(2, 0)), "None", "the start of a line");
        EditableTextDocument across = Text("x {", "abc", "}");
        Equal(Pair(Tokens(across), across, At(1, 3)), "None", "a brace of the line above at the same column is not at the caret");
        EditableTextDocument other = Text("{", "}");
        TokenCache otherCache = Tokens(other);
        Equal(Pair(otherCache, other, At(1, 1)), "Brace 0:0-1:1", "a pair over two lines");
        Equal(Pair(otherCache, other, At(0, 1)), "Brace 0:0-1:1", "from its opening brace");
        Equal(BracketPair.Find(otherCache, other, At(1, 1), At(1, 0), out _, out _), BracketPairKind.None, "the caret must be where the token ends");
        EditableTextDocument xml = Text("<a><b/></a>");
        TokenCache xmlCache = new TokenCache();
        xmlCache.SetDocument(xml, SyntaxHighlighting.GetTokenizer(SyntaxHighlighting.Language.Markup));
        string tag = Pair(xmlCache, xml, At(0, 3));
        Check(tag.StartsWith("Angle", StringComparison.Ordinal) || tag.StartsWith("Other", StringComparison.Ordinal) || tag == "None", "a tag of XML is an angle pair or another kind (" + tag + ")");
    }

    private static void MarkChecks()
    {
        Equal(BlankMarks.Marker(' '), "·", "a space: a middle dot");
        Equal(BlankMarks.Marker(' '), "°", "a no-break space: a ring");
        Equal(BlankMarks.Marker('\0'), "␀", "NUL: its control picture");
        Equal(BlankMarks.Marker('\u001B'), "␛", "ESC: its control picture");
        Equal(BlankMarks.Marker('\t'), "␉", "a tab alone: its control picture (a line draws an arrow)");
        Equal(BlankMarks.Marker('\u007F'), "␡", "DEL");
        Equal(BlankMarks.Marker('\u0085'), "�", "a C1 control character: a replacement mark");
        Equal(BlankMarks.Marker(' '), "·", "an em space: a dot");
        Equal(BlankMarks.Marker('　'), "·", "an ideographic space");
        Equal(BlankMarks.Marker('​'), "�", "a zero-width space: a replacement mark");
        Equal(BlankMarks.Marker('﻿'), "�", "a byte order mark");
        Equal(BlankMarks.Marker('­'), "�", "a soft hyphen");
        Equal(BlankMarks.Marker('a'), null, "a letter shows itself");
        Equal(BlankMarks.Marker('·'), null, "so does a middle dot");

        List<BlankMark> marks = new List<BlankMark>();
        string Marks(string text, int tabSize, int first, int last, bool lineBreak)
        {
            marks.Add(new BlankMark { Column = 99, Marker = "x" });
            BlankMarks.LineMarks(text, tabSize, first, last, lineBreak, marks);
            List<string> items = new List<string>();
            foreach (BlankMark mark in marks)
            {
                items.Add(mark.Column + mark.Marker);
            }
            return string.Join(" ", items);
        }
        Equal(Marks("a b\t\tc", 4, 0, 13, true), "1· 3→ 4→ 9¶", "a space, two tabs, the line break after the text");
        Equal(Marks("a b\t\tc", 4, 0, 13, false), "1· 3→ 4→", "no line break on the last line");
        Equal(Marks("\t \tx", 4, 0, 13, true), "0→ 4· 5→ 9¶", "a tab at the start, a space at a tab stop");
        Equal(Marks("a b\t\tc", 4, 2, 15, true), "3→ 4→ 9¶", "from a column on: those before it are left out");
        Equal(Marks("a b\t\tc", 4, 0, 3, true), "1· 3→", "up to a column: the rest and the line break are off screen");
        Equal(Marks("a b\t\tc", 4, 0, 8, true), "1· 3→ 4→", "a line break past the last column is left out");
        Equal(Marks("a b\t\tc", 4, 0, 9, true), "1· 3→ 4→ 9¶", "one at the last column is shown");
        Equal(Marks("", 4, 0, 10, true), "0¶", "an empty line: its line break");
        Equal(Marks("", 4, 1, 10, true), "", "scrolled past it: nothing");
        Equal(Marks("a b\u0001", 4, 0, 10, false), "1° 3␁", "the other marks in a line");
        Equal(Marks("\tx", 8, 0, 10, true), "0→ 9¶", "a tab of eight columns");
        Equal(Marks("ab\t", 4, 3, 3, true), "", "a tab that starts left of the first column is not shown, though it reaches into the screen");
        Equal(Marks("ab\t", 4, 2, 3, true), "2→", "one that starts at it is");
    }

    private static void Main()
    {
        FindChecks();
        ScrollChecks();
        HighlightChecks();
        PairChecks();
        MarkChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " text search checks passed.");
    }
}
