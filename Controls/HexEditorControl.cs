using System.ComponentModel;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Media;
using System.Text;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>What a search in the hex editor found (or did not find).</summary>
public sealed class HexSearchEventArgs : EventArgs
{
    public bool Found { get; internal set; }

    public int Offset { get; internal set; }

    public int Length { get; internal set; }
}

/// <summary>A colour the hex editor offers for marking bytes, with the name shown in the menus.</summary>
public sealed class HexHighlightColor
{
    public HexHighlightColor(string name, Color color)
    {
        Name = name;
        Color = color;
    }

    public string Name { get; }

    public Color Color { get; }
}

/// <summary>
/// A full hex editor over a <see cref="HexDocument"/>: the offset, the hexadecimal bytes and the ASCII column, with
/// the caret and the selection, overwrite and insert modes, the clipboard, undo and redo, and a search that is
/// repeated with F3. The bytes can be grouped into words, read as numbers in either byte order in the data inspector
/// (see <see cref="HexDataInspector"/>), coloured by value and marked with coloured blocks. It has no file access: the
/// remote file editor saves the bytes (see <see cref="RemoteFileEditor"/>). The control draws its own scroll bars,
/// because the WPF scroll bars of the text editor are hidden in hex mode.
/// </summary>
public sealed class HexEditorControl : Control, IHexViewStatus, IRemoteHexView, IHexLayoutView
{
    /// <summary>The row widths the view offers.</summary>
    public static readonly int[] BytesPerRowChoices = HexViewChoices.BytesPerRow;

    /// <summary>The group sizes the view offers: single bytes, 16-, 32- and 64-bit words.</summary>
    public static readonly int[] GroupSizeChoices = HexViewChoices.GroupSize;

    /// <summary>The colours offered for marking bytes.</summary>
    public static readonly HexHighlightColor[] HighlightColors =
    {
        new HexHighlightColor("Yellow", Color.FromArgb(255, 214, 0)),
        new HexHighlightColor("Green", Color.FromArgb(60, 200, 90)),
        new HexHighlightColor("Blue", Color.FromArgb(50, 150, 255)),
        new HexHighlightColor("Red", Color.FromArgb(255, 80, 80)),
        new HexHighlightColor("Orange", Color.FromArgb(255, 150, 30)),
        new HexHighlightColor("Purple", Color.FromArgb(170, 110, 255))
    };

    // How strongly a marked block tints the background (the text stays readable in light and dark themes).
    private const int HighlightAlpha = 100;

    private const int Margin = HexViewLayout.Margin;

    private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private HexDocument _document = new HexDocument();

    // The caret, the selection, the typing and what the keys and the commands do to the bytes; this control draws it and reads its input.
    private readonly HexEditingModel _model;

    // Where the columns and the rows are, the scroll position, the byte under a point and the scroll that keeps the caret in view.
    private readonly HexViewLayout _layout;

    // The selection made with the mouse.
    private readonly HexPointer _pointer;

    private readonly HexDataInspector _inspector;

    private readonly ToolTip _toolTip = new ToolTip();

    // The byte the tool tip describes; -1 when the mouse is not over a byte.
    private int _toolTipOffset = -1;

    private bool _colorByValue;

    // The data inspector is off until the user asks for it (Ctrl+I, the tool bar, the header link).
    private bool _showInspector;

    // Where the "Data inspector" link is drawn in the header while the inspector is hidden (empty: not drawn).
    private Rectangle _inspectorLinkBounds;

    // Whether the inspector is laid out: Control.Visible reads false while the editor itself is hidden.
    private bool _inspectorShown;

    private readonly VScrollBar _verticalScrollBar = new VScrollBar();

    private readonly HScrollBar _horizontalScrollBar = new HScrollBar();

    private readonly System.Windows.Forms.Timer _caretTimer = new System.Windows.Forms.Timer();

    // Scrolls the table while a selection is dragged beyond its edge, even when the mouse stands still there.
    private readonly System.Windows.Forms.Timer _autoScrollTimer = new System.Windows.Forms.Timer();

    private bool _caretVisible = true;

    private bool _updatingScrollBars;

    private bool _updatingLayout;

    /// <summary>The caret, the selection, the bytes or the undo history changed (the tool bar and status bar refresh).</summary>
    public event EventHandler StateChanged;

    /// <summary>The user pressed Ctrl+F.</summary>
    public event EventHandler FindRequested;

    /// <summary>The user pressed Ctrl+G.</summary>
    public event EventHandler GoToOffsetRequested;

    /// <summary>The user pressed Ctrl+S.</summary>
    public event EventHandler SaveRequested;

    /// <summary>A search ran (also when F3 repeated it).</summary>
    public event EventHandler<HexSearchEventArgs> SearchFinished;

    /// <summary>
    /// The user changed how the bytes are shown (the byte order, the groups, the row width, the colours or the data
    /// inspector): the settings keep it for the next file.
    /// </summary>
    public event EventHandler ViewOptionsChanged;

