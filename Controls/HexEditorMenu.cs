using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>The dialogs of the hex view that belong to the main window (they are WPF windows).</summary>
internal enum HexDialogKind
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

internal sealed class HexDialogRequestedEventArgs : EventArgs
{
    public HexDialogRequestedEventArgs(HexDialogKind kind)
    {
        Kind = kind;
    }

    public HexDialogKind Kind { get; }
}

/// <summary>
/// The submenus the context menu of the file editor shows in the hex view: marking bytes with colours, copying them as
/// source code, filling, inserting and swapping bytes, checksums, and how the bytes are shown (the byte order, the
/// groups, the row width, the data inspector, the colours by value).
/// </summary>
internal sealed class HexEditorMenu : IDisposable
{
    private readonly HexEditorControl _editor;

    private readonly Action<HexDialogKind> _requestDialog;

    private readonly Action _afterCommand;

    private readonly List<Image> _images = new List<Image>();

    private readonly ToolStripMenuItem _highlightMenu = new ToolStripMenuItem("Highlight");

    private readonly ToolStripMenuItem _copyAsMenu = new ToolStripMenuItem("Copy As");

    private readonly ToolStripMenuItem _bytesMenu = new ToolStripMenuItem("Edit Bytes");

    private readonly ToolStripMenuItem _viewMenu = new ToolStripMenuItem("Hex View");

    private readonly List<ToolStripMenuItem> _colorItems = new List<ToolStripMenuItem>();

    private readonly ToolStripMenuItem _highlightWithLabel;

    private readonly ToolStripMenuItem _removeHighlight;

    private readonly ToolStripMenuItem _clearHighlights;

    private readonly ToolStripMenuItem _nextHighlight;

    private readonly ToolStripMenuItem _previousHighlight;

    private readonly ToolStripMenuItem _fillSelection;

    private readonly ToolStripMenuItem _insertBytes;

    private readonly List<ToolStripMenuItem> _swapItems = new List<ToolStripMenuItem>();

    private readonly ToolStripMenuItem _checksums;

    private readonly ToolStripMenuItem _littleEndian;

    private readonly ToolStripMenuItem _bigEndian;

    private readonly Dictionary<int, ToolStripMenuItem> _groupItems = new Dictionary<int, ToolStripMenuItem>();

    private readonly Dictionary<int, ToolStripMenuItem> _rowItems = new Dictionary<int, ToolStripMenuItem>();

    private readonly ToolStripMenuItem _inspector;

    private readonly ToolStripMenuItem _colorByValue;

    public HexEditorMenu(HexEditorControl editor, Action<HexDialogKind> requestDialog, Action afterCommand)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _requestDialog = requestDialog ?? throw new ArgumentNullException(nameof(requestDialog));
        _afterCommand = afterCommand ?? (() => { });

        foreach (HexHighlightColor color in HexEditorControl.HighlightColors)
        {
            HexHighlightColor chosen = color;
            ToolStripMenuItem item = AddItem(_highlightMenu, color.Name, () => _editor.HighlightSelection(chosen.Color, ""));
            item.Image = CreateSwatch(color.Color);
            _colorItems.Add(item);
        }
        _highlightWithLabel = AddItem(_highlightMenu, "Color and Label...", () => _requestDialog(HexDialogKind.Highlight));
        _highlightMenu.DropDownItems.Add(new ToolStripSeparator());
        _removeHighlight = AddItem(_highlightMenu, "Remove Highlight", () => _editor.RemoveHighlight());
        _clearHighlights = AddItem(_highlightMenu, "Remove All Highlights", () => _editor.ClearHighlights());
        _highlightMenu.DropDownItems.Add(new ToolStripSeparator());
        _nextHighlight = AddItem(_highlightMenu, "Next Highlight", () => _editor.GoToHighlight(forward: true), "F2");
        _previousHighlight = AddItem(_highlightMenu, "Previous Highlight", () => _editor.GoToHighlight(forward: false), "Shift+F2");

        foreach (HexCopyFormat format in (HexCopyFormat[])Enum.GetValues(typeof(HexCopyFormat)))
        {
            HexCopyFormat chosen = format;
            AddItem(_copyAsMenu, HexValueFormats.GetName(format), () => _editor.CopyAs(chosen));
        }

        _fillSelection = AddItem(_bytesMenu, "Fill Selection...", () => _requestDialog(HexDialogKind.FillSelection));
        _insertBytes = AddItem(_bytesMenu, "Insert Bytes...", () => _requestDialog(HexDialogKind.InsertBytes));
        _bytesMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (int size in new[] { 2, 4, 8 })
        {
            int chosen = size;
            string bits = (size * 8).ToString(CultureInfo.InvariantCulture);
            _swapItems.Add(AddItem(_bytesMenu, "Swap Byte Order (" + bits + "-bit Words)", () => SwapByteOrder(chosen)));
        }
        _bytesMenu.DropDownItems.Add(new ToolStripSeparator());
        _checksums = AddItem(_bytesMenu, "Checksums and Hashes...", () => _requestDialog(HexDialogKind.Checksums));

