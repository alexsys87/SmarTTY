using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Controls;

public partial class TerminalEmulatorBase : TextEditorControl
{
    public enum EraseMode
    {
        ToEnd,
        ToStart,
        All
    }

    public struct CellStyle
    {
        public class ForegroundOverride
        {
            public Brush Brush;
        }

        public byte Foreground;

        public byte Background;

        public bool Bold;

        public bool Inverse;

        public bool Underline;

        /// <summary>
        /// 256-colour or 24-bit foreground set by SGR 38/90-97; 0 when <see cref="Foreground"/> applies.
        /// Encoded as palette index + 1 (1..256) or <see cref="XtermProtocol.RgbColorFlag"/> | 0xRRGGBB.
        /// </summary>
        public int ExtendedForeground;

        /// <summary>The same as <see cref="ExtendedForeground"/> for the background (SGR 48/100-107).</summary>
        public int ExtendedBackground;

        public ForegroundOverride Override;

        internal LinkCell _link;

        private static readonly string[] ColorNames = new string[9] { "Default", "Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White" };

        public static readonly CellStyle Default = default;

        public bool IsDefault
        {
            get
            {
                if (Foreground == 0 && Background == 0 && ExtendedForeground == 0 && ExtendedBackground == 0 && !Bold && !Inverse && !Underline && Override == null)
                {
                    return _link.Target == null;
                }
                return false;
            }
        }

        public bool StyleEquals(CellStyle cellStyle)
        {
            if (Foreground == cellStyle.Foreground && Background == cellStyle.Background && ExtendedForeground == cellStyle.ExtendedForeground && ExtendedBackground == cellStyle.ExtendedBackground && Bold == cellStyle.Bold && Inverse == cellStyle.Inverse && Underline == cellStyle.Underline && object.Equals(Override, cellStyle.Override))
            {
                return object.Equals(_link, cellStyle._link);
            }
            return false;
        }

        private static string GetColorName(byte colorIndex)
        {
            if (colorIndex >= ColorNames.Length)
            {
                return "Invalid";
            }
            return ColorNames[colorIndex];
        }

        public override string ToString()
        {
            if (IsDefault)
            {
                return "Default";
            }
            string text = $"{GetColorName(Foreground)} on {GetColorName(Background)}";
            if (Inverse)
            {
                text += " [inverse]";
            }
            return text;
        }
    }

    internal interface ILinkTarget
    {
        bool IsActive { get; }

        void Activate();
    }

    internal struct LinkCell
    {
        public ILinkTarget Target;
    }

    internal interface ILinkScanner
    {
        LinkCell[] Scan(string text);

        void Finish();
    }

    private class LinkTracker
    {
        private ILinkScanner _scanner;

        private TextDocument.TextPosition _expectedPosition;

        public bool IsActive { get; private set; } = true;

        public TextDocument.TextPosition ExpectedPosition
        {
            get
            {
                return _expectedPosition;
            }
            set
            {
                _expectedPosition = value;
            }
        }

        public LinkTracker(ILinkScanner linkScanner, TextDocument.TextPosition textPosition)
        {
            _scanner = linkScanner;
            _expectedPosition = textPosition;
        }

        private static TextDocument.TextPosition NextLineStart(TextDocument.TextPosition textPosition)
        {
            return new TextDocument.TextPosition(0, textPosition.Line + 1);
        }

        public LinkCell[] Scan(string text, TextDocument.TextPosition textPosition)
        {
            if (!this.IsActive || text == null)
            {
                return null;
            }
            if (textPosition != _expectedPosition && textPosition != NextLineStart(_expectedPosition))
            {
                _scanner?.Finish();
                this.IsActive = false;
                return null;
            }
            _expectedPosition = new TextDocument.TextPosition(textPosition.Column + text.Length, textPosition.Line);
            return _scanner.Scan(text);
        }

        public void Finish()
        {
            _scanner?.Finish();
        }
    }

    protected class LineInfo
    {
        public CellStyle[] Cells;

        public bool IsDimmed;

        public bool IsWrapped;

        public DateTime Timestamp;
    }

    protected class LineInfoStore
    {
        private TerminalEmulatorBase _terminal;

        private List<LineInfo> _lines = new List<LineInfo>();

        public LineInfoStore(TerminalEmulatorBase terminalEmulatorBase)
        {
            _terminal = terminalEmulatorBase;
        }

        public LineInfo GetLine(int line)
        {
            if (line >= _lines.Count)
            {
                return null;
            }
            return _lines[line];
        }

        internal void SetCells(int row, int startColumn, int count, CellStyle cellStyle, LinkCell[] items = null)
        {
            TextDocument.TextPosition textPosition = this._terminal.ToDocumentPosition(new Point(0, row));
            while (textPosition.Line >= this._lines.Count)
            {
                if (cellStyle.IsDefault && items == null && !this._terminal.AlwaysTrackLines && this._terminal.HideMargins)
                {
                    return;
                }
                this._lines.Add(null);
            }
            if (this._lines[textPosition.Line] == null)
            {
                if (cellStyle.IsDefault && items == null && !this._terminal.AlwaysTrackLines && this._terminal.HideMargins)
                {
                    return;
                }
                this._lines[textPosition.Line] = new LineInfo();
            }
            LineInfo lineInfo = this._lines[textPosition.Line];
            lineInfo.IsDimmed = false;
            lineInfo.IsWrapped = false;
            lineInfo.Timestamp = DateTime.Now;
            if (lineInfo.Cells == null || lineInfo.Cells.Length < this._terminal._visibleColumns)
            {
                CellStyle[] cellStyles = new CellStyle[this._terminal._visibleColumns];
                if (lineInfo.Cells != null)
                {
                    lineInfo.Cells.CopyTo(cellStyles, 0);
                }
                lineInfo.Cells = cellStyles;
            }
            if (startColumn == -1 || startColumn > this._terminal._visibleColumns)
            {
                startColumn = this._terminal._visibleColumns;
            }
            int remainingColumns = this._terminal._visibleColumns - startColumn;
            if (count == -1 || count > remainingColumns)
            {
                count = remainingColumns;
            }
            for (int cellOffset = 0; cellOffset < count; cellOffset++)
            {
                lineInfo.Cells[startColumn + cellOffset] = cellStyle;
                if (items != null && cellOffset < items.Length)
                {
                    lineInfo.Cells[startColumn + cellOffset]._link = items[cellOffset];
                }
                else
                {
                    lineInfo.Cells[startColumn + cellOffset]._link = default;
                }
            }
        }

