using System;
using System.Collections.Generic;
using SmarTTY.TextEditing;

// Checks of the view-independent parts of the hex editor that stage 3B-1 took out of Controls/HexEditorControl.cs: TextEditing/HexViewLayout.cs (the
// columns, the rows, the byte under a point, the scroll that keeps the caret in view, the scroll bars, the automatic scrolling and the wheel),
// HexKeyMap.cs (the keys), HexPointer.cs (the selection made with the mouse) and HexByteText.cs (the tool tip of a byte and the colours), with a fake
// view that has the sizes a test gives it.
// The same code is run in the real control by tests/HexEditorControlTests (the "view" run of its golden file).
// Run with: dotnet run --project tests/HexViewLayoutTests
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

    private sealed class FakeView : IHexLayoutView
    {
        public int ClientWidth { get; set; } = 800;

        public int ClientHeight { get; set; } = 300;

        public int InspectorWidth { get; set; }

        public int VerticalBarWidth { get; set; } = 17;

        public int HorizontalBarHeight { get; set; } = 17;

        public bool VerticalBarVisible { get; set; }

        public bool HorizontalBarVisible { get; set; }
    }

    private static byte[] Bytes(int count)
    {
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = (byte)(i * 13 + 5);
        }
        return bytes;
    }

    private sealed class Setup
    {
        public HexDocument Document;

        public HexEditingModel Model;

        public FakeView View = new FakeView();

        public HexViewLayout Layout;

        public int Changes;

        public Setup(int length = 1000, int groupSize = 1)
        {
            Document = new HexDocument(Bytes(length));
            Model = new HexEditingModel(Document) { BytesPerRow = 16 };
            Model.Changed += effects => Changes++;
            Layout = new HexViewLayout(Model, View) { GroupSize = groupSize };
            // A character of 8 pixels and a text of 15: rows of 18, a header of 22, the digits from 86.
            Layout.SetMetrics(8, 15);
        }

        // The middle of the left half of a hex digit, or of a character, of a byte in a visible row.
        public int X(int column, bool ascii, int digit = 0)
        {
            return (ascii ? Layout.AsciiStart + column * Layout.CharWidth : Layout.HexCellX(column) + digit * Layout.CharWidth) + 2 - Layout.ScrollX;
        }

        public int Y(int row)
        {
            return Layout.HeaderHeight + (row - Layout.FirstRow) * Layout.RowHeight + 5;
        }
    }

    private static void Metrics()
    {
        Console.WriteLine("-- the columns and the rows");
        Setup s = new Setup();
        Check(s.Layout.CharWidth == 8 && s.Layout.RowHeight == 18 && s.Layout.HeaderHeight == 22, "a row is the text and 3 pixels, the header a row and 4");
        Equal(s.Layout.HexStart, 6 + 8 * 8 + 2 * 8, "the digits start after the margin, the 8 digits of the offset and two characters");
        Equal(s.Layout.HexCellX(5), 86 + 5 * 24, "single bytes: a byte is two digits and a space");
        Equal(s.Layout.AsciiStart, 86 + 16 * 24 + 16, "the characters start two characters after the last group");
        Equal(s.Layout.ContentWidth, 486 + 16 * 8 + 6 + 1, "a row ends after the characters, the margin and a pixel");
        s = new Setup(groupSize: 4);
        Equal(s.Layout.HexCellX(4), 86 + 4 * 16 + 8, "groups of four: a space after each group only");
        Equal(s.Layout.AsciiStart, 86 + 16 * 16 + 4 * 8 + 16, "and the characters move with it");
        s.Layout.SetMetrics(0, -3);
        Check(s.Layout.CharWidth == 1 && s.Layout.RowHeight == 4, "a font that measures nothing still gives a pixel");
    }

    private static void Sizes()
    {
        Console.WriteLine("-- the rows and the width in view");
        Setup s = new Setup();
        Equal(s.Layout.VisibleRows, (300 - 22 - 6) / 18, "the rows below the header and above the margin");
        s.View.HorizontalBarVisible = true;
        Equal(s.Layout.VisibleRows, (300 - 17 - 22 - 6) / 18, "and above the horizontal scroll bar");
        s.View.ClientHeight = 10;
        Equal(s.Layout.VisibleRows, 1, "at least one");
        s.View.ClientHeight = 300;
        s.View.HorizontalBarVisible = false;
        Equal(s.Layout.VisibleWidth, 800, "the whole width without a scroll bar or the inspector");
        s.View.InspectorWidth = 200;
        s.View.VerticalBarVisible = true;
        Equal(s.Layout.AvailableWidth, 600, "the inspector takes its width");
        Equal(s.Layout.VisibleWidth, 583, "the vertical scroll bar its own");
        s.View.ClientWidth = 100;
        Equal(s.Layout.VisibleWidth, 1, "at least one pixel");
        Equal(s.Layout.RowCount, 1000 / 16 + 1, "the rows of the bytes and one for the caret after the last");
        Equal(new Setup(32).Layout.RowCount, 3, "a full last row: one more for the caret");
        s.View.ClientWidth = 86 + 8 * 24 + 199;
        Check(!s.Layout.InspectorFits(200), "the inspector does not fit when it leaves less than eight groups of three characters");
        s.View.ClientWidth += 1;
        Check(s.Layout.InspectorFits(200), "and fits when it does");
    }

    private static void HitTests()
    {
        Console.WriteLine("-- the byte under a point");
        Setup s = new Setup(groupSize: 4);
        Equal(s.Layout.ByteAtPoint(s.X(5, false), s.Y(2)), 37, "a digit of a byte");
        Equal(s.Layout.ByteAtPoint(s.X(5, false, 1), s.Y(2)), 37, "its second digit");
        Equal(s.Layout.ByteAtPoint(s.X(3, false, 2), s.Y(2)), -1, "the gap after a group is no byte");
        Equal(s.Layout.ByteAtPoint(s.X(7, true), s.Y(4)), 71, "a character");
        Equal(s.Layout.ByteAtPoint(s.X(15, true) + 3 * 8, s.Y(4)), -1, "right of the characters");
        Equal(s.Layout.ByteAtPoint(3, s.Y(4)), -1, "the offsets");
        Equal(s.Layout.ByteAtPoint(s.X(1, false), 10), -1, "the header");
        Equal(s.Layout.ByteAtPoint(s.X(9, false), s.Y(62)), -1, "past the last byte");
        Equal(s.Layout.ByteAtPoint(s.Layout.VisibleWidth, s.Y(1)), -1, "right of the visible width");
        Equal(s.Layout.ByteAtPoint(s.X(8, false, 1) - 2, s.Y(62)), -1, "the place of the caret after the last byte is no byte");
        // The digits of byte 12 are from 302 to 317 (groups of four): the edge of the window goes through them.
        s.View.ClientWidth = 305;
        Check(s.Layout.ByteAtPoint(305, s.Y(1)) == -1 && s.Layout.ByteAtPoint(304, s.Y(1)) == 28, "in a narrow window a byte behind its edge is not under the mouse");
        s.View.ClientWidth = 800;
        s.Layout.FirstRow = 10;
        s.Layout.ScrollX = 24;
        Equal(s.Layout.ByteAtPoint(s.X(2, false), s.Y(12)), 12 * 16 + 2, "scrolled down and to the right");

        Console.WriteLine("-- where a click puts the caret");
        s = new Setup(groupSize: 4);
        Equal(s.Layout.OffsetFromPoint(s.X(5, false), s.Y(2), out HexEditArea area, out int nibble), 37, "a high digit");
        Check(area == HexEditArea.Hex && nibble == 0, "in the hex digits, the high one");
        Equal(s.Layout.OffsetFromPoint(s.X(5, false, 1), s.Y(2), out area, out nibble), 37, "a low digit");
        Equal(nibble, 1, "the low one");
        s.Layout.OffsetFromPoint(s.Layout.HexCellX(5) + s.Layout.CharWidth, s.Y(2), out _, out nibble);
        Equal(nibble, 1, "the first pixel of the low digit is the low digit");
        s.Layout.OffsetFromPoint(s.Layout.HexCellX(5) + s.Layout.CharWidth - 1, s.Y(2), out _, out nibble);
        Equal(nibble, 0, "the pixel before it the high one");
        Equal(s.Layout.OffsetFromPoint(s.X(3, false, 2), s.Y(3), out area, out nibble), 51, "the gap after a group belongs to its last byte");
        Equal(nibble, 1, "on its low digit");
        Equal(s.Layout.OffsetFromPoint(2, s.Y(5), out area, out nibble), 80, "left of the bytes: the first byte of the row");
        Equal(s.Layout.OffsetFromPoint(s.X(7, true), s.Y(4), out area, out nibble), 71, "a character");
        Check(area == HexEditArea.Ascii && nibble == 0, "in the characters");
        Equal(s.Layout.OffsetFromPoint(s.X(15, true) + 40, s.Y(6), out area, out _), 111, "right of the characters: the last byte of the row");
        Equal(s.Layout.OffsetFromPoint(s.X(2, false), 5, out _, out _), 2, "above the rows: the first row");
        Equal(s.Layout.OffsetFromPoint(s.X(2, false), -40, out _, out _), 2, "well above the view: the first row too");
        Equal(s.Layout.OffsetFromPoint(s.X(9, false), s.Y(70), out _, out _), 1000, "below the bytes: after the last one");
        s.Layout.FirstRow = 3;
        Equal(s.Layout.OffsetFromPoint(s.X(1, false), s.Y(4), out _, out _), 65, "scrolled down");
    }

    private static void Table()
    {
        Console.WriteLine("-- the table and the scrolling while a selection is dragged");
        Setup s = new Setup();
        s.View.HorizontalBarVisible = true;
        s.View.VerticalBarVisible = true;
        s.Layout.GetTable(out int left, out int top, out int right, out int bottom);
        Check(left == 0 && top == 22 && right == 783 && bottom == 283, "below the header, above the horizontal bar, left of the vertical one");
        int x = 400;
        int y = 100;
        s.Layout.ClampToTable(ref x, ref y, out bool outside);
        Check(x == 400 && y == 100 && !outside, "a point in the table stays");
        x = -5;
        y = 900;
        s.Layout.ClampToTable(ref x, ref y, out outside);
        Check(x == 0 && y == 282 && outside, "a point outside is brought to the edge");
        s.View.ClientHeight = 22;
        s.Layout.GetTable(out _, out top, out _, out bottom);
        Equal(bottom - top, 1, "the table is at least a pixel high");
        s.View.ClientHeight = 300;
        s.Layout.AutoScrollStep(400, 100, out int rows, out int pixels);
        Check(rows == 0 && pixels == 0, "inside the table: no scrolling");
        s.Layout.AutoScrollStep(400, 21, out rows, out pixels);
        Check(rows == -HexScroll.AutoScrollSteps(1, 18) && pixels == 0, "above it: up");
        s.Layout.AutoScrollStep(400, 283, out rows, out _);
        Equal(rows, 1, "the first pixel below it: a row");
        s.Layout.AutoScrollStep(400, 283 + 50, out rows, out _);
        Equal(rows, HexScroll.AutoScrollSteps(51, 18), "below it: down, faster the further");
        s.Layout.AutoScrollStep(-20, 100, out rows, out pixels);
        Check(rows == 0 && pixels == -HexScroll.AutoScrollSteps(20, 8) * 8 * 3, "left of it: left, by three characters");
        s.Layout.AutoScrollStep(783 + 9, 100, out _, out pixels);
        Equal(pixels, HexScroll.AutoScrollSteps(10, 8) * 8 * 3, "right of it: right");

        Console.WriteLine("-- the wheel");
        Equal(s.Layout.WheelRows(120), 3, "a notch is three rows");
        Equal(s.Layout.WheelRows(-240), -6, "two notches down");
        Check(s.Layout.WheelRows(40) == 0 && s.Layout.WheelRows(40) == 0 && s.Layout.WheelRows(40) == 3, "the small steps of a touchpad add up to a notch");
    }

    private static void Scrolling()
    {
        Console.WriteLine("-- the scroll that keeps the caret in view");
        Setup s = new Setup();
        int visible = s.Layout.VisibleRows;
        Equal(s.Layout.ClampFirstRow(-4), 0, "the first row is not before the start");
        Equal(s.Layout.ClampFirstRow(1000), 63 - visible, "nor past the rows that fill the table");
        s.Model.CaretOffset = 500;
        s.Layout.EnsureVisible();
        Equal(s.Layout.FirstRow, 500 / 16 - visible + 1, "a caret below the view: its row becomes the last one");
        s.Model.CaretOffset = 100;
        s.Layout.EnsureVisible();
        Equal(s.Layout.FirstRow, 6, "a caret above the view: its row becomes the first one");
        s.Model.CaretOffset = 1000;
        s.Layout.EnsureVisible();
        Equal(s.Layout.FirstRow, 63 - visible, "after the last byte: the last rows");
        Equal(s.Layout.CaretX(), s.Layout.HexCellX(1000 % 16), "the caret after the last byte stands in the column after it");
        s.Model.Area = HexEditArea.Ascii;
        s.Model.CaretOffset = 37;
        Equal(s.Layout.CaretX(), s.Layout.AsciiStart + 5 * 8, "in the characters");

        s.View.ClientWidth = 300;
        s.Model.Area = HexEditArea.Hex;
        s.Model.CaretOffset = 15;
        s.Layout.EnsureVisible();
        Equal(s.Layout.ScrollX, s.Layout.HexCellX(15) + 16 - 300 + 6, "a caret right of the view: scrolled so that it ends a margin from the edge");
        s.Model.CaretOffset = 16;
        s.Layout.EnsureVisible();
        Equal(s.Layout.ScrollX, 86 - 6, "a caret left of the view: a margin before it");
        s.Model.CaretOffset = 17;
        int before = s.Layout.ScrollX;
        s.Layout.EnsureVisible();
        Equal(s.Layout.ScrollX, before, "a caret in view: no scroll");

        Console.WriteLine("-- the scroll bars");
        s = new Setup(10);
        s.Layout.ChooseScrollBars(out bool vertical, out bool horizontal);
        Check(!vertical && !horizontal, "a few bytes in a wide window: no scroll bars");
        s = new Setup();
        s.Layout.ChooseScrollBars(out vertical, out horizontal);
        Check(vertical && !horizontal, "many rows: the vertical one");
        s = new Setup();
        s.View.ClientWidth = 621 + 10;
        s.Layout.ChooseScrollBars(out vertical, out horizontal);
        Check(vertical && horizontal, "the rows need the vertical bar, which leaves too little width: the horizontal one too");
        s = new Setup(10);
        s.View.ClientWidth = 400;
        s.Layout.ChooseScrollBars(out vertical, out horizontal);
        Check(!vertical && horizontal, "a narrow window: the horizontal one");
        // The rows fit without the horizontal bar and not with it, and then the vertical bar takes room too.
        s = new Setup(16 * ((300 - 22 - 6) / 18) - 1);
        s.View.ClientWidth = 621;
        s.Layout.ChooseScrollBars(out vertical, out horizontal);
        Check(!vertical && !horizontal, "the content fits exactly");
        s.View.ClientWidth = 620;
        s.Layout.ChooseScrollBars(out vertical, out horizontal);
        Check(vertical && horizontal, "one pixel less: the horizontal bar, which leaves too few rows, so the vertical one as well");
        s.View.InspectorWidth = 1;
        s.View.ClientWidth = 622;
        s.Layout.ChooseScrollBars(out vertical, out horizontal);
        Check(!horizontal, "the inspector takes its width from the room");
    }

    private static void Keys()
    {
        Console.WriteLine("-- the keys");
        Check(HexKeyMap.Map(VirtualKey.A, true, false, false, out _) == HexKeyAction.SelectAll && HexKeyMap.Map(VirtualKey.C, true, false, false, out _) == HexKeyAction.Copy
            && HexKeyMap.Map(VirtualKey.X, true, false, false, out _) == HexKeyAction.Cut && HexKeyMap.Map(VirtualKey.V, true, false, false, out _) == HexKeyAction.Paste, "Ctrl+A, C, X, V");
        Check(HexKeyMap.Map(VirtualKey.Z, true, false, false, out _) == HexKeyAction.Undo && HexKeyMap.Map(VirtualKey.Z, true, true, false, out _) == HexKeyAction.Redo
            && HexKeyMap.Map(VirtualKey.Y, true, false, false, out _) == HexKeyAction.Redo, "Ctrl+Z, Ctrl+Shift+Z, Ctrl+Y");
        Check(HexKeyMap.Map(VirtualKey.F, true, false, false, out _) == HexKeyAction.Find && HexKeyMap.Map(VirtualKey.G, true, false, false, out _) == HexKeyAction.GoToOffset
            && HexKeyMap.Map(VirtualKey.I, true, false, false, out _) == HexKeyAction.ToggleDataInspector && HexKeyMap.Map(VirtualKey.S, true, false, false, out _) == HexKeyAction.Save, "Ctrl+F, G, I, S");
        Check(HexKeyMap.Map(VirtualKey.Home, true, true, false, out bool extend) == HexKeyAction.DocumentStart && extend, "Ctrl+Shift+Home: the start, extending the selection");
        Check(HexKeyMap.Map(VirtualKey.End, true, false, false, out extend) == HexKeyAction.DocumentEnd && !extend, "Ctrl+End");
        Equal(HexKeyMap.Map(VirtualKey.Left, true, false, false, out _), HexKeyAction.None, "Ctrl with another key is not the editor's");
        Equal(HexKeyMap.Map(VirtualKey.A, true, false, true, out _), HexKeyAction.None, "Ctrl+Alt+A (AltGr) is not Ctrl+A");
        Equal(HexKeyMap.Map(VirtualKey.Left, true, false, true, out _), HexKeyAction.Left, "Ctrl+Alt+Left moves like Left");
        Check(HexKeyMap.Map(VirtualKey.Right, false, true, false, out extend) == HexKeyAction.Right && extend, "Shift+Right extends");
        Check(HexKeyMap.Map(VirtualKey.Right, false, true, true, out extend) == HexKeyAction.Right && !extend, "Shift with Alt does not");
        Check(HexKeyMap.Map(VirtualKey.Up, false, false, false, out _) == HexKeyAction.Up && HexKeyMap.Map(VirtualKey.Down, false, false, false, out _) == HexKeyAction.Down
            && HexKeyMap.Map(VirtualKey.PageUp, false, false, false, out _) == HexKeyAction.PageUp && HexKeyMap.Map(VirtualKey.PageDown, false, false, false, out _) == HexKeyAction.PageDown
            && HexKeyMap.Map(VirtualKey.Home, false, false, false, out _) == HexKeyAction.RowStart && HexKeyMap.Map(VirtualKey.End, false, false, false, out _) == HexKeyAction.RowEnd, "the arrows, the page keys, Home and End");
        Check(HexKeyMap.Map(VirtualKey.Back, false, false, false, out _) == HexKeyAction.Backspace && HexKeyMap.Map(VirtualKey.Delete, false, false, false, out _) == HexKeyAction.Delete
            && HexKeyMap.Map(VirtualKey.Insert, false, false, false, out _) == HexKeyAction.ToggleOverwrite, "Backspace, Delete, Insert");
        Check(HexKeyMap.Map(VirtualKey.F3, false, false, false, out _) == HexKeyAction.FindNext && HexKeyMap.Map(VirtualKey.F3, false, true, false, out _) == HexKeyAction.FindPrevious, "F3 and Shift+F3");
        Check(HexKeyMap.Map(VirtualKey.F2, false, false, false, out _) == HexKeyAction.NextHighlight && HexKeyMap.Map(VirtualKey.F2, false, true, false, out _) == HexKeyAction.PreviousHighlight, "F2 and Shift+F2");
        Equal(HexKeyMap.Map(VirtualKey.A, false, false, false, out _), HexKeyAction.None, "a letter is typed, not a command");

        Console.WriteLine("-- what the keys do to the caret and the bytes");
        Setup s = new Setup();
        s.Model.CaretOffset = 40;
        Check(HexKeyMap.Apply(s.Model, HexKeyAction.PageDown, false, 12), "a page key is the model's");
        Equal(s.Model.CaretOffset, 40 + 11 * 16, "a page down is the visible rows less one");
        HexKeyMap.Apply(s.Model, HexKeyAction.PageUp, true, 12);
        Check(s.Model.CaretOffset == 40 && s.Model.SelectionStart == 40 && s.Model.SelectionEnd == 216, "a page up with Shift selects");
        s.Model.Anchor = -1;
        HexKeyMap.Apply(s.Model, HexKeyAction.PageDown, false, 1);
        Equal(s.Model.CaretOffset, 56, "a view of one row: a page is one row");
        s.Model.Nibble = 1;
        HexKeyMap.Apply(s.Model, HexKeyAction.RowEnd, false, 12);
        Check(s.Model.CaretOffset == 63 && s.Model.Nibble == 1, "End: the last byte of the row, on the same digit");
        HexKeyMap.Apply(s.Model, HexKeyAction.RowStart, false, 12);
        Check(s.Model.CaretOffset == 48 && s.Model.Nibble == 1, "Home: the first byte of the row");
        HexKeyMap.Apply(s.Model, HexKeyAction.DocumentEnd, false, 12);
        Check(s.Model.CaretOffset == 1000 && s.Model.Nibble == 0, "Ctrl+End: after the last byte, on the high digit");
        HexKeyMap.Apply(s.Model, HexKeyAction.RowEnd, false, 12);
        Equal(s.Model.CaretOffset, 1000, "End in the row after the last byte stays at the end");
        HexKeyMap.Apply(s.Model, HexKeyAction.DocumentStart, false, 12);
        Equal(s.Model.CaretOffset, 0, "Ctrl+Home");
        HexKeyMap.Apply(s.Model, HexKeyAction.Right, false, 12);
        HexKeyMap.Apply(s.Model, HexKeyAction.Down, false, 12);
        HexKeyMap.Apply(s.Model, HexKeyAction.Up, false, 12);
        HexKeyMap.Apply(s.Model, HexKeyAction.Left, false, 12);
        Check(s.Model.CaretOffset == 0 && s.Model.Nibble == 0, "the arrows move and come back");
        s.Model.CaretOffset = 5;
        HexKeyMap.Apply(s.Model, HexKeyAction.Delete, false, 12);
        Equal(s.Document.Length, 999, "Delete removes the byte at the caret");
        HexKeyMap.Apply(s.Model, HexKeyAction.Backspace, false, 12);
        Equal(s.Document.Length, 998, "Backspace the one before it");
        foreach (HexKeyAction action in new[] { HexKeyAction.Copy, HexKeyAction.Find, HexKeyAction.ToggleOverwrite, HexKeyAction.FindNext, HexKeyAction.NextHighlight, HexKeyAction.Undo, HexKeyAction.None })
        {
            Check(!HexKeyMap.Apply(s.Model, action, false, 12), action + " is the view's");
        }
    }

    private static void Pointer()
    {
        Console.WriteLine("-- the selection made with the mouse");
        Setup s = new Setup();
        HexPointer pointer = new HexPointer(s.Model);
        s.Model.CaretOffset = 3;
        s.Changes = 0;
        Equal(pointer.Press(HexPointerButton.Left, 40, HexEditArea.Ascii, 0, false), HexPressResult.StartedDrag, "a left press starts a drag");
        Check(pointer.Dragging && s.Model.CaretOffset == 40 && s.Model.Area == HexEditArea.Ascii && !s.Model.HasSelection && s.Changes == 1, "the caret goes there, nothing is selected, the change is told");
        pointer.DragTo(60, HexEditArea.Hex);
        Check(s.Model.SelectionStart == 40 && s.Model.SelectionEnd == 60 && s.Model.Area == HexEditArea.Hex, "a drag selects from the press");
        pointer.DragTo(40, HexEditArea.Hex);
        Check(!s.Model.HasSelection && s.Model.Anchor == -1, "back to the press: nothing selected");
        pointer.DragTo(20, HexEditArea.Hex);
        Check(s.Model.SelectionStart == 20 && s.Model.SelectionEnd == 40, "and before it");
        pointer.Release();
        Check(!pointer.Dragging, "the release ends the drag");

        pointer.Press(HexPointerButton.Left, 70, HexEditArea.Hex, 1, true);
        Check(s.Model.SelectionStart == 40 && s.Model.SelectionEnd == 70 && s.Model.Nibble == 1, "Shift+press extends from the anchor");
        pointer.DragTo(80, HexEditArea.Hex);
        Equal(s.Model.SelectionStart, 40, "and the drag keeps that anchor");
        pointer.Release();
        s.Model.Anchor = -1;
        s.Model.CaretOffset = 10;
        pointer.Press(HexPointerButton.Left, 15, HexEditArea.Hex, 0, true);
        Check(s.Model.SelectionStart == 10 && s.Model.SelectionEnd == 15, "Shift+press without a selection extends from the caret");
        pointer.Release();

        s.Model.SecondNibblePending = true;
        s.Changes = 0;
        Equal(pointer.Press(HexPointerButton.Right, 12, HexEditArea.Ascii, 1, false), HexPressResult.KeptSelection, "a right press in the selection keeps it");
        Check(s.Model.SelectionStart == 10 && s.Model.SelectionEnd == 15 && s.Model.Area == HexEditArea.Ascii && s.Model.Nibble == 0 && !s.Model.SecondNibblePending && s.Changes == 0,
            "only the column changes, a typed half byte is ended, nothing is told");
        Equal(pointer.Press(HexPointerButton.Right, 15, HexEditArea.Hex, 1, false), HexPressResult.MovedCaret, "at the end of the selection it is outside");
        Check(!s.Model.HasSelection && s.Model.CaretOffset == 15 && s.Model.Nibble == 1 && !pointer.Dragging, "the caret goes there without a selection");
        pointer.Press(HexPointerButton.Left, 30, HexEditArea.Hex, 0, false);
        pointer.Press(HexPointerButton.Right, 50, HexEditArea.Hex, 0, false);
        Check(!pointer.Dragging, "a right press ends a drag");
        int caret = s.Model.CaretOffset;
        Equal(pointer.Press(HexPointerButton.Other, 3, HexEditArea.Ascii, 1, false), HexPressResult.None, "another button does nothing");
        Check(s.Model.CaretOffset == caret && s.Model.Area == HexEditArea.Hex, "and changes nothing");
    }

    private static void Text()
    {
        Console.WriteLine("-- the tool tip of a byte");
        HexDocument document = new HexDocument(Bytes(1000));
        Equal(HexByteText.Describe(document, 41, 1, false), "Offset 0x00000029 (41)\nByte 0x1A = 26", "single bytes: the offset and the value");
        Equal(HexByteText.Describe(document, 41, 4, false), "Offset 0x00000029 (41)\nByte 0x1A = 26\n32-bit word, little endian: 0x34271A0D = 874977805", "groups: the word of the group");
        Equal(HexByteText.Describe(document, 41, 4, true), "Offset 0x00000029 (41)\nByte 0x1A = 26\n32-bit word, big endian: 0x0D1A2734 = 219817780", "in the other byte order");
        Check(!HexByteText.Describe(new HexDocument(Bytes(1001)), 1000, 4, false).Contains("word") && HexByteText.Describe(document, 999, 8, false).Contains("64-bit word"), "a group that the end cuts has no word, a group that ends with the bytes has");
        document.ReplaceByte(66, 0x41);
        document.AddHighlight(64, 2, unchecked((int)0xFF0AC81E), "header");
        Equal(HexByteText.Describe(document, 65, 1, false), "Offset 0x00000041 (65)\nByte 0x52 = 82 = 'R'\nheader: 0x00000040 - 0x00000041, 2 bytes (0x2)", "a printable byte in a named block");
        document.AddHighlight(66, 1, unchecked((int)0xFF0AC81E), "");
        Equal(HexByteText.Describe(document, 66, 1, false), "Offset 0x00000042 (66)\nByte 0x41 = 65 = 'A'\nMarked: 0x00000042 - 0x00000042, 1 byte (0x1)\nChanged, not saved yet", "a changed byte in a block without a name");

        Console.WriteLine("-- the colours of the bytes");
        RgbColor dark = RgbColor.FromArgb(30, 30, 30);
        RgbColor light = RgbColor.FromArgb(255, 255, 255);
        RgbColor text = RgbColor.FromArgb(220, 220, 220);
        Equal(HexByteText.TextColor(0x41, true, dark, text), text, "a printable byte has the colour of the text");
        Equal(HexByteText.TextColor(0x00, false, dark, text), text, "without colours by kind every byte has it");
        Equal(HexByteText.TextColor(0x00, true, dark, text), RgbColor.FromArgb(106, 106, 106), "zero is dim");
        Equal(HexByteText.TextColor(0xFF, true, dark, text), RgbColor.FromArgb(255, 120, 120), "0xFF is red on dark");
        Equal(HexByteText.TextColor(0xFF, true, light, text), RgbColor.FromArgb(190, 30, 30), "and on light");
        Equal(HexByteText.TextColor(0x1F, true, dark, text), RgbColor.FromArgb(110, 210, 140), "a control character is green");
        Equal(HexByteText.TextColor(0x7F, true, light, text), RgbColor.FromArgb(0, 125, 60), "so is 0x7F");
        Equal(HexByteText.TextColor(0x80, true, dark, text), RgbColor.FromArgb(125, 175, 255), "a byte above 0x7F is blue");
        Equal(HexByteText.TextColor(0xC3, true, light, text), RgbColor.FromArgb(30, 80, 200), "on light too");
        Check(HexByteText.IsDark(RgbColor.FromArgb(127, 127, 127)) && !HexByteText.IsDark(RgbColor.FromArgb(128, 127, 127)), "dark is a lightness below one half");
        Check(!HexByteText.IsDark(RgbColor.FromArgb(255, 0, 0)), "a pure colour has a lightness of one half");
        Equal(HexByteText.Blend(RgbColor.FromArgb(0, 100, 200), RgbColor.FromArgb(200, 100, 0), 0.25f), RgbColor.FromArgb(50, 100, 150), "a blend");
        Equal(HexByteText.Blend(dark, text, 2f), text, "a share above one is the foreground");
        Equal(HexByteText.Blend(dark, text, -1f), dark, "a share below zero the background");
        Equal(HexByteText.Blend(RgbColor.FromArgb(10, 0, 0, 0), text, 0f).A, (byte)255, "a blend is opaque");
    }

    private static int Main()
    {
        Metrics();
        Sizes();
        HitTests();
        Table();
        Scrolling();
        Keys();
        Pointer();
        Text();
        Console.WriteLine();
        Console.WriteLine("HexViewLayoutTests: all " + _passed + " checks passed.");
        return 0;
    }
}
