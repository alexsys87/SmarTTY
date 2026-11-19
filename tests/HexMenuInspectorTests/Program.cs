using System;
using System.Collections.Generic;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

// Checks of the parts of the hex view that stage 3B-3 took out of the WinForms controls so that the WPF view of the editor tab shows the same:
// CodeEditing/HexMenuModel.cs (the submenus of the hex view: their items, keys, swatches, when an item is enabled or checked and what it does), with a
// fake hex view that logs what it is asked, and TextEditing/HexInspectorModel.cs (the data inspector: its rows and texts, the text an edit starts
// with, Enter and Esc, the message and when it goes, the keys, the clicks and where the rows are), on the real HexEditingModel. The same code is run
// in the real WinForms menu and inspector by tests/HexEditorControlTests (the "menu" and "inspector" runs of its golden file).
// Run with: dotnet run --project tests/HexMenuInspectorTests
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

    // A hex view that holds what the menu reads and logs what it is asked to do.
    private sealed class FakeView : IHexEditorView
    {
        public readonly List<string> Log = new List<string>();

        public bool SwapResult = true;

        public Exception Throw;

        public HexDocument Document { get; set; }

        public bool ReadOnly { get; set; }

        public int CaretOffset { get; set; }

        public int Length { get; set; } = 100;

        public bool InsertMode { get; set; }

        public int SelectionLength { get; set; }

        public bool BigEndian { get; set; }

        public int GroupSize { get; set; } = 1;

        public int BytesPerRow { get; set; } = 16;

        public bool ShowDataInspector { get; set; }

        public bool ColorByValue { get; set; }

        public bool Focused { get; set; }

        public bool HasSelection { get; set; }

        public int SelectionStart { get; set; }

        public bool CanUndo { get; set; }

        public bool CanRedo { get; set; }

        public string LastSearchText { get; set; } = "";

        public HexHighlight HighlightAtCaret { get; set; }

        public bool HasHighlights { get; set; }

        public bool CanHighlight { get; set; }

        public event EventHandler StateChanged { add { } remove { } }

        public event EventHandler SaveRequested { add { } remove { } }

        public event EventHandler FindRequested { add { } remove { } }

        public event EventHandler GoToOffsetRequested { add { } remove { } }

        public event EventHandler ViewOptionsChanged { add { } remove { } }

        private void Did(string what)
        {
            if (Throw != null)
            {
                throw Throw;
            }
            Log.Add(what);
        }

        public bool Focus()
        {
            Did("Focus");
            return true;
        }

        public void Invalidate()
        {
            Did("Invalidate");
        }

        public void Undo() => Did("Undo");

        public void Redo() => Did("Redo");

        public bool CopySelection()
        {
            Did("CopySelection");
            return true;
        }

        public void CopyAll() => Did("CopyAll");

        public void Cut() => Did("Cut");

        public void Paste() => Did("Paste");

        public void DeleteSelection() => Did("DeleteSelection");

        public void SelectAll() => Did("SelectAll");

        public bool CopyAs(HexCopyFormat format)
        {
            Did("CopyAs " + format);
            return true;
        }

        public byte[] GetChecksumBytes(out string description)
        {
            Did("GetChecksumBytes");
            description = "";
            return new byte[0];
        }

        public bool FillSelection(byte[] pattern)
        {
            Did("FillSelection");
            return true;
        }

        public bool InsertPattern(int count, byte[] pattern)
        {
            Did("InsertPattern");
            return true;
        }

        public bool SwapSelectionByteOrder(int size, out string error)
        {
            Did("Swap " + size);
            error = SwapResult ? null : "cannot swap " + size;
            return SwapResult;
        }

        public bool HighlightSelection(RgbColor color, string label)
        {
            Did("Highlight " + color.ToArgb().ToString("x8") + " '" + label + "'");
            return true;
        }

        public bool RemoveHighlight()
        {
            Did("RemoveHighlight");
            return true;
        }

        public void ClearHighlights() => Did("ClearHighlights");

        public bool GoToHighlight(bool forward)
        {
            Did("GoToHighlight " + forward);
            return true;
        }

        public void GoToOffset(int offset) => Did("GoToOffset " + offset);

        public bool Search(byte[] pattern, bool forward, bool ignoreCase, bool wrap)
        {
            Did("Search");
            return true;
        }

        public bool FindNext(bool forward)
        {
            Did("FindNext " + forward);
            return true;
        }

        public void LoadViewOptions(int bytesPerRow, int groupSize, bool bigEndian, bool colorByValue, bool showDataInspector) => Did("LoadViewOptions");
    }

    private sealed class Setup
    {
        public readonly FakeView View = new FakeView();

        public readonly List<string> Dialogs = new List<string>();

        public readonly List<string> Messages = new List<string>();

        public readonly HexMenuModel Menu;

        public Setup()
        {
            Menu = new HexMenuModel(View, kind => Dialogs.Add(kind.ToString()), text => Messages.Add(text));
        }

        public HexSubmenu Submenu(string text)
        {
            foreach (HexSubmenu submenu in Menu.Submenus)
            {
                if (submenu.Text == text)
                {
                    return submenu;
                }
            }
            throw new Exception("no submenu " + text);
        }

        public HexMenuItem Item(string submenu, string text)
        {
            foreach (HexMenuItem item in Submenu(submenu).Items)
            {
                if (item.Text == text)
                {
                    return item;
                }
            }
            throw new Exception("no item " + submenu + " > " + text);
        }

        // The enabled (E) and checked (C) state of the items of a submenu, "-" for a separator.
        public string States(string submenu)
        {
            List<string> states = new List<string>();
            foreach (HexMenuItem item in Submenu(submenu).Items)
            {
                states.Add(item.IsSeparator ? "-" : (item.IsEnabled ? "E" : ".") + (item.IsChecked ? "C" : ""));
            }
            return string.Join(",", states);
        }

        // Runs an item and returns what the view logged, the dialogs and the messages.
        public string Run(string submenu, string text)
        {
            View.Log.Clear();
            Dialogs.Clear();
            Messages.Clear();
            Item(submenu, text).Run();
            return string.Join(";", View.Log) + "|" + string.Join(";", Dialogs) + "|" + string.Join(";", Messages);
        }
    }

    private static string Texts(HexSubmenu submenu)
    {
        List<string> texts = new List<string>();
        foreach (HexMenuItem item in submenu.Items)
        {
            texts.Add(item.IsSeparator ? "-" : item.Text + (item.Shortcut != null ? "{" + item.Shortcut + "}" : ""));
        }
        return string.Join(",", texts);
    }

    private static void MenuStructure()
    {
        Setup setup = new Setup();
        List<string> names = new List<string>();
        foreach (HexSubmenu submenu in setup.Menu.Submenus)
        {
            names.Add(submenu.Text);
        }
        Equal(string.Join(",", names), "Highlight,Copy As,Edit Bytes,Hex View", "four submenus in their order");
        Equal(Texts(setup.Submenu("Highlight")), "Yellow,Green,Blue,Red,Orange,Purple,Color and Label...,-,Remove Highlight,Remove All Highlights,-,Next Highlight{F2},Previous Highlight{Shift+F2}", "the Highlight items with their keys");
        Equal(Texts(setup.Submenu("Copy As")), "Hex (48 65 6C),Hex without spaces (48656C),C array,Python bytes,Base64,Text (UTF-8)", "a Copy As item for each format");
        Equal(Texts(setup.Submenu("Edit Bytes")), "Fill Selection...,Insert Bytes...,-,Swap Byte Order (16-bit Words),Swap Byte Order (32-bit Words),Swap Byte Order (64-bit Words),-,Checksums and Hashes...", "the Edit Bytes items");
        Equal(Texts(setup.Submenu("Hex View")), "Little Endian (x86, ARM),Big Endian (Network Order, MIPS, PowerPC),-,Single Bytes,Group by 16-bit Words,Group by 32-bit Words,Group by 64-bit Words,-,8 Bytes per Row,16 Bytes per Row,32 Bytes per Row,-,Color Bytes by Value", "the Hex View items");
        List<string> swatches = new List<string>();
        foreach (HexMenuItem item in setup.Submenu("Highlight").Items)
        {
            if (item.Swatch.HasValue)
            {
                swatches.Add(item.Text + "=" + item.Swatch.Value.ToArgb().ToString("x8"));
            }
        }
        Equal(string.Join(",", swatches), "Yellow=ffffd600,Green=ff3cc85a,Blue=ff3296ff,Red=ffff5050,Orange=ffff961e,Purple=ffaa6eff", "the six colours have opaque swatches, the other items none");
        Equal(HexViewChoices.HighlightColors.Length, 6, "the colours of the menu are the choices of the view");
        foreach (HexSubmenu submenu in setup.Menu.Submenus)
        {
            foreach (HexMenuItem item in submenu.Items)
            {
                if (item.IsSeparator)
                {
                    Check(!item.IsChecked && item.Shortcut == null && !item.Swatch.HasValue, "a separator in " + submenu.Text + " is nothing else");
                    item.Run();
                }
            }
        }
    }

    private static void EnabledStates()
    {
        Setup setup = new Setup();
        FakeView view = setup.View;
        view.Length = 0;
        view.CanHighlight = false;
        Equal(setup.States("Highlight"), ".,.,.,.,.,.,.,-,.,.,-,.,.", "empty file: nothing to mark, remove or go to");
        Check(!setup.Submenu("Copy As").IsEnabled, "empty file: Copy As is off");
        Equal(setup.States("Edit Bytes"), ".,E,-,.,.,.,-,.", "empty file: only Insert Bytes");
        Check(setup.Submenu("Highlight").IsEnabled && setup.Submenu("Edit Bytes").IsEnabled && setup.Submenu("Hex View").IsEnabled, "the other submenus are always on");

        view.Length = 100;
        view.CaretOffset = 20;
        view.CanHighlight = true;
        Equal(setup.States("Highlight"), "E,E,E,E,E,E,E,-,.,.,-,.,.", "a byte under the caret: it can be marked");
        Check(setup.Submenu("Copy As").IsEnabled, "a byte under the caret: Copy As is on");
        Equal(setup.States("Copy As"), "E,E,E,E,E,E", "the Copy As items themselves are always on");
        Equal(setup.States("Edit Bytes"), ".,E,-,.,.,.,-,E", "no selection: no fill, no swap; checksums of the file");

        view.CaretOffset = 100;
        Check(!setup.Submenu("Copy As").IsEnabled, "the caret after the last byte: Copy As is off");
        view.HasSelection = true;
        Check(setup.Submenu("Copy As").IsEnabled, "a selection: Copy As is on wherever the caret is");
        Equal(setup.States("Edit Bytes"), "E,E,-,E,E,E,-,E", "a selection: fill and swap");

        view.HasHighlights = true;
        Equal(setup.States("Highlight"), "E,E,E,E,E,E,E,-,E,E,-,E,E", "blocks and a selection: remove, remove all, next, previous");
        view.HasSelection = false;
        Equal(setup.States("Highlight"), "E,E,E,E,E,E,E,-,.,E,-,E,E", "blocks, no selection and no block at the caret: no Remove Highlight");
        view.HighlightAtCaret = new HexHighlight(0, 4, unchecked((int)0xFF00FF00), "");
        Equal(setup.States("Highlight"), "E,E,E,E,E,E,E,-,E,E,-,E,E", "a block at the caret: Remove Highlight");
        view.HasHighlights = false;
        Equal(setup.States("Highlight"), "E,E,E,E,E,E,E,-,.,.,-,.,.", "no blocks: nothing to remove or go to, whatever the caret says");

        view.HasSelection = true;
        view.ReadOnly = true;
        Equal(setup.States("Edit Bytes"), ".,.,-,.,.,.,-,E", "read-only: only the checksums");
        Equal(setup.States("Highlight"), "E,E,E,E,E,E,E,-,.,.,-,.,.", "read-only: bytes can still be marked");
    }

    private static void CheckedStates()
    {
        Setup setup = new Setup();
        FakeView view = setup.View;
        Equal(setup.States("Hex View"), "EC,E,-,EC,E,E,E,-,E,EC,E,-,E", "little endian, single bytes, 16 in a row, no colours");
        view.BigEndian = true;
        view.GroupSize = 4;
        view.BytesPerRow = 32;
        view.ColorByValue = true;
        Equal(setup.States("Hex View"), "E,EC,-,E,E,EC,E,-,E,E,EC,-,EC", "big endian, 32-bit groups, 32 in a row, colours");
        view.BytesPerRow = 2;
        Equal(setup.States("Hex View"), "E,EC,-,E,E,.C,.,-,E,E,E,-,EC", "a group wider than a row cannot be chosen (and the choice is still shown)");
        view.BytesPerRow = 8;
        Equal(setup.States("Hex View"), "E,EC,-,E,E,EC,E,-,EC,E,E,-,EC", "a group as wide as the row can be chosen");
        foreach (HexSubmenu submenu in setup.Menu.Submenus)
        {
            if (submenu.Text != "Hex View")
            {
                foreach (HexMenuItem item in submenu.Items)
                {
                    Check(!item.IsChecked, submenu.Text + " > " + item.Text + " has no check mark");
                }
            }
        }
    }

    private static void Commands()
    {
        Setup setup = new Setup();
        FakeView view = setup.View;
        Equal(setup.Run("Highlight", "Yellow"), "Highlight ffffd600 ''||", "a colour marks the selection with it and no label");
        Equal(setup.Run("Highlight", "Purple"), "Highlight ffaa6eff ''||", "each colour its own");
        Equal(setup.Run("Highlight", "Color and Label..."), "|Highlight|", "Color and Label asks for its dialog");
        Equal(setup.Run("Highlight", "Remove Highlight"), "RemoveHighlight||", "Remove Highlight");
        Equal(setup.Run("Highlight", "Remove All Highlights"), "ClearHighlights||", "Remove All Highlights");
        Equal(setup.Run("Highlight", "Next Highlight"), "GoToHighlight True||", "Next Highlight goes forward");
        Equal(setup.Run("Highlight", "Previous Highlight"), "GoToHighlight False||", "Previous Highlight goes back");
        Equal(setup.Run("Copy As", "C array"), "CopyAs CArray||", "Copy As copies in its format");
        Equal(setup.Run("Copy As", "Base64"), "CopyAs Base64||", "each format its own");
        Equal(setup.Run("Edit Bytes", "Fill Selection..."), "|FillSelection|", "Fill Selection asks for its dialog");
        Equal(setup.Run("Edit Bytes", "Insert Bytes..."), "|InsertBytes|", "Insert Bytes asks for its dialog");
        Equal(setup.Run("Edit Bytes", "Checksums and Hashes..."), "|Checksums|", "Checksums asks for its dialog");
        Equal(setup.Run("Edit Bytes", "Swap Byte Order (16-bit Words)"), "Swap 2||", "a swap of 16-bit words that works says nothing");
        view.SwapResult = false;
        Equal(setup.Run("Edit Bytes", "Swap Byte Order (32-bit Words)"), "Swap 4||cannot swap 4", "a swap that cannot be done tells why");
        Equal(setup.Run("Edit Bytes", "Swap Byte Order (64-bit Words)"), "Swap 8||cannot swap 8", "64-bit words");
        setup.Run("Hex View", "Big Endian (Network Order, MIPS, PowerPC)");
        Check(view.BigEndian, "Big Endian sets the byte order");
        setup.Run("Hex View", "Big Endian (Network Order, MIPS, PowerPC)");
        Check(view.BigEndian, "and again leaves it");
        setup.Run("Hex View", "Little Endian (x86, ARM)");
        Check(!view.BigEndian, "Little Endian sets it back");
        setup.Run("Hex View", "Group by 64-bit Words");
        Equal(view.GroupSize, 8, "a group size");
        setup.Run("Hex View", "Single Bytes");
        Equal(view.GroupSize, 1, "single bytes");
        setup.Run("Hex View", "32 Bytes per Row");
        Equal(view.BytesPerRow, 32, "a row width");
        setup.Run("Hex View", "8 Bytes per Row");
        Equal(view.BytesPerRow, 8, "another row width");
        setup.Run("Hex View", "Color Bytes by Value");
        Check(view.ColorByValue, "Color Bytes by Value switches the colours on");
        setup.Run("Hex View", "Color Bytes by Value");
        Check(!view.ColorByValue, "and off");
        view.Throw = new InvalidOperationException("broken");
        bool thrown = false;
        try
        {
            setup.Run("Highlight", "Green");
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }
        Check(thrown, "what a command throws reaches the menu, which shows it");
    }

    private static void Arguments()
    {
        FakeView view = new FakeView();
        bool[] thrown = new bool[3];
        try { new HexMenuModel(null, kind => { }, text => { }); } catch (ArgumentNullException) { thrown[0] = true; }
        try { new HexMenuModel(view, null, text => { }); } catch (ArgumentNullException) { thrown[1] = true; }
        try { new HexMenuModel(view, kind => { }, null); } catch (ArgumentNullException) { thrown[2] = true; }
        Check(thrown[0] && thrown[1] && thrown[2], "the view, the dialogs and the messages are required");
    }

    // The hex view as the inspector sees it: the real model of the hex editor.
    private sealed class Source : IHexInspectorSource
    {
        public readonly HexEditingModel Model;

        public Source(byte[] bytes)
        {
            Model = new HexEditingModel(new HexDocument(bytes, bytes)) { Anchor = -1, BytesPerRow = 16 };
        }

        public bool BigEndian
        {
            get => Model.BigEndian;
            set => Model.BigEndian = value;
        }

        public bool ReadOnly => Model.ReadOnly;

        public bool HasSelection => Model.HasSelection;

        public int SelectionStart => Model.SelectionStart;

        public int SelectionLength => Model.SelectionLength;

        public int CaretOffset => Model.CaretOffset;

        public HexDocument Document => Model.Document;

        public byte[] GetBytesAtCaret(int count) => Model.GetBytesAtCaret(count);

        public bool WriteValue(HexValueType type, string text, out string error) => Model.WriteValue(type, text, out error);
    }

    // 1.5 as a float, a time, "é", "A", 0x80.
    private static readonly byte[] InspectorBytes = { 0x00, 0x00, 0xC0, 0x3F, 0x00, 0x2C, 0x5B, 0x65, 0xC3, 0xA9, 0x41, 0x80 };

    private static int RowOf(HexValueType type)
    {
        for (int row = 0; row < HexInspectorModel.Types.Count; row++)
        {
            if (HexInspectorModel.Types[row] == type)
            {
                return row;
            }
        }
        throw new Exception("no row for " + type);
    }

    private static void InspectorRows()
    {
        Source source = new Source(InspectorBytes);
        HexInspectorModel inspector = new HexInspectorModel(source);
        Equal(HexInspectorModel.Types.Count, 13, "thirteen values");
        IReadOnlyList<HexInspectorRow> rows = inspector.ReadRows();
        Equal(rows.Count, 13, "a row for each value");
        Equal(rows[RowOf(HexValueType.Binary)].Label, "Binary", "a single byte has no byte order in its label");
        Equal(rows[RowOf(HexValueType.UInt8)].Label, HexValueFormats.GetName(HexValueType.UInt8), "the name of the value");
        Equal(rows[RowOf(HexValueType.Float32)].Label, HexValueFormats.GetName(HexValueType.Float32) + " LE", "a wider value says the byte order");
        Equal(rows[RowOf(HexValueType.Float32)].Value, "1.5", "the value of the bytes at the caret");
        Check(rows[RowOf(HexValueType.Float32)].Known, "a value the bytes give is known");
        Equal(rows[RowOf(HexValueType.Utf8Char)].Value, "U+0000", "a zero byte is a character without a picture: its code");
        Equal(inspector.ByteOrderText, "Little endian", "the byte order row");
        source.BigEndian = true;
        rows = inspector.ReadRows();
        Equal(rows[RowOf(HexValueType.Int16)].Label, HexValueFormats.GetName(HexValueType.Int16) + " BE", "big endian in the label");
        HexValueFormats.TryRead(source.GetBytesAtCaret(8), HexValueType.Int32, true, out string bigInt32);
        Equal(rows[RowOf(HexValueType.Int32)].Value, bigInt32, "the values read in the byte order of the view");
        Equal(inspector.ByteOrderText, "Big endian", "the byte order row follows");
        source.BigEndian = false;
        source.Model.GoToOffset(InspectorBytes.Length);
        rows = inspector.ReadRows();
        Check(!rows[RowOf(HexValueType.UInt8)].Known && rows[RowOf(HexValueType.UInt8)].Value == "-", "after the last byte nothing is known: a dash");
        source.Model.GoToOffset(InspectorBytes.Length - 1);
        rows = inspector.ReadRows();
        Check(rows[RowOf(HexValueType.UInt8)].Known && rows[RowOf(HexValueType.UInt8)].Value == "128", "the last byte is a byte");
        Check(!rows[RowOf(HexValueType.UInt16)].Known && rows[RowOf(HexValueType.UInt16)].Value == "-", "but no 16-bit word: a dash");

        source.Model.GoToOffset(10);
        Equal(inspector.Offset, 10, "the offset is the caret's");
        Equal(inspector.OffsetText, "0x0000000A (10)", "in hex and decimal");
        Check(inspector.SelectionText == null, "no selection: no selection row");
        source.Model.Anchor = 4;
        Equal(inspector.Offset, 4, "with a selection the offset is its start");
        Equal(inspector.SelectionText, HexEditingModel.FormatByteCount(6), "and the selection row says its size");
        Check(inspector.Highlight == null, "no block: no row");
        source.Model.HighlightSelection(unchecked((int)0xFF3CC85A), "");
        Check(inspector.Highlight != null, "a block at the offset");
        Equal(HexInspectorModel.HighlightText(inspector.Highlight), HexEditingModel.FormatByteCount(6), "a block without a label: its size");
        source.Model.HighlightSelection(unchecked((int)0xFF3CC85A), "header");
        Equal(HexInspectorModel.HighlightText(inspector.Highlight), "header", "a block with a label: the label");
        source.Model.Anchor = -1;

        Equal(inspector.Hint, "Double-click a value to change it.", "the hint of a file that can be changed");
        source.Model.ReadOnly = true;
        Equal(inspector.Hint, "Click the byte order to switch it.", "the hint of a read-only file");
        source.Model.ReadOnly = false;
        inspector.ShowMessage("hello", isError: true);
        Equal(inspector.Hint, "hello", "a message takes the place of the hint");
        Check(inspector.MessageIsError, "an error message");
        inspector.ForgetMessageIfMoved();
        Equal(inspector.Message, "hello", "the message stays while the caret stays");
        source.Model.GoToOffset(11);
        inspector.ForgetMessageIfMoved();
        Equal(inspector.Message, "", "and goes when it moved");
        Check(!inspector.MessageIsError, "with its colour");
        source.Model.GoToOffset(9);
        source.Model.Anchor = 2;
        inspector.ShowMessage("about a selection", isError: false);
        inspector.ForgetMessageIfMoved();
        Equal(inspector.Message, "about a selection", "a message about a selection stays while the selection stays (it is about its start)");
        source.Model.Anchor = -1;
        inspector.ShowMessage(null, isError: false);
        Equal(inspector.Message, "", "no message is an empty one");
        inspector.ShowMessage("kept", isError: false);
        inspector.BeginEdit(RowOf(HexValueType.UInt8), out string unused);
        inspector.ShowMessage("while editing", isError: false);
        source.Model.GoToOffset(3);
        inspector.ForgetMessageIfMoved();
        Equal(inspector.Message, "while editing", "while a value is edited the message stays");
    }

    private static void InspectorGeometry()
    {
        const int rowHeight = 20;
        Equal(HexInspectorModel.ByteOrderRowTop(rowHeight), 26, "the byte order row is below the title and a gap");
        Equal(HexInspectorModel.ValueRowTop(0, rowHeight), 46, "the first value row is below it");
        Equal(HexInspectorModel.ValueRowTop(12, rowHeight), 286, "the rows follow each other");
        Equal(HexInspectorModel.RowAt(0, rowHeight), -1, "the title is no row");
        Equal(HexInspectorModel.RowAt(25, rowHeight), -1, "the gap below it neither");
        Equal(HexInspectorModel.RowAt(26, rowHeight), -2, "the byte order from its first pixel");
        Equal(HexInspectorModel.RowAt(45, rowHeight), -2, "to its last");
        Equal(HexInspectorModel.RowAt(46, rowHeight), 0, "then the first value");
        Equal(HexInspectorModel.RowAt(305, rowHeight), 12, "the last pixel of the last value");
        Equal(HexInspectorModel.RowAt(306, rowHeight), -1, "below the values: no row");
        for (int row = 0; row < HexInspectorModel.Types.Count; row++)
        {
            Equal(HexInspectorModel.RowAt(HexInspectorModel.ValueRowTop(row, 17), 17), row, "the top of value row " + row + " is that row");
        }
        Check(HexInspectorModel.IsLink(-2, true, false), "the value of the byte order is a link");
        Check(!HexInspectorModel.IsLink(-2, false, false), "its label is not");
        Check(!HexInspectorModel.IsLink(3, true, false), "a value is not");
        Check(HexInspectorModel.IsLink(-1, false, true), "the Hide link is");
        Equal(HexInspectorModel.InnerMargin + HexInspectorModel.LabelChars + HexInspectorModel.ValueChars, 43, "the widths the view is laid out with");
    }

    private static void InspectorKeysAndClicks()
    {
        Source source = new Source(InspectorBytes);
        HexInspectorModel inspector = new HexInspectorModel(source);
        Equal(inspector.SelectedRow, -1, "no row chosen at first");
        Equal(inspector.Key(VirtualKey.Enter, false), HexInspectorCommand.Handled, "Enter without a row does nothing but is the inspector's");
        Equal(inspector.Key(VirtualKey.F2, false), HexInspectorCommand.Handled, "F2 too");
        Equal(inspector.Key(VirtualKey.Up, false), HexInspectorCommand.Handled, "Up");
        Equal(inspector.SelectedRow, 0, "Up without a row chooses the first");
        inspector.SelectedRow = -1;
        inspector.OnFocused();
        Equal(inspector.SelectedRow, 0, "the focus chooses the first row when none was");
        inspector.SelectedRow = 5;
        inspector.OnFocused();
        Equal(inspector.SelectedRow, 5, "and keeps a chosen one");
        inspector.Key(VirtualKey.Up, false);
        Equal(inspector.SelectedRow, 4, "Up goes one row up");
        inspector.Key(VirtualKey.Down, false);
        inspector.Key(VirtualKey.Down, false);
        Equal(inspector.SelectedRow, 6, "Down one down");
        for (int i = 0; i < 20; i++)
        {
            inspector.Key(VirtualKey.Down, false);
        }
        Equal(inspector.SelectedRow, 12, "Down stops at the last row");
        for (int i = 0; i < 20; i++)
        {
            inspector.Key(VirtualKey.Up, false);
        }
        Equal(inspector.SelectedRow, 0, "Up at the first");
        Equal(inspector.Key(VirtualKey.Enter, false), HexInspectorCommand.BeginEdit, "Enter on a row edits it");
        Equal(inspector.Key(VirtualKey.F2, false), HexInspectorCommand.BeginEdit, "F2 too");
        Equal(inspector.Key(VirtualKey.Escape, false), HexInspectorCommand.FocusEditor, "Esc goes back to the bytes");
        Equal(inspector.Key(VirtualKey.I, true), HexInspectorCommand.HideInspector, "Ctrl+I hides the inspector");
        Equal(inspector.Key(VirtualKey.I, false), HexInspectorCommand.NotHandled, "I alone is not the inspector's");
        Equal(inspector.Key(VirtualKey.C, true), HexInspectorCommand.CopyValue, "Ctrl+C copies the value");
        Equal(inspector.Key(VirtualKey.C, false), HexInspectorCommand.NotHandled, "C alone is not the inspector's");
        Equal(inspector.Key(VirtualKey.Tab, false), HexInspectorCommand.NotHandled, "nor is Tab");

        inspector.SelectedRow = RowOf(HexValueType.Float32);
        Check(inspector.TryGetSelectedValue(out string value) && value == "1.5", "the value of the chosen row as shown");
        inspector.Copied();
        Equal(inspector.Message, HexValueFormats.GetName(HexValueType.Float32) + " copied.", "and the message after it was copied");
        source.Model.GoToOffset(InspectorBytes.Length);
        inspector.SelectedRow = RowOf(HexValueType.UInt8);
        Check(!inspector.TryGetSelectedValue(out value), "a row the bytes give no value for: nothing to copy");
        source.Model.GoToOffset(0);
        inspector.SelectedRow = -1;
        Check(!inspector.TryGetSelectedValue(out value), "no row: nothing to copy");

        Equal(inspector.Press(5, onHideLink: true), HexInspectorCommand.HideInspector, "a click on the Hide link hides the inspector");
        Equal(inspector.SelectedRow, -1, "and chooses no row");
        Equal(inspector.Press(-2, onHideLink: false), HexInspectorCommand.FocusEditor, "a click on the byte order goes back to the bytes");
        Check(source.BigEndian, "after switching it");
        inspector.Press(-2, onHideLink: false);
        Check(!source.BigEndian, "and back");
        Equal(inspector.Press(7, onHideLink: false), HexInspectorCommand.FocusInspector, "a click on a value takes the focus");
        Equal(inspector.SelectedRow, 7, "and chooses its row");
        Equal(inspector.Press(-1, onHideLink: false), HexInspectorCommand.FocusInspector, "a click elsewhere takes the focus");
        Equal(inspector.SelectedRow, 7, "and keeps the row");

        Check(inspector.SetHover(3), "the mouse over a row changes the hover");
        Equal(inspector.HoverRow, 3, "to that row");
        Check(!inspector.SetHover(3), "the same row again changes nothing");
        Check(inspector.SetHover(-2), "the byte order is no value row");
        Equal(inspector.HoverRow, -1, "so no row is under the mouse");
        Check(!inspector.SetHover(-1), "and leaving changes nothing then");
    }

    private static void InspectorEdits()
    {
        Source source = new Source(InspectorBytes);
        HexInspectorModel inspector = new HexInspectorModel(source);
        Equal(inspector.Commit("1"), HexInspectorCommit.NotEditing, "Enter with nothing edited does nothing");
        Check(inspector.BeginEdit(RowOf(HexValueType.Float32), out string text), "an edit starts");
        Equal(text, "1.5", "with the value as shown");
        Equal(inspector.EditOriginalText, "1.5", "which is kept");
        Equal(inspector.Editing, (HexValueType?)HexValueType.Float32, "the value being edited");
        Equal(inspector.SelectedRow, RowOf(HexValueType.Float32), "its row is chosen");
        Equal(inspector.Message, "Enter writes the value at the caret (little endian), Esc cancels.", "the message says how");
        Equal(inspector.Commit("1.5"), HexInspectorCommit.Unchanged, "Enter on the same text");
        Equal(inspector.Message, "Unchanged: nothing written.", "writes nothing and says so");
        Check(inspector.Editing == null, "and ends the edit");
        Check(!source.Document.IsModified, "the bytes did not change");
        inspector.BeginEdit(RowOf(HexValueType.Float32), out text);
        Equal(inspector.Commit("2.25"), HexInspectorCommit.Written, "Enter on a new value");
        Equal(source.GetBytesAtCaret(4)[3], (byte)0x40, "writes it at the caret");
        Equal(inspector.Message, HexValueFormats.GetName(HexValueType.Float32) + " written: Ctrl+Z in the bytes undoes it.", "and says so");
        Check(inspector.Editing == null, "the edit ends");
        inspector.BeginEdit(RowOf(HexValueType.UInt8), out text);
        Equal(inspector.Commit("300"), HexInspectorCommit.Rejected, "a value that does not fit");
        Check(inspector.MessageIsError && inspector.Message.Length > 0, "is an error with the reason");
        Equal(inspector.Editing, (HexValueType?)HexValueType.UInt8, "and the edit goes on");
        inspector.CancelEdit();
        Check(inspector.Editing == null && inspector.Message == "" && !inspector.MessageIsError, "Esc ends it without a message");

        source.Model.GoToOffset(8);
        inspector.BeginEdit(RowOf(HexValueType.Utf8Char), out text);
        Equal(text, "\u00e9", "a character is edited without its code");
        source.Model.GoToOffset(0);
        inspector.BeginEdit(RowOf(HexValueType.Utf8Char), out text);
        Equal(text, "", "a character without a picture: an empty box");
        source.Model.GoToOffset(4);
        inspector.BeginEdit(RowOf(HexValueType.UnixTime32), out text);
        HexValueFormats.TryRead(source.GetBytesAtCaret(8), HexValueType.UnixTime32, false, out string shown);
        Check(shown.EndsWith(" UTC", StringComparison.Ordinal), "a time is shown with UTC");
        Equal(text, shown.Substring(0, shown.Length - 4), "and edited without it");
        source.BigEndian = true;
        inspector.BeginEdit(RowOf(HexValueType.Int16), out text);
        Equal(inspector.Message, "Enter writes the value at the caret (big endian), Esc cancels.", "the message says the byte order");
        inspector.EndEdit();
        Check(inspector.Editing == null, "the edit ends when the box loses the focus");
        Equal(inspector.Message, "Enter writes the value at the caret (big endian), Esc cancels.", "and keeps the message");

        source.Model.ReadOnly = true;
        Check(!inspector.BeginEdit(RowOf(HexValueType.UInt8), out text), "a read-only file cannot be edited");
        Check(text == null && inspector.Editing == null, "no edit starts");
        Equal(inspector.Message, "The file cannot be changed now.", "and the message says why");
        Check(inspector.MessageIsError, "as an error");

        bool thrown = false;
        try
        {
            new HexInspectorModel(null);
        }
        catch (ArgumentNullException)
        {
            thrown = true;
        }
        Check(thrown, "the inspector needs its view");
    }

    private static int Main()
    {
        try
        {
            MenuStructure();
            EnabledStates();
            CheckedStates();
            Commands();
            Arguments();
            InspectorRows();
            InspectorGeometry();
            InspectorKeysAndClicks();
            InspectorEdits();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message.StartsWith("FAIL", StringComparison.Ordinal) ? exception.Message : "FAIL: " + exception);
            return 1;
        }
        Console.WriteLine("HexMenuInspectorTests: all " + _passed + " checks passed.");
        return 0;
    }
}