        public void ClearLine(int line)
        {
            TextDocument.TextPosition textPosition = _terminal.ToDocumentPosition(new Point(0, line));
            if (textPosition.Line < _lines.Count)
            {
                _lines[textPosition.Line] = null;
            }
        }

        public void RemoveLines(int line, int count)
        {
            if (line < this._lines.Count)
            {
                count = Math.Min(count, this._lines.Count - line);
                this._lines.RemoveRange(line, count);
            }
        }

        public void InsertLines(int line, int count)
        {
            if (line < this._lines.Count)
            {
                this._lines.InsertRange(line, new LineInfo[count]);
            }
        }

        public void DeleteCells(Point point, int count)
        {
            TextDocument.TextPosition textPosition = _terminal.ToDocumentPosition(point);
            if (textPosition.Line >= this._lines.Count || this._lines[textPosition.Line] == null)
            {
                return;
            }
            LineInfo lineInfo = this._lines[textPosition.Line];
            lineInfo.IsDimmed = false;
            lineInfo.IsWrapped = false;
            for (int column = textPosition.Column; column < lineInfo.Cells.Length; column++)
            {
                int targetColumn = column + count;
                if (targetColumn < lineInfo.Cells.Length)
                {
                    lineInfo.Cells[column] = lineInfo.Cells[targetColumn];
                }
                else
                {
                    lineInfo.Cells[column] = CellStyle.Default;
                }
            }
        }

        public void InsertCells(Point point, int count)
        {
            TextDocument.TextPosition textPosition = _terminal.ToDocumentPosition(point);
            if (textPosition.Line < this._lines.Count && this._lines[textPosition.Line] != null)
            {
                LineInfo lineInfo = this._lines[textPosition.Line];
                lineInfo.IsDimmed = false;
                lineInfo.IsWrapped = false;
                if (lineInfo.Cells == null || textPosition.Column >= lineInfo.Cells.Length || count <= 0)
                {
                    return;
                }
                // Shift right-to-left so that every cell is moved before it is overwritten.
                for (int column = lineInfo.Cells.Length - 1; column >= textPosition.Column + count; column--)
                {
                    lineInfo.Cells[column] = lineInfo.Cells[column - count];
                }
                CellStyle insertedStyle = lineInfo.Cells[textPosition.Column];
                for (int offset = 0; offset < count && textPosition.Column + offset < lineInfo.Cells.Length; offset++)
                {
                    lineInfo.Cells[textPosition.Column + offset] = insertedStyle;
                }
            }
        }

        internal void Clear()
        {
            _lines.Clear();
        }

        internal void DimLines(int lineCount)
        {
            for (int line = 0; line < lineCount; line++)
            {
                if (line == _lines.Count)
                {
                    _lines.Add(new LineInfo());
                }
                else if (_lines[line] == null)
                {
                    _lines[line] = new LineInfo();
                }
                _lines[line].IsDimmed = true;
            }
        }

        public void MarkWrapped(int line)
        {
            TextDocument.TextPosition textPosition = _terminal.ToDocumentPosition(new Point(0, line));
            while (textPosition.Line >= _lines.Count)
            {
                _lines.Add(null);
            }
            if (_lines[textPosition.Line] == null)
            {
                _lines[textPosition.Line] = new LineInfo();
            }
            _lines[textPosition.Line].IsWrapped = true;
        }
    }

    private class BrushPalette
    {
        public Brush[] NormalBrushes;

        public Brush[] BrightBrushes;

        public BrushPalette(ConsolePalette consolePalette)
        {
            NormalBrushes = new Brush[consolePalette.NormalColors.Length];
            this.BrightBrushes = new Brush[consolePalette.BrightColors.Length];
            for (int normalColorIndex = 0; normalColorIndex < consolePalette.NormalColors.Length; normalColorIndex++)
            {
                NormalBrushes[normalColorIndex] = new SolidBrush(consolePalette.NormalColors[normalColorIndex]);
            }
            for (int brightColorIndex = 0; brightColorIndex < consolePalette.NormalColors.Length; brightColorIndex++)
            {
                this.BrightBrushes[brightColorIndex] = new SolidBrush(consolePalette.BrightColors[brightColorIndex]);
            }
        }

        public Brush GetBrush(byte colorIndex, bool bright)
        {
            Brush[] brushes = (bright ? this.BrightBrushes : this.NormalBrushes);
            if (colorIndex == 0 || colorIndex > brushes.Length)
            {
                return null;
            }
            return brushes[colorIndex - 1];
        }
    }

    protected class ScreenBuffer
    {
        public EditableTextDocument Document = new EditableTextDocument();

