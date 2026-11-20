using System;
using System.Collections.Generic;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

// Checks of what stage 3C-1 took out of Controls/ReadonlyTextBrowser: CodeEditing/WrapMap.cs (where the lines break while word wrap is on, when the
// map is built again, the line and the row of a row index, the row of a column) and CodeEditing/CaretNavigator.cs (where the caret goes by a key,
// the column Up, Down and the pages keep, the word under the caret, what the view is asked at the ends of the text), with a fake view over a real
// document. The same code is run in the real text view by tests/TextViewTests (its golden file).
// Run with: dotnet run --project tests/CaretNavigatorTests
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
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static TextDocument.TextPosition At(int line, int column)
    {
        return new TextDocument.TextPosition(column, line);
    }

    private static string P(TextDocument.TextPosition position)
    {
        return position.Line + ":" + position.Column;
    }

    // The text view as the models see it: a real document, the sizes a test gives, lines it hides, and a log of what it is asked.
    private sealed class FakeView : ICaretNavigationHost, IWrapMapSource
    {
        public EditableTextDocument Text;

        public int Tabs = 4;

        public int Rows = 3;

        public int Width = 10;

        public bool Wrapping;

        public int TextReads;

        public readonly List<string> Log = new List<string>();

        // Lines the view does not show (a fold): the line before and after skip them.
        public readonly HashSet<int> Hidden = new HashSet<int>();

        public WrapMap Map;

        public FakeView(params string[] lines)
        {
            Text = new EditableTextDocument(lines, TextDocument.LineEnding.Lf);
            Map = new WrapMap(this);
        }

        public TextDocument Document => Text;

        public int TabSize => Tabs;

        public int VisibleRows => Rows;

        public bool IsWrapping => Wrapping;

        public WrapMap Wrap => Map;

        public int LineCount => Text.LineCount;

        public int WrapWidth => Width;

        public string GetLineText(int line)
        {
            TextReads++;
            return Text.GetLineText(line);
        }

        public bool IsWordChar(char ch)
        {
            return char.IsLetterOrDigit(ch) || ch == '_';
        }

        public int GetPreviousRealLine(int line)
        {
            Log.Add("previous " + line);
            int previous = line - 1;
            while (previous > 0 && Hidden.Contains(previous))
            {
                previous--;
            }
            return previous;
        }

        public int GetNextRealLine(int line)
        {
            Log.Add("next " + line);
            int next = line + 1;
            while (next < Text.LineCount - 1 && Hidden.Contains(next))
            {
                next++;
            }
            return next;
        }

        public void OnMoveBeyondDocument(ref TextDocument.TextPosition textPosition, int direction)
        {
            Log.Add("beyond " + direction);
        }

        public string TakeLog()
        {
            string text = string.Join(",", Log);
            Log.Clear();
            return text;
        }
    }

    private static string Breaks(WrapMap map)
    {
        List<string> parts = new List<string>();
        for (int line = 0; line < map.Breaks.Length; line++)
        {
            parts.Add(map.Breaks[line] == null ? "-" : string.Join("/", map.Breaks[line]));
        }
        return string.Join(" ", parts) + " rows " + string.Join("/", map.FirstRows);
    }

    private static void WrapMapChecks()
    {
        FakeView view = new FakeView("short", "the quick brown fox", "\tx", "", "abcdefghijklmnopqrstuvwxy");
        WrapMap map = view.Map;
        Equal(map.Breaks.Length, 0, "nothing is built before the map is used");
        map.Ensure();
        Equal(Breaks(map), "- 10 - - 10/20 rows 0/1/3/4/5/8", "the breaks of every line and the first row of each, with the number of rows at the end");
        Equal(map.RowCount, 8, "eight rows");
        int reads = view.TextReads;
        map.Ensure();
        Equal(view.TextReads, reads, "nothing changed: the map is not built again");
        view.Width = 12;
        map.Ensure();
        Equal(Breaks(map), "- 10 - - 12/24 rows 0/1/3/4/5/8", "another width: built again");
        reads = view.TextReads;
        view.Tabs = 8;
        map.Ensure();
        Check(view.TextReads > reads, "another tab size: built again");
        Equal(Breaks(map), "- 10 - - 12/24 rows 0/1/3/4/5/8", "with the same breaks here (a tab of 8 columns and a letter still fit)");
        reads = view.TextReads;
        map.Invalidate();
        map.Ensure();
        Check(view.TextReads > reads, "invalidated: built again");
        view.Text = new EditableTextDocument(new[] { "short", "the quick brown fox", "\tx", "" }, TextDocument.LineEnding.Lf);
        Equal(map.RowCount, 5, "another number of lines: built again when the rows are counted");

        view = new FakeView("short", "the quick brown fox", "\tx", "", "abcdefghijklmnopqrstuvwxy");
        map = view.Map;
        List<string> lines = new List<string>();
        for (int row = -2; row <= 9; row++)
        {
            lines.Add(row + ">" + map.RowIndexToLine(row, out int segment) + "." + segment);
        }
        Equal(string.Join(" ", lines), "-2>0.0 -1>0.0 0>0.0 1>1.0 2>1.1 3>2.0 4>3.0 5>4.0 6>4.1 7>4.2 8>4.2 9>4.2", "the line and the row of a row index (before the text: the first; past it: the last row of the last line)");
        Equal(map.SegmentOf(1, 9) + "," + map.SegmentOf(1, 10) + "," + map.SegmentOf(4, 19) + "," + map.SegmentOf(4, 20) + "," + map.SegmentOf(4, 99), "0,1,1,2,2", "the row of a column: a break starts the next row");
        Equal(map.SegmentOf(-1, 5) + "," + map.SegmentOf(9, 5) + "," + map.SegmentOf(0, 50), "0,0,0", "a line outside the text or one of one row: the first row");
        Equal(map.SegmentStart(4, 0) + "," + map.SegmentStart(4, 1) + "," + map.SegmentStart(4, 2) + "," + map.SegmentStart(0, 3), "0,10,20,0", "where a row starts");
        Equal(map.SegmentEnd(4, 0) + "," + map.SegmentEnd(4, 1) + "," + map.SegmentEnd(4, 2) + "," + map.SegmentEnd(0, 0), "10,20," + int.MaxValue + "," + int.MaxValue, "where a row ends (the last row of a line has no end)");
        Equal(map.PositionToRowIndex(1, 3) + "," + map.PositionToRowIndex(1, 12) + "," + map.PositionToRowIndex(4, 25) + "," + map.PositionToRowIndex(-5, 3) + "," + map.PositionToRowIndex(40, 0), "1,2,7,0,5", "the row index of a column (a line outside the text is brought into it)");

        FakeView empty = new FakeView();
        Equal(empty.Map.RowCount, 0, "an empty text has no rows");
        Equal(empty.Map.RowIndexToLine(3, out int none), 0, "and every row index is its first line");
        Equal(none, 0, "at its first row");
        bool thrown = false;
        try
        {
            new WrapMap(null);
        }
        catch (ArgumentNullException)
        {
            thrown = true;
        }
        Check(thrown, "the map needs its view");
    }

    private static void Moves()
    {
        FakeView view = new FakeView("", "abc def_ghi  jkl", "\tindent\tx", "  two", "end.");
        CaretNavigator navigator = new CaretNavigator(view);
        Equal(P(navigator.Move(CaretMove.Left, false, At(1, 3))), "1:2", "Left");
        Equal(P(navigator.Move(CaretMove.Right, false, At(1, 3))), "1:4", "Right");
        Equal(P(navigator.Move(CaretMove.Left, false, At(2, 0))), "1:16", "Left at the start of a line: the end of the line before");
        Equal(view.TakeLog(), "previous 2", "which the view says");
        Equal(P(navigator.Move(CaretMove.Right, false, At(1, 16))), "2:0", "Right at the end of a line: the start of the line after");
        Equal(view.TakeLog(), "next 1", "which the view says");
        Equal(P(navigator.Move(CaretMove.Left, false, At(0, 0))), "0:0", "Left at the start of the text stays");
        Equal(view.TakeLog(), "beyond -1", "and tells the view it went beyond the start");
        Equal(P(navigator.Move(CaretMove.Right, false, At(4, 4))), "4:4", "Right at the end of the text stays");
        Equal(view.TakeLog(), "beyond 1", "and tells the view it went beyond the end");
        Equal(P(navigator.Move(CaretMove.WordLeft, false, At(1, 10))), "1:4", "Ctrl+Left goes to the start of the word");
        Equal(P(navigator.Move(CaretMove.WordLeft, false, At(1, 4))), "1:3", "from the start of a word to the blank before it");
        Equal(P(navigator.Move(CaretMove.WordRight, false, At(1, 4))), "1:11", "Ctrl+Right goes to the end of the word (the underscore is in it)");
        Equal(P(navigator.Move(CaretMove.WordRight, false, At(1, 11))), "1:12", "from the end of a word one blank on");
        Equal(P(navigator.Move(CaretMove.WordRight, false, At(1, 16))), "2:0", "at the end of a line: the next line");
        Equal(view.TakeLog(), "next 1", "the line after is the view's");
        Equal(P(navigator.Move(CaretMove.Home, false, At(3, 4))), "3:2", "Home: the start of the text of the line");
        Equal(P(navigator.Move(CaretMove.Home, false, At(3, 2))), "3:0", "Home there: the start of the line");
        Equal(P(navigator.Move(CaretMove.Home, false, At(2, 5))), "2:1", "a tab is indentation too");
        Equal(P(navigator.Move(CaretMove.Home, false, At(3, 0))), "3:2", "Home at the start of the line: the start of its text");
        Equal(P(navigator.Move(CaretMove.Home, true, At(3, 4))), "0:0", "Ctrl+Home: the start of the text");
        Equal(view.TakeLog(), "beyond " + int.MinValue, "which tells the view");
        Equal(P(navigator.Move(CaretMove.End, false, At(1, 2))), "1:16", "End: the end of the line");
        Equal(view.TakeLog(), "", "without telling the view");
        Equal(P(navigator.Move(CaretMove.End, true, At(1, 2))), "4:4", "Ctrl+End: the end of the text");
        Equal(view.TakeLog(), "beyond " + int.MaxValue, "which tells the view");
        Equal(P(navigator.Move(CaretMove.Left, false, At(-1, 0))), "0:0", "a position outside the text: the default");
        Equal(P(navigator.Move(CaretMove.Right, false, At(1, -1))), "0:0", "a column before the line too");
        Equal(P(navigator.Move((CaretMove)99, false, At(1, 3))), "1:3", "an unknown move leaves the position");
    }

    private static void KeptColumn()
    {
        FakeView view = new FakeView("abcdefghij", "ab", "\tabcdef", "abcdefghijkl", "x", "y", "z", "last line here");
        CaretNavigator navigator = new CaretNavigator(view);
        Equal(navigator.DesiredColumn, -1, "no column is kept at first");
        navigator.BeginMove(true, At(0, 8));
        Equal(navigator.DesiredColumn, 8, "Up, Down and the pages keep the column of the caret");
        TextDocument.TextPosition caret = navigator.Move(CaretMove.Down, false, At(0, 8));
        Equal(P(caret), "1:2", "Down to a short line: its end");
        navigator.BeginMove(true, caret);
        Equal(navigator.DesiredColumn, 8, "the kept column stays while they run");
        caret = navigator.Move(CaretMove.Down, false, caret);
        Equal(P(caret), "2:5", "Down to a line with a tab: the character at the column (the tab takes 4 columns)");
        caret = navigator.Move(CaretMove.Down, false, caret);
        Equal(P(caret), "3:8", "and the column again");
        caret = navigator.Move(CaretMove.Up, false, At(1, 2));
        Equal(P(caret), "0:8", "Up keeps it too");
        navigator.BeginMove(false, caret);
        Equal(navigator.DesiredColumn, -1, "another key drops it");
        navigator.BeginMove(true, At(2, 1));
        Equal(navigator.DesiredColumn, 4, "the column of a caret after a tab is in display columns");
        Equal(view.TakeLog(), "next 0,next 1,next 2,previous 1", "the line before and after are the view's");

        view.Hidden.Add(5);
        navigator.BeginMove(false, At(0, 0));
        navigator.BeginMove(true, At(4, 0));
        Equal(P(navigator.Move(CaretMove.Down, false, At(4, 0))), "6:0", "a line the view hides is skipped");
        Equal(view.TakeLog(), "next 4", "by the view");

        navigator.DesiredColumn = 3;
        Equal(P(navigator.Move(CaretMove.PageDown, false, At(0, 3))), "3:3", "PageDown goes down by the rows in view");
        Equal(P(navigator.Move(CaretMove.PageDown, false, At(6, 3))), "7:3", "and stops at the last line");
        Equal(view.TakeLog(), "beyond 3", "telling the view it went beyond the end");
        Equal(P(navigator.Move(CaretMove.PageUp, false, At(5, 3))), "2:0", "PageUp goes up by the rows in view (a column inside a tab is the tab)");
        Equal(P(navigator.Move(CaretMove.PageUp, false, At(1, 3))), "0:3", "and stops at the first line");
        Equal(view.TakeLog(), "beyond -3", "telling the view it went beyond the start");
        Equal(P(navigator.Move(CaretMove.Up, false, At(0, 3))), "0:3", "Up on the first line stays");
        Equal(view.TakeLog(), "beyond -1", "and tells the view");
        Equal(P(navigator.Move(CaretMove.Down, false, At(7, 3))), "7:3", "Down on the last line stays");
        Equal(view.TakeLog(), "beyond 1", "and tells the view");
    }

    private static void WrappedMoves()
    {
        FakeView view = new FakeView("short", "abcdefghijklmnopqrstuvwxy", "x", "the quick brown fox", "end") { Wrapping = true, Width = 10, Rows = 2 };
        CaretNavigator navigator = new CaretNavigator(view);
        navigator.BeginMove(true, At(0, 4));
        Equal(navigator.DesiredWrapOffset, 4, "the place of the kept column in its row");
        TextDocument.TextPosition caret = navigator.Move(CaretMove.Down, false, At(0, 4));
        Equal(P(caret), "1:4", "Down goes to the next row");
        caret = navigator.Move(CaretMove.Down, false, caret);
        Equal(P(caret), "1:14", "the next row of the same line, at the same place in the row");
        caret = navigator.Move(CaretMove.Down, false, caret);
        Equal(P(caret), "1:24", "and its last row");
        caret = navigator.Move(CaretMove.Down, false, caret);
        Equal(P(caret), "2:1", "a short line after it: its end");
        caret = navigator.Move(CaretMove.Down, false, caret);
        Equal(P(caret), "3:4", "and on to a longer one: the kept place in the row, not the place of the caret on the short line");
        Equal(view.TakeLog(), "", "the rows of the map, not the lines of the view");
        navigator.BeginMove(false, caret);
        navigator.BeginMove(true, At(1, 13));
        Equal(navigator.DesiredColumn + "/" + navigator.DesiredWrapOffset, "13/3", "a kept column in the second row: its place in the row");
        Equal(P(navigator.Move(CaretMove.Up, false, At(1, 13))), "1:3", "Up keeps the place in the row");
        navigator.BeginMove(false, At(0, 0));
        Equal(P(navigator.Move(CaretMove.Down, false, At(3, 9))), "3:19", "without a kept column the place of the caret in its row (the row ends at the end of the line)");
        Equal(P(navigator.Move(CaretMove.Up, false, At(3, 19))), "3:9", "and up again");
        Equal(P(navigator.Move(CaretMove.Up, false, At(0, 3))), "0:3", "Up on the first row stays");
        Equal(P(navigator.Move(CaretMove.Down, false, At(4, 1))), "4:1", "Down on the last row stays");
        Equal(view.TakeLog(), "", "and the view is not told");
        Equal(P(navigator.Move(CaretMove.Down, false, At(0, 5))), "1:5", "a column past the end of a row of the line after: kept in the row");
        view.Width = 6;
        view.Map.Invalidate();
        Equal(P(navigator.Move(CaretMove.Up, false, At(3, 9))), "3:3", "a place past the end of a shorter row above (\"the \" of a line that breaks after its blanks): the last column of that row");

        view = new FakeView("a", "b", "abcdefghijklmnopqrstuvwxy", "c", "d", "e") { Wrapping = true, Width = 10, Rows = 3 };
        navigator = new CaretNavigator(view);
        Equal(P(navigator.Move(CaretMove.PageDown, false, At(0, 0))), "2:10", "PageDown goes down by the rows in view (a row of a wrapped line)");
        Equal(P(navigator.Move(CaretMove.PageDown, false, At(2, 10))), "4:0", "and on");
        Equal(P(navigator.Move(CaretMove.PageDown, false, At(4, 0))), "5:0", "at the end the last row");
        Equal(P(navigator.Move(CaretMove.PageDown, false, At(5, 0))), "5:0", "on the last row it stays");
        Equal(P(navigator.Move(CaretMove.PageUp, false, At(5, 0))), "2:20", "PageUp goes up by the rows in view");
        Equal(P(navigator.Move(CaretMove.PageUp, false, At(1, 0))), "0:0", "at the start the first row");
        Equal(view.TakeLog(), "", "and the view is not told");
    }

    private static void WordBounds()
    {
        FakeView view = new FakeView("", "abc def_ghi, x", "  ");
        CaretNavigator navigator = new CaretNavigator(view);
        string Word(int line, int column)
        {
            return navigator.TryGetWordBounds(At(line, column), out TextDocument.TextPosition start, out TextDocument.TextPosition end) ? P(start) + "-" + P(end) : "none";
        }
        Equal(Word(0, 0), "none", "an empty line has no word");
        Equal(Word(1, 0), "1:0-1:3", "the word the caret is in front of");
        Equal(Word(1, 2), "1:0-1:3", "the word the caret is in");
        Equal(Word(1, 3), "1:0-1:3", "the word that ends at the caret (nothing to its right)");
        Equal(Word(1, 6), "1:4-1:11", "a word with an underscore");
        Equal(Word(1, 11), "1:4-1:11", "the word before a comma");
        Equal(Word(1, 12), "1:12-1:13", "not a word: the single character at the caret");
        Equal(Word(1, 14), "1:13-1:14", "the end of the line after a word: that word");
        Equal(Word(1, 40), "1:13-1:14", "a column past the end is the end");
        Equal(Word(2, 2), "2:1-2:2", "at the end of a blank line: the last character");
        Equal(Word(-1, 0), "none", "a position outside the text has no word");
        Equal(Word(1, -1), "none", "a column before the line neither");
    }

    private static void Arguments()
    {
        bool thrown = false;
        try
        {
            new CaretNavigator(null);
        }
        catch (ArgumentNullException)
        {
            thrown = true;
        }
        Check(thrown, "the navigator needs its view");
    }

    private static int Main()
    {
        try
        {
            WrapMapChecks();
            Moves();
            KeptColumn();
            WrappedMoves();
            WordBounds();
            Arguments();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message.StartsWith("FAIL", StringComparison.Ordinal) ? exception.Message : "FAIL: " + exception);
            return 1;
        }
        Console.WriteLine("CaretNavigatorTests: all " + _passed + " checks passed.");
        return 0;
    }
}
