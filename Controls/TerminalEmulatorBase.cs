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

        public ForegroundOverride Override;

        internal LinkCell _link;

        private static readonly string[] ColorNames = new string[9] { "Default", "Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White" };

        public static readonly CellStyle Default = default;

        public bool IsDefault
        {
            get
            {
                if (Foreground == 0 && Background == 0 && !Bold && !Inverse && Override == null)
                {
                    return _link.Target == null;
                }
                return false;
            }
        }

        public bool StyleEquals(CellStyle cellStyle)
        {
            if (Foreground == cellStyle.Foreground && Background == cellStyle.Background && Bold == cellStyle.Bold && Inverse == cellStyle.Inverse && object.Equals(Override, cellStyle.Override))
            {
                return object.Equals(_link, cellStyle._link);
            }
            return false;
        }

        private static string GetColorName(byte b2)
        {
            if (b2 >= ColorNames.Length)
            {
                return "Invalid";
            }
            return ColorNames[b2];
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

        public LineInfo GetLine(int value2)
        {
            if (value2 >= _lines.Count)
            {
                return null;
            }
            return _lines[value2];
        }

        internal void SetCells(int value2, int value3, int value4, CellStyle cellStyle, LinkCell[] items = null)
        {
            TextDocument.TextPosition textPosition = this._terminal.ToDocumentPosition(new Point(0, value2));
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
                CellStyle[] array = new CellStyle[this._terminal._visibleColumns];
                if (lineInfo.Cells != null)
                {
                    lineInfo.Cells.CopyTo(array, 0);
                }
                lineInfo.Cells = array;
            }
            if (value3 == -1 || value3 > this._terminal._visibleColumns)
            {
                value3 = this._terminal._visibleColumns;
            }
            int num = this._terminal._visibleColumns - value3;
            if (value4 == -1 || value4 > num)
            {
                value4 = num;
            }
            for (int num2 = 0; num2 < value4; num2++)
            {
                lineInfo.Cells[value3 + num2] = cellStyle;
                if (items != null && num2 < items.Length)
                {
                    lineInfo.Cells[value3 + num2]._link = items[num2];
                }
                else
                {
                    lineInfo.Cells[value3 + num2]._link = default;
                }
            }
        }

        public void ClearLine(int value2)
        {
            TextDocument.TextPosition textPosition = _terminal.ToDocumentPosition(new Point(0, value2));
            if (textPosition.Line < _lines.Count)
            {
                _lines[textPosition.Line] = null;
            }
        }

        public void RemoveLines(int value2, int value3)
        {
            if (value2 < this._lines.Count)
            {
                value3 = Math.Min(value3, this._lines.Count - value2);
                this._lines.RemoveRange(value2, value3);
            }
        }

        public void InsertLines(int value2, int value3)
        {
            if (value2 < this._lines.Count)
            {
                this._lines.InsertRange(value2, new LineInfo[value3]);
            }
        }

        public void DeleteCells(Point point, int value2)
        {
            TextDocument.TextPosition textPosition = _terminal.ToDocumentPosition(point);
            if (textPosition.Line >= this._lines.Count || this._lines[textPosition.Line] == null)
            {
                return;
            }
            LineInfo lineInfo = this._lines[textPosition.Line];
            lineInfo.IsDimmed = false;
            lineInfo.IsWrapped = false;
            for (int num = textPosition.Column; num < lineInfo.Cells.Length; num++)
            {
                int num2 = num + value2;
                if (num2 < lineInfo.Cells.Length)
                {
                    lineInfo.Cells[num] = lineInfo.Cells[num2];
                }
                else
                {
                    lineInfo.Cells[num] = CellStyle.Default;
                }
            }
        }

        public void InsertCells(Point point, int value2)
        {
            TextDocument.TextPosition textPosition = _terminal.ToDocumentPosition(point);
            if (textPosition.Line < this._lines.Count && this._lines[textPosition.Line] != null)
            {
                LineInfo lineInfo = this._lines[textPosition.Line];
                lineInfo.IsDimmed = false;
                lineInfo.IsWrapped = false;
                for (int num = textPosition.Column + value2; num < lineInfo.Cells.Length; num++)
                {
                    lineInfo.Cells[num] = lineInfo.Cells[num - value2];
                }
                for (int num2 = 0; num2 < value2; num2++)
                {
                    lineInfo.Cells[textPosition.Column + num2] = lineInfo.Cells[textPosition.Column];
                }
            }
        }

        internal void Clear()
        {
            _lines.Clear();
        }

        internal void DimLines(int value2)
        {
            for (int num = 0; num < value2; num++)
            {
                if (num == _lines.Count)
                {
                    _lines.Add(new LineInfo());
                }
                else if (_lines[num] == null)
                {
                    _lines[num] = new LineInfo();
                }
                _lines[num].IsDimmed = true;
            }
        }

        public void MarkWrapped(int value2)
        {
            TextDocument.TextPosition textPosition = _terminal.ToDocumentPosition(new Point(0, value2));
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
            for (int num = 0; num < consolePalette.NormalColors.Length; num++)
            {
                NormalBrushes[num] = new SolidBrush(consolePalette.NormalColors[num]);
            }
            for (int num2 = 0; num2 < consolePalette.NormalColors.Length; num2++)
            {
                this.BrightBrushes[num2] = new SolidBrush(consolePalette.BrightColors[num2]);
            }
        }

        public Brush GetBrush(byte b2, bool flag)
        {
            Brush[] array = (flag ? this.BrightBrushes : this.NormalBrushes);
            if (b2 == 0 || b2 > array.Length)
            {
                return null;
            }
            return array[b2 - 1];
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

        public ScreenBuffer(TerminalEmulatorBase terminalEmulatorBase, int value2 = int.MaxValue)
        {
            Lines = new LineInfoStore(terminalEmulatorBase);
            this.MaxScrollback = value2;
        }
    }

    public class TabStops
    {
        private bool[] _stops = new bool[0];

        public void SetStop(int value2)
        {
            if (value2 > 65536)
            {
                value2 = 65536;
            }
            if (value2 >= _stops.Length)
            {
                Array.Resize(ref _stops, value2 + 1);
            }
            _stops[value2] = true;
        }

        public int NextDistance(int value2, int value3)
        {
            if (value2 >= this._stops.Length)
            {
                if (value3 <= 0)
                {
                    value3 = 8;
                }
                return value3 - value2 % value3;
            }
            for (int num = value2 + 1; num < this._stops.Length; num++)
            {
                if (this._stops[num])
                {
                    return num - value2;
                }
            }
            return 1;
        }

        public void ClearAll()
        {
            for (int num = 0; num < _stops.Length; num++)
            {
                _stops[num] = false;
            }
        }

        internal void ClearStop(int value2)
        {
            if (value2 < _stops.Length)
            {
                _stops[value2] = false;
            }
        }

        internal int PreviousDistance(int value2)
        {
            if (value2 >= _stops.Length)
            {
                value2 = _stops.Length - 1;
            }
            if (value2 <= 0)
            {
                return 0;
            }
            for (int num = value2 - 1; num > 0; num--)
            {
                if (_stops[num])
                {
                    return value2 - num;
                }
            }
            return value2;
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

        public bool Contains(int value2)
        {
            if (value2 >= Top)
            {
                return value2 <= Bottom;
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

        public void Paint(Graphics graphics, Rectangle rectangle, SizeF sizeF, int value2, int value3, int value4, ReadonlyTextBrowser readonlyTextBrowser)
        {
            ScreenBuffer screenBuffer = this._terminal.ActiveBuffer;
            if (screenBuffer == null)
            {
                return;
            }
            using (SolidBrush brush = new SolidBrush(this._terminal.ForeColor))
            {
                for (int num = 0; num < value4; num++)
                {
                    LineInfo lineInfo = screenBuffer.Lines.GetLine(value3 + num);
                    if (lineInfo != null && !(lineInfo.Timestamp == DateTime.MinValue))
                    {
                        int num2 = (int)((float)rectangle.Top + (float)num * sizeF.Height);
                        int bottom = num2 + (int)sizeF.Height;
                        string text = lineInfo.Timestamp.ToString(this._terminal.TimestampFormat);
                        graphics.DrawString(text, readonlyTextBrowser.Font, brush, Rectangle.FromLTRB(rectangle.Left, num2, rectangle.Right, bottom), this._format);
                    }
                }
            }
            using Pen pen = new Pen(Color.FromArgb(128, this._terminal.ForeColor), 1f);
            graphics.DrawLine(pen, rectangle.Right - 5, rectangle.Top, rectangle.Right - 5, rectangle.Bottom);
        }

        public int MeasureWidth(ReadonlyTextBrowser readonlyTextBrowser, Graphics graphics, int value2)
        {
            return (int)Math.Ceiling(graphics.MeasureString(DateTime.Now.ToString(_terminal.TimestampFormat), readonlyTextBrowser.Font).Width) + 10;
        }

        public bool OnMouseDown(Point point, ReadonlyTextBrowser readonlyTextBrowser, int value2, int value3, SizeF sizeF, int value4)
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
            bool flag = _title != value;
            _title = value;
            if (flag && TitleChanged != null)
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
            if (value.Y > ((ReadonlyTextBrowser)this)._visibleRows)
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
            SetSelection(flag: false, SelectionStart);
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
        for (int num = 0; num < _buffers.Length; num++)
        {
            _buffers[num] = new ScreenBuffer(this, (num == 0) ? int.MaxValue : 0);
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
        TextDocument.TextPosition result = new TextDocument.TextPosition(point.X, point.Y);
        result.Line += ActiveBuffer.FirstRow;
        return result;
    }

    private TextDocument.TextPosition EnsureCursorPosition(Point point)
    {
        TextDocument.TextPosition result = ToDocumentPosition(point);
        EnsureDocumentSize(result);
        return result;
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
        bool flag = false;
        while (textPosition.Line >= base.EditableDocument.LineCount)
        {
            base.EditableDocument.AppendLine("");
            flag = true;
        }
        int length = base.EditableDocument.GetLineText(textPosition.Line).Length;
        if (length < textPosition.Column)
        {
            base.EditableDocument.ReplaceInLine(textPosition.Line, length, 0, new string(' ', textPosition.Column - length));
            flag = true;
        }
        if (flag)
        {
            InvalidateLayout();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (ActiveBuffer != null)
        {
            int num = base.EditableDocument.LineCount - ((ReadonlyTextBrowser)this)._visibleRows;
            if (num < 0)
            {
                num = 0;
            }
            TopLine = num;
            int num2 = num - ActiveBuffer.FirstRow;
            ActiveBuffer.FirstRow = num;
            if (num2 != 0)
            {
                MoveCursorBy(0, -num2);
            }
            TrimScrollback();
            Redraw();
        }
    }

    public void MoveCursorBy(int value2, int value3, bool flag = false)
    {
        Point point = _cursor;
        point.Offset(value2, value3);
        if (point.Y < 0)
        {
            point.Y = 0;
        }
        MoveCursorTo(point, flag);
    }

    protected void MoveCursorTo(Point point, bool flag2 = false)
    {
        bool flag = false;
        if ((_scrollRegion != null) & flag2)
        {
            while (point.Y > _scrollRegion.Bottom)
            {
                DeleteLines(_scrollRegion.Top, 1, flag: true);
                point.Y--;
                ForceCaretUpdate();
            }
        }
        if (point.Y >= ((ReadonlyTextBrowser)this)._visibleRows)
        {
            if (flag2)
            {
                ActiveBuffer.FirstRow += point.Y - ((ReadonlyTextBrowser)this)._visibleRows + 1;
                point.Y = ((ReadonlyTextBrowser)this)._visibleRows - 1;
                flag = true;
            }
            else
            {
                point.Y = ((ReadonlyTextBrowser)this)._visibleRows - 1;
            }
        }
        CursorPosition = point;
        this.EnsureCursorPosition(_cursor);
        if (flag)
        {
            TrimScrollback();
        }
    }

    protected void Trace(string text)
    {
    }

    public void WriteText(string text, bool flag = true)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Trace("'" + text + "'");
            Point point = WriteAt(_cursor, text, CurrentStyle, flag: true, _insertMode);
            if (flag)
            {
                CursorPosition = point;
            }
        }
    }

    protected void RepeatPreviousChar(int value2)
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
        WriteAt(CursorPosition, new string(ch, value2), cellStyle);
    }

    public void EraseInDisplay(EraseMode eraseMode)
    {
        Point point = CursorPosition;
        switch (eraseMode)
        {
            case EraseMode.ToEnd:
                {
                    EraseInLine(eraseMode);
                    for (int num = point.Y + 1; num < ((ReadonlyTextBrowser)this)._visibleRows; num++)
                    {
                        ClearLineFrom(num, 0, CurrentStyle);
                    }
                    break;
                }
            case EraseMode.ToStart:
                {
                    EraseInLine(eraseMode);
                    for (int num = 0; num < point.Y; num++)
                    {
                        ClearLineFrom(num, 0, CurrentStyle);
                    }
                    break;
                }
            case EraseMode.All:
                {
                    for (int num = 0; num < ((ReadonlyTextBrowser)this)._visibleRows; num++)
                    {
                        ClearLineFrom(num, 0, CurrentStyle);
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
                WriteAt(new Point(0, point.Y), new string(' ', point.X + 1), CurrentStyle);
                break;
            case EraseMode.All:
                ClearLineFrom(point.Y, 0, CurrentStyle);
                break;
        }
    }

    public void DeleteChars(int value2)
    {
        TextDocument.TextPosition textPosition = ToDocumentPosition(CursorPosition);
        base.EditableDocument.ReplaceInLine(textPosition.Line, textPosition.Column, value2, "");
        ActiveBuffer.Lines.DeleteCells(CursorPosition, value2);
        Redraw();
    }

    public void InsertChars(int value2, char ch = ' ')
    {
        TextDocument.TextPosition textPosition = ToDocumentPosition(CursorPosition);
        base.EditableDocument.ReplaceInLine(textPosition.Line, textPosition.Column, 0, new string(ch, value2));
        ActiveBuffer.Lines.InsertCells(CursorPosition, value2);
        Redraw();
    }

    internal virtual ILinkScanner CreateLinkScanner()
    {
        return null;
    }

    protected Point WriteAt(Point point, string text, CellStyle cellStyle, bool flag = true, bool flag2 = false)
    {
        if (_autoWrap && point.X >= _visibleColumns)
        {
            ActiveBuffer.Lines.MarkWrapped(point.Y);
            point = new Point(0, point.Y + 1);
        }
        if (flag && point.Y >= ((ReadonlyTextBrowser)this)._visibleRows)
        {
            int num = point.Y - ((ReadonlyTextBrowser)this)._visibleRows + 1;
            ActiveBuffer.FirstRow += num;
            point.Offset(0, -num);
        }
        this.EnsureCursorPosition(point);
        if (_autoWrap)
        {
            int num2 = _visibleColumns - point.X;
            if (text.Length > num2)
            {
                point = WriteAt(point, text.Substring(0, num2), cellStyle);
                int num4;
                for (int num3 = num2; num3 < text.Length; num3 += num4)
                {
                    num4 = Math.Min(text.Length - num3, _visibleColumns);
                    point = WriteAt(point, text.Substring(num3, num4), cellStyle);
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
        LinkCell[] array = _linkTracker?.Scan(text, textPosition);
        if (flag2)
        {
            base.EditableDocument.ReplaceInLine(textPosition.Line, textPosition.Column, 0, text);
            ActiveBuffer.Lines.InsertCells(point, text.Length);
        }
        else
        {
            int num5 = Math.Min(text.Length, length - textPosition.Column);
            base.EditableDocument.ReplaceInLine(textPosition.Line, textPosition.Column, num5, text);
        }
        ActiveBuffer.Lines.SetCells(point.Y, point.X, text.Length, cellStyle, array);
        InvalidateView();
        point.Offset(text.Length, 0);
        if (flag && point.Y >= ((ReadonlyTextBrowser)this)._visibleRows)
        {
            int num6 = point.Y - ((ReadonlyTextBrowser)this)._visibleRows + 1;
            ActiveBuffer.FirstRow += num6;
            point.Offset(0, -num6);
        }
        if (!_autoWrap)
        {
            UpdateMaxLineWidth(textPosition.Line);
            InvalidateLayout();
        }
        return point;
    }

    protected void ClearLineFrom(int value2, int value3, CellStyle cellStyle)
    {
        TextDocument.TextPosition textPosition = ToDocumentPosition(new Point(value3, value2));
        base.EditableDocument.ReplaceInLine(textPosition.Line, textPosition.Column, -1, "");
        if (value3 == 0)
        {
            ActiveBuffer.Lines.ClearLine(value2);
        }
        else
        {
            ActiveBuffer.Lines.SetCells(textPosition.Line, value3, -1, CellStyle.Default);
        }
        this.EnsureCursorPosition(_cursor);
        if (!cellStyle.IsDefault)
        {
            int num = _visibleColumns - value3;
            if (num > 0)
            {
                base.EditableDocument.ReplaceInLine(textPosition.Line, textPosition.Column, -1, new string(' ', num));
                ActiveBuffer.Lines.SetCells(value2, value3, num, cellStyle);
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

    private ColorSpan CreateColorSpan(int value2, int value3, CellStyle cellStyle)
    {
        Brush brush = ((cellStyle.Foreground != 0 || !cellStyle.Bold) ? _palette.GetBrush(cellStyle.Foreground, cellStyle.Bold) : _defaultForeBrush);
        Brush brush2 = _palette.GetBrush(cellStyle.Background, flag: false);
        if (cellStyle.Inverse)
        {
            Brush brush3 = brush;
            brush = brush2;
            brush2 = brush3;
            if (brush == null)
            {
                brush = _defaultBackBrush;
            }
            if (brush2 == null)
            {
                brush2 = _defaultColors[0].Foreground;
            }
        }
        if (cellStyle.Override != null && cellStyle.Override.Brush != null)
        {
            brush = cellStyle.Override.Brush;
        }
        return new ColorSpan
        {
            Foreground = brush,
            Background = brush2,
            Start = value2,
            End = value3,
            Underline = (cellStyle._link.Target?.IsActive ?? false)
        };
    }

    protected override ColorSpan[] GetLineColorSpans(int value2)
    {
        LineInfo lineInfo = ActiveBuffer.Lines.GetLine(value2);
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
        int num = 0;
        List<ColorSpan> list = null;
        for (int num2 = 0; num2 < lineInfo.Cells.Length; num2++)
        {
            if (cellStyle.StyleEquals(lineInfo.Cells[num2]))
            {
                continue;
            }
            if (!cellStyle.IsDefault)
            {
                if (list == null)
                {
                    list = new List<ColorSpan>();
                }
                list.Add(CreateColorSpan(num, num2, cellStyle));
            }
            cellStyle = lineInfo.Cells[num2];
            num = num2;
        }
        if (!cellStyle.IsDefault)
        {
            if (list == null)
            {
                list = new List<ColorSpan>();
            }
            list.Add(CreateColorSpan(num, lineInfo.Cells.Length, cellStyle));
        }
        return list?.ToArray();
    }

    private void TrimScrollback()
    {
        ScreenBuffer screenBuffer = ActiveBuffer;
        int num = screenBuffer.Document.LineCount - ((ReadonlyTextBrowser)this)._visibleRows;
        if (num > screenBuffer.MaxScrollback)
        {
            int num2 = num - screenBuffer.MaxScrollback;
            screenBuffer.Document.RemoveLines(0, num2);
            screenBuffer.Lines.RemoveLines(0, num2);
            ActiveBuffer.FirstRow -= num2;
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
        SetSelection(flag: false, new TextDocument.TextPosition(0, 0));
        ActiveBuffer.Lines.Clear();
        CurrentStyle = default;
        OnTextReplaced();
    }

    private void DeleteLines(int value2, int value3, bool flag)
    {
        if (flag)
        {
            int num3 = Math.Min(base.EditableDocument.LineCount, ActiveBuffer.FirstRow + ((ReadonlyTextBrowser)this)._visibleRows);
            if (_scrollRegion != null)
            {
                num3 = Math.Min(num3, ActiveBuffer.FirstRow + _scrollRegion.Bottom + 1);
            }
            base.EditableDocument.InsertText(new TextDocument.TextPosition(0, num3), new string('\n', value3));
            ActiveBuffer.Lines.InsertLines(num3, value3);
        }
        int num = value2 + ActiveBuffer.FirstRow;
        int num2 = Math.Min(value3, base.EditableDocument.LineCount - num);
        base.EditableDocument.RemoveLines(num, num2);
        ActiveBuffer.Lines.RemoveLines(num, num2);
        OnTextReplaced();
    }

    protected void ScrollRegionUp(int value2, int value3)
    {
        DeleteLines(value2, value3, flag: true);
    }

    protected void InsertBlankLines(int value2, int value3, bool flag = true)
    {
        int num = ((ReadonlyTextBrowser)this)._visibleRows;
        if (_scrollRegion != null && _scrollRegion.Contains(value2))
        {
            num = _scrollRegion.Bottom + 1;
        }
        int num2 = value2 + ActiveBuffer.FirstRow;
        base.EditableDocument.InsertText(new TextDocument.TextPosition(0, num2), new string('\n', value3));
        ActiveBuffer.Lines.InsertLines(num2, value3);
        if (flag && base.EditableDocument.LineCount > ActiveBuffer.FirstRow + num)
        {
            DeleteLines(num, value3, flag: false);
        }
        OnTextReplaced();
    }

    public string GetLineText(int value2)
    {
        TextDocument.TextPosition textPosition = ToDocumentPosition(new Point(0, value2));
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

    private string GetLineBreak(int value2)
    {
        ScreenBuffer screenBuffer = ActiveBuffer;
        if (screenBuffer != null && screenBuffer.Lines?.GetLine(value2)?.IsWrapped == true)
        {
            return "";
        }
        return "\r\n";
    }

    protected override string GetSelectedText(TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2)
    {
        return base.EditableDocument.GetRangeText(textPosition, textPosition2, GetLineBreak);
    }

    public void SelectAllText()
    {
        if (base.EditableDocument.LineCount != 0)
        {
            SetSelectionEnd(new TextDocument.TextPosition(base.EditableDocument.GetLineText(base.EditableDocument.LineCount - 1).Length, base.EditableDocument.LineCount - 1));
            SetSelection(flag: true, new TextDocument.TextPosition(0, 0));
        }
    }

    public void SetScrollRegion(int value2, int value3)
    {
        if ((value2 == 0 && value3 == ((ReadonlyTextBrowser)this)._visibleRows - 1) || value3 <= value2)
        {
            ClearScrollRegion();
            return;
        }
        _scrollRegion = new ScrollRegion
        {
            Top = value2,
            Bottom = value3
        };
        CursorPosition = new Point(value2, 0);
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
            MoveCursorBy(-CursorPosition.X, 1, flag: true);
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