        _littleEndian = AddItem(_viewMenu, "Little Endian (x86, ARM)", () => _editor.BigEndian = false);
        _bigEndian = AddItem(_viewMenu, "Big Endian (Network Order, MIPS, PowerPC)", () => _editor.BigEndian = true);
        _viewMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (int size in HexEditorControl.GroupSizeChoices)
        {
            int chosen = size;
            string text = (size == 1) ? "Single Bytes" : "Group by " + (size * 8).ToString(CultureInfo.InvariantCulture) + "-bit Words";
            _groupItems[size] = AddItem(_viewMenu, text, () => _editor.GroupSize = chosen);
        }
        _viewMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (int count in HexEditorControl.BytesPerRowChoices)
        {
            int chosen = count;
            _rowItems[count] = AddItem(_viewMenu, count.ToString(CultureInfo.InvariantCulture) + " Bytes per Row", () => _editor.BytesPerRow = chosen);
        }
        _viewMenu.DropDownItems.Add(new ToolStripSeparator());
        _inspector = AddItem(_viewMenu, "Data Inspector", () => _editor.ShowDataInspector = !_editor.ShowDataInspector);
        _colorByValue = AddItem(_viewMenu, "Color Bytes by Value", () => _editor.ColorByValue = !_editor.ColorByValue);
    }

    /// <summary>The submenus, in the order they are shown.</summary>
    public IReadOnlyList<ToolStripMenuItem> Menus => new[] { _highlightMenu, _copyAsMenu, _bytesMenu, _viewMenu };

    /// <summary>The drop-downs of the submenus (to theme and scale them with the context menu).</summary>
    public IEnumerable<ToolStripDropDown> DropDowns
    {
        get
        {
            foreach (ToolStripMenuItem menu in Menus)
            {
                yield return menu.DropDown;
            }
        }
    }

    /// <summary>Shows the submenus in the hex view only and enables what the editor allows now.</summary>
    public void Refresh(bool hexMode)
    {
        foreach (ToolStripMenuItem menu in Menus)
        {
            menu.Visible = hexMode;
        }
        if (!hexMode)
        {
            return;
        }
        bool readOnly = _editor.ReadOnly;
        bool hasBytes = _editor.Length > 0;
        bool canHighlight = _editor.CanHighlight;
        foreach (ToolStripMenuItem item in _colorItems)
        {
            item.Enabled = canHighlight;
        }
        _highlightWithLabel.Enabled = canHighlight;
        _removeHighlight.Enabled = _editor.HasHighlights && (_editor.HasSelection || _editor.HighlightAtCaret != null);
        _clearHighlights.Enabled = _editor.HasHighlights;
        _nextHighlight.Enabled = _editor.HasHighlights;
        _previousHighlight.Enabled = _editor.HasHighlights;
        _copyAsMenu.Enabled = _editor.HasSelection || _editor.CaretOffset < _editor.Length;
        _fillSelection.Enabled = !readOnly && _editor.HasSelection;
        _insertBytes.Enabled = !readOnly;
        foreach (ToolStripMenuItem item in _swapItems)
        {
            item.Enabled = !readOnly && _editor.HasSelection;
        }
        _checksums.Enabled = hasBytes;
        _littleEndian.Checked = !_editor.BigEndian;
        _bigEndian.Checked = _editor.BigEndian;
        foreach (KeyValuePair<int, ToolStripMenuItem> pair in _groupItems)
        {
            pair.Value.Checked = pair.Key == _editor.GroupSize;
            pair.Value.Enabled = pair.Key <= _editor.BytesPerRow;
        }
        foreach (KeyValuePair<int, ToolStripMenuItem> pair in _rowItems)
        {
            pair.Value.Checked = pair.Key == _editor.BytesPerRow;
        }
        _inspector.Checked = _editor.ShowDataInspector;
        _colorByValue.Checked = _editor.ColorByValue;
    }

    private ToolStripMenuItem AddItem(ToolStripMenuItem menu, string text, Action action, string shortcut = null)
    {
        // The key is shown only: the editor handles the key itself.
        ToolStripMenuItem item = new ToolStripMenuItem(text)
        {
            ShortcutKeyDisplayString = shortcut
        };
        item.Click += (sender, e) =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                ThemedMessageBox.Show(exception.Message, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
            _afterCommand();
        };
        menu.DropDownItems.Add(item);
        return item;
    }

    private void SwapByteOrder(int size)
    {
        if (!_editor.SwapSelectionByteOrder(size, out string error))
        {
            ThemedMessageBox.Show(error, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
        }
    }

    private Image CreateSwatch(Color color)
    {
        Bitmap bitmap = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            using (SolidBrush brush = new SolidBrush(color))
            {
                graphics.FillRectangle(brush, 2, 2, 12, 12);
            }
            using (Pen pen = new Pen(Color.FromArgb(120, 0, 0, 0)))
            {
                graphics.DrawRectangle(pen, 2, 2, 11, 11);
            }
        }
        _images.Add(bitmap);
        return bitmap;
    }

    public void Dispose()
    {
        foreach (Image image in _images)
        {
            image.Dispose();
        }
        _images.Clear();
    }
}
