using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SmarTTY.CodeEditing;

/// <summary>The dialogs of the hex view that belong to the main window (they are WPF windows).</summary>
public enum HexDialogKind
{
    /// <summary>Choose a colour and a label for the selected bytes.</summary>
    Highlight,

    /// <summary>Fill the selection with a byte pattern.</summary>
    FillSelection,

    /// <summary>Insert a number of bytes at the caret.</summary>
    InsertBytes,

    /// <summary>The checksums and hashes of the selection or the file.</summary>
    Checksums
}

/// <summary>An item of a submenu of the hex view, or a separator (no text).</summary>
public sealed class HexMenuItem
{
    private readonly Action _run;

    private readonly Func<bool> _isEnabled;

    private readonly Func<bool> _isChecked;

    internal HexMenuItem(string text, Action run, Func<bool> isEnabled, Func<bool> isChecked, string shortcut, RgbColor? swatch)
    {
        Text = text;
        _run = run;
        _isEnabled = isEnabled;
        _isChecked = isChecked;
        Shortcut = shortcut;
        Swatch = swatch;
    }

    /// <summary>The text of the item; null for a separator.</summary>
    public string Text { get; }

    public bool IsSeparator => Text == null;

    /// <summary>The key shown beside the item (the view handles the key itself), or null.</summary>
    public string Shortcut { get; }

    /// <summary>The colour the item shows as its image, or null.</summary>
    public RgbColor? Swatch { get; }

    /// <summary>The item can be used now.</summary>
    public bool IsEnabled => _isEnabled == null || _isEnabled();

    /// <summary>The item shows a check mark now.</summary>
    public bool IsChecked => _isChecked != null && _isChecked();

    /// <summary>Runs the command of the item; what it throws goes to the menu, which shows it.</summary>
    public void Run()
    {
        _run?.Invoke();
    }
}

/// <summary>A submenu of the hex view.</summary>
public sealed class HexSubmenu
{
    private readonly Func<bool> _isEnabled;

    internal HexSubmenu(string text, Func<bool> isEnabled)
    {
        Text = text;
        _isEnabled = isEnabled;
    }

    public string Text { get; }

    public List<HexMenuItem> Items { get; } = new List<HexMenuItem>();

    /// <summary>The submenu can be opened now.</summary>
    public bool IsEnabled => _isEnabled == null || _isEnabled();
}

/// <summary>
/// The submenus the context menu of the file editor shows in the hex view: marking bytes with colours, copying them as source code, filling, inserting
/// and swapping bytes, checksums, and how the bytes are shown (the byte order, the groups, the row width, the colours by value). It is what
/// <c>Controls/HexEditorMenu</c> built and enabled, moved here as it was (stage 3B-3) so that the WinForms menu and the WPF menu of the hex view show
/// the same items with the same rules; each view makes its own items, shows the errors of a command and does what follows one.
/// </summary>
public sealed class HexMenuModel
{
    private readonly IHexEditorView _editor;

    private readonly Action<HexDialogKind> _requestDialog;

    private readonly Action<string> _inform;

    /// <param name="requestDialog">Opens a dialog of the main window.</param>
    /// <param name="inform">Shows a message (why the byte order of the selection cannot be swapped).</param>
    public HexMenuModel(IHexEditorView editor, Action<HexDialogKind> requestDialog, Action<string> inform)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _requestDialog = requestDialog ?? throw new ArgumentNullException(nameof(requestDialog));
        _inform = inform ?? throw new ArgumentNullException(nameof(inform));

