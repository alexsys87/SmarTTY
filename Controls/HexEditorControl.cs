using SmarTTY.TextEditing;
using System;
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

/// <summary>
/// A full hex editor over a <see cref="HexDocument"/>: the offset, the hexadecimal bytes and the ASCII column, with
/// the caret and the selection, overwrite and insert modes, the clipboard, undo and redo, and a search that is
/// repeated with F3. It has no file access: the remote file editor saves the bytes (see <see cref="RemoteFileEditor"/>).
/// The control draws its own scroll bars, because the WPF scroll bars of the text editor are hidden in hex mode.
/// </summary>
public sealed class HexEditorControl : Control
{
    private const int BytesPerRow = 16;

    private const int OffsetDigits = 8;

    private const int Margin = 6;

    private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private enum EditArea
    {
        Hex,
        Ascii
    }

    private HexDocument _document = new HexDocument();

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

    public HexEditorControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, value: true);
        TabStop = true;
        _verticalScrollBar.Dock = DockStyle.Right;
        _horizontalScrollBar.Dock = DockStyle.Bottom;
        _verticalScrollBar.ValueChanged += (sender, e) => OnScrollBarChanged();
        _horizontalScrollBar.ValueChanged += (sender, e) => OnScrollBarChanged();
        Controls.Add(_verticalScrollBar);
        Controls.Add(_horizontalScrollBar);
        _caretTimer.Interval = 530;
        _caretTimer.Tick += (sender, e) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateCaretArea();
        };
        Document = _document;
        UpdateMetrics();
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
            }
            _document = value ?? new HexDocument();
            _document.Changed += OnDocumentChanged;
            _document.ModifiedChanged += OnDocumentModifiedChanged;
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

    private int VisibleRows
    {
        get
        {
            int height = ClientSize.Height - (_horizontalScrollBar.Visible ? _horizontalScrollBar.Height : 0) - _headerHeight - Margin;
            return Math.Max(1, height / Math.Max(1, _rowHeight));
        }
    }

    private int VisibleWidth => Math.Max(1, ClientSize.Width - (_verticalScrollBar.Visible ? _verticalScrollBar.Width : 0));

    private int RowCount => Math.Max(1, (_document.Length + BytesPerRow - 1) / BytesPerRow);

    public void CopySelection()
    {
        try
        {
            byte[] bytes = GetSelectedBytes();
            if (bytes.Length == 0)
            {
                return;
            }
            Clipboard.SetText(HexDocument.FormatHex(bytes));
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
        CopySelection();
        DeleteSelection();
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
        }
        else
        {
            if (_caretOffset < _document.Length)
            {
                byte existing = _document[_caretOffset];
                _document.ReplaceByte(_caretOffset, (byte)((existing & 0xF0) | digit));
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
                MoveTo((_caretOffset / BytesPerRow) * BytesPerRow, shift, _nibble);
                break;
            case Keys.End:
                MoveTo(Math.Min(_document.Length, (_caretOffset / BytesPerRow) * BytesPerRow + BytesPerRow - 1), shift, _nibble);
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
        int row = _caretOffset / BytesPerRow + rows;
        row = Math.Max(0, row);
        int column = _caretOffset % BytesPerRow;
        int offset = row * BytesPerRow + column;
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
        _dragAnchor = shift && _anchor >= 0 ? _anchor : offset;
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

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (ModifierKeys == Keys.None || ModifierKeys == Keys.Shift)
        {
            int rows = Math.Max(1, e.Delta / 120) * 3;
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
        UpdateScrollBars();
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateMetrics();
        UpdateScrollBars();
        Invalidate();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        Invalidate();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        Invalidate();
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
        TextRenderer.DrawText(graphics, "OFFSET", Font, new Point(Margin, 2), dimBrush.Color, TextFlags);
        for (int index = 0; index < BytesPerRow; index++)
        {
            int x = _hexStart - _scrollX + index * 3 * _charWidth;
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
                int offset = row * BytesPerRow;
                if (offset >= _document.Length)
                {
                    break;
                }
                string offsetText = offset.ToString("X8", CultureInfo.InvariantCulture);
                TextRenderer.DrawText(graphics, offsetText, Font, new Point(Margin, y + textYOffset), dimBrush.Color, TextFlags);
                for (int index = 0; index < BytesPerRow; index++)
                {
                    int position = offset + index;
                    if (position >= _document.Length)
                    {
                        break;
                    }
                    int x = _hexStart - _scrollX + index * 3 * _charWidth;
                    bool selected = selectionStart >= 0 && position >= selectionStart && position < selectionEnd;
                    if (selected)
                    {
                        graphics.FillRectangle(selectionBrush, x, y, _charWidth * 2, _rowHeight);
                    }
                    else if (_document.IsByteChanged(position))
                    {
                        graphics.FillRectangle(changedBrush, x, y, _charWidth * 2, _rowHeight);
                    }
                    Color color = selected ? selectionTextBrush.Color : (_document.IsByteChanged(position) ? changedTextBrush.Color : ForeColor);
                    TextRenderer.DrawText(graphics, _document[position].ToString("X2", CultureInfo.InvariantCulture), Font, new Point(x, y + textYOffset), color, TextFlags);
                    if (Focused && _caretVisible && _caretOffset == position && _area == EditArea.Hex && !selected)
                    {
                        int caretX = x + _nibble * _charWidth;
                        graphics.FillRectangle(caretBrush, caretX, y + _rowHeight - 2, _charWidth, 2);
                    }
                    int asciiX = _asciiStart - _scrollX + index * _charWidth;
                    if (selected)
                    {
                        graphics.FillRectangle(selectionBrush, asciiX, y, _charWidth, _rowHeight);
                    }
                    else if (_document.IsByteChanged(position))
                    {
                        graphics.FillRectangle(changedBrush, asciiX, y, _charWidth, _rowHeight);
                    }
                    string ascii = HexDocument.IsPrintable(_document[position]) ? ((char)_document[position]).ToString() : ".";
                    TextRenderer.DrawText(graphics, ascii, Font, new Point(asciiX, y + textYOffset), color, TextFlags);
                }
            }
            if (_document.Length == 0)
            {
                TextRenderer.DrawText(graphics, "The file is empty. Type hexadecimal digits to add bytes.", Font, new Point(Margin, _headerHeight + textYOffset), dimBrush.Color, TextFlags);
            }
            if (Focused && _caretVisible && _caretOffset == _document.Length && _document.Length > 0)
            {
                int row = _caretOffset / BytesPerRow;
                int column = _caretOffset % BytesPerRow;
                if (row >= _firstRow)
                {
                    int y = _headerHeight + (row - _firstRow) * _rowHeight;
                    int x = _hexStart - _scrollX + column * 3 * _charWidth;
                    graphics.FillRectangle(caretBrush, x, y + _rowHeight - 2, _charWidth, 2);
                }
            }
        }
        finally
        {
            graphics.SetClip(previousRegion, CombineMode.Replace);
        }
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
            int cell = Math.Max(0, Math.Min(BytesPerRow - 1, (x - _hexStart) / Math.Max(1, 3 * _charWidth)));
            int inside = (x - _hexStart) % Math.Max(1, 3 * _charWidth);
            column = cell;
            nibble = (inside / Math.Max(1, _charWidth) >= 1) ? 1 : 0;
        }
        else
        {
            area = EditArea.Ascii;
            column = Math.Max(0, Math.Min(BytesPerRow - 1, (x - _asciiStart) / Math.Max(1, _charWidth)));
        }
        return Math.Max(0, Math.Min(row * BytesPerRow + column, _document.Length));
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
        int row = Math.Min(RowCount - 1, _caretOffset / BytesPerRow);
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
        int column = _caretOffset % BytesPerRow;
        if (_caretOffset >= _document.Length)
        {
            column = _document.Length % BytesPerRow;
        }
        return ((_area == EditArea.Ascii) ? _asciiStart : _hexStart) + column * ((_area == EditArea.Ascii) ? _charWidth : 3 * _charWidth);
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
            bool needHorizontal = _contentWidth > ClientSize.Width - (needVertical ? _verticalScrollBar.Width : 0);
            if (needHorizontal)
            {
                int visibleWithHorizontal = Math.Max(1, (ClientSize.Height - _headerHeight - Margin - _horizontalScrollBar.Height) / Math.Max(1, _rowHeight));
                needVertical = rowCount > visibleWithHorizontal;
                needHorizontal = _contentWidth > ClientSize.Width - (needVertical ? _verticalScrollBar.Width : 0);
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
        _asciiStart = _hexStart + BytesPerRow * 3 * _charWidth + 2 * _charWidth;
        _contentWidth = _asciiStart + BytesPerRow * _charWidth + Margin + 1;
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

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
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
    private static Color Blend(Color background, Color foreground, float foregroundShare)
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
            _verticalScrollBar.Dispose();
            _horizontalScrollBar.Dispose();
            if (_document != null)
            {
                _document.Changed -= OnDocumentChanged;
                _document.ModifiedChanged -= OnDocumentModifiedChanged;
            }
        }
        base.Dispose(disposing);
    }
}
