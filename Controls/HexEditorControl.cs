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
public sealed class HexEditorControl : Control
{
    /// <summary>The row widths the view offers.</summary>
    public static readonly int[] BytesPerRowChoices = { 8, 16, 32 };

    /// <summary>The group sizes the view offers: single bytes, 16-, 32- and 64-bit words.</summary>
    public static readonly int[] GroupSizeChoices = { 1, 2, 4, 8 };

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

    private enum EditArea
    {
        Hex,
        Ascii
    }

    private HexDocument _document = new HexDocument();

    private readonly HexDataInspector _inspector;

    private readonly ToolTip _toolTip = new ToolTip();

    // The byte the tool tip describes; -1 when the mouse is not over a byte.
    private int _toolTipOffset = -1;

    private int _bytesPerRow = 16;

    private int _groupSize = 1;

    private bool _bigEndian;

    private bool _colorByValue;

    private bool _showInspector = true;

    // Whether the inspector is laid out: Control.Visible reads false while the editor itself is hidden.
    private bool _inspectorShown;

    private readonly VScrollBar _verticalScrollBar = new VScrollBar();

    private readonly HScrollBar _horizontalScrollBar = new HScrollBar();

    private readonly System.Windows.Forms.Timer _caretTimer = new System.Windows.Forms.Timer();

    private int _caretOffset;

    // The nibble being typed: 0 is the high one, 1 the low one.
    private int _nibble;

    // The other end of the selection; -1 when nothing is selected.
    private int _anchor = -1;

    private EditArea _area = EditArea.Hex;

    private bool _overwrite = true;

    private bool _readOnly;

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

    // The first digit of a byte was just typed: the second one joins its undo step.
    private bool _secondNibblePending;

    private bool _updatingScrollBars;

    private bool _updatingLayout;

    private byte[] _lastPattern;

    private bool _lastIgnoreCase;

    private bool _lastWrap = true;