        HexSubmenu highlight = new HexSubmenu("Highlight", null);
        foreach (HexHighlightColor color in HexViewChoices.HighlightColors)
        {
            HexHighlightColor chosen = color;
            Add(highlight, color.Name, () => _editor.HighlightSelection(chosen.Color, ""), () => _editor.CanHighlight, swatch: color.Color);
        }
        Add(highlight, "Color and Label...", () => _requestDialog(HexDialogKind.Highlight), () => _editor.CanHighlight);
        highlight.Items.Add(Separator());
        Add(highlight, "Remove Highlight", () => _editor.RemoveHighlight(), () => _editor.HasHighlights && (_editor.HasSelection || _editor.HighlightAtCaret != null));
        Add(highlight, "Remove All Highlights", () => _editor.ClearHighlights(), () => _editor.HasHighlights);
        highlight.Items.Add(Separator());
        Add(highlight, "Next Highlight", () => _editor.GoToHighlight(forward: true), () => _editor.HasHighlights, shortcut: "F2");
        Add(highlight, "Previous Highlight", () => _editor.GoToHighlight(forward: false), () => _editor.HasHighlights, shortcut: "Shift+F2");

        HexSubmenu copyAs = new HexSubmenu("Copy As", () => _editor.HasSelection || _editor.CaretOffset < _editor.Length);
        foreach (HexCopyFormat format in (HexCopyFormat[])Enum.GetValues(typeof(HexCopyFormat)))
        {
            HexCopyFormat chosen = format;
            Add(copyAs, HexValueFormats.GetName(format), () => _editor.CopyAs(chosen), null);
        }

        HexSubmenu bytes = new HexSubmenu("Edit Bytes", null);
        Add(bytes, "Fill Selection...", () => _requestDialog(HexDialogKind.FillSelection), () => !_editor.ReadOnly && _editor.HasSelection);
        Add(bytes, "Insert Bytes...", () => _requestDialog(HexDialogKind.InsertBytes), () => !_editor.ReadOnly);
        bytes.Items.Add(Separator());
        foreach (int size in new[] { 2, 4, 8 })
        {
            int chosen = size;
            string bits = (size * 8).ToString(CultureInfo.InvariantCulture);
            Add(bytes, "Swap Byte Order (" + bits + "-bit Words)", () => SwapByteOrder(chosen), () => !_editor.ReadOnly && _editor.HasSelection);
        }
        bytes.Items.Add(Separator());
        Add(bytes, "Checksums and Hashes...", () => _requestDialog(HexDialogKind.Checksums), () => _editor.Length > 0);

        HexSubmenu view = new HexSubmenu("Hex View", null);
        Add(view, "Little Endian (x86, ARM)", () => _editor.BigEndian = false, null, () => !_editor.BigEndian);
        Add(view, "Big Endian (Network Order, MIPS, PowerPC)", () => _editor.BigEndian = true, null, () => _editor.BigEndian);
        view.Items.Add(Separator());
        foreach (int size in HexViewChoices.GroupSize)
        {
            int chosen = size;
            string text = (size == 1) ? "Single Bytes" : "Group by " + (size * 8).ToString(CultureInfo.InvariantCulture) + "-bit Words";
            Add(view, text, () => _editor.GroupSize = chosen, () => chosen <= _editor.BytesPerRow, () => chosen == _editor.GroupSize);
        }
        view.Items.Add(Separator());
        foreach (int count in HexViewChoices.BytesPerRow)
        {
            int chosen = count;
            Add(view, count.ToString(CultureInfo.InvariantCulture) + " Bytes per Row", () => _editor.BytesPerRow = chosen, null, () => chosen == _editor.BytesPerRow);
        }
        view.Items.Add(Separator());
        Add(view, "Color Bytes by Value", () => _editor.ColorByValue = !_editor.ColorByValue, null, () => _editor.ColorByValue);

        Submenus = new[] { highlight, copyAs, bytes, view };
    }

    /// <summary>The submenus, in the order they are shown.</summary>
    public IReadOnlyList<HexSubmenu> Submenus { get; }

    private static HexMenuItem Separator()
    {
        return new HexMenuItem(null, null, null, null, null, null);
    }

    private static void Add(HexSubmenu menu, string text, Action run, Func<bool> isEnabled, Func<bool> isChecked = null, string shortcut = null, RgbColor? swatch = null)
    {
        menu.Items.Add(new HexMenuItem(text, run, isEnabled, isChecked, shortcut, swatch));
    }

    private void SwapByteOrder(int size)
    {
        if (!_editor.SwapSelectionByteOrder(size, out string error))
        {
            _inform(error);
        }
    }
}
