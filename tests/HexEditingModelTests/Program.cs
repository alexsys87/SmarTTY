using System;
using System.Collections.Generic;
using System.Text;
using SmarTTY.TextEditing;

// Checks of TextEditing/HexEditingModel.cs, the editing of the bytes of the hex editor (the caret, the selection, typing in both columns, the search),
// without a control: above all what it tells its view after each operation (the follow-up the old AfterContentChange and AfterCaretChange did), which is
// the contract the control relies on. tests/HexEditorControlTests drives the real control with 3608 steps against a golden file; this is the part of that
// that needs no Mono.
// Run with: dotnet run --project tests/HexEditingModelTests
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

    private static byte[] Bytes(params int[] values)
    {
        byte[] bytes = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i] = (byte)values[i];
        }
        return bytes;
    }

    private static string Hex(HexEditingModel model)
    {
        return HexDocument.FormatHex(model.Document.ToArray());
    }

    private sealed class Fixture
    {
        public readonly HexEditingModel Model;

        public readonly List<HexChangeEffects> Effects = new List<HexChangeEffects>();

        public Fixture(params int[] bytes)
        {
            Model = new HexEditingModel(new HexDocument(Bytes(bytes)));
            Model.Changed += effects => Effects.Add(effects);
        }

        public string Told()
        {
            string text = string.Join(",", Effects);
            Effects.Clear();
            return text.Length == 0 ? "nothing" : text;
        }
    }

    private const HexChangeEffects Show = HexChangeEffects.ShowCaret;

    private static void WhatTheViewIsTold()
    {
        Fixture f = new Fixture(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20);
        HexEditingModel m = f.Model;

        m.MoveHorizontally(left: false, extendSelection: false);
        Equal(f.Told(), "ShowCaret, RestartCaretBlink, EnsureCaretVisible", "a movement shows the caret, restarts the blink and scrolls to it");
        Equal(m.Nibble, 1, "Right in the hex column goes to the second digit");
        m.MoveHorizontally(left: false, extendSelection: false);
        Equal(m.CaretOffset, 1, "and then to the next byte");
        Equal(m.Nibble, 0, "at its first digit");
        f.Told();

        m.GoToOffset(5);
        Equal(f.Told(), "EnsureCaretVisible", "go to scrolls only");
        m.SelectAll();
        Equal(f.Told(), "EnsureCaretVisible", "select all scrolls only");
        Check(m.HasSelection && m.SelectionStart == 0 && m.SelectionEnd == 20, "select all selects the whole document");

        m.GoToOffset(0);
        f.Told();
        m.ToggleOverwriteMode();
        Equal(f.Told(), "None", "toggling the mode is told as a change with no follow-up of its own (the view redraws)");
        Check(!m.Overwrite, "the mode is changed");
        m.ToggleOverwriteMode();
        f.Effects.Clear();

        m.Undo();
        Equal(f.Told(), "ShowCaret, EnsureCaretVisible", "undo is told like a change of the bytes, also when there was nothing to undo");
    }

    private static void Typing()
    {
        Fixture f = new Fixture(0x00, 0x00, 0x00, 0x00);
        HexEditingModel m = f.Model;
        Check(m.TypeCharacter('A'), "a hex digit is used in the hex column");
        Equal(Hex(m), "A0 00 00 00", "the first digit is the high nibble");
        Equal(f.Told(), "ShowCaret", "the first digit shows the caret, and does not scroll");
        Equal(m.Nibble, 1, "and waits for the second");
        Check(m.TypeCharacter('b'), "the second digit");
        Equal(Hex(m), "AB 00 00 00", "completes the byte");
        Equal(f.Told(), "ShowCaret, EnsureCaretVisible", "the second digit scrolls too");
        Equal(m.CaretOffset, 1, "and the caret goes to the next byte");
        Check(!m.TypeCharacter('z'), "a letter that is no digit is not used in the hex column");
        Equal(f.Told(), "nothing", "and nothing is told");
        Check(!m.TypeCharacter('\t'), "a control character is not used");

        // The two digits of a byte are one step of the undo history.
        m.Undo();
        Equal(Hex(m), "00 00 00 00", "both digits are undone by one undo");
        f.Told();

        m.ReadOnly = true;
        Check(!m.TypeCharacter('1'), "nothing is typed in a read-only editor");
        m.ReadOnly = false;

        // The insert mode.
        Fixture g = new Fixture(0xAA, 0xBB);
        g.Model.Overwrite = false;
        g.Model.TypeCharacter('1');
        g.Model.TypeCharacter('2');
        Equal(Hex(g.Model), "12 AA BB", "in the insert mode a typed byte is added");

        // The ASCII column.
        Fixture a = new Fixture(0x41, 0x42, 0x43);
        a.Model.Area = HexEditArea.Ascii;
        Check(a.Model.TypeCharacter('z'), "a character is used in the ASCII column");
        Equal(Hex(a.Model), "7A 42 43", "it replaces the byte under the caret");
        Equal(a.Told(), "ShowCaret, EnsureCaretVisible", "and shows the caret and scrolls");
        Check(a.Model.TypeCharacter('é'), "a letter with an accent");
        Equal(Hex(a.Model), "7A C3 A9", "is stored as two UTF-8 bytes (the second overwrites the third)");
        Equal(a.Model.CaretOffset, 3, "and the caret is after them");

        Fixture s = new Fixture(0, 0, 0, 0, 0, 0);
        s.Model.Area = HexEditArea.Ascii;
        Check(s.Model.TypeCharacter('\uD83D'), "the first half of a surrogate pair is taken");
        Equal(Hex(s.Model), "00 00 00 00 00 00", "and nothing is written yet");
        Check(s.Model.TypeCharacter('\uDE00'), "the second half");
        Equal(Hex(s.Model), "F0 9F 98 80 00 00", "joins it: four UTF-8 bytes");
        Check(s.Model.TypeCharacter('\uDE00'), "a second half with no first half is taken");
        Equal(Hex(s.Model), "F0 9F 98 80 00 00", "and ignored");
    }

    private static void Selection()
    {
        Fixture f = new Fixture(1, 2, 3, 4, 5, 6, 7, 8);
        HexEditingModel m = f.Model;
        m.GoToOffset(2);
        m.MoveHorizontally(left: false, extendSelection: true);
        m.MoveHorizontally(left: false, extendSelection: true);
        Check(m.HasSelection, "Shift+Right selects");
        Equal(m.SelectionStart + "-" + m.SelectionEnd, "2-3", "by a byte after two nibbles");
        m.MoveHorizontally(left: true, extendSelection: true);
        m.MoveHorizontally(left: true, extendSelection: true);
        Check(!m.HasSelection, "going back to where it began leaves no selection");
        Equal(m.Anchor, -1, "the anchor is cleared");

        m.MoveTo(10, extendSelection: false, nibble: 1);
        Equal(m.CaretOffset, 8, "a move is clamped to the document");
        Equal(m.Nibble, 1, "and keeps the nibble that was asked for");
        m.BytesPerRow = 4;
        m.MoveTo(0, false, 0);
        m.MoveVertically(1, extendSelection: false);
        Equal(m.CaretOffset, 4, "down is a row of the bytes per row");
        m.MoveVertically(-5, extendSelection: false);
        Equal(m.CaretOffset, 0, "up stops at the first row");

        // Typing over a selection: insert mode replaces it, overwrite mode only drops it.
        Fixture o = new Fixture(1, 2, 3, 4, 5);
        o.Model.MoveTo(1, false, 0);
        o.Model.MoveTo(3, true, 0);
        o.Model.TypeCharacter('F');
        Equal(Hex(o.Model), "01 F2 03 04 05", "overwrite mode: the length does not change, the first byte of the selection is overwritten");
        Fixture n = new Fixture(1, 2, 3, 4, 5);
        n.Model.Overwrite = false;
        n.Model.MoveTo(1, false, 0);
        n.Model.MoveTo(3, true, 0);
        n.Model.TypeCharacter('F');
        Equal(Hex(n.Model), "01 F0 04 05", "insert mode: the selection is replaced by the typed byte");
    }

    private static void Editing()
    {
        Fixture f = new Fixture(1, 2, 3, 4, 5, 6);
        HexEditingModel m = f.Model;
        m.MoveTo(2, false, 0);
        m.Backspace();
        Equal(Hex(m), "01 03 04 05 06", "Backspace removes the byte before the caret");
        Equal(m.CaretOffset, 1, "and the caret goes back");
        Equal(f.Told().Contains("ShowCaret") ? "yes" : "no", "yes", "and tells the view");
        m.DeleteCurrentSelectionOrByte();
        Equal(Hex(m), "01 04 05 06", "Delete removes the byte under the caret");
        m.MoveTo(1, false, 0);
        m.MoveTo(3, true, 0);
        m.DeleteCurrentSelectionOrByte();
        Equal(Hex(m), "01 06", "Delete removes the selection");
        m.MoveTo(2, false, 0);
        f.Told();
        m.DeleteCurrentSelectionOrByte();
        Equal(f.Told(), "nothing", "Delete at the end of the file does nothing and tells nothing");

        m.ReadOnly = true;
        m.Backspace();
        m.DeleteCurrentSelectionOrByte();
        Equal(Hex(m), "01 06", "a read-only editor does not delete");
        Check(!m.FillSelection(Bytes(1)) && !m.InsertPattern(2, Bytes(1)), "and does not fill or insert a pattern");
        m.WriteValue(HexValueType.UInt8, "1", out string error);
        Equal(error, "The file cannot be changed.", "and says why");
        m.ReadOnly = false;

        m.InsertBytes(Bytes(9, 9));
        Equal(Hex(m), "01 06 09 09", "bytes pasted at the end are added");
        m.MoveTo(0, false, 0);
        m.InsertBytes(Bytes(7));
        Equal(Hex(m), "07 06 09 09", "in the overwrite mode they replace the bytes under the caret");
        m.Overwrite = false;
        m.InsertBytes(Bytes(1, 2));
        Equal(Hex(m), "07 01 02 06 09 09", "in the insert mode they are added");
    }

    private static void Commands()
    {
        Fixture f = new Fixture(1, 2, 3, 4, 5, 6, 7, 8);
        HexEditingModel m = f.Model;
        Check(!m.FillSelection(Bytes(0xFF)), "fill needs a selection");
        m.MoveTo(2, false, 0);
        m.MoveTo(6, true, 0);
        Check(m.FillSelection(Bytes(0xAB, 0xCD)), "fill repeats a pattern over the selection");
        Equal(Hex(m), "01 02 AB CD AB CD 07 08", "the pattern is cut at the end");
        Equal(m.SelectionStart + "-" + m.SelectionEnd, "2-6", "and the filled bytes stay selected");

        Check(m.SwapSelectionByteOrder(2, out string error), "swap reverses the words of the selection");
        Equal(Hex(m), "01 02 CD AB CD AB 07 08", "each word");
        Check(m.SwapSelectionByteOrder(4, out error), "a selection of 4 bytes is one word of 4");
        Equal(Hex(m), "01 02 AB CD AB CD 07 08", "which is reversed as a whole");
        m.MoveTo(2, false, 0);
        m.MoveTo(5, true, 0);
        Check(!m.SwapSelectionByteOrder(2, out error), "a selection that is not whole words is refused");
        Equal(error, "Select whole 16-bit words: a multiple of 2 bytes.", "with this message");

        m.MoveTo(0, false, 0);
        Check(m.InsertPattern(3, Bytes(0)), "insert pattern puts bytes in at the caret");
        Equal(Hex(m), "00 00 00 01 02 AB CD AB CD 07 08", "in front of the rest");
        Equal(m.SelectionStart + "-" + m.SelectionEnd, "0-3", "and selects them");

        Fixture w = new Fixture(0, 0, 0, 0);
        Check(w.Model.WriteValue(HexValueType.UInt16, "4660", out error), "write value writes a number");
        Equal(Hex(w.Model), "34 12 00 00", "little endian");
        w.Model.BigEndian = true;
        w.Model.GoToOffset(2);
        w.Model.WriteValue(HexValueType.UInt16, "4660", out error);
        Equal(Hex(w.Model), "34 12 12 34", "big endian when the editor is in that mode");
        Check(!w.Model.WriteValue(HexValueType.UInt8, "zzz", out error) && error != null, "a text that is no number is refused with a message");
        w.Model.GoToOffset(3);
        w.Model.WriteValue(HexValueType.UInt32, "1", out error);
        Equal(w.Model.Document.Length, 7, "a value that goes past the end adds bytes");
        Equal(HexDocument.FormatHex(w.Model.GetBytesAtCaret(100)), "00 00 00 01", "bytes at the caret are what is left");

        Equal(HexEditingModel.FormatByteCount(1), "1 byte (0x1)", "the count of one byte");
        Equal(HexEditingModel.FormatByteCount(4096), "4,096 bytes (0x1000)", "the count of many");
        Fixture c = new Fixture(1, 2, 3);
        byte[] all = c.Model.GetChecksumBytes(out string description);
        Equal(all.Length + " " + description, "3 The whole file, 3 bytes (0x3)", "the bytes of the checksums without a selection");
        c.Model.MoveTo(1, false, 0);
        c.Model.MoveTo(3, true, 0);
        c.Model.GetChecksumBytes(out description);
        Equal(description, "Selection 0x00000001 - 0x00000002, 2 bytes (0x2)", "and with one");
        Equal(HexDocument.FormatHex(c.Model.GetSelectedBytes()), "02 03", "the selected bytes");
        c.Model.MoveTo(3, false, 0);
        Equal(c.Model.GetSelectedBytes().Length, 0, "none at the end");
    }

    private static void Searching()
    {
        Fixture f = new Fixture(1, 2, 3, 1, 2, 3, 9, 1, 2, 3);
        HexEditingModel m = f.Model;
        Equal(m.Search(null, true, false, true), HexEditingModel.SearchSkipped, "no pattern: nothing is searched");
        Equal(m.Search(Bytes(), true, false, true), HexEditingModel.SearchSkipped, "an empty pattern too");
        Check(m.LastPattern == null, "and it is not remembered");
        f.Told();
        Equal(m.Search(Bytes(1, 2, 3), true, false, true), 0, "a match at the caret is found by a forward search");
        Equal(f.Told(), "EnsureCaretVisible", "and the view is told to scroll");
        Equal(m.SelectionStart + "-" + m.SelectionEnd, "0-3", "the match is selected");
        Equal(m.Search(Bytes(1, 2, 3), true, false, true), 3, "the next search starts after the selection");
        Equal(m.Search(Bytes(1, 2, 3), false, false, true), 0, "a backward search starts before it");
        Equal(m.Search(Bytes(1, 2, 3), false, false, true), 7, "and wraps from the end");
        Equal(m.Search(Bytes(1, 2, 3), false, false, false), 3, "without wrapping it goes on backwards");
        m.GoToOffset(0);
        m.Search(Bytes(1, 2, 3), false, false, false);
        f.Told();
        Equal(m.Search(Bytes(8), true, false, true), HexEditingModel.NotFound, "a pattern that is not there");
        Equal(f.Told(), "nothing", "tells the view nothing");
        Equal(m.LastSearchText, "08", "but is remembered for F3");
        Check(m.LastWrap && !m.LastIgnoreCase, "with its options");

        Fixture c = new Fixture('a', 'B', 'c');
        Equal(c.Model.Search(Bytes('b'), true, false, true), HexEditingModel.NotFound, "a search is case sensitive");
        Equal(c.Model.Search(Bytes('b'), true, true, true), 1, "unless asked not to");
    }

    private static void Highlights()
    {
        Fixture f = new Fixture(1, 2, 3, 4, 5, 6);
        HexEditingModel m = f.Model;
        Check(m.HighlightSelection(unchecked((int)0xFFFF0000), "  red  "), "the byte under the caret can be marked");
        Equal(m.Document.GetHighlightAt(0).Label, "red", "with a label without its blanks");
        m.GoToOffset(6);
        Check(!m.HighlightSelection(0, ""), "not past the end");
        m.MoveTo(2, false, 0);
        m.MoveTo(4, true, 0);
        m.HighlightSelection(unchecked((int)0xFF00FF00), null);
        Check(m.GoToHighlight(forward: false), "the previous block is found");
        Equal(m.SelectionStart + "-" + m.SelectionEnd, "0-1", "and selected");
        Check(m.GoToHighlight(forward: true), "the next block is found");
        Equal(m.SelectionStart + "-" + m.SelectionEnd, "2-4", "and selected");
        Check(m.GoToHighlight(forward: true), "after the last block the search goes on from the first");
        Equal(m.SelectionStart + "-" + m.SelectionEnd, "0-1", "which is selected");
        m.ClearHighlights();
        Check(!m.GoToHighlight(forward: true), "with no block at all there is nothing to go to (the view beeps)");
        m.HighlightSelection(unchecked((int)0xFF00FF00), null);
        Check(m.RemoveHighlight(), "the colour can be removed");
        m.ClearHighlights();
        Check(m.Document.Highlights.Count == 0, "or all of them");
    }

    private static int Main()
    {
        try
        {
            WhatTheViewIsTold();
            Typing();
            Selection();
            Editing();
            Commands();
            Searching();
            Highlights();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " hex editing model checks passed.");
        return 0;
    }
}