    public HexEditorControl()
    {
        _model = new HexEditingModel(_document);
        _layout = new HexViewLayout(_model, this);
        _pointer = new HexPointer(_model);
        _model.Changed += OnModelChanged;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, value: true);
        TabStop = true;
        _verticalScrollBar.Dock = DockStyle.Right;
        _horizontalScrollBar.Dock = DockStyle.Bottom;
        _verticalScrollBar.ValueChanged += (sender, e) => OnScrollBarChanged();
        _horizontalScrollBar.ValueChanged += (sender, e) => OnScrollBarChanged();
        _inspector = new HexDataInspector(this)
        {
            Dock = DockStyle.Right,
            Visible = false
        };
        // Docked first (it is the last child): the inspector takes the whole height, the scroll bars the rest.
        Controls.Add(_verticalScrollBar);
        Controls.Add(_horizontalScrollBar);
        Controls.Add(_inspector);
        _toolTip.InitialDelay = 600;
        _toolTip.ReshowDelay = 200;
        _toolTip.AutoPopDelay = 15000;
        _caretTimer.Interval = 530;
        _caretTimer.Tick += (sender, e) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateCaretArea();
        };
        _autoScrollTimer.Interval = 50;
        _autoScrollTimer.Tick += (sender, e) => OnAutoScrollTick();
        Document = _document;
        UpdateMetrics();
        UpdateInspectorVisibility();
        UpdateScrollBars();
    }

    /// <summary>The bytes being edited.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HexDocument Document
    {
        get
        {
            return _document;
        }
        set
        {
            if (_document != null)
            {
                _document.Changed -= OnDocumentChanged;
                _document.ModifiedChanged -= OnDocumentModifiedChanged;
                _document.HighlightsChanged -= OnDocumentHighlightsChanged;
            }
            _document = value ?? new HexDocument();
            _model.Document = _document;
            _document.Changed += OnDocumentChanged;
            _document.ModifiedChanged += OnDocumentModifiedChanged;
            _document.HighlightsChanged += OnDocumentHighlightsChanged;
            _model.CaretOffset = Math.Min(_model.CaretOffset, _document.Length);
            _model.Anchor = -1;
            _model.Nibble = 0;
            UpdateScrollBars();
            Invalidate();
            OnStateChanged();
        }
    }

    public int Length => _document.Length;

    public int CaretOffset => _model.CaretOffset;

    // The ASCII column is where typing goes (set by a click there); tests/HexEditorControlTests puts the caret in it without a mouse.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool AsciiAreaActive
    {
        get
        {
            return _model.Area == HexEditArea.Ascii;
        }
        set
        {
            _model.Area = value ? HexEditArea.Ascii : HexEditArea.Hex;
        }
    }

    /// <summary>The text cannot be changed (a read-only file, a preview of a huge file).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly
    {
        get
        {
            return _model.ReadOnly;
        }
        set
        {
            if (_model.ReadOnly == value)
            {
                return;
            }
            _model.ReadOnly = value;
            Invalidate();
            OnStateChanged();
        }
    }

    /// <summary>Typing replaces the byte under the caret instead of inserting a new one.</summary>
    public bool OverwriteMode => _model.Overwrite;

    public bool InsertMode => !_model.Overwrite;

    public bool HasSelection => _model.Anchor >= 0 && _model.Anchor != _model.CaretOffset;

    public int SelectionStart => HasSelection ? Math.Min(_model.Anchor, _model.CaretOffset) : _model.CaretOffset;

    public int SelectionEnd => HasSelection ? Math.Max(_model.Anchor, _model.CaretOffset) : _model.CaretOffset;

    public int SelectionLength => HasSelection ? Math.Abs(_model.CaretOffset - _model.Anchor) : 0;

    public bool CanUndo => !_model.ReadOnly && _document.CanUndo;

    public bool CanRedo => !_model.ReadOnly && _document.CanRedo;

    public bool IsModified => _document.IsModified;

    public string LastSearchText => _model.LastSearchText;

    /// <summary>The number of bytes in a row: 8, 16 or 32.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int BytesPerRow
    {
        get
        {
            return _model.BytesPerRow;
        }
        set
        {
            int bytesPerRow = Array.IndexOf(BytesPerRowChoices, value) >= 0 ? value : 16;
            if (bytesPerRow == _model.BytesPerRow)
            {
                return;
            }
            _model.BytesPerRow = bytesPerRow;
            _layout.GroupSize = Math.Min(_layout.GroupSize, _model.BytesPerRow);
            OnLayoutOptionChanged();
            OnViewOptionsChanged();
        }
    }

    /// <summary>The bytes shown together without a space: 1 (single bytes), 2, 4 or 8 (16-, 32- and 64-bit words).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int GroupSize
    {
        get
        {
            return _layout.GroupSize;
        }
        set
        {
            int groupSize = Array.IndexOf(GroupSizeChoices, value) >= 0 ? Math.Min(value, _model.BytesPerRow) : 1;
            if (groupSize == _layout.GroupSize)
            {
                return;
            }
            _layout.GroupSize = groupSize;
            OnLayoutOptionChanged();
            OnViewOptionsChanged();
        }
    }

    /// <summary>
    /// Numbers are read and written most significant byte first (network order, PowerPC, MIPS big endian); otherwise
    /// least significant first (x86, ARM).
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool BigEndian
    {
        get
        {
            return _model.BigEndian;
        }
        set
        {
            if (_model.BigEndian == value)
            {
                return;
            }
            _model.BigEndian = value;
            _toolTipOffset = -1;
            Invalidate();
            _inspector.Invalidate();
            OnStateChanged();
            OnViewOptionsChanged();
        }
    }

    /// <summary>
    /// The bytes are coloured by their kind: zero bytes are dim, 0xFF, control and non-ASCII bytes have colours of
    /// their own, so that padding, erased flash and text stand out.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ColorByValue
    {
        get
        {
            return _colorByValue;
        }
        set
        {
            if (_colorByValue == value)
            {
                return;
            }
            _colorByValue = value;
            Invalidate();
            OnStateChanged();
            OnViewOptionsChanged();
        }
    }

    /// <summary>The data inspector (the value at the caret as numbers, time and text) is shown beside the bytes.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowDataInspector
    {
        get
        {
            return _showInspector;
        }
        set
        {
            if (_showInspector == value)
            {
                return;
            }
            _showInspector = value;
            UpdateInspectorVisibility();
            OnStateChanged();
            OnViewOptionsChanged();
        }
    }

    /// <summary>Sets the view options saved in the settings, without reporting them as changed.</summary>
    public void LoadViewOptions(int bytesPerRow, int groupSize, bool bigEndian, bool colorByValue, bool showDataInspector)
    {
        _model.BytesPerRow = Array.IndexOf(BytesPerRowChoices, bytesPerRow) >= 0 ? bytesPerRow : 16;
        _layout.GroupSize = Array.IndexOf(GroupSizeChoices, groupSize) >= 0 ? Math.Min(groupSize, _model.BytesPerRow) : 1;
        _model.BigEndian = bigEndian;
        _colorByValue = colorByValue;
        _showInspector = showDataInspector;
        UpdateInspectorVisibility();
        OnLayoutOptionChanged();
        OnStateChanged();
    }

    /// <summary>The coloured block under the caret, or null.</summary>
    public HexHighlight HighlightAtCaret => _document.GetHighlightAt(_model.CaretOffset);

    public bool HasHighlights => _document.Highlights.Count > 0;

    /// <summary>There are bytes to mark: the selection or the byte under the caret.</summary>
    public bool CanHighlight => HasSelection || _model.CaretOffset < _document.Length;

    private int VisibleRows => _layout.VisibleRows;

    private int VisibleWidth => _layout.VisibleWidth;

    private int RowCount => _layout.RowCount;

    /// <summary>Copies the selected bytes (the byte under the caret without a selection); false when nothing reached the clipboard.</summary>
    public bool CopySelection()
    {
        try
        {
            byte[] bytes = _model.GetSelectedBytes();
            if (bytes.Length == 0)
            {
                return false;
            }
            Clipboard.SetText(HexDocument.FormatHex(bytes));
            return true;
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return false;
        }
    }

    public void CopyAll()
    {
        try
        {
            Clipboard.SetText(HexDocument.FormatHex(_document.ToArray()));
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    public void Cut()
    {
        if (_model.ReadOnly)
        {
            return;
        }
        if (!HasSelection && _model.CaretOffset >= _document.Length)
        {
            return;
        }
        // The bytes are removed only when they are on the clipboard.
        if (CopySelection())
        {
            DeleteSelection();
        }
    }

    public void DeleteSelection()
    {
        if (_model.ReadOnly)
        {
            return;
        }
        _model.DeleteCurrentSelectionOrByte();
    }

    public void SelectAll()
    {
        _model.SelectAll();
    }

    /// <summary>Copies the selected bytes (the byte under the caret without a selection) as <paramref name="format"/>.</summary>
    public bool CopyAs(HexCopyFormat format)
    {
        try
        {
            byte[] bytes = _model.GetSelectedBytes();
            if (bytes.Length == 0)
            {
                return false;
            }
            string text = HexValueFormats.Format(bytes, format);
            if (text.Length == 0)
            {
                return false;
            }
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return false;
        }
    }

    /// <summary>
    /// The bytes the checksums are computed for: the selection, or the whole file without one.
    /// <paramref name="description"/> says which.
    /// </summary>
    public byte[] GetChecksumBytes(out string description)
    {
        return _model.GetChecksumBytes(out description);
    }

    /// <summary>Repeats <paramref name="pattern"/> over the selected bytes (one undo step); false without a selection.</summary>
    public bool FillSelection(byte[] pattern)
    {
        return _model.FillSelection(pattern);
    }

    /// <summary>
    /// Inserts <paramref name="count"/> bytes that repeat <paramref name="pattern"/> at the caret (before the selection;
    /// also in overwrite mode) and selects them.
    /// </summary>
    public bool InsertPattern(int count, byte[] pattern)
    {
        return _model.InsertPattern(count, pattern);
    }

    /// <summary>
    /// Reverses the byte order of each <paramref name="size"/>-byte word in the selection (one undo step). The selection
    /// must hold whole words.
    /// </summary>
    public bool SwapSelectionByteOrder(int size, out string error)
    {
        return _model.SwapSelectionByteOrder(size, out error);
    }

    /// <summary>
    /// Writes <paramref name="text"/>, read as <paramref name="type"/> in the current byte order, over the bytes at the
    /// caret (one undo step). The bytes past the end of the file are added.
    /// </summary>
    public bool WriteValue(HexValueType type, string text, out string error)
    {
        return _model.WriteValue(type, text, out error);
    }

    /// <summary>Up to <paramref name="count"/> bytes from the caret (from the start of the selection).</summary>
    public byte[] GetBytesAtCaret(int count)
    {
        return _model.GetBytesAtCaret(count);
    }

    /// <summary>Marks the selected bytes (the byte under the caret without a selection) with a colour and a label.</summary>
    public bool HighlightSelection(Color color, string label)
    {
        return _model.HighlightSelection(Color.FromArgb(255, color).ToArgb(), label);
    }

    /// <summary>Removes the colour from the selected bytes, or the whole block under the caret without a selection.</summary>
    public bool RemoveHighlight()
    {
        return _model.RemoveHighlight();
    }

    public void ClearHighlights()
    {
        _model.ClearHighlights();
    }

    /// <summary>Selects the next coloured block after the caret (the previous one when <paramref name="forward"/> is false).</summary>
    public bool GoToHighlight(bool forward)
    {
        if (!_model.GoToHighlight(forward))
        {
            SystemSounds.Beep.Play();
            return false;
        }
        return true;
    }

    internal static string FormatByteCount(int count)
    {
        return HexEditingModel.FormatByteCount(count);
    }

    public void Paste()
    {
        if (_model.ReadOnly)
        {
            return;
        }
        try
        {
            string text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            byte[] bytes = HexPasteText.ToBytes(text, _model.Area, question =>
            {
                DialogResult answer = ThemedMessageBox.Show(question, "Paste", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                return answer == DialogResult.Cancel ? HexPasteChoice.Cancel : (answer == DialogResult.Yes ? HexPasteChoice.Bytes : HexPasteChoice.Text);
            });
            if (bytes == null)
            {
                return;
            }
            _model.InsertBytes(bytes);
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    public void Undo()
    {
        _model.Undo();
    }

    public void Redo()
    {
        _model.Redo();
    }

    public void ToggleOverwriteMode()
    {
        _model.ToggleOverwriteMode();
    }

    /// <summary>Moves the caret to <paramref name="offset"/> (clamped to the document) and selects nothing.</summary>
    public void GoToOffset(int offset)
    {
        _model.GoToOffset(offset);
        Focus();
    }

    /// <summary>
    /// Finds <paramref name="pattern"/> and selects it. The search starts after the selection (before it when
    /// <paramref name="forward"/> is false) and, with <paramref name="wrap"/>, continues from the other end.
    /// </summary>
    public bool Search(byte[] pattern, bool forward, bool ignoreCase, bool wrap)
    {
        int offset = _model.Search(pattern, forward, ignoreCase, wrap);
        if (offset == HexEditingModel.SearchSkipped)
        {
            return false;
        }
        if (offset < 0)
        {
            SearchFinished?.Invoke(this, new HexSearchEventArgs { Found = false });
            return false;
        }
        SearchFinished?.Invoke(this, new HexSearchEventArgs { Found = true, Offset = offset, Length = pattern.Length });
        return true;
    }

    /// <summary>Repeats the last search (F3 and Shift+F3).</summary>
    public bool FindNext(bool forward)
    {
        if (_model.LastPattern == null)
        {
            FindRequested?.Invoke(this, EventArgs.Empty);
            return false;
        }
        bool found = Search(_model.LastPattern, forward, _model.LastIgnoreCase, _model.LastWrap);
        if (!found)
        {
            SystemSounds.Beep.Play();
        }
        return found;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }
        HexKeyAction action = HexKeyMap.Map(e.KeyCode.ToVirtualKey(), e.Control, e.Shift, e.Alt, out bool extendSelection);
        if (action == HexKeyAction.None)
        {
            return;
        }
        if (!HexKeyMap.Apply(_model, action, extendSelection, VisibleRows))
        {
            switch (action)
            {
                case HexKeyAction.SelectAll:
                    SelectAll();
                    break;
                case HexKeyAction.Copy:
                    CopySelection();
                    break;
                case HexKeyAction.Cut:
                    Cut();
                    break;
                case HexKeyAction.Paste:
                    Paste();
                    break;
                case HexKeyAction.Undo:
                    Undo();
                    break;
                case HexKeyAction.Redo:
                    Redo();
                    break;
                case HexKeyAction.Find:
                    FindRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case HexKeyAction.GoToOffset:
                    GoToOffsetRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case HexKeyAction.ToggleDataInspector:
                    ShowDataInspector = !ShowDataInspector;
                    break;
                case HexKeyAction.Save:
                    SaveRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case HexKeyAction.ToggleOverwrite:
                    ToggleOverwriteMode();
                    break;
                case HexKeyAction.FindNext:
                    FindNext(true);
                    break;
                case HexKeyAction.FindPrevious:
                    FindNext(false);
                    break;
                case HexKeyAction.NextHighlight:
                    GoToHighlight(true);
                    break;
                case HexKeyAction.PreviousHighlight:
                    GoToHighlight(false);
                    break;
            }
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (e.Handled)
        {
            return;
        }
        if (_model.TypeCharacter(e.KeyChar))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// While the data inspector is hidden, the header shows a link that opens it, at its right end when it does not
    /// cover the column titles.
    /// </summary>
    private void DrawInspectorLink(Graphics graphics, int width)
    {
        _inspectorLinkBounds = Rectangle.Empty;
        if (_inspectorShown)
        {
            return;
        }
        string text = _showInspector ? "Data inspector: the window is too narrow" : "Data inspector (Ctrl+I)";
        Size size = TextRenderer.MeasureText(graphics, text, Font, new Size(int.MaxValue, int.MaxValue), TextFlags);
        int x = width - size.Width - Margin;
        int titlesEnd = _layout.AsciiStart - _layout.ScrollX + 6 * _layout.CharWidth;
        if (x < titlesEnd)
        {
            return;
        }
        bool dark = BackColor.GetBrightness() < 0.5f;
        Color link = dark ? Color.FromArgb(120, 180, 255) : Color.FromArgb(0, 90, 200);
        TextRenderer.DrawText(graphics, text, Font, new Point(x, 2), _showInspector ? Blend(BackColor, ForeColor, 0.55f) : link, TextFlags);
        if (!_showInspector)
        {
            _inspectorLinkBounds = new Rectangle(x, 0, size.Width, _layout.HeaderHeight - 2);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Left && _inspectorLinkBounds.Contains(e.Location))
        {
            ShowDataInspector = true;
            return;
        }
        if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right)
        {
            return;
        }
        if (e.Y < _layout.HeaderHeight)
        {
            return; // the column titles are not bytes: a click there used to put the caret on the row above the first visible one
        }
        int offset = _layout.OffsetFromPoint(e.X, e.Y, out HexEditArea area, out int nibble);
        bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;
        switch (_pointer.Press(e.Button == MouseButtons.Left ? HexPointerButton.Left : HexPointerButton.Right, offset, area, nibble, shift))
        {
            case HexPressResult.KeptSelection:
                Invalidate();
                return;
            case HexPressResult.MovedCaret:
                _caretVisible = true;
                EnsureVisible();
                Invalidate();
                OnStateChanged();
                return;
            case HexPressResult.StartedDrag:
                _autoScrollTimer.Start();
                return;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pointer.Dragging || e.Button != MouseButtons.Left)
        {
            Cursor = _inspectorLinkBounds.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            UpdateToolTip(e.Location);
            return;
        }
        // Beyond the table the selection ends at its edge and the timer does the scrolling: the caret is not put where the mouse is
        // (a long way off) and the table is not thrown there.
        int x = e.X;
        int y = e.Y;
        _layout.ClampToTable(ref x, ref y, out bool outside);
        DragTo(x, y, ensureVisible: !outside);
    }

    private void DragTo(int x, int y, bool ensureVisible)
    {
        int offset = _layout.OffsetFromPoint(x, y, out HexEditArea area, out _);
        _pointer.DragTo(offset, area);
        _caretVisible = true;
        if (ensureVisible)
        {
            EnsureVisible();
        }
        Invalidate();
        OnStateChanged();
    }

    private void OnAutoScrollTick()
    {
        if (!_pointer.Dragging || (Control.MouseButtons & MouseButtons.Left) == 0)
        {
            // The button was let go where the control did not see it.
            _pointer.Release();
            _autoScrollTimer.Stop();
            return;
        }
        Point point = PointToClient(Control.MousePosition);
        _layout.AutoScrollStep(point.X, point.Y, out int rows, out int pixels);
        if (rows == 0 && pixels == 0)
        {
            return;
        }
        if (rows != 0)
        {
            SetFirstRow(_layout.FirstRow + rows);
        }
        if (pixels != 0)
        {
            _layout.ScrollX += pixels;
            UpdateScrollBarValues();
        }
        int x = point.X;
        int y = point.Y;
        _layout.ClampToTable(ref x, ref y, out _);
        DragTo(x, y, ensureVisible: false);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pointer.Release();
        _autoScrollTimer.Stop();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _toolTipOffset = -1;
        _toolTip.SetToolTip(this, null);
    }

    /// <summary>
    /// Describes the byte under the mouse: its offset, the word it belongs to read in the current byte order, and the
    /// coloured block it is in.
    /// </summary>
    private void UpdateToolTip(Point location)
    {
        int offset = ByteAtPoint(location);
        if (offset == _toolTipOffset)
        {
            return;
        }
        _toolTipOffset = offset;
        _toolTip.SetToolTip(this, (offset >= 0) ? DescribeByte(offset) : null);
    }

    private string DescribeByte(int offset)
    {
        return HexByteText.Describe(_document, offset, _layout.GroupSize, _model.BigEndian);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (ModifierKeys == Keys.None || ModifierKeys == Keys.Shift)
        {
            // Positive wheel delta scrolls up (towards the start), negative down. A touchpad sends many small deltas: they add up to notches.
            int rows = _layout.WheelRows(e.Delta);
            if (rows == 0)
            {
                return;
            }
            if ((ModifierKeys & Keys.Shift) == Keys.Shift)
            {
                // Kept inside the content at both ends. The scroll bar alone cannot do that: with no bar (a wide window) or with the
                // bar at its end its value does not change, so it raises no event that would bring a position past the end back.
                _layout.ScrollX -= rows * _layout.CharWidth * 3;
                UpdateScrollBarValues();
            }
            else
            {
                SetFirstRow(_layout.FirstRow - rows);
            }
            Invalidate();
        }
        else
        {
            base.OnMouseWheel(e);
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _caretVisible = true;
        _caretTimer.Start();
        Invalidate();
        OnStateChanged();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _caretTimer.Stop();
        Invalidate();
        OnStateChanged();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateInspectorVisibility();
        UpdateScrollBars();
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateMetrics();
        if (_inspector != null)
        {
            _inspector.Font = Font;
        }
        UpdateInspectorVisibility();
        UpdateScrollBars();
        Invalidate();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        _inspector?.Invalidate();
        Invalidate();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        _inspector?.Invalidate();
        Invalidate();
    }

    /// <summary>Shows the data inspector when it is switched on and leaves room for the bytes.</summary>
    private void UpdateInspectorVisibility()
    {
        if (_inspector == null)
        {
            return;
        }
        _inspector.Width = _inspector.PreferredWidth;
        // In a narrow window the bytes come first: the inspector returns when the window grows.
        bool visible = _showInspector && _layout.InspectorFits(_inspector.Width);
        if (!visible)
        {
            // The header link changes with it.
            Invalidate();
        }
        if (_inspectorShown != visible)
        {
            _inspectorShown = visible;
            _inspector.Visible = visible;
            UpdateScrollBars();
            Invalidate();
        }
    }

    /// <summary>The row width or the groups changed: the columns move.</summary>
    private void OnLayoutOptionChanged()
    {
        _toolTipOffset = -1;
        UpdateMetrics();
        UpdateInspectorVisibility();
        UpdateScrollBars();
        EnsureVisible();
        Invalidate();
    }

    /// <summary>The colours of the bytes by kind, chosen for the brightness of the background.</summary>
    private Color ByteTextColor(byte value)
    {
        return HexByteText.TextColor(value, _colorByValue, BackColor.ToRgb(), ForeColor.ToRgb()).ToColor();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.Clear(BackColor);
        int width = VisibleWidth;
        using SolidBrush dimBrush = new SolidBrush(Blend(BackColor, ForeColor, 0.55f));
        using SolidBrush selectionBrush = new SolidBrush(SystemColors.Highlight);
        using SolidBrush selectionTextBrush = new SolidBrush(SystemColors.HighlightText);
        using SolidBrush changedBrush = new SolidBrush(Color.FromArgb(50, 255, 150, 40));
        using SolidBrush changedTextBrush = new SolidBrush(Blend(BackColor, Color.Orange, 0.25f));
        using SolidBrush caretBrush = new SolidBrush(Color.Orange);
        using Pen separatorPen = new Pen(Blend(BackColor, ForeColor, 0.35f));
        Dictionary<int, SolidBrush> highlightBrushes = new Dictionary<int, SolidBrush>();
        try
        {
            // The offset column scrolls horizontally together with the hex and ASCII columns.
            TextRenderer.DrawText(graphics, "OFFSET", Font, new Point(Margin - _layout.ScrollX, 2), dimBrush.Color, TextFlags);
            for (int index = 0; index < _model.BytesPerRow; index++)
            {
                int x = _layout.HexCellX(index) - _layout.ScrollX;
                if (x + _layout.CharWidth * 2 < 0 || x > width)
                {
                    continue;
                }
                TextRenderer.DrawText(graphics, index.ToString("X2", CultureInfo.InvariantCulture), Font, new Point(x, 2), dimBrush.Color, TextFlags);
            }
            TextRenderer.DrawText(graphics, "ASCII", Font, new Point(_layout.AsciiStart - _layout.ScrollX, 2), dimBrush.Color, TextFlags);
            DrawInspectorLink(graphics, width);
            graphics.DrawLine(separatorPen, 0, _layout.HeaderHeight - 2, width, _layout.HeaderHeight - 2);

            int textYOffset = Math.Max(0, (_layout.RowHeight - Font.Height) / 2);
            int selectionStart = HasSelection ? SelectionStart : -1;
            int selectionEnd = HasSelection ? SelectionEnd : -1;
            using Region previousRegion = graphics.Clip.Clone();
            graphics.SetClip(new Rectangle(0, _layout.HeaderHeight, width, Math.Max(0, ClientSize.Height - _layout.HeaderHeight - (_horizontalScrollBar.Visible ? _horizontalScrollBar.Height : 0))));
            try
            {
                for (int row = _layout.FirstRow; row < RowCount; row++)
                {
                    int y = _layout.HeaderHeight + (row - _layout.FirstRow) * _layout.RowHeight;
                    if (y >= ClientSize.Height)
                    {
                        break;
                    }
                    int offset = row * _model.BytesPerRow;
                    if (offset >= _document.Length)
                    {
                        break;
                    }
                    string offsetText = offset.ToString("X8", CultureInfo.InvariantCulture);
                    TextRenderer.DrawText(graphics, offsetText, Font, new Point(Margin - _layout.ScrollX, y + textYOffset), dimBrush.Color, TextFlags);
                    HexHighlight lastHighlight = null;
                    for (int index = 0; index < _model.BytesPerRow; index++)
                    {
                        int position = offset + index;
                        if (position >= _document.Length)
                        {
                            break;
                        }
                        int x = _layout.HexCellX(index) - _layout.ScrollX;
                        bool selected = selectionStart >= 0 && position >= selectionStart && position < selectionEnd;
                        bool changed = _document.IsByteChanged(position);
                        HexHighlight highlight = (index == 0 || lastHighlight == null || !lastHighlight.Contains(position))
                            ? _document.GetHighlightAt(position)
                            : lastHighlight;
                        lastHighlight = highlight;
                        // A selection or a coloured block continues over the gap to the next byte of the same row.
                        bool joinsNext = index + 1 < _model.BytesPerRow && position + 1 < _document.Length
                            && (selected ? position + 1 < selectionEnd : (highlight != null && highlight.Contains(position + 1)));
                        int fillWidth = joinsNext ? _layout.HexCellX(index + 1) - _layout.HexCellX(index) : _layout.CharWidth * 2;
                        Brush background = selected
                            ? selectionBrush
                            : (highlight != null ? GetHighlightBrush(highlightBrushes, highlight.Argb) : (changed ? changedBrush : null));
                        if (background != null)
                        {
                            graphics.FillRectangle(background, x, y, fillWidth, _layout.RowHeight);
                        }
                        byte value = _document[position];
                        Color color = selected ? selectionTextBrush.Color : (changed ? changedTextBrush.Color : ByteTextColor(value));
                        TextRenderer.DrawText(graphics, value.ToString("X2", CultureInfo.InvariantCulture), Font, new Point(x, y + textYOffset), color, TextFlags);
                        if (Focused && _caretVisible && _model.CaretOffset == position && _model.Area == HexEditArea.Hex && !selected)
                        {
                            int caretX = x + _model.Nibble * _layout.CharWidth;
                            graphics.FillRectangle(caretBrush, caretX, y + _layout.RowHeight - 2, _layout.CharWidth, 2);
                        }
                        int asciiX = _layout.AsciiStart - _layout.ScrollX + index * _layout.CharWidth;
                        if (Focused && _caretVisible && _model.CaretOffset == position && _model.Area == HexEditArea.Ascii && !selected)
                        {
                            graphics.FillRectangle(caretBrush, asciiX, y + _layout.RowHeight - 2, _layout.CharWidth, 2);
                        }
                        if (background != null)
                        {
                            graphics.FillRectangle(background, asciiX, y, _layout.CharWidth, _layout.RowHeight);
                        }
                        string ascii = HexDocument.IsPrintable(value) ? ((char)value).ToString() : ".";
                        TextRenderer.DrawText(graphics, ascii, Font, new Point(asciiX, y + textYOffset), color, TextFlags);
                    }
                }
                if (_document.Length == 0)
                {
                    TextRenderer.DrawText(graphics, "The file is empty. Type hexadecimal digits to add bytes.", Font, new Point(Margin - _layout.ScrollX, _layout.HeaderHeight + textYOffset), dimBrush.Color, TextFlags);
                }
                if (Focused && _caretVisible && _model.CaretOffset == _document.Length)
                {
                    int row = _model.CaretOffset / _model.BytesPerRow;
                    int column = _model.CaretOffset % _model.BytesPerRow;
                    if (row >= _layout.FirstRow)
                    {
                        int y = _layout.HeaderHeight + (row - _layout.FirstRow) * _layout.RowHeight;
                        int x = (_model.Area == HexEditArea.Ascii)
                            ? _layout.AsciiStart - _layout.ScrollX + column * _layout.CharWidth
                            : _layout.HexCellX(column) - _layout.ScrollX;
                        graphics.FillRectangle(caretBrush, x, y + _layout.RowHeight - 2, _layout.CharWidth, 2);
                    }
                }
            }
            finally
            {
                graphics.SetClip(previousRegion, CombineMode.Replace);
            }
        }
        finally
        {
            foreach (SolidBrush brush in highlightBrushes.Values)
            {
                brush.Dispose();
            }
        }
    }

    private static SolidBrush GetHighlightBrush(Dictionary<int, SolidBrush> brushes, int argb)
    {
        if (!brushes.TryGetValue(argb, out SolidBrush brush))
        {
            brush = new SolidBrush(Color.FromArgb(HighlightAlpha, Color.FromArgb(argb)));
            brushes[argb] = brush;
        }
        return brush;
    }

    /// <summary>The byte under <paramref name="point"/> in the hex or the ASCII column, or -1.</summary>
    private int ByteAtPoint(Point point)
    {
        return _layout.ByteAtPoint(point.X, point.Y);
    }

    private void OnScrollBarChanged()
    {
        if (_updatingScrollBars)
        {
            return;
        }
        _layout.FirstRow = _verticalScrollBar.Value;
        _layout.ScrollX = _horizontalScrollBar.Value;
        Invalidate();
    }

    private void SetFirstRow(int row)
    {
        _layout.FirstRow = _layout.ClampFirstRow(row);
        _updatingScrollBars = true;
        try
        {
            _verticalScrollBar.Value = _layout.FirstRow;
        }
        finally
        {
            _updatingScrollBars = false;
        }
        Invalidate();
    }

    private void EnsureVisible()
    {
        _layout.EnsureVisible();
        UpdateScrollBarValues();
    }

    private void UpdateScrollBars()
    {
        if (_updatingLayout || IsDisposed)
        {
            return;
        }
        _updatingLayout = true;
        try
        {
            _layout.ChooseScrollBars(out bool needVertical, out bool needHorizontal);
            _verticalScrollBar.Visible = needVertical;
            _horizontalScrollBar.Visible = needHorizontal;
            UpdateScrollBarValues();
        }
        finally
        {
            _updatingLayout = false;
        }
    }

    private void UpdateScrollBarValues()
    {
        _updatingScrollBars = true;
        try
        {
            int rowCount = RowCount;
            int visibleRows = VisibleRows;
            _verticalScrollBar.Minimum = 0;
            _verticalScrollBar.Maximum = Math.Max(0, rowCount - 1);
            _verticalScrollBar.LargeChange = Math.Max(1, Math.Min(visibleRows, rowCount));
            _verticalScrollBar.SmallChange = 1;
            _layout.FirstRow = _layout.ClampFirstRow(_layout.FirstRow);
            _verticalScrollBar.Value = _layout.FirstRow;

            int width = VisibleWidth;
            _horizontalScrollBar.Minimum = 0;
            _horizontalScrollBar.Maximum = Math.Max(0, _layout.ContentWidth - 1);
            _horizontalScrollBar.LargeChange = Math.Max(1, Math.Min(width, Math.Max(1, _layout.ContentWidth)));
            _horizontalScrollBar.SmallChange = Math.Max(1, _layout.CharWidth);
            _layout.ScrollX = HexScroll.ClampScrollX(_layout.ScrollX, _layout.ContentWidth, width);
            _horizontalScrollBar.Value = _layout.ScrollX;
        }
        finally
        {
            _updatingScrollBars = false;
        }
    }

    private void UpdateMetrics()
    {
        Size size = TextRenderer.MeasureText("0", Font, new Size(1000, 1000), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        _layout.SetMetrics(size.Width, size.Height);
    }

    // What the model asked for after an operation (the follow-up of the old AfterContentChange and AfterCaretChange), and then what every change needs.
    private void OnModelChanged(HexChangeEffects effects)
    {
        if ((effects & HexChangeEffects.ShowCaret) != 0)
        {
            _caretVisible = true;
        }
        if ((effects & HexChangeEffects.RestartCaretBlink) != 0)
        {
            _caretTimer.Stop();
            _caretTimer.Start();
        }
        if ((effects & HexChangeEffects.EnsureCaretVisible) != 0)
        {
            EnsureVisible();
        }
        Invalidate();
        OnStateChanged();
    }

    private void OnDocumentChanged(object sender, EventArgs e)
    {
        if (_model.CaretOffset > _document.Length)
        {
            _model.CaretOffset = _document.Length;
        }
        if (_model.Anchor > _document.Length)
        {
            _model.Anchor = _document.Length;
        }
        if (_model.Anchor == _model.CaretOffset)
        {
            _model.Anchor = -1;
        }
        UpdateScrollBars();
        Invalidate();
        OnStateChanged();
    }

    private void OnDocumentModifiedChanged(object sender, EventArgs e)
    {
        Invalidate();
        OnStateChanged();
    }

    private void OnDocumentHighlightsChanged(object sender, EventArgs e)
    {
        _toolTipOffset = -1;
        Invalidate();
        OnStateChanged();
    }

    private void OnStateChanged()
    {
        _inspector?.Invalidate();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewOptionsChanged()
    {
        ViewOptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidateCaretArea()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }
        Invalidate();
    }

    /// <summary>A colour between <paramref name="background"/> and <paramref name="foreground"/>.</summary>
    internal static Color Blend(Color background, Color foreground, float foregroundShare)
    {
        return HexByteText.Blend(background.ToRgb(), foreground.ToRgb(), foregroundShare).ToColor();
    }

    // The sizes the layout reads (HexViewLayout).
    int IHexLayoutView.ClientWidth => ClientSize.Width;

    int IHexLayoutView.ClientHeight => ClientSize.Height;

    int IHexLayoutView.InspectorWidth => _inspectorShown ? _inspector.Width : 0;

    int IHexLayoutView.VerticalBarWidth => _verticalScrollBar.Width;

    int IHexLayoutView.HorizontalBarHeight => _horizontalScrollBar.Height;

    bool IHexLayoutView.VerticalBarVisible => _verticalScrollBar.Visible;

    bool IHexLayoutView.HorizontalBarVisible => _horizontalScrollBar.Visible;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _caretTimer.Stop();
            _caretTimer.Dispose();
            _autoScrollTimer.Stop();
            _autoScrollTimer.Dispose();
            _toolTip.Dispose();
            _verticalScrollBar.Dispose();
            _horizontalScrollBar.Dispose();
            _inspector.Dispose();
            if (_document != null)
            {
                _document.Changed -= OnDocumentChanged;
                _document.ModifiedChanged -= OnDocumentModifiedChanged;
                _document.HighlightsChanged -= OnDocumentHighlightsChanged;
            }
        }
        base.Dispose(disposing);
    }
}
