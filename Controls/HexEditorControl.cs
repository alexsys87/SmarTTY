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
public sealed class HexEditorControl : Control, IHexViewStatus, IRemoteHexView
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

    private const int OffsetDigits = 8;

    private const int Margin = 6;

    private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private HexDocument _document = new HexDocument();

    // The caret, the selection, the typing and what the keys and the commands do to the bytes; this control draws it and reads its input.
    private readonly HexEditingModel _model;

    private readonly HexDataInspector _inspector;

    private readonly ToolTip _toolTip = new ToolTip();

    // The byte the tool tip describes; -1 when the mouse is not over a byte.
    private int _toolTipOffset = -1;

    private int _groupSize = 1;

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

    private bool _dragging;

    private int _dragAnchor;

    private int _firstRow;

    private int _scrollX;

    private int _charWidth = 8;

    private int _rowHeight = 18;

    private int _headerHeight = 22;

    private int _hexStart = 80;

    private int _asciiStart = 480;

    private int _contentWidth = 640;

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
            _groupSize = Math.Min(_groupSize, _model.BytesPerRow);
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
            return _groupSize;
        }
        set
        {
            int groupSize = Array.IndexOf(GroupSizeChoices, value) >= 0 ? Math.Min(value, _model.BytesPerRow) : 1;
            if (groupSize == _groupSize)
            {
                return;
            }
            _groupSize = groupSize;
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
        _groupSize = Array.IndexOf(GroupSizeChoices, groupSize) >= 0 ? Math.Min(groupSize, _model.BytesPerRow) : 1;
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

    private int VisibleRows
    {
        get
        {
            int height = ClientSize.Height - (_horizontalScrollBar.Visible ? _horizontalScrollBar.Height : 0) - _headerHeight - Margin;
            return Math.Max(1, height / Math.Max(1, _rowHeight));
        }
    }

    private int VisibleWidth => Math.Max(1, AvailableWidth - (_verticalScrollBar.Visible ? _verticalScrollBar.Width : 0));

    // The width left for the bytes beside the data inspector.
    private int AvailableWidth => ClientSize.Width - (_inspectorShown ? _inspector.Width : 0);

    // One more row than the bytes fill when the last row is full: the caret sits there after the last byte.
    private int RowCount => _document.Length / _model.BytesPerRow + 1;

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
            byte[] bytes;
            if (_model.Area == HexEditArea.Ascii)
            {
                bytes = Encoding.UTF8.GetBytes(text);
            }
            else
            {
                switch (HexDocument.ClassifyClipboard(text))
                {
                    case HexDocument.ClipboardKind.Hex:
                        bytes = HexDocument.ParseClipboardBytes(text, preferHex: true);
                        break;
                    case HexDocument.ClipboardKind.Ambiguous:
                        // "dead" is DE AD as hex and 64 65 61 64 as text: only the user knows which was copied.
                        byte[] asHex = HexDocument.ParseClipboardBytes(text, preferHex: true);
                        byte[] asText = Encoding.UTF8.GetBytes(text);
                        byte[] textStart = new byte[Math.Min(asText.Length, 8)];
                        Array.Copy(asText, textStart, textStart.Length);
                        DialogResult answer = ThemedMessageBox.Show(
                            "The clipboard holds \"" + (text.Length > 40 ? text.Substring(0, 40) + "..." : text.Trim()) + "\", which can be hexadecimal bytes or text.\n\n"
                            + "Yes: paste as bytes (" + HexDocument.FormatHex(asHex) + ")\n"
                            + "No: paste as text (" + HexDocument.FormatHex(textStart) + (asText.Length > textStart.Length ? " ..." : "") + ")",
                            "Paste", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                        if (answer == DialogResult.Cancel)
                        {
                            return;
                        }
                        bytes = (answer == DialogResult.Yes) ? asHex : asText;
                        break;
                    default:
                        bytes = Encoding.UTF8.GetBytes(text);
                        break;
                }
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
        bool control = e.Control && !e.Alt;
        bool shift = e.Shift && !e.Alt;
        if (control)
        {
            switch (e.KeyCode)
            {
                case Keys.A:
                    SelectAll();
                    break;
                case Keys.C:
                    CopySelection();
                    break;
                case Keys.X:
                    Cut();
                    break;
                case Keys.V:
                    Paste();
                    break;
                case Keys.Z:
                    if (e.Shift)
                    {
                        Redo();
                    }
                    else
                    {
                        Undo();
                    }
                    break;
                case Keys.Y:
                    Redo();
                    break;
                case Keys.F:
                    FindRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case Keys.G:
                    GoToOffsetRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case Keys.I:
                    ShowDataInspector = !ShowDataInspector;
                    break;
                case Keys.S:
                    SaveRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case Keys.Home:
                    _model.MoveTo(0, shift, 0);
                    break;
                case Keys.End:
                    _model.MoveTo(_document.Length, shift, 0);
                    break;
                default:
                    return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        switch (e.KeyCode)
        {
            case Keys.Left:
                _model.MoveHorizontally(left: true, extendSelection: shift);
                break;
            case Keys.Right:
                _model.MoveHorizontally(left: false, extendSelection: shift);
                break;
            case Keys.Up:
                _model.MoveVertically(-1, shift);
                break;
            case Keys.Down:
                _model.MoveVertically(1, shift);
                break;
            case Keys.PageUp:
                _model.MoveVertically(-Math.Max(1, VisibleRows - 1), shift);
                break;
            case Keys.PageDown:
                _model.MoveVertically(Math.Max(1, VisibleRows - 1), shift);
                break;
            case Keys.Home:
                _model.MoveTo((_model.CaretOffset / _model.BytesPerRow) * _model.BytesPerRow, shift, _model.Nibble);
                break;
            case Keys.End:
                _model.MoveTo(Math.Min(_document.Length, (_model.CaretOffset / _model.BytesPerRow) * _model.BytesPerRow + _model.BytesPerRow - 1), shift, _model.Nibble);
                break;
            case Keys.Back:
                _model.Backspace();
                break;
            case Keys.Delete:
                _model.DeleteCurrentSelectionOrByte();
                break;
            case Keys.Insert:
                ToggleOverwriteMode();
                break;
            case Keys.F3:
                FindNext(!shift);
                break;
            case Keys.F2:
                GoToHighlight(!shift);
                break;
            default:
                return;
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
        int titlesEnd = _asciiStart - _scrollX + 6 * _charWidth;
        if (x < titlesEnd)
        {
            return;
        }
        bool dark = BackColor.GetBrightness() < 0.5f;
        Color link = dark ? Color.FromArgb(120, 180, 255) : Color.FromArgb(0, 90, 200);
        TextRenderer.DrawText(graphics, text, Font, new Point(x, 2), _showInspector ? Blend(BackColor, ForeColor, 0.55f) : link, TextFlags);
        if (!_showInspector)
        {
            _inspectorLinkBounds = new Rectangle(x, 0, size.Width, _headerHeight - 2);
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
        if (e.Y < _headerHeight)
        {
            return; // the column titles are not bytes: a click there used to put the caret on the row above the first visible one
        }
        int offset = OffsetFromPoint(e.Location, out HexEditArea area, out int nibble);
        bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;
        if (e.Button == MouseButtons.Right)
        {
            _model.SecondNibblePending = false;
            // A right click inside the selection opens the menu for it (Copy, Cut, Delete) and keeps it.
            if (HasSelection && offset >= SelectionStart && offset < SelectionEnd)
            {
                _model.Area = area;
                Invalidate();
                return;
            }
            _model.Anchor = -1;
            _model.CaretOffset = offset;
            _model.Area = area;
            _model.Nibble = nibble;
            _dragging = false;
            _caretVisible = true;
            EnsureVisible();
            Invalidate();
            OnStateChanged();
            return;
        }
        _dragging = true;
        _autoScrollTimer.Start();
        // Shift+click extends the selection from its anchor, or from the caret when nothing is selected.
        _dragAnchor = shift ? (_model.Anchor >= 0 ? _model.Anchor : _model.CaretOffset) : offset;
        _model.Anchor = shift ? _dragAnchor : -1;
        _model.CaretOffset = offset;
        _model.Area = area;
        _model.Nibble = nibble;
        if (!shift)
        {
            _model.Anchor = offset;
        }
        _model.AfterCaretChange();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || e.Button != MouseButtons.Left)
        {
            Cursor = _inspectorLinkBounds.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            UpdateToolTip(e.Location);
            return;
        }
        // Beyond the table the selection ends at its edge and the timer does the scrolling: the caret is not put where the mouse is
        // (a long way off) and the table is not thrown there.
        Point clamped = ClampToTable(e.Location, out bool outside);
        DragTo(clamped, ensureVisible: !outside);
    }

    private void DragTo(Point point, bool ensureVisible)
    {
        int offset = OffsetFromPoint(point, out HexEditArea area, out _);
        _model.Anchor = _dragAnchor;
        _model.CaretOffset = offset;
        _model.Area = area;
        if (_model.Anchor == _model.CaretOffset)
        {
            _model.Anchor = -1;
        }
        _caretVisible = true;
        if (ensureVisible)
        {
            EnsureVisible();
        }
        Invalidate();
        OnStateChanged();
    }

    // The rows of bytes: below the header, above the horizontal scroll bar, left of the vertical one (and of the data inspector).
    private Rectangle TableBounds()
    {
        int bottom = ClientSize.Height - (_horizontalScrollBar.Visible ? _horizontalScrollBar.Height : 0);
        return new Rectangle(0, _headerHeight, VisibleWidth, Math.Max(1, bottom - _headerHeight));
    }

    private Point ClampToTable(Point point, out bool outside)
    {
        Rectangle table = TableBounds();
        int x = Math.Max(table.Left, Math.Min(point.X, table.Right - 1));
        int y = Math.Max(table.Top, Math.Min(point.Y, table.Bottom - 1));
        outside = x != point.X || y != point.Y;
        return new Point(x, y);
    }

    private void OnAutoScrollTick()
    {
        if (!_dragging || (Control.MouseButtons & MouseButtons.Left) == 0)
        {
            // The button was let go where the control did not see it.
            _dragging = false;
            _autoScrollTimer.Stop();
            return;
        }
        Point point = PointToClient(Control.MousePosition);
        Rectangle table = TableBounds();
        int rows = 0;
        int pixels = 0;
        if (point.Y < table.Top)
        {
            rows = -HexScroll.AutoScrollSteps(table.Top - point.Y, _rowHeight);
        }
        else if (point.Y >= table.Bottom)
        {
            rows = HexScroll.AutoScrollSteps(point.Y - table.Bottom + 1, _rowHeight);
        }
        if (point.X < table.Left)
        {
            pixels = -HexScroll.AutoScrollSteps(table.Left - point.X, _charWidth) * _charWidth * 3;
        }
        else if (point.X >= table.Right)
        {
            pixels = HexScroll.AutoScrollSteps(point.X - table.Right + 1, _charWidth) * _charWidth * 3;
        }
        if (rows == 0 && pixels == 0)
        {
            return;
        }
        if (rows != 0)
        {
            SetFirstRow(_firstRow + rows);
        }
        if (pixels != 0)
        {
            _scrollX += pixels;
            UpdateScrollBarValues();
        }
        DragTo(ClampToTable(point, out _), ensureVisible: false);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
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
        StringBuilder text = new StringBuilder();
        byte value = _document[offset];
        text.Append("Offset 0x").Append(offset.ToString("X8", CultureInfo.InvariantCulture))
            .Append(" (").Append(offset.ToString(CultureInfo.InvariantCulture)).Append(')');
        text.Append("\nByte 0x").Append(value.ToString("X2", CultureInfo.InvariantCulture))
            .Append(" = ").Append(value.ToString(CultureInfo.InvariantCulture));
        if (HexDocument.IsPrintable(value))
        {
            text.Append(" = '").Append((char)value).Append('\'');
        }
        if (_groupSize > 1)
        {
            int start = offset - offset % _groupSize;
            if (start + _groupSize <= _document.Length)
            {
                ulong word = HexValueFormats.ReadUnsigned(_document.GetBytes(start, _groupSize), 0, _groupSize, _model.BigEndian);
                text.Append("\n").Append((_groupSize * 8).ToString(CultureInfo.InvariantCulture)).Append("-bit word, ")
                    .Append(_model.BigEndian ? "big" : "little").Append(" endian: 0x")
                    .Append(word.ToString("X" + (_groupSize * 2).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture))
                    .Append(" = ").Append(word.ToString(CultureInfo.InvariantCulture));
            }
        }
        HexHighlight highlight = _document.GetHighlightAt(offset);
        if (highlight != null)
        {
            text.Append("\n").Append(highlight.Label.Length > 0 ? highlight.Label : "Marked").Append(": 0x")
                .Append(highlight.Start.ToString("X8", CultureInfo.InvariantCulture)).Append(" - 0x")
                .Append((highlight.End - 1).ToString("X8", CultureInfo.InvariantCulture)).Append(", ").Append(FormatByteCount(highlight.Length));
        }
        if (_document.IsByteChanged(offset))
        {
            text.Append("\nChanged, not saved yet");
        }
        return text.ToString();
    }
    private int _wheelRemainder;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (ModifierKeys == Keys.None || ModifierKeys == Keys.Shift)
        {
            // Positive wheel delta scrolls up (towards the start), negative down. A touchpad sends many small deltas: they add up to notches.
            int rows = HexScroll.WheelNotches(ref _wheelRemainder, e.Delta) * 3;
            if (rows == 0)
            {
                return;
            }
            if ((ModifierKeys & Keys.Shift) == Keys.Shift)
            {
                // Kept inside the content at both ends. The scroll bar alone cannot do that: with no bar (a wide window) or with the
                // bar at its end its value does not change, so it raises no event that would bring a position past the end back.
                _scrollX -= rows * _charWidth * 3;
                UpdateScrollBarValues();
            }
            else
            {
                SetFirstRow(_firstRow - rows);
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
        bool visible = _showInspector && ClientSize.Width >= _inspector.Width + _hexStart + 8 * 3 * _charWidth;
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

    /// <summary>The left edge of the hex digits of <paramref name="column"/>, before scrolling.</summary>
    private int HexCellX(int column)
    {
        return _hexStart + column * 2 * _charWidth + (column / _groupSize) * _charWidth;
    }

    /// <summary>The colours of the bytes by kind, chosen for the brightness of the background.</summary>
    private Color ByteTextColor(byte value)
    {
        if (!_colorByValue || HexDocument.IsPrintable(value))
        {
            return ForeColor;
        }
        bool dark = BackColor.GetBrightness() < 0.5f;
        if (value == 0x00)
        {
            return Blend(BackColor, ForeColor, 0.4f);
        }
        if (value == 0xFF)
        {
            return dark ? Color.FromArgb(255, 120, 120) : Color.FromArgb(190, 30, 30);
        }
        if (value < 0x20 || value == 0x7F)
        {
            return dark ? Color.FromArgb(110, 210, 140) : Color.FromArgb(0, 125, 60);
        }
        return dark ? Color.FromArgb(125, 175, 255) : Color.FromArgb(30, 80, 200);
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
            TextRenderer.DrawText(graphics, "OFFSET", Font, new Point(Margin - _scrollX, 2), dimBrush.Color, TextFlags);
            for (int index = 0; index < _model.BytesPerRow; index++)
            {
                int x = HexCellX(index) - _scrollX;
                if (x + _charWidth * 2 < 0 || x > width)
                {
                    continue;
                }
                TextRenderer.DrawText(graphics, index.ToString("X2", CultureInfo.InvariantCulture), Font, new Point(x, 2), dimBrush.Color, TextFlags);
            }
            TextRenderer.DrawText(graphics, "ASCII", Font, new Point(_asciiStart - _scrollX, 2), dimBrush.Color, TextFlags);
            DrawInspectorLink(graphics, width);
            graphics.DrawLine(separatorPen, 0, _headerHeight - 2, width, _headerHeight - 2);

            int textYOffset = Math.Max(0, (_rowHeight - Font.Height) / 2);
            int selectionStart = HasSelection ? SelectionStart : -1;
            int selectionEnd = HasSelection ? SelectionEnd : -1;
            using Region previousRegion = graphics.Clip.Clone();
            graphics.SetClip(new Rectangle(0, _headerHeight, width, Math.Max(0, ClientSize.Height - _headerHeight - (_horizontalScrollBar.Visible ? _horizontalScrollBar.Height : 0))));
            try
            {
                for (int row = _firstRow; row < RowCount; row++)
                {
                    int y = _headerHeight + (row - _firstRow) * _rowHeight;
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
                    TextRenderer.DrawText(graphics, offsetText, Font, new Point(Margin - _scrollX, y + textYOffset), dimBrush.Color, TextFlags);
                    HexHighlight lastHighlight = null;
                    for (int index = 0; index < _model.BytesPerRow; index++)
                    {
                        int position = offset + index;
                        if (position >= _document.Length)
                        {
                            break;
                        }
                        int x = HexCellX(index) - _scrollX;
                        bool selected = selectionStart >= 0 && position >= selectionStart && position < selectionEnd;
                        bool changed = _document.IsByteChanged(position);
                        HexHighlight highlight = (index == 0 || lastHighlight == null || !lastHighlight.Contains(position))
                            ? _document.GetHighlightAt(position)
                            : lastHighlight;
                        lastHighlight = highlight;
                        // A selection or a coloured block continues over the gap to the next byte of the same row.
                        bool joinsNext = index + 1 < _model.BytesPerRow && position + 1 < _document.Length
                            && (selected ? position + 1 < selectionEnd : (highlight != null && highlight.Contains(position + 1)));
                        int fillWidth = joinsNext ? HexCellX(index + 1) - HexCellX(index) : _charWidth * 2;
                        Brush background = selected
                            ? selectionBrush
                            : (highlight != null ? GetHighlightBrush(highlightBrushes, highlight.Argb) : (changed ? changedBrush : null));
                        if (background != null)
                        {
                            graphics.FillRectangle(background, x, y, fillWidth, _rowHeight);
                        }
                        byte value = _document[position];
                        Color color = selected ? selectionTextBrush.Color : (changed ? changedTextBrush.Color : ByteTextColor(value));
                        TextRenderer.DrawText(graphics, value.ToString("X2", CultureInfo.InvariantCulture), Font, new Point(x, y + textYOffset), color, TextFlags);
                        if (Focused && _caretVisible && _model.CaretOffset == position && _model.Area == HexEditArea.Hex && !selected)
                        {
                            int caretX = x + _model.Nibble * _charWidth;
                            graphics.FillRectangle(caretBrush, caretX, y + _rowHeight - 2, _charWidth, 2);
                        }
                        int asciiX = _asciiStart - _scrollX + index * _charWidth;
                        if (Focused && _caretVisible && _model.CaretOffset == position && _model.Area == HexEditArea.Ascii && !selected)
                        {
                            graphics.FillRectangle(caretBrush, asciiX, y + _rowHeight - 2, _charWidth, 2);
                        }
                        if (background != null)
                        {
                            graphics.FillRectangle(background, asciiX, y, _charWidth, _rowHeight);
                        }
                        string ascii = HexDocument.IsPrintable(value) ? ((char)value).ToString() : ".";
                        TextRenderer.DrawText(graphics, ascii, Font, new Point(asciiX, y + textYOffset), color, TextFlags);
                    }
                }
                if (_document.Length == 0)
                {
                    TextRenderer.DrawText(graphics, "The file is empty. Type hexadecimal digits to add bytes.", Font, new Point(Margin - _scrollX, _headerHeight + textYOffset), dimBrush.Color, TextFlags);
                }
                if (Focused && _caretVisible && _model.CaretOffset == _document.Length)
                {
                    int row = _model.CaretOffset / _model.BytesPerRow;
                    int column = _model.CaretOffset % _model.BytesPerRow;
                    if (row >= _firstRow)
                    {
                        int y = _headerHeight + (row - _firstRow) * _rowHeight;
                        int x = (_model.Area == HexEditArea.Ascii)
                            ? _asciiStart - _scrollX + column * _charWidth
                            : HexCellX(column) - _scrollX;
                        graphics.FillRectangle(caretBrush, x, y + _rowHeight - 2, _charWidth, 2);
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
        if (point.Y < _headerHeight || point.X >= VisibleWidth)
        {
            return -1;
        }
        int row = (point.Y - _headerHeight) / Math.Max(1, _rowHeight) + _firstRow;
        int x = point.X + _scrollX;
        int column = -1;
        if (x >= _hexStart && x < HexCellX(_model.BytesPerRow))
        {
            for (int index = 0; index < _model.BytesPerRow; index++)
            {
                if (x >= HexCellX(index) && x < HexCellX(index) + 2 * _charWidth)
                {
                    column = index;
                    break;
                }
            }
        }
        else if (x >= _asciiStart && x < _asciiStart + _model.BytesPerRow * _charWidth)
        {
            column = (x - _asciiStart) / Math.Max(1, _charWidth);
        }
        if (column < 0)
        {
            return -1;
        }
        int offset = row * _model.BytesPerRow + column;
        return (offset < _document.Length) ? offset : -1;
    }

    private int OffsetFromPoint(Point point, out HexEditArea area, out int nibble)
    {
        int row = Math.Max(0, (point.Y - _headerHeight) / Math.Max(1, _rowHeight) + _firstRow);
        int x = point.X + _scrollX;
        area = HexEditArea.Hex;
        nibble = 0;
        int column;
        if (x < _hexStart)
        {
            column = 0;
        }
        else if (x < _asciiStart)
        {
            // The gap after a byte belongs to it: a click there puts the caret on its low digit.
            column = _model.BytesPerRow - 1;
            for (int index = 0; index < _model.BytesPerRow; index++)
            {
                if (x < HexCellX(index + 1))
                {
                    column = index;
                    break;
                }
            }
            nibble = (x - HexCellX(column) >= _charWidth) ? 1 : 0;
        }
        else
        {
            area = HexEditArea.Ascii;
            column = Math.Max(0, Math.Min(_model.BytesPerRow - 1, (x - _asciiStart) / Math.Max(1, _charWidth)));
        }
        return Math.Max(0, Math.Min(row * _model.BytesPerRow + column, _document.Length));
    }

    private void OnScrollBarChanged()
    {
        if (_updatingScrollBars)
        {
            return;
        }
        _firstRow = _verticalScrollBar.Value;
        _scrollX = _horizontalScrollBar.Value;
        Invalidate();
    }

    private void SetFirstRow(int row)
    {
        _firstRow = Math.Max(0, Math.Min(row, Math.Max(0, RowCount - VisibleRows)));
        _updatingScrollBars = true;
        try
        {
            _verticalScrollBar.Value = _firstRow;
        }
        finally
        {
            _updatingScrollBars = false;
        }
        Invalidate();
    }

    private void EnsureVisible()
    {
        int row = Math.Min(RowCount - 1, _model.CaretOffset / _model.BytesPerRow);
        int visibleRows = VisibleRows;
        if (row < _firstRow)
        {
            _firstRow = row;
        }
        else if (row >= _firstRow + visibleRows)
        {
            _firstRow = row - visibleRows + 1;
        }
        _firstRow = Math.Max(0, Math.Min(_firstRow, Math.Max(0, RowCount - visibleRows)));
        int x = CaretX();
        int width = VisibleWidth;
        if (x < _scrollX)
        {
            _scrollX = Math.Max(0, x - Margin);
        }
        else if (x + 2 * _charWidth > _scrollX + width)
        {
            _scrollX = Math.Max(0, x + 2 * _charWidth - width + Margin);
        }
        UpdateScrollBarValues();
    }

    private int CaretX()
    {
        int column = _model.CaretOffset % _model.BytesPerRow;
        if (_model.CaretOffset >= _document.Length)
        {
            column = _document.Length % _model.BytesPerRow;
        }
        return (_model.Area == HexEditArea.Ascii) ? _asciiStart + column * _charWidth : HexCellX(column);
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
            int rowCount = RowCount;
            int visibleWithoutHorizontal = Math.Max(1, (ClientSize.Height - _headerHeight - Margin) / Math.Max(1, _rowHeight));
            bool needVertical = rowCount > visibleWithoutHorizontal;
            bool needHorizontal = _contentWidth > AvailableWidth - (needVertical ? _verticalScrollBar.Width : 0);
            if (needHorizontal)
            {
                int visibleWithHorizontal = Math.Max(1, (ClientSize.Height - _headerHeight - Margin - _horizontalScrollBar.Height) / Math.Max(1, _rowHeight));
                needVertical = rowCount > visibleWithHorizontal;
                needHorizontal = _contentWidth > AvailableWidth - (needVertical ? _verticalScrollBar.Width : 0);
            }
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
            _firstRow = Math.Max(0, Math.Min(_firstRow, Math.Max(0, rowCount - visibleRows)));
            _verticalScrollBar.Value = _firstRow;

            int width = VisibleWidth;
            _horizontalScrollBar.Minimum = 0;
            _horizontalScrollBar.Maximum = Math.Max(0, _contentWidth - 1);
            _horizontalScrollBar.LargeChange = Math.Max(1, Math.Min(width, Math.Max(1, _contentWidth)));
            _horizontalScrollBar.SmallChange = Math.Max(1, _charWidth);
            _scrollX = HexScroll.ClampScrollX(_scrollX, _contentWidth, width);
            _horizontalScrollBar.Value = _scrollX;
        }
        finally
        {
            _updatingScrollBars = false;
        }
    }

    private void UpdateMetrics()
    {
        Size size = TextRenderer.MeasureText("0", Font, new Size(1000, 1000), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        _charWidth = Math.Max(1, size.Width);
        _rowHeight = Math.Max(1, size.Height) + 3;
        _headerHeight = _rowHeight + 4;
        _hexStart = Margin + OffsetDigits * _charWidth + 2 * _charWidth;
        _asciiStart = HexCellX(_model.BytesPerRow) + 2 * _charWidth;
        _contentWidth = _asciiStart + _model.BytesPerRow * _charWidth + Margin + 1;
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
        float share = Math.Max(0f, Math.Min(1f, foregroundShare));
        return Color.FromArgb(
            (int)Math.Round(background.R * (1f - share) + foreground.R * share),
            (int)Math.Round(background.G * (1f - share) + foreground.G * share),
            (int)Math.Round(background.B * (1f - share) + foreground.B * share));
    }

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
