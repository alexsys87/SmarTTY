using System;
using System.Collections.Generic;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

// Checks of the parts of the hex view that stage 3B-3 took out of the WinForms controls so that the WPF view of the editor tab shows the same:
// CodeEditing/HexMenuModel.cs (the submenus of the hex view: their items, keys, swatches, when an item is enabled or checked and what it does), with a
// fake hex view that logs what it is asked. The same code is run in the real WinForms menu by tests/HexEditorControlTests (the "menu" run of its
// golden file).
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

    private static int Main()
    {
        try
        {
            MenuStructure();
            EnabledStates();
            CheckedStates();
            Commands();
            Arguments();
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