    private string _lastSearchText = "";

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
        Document = _document;
        UpdateMetrics();
        UpdateInspectorVisibility();
        UpdateScrollBars();
    }

    /// <summary>The bytes being edited.</summary>
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
            _document.Changed += OnDocumentChanged;
            _document.ModifiedChanged += OnDocumentModifiedChanged;
            _document.HighlightsChanged += OnDocumentHighlightsChanged;
            _caretOffset = Math.Min(_caretOffset, _document.Length);
            _anchor = -1;
            _nibble = 0;
            UpdateScrollBars();
            Invalidate();
            OnStateChanged();
        }
    }

    public int Length => _document.Length;

    public int CaretOffset => _caretOffset;

    /// <summary>The text cannot be changed (a read-only file, a preview of a huge file).</summary>
    public bool ReadOnly
    {
        get
        {
            return _readOnly;
        }
        set
        {
            if (_readOnly == value)
            {
                return;
            }
            _readOnly = value;
            Invalidate();
            OnStateChanged();
        }
    }

    /// <summary>Typing replaces the byte under the caret instead of inserting a new one.</summary>
    public bool OverwriteMode => _overwrite;

    public bool InsertMode => !_overwrite;

    public bool HasSelection => _anchor >= 0 && _anchor != _caretOffset;

    public int SelectionStart => HasSelection ? Math.Min(_anchor, _caretOffset) : _caretOffset;

    public int SelectionEnd => HasSelection ? Math.Max(_anchor, _caretOffset) : _caretOffset;

    public int SelectionLength => HasSelection ? Math.Abs(_caretOffset - _anchor) : 0;

    public bool CanUndo => !_readOnly && _document.CanUndo;

    public bool CanRedo => !_readOnly && _document.CanRedo;

    public bool IsModified => _document.IsModified;

    public string LastSearchText => _lastSearchText;

    /// <summary>The number of bytes in a row: 8, 16 or 32.</summary>
    public int BytesPerRow
    {
        get
        {
            return _bytesPerRow;
        }
        set
        {
            int bytesPerRow = Array.IndexOf(BytesPerRowChoices, value) >= 0 ? value : 16;
            if (bytesPerRow == _bytesPerRow)
            {
                return;
            }
            _bytesPerRow = bytesPerRow;
            _groupSize = Math.Min(_groupSize, _bytesPerRow);
            OnLayoutOptionChanged();
            OnViewOptionsChanged();
        }
    }

    /// <summary>The bytes shown together without a space: 1 (single bytes), 2, 4 or 8 (16-, 32- and 64-bit words).</summary>
    public int GroupSize
    {
        get
        {
            return _groupSize;
        }
        set
        {
            int groupSize = Array.IndexOf(GroupSizeChoices, value) >= 0 ? Math.Min(value, _bytesPerRow) : 1;
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
    public bool BigEndian
    {
        get
        {
            return _bigEndian;
        }
        set
        {
            if (_bigEndian == value)
            {
                return;
            }
            _bigEndian = value;
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
        _bytesPerRow = Array.IndexOf(BytesPerRowChoices, bytesPerRow) >= 0 ? bytesPerRow : 16;
        _groupSize = Array.IndexOf(GroupSizeChoices, groupSize) >= 0 ? Math.Min(groupSize, _bytesPerRow) : 1;
        _bigEndian = bigEndian;
        _colorByValue = colorByValue;
        _showInspector = showDataInspector;
        UpdateInspectorVisibility();
        OnLayoutOptionChanged();
        OnStateChanged();
    }

    /// <summary>The coloured block under the caret, or null.</summary>
    public HexHighlight HighlightAtCaret => _document.GetHighlightAt(_caretOffset);

    public bool HasHighlights => _document.Highlights.Count > 0;

    /// <summary>There are bytes to mark: the selection or the byte under the caret.</summary>
    public bool CanHighlight => HasSelection || _caretOffset < _document.Length;

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
    private int RowCount => _document.Length / _bytesPerRow + 1;

    /// <summary>Copies the selected bytes (the byte under the caret without a selection); false when nothing reached the clipboard.</summary>
    public bool CopySelection()
    {
        try
        {
            byte[] bytes = GetSelectedBytes();
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
        if (_readOnly)
        {
            return;
        }
        if (!HasSelection && _caretOffset >= _document.Length)
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
        if (_readOnly)
        {
            return;
        }
        DeleteCurrentSelectionOrByte();
    }

    public void SelectAll()
    {
        _anchor = 0;
        _caretOffset = _document.Length;
        _nibble = 0;
        _area = EditArea.Hex;
        EnsureVisible();
        Invalidate();
        OnStateChanged();
    }

    /// <summary>Copies the selected bytes (the byte under the caret without a selection) as <paramref name="format"/>.</summary>
    public bool CopyAs(HexCopyFormat format)
    {
        try
        {
            byte[] bytes = GetSelectedBytes();
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
        if (HasSelection)
        {
            description = "Selection 0x" + SelectionStart.ToString("X8", CultureInfo.InvariantCulture) + " - 0x"
                + (SelectionEnd - 1).ToString("X8", CultureInfo.InvariantCulture) + ", " + FormatByteCount(SelectionLength);
            return _document.GetBytes(SelectionStart, SelectionLength);
        }
        description = "The whole file, " + FormatByteCount(_document.Length);
        return _document.ToArray();
    }

    /// <summary>Repeats <paramref name="pattern"/> over the selected bytes (one undo step); false without a selection.</summary>
    public bool FillSelection(byte[] pattern)
    {
        if (_readOnly || !HasSelection || pattern == null || pattern.Length == 0)
        {
            return false;
        }
        int start = SelectionStart;
        int count = SelectionLength;
        _document.Replace(start, count, HexValueFormats.RepeatPattern(pattern, count));
        _anchor = start;
        _caretOffset = start + count;
        _nibble = 0;
        AfterContentChange();
        return true;
    }

    /// <summary>
    /// Inserts <paramref name="count"/> bytes that repeat <paramref name="pattern"/> at the caret (before the selection;
    /// also in overwrite mode) and selects them.
    /// </summary>
    public bool InsertPattern(int count, byte[] pattern)
    {
        if (_readOnly || count <= 0 || pattern == null || pattern.Length == 0)
        {
            return false;
        }
        int start = HasSelection ? SelectionStart : _caretOffset;
        _document.Insert(start, HexValueFormats.RepeatPattern(pattern, count));
        _anchor = start;
        _caretOffset = start + count;
        _nibble = 0;
        AfterContentChange();
        return true;
    }

    /// <summary>
    /// Reverses the byte order of each <paramref name="size"/>-byte word in the selection (one undo step). The selection
    /// must hold whole words.
    /// </summary>
    public bool SwapSelectionByteOrder(int size, out string error)
    {
        error = null;
        if (_readOnly)
        {
            error = "The file cannot be changed.";
            return false;
        }
        if (!HasSelection || SelectionLength < size || SelectionLength % size != 0)
        {
            error = "Select whole " + (size * 8).ToString(CultureInfo.InvariantCulture) + "-bit words: a multiple of "
                + size.ToString(CultureInfo.InvariantCulture) + " bytes.";
            return false;
        }
        int start = SelectionStart;
        int count = SelectionLength;
        bool caretAtEnd = _caretOffset > _anchor;
        _document.Replace(start, count, HexValueFormats.SwapByteOrder(_document.GetBytes(start, count), size));
        _anchor = caretAtEnd ? start : start + count;
        _caretOffset = caretAtEnd ? start + count : start;
        _nibble = 0;
        AfterContentChange();
        return true;
    }

    /// <summary>
    /// Writes <paramref name="text"/>, read as <paramref name="type"/> in the current byte order, over the bytes at the
    /// caret (one undo step). The bytes past the end of the file are added.
    /// </summary>
    public bool WriteValue(HexValueType type, string text, out string error)
    {
        if (_readOnly)
        {
            error = "The file cannot be changed.";
            return false;
        }
        if (!HexValueFormats.TryParse(type, text, _bigEndian, out byte[] bytes, out error))
        {
            return false;
        }
        int offset = HasSelection ? SelectionStart : _caretOffset;
        int replaced = Math.Min(bytes.Length, _document.Length - offset);
        _document.Replace(offset, replaced, bytes);
        _anchor = -1;
        _caretOffset = offset;
        _nibble = 0;
        AfterContentChange();
        return true;
    }

    /// <summary>Up to <paramref name="count"/> bytes from the caret (from the start of the selection).</summary>
    public byte[] GetBytesAtCaret(int count)
    {
        int offset = HasSelection ? SelectionStart : _caretOffset;
        int available = Math.Max(0, Math.Min(count, _document.Length - offset));
        return _document.GetBytes(offset, available);
    }

    /// <summary>Marks the selected bytes (the byte under the caret without a selection) with a colour and a label.</summary>
    public bool HighlightSelection(Color color, string label)
    {
        int start = HasSelection ? SelectionStart : _caretOffset;
        int count = HasSelection ? SelectionLength : 1;
        if (start + count > _document.Length || count <= 0)
        {
            return false;
        }
        _document.AddHighlight(start, count, Color.FromArgb(255, color).ToArgb(), (label ?? "").Trim());
        return true;
    }

    /// <summary>Removes the colour from the selected bytes, or the whole block under the caret without a selection.</summary>
    public bool RemoveHighlight()
    {
        return HasSelection ? _document.RemoveHighlights(SelectionStart, SelectionLength) : _document.RemoveHighlights(_caretOffset, 0);
    }

    public void ClearHighlights()
    {
        _document.ClearHighlights();
    }

    /// <summary>Selects the next coloured block after the caret (the previous one when <paramref name="forward"/> is false).</summary>
    public bool GoToHighlight(bool forward)
    {
        int from = HasSelection ? SelectionStart : _caretOffset;
        HexHighlight highlight = _document.FindHighlight(from, forward);
        if (highlight == null)
        {
            SystemSounds.Beep.Play();
            return false;
        }
        _anchor = highlight.Start;
        _caretOffset = highlight.End;
        _nibble = 0;
        _area = EditArea.Hex;
        AfterCaretChange();
        return true;
    }

    internal static string FormatByteCount(int count)
    {
        return count.ToString("N0", CultureInfo.InvariantCulture) + (count == 1 ? " byte" : " bytes")
            + " (0x" + count.ToString("X", CultureInfo.InvariantCulture) + ")";
    }

    public void Paste()
    {
        if (_readOnly)
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
            byte[] bytes = (_area == EditArea.Ascii) ? Encoding.UTF8.GetBytes(text) : HexDocument.ParseClipboardBytes(text, preferHex: true);
            InsertBytes(bytes);
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    public void Undo()
    {
        if (_readOnly)
        {
            return;
        }
        _document.Undo();
        AfterContentChange();
    }

    public void Redo()
    {
        if (_readOnly)
        {
            return;
        }
        _document.Redo();
        AfterContentChange();
    }

    public void ToggleOverwriteMode()
    {
        if (_readOnly)
        {
            return;
        }
        _overwrite = !_overwrite;
        Invalidate();
        OnStateChanged();
    }

    /// <summary>Moves the caret to <paramref name="offset"/> (clamped to the document) and selects nothing.</summary>
    public void GoToOffset(int offset)
    {
        _anchor = -1;
        _caretOffset = Math.Max(0, Math.Min(offset, _document.Length));
        _nibble = 0;
        _area = EditArea.Hex;
        EnsureVisible();
        Invalidate();
        OnStateChanged();
        Focus();
    }

    /// <summary>
    /// Finds <paramref name="pattern"/> and selects it. The search starts after the selection (before it when
    /// <paramref name="forward"/> is false) and, with <paramref name="wrap"/>, continues from the other end.
    /// </summary>
    public bool Search(byte[] pattern, bool forward, bool ignoreCase, bool wrap)
    {
        if (pattern == null || pattern.Length == 0)
        {
            return false;
        }
        _lastPattern = (byte[])pattern.Clone();
        _lastIgnoreCase = ignoreCase;
        _lastWrap = wrap;
        _lastSearchText = HexDocument.FormatHex(pattern);
        int start = HasSelection
            ? (forward ? SelectionEnd : SelectionStart - 1)
            : (forward ? _caretOffset : _caretOffset - 1);
        int offset = _document.IndexOf(pattern, start, forward, ignoreCase, wrap);
        if (offset < 0)
        {
            SearchFinished?.Invoke(this, new HexSearchEventArgs { Found = false });
            return false;
        }
        _anchor = offset;
        _caretOffset = offset + pattern.Length;
        _nibble = 0;
        _area = EditArea.Hex;
        EnsureVisible();
        Invalidate();
        OnStateChanged();
        SearchFinished?.Invoke(this, new HexSearchEventArgs { Found = true, Offset = offset, Length = pattern.Length });
        return true;
    }

    /// <summary>Repeats the last search (F3 and Shift+F3).</summary>
    public bool FindNext(bool forward)
    {
        if (_lastPattern == null)
        {
            FindRequested?.Invoke(this, EventArgs.Empty);
            return false;
        }
        bool found = Search(_lastPattern, forward, _lastIgnoreCase, _lastWrap);
        if (!found)
        {
            SystemSounds.Beep.Play();
        }
        return found;
    }

    private byte[] GetSelectedBytes()
    {
        if (HasSelection)
        {
            return _document.GetBytes(SelectionStart, SelectionLength);
        }
        if (_caretOffset < _document.Length)
        {
            return new[] { _document[_caretOffset] };
        }
        return Array.Empty<byte>();
    }

    private void InsertBytes(byte[] bytes)
    {
        if (_readOnly || bytes == null || bytes.Length == 0)
        {
            return;
        }
        if (HasSelection)
        {
            int start = SelectionStart;
            int count = SelectionLength;
            _anchor = -1;
            _caretOffset = start;
            _document.Replace(start, count, bytes);
        }
        else if (_overwrite)
        {
            int count = Math.Min(bytes.Length, Math.Max(0, _document.Length - _caretOffset));
            _document.Replace(_caretOffset, count, bytes);
        }
        else
        {
            _document.Insert(_caretOffset, bytes);
        }
        _caretOffset = Math.Min(_document.Length, _caretOffset + bytes.Length);
        _nibble = 0;
        _anchor = -1;
        AfterContentChange();
    }

    private void DeleteCurrentSelectionOrByte()
    {
        if (_readOnly)
        {
            return;
        }
        if (HasSelection)
        {
            int start = SelectionStart;
            int count = SelectionLength;
            _anchor = -1;
            _caretOffset = start;
            _document.Remove(start, count);
        }
        else if (_caretOffset < _document.Length)
        {
            _document.Remove(_caretOffset, 1);
        }
        else
        {
            return;
        }
        _nibble = 0;
        AfterContentChange();
    }

    private void AfterContentChange()
    {
        _secondNibblePending = false;
        _caretOffset = Math.Max(0, Math.Min(_caretOffset, _document.Length));
        if (_anchor > _document.Length)
        {
            _anchor = _document.Length;
        }
        if (_anchor == _caretOffset)
        {
            _anchor = -1;
        }
        _caretVisible = true;
        EnsureVisible();
        Invalidate();
        OnStateChanged();
    }

    private void EnterHexDigit(int digit)
    {
        if (_readOnly)
        {
            return;
        }
        if (HasSelection)
        {
            DeleteCurrentSelectionOrByte();
        }
        if (_nibble == 0)
        {
            byte value = (byte)(digit << 4);
            if (_overwrite && _caretOffset < _document.Length)
            {
                byte existing = _document[_caretOffset];
                _document.ReplaceByte(_caretOffset, (byte)((existing & 0x0F) | value));
            }
            else
            {
                _document.Insert(_caretOffset, new[] { value });
            }
            _nibble = 1;
            _secondNibblePending = true;
        }
        else
        {
            bool joinStep = _secondNibblePending;
            _secondNibblePending = false;
            if (_caretOffset < _document.Length)
            {
                byte existing = _document[_caretOffset];
                byte value = (byte)((existing & 0xF0) | digit);
                if (joinStep)
                {
                    _document.ReplaceByteIntoLastStep(_caretOffset, value);
                }
                else
                {
                    _document.ReplaceByte(_caretOffset, value);
                }
            }
            else
            {
                _document.Insert(_caretOffset, new[] { (byte)digit });
            }
            _nibble = 0;
            _caretOffset = Math.Min(_document.Length, _caretOffset + 1);
            EnsureVisible();
        }
        _caretVisible = true;
        Invalidate();
        OnStateChanged();
    }

    private void EnterAscii(byte value)
    {
        if (_readOnly)
        {
            return;
        }
        _secondNibblePending = false;
        if (HasSelection)
        {
            DeleteCurrentSelectionOrByte();
        }
        if (_overwrite && _caretOffset < _document.Length)
        {
            _document.ReplaceByte(_caretOffset, value);
        }
        else
        {
            _document.Insert(_caretOffset, new[] { value });
        }
        _caretOffset = Math.Min(_document.Length, _caretOffset + 1);
        _nibble = 0;
        _caretVisible = true;
        EnsureVisible();
        Invalidate();
        OnStateChanged();
    }

    private static int HexDigitValue(char ch)
    {
        if (ch >= '0' && ch <= '9')
        {
            return ch - '0';
        }
        if (ch >= 'a' && ch <= 'f')
        {
            return ch - 'a' + 10;
        }
        if (ch >= 'A' && ch <= 'F')
        {
            return ch - 'A' + 10;
        }
        return -1;
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
                case Keys.S:
                    SaveRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case Keys.Home:
                    MoveTo(0, shift, 0);
                    break;
                case Keys.End:
                    MoveTo(_document.Length, shift, 0);
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
                MoveHorizontally(left: true, extendSelection: shift);
                break;
            case Keys.Right:
                MoveHorizontally(left: false, extendSelection: shift);
                break;
            case Keys.Up:
                MoveVertically(-1, shift);
                break;
            case Keys.Down:
                MoveVertically(1, shift);
                break;
            case Keys.PageUp:
                MoveVertically(-Math.Max(1, VisibleRows - 1), shift);
                break;
            case Keys.PageDown:
                MoveVertically(Math.Max(1, VisibleRows - 1), shift);
                break;
            case Keys.Home:
                MoveTo((_caretOffset / _bytesPerRow) * _bytesPerRow, shift, _nibble);
                break;
            case Keys.End:
                MoveTo(Math.Min(_document.Length, (_caretOffset / _bytesPerRow) * _bytesPerRow + _bytesPerRow - 1), shift, _nibble);
                break;
            case Keys.Back:
                if (_readOnly)
                {
                    break;
                }
                if (HasSelection)
                {
                    DeleteCurrentSelectionOrByte();
                }
                else if (_caretOffset > 0)
                {
                    int offset = _caretOffset - 1;
                    _document.Remove(offset, 1);
                    _caretOffset = offset;
                    _nibble = 0;
                    AfterContentChange();
                }
                break;
            case Keys.Delete:
                if (!_readOnly)
                {
                    DeleteCurrentSelectionOrByte();
                }
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
        if (e.Handled || _readOnly || char.IsControl(e.KeyChar))
        {
            return;
        }
        if (_area == EditArea.Hex)
        {
            int digit = HexDigitValue(e.KeyChar);
            if (digit >= 0)
            {
                EnterHexDigit(digit);
                e.Handled = true;
            }
            return;
        }
        if (e.KeyChar >= 0x20 && e.KeyChar <= 0xFF)
        {
            EnterAscii((byte)e.KeyChar);
            e.Handled = true;
        }
    }

    private void MoveHorizontally(bool left, bool extendSelection)
    {
        if (!extendSelection)
        {
            _anchor = -1;
        }
        else if (_anchor < 0)
        {
            _anchor = _caretOffset;
        }
        if (_area == EditArea.Ascii)
        {
            _caretOffset = Math.Max(0, Math.Min(_document.Length, _caretOffset + (left ? -1 : 1)));
            _nibble = 0;
        }
        else if (left)
        {
            if (_nibble == 1)
            {
                _nibble = 0;
            }
            else if (_caretOffset > 0)
            {
                _caretOffset--;
                _nibble = 1;
            }
        }
        else if (_nibble == 0)
        {
            if (_caretOffset < _document.Length)
            {
                _nibble = 1;
            }
        }
        else
        {
            if (_caretOffset < _document.Length)
            {
                _caretOffset++;
            }
            _nibble = 0;
        }
        AfterCaretChange();
    }

    private void MoveVertically(int rows, bool extendSelection)
    {
        int row = _caretOffset / _bytesPerRow + rows;
        row = Math.Max(0, row);
        int column = _caretOffset % _bytesPerRow;
        int offset = row * _bytesPerRow + column;
        MoveTo(offset, extendSelection, _nibble);
    }

    private void MoveTo(int offset, bool extendSelection, int nibble)
    {
        if (!extendSelection)
        {
            _anchor = -1;
        }
        else if (_anchor < 0)
        {
            _anchor = _caretOffset;
        }
        _caretOffset = Math.Max(0, Math.Min(offset, _document.Length));
        _nibble = (nibble == 1) ? 1 : 0;
        _area = EditArea.Hex;
        AfterCaretChange();
    }

    private void AfterCaretChange()
    {
        _secondNibblePending = false;
        if (_anchor == _caretOffset)
        {
            _anchor = -1;
        }
        _caretVisible = true;
        _caretTimer.Stop();
        _caretTimer.Start();
        EnsureVisible();
        Invalidate();
        OnStateChanged();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right)
        {
            return;
        }
        int offset = OffsetFromPoint(e.Location, out EditArea area, out int nibble);
        bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;
        if (e.Button == MouseButtons.Right)
        {
            _secondNibblePending = false;
            // A right click inside the selection opens the menu for it (Copy, Cut, Delete) and keeps it.
            if (HasSelection && offset >= SelectionStart && offset < SelectionEnd)
            {
                _area = area;
                Invalidate();
                return;
            }
            _anchor = -1;
            _caretOffset = offset;
            _area = area;
            _nibble = nibble;
            _dragging = false;
            _caretVisible = true;
            EnsureVisible();
            Invalidate();
            OnStateChanged();
            return;
        }
        _dragging = true;
        // Shift+click extends the selection from its anchor, or from the caret when nothing is selected.
        _dragAnchor = shift ? (_anchor >= 0 ? _anchor : _caretOffset) : offset;
        _anchor = shift ? _dragAnchor : -1;
        _caretOffset = offset;
        _area = area;
        _nibble = nibble;
        if (!shift)
        {
            _anchor = offset;
        }
        AfterCaretChange();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || e.Button != MouseButtons.Left)
        {
            UpdateToolTip(e.Location);
            return;
        }
        int offset = OffsetFromPoint(e.Location, out EditArea area, out _);
        _anchor = _dragAnchor;
        _caretOffset = offset;
        _area = area;
        if (_anchor == _caretOffset)
        {
            _anchor = -1;
        }
        _caretVisible = true;
        EnsureVisible();
        Invalidate();
        OnStateChanged();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
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
                ulong word = HexValueFormats.ReadUnsigned(_document.GetBytes(start, _groupSize), 0, _groupSize, _bigEndian);
                text.Append("\n").Append((_groupSize * 8).ToString(CultureInfo.InvariantCulture)).Append("-bit word, ")
                    .Append(_bigEndian ? "big" : "little").Append(" endian: 0x")
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
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (ModifierKeys == Keys.None || ModifierKeys == Keys.Shift)
        {
            // Positive wheel delta scrolls up (towards the start), negative down.
            int rows = Math.Sign(e.Delta) * Math.Max(1, Math.Abs(e.Delta) / 120) * 3;
            if ((ModifierKeys & Keys.Shift) == Keys.Shift)
            {
                _scrollX = Math.Max(0, _scrollX - rows * _charWidth * 3);
                _horizontalScrollBar.Value = Math.Min(_scrollX, Math.Max(0, _horizontalScrollBar.Maximum - _horizontalScrollBar.LargeChange + 1));
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
            for (int index = 0; index < _bytesPerRow; index++)
            {
                int x = HexCellX(index) - _scrollX;
                if (x + _charWidth * 2 < 0 || x > width)
                {
                    continue;
                }
                TextRenderer.DrawText(graphics, index.ToString("X2", CultureInfo.InvariantCulture), Font, new Point(x, 2), dimBrush.Color, TextFlags);
            }
            TextRenderer.DrawText(graphics, "ASCII", Font, new Point(_asciiStart - _scrollX, 2), dimBrush.Color, TextFlags);
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
                    int offset = row * _bytesPerRow;
                    if (offset >= _document.Length)
                    {
                        break;
                    }
                    string offsetText = offset.ToString("X8", CultureInfo.InvariantCulture);
                    TextRenderer.DrawText(graphics, offsetText, Font, new Point(Margin - _scrollX, y + textYOffset), dimBrush.Color, TextFlags);
                    HexHighlight lastHighlight = null;
                    for (int index = 0; index < _bytesPerRow; index++)
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
                        bool joinsNext = index + 1 < _bytesPerRow && position + 1 < _document.Length
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
                        if (Focused && _caretVisible && _caretOffset == position && _area == EditArea.Hex && !selected)
                        {
                            int caretX = x + _nibble * _charWidth;
                            graphics.FillRectangle(caretBrush, caretX, y + _rowHeight - 2, _charWidth, 2);
                        }
                        int asciiX = _asciiStart - _scrollX + index * _charWidth;
                        if (Focused && _caretVisible && _caretOffset == position && _area == EditArea.Ascii && !selected)
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
                if (Focused && _caretVisible && _caretOffset == _document.Length)
                {
                    int row = _caretOffset / _bytesPerRow;
                    int column = _caretOffset % _bytesPerRow;
                    if (row >= _firstRow)
                    {
                        int y = _headerHeight + (row - _firstRow) * _rowHeight;
                        int x = (_area == EditArea.Ascii)
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
        if (x >= _hexStart && x < HexCellX(_bytesPerRow))
        {
            for (int index = 0; index < _bytesPerRow; index++)
            {
                if (x >= HexCellX(index) && x < HexCellX(index) + 2 * _charWidth)
                {
                    column = index;
                    break;
                }
            }
        }
        else if (x >= _asciiStart && x < _asciiStart + _bytesPerRow * _charWidth)
        {
            column = (x - _asciiStart) / Math.Max(1, _charWidth);
        }
        if (column < 0)
        {
            return -1;
        }
        int offset = row * _bytesPerRow + column;
        return (offset < _document.Length) ? offset : -1;
    }

    private int OffsetFromPoint(Point point, out EditArea area, out int nibble)
    {
        int row = Math.Max(0, (point.Y - _headerHeight) / Math.Max(1, _rowHeight) + _firstRow);
        int x = point.X + _scrollX;
        area = EditArea.Hex;
        nibble = 0;
        int column;
        if (x < _hexStart)
        {
            column = 0;
        }
        else if (x < _asciiStart)
        {
            // The gap after a byte belongs to it: a click there puts the caret on its low digit.
            column = _bytesPerRow - 1;
            for (int index = 0; index < _bytesPerRow; index++)
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
            area = EditArea.Ascii;
            column = Math.Max(0, Math.Min(_bytesPerRow - 1, (x - _asciiStart) / Math.Max(1, _charWidth)));
        }
        return Math.Max(0, Math.Min(row * _bytesPerRow + column, _document.Length));
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
        int row = Math.Min(RowCount - 1, _caretOffset / _bytesPerRow);
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
        int column = _caretOffset % _bytesPerRow;
        if (_caretOffset >= _document.Length)
        {
            column = _document.Length % _bytesPerRow;
        }
        return (_area == EditArea.Ascii) ? _asciiStart + column * _charWidth : HexCellX(column);
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
            _scrollX = Math.Max(0, Math.Min(_scrollX, Math.Max(0, _contentWidth - width)));
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
        _asciiStart = HexCellX(_bytesPerRow) + 2 * _charWidth;
        _contentWidth = _asciiStart + _bytesPerRow * _charWidth + Margin + 1;
    }

    private void OnDocumentChanged(object sender, EventArgs e)
    {
        if (_caretOffset > _document.Length)
        {
            _caretOffset = _document.Length;
        }
        if (_anchor > _document.Length)
        {
            _anchor = _document.Length;
        }
        if (_anchor == _caretOffset)
        {
            _anchor = -1;
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
