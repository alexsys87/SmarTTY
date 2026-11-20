using System;
using System.Collections.Generic;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

// Checks of what stage 3C-1 took out of Controls/ReadonlyTextBrowser: CodeEditing/WrapMap.cs (where the lines break while word wrap is on, when the
// map is built again, the line and the row of a row index, the row of a column) and CodeEditing/CaretNavigator.cs (where the caret goes by a key,
// the column Up, Down and the pages keep, the word under the caret, what the view is asked at the ends of the text), with a fake view over a real
// document; what 3C-2 took: CodeEditing/TextSelection.cs (the state of the selection and its rules); and what 3C-3 took: CodeEditing/UndoHistory.cs
// (undo, redo, the saved state, single steps) with fake steps that log what they are asked, and CodeEditing/IndentEdits.cs (the indentation of the
// indent commands and of a join); and what 3D-1 took: CodeEditing/TextEditCommands.cs (the undoable edits on a real document, with a fake view
// that logs what it is told) and CodeEditing/TextTyping.cs (what typing and the edit keys ask of the editor). The same code is run in the real text
// view by tests/TextViewTests (its golden file).
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

    private static void Selection()
    {
        TextSelection selection = new TextSelection();
        Check(!selection.HasSelection, "no selection at first");
        Equal(P(selection.End(false, At(3, 4))), "3:4", "outside terminal mode the end is the caret");
        selection.TerminalEnd = At(5, 1);
        Equal(P(selection.End(true, At(3, 4))), "5:1", "in terminal mode it is its own end");
        Check(selection.SetActive(true), "switched on: changed");
        Check(!selection.SetActive(true), "on again: no change");
        Check(selection.SetActive(false), "switched off: changed");
        Check(!selection.SetActive(false), "off again: no change");
        Check(selection.MoveAnchor(At(1, 2)), "a new start: moved");
        Check(!selection.MoveAnchor(At(1, 2)), "the same start: not moved");
        Equal(P(selection.Anchor), "1:2", "the start");
        Check(selection.MoveTerminalEnd(At(2, 0)), "a new terminal end: moved");
        Check(!selection.MoveTerminalEnd(At(2, 0)), "the same: not moved");
        Equal(P(selection.TerminalEnd), "2:0", "the terminal end");
        Check(!selection.Clear(), "clearing no selection: false");
        Check(selection.StartIfNone(At(4, 4)), "a selection starts when there is none");
        Check(selection.HasSelection && P(selection.Anchor) == "4:4", "at the given place");
        Check(!selection.StartIfNone(At(0, 0)), "and not when there is one");
        Equal(P(selection.Anchor), "4:4", "which keeps its start");
        Check(selection.Clear() && !selection.HasSelection, "clearing a selection: true, and it is gone");
        Equal(P(selection.Anchor), "4:4", "the start is kept");

        string Lines(int anchorLine, int anchorColumn, int endLine, int endColumn, bool has = true)
        {
            TextSelection lines = new TextSelection { HasSelection = has, Anchor = At(anchorLine, anchorColumn) };
            lines.GetSelectedLines(At(endLine, endColumn), out int first, out int last);
            return first + ".." + last;
        }
        Equal(Lines(1, 2, 3, 4, has: false), "0..-1", "no selection: no lines");
        Equal(Lines(2, 2, 2, 2), "0..-1", "an empty selection: no lines");
        Equal(Lines(1, 2, 3, 4), "1..3", "from the start to the end");
        Equal(Lines(3, 4, 1, 2), "1..3", "backward the same");
        Equal(Lines(1, 0, 3, 0), "1..2", "an end at the start of a line does not take it");
        Equal(Lines(3, 0, 1, 0), "1..2", "nor a start there when the selection goes backward");
        Equal(Lines(0, 0, 1, 0), "0..0", "the first line alone");
        Equal(Lines(0, 3, 0, 0), "0..0", "a backward selection in the first line");

        string Range(int first, int last, int count)
        {
            bool any = TextSelection.ClampLineRange(ref first, ref last, count);
            return any ? first + ".." + last : "none";
        }
        Equal(Range(2, 4, 10), "2..4", "a range in the text");
        Equal(Range(4, 2, 10), "2..4", "a reversed range is put in order");
        Equal(Range(-3, 100, 10), "0..9", "a range past both ends is brought into the text");
        Equal(Range(5, 5, 10), "5..5", "one line");
        Equal(Range(-1, 3, 10), "0..3", "a range that starts one line before the text starts at its first line");
        Equal(Range(12, 15, 10), "none", "a range after the text: nothing");
        Equal(Range(0, 0, 0), "none", "an empty text: nothing");

        FakeView view = new FakeView("one", "", "three!");
        Equal(P(TextSelection.DocumentEnd(view.Text)), "2:6", "Select all ends at the end of the last line");
        Equal(P(TextSelection.DocumentEnd(new FakeView("only").Text)), "0:4", "of a text of one line too");
    }

    // An edit that logs what it is asked.
    private sealed class Step : IUndoStep
    {
        private readonly string _name;

        private readonly List<string> _log;

        public Step(string name, List<string> log)
        {
            _name = name;
            _log = log;
        }

        public void Execute() => _log.Add("do " + _name);

        public void Undo() => _log.Add("undo " + _name);
    }

    private static void Undo()
    {
        List<string> log = new List<string>();
        int changes = 0;
        UndoHistory history = null;
        history = new UndoHistory(() => { changes++; log.Add("modified " + (history.IsModified ? "T" : "F")); });
        string Take()
        {
            string text = string.Join(",", log);
            log.Clear();
            return text;
        }
        Check(!history.IsModified && !history.HasUndo && !history.HasRedo && history.CurrentVersion == null, "an empty history: saved, nothing to undo or redo");
        Check(!history.Undo() && !history.Redo(), "undo and redo of nothing do nothing");
        Equal(Take(), "", "and tell nothing");
        history.Execute(new Step("a", log));
        Equal(Take(), "do a,modified T", "an edit is made and the text is modified");
        Check(history.HasUndo && !history.HasRedo, "it can be undone");
        object afterA = history.CurrentVersion;
        Check(afterA != null, "the version is the edit");
        history.Execute(new Step("b", log));
        Equal(Take(), "do b", "a second edit, still modified: no news");
        Check(history.Undo(), "undo");
        Equal(Take(), "undo b", "undoes the newest edit");
        Check(history.HasRedo, "which can be redone");
        Check(ReferenceEquals(history.CurrentVersion, afterA), "the version is the edit before it");
        history.Undo();
        Equal(Take(), "undo a,modified F", "undoing back to the start: saved again");
        Check(!history.HasUndo, "nothing more to undo");
        history.Redo();
        Equal(Take(), "do a,modified T", "redo does the edit again");
        history.Execute(new Step("c", log));
        Equal(Take(), "do c", "an edit after an undo");
        Check(!history.HasRedo, "drops what could be redone");

        history.MarkSaved(history.CurrentVersion);
        Equal(Take(), "modified F", "marked saved");
        history.Undo();
        Equal(Take(), "undo c,modified T", "undone past the saved text: modified");
        history.Redo();
        Equal(Take(), "do c,modified F", "redone to it: saved");
        history.MarkSaved(afterA);
        Equal(Take(), "modified T", "saved at an older version: modified now");
        history.Undo();
        Equal(Take(), "undo c,modified F", "and saved when that version comes back");
        history.IsModified = true;
        Equal(Take(), "modified T", "set modified: the text was never saved like this");
        history.Redo();
        history.Undo();
        Equal(Take(), "do c,undo c", "so no undo or redo brings the saved state back");
        Check(history.IsModified, "still modified");
        history.IsModified = false;
        Equal(Take(), "modified F", "set saved: the text as it is now");
        history.IsModified = false;
        Equal(Take(), "", "set saved again: no news");

        Check(!history.InEdit, "no edit runs between edits");
        bool inEdit = false;
        history.Execute(new ActionStep(() => inEdit = history.InEdit));
        Check(inEdit, "an edit runs while it is made");
        history.Undo();
        log.Clear();
        history.ExecuteAsSingleStep(() =>
        {
            history.Execute(new Step("x", log));
            history.ExecuteAsSingleStep(() => history.Execute(new Step("y", log)));
            history.Execute(new Step("z", log));
            inEdit = history.InEdit;
        });
        Equal(Take(), "do x,do y,do z,modified T", "three edits as one step, one of them nested");
        Check(inEdit, "a single step is an edit");
        history.Undo();
        Equal(Take(), "undo z,undo y,undo x,modified F", "undone at once, the newest first");
        history.Redo();
        Equal(Take(), "do x,do y,do z,modified T", "redone at once, in order");
        history.ExecuteAsSingleStep(() => { });
        history.Undo();
        Equal(Take(), "undo z,undo y,undo x,modified F", "an empty single step is no step of its own");
        bool thrown = false;
        try
        {
            history.ExecuteAsSingleStep(() =>
            {
                history.Execute(new Step("w", log));
                throw new InvalidOperationException("broken");
            });
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }
        Check(thrown && !history.InEdit, "what a single step throws goes on, and the edit is over");
        Equal(Take(), "do w", "the edits made before it stay, recorded as one step, and the modified state is not looked at again (as the old code)");
        history.Undo();
        Check(!history.IsModified, "so the text counts as saved though it was changed");
        Equal(Take(), "undo w", "undone as one step");

        history.Execute(new Step("v", log));
        history.Undo();
        log.Clear();
        history.Reset();
        Check(!history.HasUndo && !history.HasRedo && !history.IsModified, "a new document: an empty history, saved");
        history.Execute(new Step("u", log));
        history.Undo();
        Equal(Take(), "do u,modified T,undo u,modified F", "its saved state is the empty history");
        history.IsModified = true;
        history.Reset();
        log.Clear();
        history.Execute(new Step("t", log));
        history.Undo();
        Equal(Take(), "do t,undo t", "a new document while modified: the empty history is not the saved text");
        Check(history.IsModified, "so it stays modified");
        thrown = false;
        try
        {
            new UndoHistory(null);
        }
        catch (ArgumentNullException)
        {
            thrown = true;
        }
        Check(thrown, "the history needs to tell the view");
    }

    private sealed class ActionStep : IUndoStep
    {
        private readonly Action _action;

        public ActionStep(Action action)
        {
            _action = action;
        }

        public void Execute() => _action();

        public void Undo() => _action();
    }

    private static void Indents()
    {
        Equal(IndentEdits.IndentUnit(false, 4), "\t", "a tab without spaces");
        Equal(IndentEdits.IndentUnit(true, 4), "    ", "the tab size in spaces");
        Equal(IndentEdits.IndentUnit(true, 0), " ", "at least one space");
        Equal(IndentEdits.IndentText("\t", 3), "\t\t\t", "three levels");
        Equal(IndentEdits.IndentText("  ", 0), "", "no level");
        Equal(IndentEdits.IndentText("  ", -2), "", "fewer than none");
        Equal(IndentEdits.UnindentWidth("\tx", 1, 4), 1, "a tab is a level");
        Equal(IndentEdits.UnindentWidth("        x", 1, 4), 4, "four spaces are a level");
        Equal(IndentEdits.UnindentWidth("        x", 2, 4), 8, "and two levels");
        Equal(IndentEdits.UnindentWidth("  x", 1, 4), 2, "fewer spaces are what there is");
        Equal(IndentEdits.UnindentWidth("x", 3, 4), 0, "no indentation: nothing");
        Equal(IndentEdits.UnindentWidth("\t  \tx", 3, 4), 4, "a tab, spaces and a tab");
        Equal(IndentEdits.UnindentWidth("    ", 2, 4), 4, "a line of blanks: never more than the line");
        Equal(IndentEdits.UnindentWidth("\t", 2, 4), 1, "a single tab is unindented too");
        Equal(IndentEdits.UnindentWidth("", 1, 4), 0, "an empty line");
        Equal(IndentEdits.UnindentWidth("     x", 1, 4), 4, "a level of spaces takes up to the tab size");
        Equal(IndentEdits.UnindentWidth("        x", 1, 2), 2, "with a tab size of 2");
        Equal(IndentEdits.SharedIndentLength("    ", "    second"), 4, "a join drops the indentation the lines share");
        Equal(IndentEdits.SharedIndentLength("  ", "    second"), 2, "no more than the first line's");
        Equal(IndentEdits.SharedIndentLength("\t", "    x"), 0, "a tab and spaces share nothing");
        Equal(IndentEdits.SharedIndentLength("    ", "  "), 2, "no more than the next line");
        Equal(IndentEdits.SharedIndentLength("", "  x"), 0, "no indentation: nothing");
    }

    // The text view as the edit commands and the typing see it: a real document, a caret and a selection, and a log of what it is told.
    private sealed class EditView : ITextEditView, ITextTypingHost
    {
        public readonly List<string> Log = new List<string>();

        public EditableTextDocument Text;

        public TextDocument.TextPosition Caret;

        public bool Selected;

        public TextDocument.TextPosition Anchor;

        public bool Overwrite;

        public bool Spaces;

        public int Tabs = 4;

        public bool Editable = true;

        public bool CopyLine = true;

        public string Indentation = "";

        public TextDocument.TextPosition Moved = new TextDocument.TextPosition(-1, -1);

        public Func<char, string> HookText;

        public TextDocument.TextPosition HookCaret = new TextDocument.TextPosition(-1, -1);

        public EditView(params string[] lines)
        {
            Text = new EditableTextDocument(lines, TextDocument.LineEnding.Lf);
        }

        public EditableTextDocument Document => Text;

        public TextDocument.TextPosition CaretPosition => Caret;

        public bool HasSelection
        {
            get => Selected;
            set
            {
                Log.Add("selection " + (value ? "on" : "off"));
                Selected = value;
            }
        }

        public TextDocument.TextPosition SelectionStart => Anchor;

        public TextDocument.TextPosition SelectionEnd => Caret;

        public bool OverwriteMode => Overwrite;

        public bool InsertSpaces => Spaces;

        public int TabSize => Tabs;

        public bool IsEditable => Editable;

        public bool CopyLineWhenNoSelection => CopyLine;

        public void GetSelectedLines(out int first, out int last)
        {
            if (!Selected || Anchor == Caret)
            {
                first = 0;
                last = -1;
                return;
            }
            TextDocument.TextPosition start = Anchor < Caret ? Anchor : Caret;
            TextDocument.TextPosition end = Anchor < Caret ? Caret : Anchor;
            first = start.Line;
            last = end.LastFullLine;
        }

        public void SetSelection(bool select, TextDocument.TextPosition start)
        {
            Log.Add("set selection " + (select ? "on " : "off ") + P(start));
            Selected = select;
            Anchor = start;
        }

        public void SetCaret(TextDocument.TextPosition position)
        {
            Log.Add("caret " + P(position));
            Caret = position;
        }

        public void SetCaretColumn(int column)
        {
            Log.Add("caret column " + column);
            Caret = new TextDocument.TextPosition(column, Caret.Line);
        }

        public void OnLineChanged(int line, int column, int addedLength, int removedLength) => Log.Add("line " + line + "," + column + "," + addedLength + "," + removedLength);

        public void OnRangeChanged(TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd) => Log.Add("range " + P(start) + "," + P(oldEnd) + "," + P(newEnd));

        public void InvalidateLine(int line) => Log.Add("invalidate " + line);

        public void UpdateLayout() => Log.Add("layout");

        public void Redraw() => Log.Add("redraw");

        public bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

        public string GetIndentation(int line) => Indentation;

        public int IndexToColumn(int line, int index) => Text.IndexToColumn(line, index, Tabs);

        public TextDocument.TextPosition GetMovedPosition(CaretMove move)
        {
            Log.Add("moved " + move);
            return Moved;
        }

        public void ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text) => Log.Add("replace " + P(start) + " " + P(end) + (select ? " select" : "") + " '" + text.Replace("\n", "\\n") + "'");

        public void Indent(int levels) => Log.Add("indent " + levels);

        public void UpdateMaxLineWidth(int line) => Log.Add("width " + line);

        public void OnCharTyping(char ch, ref string text, ref TextDocument.TextPosition textPosition)
        {
            Log.Add("typing " + ch);
            if (HookText != null)
            {
                text = HookText(ch);
                textPosition = HookCaret;
            }
        }

        public void OnCharTyped(char ch, TextDocument.TextPosition textPosition) => Log.Add("typed " + ch + "@" + P(textPosition));

        public void CopySelection(bool select) => Log.Add("copy " + select);

        public void Paste() => Log.Add("paste");

        public void Cut() => Log.Add("cut");

        public void Undo() => Log.Add("undo");

        public void Redo() => Log.Add("redo");

        public string Take()
        {
            string text = string.Join("; ", Log);
            Log.Clear();
            return text;
        }

        public string All => Text.GetText().Replace("\n", "|");
    }

    private static void EditCommands()
    {
        EditView view = new EditView("alpha beta", "gamma", "delta");
        view.Caret = At(0, 2);
        view.Selected = true;
        view.Anchor = At(0, 0);
        ReplaceInLineCommand inLine = new ReplaceInLineCommand(view, 0, 6, 4, "BETA!");
        inLine.Execute();
        Equal(view.All, "alpha BETA!|gamma|delta", "a part of a line is replaced");
        Equal(view.Take(), "line 0,6,4,5; selection off; caret column 11; invalidate 0; layout", "the view is told the line, the selection goes, the caret goes after the new text, the line is drawn");
        inLine.Undo();
        Equal(view.All, "alpha beta|gamma|delta", "undone");
        Equal(view.Take(), "line 0,6,5,4; set selection on 0:0; caret 0:2", "the selection and the caret come back as they were");
        inLine.Execute();
        Equal(view.All, "alpha BETA!|gamma|delta", "redone");
        view.Take();

        view = new EditView("one", "two", "three");
        view.Caret = At(2, 1);
        view.Selected = true;
        view.Anchor = At(0, 1);
        ReplaceRangeCommand range = new ReplaceRangeCommand(view, At(0, 1), At(2, 1), "X\nY", true);
        range.Execute();
        Equal(view.All, "oX|Yhree", "a range across lines is replaced, with a line break in the new text");
        Equal(view.Take(), "caret 0:1; selection off; range 0:1,2:1,1:1; layout; caret 1:1; redraw", "the range goes (the caret to its start, the selection off), the view is told, the caret goes after the new text");
        range.Undo();
        Equal(view.All, "one|two|three", "undone: the removed text comes back");
        Equal(view.Take(), "caret 0:1; selection off; range 0:1,1:1,2:1; set selection off 0:1; caret 2:1", "and no selection comes back: a range replacement drops it");
        ReplaceRangeCommand insert = new ReplaceRangeCommand(view, At(1, 3), At(1, 3), "!", true);
        insert.Execute();
        Equal(view.All, "one|two!|three", "an insertion at a point removes nothing");
        insert.Undo();
        Equal(view.All, "one|two|three", "and its undo removes the inserted text");
        view.Take();
        ReplaceRangeCommand delete = new ReplaceRangeCommand(view, At(0, 2), At(1, 1), "", true);
        delete.Execute();
        Equal(view.All, "onwo|three", "a deletion across a line break");
        delete.Undo();
        Equal(view.All, "one|two|three", "undone");
        EditView crlf = new EditView();
        crlf.Text = new EditableTextDocument(new[] { "a", "b", "c" }, TextDocument.LineEnding.Lf);
        ReplaceRangeCommand lines = new ReplaceRangeCommand(crlf, At(0, 1), At(2, 0), "", true);
        lines.Execute();
        lines.Undo();
        Equal(crlf.Text.GetText(), "a\nb\nc", "an undone deletion of lines keeps the line breaks of the file");

        view = new EditView("first", "  second", "", "third");
        view.Caret = At(3, 0);
        view.Selected = true;
        view.Anchor = At(0, 2);
        IndentCommand indent = new IndentCommand(view, 1);
        indent.Execute();
        Equal(view.All, "\tfirst|\t  second||third", "the lines of the selection are indented by a tab (not the blank line, not the line the selection ends at the start of)");
        Equal(view.Take(), "line 0,0,0,1; invalidate 0; layout; line 1,0,0,1; invalidate 1; layout", "each indented line is told and drawn");
        indent.Undo();
        Equal(view.All, "first|  second||third", "undone: exactly the indentation that was added");
        Equal(view.Take(), "line 0,0,1,0; invalidate 0; line 1,0,1,0; invalidate 1; layout; set selection on 0:2; caret 3:0", "and the selection comes back");
        view.Selected = false;
        view.Caret = At(1, 4);
        view.Spaces = true;
        IndentCommand twice = new IndentCommand(view, 2);
        twice.Execute();
        Equal(view.All, "first|          second||third", "without a selection the caret line, by two levels of spaces");
        twice.Undo();
        Equal(view.All, "first|  second||third", "undone");
        IndentCommand unindent = new IndentCommand(view, -1);
        unindent.Execute();
        Equal(view.All, "first|second||third", "unindent removes what there is of a level");
        view.Take();
        unindent.Undo();
        Equal(view.All, "first|  second||third", "undone: what was removed comes back");
        Equal(view.Take(), "line 1,0,0,2; set selection off 0:2; caret 1:4", "told");
        EditView deep = new EditView("\t\t\tx");
        IndentCommand two = new IndentCommand(deep, -2);
        two.Execute();
        Equal(deep.All, "\tx", "unindent by two levels");
        two.Undo();
        Equal(deep.All, "\t\t\tx", "undone");
        IndentCommand none = new IndentCommand(view, 0);
        none.Execute();
        Equal(view.Take(), "", "an indent by no level does nothing");
        none.Undo();
        Equal(view.Take(), "set selection off 0:2; caret 1:4", "and its undo only puts the caret back");
    }

    private static void Typing()
    {
        EditView view = new EditView("abc", "    ind", "tail");
        TextTyping typing = new TextTyping(view);
        view.Caret = At(0, 1);
        typing.TypeCharacter('x', false);
        Equal(view.Take(), "typing x; replace 0:1 0:0 select 'x'; typed x@0:1; width 0", "a character is put at the caret");
        view.Selected = true;
        view.Anchor = At(0, 0);
        view.Caret = At(0, 2);
        typing.TypeCharacter('y', false);
        Equal(view.Take(), "typing y; replace 0:2 0:0 'y'; typed y@0:2; width 0", "over a selection it replaces it");
        view.Selected = false;
        typing.TypeCharacter('\u0001', false);
        Equal(view.Take(), "", "a control character does nothing");
        view.Overwrite = true;
        view.Caret = At(0, 1);
        typing.TypeCharacter('z', false);
        Equal(view.Take(), "replace 0:1 0:2 'z'; width 0", "overwrite replaces the next character, without the hooks");
        view.Caret = At(0, 3);
        typing.TypeCharacter('z', false);
        Equal(view.Take(), "typing z; replace 0:3 0:0 select 'z'; typed z@0:3; width 0", "at the end of a line it inserts");
        view.Overwrite = false;
        view.HookText = ch => "()";
        view.HookCaret = At(0, 2);
        view.Caret = At(0, 1);
        typing.TypeCharacter('(', false);
        Equal(view.Take(), "typing (; replace 0:1 0:0 select '()'; caret 0:2; typed (@0:2; width 0", "the hook changes the text and the caret");
        view.HookText = ch => null;
        view.HookCaret = At(-1, -1);
        typing.TypeCharacter('#', false);
        Equal(view.Take(), "typing #; typed #@0:2; width 0", "a hook that types nothing");
        view.HookText = null;

        view.Caret = At(1, 7);
        view.Indentation = "    ";
        typing.TypeCharacter('\r', false);
        Equal(view.Take(), "replace 1:7 0:0 select '\\n    '", "Enter types the line break of the line and the indentation");
        view.Caret = At(1, 2);
        typing.TypeCharacter('\r', false);
        Equal(view.Take(), "replace 1:2 0:0 select '\\n  '", "with the caret inside the indentation only the part before it");
        view.Caret = At(2, 4);
        view.Indentation = "";
        typing.TypeCharacter('\r', false);
        Equal(view.Take(), "replace 2:4 0:0 select '\\n'", "a last line takes the line break of the line before it");
        EditView single = new EditView();
        single.Text = new EditableTextDocument(new[] { "one" }, TextDocument.LineEnding.Inherit);
        single.Text.DefaultLineEnding = TextDocument.LineEnding.Lf;
        single.Caret = At(0, 3);
        new TextTyping(single).TypeCharacter('\r', false);
        Equal(single.Take(), "replace 0:3 0:0 select '\\n'", "a line with an inherited line break (the only line of a file): the default line break of the document");

        view.Caret = At(0, 2);
        view.Moved = At(0, 1);
        typing.TypeCharacter('\b', false);
        Equal(view.Take(), "moved Left; replace 0:2 0:1 ''; width 0", "Backspace removes up to where Left goes");
        view.Spaces = true;
        view.Caret = At(1, 3);
        typing.TypeCharacter('\b', false);
        Equal(view.Take(), "replace 1:3 1:0 ''; width 1", "in space indentation back to the tab stop");
        view.Caret = At(1, 4);
        typing.TypeCharacter('\b', false);
        Equal(view.Take(), "replace 1:4 1:0 ''; width 1", "a whole level");
        view.Caret = At(1, 6);
        typing.TypeCharacter('\b', false);
        Equal(view.Take(), "moved Left; replace 1:6 0:1 ''; width 1", "after the indentation a character");
        view.Selected = true;
        view.Anchor = At(0, 0);
        typing.TypeCharacter('\b', false);
        Equal(view.Take(), "replace 1:6 0:0 ''; width 1", "over a selection it removes it");
        view.Selected = false;
        view.Caret = At(0, 1);
        typing.TypeCharacter('\t', false);
        Equal(view.Take(), "typing \t; replace 0:1 0:0 select '   '; typed \t@0:1; width 0", "Tab with spaces: to the next tab stop");
        view.Spaces = false;
        typing.TypeCharacter('\t', false);
        Equal(view.Take(), "typing \t; replace 0:1 0:0 select '\t'; typed \t@0:1; width 0", "Tab without spaces: a tab");
        typing.TypeCharacter('\t', true);
        Equal(view.Take(), "indent -1", "Shift+Tab unindents");
        view.Selected = true;
        view.Anchor = At(2, 0);
        typing.TypeCharacter('\t', false);
        Equal(view.Take(), "indent 1", "Tab over a selection of several lines indents");
        view.Anchor = At(0, 3);
        typing.TypeCharacter('\t', false);
        Equal(view.Take(), "typing \t; replace 0:1 0:3 '\t'; typed \t@0:1; width 0", "over a selection in one line it is typed");
        view.Spaces = true;
        Equal(typing.SpacesToNextTabStop(), "   ", "the spaces start at the start of the selection");
        view.Selected = false;

        view = new EditView("foo  bar.baz", "next");
        typing = new TextTyping(view);
        view.Caret = At(0, 0);
        Equal(typing.KeyDown(VirtualKey.Delete, true, false, false), TypingKeyResult.Handled, "Ctrl+Delete is handled");
        Equal(view.Take(), "replace 0:0 0:3 ''; width 0; width 0", "and removes the word (the width is looked at by the word and again by Delete, as before)");
        view.Caret = At(0, 3);
        typing.KeyDown(VirtualKey.Delete, true, false, false);
        Equal(view.Take(), "replace 0:3 0:8 ''; width 0; width 0", "and the blanks before the next word with it");
        view.Caret = At(0, 8);
        typing.KeyDown(VirtualKey.Delete, true, false, false);
        Equal(view.Take(), "replace 0:8 0:9 ''; width 0; width 0", "a mark is a word of its own");
        view.Caret = At(0, 12);
        typing.KeyDown(VirtualKey.Delete, true, false, false);
        Equal(view.Take(), "replace 0:12 1:0 ''; width 0; width 0", "at the end of a line the line break");
        view.Caret = At(1, 4);
        typing.KeyDown(VirtualKey.Delete, true, false, false);
        Equal(view.Take(), "width 1", "at the end of the text nothing but the width");
        view.Moved = At(1, 4);
        Equal(typing.KeyDown(VirtualKey.Delete, false, false, false), TypingKeyResult.None, "Delete is not marked handled");
        Equal(view.Take(), "moved Right; replace 1:4 1:4 ''; width 1", "and removes up to where Right goes");
        Equal(typing.KeyDown(VirtualKey.Back, true, false, false), TypingKeyResult.Handled | TypingKeyResult.SuppressKeyPress, "Ctrl+Backspace is handled and types nothing");
        Equal(view.Take(), "replace 1:4 1:0 ''; width 1", "and removes the word before the caret");
        view.Caret = At(0, 7);
        typing.KeyDown(VirtualKey.Back, true, false, false);
        Equal(view.Take(), "replace 0:7 0:5 ''; width 0", "inside a word the part before the caret");
        view.Caret = At(0, 5);
        typing.KeyDown(VirtualKey.Back, true, false, false);
        Equal(view.Take(), "replace 0:5 0:0 ''; width 0", "with the blanks after the word");
        view.Caret = At(1, 0);
        typing.KeyDown(VirtualKey.Back, true, false, false);
        Equal(view.Take(), "replace 1:0 0:12 ''; width 1", "at the start of a line the line break");
        view.Caret = At(0, 0);
        typing.KeyDown(VirtualKey.Back, true, false, false);
        Equal(view.Take(), "", "at the start of the text nothing");
        Equal(typing.KeyDown(VirtualKey.Back, true, true, false), TypingKeyResult.None, "Ctrl+Alt+Backspace (AltGr) is not a command");
        Equal(typing.KeyDown(VirtualKey.Back, true, false, true), TypingKeyResult.None, "nor Ctrl+Shift+Backspace");
        view.Selected = true;
        view.Anchor = At(0, 2);
        typing.KeyDown(VirtualKey.Delete, true, false, false);
        Equal(view.Take(), "replace 0:0 0:2 ''; width 0", "Ctrl+Delete over a selection removes it");
        typing.KeyDown(VirtualKey.Back, true, false, false);
        Equal(view.Take(), "replace 0:0 0:2 ''; width 0", "and so does Ctrl+Backspace");
        view.Selected = false;

        Check(typing.KeyDown(VirtualKey.C, true, false, false) == TypingKeyResult.Handled && view.Take() == "copy True", "Ctrl+C copies (the line without a selection)");
        view.CopyLine = false;
        Check(typing.KeyDown(VirtualKey.Insert, true, false, false) == TypingKeyResult.Handled && view.Take() == "copy False", "Ctrl+Insert copies too");
        Check(typing.KeyDown(VirtualKey.V, true, false, false) == TypingKeyResult.Handled && view.Take() == "paste", "Ctrl+V pastes");
        Check(typing.KeyDown(VirtualKey.Insert, false, false, true) == TypingKeyResult.Handled && view.Take() == "paste", "Shift+Insert pastes");
        Check(typing.KeyDown(VirtualKey.X, true, false, false) == TypingKeyResult.Handled && view.Take() == "cut", "Ctrl+X cuts");
        Check(typing.KeyDown(VirtualKey.Delete, false, false, true) == TypingKeyResult.Handled && view.Take() == "cut", "Shift+Delete cuts (and deletes nothing first)");
        Check(typing.KeyDown(VirtualKey.Z, true, false, false) == TypingKeyResult.Handled && view.Take() == "undo", "Ctrl+Z undoes");
        Check(typing.KeyDown(VirtualKey.Z, true, false, true) == TypingKeyResult.Handled && view.Take() == "redo", "Ctrl+Shift+Z redoes");
        Check(typing.KeyDown(VirtualKey.Y, true, false, false) == TypingKeyResult.Handled && view.Take() == "redo", "Ctrl+Y redoes");
        Check(typing.KeyDown(VirtualKey.Z, true, true, false) == TypingKeyResult.None && view.Take() == "", "Ctrl+Alt+Z (AltGr) is not a command");
        Check(typing.KeyDown(VirtualKey.A, false, false, false) == TypingKeyResult.None && view.Take() == "", "another key is not the typing's");
        view.Editable = false;
        Check(typing.KeyDown(VirtualKey.Z, true, false, false) == TypingKeyResult.None && view.Take() == "", "a text that cannot be edited takes no key");
        view.Editable = true;

        view = new EditView("l0", "l1", "l2", "l3");
        typing = new TextTyping(view);
        typing.RemoveLines(1, 2);
        Equal(view.Take(), "replace 1:0 3:0 ''", "lines before the last go with their line breaks");
        typing.RemoveLines(2, 3);
        Equal(view.Take(), "replace 1:2 3:2 ''", "the last lines go with the line break before them");
        typing.RemoveLines(1, 3);
        Equal(view.Take(), "replace 0:2 3:2 ''", "from the second line: the line break of the first");
        typing.RemoveLines(0, 3);
        Equal(view.Take(), "replace 0:0 3:2 ''", "all lines");
        typing.DeleteSelection();
        Equal(view.Take(), "", "DeleteSelection without a selection does nothing");
        view.Selected = true;
        view.Anchor = At(0, 1);
        view.Caret = At(1, 1);
        typing.DeleteSelection();
        Equal(view.Take(), "replace 1:1 0:1 ''; width 1", "DeleteSelection removes it");
        view.Editable = false;
        typing.DeleteSelection();
        Equal(view.Take(), "", "not in a text that cannot be edited");
        view.Editable = true;
        typing.InsertText("ins");
        Equal(view.Take(), "replace 1:1 0:1 'ins'", "InsertText over the selection");
        view.Selected = false;
        typing.InsertText("ins");
        Equal(view.Take(), "replace 1:1 0:1 select 'ins'", "InsertText at the caret");

        view = new EditView("alpha beta_2 gamma");
        typing = new TextTyping(view);
        List<string> words = new List<string>();
        foreach (int column in new[] { 0, 3, 5, 6, 12, 18 })
        {
            view.Caret = At(0, column);
            string word = typing.GetWordAtCaret(out int start, out int length);
            words.Add(column + ":" + (word ?? "null") + "/" + start + "+" + length);
        }
        Equal(string.Join(" ", words), "0:null/-1+0 3:alpha/0+5 5:alpha/0+5 6:beta_2/6+6 12:beta_2/6+6 18:gamma/13+5", "the word at the caret");
        view.Caret = At(0, 8);
        typing.ReplaceWordAtCaret("B");
        Equal(view.Take(), "replace 0:6 0:12 'B'", "the word at the caret is replaced");
        view.Caret = At(0, 0);
        typing.ReplaceWordAtCaret("B");
        Equal(view.Take(), "replace 0:0 0:0 select 'B'", "no word: the text is inserted");
        view = new EditView("a  b");
        typing = new TextTyping(view);
        view.Caret = At(0, 2);
        typing.ReplaceWordAtCaret("B");
        Equal(view.Take(), "replace 0:2 0:0 select 'B'", "between blanks: inserted");
        bool thrown = false;
        try
        {
            new TextTyping(null);
        }
        catch (ArgumentNullException)
        {
            thrown = true;
        }
        Check(thrown, "the typing needs its editor");
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
            Selection();
            Undo();
            Indents();
            EditCommands();
            Typing();
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
