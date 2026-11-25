using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;

// Checks of the screen of the terminal (Terminal/Emulation/TerminalScreen.cs): the cursor and where it may go, writing with the wrap and the
// scroll, the scroll region, erasing, inserting and deleting, the two buffers, the batch that draws once, the scrollback limit, the resize, and
// what the screen tells the view. The view is a fake that keeps the document and counts the calls.
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

    private sealed class Scanner : ILinkScanner
    {
        public List<string> Texts = new List<string>();

        public bool Finished;

        public LinkCell[] Scan(string text)
        {
            Texts.Add(text);
            return null;
        }

        public void Finish()
        {
            Finished = true;
        }
    }

    private sealed class FakeView : ITerminalScreenView
    {
        public int VisibleRows { get; set; } = 4;

        public int VisibleColumns { get; set; } = 10;

        public EditableTextDocument Document { get; private set; }

        public int TopLine { get; set; }

        public bool AlwaysTrackLines { get; set; }

        public bool HideMargins { get; set; } = true;

        public TextDocument.TextPosition SelectionStart { get; set; }

        public bool HideCaret { get; set; }

        public bool TraceEnabled { get; set; }

        public int Redraws;

        public int Layouts;

        public int ForcedCarets;

        public int TextReplaced;

        public int SelectionsDropped;

        public int DocumentsSet;

        public int MaxLineWidthCalls;

        public TextDocument.TextPosition Caret;

        public int CaretCalls;

        public Scanner Scanner;

        public List<string> Traces = new List<string>();

        public void SetDocument(EditableTextDocument document)
        {
            Document = document;
            DocumentsSet++;
        }

        public void SetCaret(TextDocument.TextPosition position)
        {
            Caret = position;
            CaretCalls++;
        }

        public void ForceCaretUpdate()
        {
            ForcedCarets++;
        }

        public void SetSelection(bool select, TextDocument.TextPosition position)
        {
            if (!select)
            {
                SelectionsDropped++;
            }
        }

        public void OnTextReplaced()
        {
            TextReplaced++;
        }

        public void UpdateMaxLineWidth(int line)
        {
            MaxLineWidthCalls++;
        }

        public void UpdateLayout()
        {
            Layouts++;
        }

        public void Redraw()
        {
            Redraws++;
        }

        public ILinkScanner CreateLinkScanner()
        {
            return Scanner;
        }

        public void Trace(string text)
        {
            Traces.Add(text);
        }
    }

    private static TerminalScreen Make(out FakeView view, int columns = 10, int rows = 4)
    {
        view = new FakeView { VisibleColumns = columns, VisibleRows = rows };
        TerminalScreen screen = new TerminalScreen(view);
        screen.CreateBuffers();
        screen.ActiveBufferIndex = 0;
        return screen;
    }

    private static string Lines(FakeView view)
    {
        List<string> lines = new List<string>();
        for (int line = 0; line < view.Document.LineCount; line++)
        {
            lines.Add(view.Document.GetLineText(line));
        }
        return string.Join("|", lines);
    }

    private static CellStyle Red()
    {
        CellStyle style = default;
        style.Foreground = 2;
        return style;
    }

    private static void Fill(TerminalScreen screen, params string[] rows)
    {
        for (int index = 0; index < rows.Length; index++)
        {
            screen.WriteAt(new GridPoint(0, index), rows[index], default);
        }
    }

    private static void CursorChecks()
    {
        TerminalScreen screen = Make(out FakeView view);
        screen.Cursor = new GridPoint(50, 50);
        Equal(screen.Cursor, new GridPoint(10, 3), "a cursor past the screen goes to the last row and to the column after the last one");
        screen.Cursor = new GridPoint(-3, -3);
        Equal(screen.Cursor, new GridPoint(0, 0), "a negative cursor goes to the corner");
        screen.Cursor = new GridPoint(4, 2);
        Equal(view.Caret, new TextDocument.TextPosition(4, 2), "the caret of the view follows the cursor");
        int calls = view.CaretCalls;
        screen.Cursor = new GridPoint(4, 2);
        Equal(view.CaretCalls, calls, "the same cursor does not move the caret again");
        screen.MoveCursorBy(3, -1);
        Equal(screen.Cursor, new GridPoint(7, 1), "the cursor moves by a distance");
        screen.MoveCursorBy(0, -9);
        Equal(screen.Cursor.Y, 0, "the cursor does not go above the screen");
        screen.MoveCursorBy(0, 9);
        Equal(screen.Cursor.Y, 3, "without the scroll the cursor stops at the last row");
        Check(view.Document.LineCount >= 4, "and the document has a line for every row up to it");
        screen.Cursor = new GridPoint(10, 1);
        Equal(view.Caret, new TextDocument.TextPosition(0, 2), "at the pending-wrap position the caret is in the first column of the next row");
        Equal(screen.CursorDocumentLine, 1, "the line of the cursor");
    }

    private static void BatchChecks()
    {
        TerminalScreen screen = Make(out FakeView view);
        screen.BeginBatch();
        int calls = view.CaretCalls;
        int redraws = view.Redraws;
        screen.Cursor = new GridPoint(3, 1);
        screen.WriteText("abc");
        screen.WriteText("def");
        Equal(view.CaretCalls, calls, "no caret is set in a batch");
        Equal(view.Redraws, redraws, "nothing is drawn in a batch");
        screen.EndBatch();
        Equal(view.Redraws, redraws + 2, "the batch draws at the end (once for the layout it made pending, once for the text)");
        Equal(view.Caret, new TextDocument.TextPosition(9, 1), "and puts the caret where the cursor is");
        redraws = view.Redraws;
        screen.WriteText("x");
        Check(view.Redraws > redraws, "outside a batch a write draws at once");
    }

    private static void WriteChecks()
    {
        TerminalScreen screen = Make(out FakeView view);
        screen.CurrentStyle = Red();
        screen.WriteText("hello");
        Equal(Lines(view), "hello", "text is written");
        Equal(screen.Cursor, new GridPoint(5, 0), "the cursor moves behind it");
        Equal(screen.ActiveBuffer.Lines.GetLine(0).Cells[2].Foreground, (byte)2, "the cells get the current style");
        screen.WriteText("123456");
        Equal(Lines(view), "hello12345|6", "text wraps at the right edge");
        Check(screen.ActiveBuffer.Lines.GetLine(0).IsWrapped, "the row that wrapped is marked");
        Equal(screen.Cursor, new GridPoint(1, 1), "the cursor is in the next row");
        screen.Cursor = new GridPoint(0, 3);
        screen.WriteText("0123456789");
        Equal(screen.Cursor, new GridPoint(10, 3), "a full row leaves the cursor at the pending-wrap position");
        screen.WriteText("z");
        Equal(screen.ActiveBuffer.FirstRow, 1, "text at the end of the last row scrolls the screen");
        Equal(Lines(view), "hello12345|6||0123456789|z", "and goes to a new row");
        Equal(screen.Cursor, new GridPoint(1, 3), "the cursor stays on the last row");
        screen.WriteText("");
        Equal(view.Traces.Count, 0, "no trace without the flag");
        view.TraceEnabled = true;
        screen.WriteText("t");
        Equal(string.Join(",", view.Traces), "'t'", "the text is traced when the view asks for it");

        screen = Make(out view);
        screen.WriteText("ab");
        screen.Cursor = new GridPoint(0, 0);
        screen.InsertMode = true;
        screen.WriteText("XY");
        Equal(Lines(view), "XYab", "in the insert mode the text pushes the rest to the right");
        screen.WriteText("123456789");
        Equal(view.Document.GetLineText(0).Length <= 10 ? "fits" : "long", "fits", "what is pushed past the right edge is lost");

        screen = Make(out view);
        screen.AutoWrap = false;
        screen.WriteText("0123456789ABCDEF");
        Equal(Lines(view), "0123456789ABCDEF", "without the wrap the line goes on past the edge");
        Check(view.MaxLineWidthCalls > 0, "and the view is told that the line is wider");
    }

    // The lines with the second cell of a wide character shown as '#'.
    private static string Cells(FakeView view)
    {
        return Lines(view).Replace(CellWidth.Filler, '#');
    }

    private static void WidthTableChecks()
    {
        int wide = 0;
        int starts = 0;
        bool previous = false;
        for (int code = 0; code <= 0xFFFF; code++)
        {
            bool isWide = CellWidth.IsWide((char)code);
            if (isWide)
            {
                wide++;
            }
            if (isWide && !previous)
            {
                starts++;
            }
            previous = isWide;
        }
        // The numbers of the wide and fullwidth characters of EastAsianWidth.txt of Unicode 15.1 in the Basic Multilingual Plane.
        Equal(wide, 42184, "the table holds every wide and fullwidth character of the plane");
        Equal(starts, 58, "in 58 ranges (the table is sorted and no two ranges touch)");
        string[] narrow = { "a", "\u00e9", "\u0416", "\u1160", "\u2764", "\u303F", "\uD7A4", "\uFF61", "\uFFEE", "\uD83D", "\uFFFF", "\uFFFD", "\u2500" };
        foreach (string ch in narrow)
        {
            Check(!CellWidth.IsWide(ch[0]), "U+" + ((int)ch[0]).ToString("X4") + " takes one cell");
        }
        string[] wideOnes = { "\u1100", "\u115F", "\u231A", "\u3000", "\u3042", "\u4E00", "\u9FFF", "\uAC00", "\uD7A3", "\uF900", "\uFF01", "\uFF60", "\uFFE6" };
        foreach (string ch in wideOnes)
        {
            Check(CellWidth.IsWide(ch[0]), "U+" + ((int)ch[0]).ToString("X4") + " takes two cells");
        }
        Equal(CellWidth.ToCells("ab"), "ab", "a text without wide characters is its own cells");
        Equal(CellWidth.ToCells("a\u4e2d\u6587b").Replace(CellWidth.Filler, '#'), "a\u4e2d#\u6587#b", "a filler after every wide character");
        Equal(CellWidth.ToCells("\uFFFFx"), "\uFFFDx", "a filler in the text is not taken for one");
        Equal(CellWidth.Strip("a\u4e2d\uFFFFb\uFFFF"), "a\u4e2db", "the fillers are taken out of the text that leaves the screen");
        Check(CellWidth.Strip(null) == null, "nothing stays nothing");
        Check(CellWidth.IsSecondCell("\u4e2d\uFFFF", 1), "the filler after a wide character is its second cell");
        Check(!CellWidth.IsSecondCell("a\uFFFF", 1), "a filler after a narrow character is an empty cell");
        Check(!CellWidth.IsSecondCell("\u4e2d\uFFFF", 0) && !CellWidth.IsSecondCell("\u4e2d\uFFFF", 2), "and no other place is");
    }

    private static void WideChecks()
    {
        const string Zhong = "\u4e2d";
        const string Wen = "\u6587";
        TerminalScreen screen = Make(out FakeView view);
        screen.WriteText("a" + Zhong + "b");
        Equal(Cells(view), "a" + Zhong + "#b", "a wide character takes two cells: itself and a filler");
        Equal(screen.Cursor, new GridPoint(4, 0), "and the cursor moves by two for it");

        screen = Make(out view);
        screen.WriteText("123456789" + Zhong + "x");
        Equal(Cells(view), "123456789#|" + Zhong + "#x", "a wide character that would start in the last column goes to the next row, the last cell left empty");
        Check(screen.ActiveBuffer.Lines.GetLine(0).IsWrapped, "the row that wrapped is marked");
        Equal(screen.Cursor, new GridPoint(3, 1), "the cursor is behind it in the next row");

        screen = Make(out view);
        screen.WriteText("12345678" + Zhong);
        Equal(Cells(view), "12345678" + Zhong + "#|", "a wide character in the last two columns fits (the caret of the pending wrap is in the next row)");
        Equal(screen.Cursor, new GridPoint(10, 0), "and leaves the cursor at the pending-wrap position");

        screen = Make(out view);
        screen.WriteText("12345678");
        screen.WriteText(Zhong);
        Equal(Cells(view), "12345678" + Zhong + "#|", "written on its own it fits there too");
        screen.Cursor = new GridPoint(9, 0);
        screen.WriteText(Wen);
        Equal(Cells(view), "12345678 #|" + Wen + "#", "from the last column it goes to the next row: the half it was written over is a blank, the last cell is left empty");

        screen = Make(out view);
        screen.WriteText(Zhong);
        screen.Cursor = new GridPoint(1, 0);
        screen.WriteText("x");
        Equal(Cells(view), " x", "writing over the second cell blanks the first one");
        screen = Make(out view);
        screen.WriteText(Zhong);
        screen.Cursor = new GridPoint(0, 0);
        screen.WriteText("x");
        Equal(Cells(view), "x ", "writing over the first cell blanks the second one");
        screen = Make(out view);
        screen.WriteText(Zhong + Wen);
        screen.Cursor = new GridPoint(1, 0);
        screen.WriteText(Zhong);
        Equal(Cells(view), " " + Zhong + "# ", "a wide character over the halves of two others blanks the halves left");
        screen = Make(out view);
        screen.WriteText("abc");
        screen.Cursor = new GridPoint(1, 0);
        screen.WriteText(Zhong);
        Equal(Cells(view), "a" + Zhong + "#", "a wide character over narrow ones takes two of them");

        screen = Make(out view);
        screen.WriteText("a" + Zhong + "b");
        screen.Cursor = new GridPoint(2, 0);
        screen.EraseInLine(EraseMode.ToEnd);
        Equal(Cells(view), "a ", "erasing from the second cell blanks the first one");
        screen = Make(out view);
        screen.WriteText(Zhong + "ab");
        screen.Cursor = new GridPoint(0, 0);
        screen.EraseInLine(EraseMode.ToStart);
        Equal(Cells(view), "  ab", "erasing up to the first cell blanks the second one");

        screen = Make(out view);
        screen.WriteText("a" + Zhong + "bc");
        screen.Cursor = new GridPoint(2, 0);
        screen.DeleteChars(1);
        Equal(Cells(view), "a bc", "deleting the second cell leaves a blank for the first");
        screen = Make(out view);
        screen.WriteText("a" + Zhong + "bc");
        screen.Cursor = new GridPoint(1, 0);
        screen.DeleteChars(2);
        Equal(Cells(view), "abc", "deleting both cells removes the character");
        screen = Make(out view);
        screen.WriteText(Zhong + "b");
        screen.Cursor = new GridPoint(1, 0);
        screen.InsertChars(1);
        Equal(Cells(view), "   b", "inserting between the two cells blanks the character");
        screen = Make(out view);
        screen.WriteText("12345678" + Zhong);
        screen.Cursor = new GridPoint(0, 0);
        screen.InsertChars(1);
        Equal(Cells(view), " 12345678 |", "a wide character pushed half past the right edge becomes a blank");

        screen = Make(out view);
        screen.WriteText("ab");
        screen.Cursor = new GridPoint(0, 0);
        screen.InsertMode = true;
        screen.WriteText(Zhong);
        Equal(Cells(view), Zhong + "#ab", "in the insert mode a wide character pushes the rest by two");

        screen = Make(out view);
        screen.WriteText(Zhong);
        screen.RepeatPreviousChar(2);
        Equal(Cells(view), Zhong + "#" + Zhong + "#" + Zhong + "#", "REP after a wide character repeats it");

        screen = Make(out view);
        screen.WideCharacters = false;
        screen.WriteText(Zhong + "x");
        Equal(Cells(view), Zhong + "x", "without wide characters (the byte mode) every character takes one cell");
        Equal(screen.Cursor, new GridPoint(2, 0), "and the cursor moves by one for it");
        screen.WideCharacters = true;
        screen.WriteText("\uFFFF");
        Equal(Lines(view), Zhong + "x\uFFFD", "a filler that came with the text is shown as the replacement character");

        screen = Make(out view, columns: 1);
        screen.WriteText(Zhong);
        Equal(Cells(view), Zhong + "|#|", "on a screen of one column the two cells go to two rows (and the write ends)");

        screen = Make(out view);
        screen.AutoWrap = false;
        screen.WriteText("123456789" + Zhong);
        Equal(Cells(view).Split('|')[0], "123456789" + Zhong + "#", "without the wrap a wide character goes on past the edge");
    }

    // The characters of no width kept with the cells of a row: "column:marks" for every cell that has some (U+XXXX for each unit).
    private static string Marks(TerminalScreen screen, int row)
    {
        LineInfo line = screen.ActiveBuffer.Lines.GetLine(screen.ActiveBuffer.FirstRow + row);
        List<string> parts = new List<string>();
        for (int column = 0; line?.Marks != null && column < line.Marks.Length; column++)
        {
            if (line.Marks[column] != null)
            {
                parts.Add(column + ":" + string.Concat(line.Marks[column].Select(ch => "U+" + ((int)ch).ToString("X4"))));
            }
        }
        return string.Join(" ", parts);
    }

    private static void ZeroWidthChecks()
    {
        int zero = 0;
        int starts = 0;
        bool previous = false;
        for (int code = 0; code <= 0x10FFFF; code++)
        {
            bool isZero = CellWidth.IsZeroWidth(code);
            zero += isZero ? 1 : 0;
            starts += isZero && !previous ? 1 : 0;
            previous = isZero;
        }
        Equal(zero + " in " + starts, "2394 in 354", "the table of the characters of no width (Unicode 15.1: Mn, Me, Cf but the shown ones, the medial and final jamo)");
        Check(CellWidth.IsZeroWidth(0x0301) && CellWidth.IsZeroWidth(0x200D) && CellWidth.IsZeroWidth(0xFE0F) && CellWidth.IsZeroWidth(0x1160) && CellWidth.IsZeroWidth(0xE0100),
            "a combining accent, the joiner, a variation selector, a medial jamo and a selector above the plane take no cell");
        Check(!CellWidth.IsZeroWidth(0x00AD) && !CellWidth.IsZeroWidth(0x0600) && !CellWidth.IsZeroWidth('a') && !CellWidth.IsZeroWidth(0x1100),
            "the soft hyphen, a prepended concatenation mark, a letter and a leading jamo take one");
        Equal(CellWidth.ZeroWidthLength("x\uDB40\uDD00", 1) + " " + CellWidth.ZeroWidthLength("\u0301", 0) + " " + CellWidth.ZeroWidthLength("ab", 0), "2 1 0", "the units of a character of no width");

        TerminalScreen screen = Make(out FakeView view);
        screen.WriteText("e\u0301x");
        Equal(Lines(view) + "|" + Marks(screen, 0) + "|" + screen.Cursor.X, "ex|0:U+0301|2", "a combining accent goes with the cell before it and takes none");
        screen = Make(out view);
        screen.WriteText("o\u0302\u0323");
        Equal(Marks(screen, 0), "0:U+0302U+0323", "two marks of one letter, in their order");
        screen = Make(out view);
        screen.WriteText("a");
        screen.WriteText("\u0300");
        Equal(Marks(screen, 0) + "|" + screen.Cursor.X, "0:U+0300|1", "a mark that comes in a write of its own finds the cell before the cursor");
        screen = Make(out view);
        screen.WriteText("\u4e2d\u0301b");
        Equal(Marks(screen, 0) + "|" + screen.Cursor.X, "0:U+0301|3", "after a wide character a mark goes with its first cell");
        screen = Make(out view);
        screen.WriteText("\u231A\uFE0F");
        Equal(Cells(view) + "|" + Marks(screen, 0) + "|" + screen.Cursor.X, "\u231A#|0:U+FE0F|2", "the variation selector after an emoji takes no cell");
        screen = Make(out view);
        screen.WriteText("x\uDB40\uDD00y");
        Equal(Lines(view) + "|" + Marks(screen, 0), "xy|0:U+DB40U+DD00", "a character of no width above the plane is kept whole");
        screen = Make(out view);
        screen.WriteText("123456789x");
        screen.WriteText("\u0301");
        Equal(Marks(screen, 0) + "|" + screen.Cursor.X, "9:U+0301|10", "at the pending-wrap position a mark goes with the last cell");
        screen = Make(out view);
        screen.Cursor = new GridPoint(0, 1);
        screen.WriteText("\u0301z");
        Equal(Lines(view) + "|" + Marks(screen, 1), "|z|", "at the start of a row a mark has no cell before it and is dropped");

        screen = Make(out view);
        screen.WriteText("e\u0301");
        screen.Cursor = new GridPoint(0, 0);
        screen.WriteText("z");
        Equal(Lines(view) + "|" + Marks(screen, 0), "z|", "a cell written again loses its marks");
        screen = Make(out view);
        screen.WriteText("ab\u0301c");
        screen.Cursor = new GridPoint(0, 0);
        screen.DeleteChars(1);
        Equal(Lines(view) + "|" + Marks(screen, 0), "bc|0:U+0301", "a deletion moves the marks with their cells");
        screen = Make(out view);
        screen.WriteText("a\u0301b");
        screen.Cursor = new GridPoint(0, 0);
        screen.InsertChars(1);
        Equal(Lines(view) + "|" + Marks(screen, 0), " ab|1:U+0301", "an insertion moves them too, and the cell put in has none");
        screen = Make(out view);
        screen.WriteText("a\u0301bc\u0300");
        screen.Cursor = new GridPoint(1, 0);
        screen.InsertChars(1);
        Equal(Lines(view) + "|" + Marks(screen, 0), "a bc|0:U+0301 3:U+0300", "the marks before the insertion stay, the cell put in takes none of them");
        screen = Make(out view);
        screen.WriteText("\u4e2d\u0301");
        screen.Cursor = new GridPoint(1, 0);
        screen.WriteText("x");
        Equal(Lines(view) + "|" + Marks(screen, 0), " x|", "a wide character cut in two loses its marks with it");
        screen = Make(out view);
        screen.WriteText("a\u0301b\u0301");
        screen.Cursor = new GridPoint(1, 0);
        screen.EraseInLine(EraseMode.ToEnd);
        Equal(Lines(view) + "|" + Marks(screen, 0), "a|0:U+0301", "erasing from a column takes the marks from there on");
        screen.EraseInLine(EraseMode.All);
        Equal(Marks(screen, 0), "", "and erasing the row takes them all");

        screen = Make(out view);
        screen.WideCharacters = false;
        screen.WriteText("e\u0301");
        Equal(Lines(view) + "|" + Marks(screen, 0) + "|" + screen.Cursor.X, "e\u0301||2", "in the byte mode a mark takes a cell of its own, as the device counts it");
    }

    private static void ScrollRegionChecks()
    {
        TerminalScreen screen = Make(out FakeView view);
        Fill(screen, "A", "B", "C", "D");
        screen.SetScrollRegion(1, 2);
        Check(screen.HasScrollRegion, "a region is set");
        Equal(screen.ScrollTop, 1, "its top");
        Equal(screen.ScrollBottom, 2, "its bottom");
        Equal(screen.Cursor, new GridPoint(0, 0), "setting it homes the cursor");
        screen.Cursor = new GridPoint(0, 2);
        screen.MoveCursorBy(0, 1, scroll: true);
        Equal(Lines(view), "A|C||D", "a line feed at the bottom of the region scrolls only the region");
        Equal(screen.ActiveBuffer.FirstRow, 0, "the screen did not scroll");
        screen.ScrollRegionUp(1, 1);
        Equal(Lines(view), "A|||D", "scroll up removes the first row of the region and adds one at its end");
        screen.SetScrollRegion(0, 3);
        Check(!screen.HasScrollRegion, "a region of the whole screen is no region");
        Equal(screen.ScrollBottom, 3, "the bottom is the last row then");
        screen.SetScrollRegion(2, 2);
        Check(!screen.HasScrollRegion, "a region of one row is no region");
        screen.SetScrollRegion(1, 2);
        screen.ClearScrollRegion();
        Check(!screen.HasScrollRegion, "the region is cleared");

        screen = Make(out view);
        Fill(screen, "A", "B", "C", "D");
        screen.SetScrollRegion(1, 2);
        screen.InsertBlankLines(1, 1);
        Equal(Lines(view), "A||B|D", "blank lines inserted in the region push its last row out of it");
        screen = Make(out view);
        Fill(screen, "A", "B", "C", "D");
        screen.InsertBlankLines(0, 1);
        Equal(Lines(view), "|A|B|C", "blank lines inserted on the screen push its last row out");
    }

    private static void EraseChecks()
    {
        TerminalScreen screen = Make(out FakeView view);
        Fill(screen, "abcdef", "ghijkl", "mnopqr", "stuvwx");
        screen.Cursor = new GridPoint(2, 1);
        screen.EraseInLine(EraseMode.ToEnd);
        Equal(Lines(view), "abcdef|gh|mnopqr|stuvwx", "erase to the end of the row");
        screen.Cursor = new GridPoint(2, 2);
        screen.EraseInLine(EraseMode.ToStart);
        Equal(Lines(view), "abcdef|gh|   pqr|stuvwx", "erase to the start of the row");
        screen.EraseInLine(EraseMode.All);
        Equal(view.Document.GetLineText(2), "  ", "erase the row (the row is padded up to the cursor, which stays where it was)");
        screen.Cursor = new GridPoint(0, 3);
        screen.EraseInLine(EraseMode.All);

        screen = Make(out view);
        Fill(screen, "abcdef", "ghijkl", "mnopqr", "stuvwx");
        screen.Cursor = new GridPoint(2, 1);
        screen.EraseInDisplay(EraseMode.ToEnd);
        Equal(Lines(view), "abcdef|gh||", "erase from the cursor to the end of the screen");
        screen = Make(out view);
        Fill(screen, "abcdef", "ghijkl", "mnopqr", "stuvwx");
        screen.Cursor = new GridPoint(2, 1);
        screen.EraseInDisplay(EraseMode.ToStart);
        Equal(Lines(view), "|   jkl|mnopqr|stuvwx", "erase from the start of the screen to the cursor");
        screen.EraseInDisplay(EraseMode.All);
        Equal(Lines(view), "|  ||", "erase the screen (the row of the cursor is padded up to the cursor)");

        screen = Make(out view);
        Fill(screen, "abc");
        screen.CurrentStyle = Red();
        screen.Cursor = new GridPoint(1, 0);
        screen.EraseInLine(EraseMode.ToEnd);
        Equal(view.Document.GetLineText(0), "a         ", "a style as the background pads the erased part");
        Equal(screen.ActiveBuffer.Lines.GetLine(0).Cells[9].Foreground, (byte)2, "and the padding has the style");
        screen = Make(out view);
        screen.CurrentStyle = Red();
        screen.EraseInLine(EraseMode.ToStart);
        screen.Cursor = new GridPoint(10, 0);
        screen.EraseInLine(EraseMode.ToStart);
        Equal(view.Document.GetLineText(0).Length, 10, "at the pending-wrap position the erase to the start stops at the last column");
        Equal(view.Document.GetLineText(1), "", "and does not blank the first cell of the next row");
    }

    private static void CharChecks()
    {
        TerminalScreen screen = Make(out FakeView view);
        Fill(screen, "abcdef");
        screen.Cursor = new GridPoint(1, 0);
        screen.InsertChars(2);
        Equal(view.Document.GetLineText(0), "a  bcdef", "characters are inserted");
        screen.DeleteChars(3);
        Equal(view.Document.GetLineText(0), "acdef", "characters are deleted");
        screen.DeleteChars(99);
        Equal(view.Document.GetLineText(0), "a", "deleting more than there is takes the rest of the row");
        screen.Cursor = new GridPoint(5, 0);
        string padded = view.Document.GetLineText(0);
        view.Document.ReplaceInLine(0, 1, -1, "");
        screen.DeleteChars(2);
        Equal(padded + "|" + view.Document.GetLineText(0), "a    |a", "nothing is deleted past the end of the row");
        screen.Cursor = new GridPoint(0, 0);
        screen.InsertChars(20);
        Equal(view.Document.GetLineText(0).Length, 10, "what an insertion pushes past the right edge is lost");

        screen = Make(out view);
        screen.CurrentStyle = Red();
        screen.WriteText("abc");
        screen.Cursor = new GridPoint(0, 0);
        screen.InsertChars(1);
        CellStyle[] cells = screen.ActiveBuffer.Lines.GetLine(0).Cells;
        Equal(cells[3].Foreground + "," + cells[4].Foreground, "2,0", "the styles of the cells move with the text");
        screen = Make(out view);
        Fill(screen, "ab");
        screen.Cursor = new GridPoint(2, 0);
        screen.RepeatPreviousChar(3);
        Equal(view.Document.GetLineText(0), "abbbb", "the character before the cursor is repeated");
        screen = Make(out view);
        screen.RepeatPreviousChar(2);
        Equal(view.Document.GetLineText(0), "  ", "at the start of a row it is a space");
    }

    private static void BufferChecks()
    {
        TerminalScreen screen = Make(out FakeView view);
        Fill(screen, "main");
        screen.Cursor = new GridPoint(3, 2);
        view.TopLine = 7;
        EditableTextDocument main = view.Document;
        screen.ActiveBufferIndex = 1;
        Check(view.Document != main, "the alternate buffer has its own document");
        Equal(screen.ActiveBufferIndex, 1, "its index");
        Equal(screen.Cursor, new GridPoint(0, 0), "its cursor starts at the corner");
        Equal(view.TopLine, 0, "and its first line is the top");
        int dropped = view.SelectionsDropped;
        screen.WriteText("alt");
        screen.ActiveBufferIndex = 0;
        Check(view.Document == main, "the normal buffer comes back");
        Equal(screen.Cursor, new GridPoint(3, 2), "with its cursor");
        Equal(view.TopLine, 7, "and its first line");
        Equal(view.SelectionsDropped, dropped + 1, "the selection is dropped when the buffer changes (once for the way back)");
        screen.ActiveBufferIndex = 5;
        Equal(screen.ActiveBufferIndex, 1, "an index past the last one means the last one");
        screen.ClearActiveScreen();
        Equal(view.Document.GetLineText(0), "   ", "the alternate screen is blanked (its first row is padded up to the cursor, which is kept)");
        Equal(screen.ActiveBuffer.FirstRow, 0, "from its first row");
        Check(view.TextReplaced > 0, "and the view is told");
        screen.ActiveBufferIndex = 0;
        Equal(view.Document.GetLineText(0), "main", "the normal buffer was not touched");
    }

    private static void ScrollbackChecks()
    {
        TerminalScreen screen = Make(out FakeView view);
        for (int line = 0; line < 10; line++)
        {
            screen.WriteText("line" + line);
            screen.MoveCursorBy(-screen.Cursor.X, 1, scroll: true);
        }
        Equal(screen.ActiveBuffer.FirstRow, 7, "ten lines on a screen of four scroll seven rows off");
        int layouts = view.Layouts;
        screen.ScrollbackLimit = 2;
        Equal(view.Document.LineCount, 4 + 2, "the scrollback is cut to the limit");
        Equal(screen.ActiveBuffer.FirstRow, 2, "the first row of the screen follows");
        Equal(view.Document.GetLineText(0), "line5", "the oldest lines go");
        Check(view.Layouts > layouts, "and the view lays itself out again");
        Equal(screen.ScrollbackLimit, 2, "the limit is kept");
    }

    private static void ResizeChecks()
    {
        TerminalScreen screen = Make(out FakeView view);
        for (int line = 0; line < 8; line++)
        {
            screen.WriteText("l" + line);
            screen.MoveCursorBy(-screen.Cursor.X, 1, scroll: true);
        }
        Equal(screen.ActiveBuffer.FirstRow, 5, "eight lines, four rows: five rows scrolled off");
        Equal(screen.Cursor, new GridPoint(0, 3), "the cursor is on the last row");
        view.VisibleRows = 6;
        screen.Resized();
        Equal(view.TopLine, 3, "a taller view shows the last six lines (the document has nine: the cursor is on the empty last one)");
        Equal(screen.ActiveBuffer.FirstRow, 3, "the first row of the screen follows");
        Equal(screen.Cursor, new GridPoint(0, 5), "and the cursor stays on its text");
        view.VisibleRows = 2;
        screen.Resized();
        Equal(screen.ActiveBuffer.FirstRow, 7, "a shorter view moves the first row down");
        Equal(screen.Cursor, new GridPoint(0, 1), "the cursor is held on the screen");
    }

    private static void SessionChecks()
    {
        TerminalScreen screen = Make(out FakeView view);
        for (int line = 0; line < 6; line++)
        {
            screen.WriteText("l" + line);
            screen.MoveCursorBy(-screen.Cursor.X, 1, scroll: true);
        }
        screen.WriteText("prompt$ ");
        screen.ClearOutput();
        Equal(Lines(view), "prompt$ ", "Clear removes everything above the line of the cursor");
        Equal(screen.ActiveBuffer.FirstRow, 0, "the screen starts at the top");
        Equal(screen.Cursor, new GridPoint(8, 0), "and the cursor is on the prompt");
        screen.ClearOutput();
        Equal(Lines(view), "prompt$ ", "with nothing above, Clear does nothing");
        screen.ActiveBufferIndex = 1;
        screen.WriteText("vi");
        screen.ClearOutput();
        Equal(view.Document.GetLineText(0), "vi", "the alternate screen is left alone");
        screen.ActiveBufferIndex = 0;

        screen.CurrentStyle = Red();
        screen.Reset();
        Equal(Lines(view), "", "a reset empties the screen");
        Equal(screen.Cursor, new GridPoint(0, 0), "and homes the cursor");
        Equal(screen.CurrentStyle.IsDefault, true, "and takes the style off");

        screen = Make(out view);
        screen.WriteText("abc");
        screen.ActiveBufferIndex = 1;
        screen.ArchiveScreen();
        Equal(screen.ActiveBufferIndex, 0, "a new session starts on the normal screen");
        Equal(screen.Cursor, new GridPoint(0, 1), "under the text, in the first column");
        Check(screen.ActiveBuffer.Lines.GetLine(0).IsDimmed, "and what was there is dimmed");
    }

    private static void LinkChecks()
    {
        TerminalScreen screen = Make(out FakeView view);
        view.Scanner = new Scanner();
        screen.WriteText("see a.c:3");
        Equal(string.Join(",", view.Scanner.Texts), "see a.c:3", "the scanner of the view gets the text");
        screen.ResetLinkTracking();
        Check(view.Scanner.Finished, "it is told to finish when the tracking is reset");
        view.Scanner = new Scanner();
        screen.Cursor = new GridPoint(0, 1);
        screen.WriteText("more");
        Equal(view.Scanner.Texts.Count, 1, "and a new one is made for the next text");
    }

    private static void ViewChecks()
    {
        TerminalScreen screen = Make(out FakeView view);
        Equal(screen.Rows, 4, "the rows are the rows of the view");
        Equal(screen.Columns, 10, "the columns are the columns of the view");
        screen.HideCaret = true;
        Check(view.HideCaret, "the caret is hidden in the view");
        TerminalScreen early = new TerminalScreen(new FakeView());
        Check(early.ActiveBuffer == null, "there is no active buffer before the buffers are made");
        screen.Tabs = new TabStops();
        Check(screen.Tabs != null, "the tab stops can be replaced");
    }

    private static int Main()
    {
        CursorChecks();
        BatchChecks();
        WriteChecks();
        WidthTableChecks();
        WideChecks();
        ZeroWidthChecks();
        ScrollRegionChecks();
        EraseChecks();
        CharChecks();
        BufferChecks();
        ScrollbackChecks();
        ResizeChecks();
        SessionChecks();
        LinkChecks();
        ViewChecks();
        Console.WriteLine("All " + _passed + " terminal screen checks passed.");
        return 0;
    }
}