        public readonly LineInfoStore Lines;

        public Point Cursor;

        public int SavedTopLine;

        public int MaxScrollback;

        public int FirstRow { get; set; }

        public ScreenBuffer(TerminalEmulatorBase terminalEmulatorBase, int maxScrollback = int.MaxValue)
        {
            Lines = new LineInfoStore(terminalEmulatorBase);
            this.MaxScrollback = maxScrollback;
        }
    }

    public class TabStops
    {
        private bool[] _stops = new bool[0];

        public void SetStop(int column)
        {
            if (column > 65536)
            {
                column = 65536;
            }
            if (column >= _stops.Length)
            {
                Array.Resize(ref _stops, column + 1);
            }
            _stops[column] = true;
        }

        public int NextDistance(int column, int tabSize)
        {
            if (column >= this._stops.Length)
            {
                if (tabSize <= 0)
                {
                    tabSize = 8;
                }
                return tabSize - column % tabSize;
            }
            for (int nextColumn = column + 1; nextColumn < this._stops.Length; nextColumn++)
            {
                if (this._stops[nextColumn])
                {
                    return nextColumn - column;
                }
            }
            return 1;
        }

        public void ClearAll()
        {
            for (int column = 0; column < _stops.Length; column++)
            {
                _stops[column] = false;
            }
        }

        internal void ClearStop(int column)
        {
            if (column < _stops.Length)
            {
                _stops[column] = false;
            }
        }

        internal int PreviousDistance(int column)
        {
            if (column >= _stops.Length)
            {
                column = _stops.Length - 1;
            }
            if (column <= 0)
            {
                return 0;
            }
            for (int previousColumn = column - 1; previousColumn > 0; previousColumn--)
            {
                if (_stops[previousColumn])
                {
                    return column - previousColumn;
                }
            }
            return column;
        }
    }

    private class ScrollRegion
    {
        public int Top;

        public int Bottom;

        public override string ToString()
        {
            return $"Lines {Top}-{Bottom}";
        }

        public bool Contains(int line)
        {
            if (line >= Top)
            {
                return line <= Bottom;
            }
            return false;
        }
    }

    private class TimestampMargin : IMargin
    {
        private TerminalEmulatorBase _terminal;

        private StringFormat _format;

        public TimestampMargin(TerminalEmulatorBase terminalEmulatorBase)
        {
            _terminal = terminalEmulatorBase;
            _format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center
            };
        }

        public void Paint(Graphics graphics, Rectangle rectangle, SizeF sizeF, int row, int topLine, int visibleRows, ReadonlyTextBrowser readonlyTextBrowser)
        {
            ScreenBuffer screenBuffer = this._terminal.ActiveBuffer;
            if (screenBuffer == null)
            {
                return;
            }
            using (SolidBrush brush = new SolidBrush(this._terminal.ForeColor))
            {
                for (int rowOffset = 0; rowOffset < visibleRows; rowOffset++)
                {
                    LineInfo lineInfo = screenBuffer.Lines.GetLine(topLine + rowOffset);
                    if (lineInfo != null && !(lineInfo.Timestamp == DateTime.MinValue))
                    {
                        int y = (int)((float)rectangle.Top + (float)rowOffset * sizeF.Height);
                        int bottom = y + (int)sizeF.Height;
                        string text = lineInfo.Timestamp.ToString(this._terminal.TimestampFormat);
                        graphics.DrawString(text, readonlyTextBrowser.Font, brush, Rectangle.FromLTRB(rectangle.Left, y, rectangle.Right, bottom), this._format);
                    }
                }
            }
            using Pen pen = new Pen(Color.FromArgb(128, this._terminal.ForeColor), 1f);
            graphics.DrawLine(pen, rectangle.Right - 5, rectangle.Top, rectangle.Right - 5, rectangle.Bottom);
        }

        public int MeasureWidth(ReadonlyTextBrowser readonlyTextBrowser, Graphics graphics, int line)
        {
            return (int)Math.Ceiling(graphics.MeasureString(DateTime.Now.ToString(_terminal.TimestampFormat), readonlyTextBrowser.Font).Width) + 10;
        }

