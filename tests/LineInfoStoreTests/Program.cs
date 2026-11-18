using System;
using System.Collections.Generic;
using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;

// Checks of the store of the line information of the terminal (Terminal/Emulation/LineInfoStore.cs): which lines are kept, the
// styles of the cells, shifting cells for the insert and delete of characters, and the lines inserted, removed, dimmed and
// marked as wrapped. The terminal is a fake that tells where a screen row is in the text and how wide the screen is.
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

    private sealed class FakeTerminal : ILineInfoHost
    {
        // Rows scrolled off the screen: the text line of the screen row 0.
        public int FirstRow;

        public bool AlwaysTrackLines { get; set; }

        public bool HideMargins { get; set; } = true;

        public int VisibleColumns { get; set; } = 10;

        public TextDocument.TextPosition ToDocumentPosition(GridPoint point)
        {
            return new TextDocument.TextPosition(point.X, point.Y + FirstRow);
        }
    }

    private sealed class Target : ILinkTarget
    {
        public bool IsActive => true;

        public void Activate()
        {
        }
    }

    private static CellStyle Red()
    {
        CellStyle style = default;
        style.Foreground = 2;
        return style;
    }

    private static CellStyle Style(byte foreground)
    {
        CellStyle style = default;
        style.Foreground = foreground;
        return style;
    }

    private static byte[] Foregrounds(LineInfo line)
    {
        byte[] result = new byte[line.Cells.Length];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = line.Cells[index].Foreground;
        }
        return result;
    }

    private static string Join(byte[] bytes)
    {
        return string.Join(",", bytes);
    }

    private static void Main()
    {
        Which();
        Cells();
        Shifts();
        Lines();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " line information checks passed.");
    }

    // Which lines get information at all.
    private static void Which()
    {
        FakeTerminal terminal = new FakeTerminal();
        LineInfoStore store = new LineInfoStore(terminal);
        store.SetCells(0, 0, 5, CellStyle.Default);
        Check(store.GetLine(0) == null, "plain text without styles on a screen without margins keeps no line");
        Check(store.GetLine(7) == null, "a line past the end is null");

        terminal.AlwaysTrackLines = true;
        store.SetCells(0, 0, 5, CellStyle.Default);
        Check(store.GetLine(0) != null, "the lines are kept when the terminal tracks them always");
        Equal(store.GetLine(0).Cells.Length, 10, "the cells are as many as the columns");
        Check(store.GetLine(0).Timestamp != default, "the time of the line is set");

        terminal.AlwaysTrackLines = false;
        terminal.HideMargins = false;
        store.SetCells(2, 0, 5, CellStyle.Default);
        Check(store.GetLine(2) != null, "the lines are kept when margins are shown");
        Check(store.GetLine(1) == null, "...and the line before it is a hole");

        FakeTerminal other = new FakeTerminal();
        LineInfoStore styled = new LineInfoStore(other);
        styled.SetCells(1, 0, 3, Red());
        Check(styled.GetLine(1) != null, "a style makes a line");
        Check(styled.GetLine(0) == null, "the line above stays a hole");

        LinkCell[] links = new LinkCell[1];
        links[0].Target = new Target();
        LineInfoStore linked = new LineInfoStore(new FakeTerminal());
        linked.SetCells(0, 0, 3, CellStyle.Default, links);
        Check(linked.GetLine(0) != null, "a link makes a line");

        FakeTerminal scrolled = new FakeTerminal { FirstRow = 3 };
        LineInfoStore scrolledStore = new LineInfoStore(scrolled);
        scrolledStore.SetCells(0, 0, 3, Red());
        Check(scrolledStore.GetLine(3) != null && scrolledStore.GetLine(0) == null, "the screen row is counted from the first row of the screen");
    }

    private static void Cells()
    {
        FakeTerminal terminal = new FakeTerminal();
        LineInfoStore store = new LineInfoStore(terminal);
        store.SetCells(0, 2, 3, Style(2));
        Equal(Join(Foregrounds(store.GetLine(0))), "0,0,2,2,2,0,0,0,0,0", "three cells from column 2 get the style");

        store.SetCells(0, 7, -1, Style(3));
        Equal(Join(Foregrounds(store.GetLine(0))), "0,0,2,2,2,0,0,3,3,3", "-1 is up to the end of the line");

        store.SetCells(0, 8, 99, Style(4));
        Equal(Join(Foregrounds(store.GetLine(0))), "0,0,2,2,2,0,0,3,4,4", "a count past the end stops at the end");

        store.SetCells(0, -1, 5, Style(5));
        Equal(Join(Foregrounds(store.GetLine(0))), "0,0,2,2,2,0,0,3,4,4", "a start of -1 is the end of the line: nothing to set");

        store.SetCells(0, 50, 5, Style(5));
        Equal(Join(Foregrounds(store.GetLine(0))), "0,0,2,2,2,0,0,3,4,4", "a start past the end is the end of the line: nothing to set");

        LinkCell[] links = new LinkCell[2];
        links[0].Target = new Target();
        links[1].Severity = 3;
        store.SetCells(1, 0, 4, Style(2), links);
        LineInfo line = store.GetLine(1);
        Check(line.Cells[0]._link.Target != null && line.Cells[1]._link.Severity == 3, "the links go to the cells they are given for");
        Check(line.Cells[2]._link.Target == null && line.Cells[2]._link.Severity == 0, "the cells after the links get none");
        store.SetCells(1, 0, 4, Style(2));
        Check(store.GetLine(1).Cells[0]._link.Target == null && store.GetLine(1).Cells[1]._link.Severity == 0, "styling again without links takes the old links away");

        store.SetCells(2, 0, 3, Style(2));
        store.GetLine(2).IsDimmed = true;
        store.GetLine(2).IsWrapped = true;
        store.SetCells(2, 0, 3, Style(2));
        Check(!store.GetLine(2).IsDimmed && !store.GetLine(2).IsWrapped, "writing to a line makes it neither dimmed nor wrapped");

        terminal.VisibleColumns = 14;
        store.SetCells(0, 12, 5, Style(6));
        Equal(store.GetLine(0).Cells.Length, 14, "a wider screen makes the cells of the line longer");
        Equal(Join(Foregrounds(store.GetLine(0))), "0,0,2,2,2,0,0,3,4,4,0,0,6,6", "...and keeps the old ones");
    }

    private static void Shifts()
    {
        FakeTerminal terminal = new FakeTerminal();
        LineInfoStore store = new LineInfoStore(terminal);
        for (int column = 0; column < 10; column++)
        {
            store.SetCells(0, column, 1, Style((byte)(column + 1)));
        }
        store.DeleteCells(new GridPoint(2, 0), 3);
        Equal(Join(Foregrounds(store.GetLine(0))), "1,2,6,7,8,9,10,0,0,0", "deleting 3 cells at column 2 moves the rest left and clears the end");

        for (int column = 0; column < 10; column++)
        {
            store.SetCells(0, column, 1, Style((byte)(column + 1)));
        }
        store.InsertCells(new GridPoint(2, 0), 3);
        Equal(Join(Foregrounds(store.GetLine(0))), "1,2,3,3,3,3,4,5,6,7", "inserting 3 cells at column 2 moves the rest right and copies the style of the cell");

        store.InsertCells(new GridPoint(2, 0), 0);
        Equal(Join(Foregrounds(store.GetLine(0))), "1,2,3,3,3,3,4,5,6,7", "inserting none changes nothing");
        store.InsertCells(new GridPoint(40, 0), 2);
        Equal(Join(Foregrounds(store.GetLine(0))), "1,2,3,3,3,3,4,5,6,7", "inserting past the end changes nothing");
        store.DeleteCells(new GridPoint(2, 5), 2);
        store.InsertCells(new GridPoint(2, 5), 2);
        Check(store.GetLine(5) == null, "a line without information stays so");

        store.GetLine(0).IsWrapped = true;
        store.GetLine(0).IsDimmed = true;
        store.DeleteCells(new GridPoint(0, 0), 1);
        Check(!store.GetLine(0).IsWrapped && !store.GetLine(0).IsDimmed, "deleting cells clears the marks of the line");
        store.GetLine(0).IsWrapped = true;
        store.InsertCells(new GridPoint(0, 0), 1);
        Check(!store.GetLine(0).IsWrapped, "inserting cells clears the mark of the line");

        FakeTerminal scrolled = new FakeTerminal { FirstRow = 2 };
        LineInfoStore scrolledStore = new LineInfoStore(scrolled);
        scrolledStore.SetCells(0, 0, 10, Style(2));
        scrolledStore.DeleteCells(new GridPoint(0, 0), 4);
        Equal(Join(Foregrounds(scrolledStore.GetLine(2))), "2,2,2,2,2,2,0,0,0,0", "the cells shifted are those of the line in the text, not of the row of the screen");

        LineInfoStore marked = new LineInfoStore(new FakeTerminal());
        marked.MarkWrapped(1);
        Check(marked.GetLine(1).Cells == null, "a line that was only marked has no cells");
        marked.DeleteCells(new GridPoint(0, 1), 2);
        marked.InsertCells(new GridPoint(0, 1), 2);
        Check(marked.GetLine(1).Cells == null, "...and shifting its cells changes nothing");
    }

    private static void Lines()
    {
        FakeTerminal terminal = new FakeTerminal();
        LineInfoStore store = new LineInfoStore(terminal);
        for (int row = 0; row < 4; row++)
        {
            store.SetCells(row, 0, 1, Style((byte)(row + 1)));
        }
        store.ClearLine(1);
        Check(store.GetLine(1) == null && store.GetLine(2) != null, "ClearLine makes one line a hole");
        store.ClearLine(9);
        Equal(store.GetLine(3).Cells[0].Foreground, (byte)4, "clearing a line past the end changes nothing");

        store.RemoveLines(0, 2);
        Equal(store.GetLine(0).Cells[0].Foreground, (byte)3, "removing two lines moves the rest up");
        Equal(store.GetLine(1).Cells[0].Foreground, (byte)4, "...in order");
        Check(store.GetLine(2) == null, "...and the end is gone");
        store.RemoveLines(1, 50);
        Check(store.GetLine(1) == null, "removing more than there is stops at the end");
        store.RemoveLines(9, 1);
        Equal(store.GetLine(0).Cells[0].Foreground, (byte)3, "removing past the end changes nothing");

        store.InsertLines(0, 2);
        Check(store.GetLine(0) == null && store.GetLine(1) == null, "inserting two lines makes two holes");
        Equal(store.GetLine(2).Cells[0].Foreground, (byte)3, "...and moves the rest down");
        store.InsertLines(30, 2);
        Check(store.GetLine(30) == null, "inserting past the end does nothing");

        LineInfoStore dimmed = new LineInfoStore(new FakeTerminal());
        dimmed.SetCells(1, 0, 2, Style(2));
        dimmed.DimLines(3);
        Check(dimmed.GetLine(0).IsDimmed && dimmed.GetLine(1).IsDimmed && dimmed.GetLine(2).IsDimmed, "DimLines dims the lines, making the ones that have none");
        Equal(dimmed.GetLine(1).Cells[0].Foreground, (byte)2, "...and keeps the cells of the line that had them");
        Check(dimmed.GetLine(3) == null, "...and the line after them is not touched");

        LineInfoStore wrapped = new LineInfoStore(new FakeTerminal { FirstRow = 1 });
        wrapped.MarkWrapped(2);
        Check(wrapped.GetLine(3).IsWrapped && wrapped.GetLine(0) == null && wrapped.GetLine(2) == null, "MarkWrapped marks the line of the row, counted from the first row of the screen");

        dimmed.Clear();
        Check(dimmed.GetLine(0) == null, "Clear forgets all lines");
    }
}
