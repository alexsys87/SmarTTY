using System;
using System.Collections.Generic;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

// Checks of the models that the WPF text view of stage 3D-2 (GUI/TextView/WpfTextEditor) draws and handles the mouse with:
// CodeEditing/TextViewLayout.cs (the rows, the margin of the line numbers, what a row shows, the selected columns, the position under a point and
// the point of a position, the scroll that keeps the caret in view, the scroll bars, the wheel and the automatic scrolling of a drag) and
// CodeEditing/TextPointer.cs (what a click, a click with Shift, a double and a triple click and a drag select). The view is a fake with sizes in
// device pixels over a real document; the selection and the word bounds are the real TextSelection and CaretNavigator.
// Run with: dotnet run --project tests/TextViewLayoutTests
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

    // The view: a real document and the sizes the checks give (a cell of 10 x 18 pixels, scroll bars of 15).
    private sealed class FakeView : ITextLayoutView, ICaretNavigationHost
    {
        private EditableTextDocument _text;

        // A new document is told to the layout, as the view does.
        public EditableTextDocument Text
        {
            get => _text;
            set
            {
                _text = value;
                Layout?.OnDocumentChanged();
            }
        }

        public int Tabs = 4;

        public bool Wrapping;

        public bool Numbers;

        public int Width = 200;

        public int Height = 90;

        public bool VerticalBar;

        public bool HorizontalBar;

        public TextViewLayout Layout;

        public FakeView(params string[] lines)
        {
            Text = new EditableTextDocument(lines, TextDocument.LineEnding.Lf);
            Layout = new TextViewLayout(this);
            Layout.SetMetrics(10, 16);
        }

        public TextDocument Document => Text;

        public int TabSize => Tabs;

        public bool WordWrap => Wrapping;

        public bool ShowLineNumbers => Numbers;

        public int ClientWidth => Width;

        public int ClientHeight => Height;

        public int VerticalBarWidth => 15;

        public int HorizontalBarHeight => 15;

        public bool VerticalBarVisible => VerticalBar;

        public bool HorizontalBarVisible => HorizontalBar;

        public ITextFolds FoldSource;

        public ITextFolds Folds => FoldSource;

        int ICaretNavigationHost.VisibleRows => Layout.VisibleRows;

        bool ICaretNavigationHost.IsWrapping => Wrapping;

        WrapMap ICaretNavigationHost.Wrap => Layout.Wrap;

        bool ICaretNavigationHost.IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

        int ICaretNavigationHost.GetPreviousRealLine(int line) => Math.Max(0, line - 1);

        int ICaretNavigationHost.GetNextRealLine(int line) => Math.Min(Text.LineCount - 1, line + 1);

        void ICaretNavigationHost.OnMoveBeyondDocument(ref TextDocument.TextPosition textPosition, int direction)
        {
        }
    }

    private static string Rows(TextViewLayout layout)
    {
        List<string> rows = new List<string>();
        for (int row = 0; row < layout.RowCount; row++)
        {
            int line = layout.RowToLine(row, out int start, out int end);
            rows.Add(line + "[" + start + "," + end + ")" + (layout.IsFirstRowOfLine(row) ? "" : "+") + "'" + layout.RowText(row) + "'");
        }
        return string.Join(" ", rows);
    }

    private static string Selected(TextViewLayout layout, TextDocument.TextPosition start, TextDocument.TextPosition end)
    {
        List<string> rows = new List<string>();
        for (int row = 0; row < layout.RowCount; row++)
        {
            if (layout.SelectedColumns(row, start, end, out int from, out int to, out bool pastEnd))
            {
                rows.Add(row + ":" + from + "-" + to + (pastEnd ? "+" : ""));
            }
        }
        return string.Join(" ", rows);
    }

    private static string Point(TextViewLayout layout, TextDocument.TextPosition position)
    {
        layout.PositionToPoint(position, out int x, out int y);
        return x + "," + y;
    }

    private static void Rows()
    {
        FakeView view = new FakeView("alpha", "a\tb", "", "\tx\ty");
        TextViewLayout layout = view.Layout;
        Equal(layout.RowHeight, 18, "a row is the height of the text and two pixels");
        Equal(layout.TextHeight, 16, "the height of the text");
        Equal(layout.GutterWidth, 0, "no margin without line numbers");
        Equal(layout.TextLeft, 4, "the text starts after a blank");
        Equal(layout.RowCount, 4, "a row a line");
        Equal(Rows(layout), "0[0,5)'alpha' 1[0,5)'a   b' 2[0,0)'' 3[0,9)'    x   y'", "the rows show the lines with the tabs spread over their columns");
        view.Tabs = 8;
        Equal(layout.RowText(3), "        x       y", "another tab size");
        view.Tabs = 0;
        Equal(layout.RowText(1), "a b", "a tab size of 0 counts as 1");
        view.Tabs = 4;
        Equal(layout.LineColumns(3), 9, "the columns of a line");
        Equal(layout.MaxColumns, 9, "the widest line");
        Equal(layout.ContentWidth, 4 + 10 * 10, "the content: the margin, the widest line and a cell for the caret");
        view.Text.ReplaceInLine(0, 5, 0, "-is-a-long-line");
        Equal(layout.MaxColumns, 9, "the widest line is kept until a line is told");
        layout.UpdateMaxColumns(0);
        Equal(layout.MaxColumns, 20, "a line that grew");
        view.Text.ReplaceInLine(0, 0, 20, "");
        layout.UpdateMaxColumns(0);
        Equal(layout.MaxColumns, 20, "one that shrank leaves it");
        layout.UpdateMaxColumns(-1);
        Equal(layout.MaxColumns, 9, "-1 counts it again");
        view.Text.ReplaceInLine(0, 0, 0, "grown without being told....");
        layout.UpdateMaxColumns(99);
        Equal(layout.MaxColumns, 28, "so does a line past the text");
        Equal(layout.RowToLine(99, out int start, out int end) + "[" + start + "," + end + ")", "3[0,9)", "a row past the end is the last line");

        view.Numbers = true;
        Equal(layout.GutterWidth, 4 + 3 * 10, "the margin: two digits at least and a blank");
        Equal(layout.TextLeft, 38, "the text after the margin");
        view.Text = new EditableTextDocument(System.Linq.Enumerable.Repeat("", 100), TextDocument.LineEnding.Lf);
        Equal(layout.GutterWidth, 4 + 4 * 10, "three digits for a hundred lines");
        Equal(layout.LineNumberText(0) + " " + layout.LineNumberText(99) + " " + (layout.LineNumberText(100) ?? "none"), "1 100 none", "the numbers of the lines, none past the end");
        Check(new FakeView().Layout.RowCount == 0, "a text without lines has no rows");
    }

    private static void Wrapped()
    {
        FakeView view = new FakeView("aaaa bbbb cccc dddd eeee", "short", "\t\t\t\t\tz");
        view.Wrapping = true;
        TextViewLayout layout = view.Layout;
        Equal(layout.WrapWidth, (200 - 15 - 4) / 10, "the columns: the width less the scroll bar, which is always kept, and the margin");
        view.VerticalBar = true;
        Equal(layout.WrapWidth, 18, "the same with the bar shown");
        Equal(Rows(layout), "0[0,15)'aaaa bbbb cccc ' 0[15,24)+'dddd eeee' 1[0,5)'short' 2[0,16)'                ' 2[16,21)+'    z'", "the rows of wrapped lines");
        Equal(layout.RowCount, 5, "five rows");
        Equal(layout.PositionToRow(At(0, 14)) + " " + layout.PositionToRow(At(0, 15)) + " " + layout.PositionToRow(At(2, 5)), "0 1 4", "the row of a position: a break belongs to the row it starts");
        Equal(layout.LineNumberText(0) + "," + (layout.LineNumberText(1) ?? "-") + "," + layout.LineNumberText(2) + "," + layout.LineNumberText(3), "1,-,2,3", "only the first row of a line has its number");
        view.Width = 100;
        Equal(layout.WrapWidth, 8, "a narrower view wraps again");
        Equal(layout.RowCount, 9, "with more rows");
        view.Width = 10;
        Equal(layout.WrapWidth, 2, "at least two columns");
        view.Wrapping = false;
        Equal(layout.RowCount, 3, "not wrapped: a row a line");
        Check(layout.IsFirstRowOfLine(1), "and every row is the first of its line");
    }

    private static void Selection()
    {
        FakeView view = new FakeView("alpha", "a\tb", "", "gamma");
        TextViewLayout layout = view.Layout;
        Equal(Selected(layout, At(0, 2), At(0, 4)), "0:2-4", "within a line");
        Equal(Selected(layout, At(0, 4), At(0, 2)), "0:2-4", "in either order");
        Equal(Selected(layout, At(0, 2), At(0, 2)), "", "an empty selection covers nothing");
        Equal(Selected(layout, At(0, 3), At(3, 2)), "0:3-5+ 1:0-5+ 2:0-0+ 3:0-2", "across lines: the line breaks are selected, an empty line too");
        Equal(Selected(layout, At(1, 1), At(1, 2)), "1:1-4", "a tab takes its columns");
        Equal(Selected(layout, At(0, 5), At(1, 0)), "0:5-5+", "only the line break");
        view.Text = new EditableTextDocument(new[] { "aaaa bbbb cccc dddd eeee", "x" }, TextDocument.LineEnding.Lf);
        view.Wrapping = true;
        Equal(Selected(layout, At(0, 10), At(0, 20)), "0:10-15 1:0-5", "over the rows of a wrapped line");
        Equal(Selected(layout, At(0, 2), At(1, 1)), "0:2-15 1:0-9+ 2:0-1", "the line break only after the last row of a line");
        Equal(Selected(layout, At(0, 16), At(0, 20)), "1:1-5", "a selection in the second row only");
    }

    private static void Points()
    {
        FakeView view = new FakeView("alpha", "a\tb", "", "gamma delta", "x", "y", "z");
        TextViewLayout layout = view.Layout;
        Equal(P(layout.HitTest(4, 0)), "0:0", "the start of the text");
        Equal(P(layout.HitTest(0, 0)), "0:0", "left of the text: the start of the row");
        Equal(P(layout.HitTest(4 + 14, 0)), "0:1", "the nearer edge of a character: before its middle");
        Equal(P(layout.HitTest(4 + 15, 0)), "0:2", "from its middle on, after it");
        Equal(P(layout.HitTest(4 + 500, 0)), "0:5", "past the end of a line: its end");
        Equal(P(layout.HitTest(4 + 20, 18)), "1:1", "a tab: its start column");
        Equal(P(layout.HitTest(4 + 30, 18)), "1:1", "its middle: before it");
        Equal(P(layout.HitTest(4 + 40, 18)), "1:2", "its end: after it");
        Equal(P(layout.HitTest(4 + 20, 36)), "2:0", "an empty line");
        Equal(P(layout.HitTest(50, -40)), "0:5", "above the rows: the first row");
        Equal(P(layout.HitTest(50, 5000)), "6:1", "below them: the last row");
        Equal(P(layout.HitTest(50, 35)), "1:3", "the last pixel of a row is in it");
        layout.FirstRow = 3;
        Equal(P(layout.HitTest(4, 0)), "3:0", "the rows count from the first in view");
        layout.ScrollX = 20;
        Equal(P(layout.HitTest(4, 0)), "3:2", "and the columns from the horizontal scroll");
        Equal(Point(layout, At(3, 2)), "4,0", "the point of a position: the left edge of its cell and the top of its row");
        Equal(Point(layout, At(0, 0)), "-16,-54", "a position above the view and left of it");
        layout.ScrollX = 0;
        Equal(Point(layout, At(1, 2)), "44,-36", "after a tab");
        view.Numbers = true;
        Equal(Point(layout, At(3, 0)), "38,0", "after the margin");
        Equal(P(layout.HitTest(10, 0)), "3:0", "a point in the margin: the start of the row");
        view.Numbers = false;
        layout.FirstRow = 0;

        view.Text = new EditableTextDocument(new[] { "aaaa bbbb cccc dddd eeee", "x" }, TextDocument.LineEnding.Lf);
        view.Wrapping = true;
        layout.ScrollX = 30;
        Equal(P(layout.HitTest(4 + 500, 0)), "0:15", "the end of a row that wraps: the break, where the next row starts");
        Equal(P(layout.HitTest(4 + 20, 18)), "0:17", "the second row of a line starts at its break");
        Equal(P(layout.HitTest(4 + 500, 18)), "0:24", "its end is the end of the line");
        Equal(P(layout.HitTest(-30, 18)), "0:15", "left of the view (a drag): the start of that row, not a column of the row before");
        Equal(Point(layout, At(0, 17)), "24,18", "the point of a position in the second row; no horizontal scroll while wrapping");
        Equal(Point(layout, At(0, 15)), "4,18", "the break is drawn at the start of the next row");
        Equal(Point(layout, At(9, 0)), "4,36", "a line past the text is the last one");
    }

    private static void Scrolling()
    {
        string[] lines = new string[20];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = "line " + i + (i == 7 ? new string('-', 40) : "");
        }
        FakeView view = new FakeView(lines);
        TextViewLayout layout = view.Layout;
        Equal(layout.VisibleRows, 5, "the rows that fit whole (90 / 18)");
        view.HorizontalBar = true;
        Equal(layout.VisibleRows, 4, "fewer under a horizontal bar");
        Equal(layout.VisibleHeight, 75, "the height above it");
        view.VerticalBar = true;
        Equal(layout.VisibleWidth, 185, "the width left of a vertical bar");
        view.HorizontalBar = false;
        view.VerticalBar = false;
        view.Height = 5;
        Equal(layout.VisibleRows, 1, "at least one row");
        view.Height = 90;
        Equal(layout.ClampFirstRow(-3) + " " + layout.ClampFirstRow(7) + " " + layout.ClampFirstRow(99), "0 7 15", "the first row: not before the first, not past the last screen");
        Check(layout.EnsureVisible(At(10, 0)), "a position below the view scrolls it");
        Equal(layout.FirstRow, 6, "to show it in the last row");
        Check(!layout.EnsureVisible(At(8, 0)), "one in view does not");
        Check(layout.EnsureVisible(At(2, 0)), "one above");
        Equal(layout.FirstRow, 2, "is shown in the first row");
        layout.FirstRow = 40;
        layout.EnsureVisible(At(19, 0));
        Equal(layout.FirstRow, 15, "the first row is brought back into the text");
        Check(layout.EnsureVisible(At(7, 45)), "a position right of the view");
        Equal(layout.ScrollX, 45 * 10 + 10 - (200 - 4), "scrolls it so that its cell is at the right edge");
        Check(layout.EnsureVisible(At(7, 30)) == false, "one in view does not");
        Check(layout.EnsureVisible(At(7, 2)), "one left of the view");
        Equal(layout.ScrollX, 0, "keeps four columns before it, not less than the start");
        layout.ScrollX = 300;
        layout.EnsureVisible(At(7, 25));
        Equal(layout.ScrollX, 210, "four columns before it");
        Equal(layout.ClampScrollX(-5) + " " + layout.ClampScrollX(100) + " " + layout.ClampScrollX(1000), "0 100 274", "the horizontal scroll is brought into the content (474 wide, 200 in view)");
        view.Wrapping = true;
        Equal(layout.ClampScrollX(100), 0, "none while wrapping");
        layout.ScrollX = 50;
        layout.EnsureVisible(At(7, 40));
        Equal(layout.ScrollX, 0, "and the caret puts it back to 0");
    }

    private static string Bars(FakeView view)
    {
        view.Layout.ChooseScrollBars(out bool vertical, out bool horizontal);
        return (vertical ? "V" : "-") + (horizontal ? "H" : "-");
    }

    private static void Bars()
    {
        FakeView view = new FakeView("a", "b", "c");
        Equal(Bars(view), "--", "the text fits: no bars");
        view.Text = new EditableTextDocument(new[] { "a", "b", "c", "d", "e", "f" }, TextDocument.LineEnding.Lf);
        Equal(Bars(view), "V-", "more rows than fit: the vertical bar");
        view.Text = new EditableTextDocument(new[] { new string('x', 30) }, TextDocument.LineEnding.Lf);
        Equal(Bars(view), "-H", "a line wider than the view: the horizontal bar");
        view.Text = new EditableTextDocument(new[] { new string('x', 18) }, TextDocument.LineEnding.Lf);
        Equal(Bars(view), "--", "a line that just fits with its caret cell (4 + 19 x 10 = 194)");
        view.Text = new EditableTextDocument(new[] { new string('x', 18), "b", "c", "d", "e", "f" }, TextDocument.LineEnding.Lf);
        Equal(Bars(view), "VH", "the vertical bar takes the width the line needed");
        view.Text = new EditableTextDocument(new[] { new string('x', 30), "b", "c", "d", "e" }, TextDocument.LineEnding.Lf);
        Equal(Bars(view), "VH", "the horizontal bar takes the row the last line needed");
        view.Text = new EditableTextDocument(new[] { new string('x', 30), "b", "c", "d" }, TextDocument.LineEnding.Lf);
        Equal(Bars(view), "-H", "four rows still fit above it");
        view.Wrapping = true;
        Equal(Bars(view), "--", "wrapped: no horizontal bar");
        view.Text = new EditableTextDocument(new[] { new string('x', 90) }, TextDocument.LineEnding.Lf);
        Equal(Bars(view), "--", "a long line wrapped into five rows fits");
        view.Text = new EditableTextDocument(new[] { new string('x', 120) }, TextDocument.LineEnding.Lf);
        Equal(Bars(view), "V-", "into seven it does not");
    }

    private static void WheelAndDrag()
    {
        FakeView view = new FakeView(new string('x', 50), "b", "c", "d", "e", "f", "g", "h");
        TextViewLayout layout = view.Layout;
        Equal(layout.WheelRows(120), 3, "a notch is three rows");
        Equal(layout.WheelRows(-240), -6, "two notches back");
        Equal(layout.WheelRows(60), 0, "half a notch waits");
        Equal(layout.WheelRows(60), 3, "and makes a notch with the next half");
        Equal(layout.WheelRows(60), 0, "half again");
        Equal(layout.WheelRows(-120), -3, "a change of direction drops the rest");
        Equal(layout.WheelRows(0), 0, "no delta");
        string Step(int x, int y)
        {
            layout.AutoScrollStep(x, y, out int rows, out int pixels);
            return rows + "," + pixels;
        }
        Equal(Step(50, 40), "0,0", "inside the text: no scroll");
        Equal(Step(50, -1), "-1,0", "just above: a row up");
        Equal(Step(50, -40), "-3,0", "farther: more rows");
        Equal(Step(50, 89), "0,0", "the last pixel of the last whole row");
        Equal(Step(50, 90), "1,0", "below the whole rows: a row down");
        Equal(Step(50, 90 + 36), "3,0", "farther: more");
        Equal(Step(3, 40), "0,-10", "left of the text: a column left");
        Equal(Step(-25, 40), "0,-30", "farther: more columns (29 pixels: three)");
        Equal(Step(199, 40), "0,0", "the last pixel of the view");
        Equal(Step(200, 40), "0,10", "beyond it: a column right");
        view.VerticalBar = true;
        Equal(Step(190, 40), "0,10", "beyond the text left of a vertical bar");
        view.Wrapping = true;
        Equal(Step(-25, -1), "-1,0", "wrapped: rows only");
    }

    // ---- the pointer

    private static TextPointer Pointer(FakeView view, out TextSelection selection)
    {
        selection = new TextSelection();
        return new TextPointer(selection, new CaretNavigator(view), () => view.Text);
    }

    private static string S(TextSelection selection, TextDocument.TextPosition caret)
    {
        return (selection.HasSelection ? P(selection.Anchor) + ".." : "") + P(caret);
    }

    private static void Clicks()
    {
        FakeView view = new FakeView("alpha beta, gamma", "", "second line", "last");
        TextPointer pointer = Pointer(view, out TextSelection selection);
        Check(pointer.DragTo(At(0, 3)) == null, "no press: a move does nothing");
        TextDocument.TextPosition caret = pointer.Press(At(0, 3), 1, false, At(3, 0));
        Equal(S(selection, caret), "0:3", "a click puts the caret, no selection");
        Check(pointer.Dragging && pointer.Unit == TextPointerUnit.Character, "and starts a drag by characters");
        caret = pointer.DragTo(At(0, 8)).Value;
        Equal(S(selection, caret), "0:3..0:8", "a drag forward selects from the click");
        caret = pointer.DragTo(At(0, 1)).Value;
        Equal(S(selection, caret), "0:3..0:1", "back past it: from the click backwards");
        caret = pointer.DragTo(At(0, 3)).Value;
        Equal(S(selection, caret), "0:3", "back to it: no selection");
        caret = pointer.DragTo(At(2, 4)).Value;
        Equal(S(selection, caret), "0:3..2:4", "to another line");
        pointer.Release();
        Check(!pointer.Dragging && pointer.DragTo(At(0, 0)) == null, "released: a move does nothing");
        Equal(S(selection, caret), "0:3..2:4", "and the selection stays");

        caret = pointer.Press(At(3, 2), 1, true, caret);
        Equal(S(selection, caret), "0:3..3:2", "a click with Shift extends the selection from where it starts");
        caret = pointer.DragTo(At(0, 0)).Value;
        Equal(S(selection, caret), "0:3..0:0", "and a drag goes on from there");
        pointer.Release();
        selection.Clear();
        caret = pointer.Press(At(2, 6), 1, true, At(0, 6));
        Equal(S(selection, caret), "0:6..2:6", "without a selection, from the caret");
        caret = pointer.Press(At(0, 6), 1, true, caret);
        Equal(S(selection, caret), "0:6", "back to where it starts: nothing selected");
        pointer.Release();

        caret = pointer.Press(At(0, 7), 2, false, caret);
        Equal(S(selection, caret), "0:6..0:10", "a double click selects the word, the caret at its end");
        Check(pointer.Unit == TextPointerUnit.Word, "and drags by words");
        caret = pointer.DragTo(At(0, 14)).Value;
        Equal(S(selection, caret), "0:6..0:17", "forward: to the end of the word under the mouse");
        caret = pointer.DragTo(At(0, 8)).Value;
        Equal(S(selection, caret), "0:6..0:10", "within the first word: the word");
        caret = pointer.DragTo(At(0, 2)).Value;
        Equal(S(selection, caret), "0:10..0:0", "back: from its end to the start of the word under the mouse");
        caret = pointer.DragTo(At(1, 0)).Value;
        Equal(S(selection, caret), "0:6..1:0", "an empty line is a point");
        pointer.Release();
        caret = pointer.Press(At(0, 5), 2, false, caret);
        Equal(S(selection, caret), "0:0..0:5", "a double click on a blank right after a word selects the word (as a double click finds it)");
        caret = pointer.Press(At(0, 11), 2, false, caret);
        Equal(S(selection, caret), "0:11..0:12", "one after a mark selects the blank");
        caret = pointer.Press(At(0, 10), 2, false, caret);
        Equal(S(selection, caret), "0:6..0:10", "after a word: the word");
        caret = pointer.Press(At(1, 0), 2, false, caret);
        Equal(S(selection, caret), "1:0", "on an empty line: nothing selected");
        pointer.Release();

        caret = pointer.Press(At(2, 4), 3, false, caret);
        Equal(S(selection, caret), "2:0..3:0", "a triple click selects the line with its line break");
        Check(pointer.Unit == TextPointerUnit.Line, "and drags by lines");
        caret = pointer.DragTo(At(3, 1)).Value;
        Equal(S(selection, caret), "2:0..3:4", "down to the last line: to its end");
        caret = pointer.DragTo(At(0, 9)).Value;
        Equal(S(selection, caret), "3:0..0:0", "up: from the end of the first line to the start of the line under the mouse");
        pointer.Release();
        caret = pointer.Press(At(3, 2), 3, false, caret);
        Equal(S(selection, caret), "3:0..3:4", "the last line has no line break");
        caret = pointer.Press(At(0, 2), 5, false, caret);
        Equal(S(selection, caret), "0:0..1:0", "more clicks select lines too");
        caret = pointer.Press(At(2, 3), 1, false, caret, lineUnit: true);
        Equal(S(selection, caret), "2:0..3:0", "a click in the margin selects the line");
        Check(pointer.Unit == TextPointerUnit.Line, "and drags by lines");
        caret = pointer.Press(At(1, 0), 1, false, caret, lineUnit: true);
        Equal(S(selection, caret), "1:0..2:0", "an empty line in the margin");
        view.Text = new EditableTextDocument(new[] { "" }, TextDocument.LineEnding.Lf);
        caret = pointer.Press(At(0, 0), 3, false, caret);
        Equal(S(selection, caret), "0:0", "the only line, empty: nothing selected");

        foreach (Action make in new Action[] { () => new TextPointer(null, new CaretNavigator(view), () => view.Text), () => new TextPointer(new TextSelection(), null, () => view.Text), () => new TextPointer(new TextSelection(), new CaretNavigator(view), null), () => new TextViewLayout(null) })
        {
            bool thrown = false;
            try
            {
                make();
            }
            catch (ArgumentNullException)
            {
                thrown = true;
            }
            Check(thrown, "the models need what they work on");
        }
    }

    private static string Runs(TextViewLayout layout, int row, params TextRun[] lineRuns)
    {
        return string.Join(" ", layout.RowRuns(row, lineRuns));
    }

    private static TextRun R(int from, int to, int color) => new TextRun(from, to, color);

    private static void Colors()
    {
        FakeView view = new FakeView("int a = 1; // one", "a\tb = c;", "", "xy");
        TextViewLayout layout = view.Layout;
        Equal(Runs(layout, 0, R(0, 3, 1), R(8, 9, 4), R(11, 17, 3)), "0-3:1 3-8:-1 8-9:4 9-11:-1 11-17:3", "the runs of a row cover it, the gaps in the colour of the text");
        Equal(Runs(layout, 0), "0-17:-1", "no runs: the colour of the text");
        Equal(Runs(layout, 0, null), "0-17:-1", "a null list too");
        Equal(Runs(layout, 2, R(0, 0, 1)), "", "an empty row has none");
        Equal(Runs(layout, 1, R(0, 1, 5), R(1, 2, 2), R(2, 3, 5)), "0-1:5 1-4:2 4-5:5 5-10:-1", "a tab takes its columns");
        Equal(Runs(layout, 1, R(2, 3, 5)), "0-4:-1 4-5:5 5-10:-1", "a run after a tab starts after its columns");
        Equal(Runs(layout, 0, R(0, 3, 1), R(3, 5, 1)), "0-5:1 5-17:-1", "neighbours of one colour are joined");
        Equal(Runs(layout, 0, R(0, 4, -1), R(10, 12, -1)), "0-17:-1", "and with the colour of the text");
        Equal(Runs(layout, 0, R(0, 5, 1), R(3, 8, 2)), "0-5:1 5-8:2 8-17:-1", "a run that goes back over an earlier one is cut");
        Equal(Runs(layout, 0, R(5, 8, 1), R(2, 4, 2)), "0-5:-1 5-8:1 8-17:-1", "one that lies before is skipped");
        Equal(Runs(layout, 3, R(0, 99, 6)), "0-2:6", "a run past the end of the line is cut to it");
        Equal(Runs(layout, 0, R(4, 4, 1)), "0-17:-1", "an empty run adds nothing");
        view.Text = new EditableTextDocument(new[] { "aaaa bbbb cccc dddd eeee" }, TextDocument.LineEnding.Lf);
        view.Wrapping = true;
        Equal(Runs(layout, 0, R(0, 4, 1), R(10, 19, 2), R(20, 24, 3)), "0-4:1 4-10:-1 10-15:2", "a wrapped line: the runs of its first row");
        Equal(Runs(layout, 1, R(0, 4, 1), R(10, 19, 2), R(20, 24, 3)), "0-4:2 4-5:-1 5-9:3", "of its second row, from the start of the row");

        TextDocument.TextPosition A(int line, int column) => At(line, column);
        string Changed(TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd)
        {
            SyntaxColoring.ChangedLines(start, oldEnd, newEnd, out int first, out int removed, out int added);
            return first + "," + removed + "," + added;
        }
        Equal(Changed(A(3, 2), A(3, 5), A(3, 4)), "3,0,0", "an edit within a line: that line");
        Equal(Changed(A(3, 2), A(5, 1), A(4, 0)), "3,2,1", "across lines: the lines it took and put in");
        Equal(Changed(A(3, 0), A(3, 0), A(4, 0)), "2,0,1", "a line break put in at the start of a line: from the line before");
        Equal(Changed(A(0, 0), A(0, 0), A(1, 0)), "0,0,1", "not before the first line");
        Equal(Changed(A(3, 1), A(3, 1), A(4, 0)), "3,0,1", "a line break put in after the start: from that line");
        Equal(Changed(A(3, 0), A(3, 0), A(4, 2)), "3,0,1", "a line break and more: from that line");
        Equal(Changed(A(3, 0), A(3, 0), A(5, 0)), "3,0,2", "two line breaks: from that line");
        Equal(Changed(A(3, 0), A(4, 0), A(3, 0)), "2,1,0", "an empty line taken out: from the line before");
        Equal(Changed(A(0, 0), A(1, 0), A(0, 0)), "0,1,0", "not before the first line");
        Equal(Changed(A(3, 0), A(4, 1), A(3, 0)), "3,1,0", "more than the line taken out: from that line");
        Equal(Changed(A(3, 5), A(4, 0), A(3, 5)), "3,1,0", "the line break at the end of a line taken out: from that line");
        Equal(Changed(A(3, 0), A(5, 0), A(3, 0)), "3,2,0", "two lines taken out: from that line");
        Equal(Changed(A(3, 0), A(4, 0), A(3, 1)), "3,1,0", "a line replaced by a character: from that line");
        Equal(SyntaxColoring.ColorIndex(3, 8) + " " + SyntaxColoring.ColorIndex(7, 8) + " " + SyntaxColoring.ColorIndex(8, 8) + " " + SyntaxColoring.ColorIndex(0, 8) + " " + SyntaxColoring.ColorIndex(5, 0), "3 7 0 0 -1",
            "the colour of a token type: its own, the first past them, none without colours");
    }

    // Folds as a folding model gives them: a header hides some lines and shows its text, a label and the end of its block.
    private sealed class FakeFolds : ITextFolds
    {
        public readonly Dictionary<int, int> Hidden = new Dictionary<int, int>();

        public int GetHiddenLineCount(int line) => Hidden.TryGetValue(line, out int count) ? count : 0;

        public string GetFoldedText(int line, string lineText, out int labelStart, out int labelLength)
        {
            if (!Hidden.ContainsKey(line))
            {
                // What a line that is not folded gives back is no label: the layout must not use it.
                labelStart = 0;
                labelLength = 3;
                return null;
            }
            labelStart = lineText.Length + 1;
            labelLength = 5;
            return lineText + " {...} end";
        }
    }

    private static void Folds()
    {
        FakeView view = new FakeView("int f()", "{", "  a();", "}", "\tg();", "x");
        TextViewLayout layout = view.Layout;
        FakeFolds folds = new FakeFolds();
        layout.FoldMarginWidth = 12;
        Equal(layout.GutterWidth + " " + layout.IsInFoldMargin(2), "0 False", "no folds: no margin of the marks");
        view.FoldSource = folds;
        Equal(layout.GutterWidth + " " + layout.TextLeft + " " + layout.IsInFoldMargin(0) + " " + layout.IsInFoldMargin(11) + " " + layout.IsInFoldMargin(12), "12 16 True True False", "a view that folds has the margin of the marks");
        view.Numbers = true;
        Equal(layout.NumbersWidth + " " + layout.GutterWidth + " " + layout.IsInFoldMargin(33) + " " + layout.IsInFoldMargin(34) + " " + layout.IsInFoldMargin(45) + " " + layout.IsInFoldMargin(46), "34 46 False True True False", "after the numbers");
        view.Numbers = false;
        Equal(Rows(layout), "0[0,7)'int f()' 1[0,1)'{' 2[0,6)'  a();' 3[0,1)'}' 4[0,8)'    g();' 5[0,1)'x'", "nothing folded: a row a line");
        folds.Hidden[0] = 3;
        Equal(layout.RowCount, 6, "the rows are kept until they are made again");
        layout.InvalidateRows();
        Equal(Rows(layout), "0[0,17)'int f() {...} end' 4[0,8)'    g();' 5[0,1)'x'", "a folded header shows the text of its fold and hides its lines");
        Equal(layout.LineText(0) + "|" + layout.LineText(2), "int f() {...} end|  a();", "the text a line shows");
        Equal(layout.PositionToRow(At(0, 3)) + " " + layout.PositionToRow(At(2, 1)) + " " + layout.PositionToRow(At(3, 0)) + " " + layout.PositionToRow(At(4, 0)) + " " + layout.PositionToRow(At(5, 0)), "0 0 0 1 2", "a hidden line is in the row of its header");
        Equal(Point(layout, At(0, 3)) + " " + Point(layout, At(2, 1)) + " " + Point(layout, At(4, 1)), "46,0 16,0 56,18", "the point of a hidden line is the start of the row of its header");
        Equal(P(layout.HitTest(16 + 120, 0)) + " " + P(layout.HitTest(16 + 500, 0)) + " " + P(layout.HitTest(16, 18)), "0:12 0:17 4:0", "a point on a folded header is an index of its text; the next row is the line after the block");
        Equal(layout.LineBefore(4) + " " + layout.LineAfter(0) + " " + layout.LineAfter(2) + " " + layout.LineBefore(0) + " " + layout.LineAfter(5) + " " + layout.LineBefore(5), "0 4 4 0 5 4", "the lines before and after go past the hidden lines");
        Equal(Runs(layout, 0, R(0, 3, 1), R(4, 12, 2)), "0-3:1 3-4:-1 4-8:2 8-13:-2 13-17:-1", "the colours of a folded header end at its label, the label has its own, the rest of the text");
        Equal(Runs(layout, 0), "0-8:-1 8-13:-2 13-17:-1", "without colours");
        Equal(Runs(layout, 1, R(1, 3, 4)), "0-4:-1 4-6:4 6-8:-1", "a line that is not folded has no label");
        Equal(Selected(layout, At(0, 2), At(4, 1)), "0:2-17+ 1:0-4", "a selection over a folded block");
        layout.UpdateMaxColumns(-1);
        Equal(layout.MaxColumns, 17, "the widest line counts the text of a folded header");
        folds.Hidden[4] = 1;
        layout.InvalidateRows();
        Equal(Rows(layout), "0[0,17)'int f() {...} end' 4[0,18)'    g(); {...} end'", "a fold that hides the last line");
        Equal(layout.LineAfter(4) + " " + layout.PositionToRow(At(5, 0)), "4 1", "nothing after the last row");
        view.Wrapping = true;
        Equal(layout.RowCount + " " + layout.LineText(0), "6 int f()", "while the lines are wrapped nothing is folded");
        Equal(layout.LineBefore(2) + " " + layout.LineAfter(2) + " " + layout.LineBefore(0) + " " + layout.LineAfter(5), "1 3 0 5", "and the neighbours are the next lines");
        view.Wrapping = false;
        folds.Hidden.Clear();
        layout.InvalidateRows();
        Equal(layout.RowCount, 6, "unfolded again");
        view.Text = new EditableTextDocument(new[] { "a" }, TextDocument.LineEnding.Lf);
        folds.Hidden[0] = 1;
        Equal(layout.RowCount, 1, "a new document makes the rows again");

        string[] texts = { "", "abc", "\tx", "a\tb\t\tc", "\t\t", "  \t z" };
        bool same = true;
        foreach (int tabSize in new[] { 1, 3, 4, 8 })
        {
            foreach (string text in texts)
            {
                EditableTextDocument document = new EditableTextDocument(new[] { text }, TextDocument.LineEnding.Lf);
                for (int i = -1; i <= text.Length + 2; i++)
                {
                    same &= TextViewLayout.IndexToColumn(text, i, tabSize) == document.IndexToColumn(0, i, tabSize);
                }
                for (int column = -1; column <= 3 * tabSize + 6; column++)
                {
                    same &= TextViewLayout.ColumnToIndex(text, column, tabSize) == document.ColumnToIndex(0, column, tabSize);
                }
            }
        }
        Check(same, "the columns of a text are counted as TextDocument counts them");
    }

    private static int Main()
    {
        try
        {
            Rows();
            Wrapped();
            Selection();
            Points();
            Scrolling();
            Bars();
            WheelAndDrag();
            Clicks();
            Colors();
            Folds();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message.StartsWith("FAIL", StringComparison.Ordinal) ? exception.Message : "FAIL: " + exception);
            return 1;
        }
        Console.WriteLine("TextViewLayoutTests: all " + _passed + " checks passed.");
        return 0;
    }
}