        public bool OnMouseDown(Point point, ReadonlyTextBrowser readonlyTextBrowser, int line, int row, SizeF sizeF, int column)
        {
            return true;
        }
    }

    private ScreenBuffer[] _buffers = new ScreenBuffer[2];

    private string _title;

    private Point _cursor;

    private bool _isBatching;

    private bool _layoutPending;

    private bool _redrawPending;

    protected bool _insertMode;

    private LinkTracker _linkTracker;

    private SolidBrush _defaultForeBrush = new SolidBrush(Color.White);

    private SolidBrush _defaultBackBrush = new SolidBrush(Color.Black);

    private BrushPalette _palette = new BrushPalette(ConsolePalette.PuTTY);

    // Brushes of 256-colour and 24-bit colours, created on first use.
    private readonly Dictionary<int, Brush> _extendedBrushes = new Dictionary<int, Brush>();

    public Brush InactiveBrush = Brushes.DarkGray;

    public CellStyle CurrentStyle;

    private int _activeBufferIndex;

    public TabStops Tabs = new TabStops();

    public bool CopyOnSelect = true;

    private bool _autoWrap = true;

    private ScrollRegion _scrollRegion;

    public string TimestampFormat = "HH:mm:ss";

    public bool AlwaysTrackLines;

    public bool CaretWhenUnfocused
    {
        get
        {
            return ShowCaretWhenUnfocused;
        }
        set
        {
            ShowCaretWhenUnfocused = value;
        }
    }

    public string Title
    {
        get
        {
            return _title;
        }
        set
        {
            bool titleChanged = _title != value;
            _title = value;
            if (titleChanged && TitleChanged != null)
            {
                TitleChanged(this, EventArgs.Empty);
            }
        }
    }

    public Size TerminalSize => new Size(_visibleColumns, ((ReadonlyTextBrowser)this)._visibleRows);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Point CursorPosition
    {
        get
        {
            return _cursor;
        }
        set
        {
            if (value.X > _visibleColumns)
            {
                value.X = _visibleColumns;
            }
            if (value.X < 0)
            {
                value.X = 0;
            }
            if (value.Y >= ((ReadonlyTextBrowser)this)._visibleRows)
            {
                value.Y = ((ReadonlyTextBrowser)this)._visibleRows - 1;
            }
            if (value.Y < 0)
            {
                value.Y = 0;
            }
            if (!(value == _cursor))
            {
                _cursor = value;
                if (!_isBatching)
                {
                    UpdateCaret();
                }
            }
        }
    }

    protected virtual bool UnusedFlag => false;

    public Color NormalTextColor
    {
        get
        {
            return _defaultForeBrush.Color;
        }
        set
        {
            _defaultForeBrush = new SolidBrush(value);
            Redraw();
        }
    }

    public override Color BackColor
    {
        get
        {
            return base.BackColor;
        }
        set
        {
            base.BackColor = value;
            _defaultBackBrush = new SolidBrush(value);
        }
    }

    protected ScreenBuffer ActiveBuffer
    {
        get
        {
            if (_activeBufferIndex < 0 || _activeBufferIndex >= _buffers.Length)
            {
                return _buffers[0];
            }
            return _buffers[_activeBufferIndex];
        }
    }

    public int ActiveBufferIndex
    {
        get
        {
            return _activeBufferIndex;
        }
        set
        {
            if (value >= _buffers.Length)
            {
                value = _buffers.Length - 1;
            }
            ActiveBuffer.Cursor = CursorPosition;
            ActiveBuffer.SavedTopLine = TopLine;
            _activeBufferIndex = value;
            SetDocument(_buffers[value].Document);
            CursorPosition = _buffers[_activeBufferIndex].Cursor;
            TopLine = ActiveBuffer.SavedTopLine;
            SetSelection(select: false, SelectionStart);
        }
    }

    public int ScrollbackLimit
    {
        get
        {
            return _buffers[0].MaxScrollback;
        }
        set
        {
            _buffers[0].MaxScrollback = value;
            TrimScrollback();
        }
    }

    public bool AutoWrap
    {
        get
        {
            return _autoWrap;
        }
        set
        {
            _autoWrap = value;
            base.HorizontalScrollBarVisible = !value;
            UpdateLayout();
        }
    }

    public int ScrollBarWidth => Width - TextArea.Width;

    public bool HasScrollRegion => _scrollRegion != null;

    protected int ScrollTop
    {
        get
        {
            if (_scrollRegion == null)
            {
                return 0;
            }
            return _scrollRegion.Top;
        }
    }

    protected int ScrollBottom
    {
        get
        {
            if (_scrollRegion == null)
            {
                return ((ReadonlyTextBrowser)this)._visibleRows - 1;
            }
            return _scrollRegion.Bottom;
        }
    }

    public event EventHandler TitleChanged;

    public TerminalEmulatorBase()
    {
        InitializeComponent();
        CaretWhenUnfocused = true;
        _allowPartialRows = false;
        for (int bufferIndex = 0; bufferIndex < _buffers.Length; bufferIndex++)
        {
            _buffers[bufferIndex] = new ScreenBuffer(this, (bufferIndex == 0) ? int.MaxValue : 0);
        }
        ActiveBufferIndex = 0;
        ClearMargins();
        AddMargin(new TimestampMargin(this));
        HideMargins = true;
    }

    public void BeginBatch()
    {
        _isBatching = true;
    }

    public void EndBatch()
    {
        _isBatching = false;
        if (_layoutPending)
        {
            UpdateLayout();
            Redraw();
            _layoutPending = false;
        }
        if (_redrawPending)
        {
            _redrawPending = false;
            Redraw();
        }
        UpdateCaret();
    }

    protected void UpdateCaret()
    {
        Point point = _cursor;
        if (_autoWrap && point.X == _visibleColumns)
        {
            point = new Point(0, point.Y + 1);
        }
        TextDocument.TextPosition textPosition = EnsureCursorPosition(point);
        SetCaret(textPosition);
    }

    protected TextDocument.TextPosition ToDocumentPosition(Point point)
    {
        TextDocument.TextPosition position = new TextDocument.TextPosition(point.X, point.Y);
        position.Line += ActiveBuffer.FirstRow;
        return position;
    }

    private TextDocument.TextPosition EnsureCursorPosition(Point point)
    {
        TextDocument.TextPosition documentPosition = ToDocumentPosition(point);
        EnsureDocumentSize(documentPosition);
        return documentPosition;
    }

    private void InvalidateLayout()
    {
        if (_isBatching)
        {
            _layoutPending = true;
            return;
        }
        UpdateLayout();
        Redraw();
    }

    protected void InvalidateView()
    {
        if (_isBatching)
        {
            _redrawPending = true;
        }
        else
        {
            Redraw();
        }
    }

    private void EnsureDocumentSize(TextDocument.TextPosition textPosition)
    {
        bool scrolled = false;
        while (textPosition.Line >= base.EditableDocument.LineCount)
        {
            base.EditableDocument.AppendLine("");
            scrolled = true;
        }
        int length = base.EditableDocument.GetLineText(textPosition.Line).Length;
        if (length < textPosition.Column)
        {
            base.EditableDocument.ReplaceInLine(textPosition.Line, length, 0, new string(' ', textPosition.Column - length));
            scrolled = true;
        }
        if (scrolled)
        {
            InvalidateLayout();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (ActiveBuffer != null)
        {
            int topLine = base.EditableDocument.LineCount - ((ReadonlyTextBrowser)this)._visibleRows;
            if (topLine < 0)
            {
                topLine = 0;
            }
            TopLine = topLine;
            int rowDelta = topLine - ActiveBuffer.FirstRow;
            ActiveBuffer.FirstRow = topLine;
            if (rowDelta != 0)
            {
                MoveCursorBy(0, -rowDelta);
            }
            TrimScrollback();
            Redraw();
        }
    }

    public void MoveCursorBy(int deltaX, int deltaY, bool scroll = false)
    {
        Point point = _cursor;
        point.Offset(deltaX, deltaY);
        if (point.Y < 0)
        {
            point.Y = 0;
        }
        MoveCursorTo(point, scroll);
    }

    protected void MoveCursorTo(Point point, bool scroll = false)
    {
        bool scrolled = false;
        if ((_scrollRegion != null) & scroll)
        {
            while (point.Y > _scrollRegion.Bottom)
            {
                DeleteLines(_scrollRegion.Top, 1, scroll: true);
                point.Y--;
                ForceCaretUpdate();
            }
        }
        if (point.Y >= ((ReadonlyTextBrowser)this)._visibleRows)
        {
            if (scroll)
            {
                ActiveBuffer.FirstRow += point.Y - ((ReadonlyTextBrowser)this)._visibleRows + 1;
                point.Y = ((ReadonlyTextBrowser)this)._visibleRows - 1;
                scrolled = true;
            }
            else
            {
                point.Y = ((ReadonlyTextBrowser)this)._visibleRows - 1;
            }
        }
        CursorPosition = point;
        this.EnsureCursorPosition(_cursor);
        if (scrolled)
        {
            TrimScrollback();
        }
    }

    protected void Trace(string text)
    {
    }

    public void WriteText(string text, bool moveCursor = true)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Trace("'" + text + "'");
            Point point = WriteAt(_cursor, text, CurrentStyle, scroll: true, _insertMode);
            if (moveCursor)
            {
                CursorPosition = point;
            }
        }
    }

    protected void RepeatPreviousChar(int count)
    {
        TextDocument.TextPosition textPosition = ToDocumentPosition(CursorPosition);
        string text = base.EditableDocument.GetLineText(textPosition.Line);
        char ch = ' ';
        if (textPosition.Column > 0 && text != null && textPosition.Column <= text.Length)
        {
            ch = text[textPosition.Column - 1];
        }
        CellStyle cellStyle = default;
        LineInfo lineInfo = ActiveBuffer.Lines.GetLine(textPosition.Line);
        if (lineInfo != null && textPosition.Column < lineInfo.Cells.Length)
        {
            cellStyle = lineInfo.Cells[textPosition.Column];
        }
        WriteAt(CursorPosition, new string(ch, count), cellStyle);
    }

    public void EraseInDisplay(EraseMode eraseMode)
    {
        Point point = CursorPosition;
        switch (eraseMode)
        {
            case EraseMode.ToEnd:
                {
                    EraseInLine(eraseMode);
                    for (int line = point.Y + 1; line < ((ReadonlyTextBrowser)this)._visibleRows; line++)
                    {
                        ClearLineFrom(line, 0, CurrentStyle);
                    }
                    break;
                }
            case EraseMode.ToStart:
                {
                    EraseInLine(eraseMode);
                    for (int lineBelowCursor = 0; lineBelowCursor < point.Y; lineBelowCursor++)
                    {
                        ClearLineFrom(lineBelowCursor, 0, CurrentStyle);
                    }
                    break;
                }
            case EraseMode.All:
                {
                    for (int lineAboveCursor = 0; lineAboveCursor < ((ReadonlyTextBrowser)this)._visibleRows; lineAboveCursor++)
                    {
                        ClearLineFrom(lineAboveCursor, 0, CurrentStyle);
                    }
                    break;
                }
        }
    }

    public void EraseInLine(EraseMode eraseMode)
    {
        Point point = CursorPosition;
        switch (eraseMode)
        {
            case EraseMode.ToEnd:
                ClearLineFrom(point.Y, point.X, CurrentStyle);
                break;
            case EraseMode.ToStart:
                // At the pending-wrap position (X == columns) the line ends at the last column: erasing one more
                // character would wrap onto the next line and blank its first cell.
                WriteAt(new Point(0, point.Y), new string(' ', Math.Min(point.X + 1, _visibleColumns)), CurrentStyle);
                break;
            case EraseMode.All:
                ClearLineFrom(point.Y, 0, CurrentStyle);
                break;
        }
    }

    public void DeleteChars(int count)
    {
        TextDocument.TextPosition textPosition = ToDocumentPosition(CursorPosition);
        int lineLength = base.EditableDocument.GetLineText(textPosition.Line).Length;
        if (textPosition.Column >= lineLength)
        {
            return;
        }
        count = Math.Min(count, lineLength - textPosition.Column);
        base.EditableDocument.ReplaceInLine(textPosition.Line, textPosition.Column, count, "");
        ActiveBuffer.Lines.DeleteCells(CursorPosition, count);
        Redraw();
    }

    public void InsertChars(int count, char ch = ' ')
    {
        TextDocument.TextPosition textPosition = EnsureCursorPosition(CursorPosition);
        base.EditableDocument.ReplaceInLine(textPosition.Line, textPosition.Column, 0, new string(ch, count));
        ActiveBuffer.Lines.InsertCells(CursorPosition, count);
        TruncateToScreenWidth(textPosition.Line);
        Redraw();
    }

    // Characters pushed past the right margin by an insertion are lost, as on a real terminal; keeping them
    // would leave text beyond the screen edge that shows up again when the line is copied or the window widened.
    private void TruncateToScreenWidth(int documentLine)
    {
        if (!_autoWrap)
        {
            return;
        }
        int length = base.EditableDocument.GetLineText(documentLine).Length;
        if (length > _visibleColumns)
        {
            base.EditableDocument.ReplaceInLine(documentLine, _visibleColumns, length - _visibleColumns, "");
        }
    }

    internal virtual ILinkScanner CreateLinkScanner()
    {
        return null;
    }

    protected Point WriteAt(Point point, string text, CellStyle cellStyle, bool scroll = true, bool insert = false)
    {
        if (_autoWrap && point.X >= _visibleColumns)
        {
            ActiveBuffer.Lines.MarkWrapped(point.Y);
            point = new Point(0, point.Y + 1);
        }
        if (scroll && point.Y >= ((ReadonlyTextBrowser)this)._visibleRows)
        {
            int scrollAmount = point.Y - ((ReadonlyTextBrowser)this)._visibleRows + 1;
            ActiveBuffer.FirstRow += scrollAmount;
            point.Offset(0, -scrollAmount);
        }
        this.EnsureCursorPosition(point);
        if (_autoWrap)
        {
            int availableColumns = _visibleColumns - point.X;
            if (text.Length > availableColumns)
            {
                point = WriteAt(point, text.Substring(0, availableColumns), cellStyle);
                int chunkLength;
                for (int chunkOffset = availableColumns; chunkOffset < text.Length; chunkOffset += chunkLength)
                {
                    chunkLength = Math.Min(text.Length - chunkOffset, _visibleColumns);
                    point = WriteAt(point, text.Substring(chunkOffset, chunkLength), cellStyle);
                }
                return point;
            }
        }
        TextDocument.TextPosition textPosition = ToDocumentPosition(point);
        int length = base.EditableDocument.GetLineText(textPosition.Line).Length;
        LinkTracker linkTracker = _linkTracker;
        if (linkTracker == null || !linkTracker.IsActive)
        {
            ILinkScanner linkScanner = CreateLinkScanner();
            if (linkScanner != null)
            {
                _linkTracker = new LinkTracker(linkScanner, textPosition);
            }
        }
        LinkCell[] linkCells = _linkTracker?.Scan(text, textPosition);
        if (insert)
        {
            base.EditableDocument.ReplaceInLine(textPosition.Line, textPosition.Column, 0, text);
            ActiveBuffer.Lines.InsertCells(point, text.Length);
            TruncateToScreenWidth(textPosition.Line);
        }
        else
        {
            int replaceLength = Math.Min(text.Length, length - textPosition.Column);
            base.EditableDocument.ReplaceInLine(textPosition.Line, textPosition.Column, replaceLength, text);
        }
        ActiveBuffer.Lines.SetCells(point.Y, point.X, text.Length, cellStyle, linkCells);
        InvalidateView();
        point.Offset(text.Length, 0);
        if (scroll && point.Y >= ((ReadonlyTextBrowser)this)._visibleRows)
        {
            int trailingScroll = point.Y - ((ReadonlyTextBrowser)this)._visibleRows + 1;
            ActiveBuffer.FirstRow += trailingScroll;
            point.Offset(0, -trailingScroll);
        }
        if (!_autoWrap)
        {
            UpdateMaxLineWidth(textPosition.Line);
            InvalidateLayout();
        }
        return point;
    }

    protected void ClearLineFrom(int line, int startColumn, CellStyle cellStyle)
    {
        TextDocument.TextPosition textPosition = ToDocumentPosition(new Point(startColumn, line));
        base.EditableDocument.ReplaceInLine(textPosition.Line, textPosition.Column, -1, "");
        if (startColumn == 0)
        {
            ActiveBuffer.Lines.ClearLine(line);
        }
        else
        {
            // SetCells takes a screen row (it adds the scrollback offset itself).
            ActiveBuffer.Lines.SetCells(line, startColumn, -1, CellStyle.Default);
        }
        this.EnsureCursorPosition(_cursor);
        if (!cellStyle.IsDefault)
        {
            int paddingLength = _visibleColumns - startColumn;
            if (paddingLength > 0)
            {
                base.EditableDocument.ReplaceInLine(textPosition.Line, textPosition.Column, -1, new string(' ', paddingLength));
                ActiveBuffer.Lines.SetCells(line, startColumn, paddingLength, cellStyle);
            }
        }
    }

    protected void ResetLinkTracking()
    {
        _linkTracker?.Finish();
        _linkTracker = null;
    }

    public void SetPalette(ConsolePalette consolePalette)
    {
        _palette = new BrushPalette(consolePalette);
    }

    // Resolves a 256-colour or 24-bit colour (see CellStyle.ExtendedForeground); the 16 base colours keep
    // following the user's palette.
    private Brush GetExtendedBrush(int encodedColor)
    {
        int rgb;
        if ((encodedColor & XtermProtocol.RgbColorFlag) != 0)
        {
            rgb = encodedColor & 0xFFFFFF;
        }
        else
        {
            int index = encodedColor - 1;
            if (index < 8)
            {
                return _palette.GetBrush((byte)(index + 1), bright: false);
            }
            if (index < 16)
            {
                return _palette.GetBrush((byte)(index - 7), bright: true);
            }
            rgb = XtermProtocol.Xterm256ToRgb(index);
        }
        if (!_extendedBrushes.TryGetValue(rgb, out Brush brush))
        {
            if (_extendedBrushes.Count > 4096)
            {
                _extendedBrushes.Clear();
            }
            brush = new SolidBrush(Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF));
            _extendedBrushes[rgb] = brush;
        }
        return brush;
    }

    private ColorSpan CreateColorSpan(int start, int end, CellStyle cellStyle)
    {
        Brush foregroundBrush = (cellStyle.ExtendedForeground != 0) ? GetExtendedBrush(cellStyle.ExtendedForeground) : ((cellStyle.Foreground != 0 || !cellStyle.Bold) ? _palette.GetBrush(cellStyle.Foreground, cellStyle.Bold) : _defaultForeBrush);
        Brush backgroundBrush = (cellStyle.ExtendedBackground != 0) ? GetExtendedBrush(cellStyle.ExtendedBackground) : _palette.GetBrush(cellStyle.Background, bright: false);
        if (cellStyle.Inverse)
        {
            Brush swapBrush = foregroundBrush;
            foregroundBrush = backgroundBrush;
            backgroundBrush = swapBrush;
            if (foregroundBrush == null)
            {
                foregroundBrush = _defaultBackBrush;
            }
            if (backgroundBrush == null)
            {
                backgroundBrush = _defaultColors[0].Foreground;
            }
        }
        if (cellStyle.Override != null && cellStyle.Override.Brush != null)
        {
            foregroundBrush = cellStyle.Override.Brush;
        }
        return new ColorSpan
        {
            Foreground = foregroundBrush,
            Background = backgroundBrush,
            Start = start,
            End = end,
            Underline = cellStyle.Underline || (cellStyle._link.Target?.IsActive ?? false)
        };
    }

    protected override ColorSpan[] GetLineColorSpans(int line)
    {
        LineInfo lineInfo = ActiveBuffer.Lines.GetLine(line);
        if (lineInfo == null)
        {
            return null;
        }
        if (lineInfo.IsDimmed)
        {
            return new ColorSpan[1]
            {
                new ColorSpan
                {
                    Start = 0,
                    End = int.MaxValue,
                    Foreground = InactiveBrush
                }
            };
        }
        if (lineInfo.Cells == null || lineInfo.Cells.Length == 0)
        {
            return null;
        }
        CellStyle cellStyle = default;
        int spanStart = 0;
        List<ColorSpan> list = null;
        for (int column = 0; column < lineInfo.Cells.Length; column++)
        {
            if (cellStyle.StyleEquals(lineInfo.Cells[column]))
            {
                continue;
            }
            if (!cellStyle.IsDefault)
            {
                if (list == null)
                {
                    list = new List<ColorSpan>();
                }
                list.Add(CreateColorSpan(spanStart, column, cellStyle));
            }
            cellStyle = lineInfo.Cells[column];
            spanStart = column;
        }
        if (!cellStyle.IsDefault)
        {
            if (list == null)
            {
                list = new List<ColorSpan>();
            }
            list.Add(CreateColorSpan(spanStart, lineInfo.Cells.Length, cellStyle));
        }
        return list?.ToArray();
    }

    /// <summary>
    /// "Clear" of the context menu: removes the scrollback and everything on the screen above the line of the cursor, which
    /// moves to the top together with its prompt. Nothing is sent to the remote side, so it works whatever is running there.
    /// The alternate screen (vi, mc, htop ...) belongs to the program and is left alone.
    /// </summary>
    public virtual void ClearOutput()
    {
        if (_activeBufferIndex != 0)
        {
            return;
        }
        ScreenBuffer screenBuffer = ActiveBuffer;
        int cursorLine = screenBuffer.FirstRow + CursorPosition.Y;
        // A command line that wrapped over several rows is one line for the shell, which redraws it as a whole.
        while (cursorLine > 0 && screenBuffer.Lines.GetLine(cursorLine - 1)?.IsWrapped == true)
        {
            cursorLine--;
        }
        int linesToRemove = Math.Min(cursorLine, screenBuffer.Document.LineCount);
        if (linesToRemove <= 0)
        {
            return;
        }
        int cursorRow = screenBuffer.FirstRow + CursorPosition.Y - linesToRemove;
        screenBuffer.Document.RemoveLines(0, linesToRemove);
        screenBuffer.Lines.RemoveLines(0, linesToRemove);
        screenBuffer.FirstRow = 0;
        SetSelection(select: false, SelectionStart);
        CursorPosition = new Point(CursorPosition.X, Math.Max(cursorRow, 0));
        TopLine = 0;
        OnTextReplaced();
    }

    private void TrimScrollback()
    {
        ScreenBuffer screenBuffer = ActiveBuffer;
        int overflowLines = screenBuffer.Document.LineCount - ((ReadonlyTextBrowser)this)._visibleRows;
        if (overflowLines > screenBuffer.MaxScrollback)
        {
            int linesToRemove = overflowLines - screenBuffer.MaxScrollback;
            screenBuffer.Document.RemoveLines(0, linesToRemove);
            screenBuffer.Lines.RemoveLines(0, linesToRemove);
            ActiveBuffer.FirstRow -= linesToRemove;
            if (ActiveBuffer.FirstRow < 0)
            {
                ActiveBuffer.FirstRow = 0;
            }
            UpdateLayout();
            Redraw();
        }
    }

    protected Point HitTestCell(Point point)
    {
        TextDocument.TextPosition textPosition = HitTest(point);
        if (!textPosition.IsValid)
        {
            return default;
        }
        return new Point(textPosition.Column, textPosition.Line - ActiveBuffer.FirstRow);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
            base.OnPaint(e);
        }
        catch
        {
        }
    }

    public virtual void Reset()
    {
        base.EditableDocument.Clear();
        ActiveBuffer.FirstRow = 0;
        CursorPosition = new Point(0, 0);
        SetSelection(select: false, new TextDocument.TextPosition(0, 0));
        ActiveBuffer.Lines.Clear();
        CurrentStyle = default;
        OnTextReplaced();
    }

    /// <summary>
    /// Blanks the active screen buffer and its colours, keeping the cursor where it is. Used when an application
    /// switches to the alternate screen (modes 1047/1049), which must not show what the previous one left there.
    /// </summary>
    protected void ClearActiveScreen()
    {
        base.EditableDocument.Clear();
        ActiveBuffer.FirstRow = 0;
        ActiveBuffer.Lines.Clear();
        SetSelection(select: false, new TextDocument.TextPosition(0, 0));
        EnsureCursorPosition(_cursor);
        OnTextReplaced();
    }

    private void DeleteLines(int line, int count, bool scroll)
    {
        if (scroll)
        {
            int insertLine = Math.Min(base.EditableDocument.LineCount, ActiveBuffer.FirstRow + ((ReadonlyTextBrowser)this)._visibleRows);
            if (_scrollRegion != null)
            {
                insertLine = Math.Min(insertLine, ActiveBuffer.FirstRow + _scrollRegion.Bottom + 1);
            }
            base.EditableDocument.InsertText(new TextDocument.TextPosition(0, insertLine), new string('\n', count));
            ActiveBuffer.Lines.InsertLines(insertLine, count);
        }
        int documentLine = line + ActiveBuffer.FirstRow;
        int linesToRemove = Math.Min(count, base.EditableDocument.LineCount - documentLine);
        base.EditableDocument.RemoveLines(documentLine, linesToRemove);
        ActiveBuffer.Lines.RemoveLines(documentLine, linesToRemove);
        OnTextReplaced();
    }

    protected void ScrollRegionUp(int topLine, int count)
    {
        DeleteLines(topLine, count, scroll: true);
    }

    protected void InsertBlankLines(int line, int count, bool redraw = true)
    {
        int blankInsertLine = ((ReadonlyTextBrowser)this)._visibleRows;
        if (_scrollRegion != null && _scrollRegion.Contains(line))
        {
            blankInsertLine = _scrollRegion.Bottom + 1;
        }
        int documentLine = line + ActiveBuffer.FirstRow;
        base.EditableDocument.InsertText(new TextDocument.TextPosition(0, documentLine), new string('\n', count));
        ActiveBuffer.Lines.InsertLines(documentLine, count);
        if (redraw && base.EditableDocument.LineCount > ActiveBuffer.FirstRow + blankInsertLine)
        {
            DeleteLines(blankInsertLine, count, scroll: false);
        }
        OnTextReplaced();
    }

    public string GetLineText(int line)
    {
        TextDocument.TextPosition textPosition = ToDocumentPosition(new Point(0, line));
        return base.EditableDocument.GetLineText(textPosition.Line);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && ((ReadonlyTextBrowser)this).HasSelection && CopyOnSelect)
        {
            CopySelection();
        }
    }

    private string GetLineBreak(int line)
    {
        ScreenBuffer screenBuffer = ActiveBuffer;
        if (screenBuffer != null && screenBuffer.Lines?.GetLine(line)?.IsWrapped == true)
        {
            return "";
        }
        return "\r\n";
    }

    protected override string GetSelectedText(TextDocument.TextPosition start, TextDocument.TextPosition end)
    {
        return base.EditableDocument.GetRangeText(start, end, GetLineBreak);
    }

    public void SelectAllText()
    {
        if (base.EditableDocument.LineCount != 0)
        {
            SetSelectionEnd(new TextDocument.TextPosition(base.EditableDocument.GetLineText(base.EditableDocument.LineCount - 1).Length, base.EditableDocument.LineCount - 1));
            SetSelection(select: true, new TextDocument.TextPosition(0, 0));
        }
    }

    public void SetScrollRegion(int top, int bottom)
    {
        if ((top == 0 && bottom == ((ReadonlyTextBrowser)this)._visibleRows - 1) || bottom <= top)
        {
            ClearScrollRegion();
            return;
        }
        _scrollRegion = new ScrollRegion
        {
            Top = top,
            Bottom = bottom
        };
        // DECSTBM moves the cursor to the home position.
        CursorPosition = new Point(0, 0);
        this.EnsureCursorPosition(_cursor);
    }

    public void ClearScrollRegion()
    {
        _scrollRegion = null;
        CursorPosition = new Point(0, 0);
        EnsureCursorPosition(_cursor);
    }

    public void ArchiveScreen()
    {
        ActiveBufferIndex = 0;
        ActiveBuffer.Lines.DimLines(ToDocumentPosition(_cursor).Line + 1);
        if (_cursor.X > 0)
        {
            MoveCursorBy(-CursorPosition.X, 1, scroll: true);
        }
        Redraw();
    }

    public virtual void BeginNewSession()
    {
        ArchiveScreen();
    }

    public void ClearText()
    {
        Text = "";
        _cursor = default;
        ActiveBuffer.FirstRow = 0;
        UpdateCaret();
        Redraw();
    }
}
