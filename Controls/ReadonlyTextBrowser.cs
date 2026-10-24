using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class ReadonlyTextBrowser : DpiAwareControl, ICaretHost
{
    private class ScrollBarAdapter : IScrollBar, IDisposable
    {
        private readonly ScrollBar _scrollBar;

        public int Maximum => _scrollBar.Maximum;

        public int Value
        {
            get
            {
                return _scrollBar.Value;
            }
            set
            {
                _scrollBar.Value = value;
            }
        }

        public bool Visible
        {
            get
            {
                return _scrollBar.Visible;
            }
            set
            {
                _scrollBar.Visible = value;
            }
        }

        public ScrollLimits Limits
        {
            get
            {
                return new ScrollLimits
                {
                    Minimum = _scrollBar.Minimum,
                    Maximum = _scrollBar.Maximum,
                    LargeChange = _scrollBar.LargeChange
                };
            }
            set
            {
                _scrollBar.Minimum = value.Minimum;
                _scrollBar.Maximum = value.Maximum;
                _scrollBar.LargeChange = value.LargeChange;
            }
        }

        public int Width => _scrollBar.Width;

        public int Height => _scrollBar.Height;

        public event EventHandler ValueChanged;

        public ScrollBarAdapter(ScrollBar scrollBar)
        {
            _scrollBar = scrollBar;
            scrollBar.Cursor = Cursors.Arrow;
            scrollBar.ValueChanged += (object sender, EventArgs e) =>
            {
                ValueChanged?.Invoke(this, e);
            };
        }

        public void Dispose()
        {
            _scrollBar.Dispose();
        }

        public void Register(ReadonlyTextBrowser readonlyTextBrowser, DockStyle dockStyle)
        {
            _scrollBar.Dock = dockStyle;
            readonlyTextBrowser.Controls.Add(_scrollBar);
        }
    }

    public class LineRange
    {
        public readonly int First;

        public readonly int Last;

        public int Count
        {
            get
            {
                if (Last < First)
                {
                    return 0;
                }
                return Last - First + 1;
            }
        }

        public LineRange(int start, int end)
        {
            First = start;
            Last = end;
        }
    }

    public enum CaretDirection
    {
        Forward,
        Backward,
        Unspecified
    }

    private class MarginSlot
    {
        public IMargin Margin;

        public int Left;

        public int Right;

        public MarginSlot(IMargin margin)
        {
            Margin = margin;
        }
    }

    private struct CharMetricsCache
    {
        public SizeF CharSize;

        public Font Font;

        public int Width;

        public StringFormat StringFormat;
    }

    protected class HoverTarget
    {
        public Cursor Cursor;

        public Action ClickAction;
    }

    protected enum CaretMove
    {
        Left,
        Right,
        Up,
        Down,
        WordLeft,
        WordRight,
        Home,
        End,
        PageUp,
        PageDown
    }

    public struct ColorSpan(int start, int end, Brush brush)
    {
        public int Start = start;

        public int End = end;

        public Brush Foreground = brush;

        public Brush Background = null;

        public bool Underline = false;
    }

    private class TextRunBatch
    {
        private float _x;

        private float _y;

        private int _lastIndex;

        private string _text;

        private Font _font;

        private Brush _foreground;

        private Brush _background;

        private StringFormat _stringFormat;

        private SizeF _charSize;

        private void FlushPending(Graphics graphics)
        {
            if (_text != null)
            {
                if (_background != null)
                {
                    graphics.FillRectangle(_background, _x, _y, (float)_text.Length * _charSize.Width, _charSize.Height);
                }
                graphics.DrawString(_text, _font, _foreground, _x, _y, _stringFormat);
            }
            _text = null;
        }

        public void Append(Graphics graphics, Font font, string text, float x, float y, int index, Brush foregroundBrush, Brush backgroundBrush, StringFormat stringFormat, SizeF sizeF)
        {
            if (this._lastIndex != index || this._y != y || this._foreground != foregroundBrush || this._stringFormat != stringFormat || _background != backgroundBrush)
            {
                this.FlushPending(graphics);
            }
            if (this._text == null)
            {
                this._text = text;
                this._x = x;
            }
            else
            {
                this._text += text;
            }
            this._charSize = sizeF;
            this._lastIndex = index + 1;
            this._y = y;
            this._foreground = foregroundBrush;
            this._font = font;
            this._stringFormat = stringFormat;
            _background = backgroundBrush;
        }

        public void Flush(Graphics graphics)
        {
            FlushPending(graphics);
        }
    }

    public class Highlight
    {
        public TextDocument.TextPosition Start;

        public TextDocument.TextPosition End;

        public HighlightStyle Style;

        public Highlight(TextDocument.TextPosition start, TextDocument.TextPosition end, HighlightStyle highlightStyle)
        {
            this.Start = start;
            this.End = end;
            this.Style = highlightStyle;
        }
    }

    internal struct DisplayRow
    {
        public int Line;

        public IVirtualLine VirtualLine;

        public ILineDecorator Decorator;

        public override string ToString()
        {
            if (VirtualLine == null)
            {
                return $"Line {Line}";
            }
            return $"Virtual line {VirtualLine} after line {Line}";
        }
    }

    public interface IVirtualLine
    {
        void Draw(Graphics graphics, ReadonlyTextBrowser readonlyTextBrowser, int row, int firstColumn, int line, Rectangle rectangle, SizeF sizeF);
    }

    public interface ILineDecorator
    {
        string Transform(string text, out CharacterRange characterRange, out Brush brush);

        void DrawDecoration(Graphics graphics, SizeF sizeF, Rectangle rectangle, CharacterRange characterRange);

        TextDocument.TextPosition MapPosition(string text, TextDocument.TextPosition textPosition);
    }

    public interface IVirtualLineProvider
    {
        int GetTotalVirtualRows();

        int GetTotalHiddenLines();

        int GetNextSpecialLine(int line);

        int GetVirtualRowCount(int line);

        void GetVirtualLines(int line, List<IVirtualLine> list);

        int GetHiddenLineCount(int line);

        ILineDecorator GetDecorator(int line);

        bool OnCaretMoved(int line, int column);
    }

    private class CompositeVirtualLineProvider : IVirtualLineProvider
    {
        private List<IVirtualLineProvider> _providers;

        public CompositeVirtualLineProvider(List<IVirtualLineProvider> list)
        {
            this._providers = list;
        }

        public int GetTotalVirtualRows()
        {
            int totalRows = 0;
            for (int providerIndex = 0; providerIndex < _providers.Count; providerIndex++)
            {
                totalRows += _providers[providerIndex].GetTotalVirtualRows();
            }
            return totalRows;
        }

        public int GetTotalHiddenLines()
        {
            int totalLines = 0;
            for (int providerIndex = 0; providerIndex < _providers.Count; providerIndex++)
            {
                totalLines += _providers[providerIndex].GetTotalHiddenLines();
            }
            return totalLines;
        }

        public int GetNextSpecialLine(int line)
        {
            int nextLine = int.MaxValue;
            for (int providerIndex = 0; providerIndex < this._providers.Count; providerIndex++)
            {
                int providerLine = this._providers[providerIndex].GetNextSpecialLine(line);
                if (providerLine < nextLine)
                {
                    nextLine = providerLine;
                }
            }
            return nextLine;
        }

        public int GetVirtualRowCount(int line)
        {
            int totalRows = 0;
            for (int providerIndex = 0; providerIndex < this._providers.Count; providerIndex++)
            {
                totalRows += this._providers[providerIndex].GetVirtualRowCount(line);
            }
            return totalRows;
        }

        public void GetVirtualLines(int line, List<IVirtualLine> list)
        {
            for (int providerIndex = 0; providerIndex < this._providers.Count; providerIndex++)
            {
                this._providers[providerIndex].GetVirtualLines(line, list);
            }
        }

        public int GetHiddenLineCount(int line)
        {
            int totalLines = 0;
            for (int providerIndex = 0; providerIndex < this._providers.Count; providerIndex++)
            {
                totalLines += this._providers[providerIndex].GetHiddenLineCount(line);
            }
            return totalLines;
        }

        public ILineDecorator GetDecorator(int line)
        {
            for (int providerIndex = 0; providerIndex < this._providers.Count; providerIndex++)
            {
                ILineDecorator lineDecorator = this._providers[providerIndex].GetDecorator(line);
                if (lineDecorator != null)
                {
                    return lineDecorator;
                }
            }
            return null;
        }

        public bool OnCaretMoved(int line, int column)
        {
            bool caretMoved = false;
            foreach (IVirtualLineProvider item in this._providers)
            {
                if (item.OnCaretMoved(line, column))
                {
                    caretMoved = true;
                }
            }
            return caretMoved;
        }
    }

    public delegate void SelectionChangedHandler(ReadonlyTextBrowser readonlyTextBrowser, TextDocument.TextPosition previousCaret, TextDocument.TextPosition currentCaret, bool hasSelection);

    public enum SelectionChangeSource
    {
        FindResult,
        Programmatic
    }

    public delegate void CtrlTabHandler(ReadonlyTextBrowser readonlyTextBrowser, bool shift);

    public enum FindSelectMode
    {
        None,
        SelectOrClear,
        Select
    }

    private int _tabSize = 4;

    private int _verticalWheelStep = 3;

    private int _horizontalWheelStep = 3;

    private int _ctrlWheelMultiplier = 10;

    private int _leftPadding = 5;

    private bool _groupBinaryDigits;

    private bool _showTabs;

    private EditableTextDocument _document = new EditableTextDocument();

    private IScrollBar _verticalScrollBar = new ScrollBarAdapter(new VScrollBar());

    private IScrollBar _horizontalScrollBar = new ScrollBarAdapter(new HScrollBar());

    private SizeF _charSize;

    protected int _visibleColumns;

    protected internal int _visibleRows;

    private Rectangle _textArea;

    public const string WordChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

    private HighlightStyle _selectionStyle = new HighlightStyle();

    private ICaret _caret;

    internal readonly StringFormat _stringFormat = new StringFormat(StringFormat.GenericTypographic);

    private TextDocument.TextPosition _caretPosition;

    private TextDocument.TextPosition _selectionStart;

    private TextDocument.TextPosition _selectionEnd;

    protected internal bool _hasSelection;

    private bool _terminalMode;

    private bool _navigationLocked;

    private bool _hideMargins;

    private bool _singleLine;

    private bool _showLineNumbers = true;

    private bool _noHorizontalScroll;

    private bool _forceCaretUpdate;

    private List<MarginSlot> _margins = new List<MarginSlot>();

    private int _maxLineWidth = -1;

    protected bool _allowPartialRows;

    public bool NativeScrollSemantics = true;

    private CharMetricsCache _metricsCache;

    private readonly int _doubleClickTime;

    private DateTime _lastClickTime;

    private DateTime _lastDoubleClickTime;

    private Point _lastClickPoint;

    private HoverTarget _hoverTarget;

    private int _desiredColumn = -1;

    protected ColorSpan[] _defaultColors = new ColorSpan[1]
    {
        new ColorSpan(0, int.MaxValue, Brushes.Black)
    };

    private TextRunBatch _textBatch = new TextRunBatch();

    private Dictionary<int, Highlight> _highlights = new Dictionary<int, Highlight>();

    private int _lastHighlightId;

    protected bool _layoutSuspended;

    protected bool _resizePending;

    private IVirtualLineProvider _virtualLines;

    public List<IVirtualLineProvider> VirtualLineProviders = new List<IVirtualLineProvider>();

    private DisplayRow[] _displayRows;

    private int _firstVisibleLine;

    private int _lastVisibleLine;

    public double Value;

    private int _previousRealLine;

    private int _nextRealLine;

    private int _flashStep;

    private Bitmap _flashBitmap;

    private Rectangle _flashRect;

    protected Pen _flashPen = Pens.Black;

    private Rectangle _caretBounds;

    private bool _caretHidden;

    private MouseButtons _lastMouseButton;

    public Brush Brush = new SolidBrush(SystemColors.ButtonFace);

    public Brush LineNumberForegroundBrush = Brushes.DarkGray;

    public int LineHeight => (int)Math.Ceiling(_charSize.Height);

    public int TabSize
    {
        get
        {
            return _tabSize;
        }
        set
        {
            _tabSize = value;
            UpdateLayout();
        }
    }

    internal EditableTextDocument Document => _document;

    public HighlightStyle SelectionStyle
    {
        get
        {
            return _selectionStyle;
        }
        set
        {
            _selectionStyle = value;
        }
    }

    public bool GroupBinaryDigits
    {
        get
        {
            return _groupBinaryDigits;
        }
        set
        {
            _groupBinaryDigits = value;
            Redraw();
        }
    }

    public int TopLine
    {
        get
        {
            return ScrollLine;
        }
        set
        {
            if (value < 0)
            {
                value = 0;
            }
            if (value > _verticalScrollBar.Maximum)
            {
                value = _verticalScrollBar.Maximum;
            }
            ScrollLine = value;
        }
    }

    public int LeftColumn => ScrollColumn;

    public bool OverwriteMode
    {
        get
        {
            return _caret.OverwriteMode;
        }
        set
        {
            _caret.OverwriteMode = value;
        }
    }

    public bool TerminalMode
    {
        get
        {
            return _terminalMode;
        }
        set
        {
            bool isUnderline = (_caret.UnderlineStyle = value);
            _terminalMode = isUnderline;
        }
    }

    public bool NavigationLocked
    {
        get
        {
            return _navigationLocked;
        }
        set
        {
            _navigationLocked = value;
        }
    }

    protected bool ShowCaretWhenUnfocused
    {
        get
        {
            return _caret.ShowWhenUnfocused;
        }
        set
        {
            _caret.ShowWhenUnfocused = value;
        }
    }

    public bool HideMargins
    {
        get
        {
            return _hideMargins;
        }
        set
        {
            _hideMargins = value;
            UpdateLayout();
            Redraw();
        }
    }

    public bool SingleLine
    {
        get
        {
            return _singleLine;
        }
        set
        {
            _singleLine = value;
            UpdateLayout();
            Redraw();
        }
    }

    public bool ShowLineNumbers
    {
        get
        {
            return _showLineNumbers;
        }
        set
        {
            if (value != _showLineNumbers)
            {
                if (value)
                {
                    AddMargin(new LineNumberMargin(this));
                }
                else
                {
                    foreach (MarginSlot item in _margins)
                    {
                        if (item.Margin is LineNumberMargin)
                        {
                            _margins.Remove(item);
                            break;
                        }
                    }
                }
                UpdateLayout();
            }
            _showLineNumbers = value;
        }
    }

    public int LineCount => _document.LineCount;

    public TextDocument.TextPosition SelectionStart => _selectionStart;

    public TextDocument.TextPosition SelectionEnd
    {
        get
        {
            if (TerminalMode)
            {
                return _selectionEnd;
            }
            return _caretPosition;
        }
    }

    public bool IsCaretAtLineEnd => _document.GetLineText(_caretPosition.Line).Length == _caretPosition.Column;

    public bool HasSelection => _hasSelection;

    protected Rectangle TextArea => _textArea;

    protected internal SizeF CharSize => _charSize;

    public TextDocument.TextPosition CaretPosition => _caretPosition;

    public LineRange SelectedLines
    {
        get
        {
            if (!_hasSelection || SelectionEnd == SelectionStart)
            {
                return new LineRange(0, -1);
            }
            if (SelectionEnd < SelectionStart)
            {
                return new LineRange(SelectionEnd.Line, SelectionStart.LastFullLine);
            }
            return new LineRange(SelectionStart.Line, SelectionEnd.LastFullLine);
        }
    }

    private int ScrollLine
    {
        get
        {
            if (_singleLine)
            {
                return 0;
            }
            return Math.Min(_verticalScrollBar.Value, Math.Max(LineCount - _visibleRows, 0));
        }
        set
        {
            _verticalScrollBar.Value = value;
        }
    }

    public bool NoHorizontalScroll
    {
        get
        {
            return _noHorizontalScroll;
        }
        set
        {
            _noHorizontalScroll = value;
            ScrollColumn = 0;
        }
    }

    protected int ScrollColumn
    {
        get
        {
            return _horizontalScrollBar.Value;
        }
        set
        {
            try
            {
                if (_noHorizontalScroll)
                {
                    value = 0;
                }
                if (value == int.MaxValue)
                {
                    value = Math.Max(0, _horizontalScrollBar.Maximum - _visibleColumns);
                }
                _horizontalScrollBar.Value = value;
            }
            catch (Exception)
            {
            }
        }
    }

    public bool NoLeftPadding { get; set; }

    protected virtual bool AllowCrossLineWordSelection => false;

    protected bool FillLineBackground { get; set; }

    public override string Text
    {
        get
        {
            return _document.ToString();
        }
        set
        {
            _document.SetText(value);
            OnTextReplaced();
        }
    }

    // The font chosen by the application (terminal settings) and the factor it is shown with on the current
    // monitor (monitor DPI / system DPI, see DpiScaling): a font assigned later still gets the monitor scale.
    private Font _logicalFont;

    private float _fontDpiScale = 1f;

    public override Font Font
    {
        get
        {
            return base.Font;
        }
        set
        {
            _logicalFont = value;
            base.Font = value == null || _fontDpiScale == 1f ? value : SmarTTY.Forms.DpiScaling.ScaleFont(value, _fontDpiScale);
        }
    }

    internal void SetFontDpiScale(float scale)
    {
        if (scale == _fontDpiScale)
        {
            return;
        }
        _fontDpiScale = scale;
        if (_logicalFont != null)
        {
            base.Font = scale == 1f ? _logicalFont : SmarTTY.Forms.DpiScaling.ScaleFont(_logicalFont, scale);
        }
    }

    public bool LayoutSuspended
    {
        get
        {
            return _layoutSuspended;
        }
        set
        {
            _layoutSuspended = value;
            if (!_layoutSuspended && _resizePending)
            {
                _resizePending = false;
                OnResize(null);
            }
        }
    }

    internal DisplayRow[] DisplayRows => _displayRows;

    public Rectangle CaretBounds
    {
        get
        {
            return _caretBounds;
        }
        set
        {
            _caretBounds = value;
        }
    }

    private Control AsControl => this;

    Control ICaretHost.HostControl => AsControl;

    public bool IsScrolledToEnd => TopLine + _visibleRows >= LineCount;

    public bool VerticalScrollBarVisible
    {
        get
        {
            return _verticalScrollBar.Visible;
        }
        set
        {
            if (_verticalScrollBar.Visible != value)
            {
                _verticalScrollBar.Visible = value;
            }
        }
    }

    public bool HorizontalScrollBarVisible
    {
        get
        {
            return _horizontalScrollBar.Visible;
        }
        set
        {
            if (_horizontalScrollBar.Visible != value)
            {
                _horizontalScrollBar.Visible = value;
            }
        }
    }

    public bool HideCaret
    {
        get
        {
            return _caretHidden;
        }
        set
        {
            _caretHidden = value;
            if (value)
            {
                _caret.Disable();
            }
            else
            {
                _caret.Enable();
            }
        }
    }

    public int FirstVisibleLine => _firstVisibleLine;

    public int VisibleRowCount => _visibleRows;

    public event SelectionChangedHandler SelectionChanged;

    public event CtrlTabHandler CtrlTabPressed;

    public void UseUnderlineCaret()
    {
        _caret.UnderlineStyle = true;
    }

    private TextDocument.TextPosition NormalizePosition(TextDocument.TextPosition textPosition)
    {
        int row = LineToRow(textPosition.Line);
        if (row == -1)
        {
            return textPosition;
        }
        if (_displayRows[row].VirtualLine != null)
        {
            textPosition.Line = _displayRows[row].Line;
        }
        if (_displayRows[row].Decorator != null)
        {
            textPosition = _displayRows[row].Decorator.MapPosition(_document.GetLineText(textPosition.Line), textPosition);
        }
        string text = _document.GetLineText(textPosition.Line);
        if (textPosition.Column > text.Length)
        {
            textPosition.Column = text.Length;
        }
        return textPosition;
    }

    protected void ForceCaretUpdate()
    {
        _forceCaretUpdate = true;
    }

    public void SetCaret(TextDocument.TextPosition textPosition, CaretDirection caretDirection = CaretDirection.Unspecified)
    {
        SetCaretCore(textPosition, SelectionChangeSource.Programmatic, caretDirection);
    }

    protected virtual void SetCaretCore(TextDocument.TextPosition textPosition, SelectionChangeSource selectionChangeSource, CaretDirection caretDirection = CaretDirection.Unspecified)
    {
        if (!(_caretPosition == textPosition) || _forceCaretUpdate)
        {
            if (_virtualLines.OnCaretMoved(textPosition.Line, textPosition.Column))
            {
                UpdateLayout();
            }
            TextDocument.TextPosition previousCaret = _caretPosition;
            _caretPosition = NormalizePosition(textPosition);
            OnSelectionChanged(previousCaret, _caretPosition, _hasSelection, selectionChangeSource);
            EnsureCaretVisible(_caretPosition);
            UpdateCaret();
            _forceCaretUpdate = false;
        }
    }

    public void SetSelection(bool select, TextDocument.TextPosition textPosition)
    {
        SetSelectionCore(select, textPosition, SelectionChangeSource.Programmatic);
    }

    protected void SetSelectionCore(bool select, TextDocument.TextPosition textPosition, SelectionChangeSource selectionChangeSource)
    {
        bool selectionChanged = false;
        TextDocument.TextPosition previousCaret = _caretPosition;
        if (_hasSelection != select)
        {
            selectionChanged = true;
            _hasSelection = select;
        }
        if (_selectionStart != textPosition)
        {
            _selectionStart = textPosition;
            OnSelectionStartChanged();
            selectionChanged = true;
        }
        if (selectionChanged)
        {
            Redraw();
            OnSelectionChanged(previousCaret, _caretPosition, _hasSelection, selectionChangeSource);
        }
    }

    protected void SetCaretColumn(int column)
    {
        TextDocument.TextPosition textPosition = _caretPosition;
        _caretPosition.Column = column;
        _caretPosition = NormalizePosition(_caretPosition);
        EnsureCaretVisible(_caretPosition);
        OnSelectionChanged(textPosition, _caretPosition, _hasSelection, SelectionChangeSource.Programmatic);
        UpdateCaret();
    }

    private int Round(float number)
    {
        return (int)Math.Round(number);
    }

    [DllImport("user32.dll")]
    private static extern int GetDoubleClickTime();

    public ReadonlyTextBrowser()
    {
        InitializeComponent();
        AddMargin(new LineNumberMargin(this));
        _virtualLines = new CompositeVirtualLineProvider(VirtualLineProviders);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
        _verticalScrollBar.Register(this, DockStyle.Right);
        _horizontalScrollBar.Register(this, DockStyle.Bottom);
        UpdateLayout();
        _verticalScrollBar.ValueChanged += OnScrollBarChanged;
        _horizontalScrollBar.ValueChanged += OnScrollBarChanged;
        PlatformID platform = Environment.OSVersion.Platform;
        if (platform == PlatformID.Unix || platform == PlatformID.MacOSX)
        {
            _caret = new SoftwareCaret(this);
        }
        else
        {
            _caret = new WindowsCaret(this);
        }
        try
        {
            _doubleClickTime = GetDoubleClickTime();
        }
        catch (Exception)
        {
            _doubleClickTime = 500;
        }
    }

    protected void UpdateMaxLineWidth(int line)
    {
        if (line == -1)
        {
            _maxLineWidth = -1;
        }
        else if (_maxLineWidth != -1)
        {
            int lineLength = _document.IndexToColumn(line, _document.GetLineText(line).Length - 1, TabSize);
            if (lineLength > _maxLineWidth)
            {
                _maxLineWidth = lineLength;
            }
        }
    }

    protected internal void AddMargin(IMargin margin)
    {
        _margins.Add(new MarginSlot(margin));
    }

    protected internal void ClearMargins()
    {
        _margins.Clear();
    }

    public void UpdateLayout()
    {
        if (_verticalScrollBar == null || _horizontalScrollBar == null)
        {
            return;
        }
        if (_maxLineWidth == -1)
        {
            _maxLineWidth = _document.GetMaxLineWidth(TabSize);
        }
        Graphics graphics = null;
        try
        {
            if (_metricsCache.Font != Font || _metricsCache.StringFormat != _stringFormat || _metricsCache.Width != Width)
            {
                if (graphics == null)
                {
                    graphics = Graphics.FromHwnd(Handle);
                }
                _metricsCache.CharSize = graphics.MeasureString("M", Font, Width, _stringFormat);
                _metricsCache.Font = Font;
                _metricsCache.StringFormat = _stringFormat;
                _metricsCache.Width = Width;
            }
            _charSize = _metricsCache.CharSize;
            int marginRight = 0;
            if (!_hideMargins && _margins.Count > 0)
            {
                if (graphics == null)
                {
                    graphics = Graphics.FromHwnd(Handle);
                }
                for (int marginIndex = 0; marginIndex < _margins.Count; marginIndex++)
                {
                    _margins[marginIndex].Left = marginRight;
                    _margins[marginIndex].Right = _margins[marginIndex].Left + _margins[marginIndex].Margin.MeasureWidth(this, graphics, _document.LineCount);
                    marginRight = _margins[marginIndex].Right;
                }
            }
            int scrollBarWidth = (_verticalScrollBar.Visible ? _verticalScrollBar.Width : 0);
            int scrollBarHeight = (_horizontalScrollBar.Visible ? _horizontalScrollBar.Height : 0);
            int leftPadding = ((!NoLeftPadding) ? _leftPadding : 0);
            if (_singleLine)
            {
                int verticalCenter = (int)((float)(ClientRectangle.Height - scrollBarHeight) - _charSize.Height) / 2;
                _textArea = new Rectangle(ClientRectangle.Left + marginRight + leftPadding, ClientRectangle.Top + verticalCenter, ClientRectangle.Width - (scrollBarWidth + marginRight + leftPadding), (int)_charSize.Height);
            }
            else
            {
                _textArea = new Rectangle(ClientRectangle.Left + marginRight + leftPadding, ClientRectangle.Top, ClientRectangle.Width - (scrollBarWidth + marginRight + leftPadding), ClientRectangle.Height - scrollBarHeight);
            }
            int previousVisibleRows = _visibleRows;
            int previousVisibleColumns = _visibleColumns;
            if (_allowPartialRows)
            {
                _visibleColumns = (int)Math.Ceiling((float)_textArea.Width / _charSize.Width);
                _visibleRows = (int)Math.Ceiling((float)_textArea.Height / _charSize.Height);
            }
            else
            {
                _visibleColumns = (int)Math.Floor((float)_textArea.Width / _charSize.Width);
                _visibleRows = (int)Math.Floor((float)_textArea.Height / _charSize.Height);
            }
            if (_visibleColumns < 1)
            {
                _visibleColumns = 1;
            }
            if (_visibleRows < 1)
            {
                _visibleRows = 1;
            }
            if (_visibleRows != previousVisibleRows || _visibleColumns != previousVisibleColumns)
            {
                AddRef();
            }
            int lineCount = _document.LineCount;
            if (_virtualLines != null)
            {
                lineCount += _virtualLines.GetTotalVirtualRows() - _virtualLines.GetTotalHiddenLines();
            }
            if (lineCount < 0)
            {
                lineCount = 0;
            }
            if (_singleLine && lineCount > 1)
            {
                lineCount = 1;
            }
            if (NativeScrollSemantics)
            {
                _verticalScrollBar.Limits = new ScrollLimits
                {
                    Maximum = Math.Max(lineCount, 0),
                    LargeChange = _visibleRows
                };
                _horizontalScrollBar.Limits = new ScrollLimits
                {
                    Maximum = _maxLineWidth,
                    LargeChange = _visibleColumns
                };
            }
            else
            {
                _verticalScrollBar.Limits = new ScrollLimits
                {
                    Maximum = Math.Max(lineCount - _visibleRows + 1, 0),
                    LargeChange = _visibleRows
                };
                _horizontalScrollBar.Limits = new ScrollLimits
                {
                    Maximum = Math.Max(_maxLineWidth - _visibleColumns + 1, 0),
                    LargeChange = _visibleColumns
                };
            }
            int horizontalScrollRange = _maxLineWidth - _visibleColumns + 2;
            if (horizontalScrollRange < 0)
            {
                horizontalScrollRange = 0;
            }
            ScrollColumn = Math.Min(ScrollColumn, horizontalScrollRange);
            int verticalScrollRange = _document.LineCount - _visibleRows + 2;
            if (verticalScrollRange < 0)
            {
                verticalScrollRange = 0;
            }
            ScrollLine = Math.Min(ScrollLine, verticalScrollRange);
            UpdateCaret();
            RebuildDisplayRows();
        }
        finally
        {
            graphics?.Dispose();
        }
    }

    protected virtual void AddRef()
    {
    }

    private void UpdateCaret()
    {
        if (_caret == null)
        {
            return;
        }
        if (!TryGetCaretColumn(CaretPosition, out var caretColumn))
        {
            _caret.Disable();
            return;
        }
        int dx = 0;
        if (_hasSelection)
        {
            if (CaretPosition.Line < _selectionStart.Line && CaretPosition.Line == _document.GetLineText(CaretPosition.Line).Length)
            {
                dx = 1;
            }
            if (_selectionStart < CaretPosition)
            {
                dx = 1;
            }
            else if (_selectionStart > CaretPosition)
            {
                dx = 1;
            }
        }
        else
        {
            foreach (Highlight value in _highlights.Values)
            {
                if (value.End == _caretPosition)
                {
                    dx = 1;
                }
                else if (value.Start == _caretPosition)
                {
                    dx = 1;
                }
            }
        }
        Point point = Point.Round(PositionToPoint(caretColumn, CaretPosition.Line));
        point.Offset(dx, 0);
        _caret.SetPosition(point);
        if (!_caretHidden)
        {
            _caret.Enable();
        }
    }

    protected bool TryGetCaretColumn(TextDocument.TextPosition textPosition, out int column)
    {
        column = 0;
        if (textPosition.Line < _firstVisibleLine || textPosition.Line > _lastVisibleLine)
        {
            return false;
        }
        column = _document.IndexToColumn(textPosition.Line, textPosition.Column, TabSize);
        if (column < ScrollColumn || column >= ScrollColumn + _visibleColumns)
        {
            return false;
        }
        return true;
    }

    public float ColumnToX(int column)
    {
        int visibleColumn = column - ScrollColumn;
        if (visibleColumn < 0)
        {
            visibleColumn = 0;
        }
        if (visibleColumn > _visibleColumns)
        {
            visibleColumn = _visibleColumns;
        }
        return (float)_textArea.Left + (float)visibleColumn * _charSize.Width;
    }

    private float RowToY(int row)
    {
        return (float)_textArea.Top + (float)row * _charSize.Height;
    }

    private float LineToY(int line)
    {
        int rowIndex = -1;
        for (int candidateRow = 0; candidateRow < _displayRows.Length; candidateRow++)
        {
            if (_displayRows[candidateRow].Line == line)
            {
                rowIndex = candidateRow;
                break;
            }
        }
        if (rowIndex == -1)
        {
            return -1f;
        }
        if (rowIndex < 0)
        {
            rowIndex = 0;
        }
        if (rowIndex >= this._visibleRows)
        {
            rowIndex = this._visibleRows - 1;
        }
        return RowToY(rowIndex);
    }

    protected PointF PositionToPoint(int column, int line)
    {
        return new PointF(ColumnToX(column), LineToY(line));
    }

    private int YToRow(float y)
    {
        y -= (float)_textArea.Top;
        if (y < 0f)
        {
            y = 0f;
        }
        if (y > (float)_textArea.Height)
        {
            y = _textArea.Height;
        }
        int row = (int)(y / _charSize.Height);
        if (row >= this._visibleRows && this._visibleRows != 0)
        {
            row = this._visibleRows - 1;
        }
        return row;
    }

    protected int YToLine(float y)
    {
        int row = YToRow(y);
        if (_displayRows[row].VirtualLine != null)
        {
            return -1;
        }
        return _displayRows[row].Line;
    }

    private int XToColumn(float x)
    {
        x -= (float)_textArea.Left;
        if (x < 0f)
        {
            x = 0f;
        }
        return ScrollColumn + (int)((x + _charSize.Width / 2f) / _charSize.Width);
    }

    protected TextDocument.TextPosition HitTest(Point point, bool allowPastEnd = false)
    {
        int line = YToLine(point.Y);
        if (line == -1)
        {
            return new TextDocument.TextPosition(-1, -1);
        }
        TextDocument.TextPosition columnIndex = new TextDocument.TextPosition(_document.ColumnToIndex(line, XToColumn(point.X), TabSize, out var isPastEnd), line);
        if (columnIndex.Line >= _document.LineCount || (!allowPastEnd & isPastEnd))
        {
            return new TextDocument.TextPosition(-1, -1);
        }
        int length = _document.GetLineText(columnIndex.Line).Length;
        if (columnIndex.Column > length)
        {
            if (!allowPastEnd)
            {
                return new TextDocument.TextPosition(-1, -1);
            }
            columnIndex.Column = length;
        }
        return columnIndex;
    }

    private void MoveCaretToPoint(Point point)
    {
        TextDocument.TextPosition textPosition = HitTest(point, allowPastEnd: true);
        if (textPosition.IsValid)
        {
            SetCaret(textPosition);
        }
    }

    public virtual string GetIndentation(int line)
    {
        string text = Document.GetLineText(line);
        int indentLength;
        for (indentLength = 0; indentLength < text.Length && (text[indentLength] == ' ' || text[indentLength] == '\t'); indentLength++)
        {
        }
        return text.Substring(0, indentLength);
    }

    public string GetLineOrNull(int line)
    {
        if (line >= _document.LineCount)
        {
            return null;
        }
        return _document.GetLineText(line);
    }

    public int ColumnToIndex(int line, int column)
    {
        return _document.ColumnToIndex(line, column, TabSize);
    }

    public int IndexToColumn(int line, int index)
    {
        return _document.IndexToColumn(line, index, TabSize);
    }

    private bool EnsureCaretVisible(TextDocument.TextPosition textPosition)
    {
        return EnsureVisible(textPosition, center: false);
    }

    private bool EnsureVisible(TextDocument.TextPosition textPosition, bool center)
    {
        int firstLine = _firstVisibleLine;
        int lastLine = _lastVisibleLine;
        int firstColumn = ScrollColumn;
        int lastColumn = firstColumn + _visibleColumns - 1;
        bool lineScrolled = true;
        if (!center)
        {
            if (textPosition.Line < firstLine)
            {
                TopLine = LineToRowIndex(textPosition.Line);
            }
            else if (textPosition.Line > lastLine || (_allowPartialRows && textPosition.Line == lastLine && LineToRow(textPosition.Line) >= this._visibleRows - 1))
            {
                TopLine = LineToRowIndex(textPosition.Line) - this._visibleRows + 2;
            }
            else
            {
                lineScrolled = false;
            }
        }
        else
        {
            int topRow = LineToRowIndex(textPosition.Line) - this._visibleRows / 2;
            if (topRow < 0)
            {
                topRow = 0;
            }
            TopLine = topRow;
            lineScrolled = true;
        }
        bool columnScrolled = true;
        if (!_terminalMode)
        {
            int caretColumn = _document.IndexToColumn(textPosition.Line, textPosition.Column, TabSize);
            if (caretColumn < firstColumn)
            {
                ScrollColumn = caretColumn;
            }
            else if (caretColumn >= lastColumn)
            {
                ScrollColumn = caretColumn - _visibleColumns + 2;
            }
            else
            {
                columnScrolled = false;
            }
        }
        return lineScrolled | columnScrolled;
    }

    protected virtual bool OnTripleClick(MouseEventArgs e)
    {
        return false;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        bool handledByMargin = false;
        if (_hoverTarget?.ClickAction != null && e.Button == MouseButtons.Left)
        {
            _hoverTarget.ClickAction();
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            bool isDoubleClick = (DateTime.Now - _lastDoubleClickTime).TotalMilliseconds <= (double)_doubleClickTime;
            if (isDoubleClick && OnTripleClick(e))
            {
                _lastMouseButton = e.Button;
                return;
            }
            if (_lastClickPoint == e.Location && (DateTime.Now - _lastClickTime).TotalMilliseconds <= (double)_doubleClickTime)
            {
                if (_terminalMode)
                {
                    TextDocument.TextPosition textPosition = HitTest(e.Location);
                    if (textPosition.IsValid && textPosition.Line < Document.LineCount)
                    {
                        if (isDoubleClick)
                        {
                            SetSelection(select: true, new TextDocument.TextPosition(0, textPosition.Line));
                            SetSelectionEnd(new TextDocument.TextPosition(GetLineOrNull(textPosition.Line).Length, textPosition.Line));
                            InvalidateLine(textPosition.Line);
                        }
                        else
                        {
                            TextDocument.TextPosition wordLeft = ComputeMovedPosition(CaretMove.WordLeft, Control.ModifierKeys, textPosition);
                            TextDocument.TextPosition wordRight = ComputeMovedPosition(CaretMove.WordRight, Control.ModifierKeys, textPosition);
                            if (wordLeft.Line == wordRight.Line || AllowCrossLineWordSelection)
                            {
                                SetSelection(select: true, wordLeft);
                                SetSelectionEnd(wordRight);
                                InvalidateLine(wordLeft.Line);
                            }
                        }
                    }
                }
                else
                {
                    TextDocument.TextPosition wordLeft = GetMovedPosition(CaretMove.WordLeft);
                    TextDocument.TextPosition wordRight = GetMovedPosition(CaretMove.WordRight);
                    if (wordLeft.Line == CaretPosition.Line && wordRight.Line == CaretPosition.Line)
                    {
                        bool invalidateCurrentLine = true;
                        if (_hasSelection && _selectionStart.Line != CaretPosition.Line)
                        {
                            invalidateCurrentLine = false;
                        }
                        _selectionStart = wordLeft;
                        _hasSelection = true;
                        OnSelectionStartChanged();
                        if (!NavigationLocked)
                        {
                            SetCaret(wordRight);
                        }
                        if (invalidateCurrentLine)
                        {
                            InvalidateLine(wordLeft.Line);
                        }
                        else
                        {
                            Redraw();
                        }
                    }
                }
                _lastDoubleClickTime = DateTime.Now;
                _lastMouseButton = e.Button;
                base.OnMouseDoubleClick(e);
            }
            else if (e.Location.X < _textArea.Left)
            {
                bool handled = false;
                if (!_hideMargins)
                {
                    foreach (MarginSlot item in _margins)
                    {
                        if (e.X >= item.Left && e.X <= item.Right)
                        {
                            handled = item.Margin.OnMouseDown(new Point(e.X - item.Left, e.Y - _textArea.Top), this, ScrollLine, this._visibleRows, _charSize, YToRow(e.Y));
                            if (handled)
                            {
                                handledByMargin = true;
                                break;
                            }
                        }
                    }
                }
                int line = YToLine(e.Location.Y);
                if (!handled && line != -1)
                {
                    _hasSelection = true;
                    _selectionStart = new TextDocument.TextPosition(0, line);
                    OnSelectionStartChanged();
                    if (!_terminalMode && !NavigationLocked)
                    {
                        SetCaret(new TextDocument.TextPosition(_document.GetLineText(line).Length, line));
                    }
                }
            }
            else
            {
                if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift)
                {
                    if (!_hasSelection)
                    {
                        _hasSelection = true;
                        _selectionStart = SelectionEnd;
                        OnSelectionStartChanged();
                    }
                }
                else
                {
                    _hasSelection = false;
                    OnSelectionStartChanged();
                }
                PlaceCaretAt(e.Location);
            }
            if (handledByMargin)
            {
                _lastClickTime = DateTime.MinValue;
            }
            else
            {
                _lastClickTime = DateTime.Now;
            }
            _lastClickPoint = e.Location;
            Redraw();
        }
        else if (!_hasSelection)
        {
            PlaceCaretAt(e.Location);
        }
        base.OnMouseDown(e);
    }

    protected virtual void PlaceCaretAt(Point point)
    {
        if (_terminalMode)
        {
            UpdateSelectionFromPoint(adjustStart: false, point, select: false);
        }
        else if (!_navigationLocked)
        {
            MoveCaretToPoint(point);
        }
    }

    protected virtual void AdjustSelectionEnd(ref TextDocument.TextPosition textPosition)
    {
    }

    private void UpdateSelectionFromPoint(bool adjustStart, Point point, bool select)
    {
        TextDocument.TextPosition textPosition = HitTest(point, allowPastEnd: true);
        if (!textPosition.IsValid)
        {
            return;
        }
        bool selectionChanged = false;
        TextDocument.TextPosition previousCaret = _caretPosition;
        if (_hasSelection != select)
        {
            selectionChanged = true;
            _hasSelection = select;
        }
        if (adjustStart)
        {
            AdjustSelectionEnd(ref textPosition);
            if (_selectionStart != textPosition)
            {
                _selectionStart = textPosition;
                selectionChanged = true;
                OnSelectionStartChanged();
            }
        }
        else if (_selectionEnd != textPosition)
        {
            _selectionEnd = textPosition;
            selectionChanged = true;
        }
        if (selectionChanged)
        {
            Redraw();
            OnSelectionChanged(previousCaret, _caretPosition, _hasSelection, SelectionChangeSource.Programmatic);
        }
    }

    protected virtual void OnSelectionStartChanged()
    {
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _hoverTarget != null && !_hasSelection)
        {
            return;
        }
        _hoverTarget = null;
        if (e.Button == MouseButtons.Left && _lastMouseButton != MouseButtons.Left)
        {
            if (!_hasSelection && (!_terminalMode || HitTest(e.Location, allowPastEnd: true).IsValid))
            {
                _selectionStart = CaretPosition;
                _hasSelection = true;
                OnSelectionStartChanged();
            }
            TextDocument.TextPosition textPosition = CaretPosition;
            if (_terminalMode)
            {
                UpdateSelectionFromPoint(adjustStart: true, e.Location, select: true);
            }
            else if (!_navigationLocked)
            {
                MoveCaretToPoint(e.Location);
            }
            UpdateCaret();
            if (SelectionEnd == SelectionStart)
            {
                _hasSelection = false;
                OnSelectionStartChanged();
            }
            if (SelectionEnd.Line == textPosition.Line)
            {
                InvalidateLine(CaretPosition.Line);
            }
            else
            {
                Redraw();
            }
        }
        if (_textArea.Contains(e.Location))
        {
            int line = YToLine(e.Y);
            if (line != -1)
            {
                _hoverTarget = GetHoverTarget(line, e.Location);
                Cursor = _hoverTarget?.Cursor ?? Cursors.IBeam;
                base.OnMouseMove(e);
                return;
            }
        }
        Cursor = Cursors.Arrow;
        base.OnMouseMove(e);
    }

    protected virtual HoverTarget GetHoverTarget(int line, Point point)
    {
        return null;
    }

    protected TextDocument.TextPosition GetMovedPosition(CaretMove caretMove, bool extendSelection = false)
    {
        return ComputeMovedPosition(caretMove, Control.ModifierKeys, CaretPosition, extendSelection);
    }

    protected virtual bool IsWordChar(char ch)
    {
        return "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".IndexOf(ch) != -1;
    }

    protected virtual void OnMoveBeyondDocument(ref TextDocument.TextPosition textPosition, int direction)
    {
    }

    protected virtual TextDocument.TextPosition ComputeMovedPosition(CaretMove caretMove, Keys keys, TextDocument.TextPosition textPosition, bool extendSelection = false)
    {
        if (textPosition.Column < 0 || textPosition.Line < 0)
        {
            return default;
        }
        string text = _document.GetLineText(textPosition.Line);
        switch (caretMove)
        {
            case CaretMove.Left:
            case CaretMove.WordLeft:
                if (textPosition.Column > 0)
                {
                    textPosition.Column--;
                    if (caretMove == CaretMove.WordLeft)
                    {
                        while (textPosition.Column > 0 && IsWordChar(text[textPosition.Column - 1]) && IsWordChar(text[textPosition.Column]))
                        {
                            textPosition.Column--;
                        }
                    }
                }
                else
                {
                    if (textPosition.Line == 0)
                    {
                        OnMoveBeyondDocument(ref textPosition, -1);
                    }
                    if (textPosition.Line > 0)
                    {
                        textPosition.Line = GetPreviousRealLine(textPosition.Line);
                        textPosition.Column = _document.GetLineText(textPosition.Line).Length;
                    }
                }
                return textPosition;
            case CaretMove.Right:
            case CaretMove.WordRight:
                if (textPosition.Column < text.Length)
                {
                    textPosition.Column++;
                    if (caretMove == CaretMove.WordRight)
                    {
                        while (textPosition.Column > 0 && textPosition.Column < text.Length && IsWordChar(text[textPosition.Column - 1]) && IsWordChar(text[textPosition.Column]))
                        {
                            textPosition.Column++;
                        }
                    }
                }
                else
                {
                    if (textPosition.Line == _document.LineCount - 1)
                    {
                        OnMoveBeyondDocument(ref textPosition, 1);
                    }
                    if (textPosition.Line < _document.LineCount - 1)
                    {
                        textPosition.Line = GetNextRealLine(textPosition.Line);
                        textPosition.Column = 0;
                    }
                }
                return textPosition;
            case CaretMove.Up:
                if (textPosition.Line == 0)
                {
                    OnMoveBeyondDocument(ref textPosition, -1);
                }
                if (textPosition.Line > 0)
                {
                    textPosition.Line = GetPreviousRealLine(textPosition.Line);
                    int index = _document.ColumnToIndex(textPosition.Line, _desiredColumn, TabSize);
                    textPosition.Column = Math.Min(index, _document.GetLineText(textPosition.Line).Length);
                }
                return textPosition;
            case CaretMove.Down:
                if (textPosition.Line == _document.LineCount - 1)
                {
                    OnMoveBeyondDocument(ref textPosition, 1);
                }
                if (textPosition.Line < _document.LineCount - 1)
                {
                    textPosition.Line = GetNextRealLine(textPosition.Line);
                    int columnIndexAfterDown = _document.ColumnToIndex(textPosition.Line, _desiredColumn, TabSize);
                    textPosition.Column = Math.Min(columnIndexAfterDown, _document.GetLineText(textPosition.Line).Length);
                }
                return textPosition;
            case CaretMove.Home:
                if ((keys & Keys.Control) == Keys.Control)
                {
                    OnMoveBeyondDocument(ref textPosition, int.MinValue);
                    textPosition.Column = 0;
                    textPosition.Line = 0;
                }
                else
                {
                    string lineText = _document.GetLineText(textPosition.Line);
                    int indentLength;
                    for (indentLength = 0; indentLength < lineText.Length && (lineText[indentLength] == ' ' || lineText[indentLength] == '\t'); indentLength++)
                    {
                    }
                    if (textPosition.Column == indentLength)
                    {
                        textPosition.Column = 0;
                    }
                    else
                    {
                        textPosition.Column = indentLength;
                    }
                }
                return textPosition;
            case CaretMove.End:
                if ((keys & Keys.Control) == Keys.Control)
                {
                    OnMoveBeyondDocument(ref textPosition, int.MaxValue);
                    textPosition.Line = _document.LineCount - 1;
                }
                textPosition.Column = _document.GetLineText(textPosition.Line).Length;
                return textPosition;
            case CaretMove.PageUp:
                {
                    textPosition.Line -= this._visibleRows;
                    if (textPosition.Line < 0)
                    {
                        OnMoveBeyondDocument(ref textPosition, -this._visibleRows);
                    }
                    if (textPosition.Line < 0)
                    {
                        textPosition.Line = 0;
                    }
                    int columnAfterPageUp = _document.ColumnToIndex(textPosition.Line, _desiredColumn, TabSize);
                    textPosition.Column = Math.Min(columnAfterPageUp, _document.GetLineText(textPosition.Line).Length);
                    return textPosition;
                }
            case CaretMove.PageDown:
                {
                    textPosition.Line += this._visibleRows;
                    if (textPosition.Line >= _document.LineCount)
                    {
                        OnMoveBeyondDocument(ref textPosition, this._visibleRows);
                    }
                    if (textPosition.Line >= _document.LineCount)
                    {
                        textPosition.Line = _document.LineCount - 1;
                    }
                    int columnAfterPageDown = _document.ColumnToIndex(textPosition.Line, _desiredColumn, TabSize);
                    textPosition.Column = Math.Min(columnAfterPageDown, _document.GetLineText(textPosition.Line).Length);
                    return textPosition;
                }
            default:
                return textPosition;
        }
    }

    protected void MoveCaret(CaretMove caretMove)
    {
        TextDocument.TextPosition textPosition = GetMovedPosition(caretMove);
        SetCaret(textPosition);
        UpdateCaret();
        if (EnsureCaretVisible(CaretPosition))
        {
            Redraw();
        }
    }

    private bool TryGetNavigationTarget(KeyEventArgs e, bool extendSelection, out TextDocument.TextPosition textPosition, out CaretDirection caretDirection)
    {
        caretDirection = CaretDirection.Unspecified;
        Keys keyCode = e.KeyCode;
        if ((uint)(keyCode - 33) <= 1u || keyCode == Keys.Up || keyCode == Keys.Down)
        {
            if (_desiredColumn == -1)
            {
                _desiredColumn = _document.IndexToColumn(CaretPosition.Line, CaretPosition.Column, TabSize);
            }
        }
        else
        {
            _desiredColumn = -1;
        }
        switch (e.KeyCode)
        {
            case Keys.Left:
                if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
                {
                    textPosition = GetMovedPosition(CaretMove.WordLeft, extendSelection);
                }
                else
                {
                    textPosition = GetMovedPosition(CaretMove.Left, extendSelection);
                }
                caretDirection = CaretDirection.Backward;
                return true;
            case Keys.Right:
                if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
                {
                    textPosition = GetMovedPosition(CaretMove.WordRight, extendSelection);
                }
                else
                {
                    textPosition = GetMovedPosition(CaretMove.Right, extendSelection);
                }
                caretDirection = CaretDirection.Forward;
                return true;
            case Keys.Up:
                textPosition = GetMovedPosition(CaretMove.Up, extendSelection);
                return true;
            case Keys.Down:
                textPosition = GetMovedPosition(CaretMove.Down, extendSelection);
                return true;
            case Keys.Home:
                textPosition = GetMovedPosition(CaretMove.Home, extendSelection);
                return true;
            case Keys.End:
                textPosition = GetMovedPosition(CaretMove.End, extendSelection);
                return true;
            case Keys.Prior:
                textPosition = GetMovedPosition(CaretMove.PageUp, extendSelection);
                return true;
            case Keys.Next:
                textPosition = GetMovedPosition(CaretMove.PageDown, extendSelection);
                return true;
            default:
                textPosition = new TextDocument.TextPosition(CaretPosition);
                return false;
        }
    }

    protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        Keys keyCode = e.KeyCode;
        if (keyCode == Keys.Tab || (uint)(keyCode - 33) <= 7u)
        {
            e.IsInputKey = true;
        }
    }

    protected override bool IsInputChar(char charCode)
    {
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
        {
            switch (e.KeyCode)
            {
                case Keys.Up:
                    if (!TerminalMode)
                    {
                        if (ScrollLine > 0)
                        {
                            ScrollLine--;
                            UpdateCaret();
                            Redraw();
                            e.Handled = true;
                        }
                        return;
                    }
                    break;
                case Keys.Down:
                    if (!TerminalMode)
                    {
                        if (ScrollLine < _document.LineCount - this._visibleRows + 2)
                        {
                            ScrollLine++;
                            UpdateCaret();
                            Redraw();
                            e.Handled = true;
                        }
                        return;
                    }
                    break;
                case Keys.A:
                    SelectAll();
                    e.Handled = true;
                    break;
                case Keys.Tab:
                    OnCtrlTab(e.Shift);
                    e.SuppressKeyPress = true;
                    break;
            }
        }
        bool navigated = false;
        bool needsRedraw = false;
        if (!e.Handled && !_navigationLocked)
        {
            CaretDirection caretDirection;
            if ((Control.ModifierKeys & Keys.Shift) != Keys.Shift)
            {
                if (TryGetNavigationTarget(e, extendSelection: true, out var textPosition, out caretDirection))
                {
                    navigated = true;
                    if (_hasSelection)
                    {
                        if (_selectionStart.Line != textPosition.Line)
                        {
                            needsRedraw = true;
                        }
                        _hasSelection = false;
                        OnSelectionStartChanged();
                    }
                    if (textPosition.Line != CaretPosition.Line)
                    {
                        needsRedraw = true;
                    }
                    if (!NavigationLocked)
                    {
                        SetCaret(textPosition, caretDirection);
                    }
                    e.Handled = true;
                    if (EnsureCaretVisible(CaretPosition))
                    {
                        needsRedraw = true;
                    }
                }
            }
            else
            {
                bool selectionStarted = false;
                if (!_hasSelection)
                {
                    _hasSelection = true;
                    _selectionStart = CaretPosition;
                    selectionStarted = true;
                    OnSelectionStartChanged();
                }
                if (TryGetNavigationTarget(e, extendSelection: true, out var navigationTarget, out caretDirection))
                {
                    navigated = true;
                    if (navigationTarget.Line != CaretPosition.Line)
                    {
                        needsRedraw = true;
                    }
                    if (!NavigationLocked)
                    {
                        SetCaret(navigationTarget, caretDirection);
                    }
                    e.Handled = true;
                    if (EnsureCaretVisible(CaretPosition))
                    {
                        needsRedraw = true;
                    }
                }
                else if (selectionStarted)
                {
                    _hasSelection = false;
                    OnSelectionStartChanged();
                }
            }
        }
        if (needsRedraw)
        {
            Redraw();
        }
        else if (navigated)
        {
            InvalidateLine(CaretPosition.Line);
        }
        base.OnKeyDown(e);
        if (!e.Handled && e.KeyCode == Keys.Insert)
        {
            if (!e.Shift && !e.Control)
            {
                OverwriteMode = !OverwriteMode;
            }
            e.Handled = true;
        }
    }

    /// <summary>Selects the whole text (Ctrl+A); nothing happens in terminal mode.</summary>
    public void SelectAllText()
    {
        SelectAll();
    }

    protected virtual void SelectAll()
    {
        if (!TerminalMode)
        {
            _selectionStart = new TextDocument.TextPosition(0, 0);
            OnSelectionStartChanged();
            SetCaret(new TextDocument.TextPosition(_document.GetLineText(_document.LineCount - 1).Length, _document.LineCount - 1));
            _hasSelection = true;
            EnsureCaretVisible(CaretPosition);
            Redraw();
        }
    }

    private void PaintSelection(Graphics graphics, TextDocument.TextPosition start, TextDocument.TextPosition end, HighlightStyle highlightStyle, Brush[,] items)
    {
        if (start == end)
        {
            return;
        }
        TextDocument.TextPosition rangeStart;
        TextDocument.TextPosition rangeEnd;
        if (start < end)
        {
            rangeStart = start;
            rangeEnd = end;
        }
        else
        {
            rangeStart = end;
            rangeEnd = start;
        }
        int firstColumn = ScrollColumn;
        int lastColumn = firstColumn + _visibleColumns - 1;
        int previousStartX = -1;
        int previousEndX = -1;
        for (int row = 0; row < this._visibleRows; row++)
        {
            int line = _displayRows[row].Line;
            if (line < rangeStart.Line)
            {
                continue;
            }
            if (line > rangeEnd.Line)
            {
                break;
            }
            string text = _document.GetLineText(line);
            int lineLength = _document.IndexToColumn(line, text.Length, TabSize);
            int startColumn = 0;
            int endColumn = lineLength;
            if (line == rangeStart.Line)
            {
                startColumn = _document.IndexToColumn(line, rangeStart.Column, TabSize);
                if (startColumn > lineLength)
                {
                    startColumn = lineLength;
                }
            }
            if (line == rangeEnd.Line)
            {
                endColumn = _document.IndexToColumn(line, rangeEnd.Column, TabSize);
                if (endColumn > lineLength)
                {
                    endColumn = lineLength;
                }
            }
            if (rangeStart.Line == rangeEnd.Line && _displayRows[row].Line == rangeStart.Line && _displayRows[row].VirtualLine != null)
            {
                break;
            }
            if (highlightStyle.TextBrush != null)
            {
                for (int column = Math.Max(startColumn, firstColumn); column < Math.Min(endColumn, lastColumn + 1); column++)
                {
                    items[row, column - firstColumn] = highlightStyle.TextBrush;
                }
            }
            int startX = Round(ColumnToX(startColumn));
            int endX = Round(ColumnToX(endColumn));
            if ((line != rangeStart.Line || line != rangeEnd.Line) && startColumn == endColumn)
            {
                endX = startX + Round(_charSize.Width / 4f);
            }
            if (_displayRows[row].VirtualLine != null)
            {
                startX = previousStartX;
                endX = previousEndX;
            }
            int y = Round(RowToY(row));
            if (highlightStyle.FillBrush != null)
            {
                graphics.FillRectangle(highlightStyle.FillBrush, startX, y, endX - startX, _charSize.Height);
            }
            if (highlightStyle.BorderPen != null)
            {
                if (line == rangeStart.Line)
                {
                    graphics.DrawLine(highlightStyle.BorderPen, startX, y, endX, y);
                }
                else
                {
                    if (previousStartX != startX)
                    {
                        graphics.DrawLine(highlightStyle.BorderPen, startX, y, previousStartX, y);
                    }
                    if (previousEndX != endX)
                    {
                        graphics.DrawLine(highlightStyle.BorderPen, endX, y, previousEndX, y);
                    }
                }
                if (line == rangeEnd.Line)
                {
                    graphics.DrawLine(highlightStyle.BorderPen, startX, (float)y + _charSize.Height, endX, (float)y + _charSize.Height);
                }
                graphics.DrawLine(highlightStyle.BorderPen, startX, y, startX, (float)y + _charSize.Height);
                graphics.DrawLine(highlightStyle.BorderPen, endX, y, endX, (float)y + _charSize.Height);
            }
            previousStartX = startX;
            previousEndX = endX;
        }
    }

    protected void InvalidateLine(int line)
    {
        if (line >= _firstVisibleLine && line <= _lastVisibleLine)
        {
            int row = LineToRow(line);
            if (row != -1)
            {
                Invalidate(new Rectangle(0, Round((float)_textArea.Top + (float)row * _charSize.Height), Width, (int)Math.Ceiling(_charSize.Height)));
            }
        }
    }

    protected void InvalidateLines(int startLine, int endLine)
    {
        if (startLine > endLine)
        {
            int temporaryLine = startLine;
            startLine = endLine;
            endLine = temporaryLine;
        }
        if (startLine < _firstVisibleLine)
        {
            startLine = _firstVisibleLine;
        }
        if (endLine > _lastVisibleLine)
        {
            endLine = _lastVisibleLine;
        }
        int startRow = LineToRow(startLine);
        int endRow = LineToRow(endLine);
        if (startRow != -1 && endRow != -1 && endRow >= startRow)
        {
            Invalidate(new Rectangle(Left, Round((float)_textArea.Top + (float)startRow * _charSize.Height), Width, (int)Math.Ceiling((float)(endRow - startRow + 1) * _charSize.Height + 1f)));
        }
    }

    protected virtual ColorSpan[] GetLineColorSpans(int line)
    {
        return null;
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        _defaultColors[0].Foreground = new SolidBrush(base.ForeColor);
    }

    private bool IsBinaryDigit(char ch)
    {
        if (ch != '0')
        {
            return ch == '1';
        }
        return true;
    }

    private void PaintLine(Graphics graphics, int line, int row, int firstColumn, Brush[,] items)
    {
        int startIndex = 0;
        int startColumn = 0;
        CharacterRange characterRange = default;
        Brush brush = null;
        string text = _document.GetLineText(line);
        if (_displayRows[row].Decorator != null)
        {
            text = _displayRows[row].Decorator.Transform(text, out characterRange, out brush);
        }
        text = TextDocument.SubstringFromColumn(text, firstColumn, TabSize, out startIndex, out startColumn);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        int binaryDigitCount = 0;
        if (_groupBinaryDigits && IsBinaryDigit(text[0]))
        {
            string lineText = _document.GetLineText(line);
            int binaryIndex = startColumn;
            while (binaryIndex > 0 && IsBinaryDigit(lineText[binaryIndex]))
            {
                binaryDigitCount++;
                binaryIndex--;
            }
        }
        binaryDigitCount %= 4;
        if (startColumn > characterRange.First)
        {
            characterRange.First = startColumn;
        }
        int characterOffset = characterRange.First - startColumn;
        if (characterRange.Length > text.Length - characterOffset)
        {
            characterRange.Length = text.Length - characterOffset;
        }
        int left = _textArea.Left;
        int y = Round((float)_textArea.Top + (float)row * _charSize.Height);
        int tabStartColumn = -1;
        ColorSpan[] colorSpans = ((brush == null) ? GetLineColorSpans(line) : new ColorSpan[1]
        {
            new ColorSpan(0, int.MaxValue, brush)
        });
        if (colorSpans == null || colorSpans.Length == 0)
        {
            colorSpans = _defaultColors;
        }
        int spanIndex = 0;
        List<KeyValuePair<int, int>> list = null;
        Pen pen = null;
        Brush backgroundBrush = null;
        int lastPaintedColumn = 0;
        int characterIndex = 0;
        int column = startIndex;
        while (characterIndex < text.Length && column < _visibleColumns)
        {
            int displayColumn = column + firstColumn;
            int lineColumn = characterIndex + startColumn;
            lastPaintedColumn = column;
            Brush foregroundBrush = _defaultColors[0].Foreground;
            while (colorSpans[spanIndex].End <= lineColumn)
            {
                if (++spanIndex >= colorSpans.Length)
                {
                    colorSpans = _defaultColors;
                    spanIndex = 0;
                    break;
                }
            }
            bool isUnderlined = false;
            if (colorSpans[spanIndex].Start <= lineColumn)
            {
                foregroundBrush = colorSpans[spanIndex].Foreground;
                if (foregroundBrush == null)
                {
                    foregroundBrush = _defaultColors[0].Foreground;
                }
                backgroundBrush = colorSpans[spanIndex].Background;
                isUnderlined = colorSpans[spanIndex].Underline;
            }
            else
            {
                backgroundBrush = null;
            }
            if (items != null && items[row, column] != null)
            {
                foregroundBrush = items[row, column];
                backgroundBrush = null;
            }
            if (_showTabs)
            {
                if (text[characterIndex] == '\t')
                {
                    if (tabStartColumn == -1)
                    {
                        tabStartColumn = characterIndex;
                    }
                }
                else if (tabStartColumn != -1)
                {
                    float tabLineY = (float)y + _charSize.Height / 2f;
                    graphics.DrawLine(Pens.DarkGray, (float)left + (float)tabStartColumn * _charSize.Width, tabLineY, (float)left + (float)column * _charSize.Width, tabLineY);
                    tabStartColumn = -1;
                }
            }
            if (text[characterIndex] == '\t')
            {
                column += TabSize - displayColumn % TabSize - 1;
            }
            else
            {
                _textBatch.Append(graphics, Font, text.Substring(characterIndex, 1), (float)left + (float)column * _charSize.Width, y, characterIndex, foregroundBrush, backgroundBrush, _stringFormat, _charSize);
            }
            if (characterIndex == characterRange.First && _displayRows[row].Decorator != null)
            {
                _displayRows[row].Decorator.DrawDecoration(graphics, _charSize, new Rectangle(left + (int)((float)column * _charSize.Width), y, (int)(_charSize.Width * (float)characterRange.Length), (int)_charSize.Height), characterRange);
            }
            bool needsUnderline = false;
            if (_groupBinaryDigits)
            {
                binaryDigitCount = (IsBinaryDigit(text[characterIndex]) ? (binaryDigitCount + 1) : 0);
                if (binaryDigitCount >= 4)
                {
                    needsUnderline = true;
                }
            }
            if (isUnderlined)
            {
                needsUnderline = true;
            }
            if (needsUnderline)
            {
                if (list == null)
                {
                    list = new List<KeyValuePair<int, int>>();
                    pen = new Pen((foregroundBrush as SolidBrush)?.Color ?? Color.Black);
                }
                int underlineStartX = (int)((float)left + (float)column * _charSize.Width);
                int value = (int)((float)left + (float)(column + 1) * _charSize.Width);
                if (list.Count > 0 && underlineStartX == list[list.Count - 1].Value)
                {
                    underlineStartX = list[list.Count - 1].Key;
                    list.RemoveAt(list.Count - 1);
                }
                list.Add(new KeyValuePair<int, int>(underlineStartX, value));
                binaryDigitCount = 0;
            }
            characterIndex++;
            column++;
        }
        _textBatch.Flush(graphics);
        if (backgroundBrush != null && FillLineBackground && lastPaintedColumn != _visibleColumns)
        {
            graphics.FillRectangle(backgroundBrush, ColumnToX(lastPaintedColumn + 1 + ScrollColumn), LineToY(line), _charSize.Width * (float)(_visibleColumns - lastPaintedColumn), _charSize.Height);
        }
        if (_showTabs && tabStartColumn != -1)
        {
            float tabUnderlineY = (float)y + _charSize.Height / 2f;
            graphics.DrawLine(Pens.DarkGray, (float)left + (float)tabStartColumn * _charSize.Width, tabUnderlineY, (float)left + (float)text.Length * _charSize.Width, tabUnderlineY);
        }
        PaintLineOverlay(graphics, line, firstColumn, left, y, (int)Math.Ceiling(_charSize.Height));
        if (list == null || pen == null)
        {
            return;
        }
        int underlineY = y + (int)_charSize.Height;
        foreach (KeyValuePair<int, int> item in list)
        {
            graphics.DrawLine(pen, item.Key, underlineY - 1, item.Value, underlineY - 1);
        }
    }

    protected virtual void PaintLineOverlay(Graphics graphics, int line, int firstColumn, int left, int top, int height)
    {
    }

    public int AddHighlight(TextDocument.TextPosition start, TextDocument.TextPosition end, HighlightStyle highlightStyle)
    {
        return SetHighlight(-1, start, end, highlightStyle);
    }

    public void RemoveHighlight(int highlightIndex)
    {
        if (highlightIndex != -1)
        {
            Highlight highlight = _highlights[highlightIndex];
            InvalidateLines(highlight.Start.Line, highlight.End.Line);
            _highlights.Remove(highlightIndex);
        }
    }

    public int SetHighlight(int highlightIndex, TextDocument.TextPosition start, TextDocument.TextPosition end, HighlightStyle highlightStyle)
    {
        if (highlightIndex == -1)
        {
            highlightIndex = ++_lastHighlightId;
        }
        else
        {
            Highlight highlight = _highlights[highlightIndex];
            InvalidateLines(highlight.Start.Line, highlight.End.Line);
        }
        Highlight updatedHighlight = new Highlight(start, end, highlightStyle);
        _highlights[highlightIndex] = updatedHighlight;
        InvalidateLines(updatedHighlight.Start.Line, updatedHighlight.End.Line);
        return highlightIndex;
    }

    public void Redraw()
    {
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_flashBitmap != null)
        {
            e.Graphics.DrawImage(_flashBitmap, 0, 0);
            PaintFlash(e.Graphics);
            return;
        }
        if (!_hideMargins)
        {
            foreach (MarginSlot item in _margins)
            {
                item.Margin.Paint(e.Graphics, new Rectangle(item.Left, 0, item.Right - item.Left, Height), _charSize, _document.LineCount, ScrollLine, this._visibleRows, this);
            }
        }
        Brush[,] backgroundBrushes = new Brush[this._visibleRows, _visibleColumns];
        foreach (Highlight value in _highlights.Values)
        {
            PaintSelection(e.Graphics, value.Start, value.End, value.Style, backgroundBrushes);
        }
        PaintMatches(e.Graphics);
        if (_hasSelection)
        {
            PaintSelection(e.Graphics, _selectionStart, SelectionEnd, _selectionStyle, backgroundBrushes);
        }
        int firstColumn = ScrollColumn;
        int firstRow = ((e.ClipRectangle.Top >= _textArea.Top) ? ((int)Math.Floor((float)(e.ClipRectangle.Top - _textArea.Top) / _charSize.Height)) : 0);
        int lastRow = (int)Math.Ceiling(((float)(e.ClipRectangle.Bottom - _textArea.Top) - _charSize.Height) / _charSize.Height);
        for (int row = firstRow; row <= Math.Min(this._visibleRows - 1, lastRow); row++)
        {
            if (_displayRows[row].VirtualLine == null)
            {
                int line = _displayRows[row].Line;
                if (line >= _document.LineCount)
                {
                    break;
                }
                if (line != -1)
                {
                    PaintLine(e.Graphics, line, row, firstColumn, backgroundBrushes);
                }
            }
            else
            {
                Rectangle rectangle = new Rectangle(_textArea.Left, (int)RowToY(row), _textArea.Width, (int)_charSize.Height);
                _displayRows[row].VirtualLine.Draw(e.Graphics, this, row, ScrollColumn, _displayRows[row].Line, rectangle, _charSize);
            }
        }
        base.OnPaint(e);
    }

    public void LoadFile(string text)
    {
        _document.LoadFromFile(text);
        OnTextReplaced();
    }

    public void SaveToFile(string text)
    {
        StreamWriter streamWriter = new StreamWriter(text, append: false);
        for (int line = 0; line < Document.LineCount; line++)
        {
            if (line < Document.LineCount - 1)
            {
                streamWriter.WriteLine(_document.GetLineText(line));
            }
            else
            {
                streamWriter.Write(_document.GetLineText(line));
            }
        }
        streamWriter.Dispose();
    }

    private void OnScrollBarChanged(object sender, EventArgs e)
    {
        Redraw();
        RebuildDisplayRows();
        UpdateCaret();
        IScrollBar scrollBar = sender as IScrollBar;
        OnScroll(new ScrollEventArgs(ScrollEventType.ThumbTrack, scrollBar.Value, (scrollBar != _horizontalScrollBar) ? ScrollOrientation.VerticalScroll : ScrollOrientation.HorizontalScroll));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_layoutSuspended)
        {
            UpdateLayout();
        }
        else
        {
            _resizePending = true;
        }
        Redraw();
    }

    private void CopyScrollState(IScrollBar scrollBar, IScrollBar scrollBar2)
    {
        scrollBar2.Limits = scrollBar.Limits;
        scrollBar2.Value = scrollBar.Value;
        scrollBar2.Visible = scrollBar.Visible;
    }

    public void ReplaceScrollBars(IScrollBar scrollBar, IScrollBar scrollBar2)
    {
        CopyScrollState(_verticalScrollBar, scrollBar);
        CopyScrollState(_horizontalScrollBar, scrollBar2);
        _verticalScrollBar?.Dispose();
        _horizontalScrollBar?.Dispose();
        _verticalScrollBar.ValueChanged -= OnScrollBarChanged;
        _horizontalScrollBar.ValueChanged -= OnScrollBarChanged;
        _verticalScrollBar = scrollBar;
        _horizontalScrollBar = scrollBar2;
        scrollBar.Register(this, DockStyle.Right);
        scrollBar2.Register(this, DockStyle.Bottom);
        _verticalScrollBar.ValueChanged += OnScrollBarChanged;
        _horizontalScrollBar.ValueChanged += OnScrollBarChanged;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        IScrollBar scrollBar;
        int step;
        if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift)
        {
            scrollBar = _horizontalScrollBar;
            step = _horizontalWheelStep;
        }
        else
        {
            scrollBar = _verticalScrollBar;
            step = _verticalWheelStep;
        }
        if (e.Delta > 0)
        {
            step = -step;
        }
        if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
        {
            step *= _ctrlWheelMultiplier;
        }
        ScrollLimits limits = scrollBar.Limits;
        int maxValue = (NativeScrollSemantics ? Math.Max(scrollBar.Maximum - limits.LargeChange + 1, 0) : Math.Max(scrollBar.Maximum, 0));
        int newValue = scrollBar.Value + step;
        if (newValue > maxValue)
        {
            newValue = maxValue;
        }
        if (newValue < limits.Minimum)
        {
            newValue = limits.Minimum;
        }
        scrollBar.Value = newValue;
        Redraw();
        base.OnMouseWheel(e);
    }

    protected virtual void OnTextReplaced()
    {
        _maxLineWidth = -1;
        UpdateLayout();
        Redraw();
    }

    private int LineToRow(int line)
    {
        for (int row = 0; row < _displayRows.Length; row++)
        {
            if (_displayRows[row].Line == line && _displayRows[row].VirtualLine == null)
            {
                return row;
            }
        }
        return -1;
    }

    public int GetPreviousRealLine(int line)
    {
        int row = LineToRow(line);
        while (--row >= 0)
        {
            if (_displayRows[row].VirtualLine == null)
            {
                return _displayRows[row].Line;
            }
        }
        return _previousRealLine;
    }

    public int GetNextRealLine(int line)
    {
        int row = LineToRow(line);
        while (++row < this._visibleRows)
        {
            if (_displayRows[row].VirtualLine == null)
            {
                return _displayRows[row].Line;
            }
        }
        return _nextRealLine;
    }

    private int RowToLine(int row, out int remainingRows, out int previousLine)
    {
        int line = 0;
        int rowIndex = 0;
        int previousVisibleLine = 0;
        while (true)
        {
            if (rowIndex == row)
            {
                remainingRows = -1;
                previousLine = previousVisibleLine;
                return line;
            }
            if (rowIndex > row)
            {
                break;
            }
            previousVisibleLine = line;
            int nextSpecialLine = _virtualLines.GetNextSpecialLine(Math.Min(line, 0));
            if (nextSpecialLine == -1)
            {
                nextSpecialLine = _document.LineCount;
            }
            int lineSpan = nextSpecialLine - line;
            if (lineSpan > 0)
            {
                int step = Math.Min(lineSpan, row - rowIndex);
                line += step;
                rowIndex += step;
                previousVisibleLine = line - 1;
            }
            else
            {
                line += _virtualLines.GetHiddenLineCount(line);
                rowIndex += _virtualLines.GetVirtualRowCount(line);
                line++;
                rowIndex++;
            }
        }
        remainingRows = rowIndex - row;
        previousLine = previousVisibleLine;
        return previousVisibleLine;
    }

    private int LineToRowIndex(int line)
    {
        int currentLine = 0;
        int rowIndex = 0;
        while (currentLine < line)
        {
            int nextSpecialLine = _virtualLines.GetNextSpecialLine(currentLine);
            if (nextSpecialLine == -1)
            {
                nextSpecialLine = _document.LineCount;
            }
            int lineSpan = nextSpecialLine - currentLine;
            if (lineSpan > 0)
            {
                int step = Math.Min(lineSpan, line - currentLine);
                currentLine += step;
                rowIndex += step;
            }
            else
            {
                currentLine += _virtualLines.GetHiddenLineCount(currentLine);
                rowIndex += _virtualLines.GetVirtualRowCount(currentLine);
                currentLine++;
                rowIndex++;
            }
        }
        return rowIndex;
    }

    internal void RebuildDisplayRows()
    {
        if (_displayRows == null || _displayRows.Length != _visibleRows)
        {
            _displayRows = new DisplayRow[_visibleRows];
        }
        int scrollLine = ScrollLine;
        if (_virtualLines == null)
        {
            _firstVisibleLine = scrollLine;
            _lastVisibleLine = scrollLine + _visibleRows;
            for (int row = 0; row < _visibleRows; row++)
            {
                _displayRows[row].Line = scrollLine + row;
                _displayRows[row].VirtualLine = null;
                _displayRows[row].Decorator = null;
            }
            _previousRealLine = ((scrollLine == 0) ? scrollLine : (scrollLine - 1));
            _nextRealLine = scrollLine + _visibleRows;
            return;
        }
        int line = RowToLine(scrollLine, out var remainingRows, out _previousRealLine);
        int displayRowIndex = 0;
        List<IVirtualLine> list = new List<IVirtualLine>();
        if (remainingRows != -1)
        {
            _virtualLines.GetVirtualLines(line, list);
            for (int virtualLineIndex = list.Count - remainingRows; virtualLineIndex < list.Count; virtualLineIndex++)
            {
                _displayRows[displayRowIndex].Line = line;
                _displayRows[displayRowIndex].VirtualLine = list[virtualLineIndex];
                _displayRows[displayRowIndex].Decorator = null;
                displayRowIndex++;
            }
            line += 1 + _virtualLines.GetHiddenLineCount(line);
        }
        _firstVisibleLine = line;
        list.Clear();
        int previousLine = line;
        while (displayRowIndex < _visibleRows)
        {
            _displayRows[displayRowIndex].VirtualLine = null;
            _displayRows[displayRowIndex].Line = line;
            _displayRows[displayRowIndex].Decorator = _virtualLines.GetDecorator(line);
            _virtualLines.GetVirtualLines(line, list);
            if (list.Count > 0)
            {
                for (int virtualLine = 0; virtualLine < list.Count; virtualLine++)
                {
                    if (++displayRowIndex >= _visibleRows)
                    {
                        break;
                    }
                    _displayRows[displayRowIndex].VirtualLine = list[virtualLine];
                    _displayRows[displayRowIndex].Line = line;
                    _displayRows[displayRowIndex].Decorator = null;
                }
                list.Clear();
            }
            previousLine = line;
            line += 1 + _virtualLines.GetHiddenLineCount(line);
            displayRowIndex++;
            if (line >= _document.LineCount)
            {
                for (; displayRowIndex < _visibleRows; displayRowIndex++)
                {
                    _displayRows[displayRowIndex].VirtualLine = null;
                    _displayRows[displayRowIndex].Line = -1;
                    _displayRows[displayRowIndex].Decorator = null;
                }
            }
        }
        _nextRealLine = line;
        _lastVisibleLine = previousLine;
    }

    protected virtual void OnSelectionChanged(TextDocument.TextPosition previousCaret, TextDocument.TextPosition currentCaret, bool hasSelection, SelectionChangeSource selectionChangeSource)
    {
        if (SelectionChanged != null)
        {
            SelectionChanged(this, previousCaret, currentCaret, hasSelection);
        }
    }

    protected virtual void OnCtrlTab(bool shift)
    {
        if (CtrlTabPressed != null)
        {
            CtrlTabPressed(this, shift);
        }
    }

    private Rectangle InflateFlashRect(Rectangle flashRect, Rectangle clientBounds, int step, int totalSteps)
    {
        int verticalInset = Math.Max(flashRect.Top - clientBounds.Top, clientBounds.Bottom - flashRect.Bottom);
        int horizontalInset = Math.Min(flashRect.Left - clientBounds.Left, clientBounds.Right - flashRect.Right);
        horizontalInset = (verticalInset = Math.Min(horizontalInset, verticalInset));
        horizontalInset = horizontalInset * step / totalSteps;
        verticalInset = verticalInset * step / totalSteps;
        return new Rectangle(flashRect.Left - horizontalInset, flashRect.Top - verticalInset, flashRect.Width + 2 * horizontalInset, flashRect.Height + 2 * verticalInset);
    }

    private void PaintFlash(Graphics graphics)
    {
        graphics.DrawRectangle(Pens.Black, _flashRect);
        for (int step = 0; step < 3; step++)
        {
            int inset = _flashStep - step * 2;
            if (inset >= 0 && inset <= 10)
            {
                graphics.DrawRectangle(_flashPen, InflateFlashRect(_flashRect, ClientRectangle, inset, 10));
            }
        }
    }

    public void StartFlash(int interval)
    {
        _flashTimer.Interval = (int)Math.Ceiling((double)interval / 16.0);
        _flashStep = 16;
        _flashTimer.Enabled = true;
        Bitmap bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, ClientRectangle);
        _flashBitmap = bitmap;
        if (!TryGetCaretColumn(CaretPosition, out var caretColumn))
        {
            caretColumn = 0;
        }
        Point point = Point.Round(PositionToPoint(caretColumn, CaretPosition.Line));
        TextDocument.TextPosition textPosition = GetMovedPosition(CaretMove.WordRight);
        caretColumn = ((textPosition.Line != CaretPosition.Line) ? _document.IndexToColumn(textPosition.Line, int.MaxValue, TabSize) : _document.IndexToColumn(textPosition.Line, textPosition.Column, TabSize));
        int endX = (int)ColumnToX(caretColumn);
        _flashRect = new Rectangle(point.X, point.Y, endX - point.X, (int)_charSize.Height);
    }

    private void OnFlashTick(object sender, EventArgs e)
    {
        if (--_flashStep == 0)
        {
            _flashTimer.Enabled = false;
            _flashBitmap = null;
        }
        Redraw();
    }

    public void CenterOnCaret()
    {
        EnsureVisible(CaretPosition, center: true);
    }

    public void MoveCaretToEnd(bool toStart)
    {
        TextDocument.TextPosition textPosition = new TextDocument.TextPosition
        {
            Line = _document.LineCount - 1
        };
        if (toStart)
        {
            textPosition.Column = 0;
        }
        else
        {
            textPosition.Column = _document.GetLineText(textPosition.Line).Length;
        }
        if (_hasSelection)
        {
            SetSelection(select: false, default);
        }
        SetCaret(textPosition);
        EnsureCaretVisible(textPosition);
    }

    public virtual void SetDocument(EditableTextDocument editableTextDocument)
    {
        _document = editableTextDocument;
        OnTextReplaced();
    }

    public void SetSelectionEnd(TextDocument.TextPosition textPosition)
    {
        _selectionEnd = textPosition;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateLayout();
        UpdateCaret();
        if (_caret != null && _caret.IsEnabled)
        {
            _caret.Disable();
            if (!_caretHidden)
            {
                _caret.Enable();
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _lastMouseButton = MouseButtons.None;
    }

    public TextDocument.TextPosition GetSelectionBoundary(bool reverse)
    {
        if (!_hasSelection)
        {
            return CaretPosition;
        }
        bool forward = SelectionEnd > SelectionStart;
        if (reverse)
        {
            forward = !forward;
        }
        if (!forward)
        {
            return SelectionEnd;
        }
        return SelectionStart;
    }

    public TextDocument.TextPosition Find(TextDocument.TextPosition textPosition, string searchText, bool searchBackward, bool ignoreCase, bool wrapAround, FindSelectMode findSelectMode = FindSelectMode.None)
    {
        if (string.IsNullOrEmpty(searchText))
        {
            return new TextDocument.TextPosition(-1, -1);
        }
        return Find(textPosition, TextMatcher.Literal(searchText, ignoreCase), searchBackward, wrapAround, findSelectMode, TextDocument.TextPosition.Invalid, TextDocument.TextPosition.Invalid);
    }

    /// <summary>
    /// Finds the next match of <paramref name="matcher"/> (plain text, whole word or regular expression), line by line.
    /// A valid <paramref name="scopeStart"/> and <paramref name="scopeEnd"/> limit the search to that range (the search
    /// wraps around inside it). Returns where the match begins, or an invalid position when there is none.
    /// </summary>
    public TextDocument.TextPosition Find(TextDocument.TextPosition textPosition, TextMatcher matcher, bool searchBackward, bool wrapAround, FindSelectMode findSelectMode, TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd)
    {
        if (_document == null || _document.LineCount == 0 || matcher == null)
        {
            return new TextDocument.TextPosition(-1, -1);
        }
        bool scoped = scopeStart.IsValid && scopeEnd.IsValid;
        int firstLine = scoped ? Math.Max(0, scopeStart.Line) : 0;
        int lastLine = scoped ? Math.Min(scopeEnd.Line, _document.LineCount - 1) : (_document.LineCount - 1);
        int direction = ((!searchBackward) ? 1 : (-1));
        int startColumn = textPosition.Column;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int line = textPosition.Line; line >= firstLine && line <= lastLine; line += direction)
            {
                string text = _document.GetLineText(line);
                if (text == null)
                {
                    continue;
                }
                // The part of the line that may hold a match: the whole line, or what the scope leaves of it.
                int lower = (scoped && line == firstLine) ? scopeStart.Column : 0;
                int upper = (scoped && line == lastLine) ? Math.Min(scopeEnd.Column, text.Length) : text.Length;
                int from;
                int to;
                if (searchBackward)
                {
                    from = lower;
                    to = (startColumn == -1) ? upper : Math.Min(startColumn, upper);
                }
                else
                {
                    from = (startColumn == -1) ? lower : Math.Max(startColumn, lower);
                    to = upper;
                }
                if (matcher.Match(text, from, to, searchBackward, allowEmpty: false, out int matchIndex, out int matchLength))
                {
                    TextDocument.TextPosition position = new TextDocument.TextPosition
                    {
                        Column = matchIndex,
                        Line = line
                    };
                    if (findSelectMode != FindSelectMode.None)
                    {
                        if (TerminalMode)
                        {
                            SetSelectionEnd(position);
                            SetSelectionCore(select: true, new TextDocument.TextPosition
                            {
                                Line = line,
                                Column = matchIndex + matchLength
                            }, SelectionChangeSource.FindResult);
                            EnsureCaretVisible(position);
                        }
                        else
                        {
                            SetCaretCore(position, SelectionChangeSource.FindResult);
                            SetSelectionCore(select: true, new TextDocument.TextPosition
                            {
                                Line = line,
                                Column = matchIndex + matchLength
                            }, SelectionChangeSource.FindResult);
                        }
                        int matchEnd = matchIndex + matchLength;
                        if (matchEnd <= _visibleColumns)
                        {
                            ScrollColumn = 0;
                        }
                        else if (text.Length - matchIndex < _visibleColumns && text.Length > _visibleColumns)
                        {
                            ScrollColumn = text.Length - _visibleColumns;
                        }
                        else if (matchLength < _visibleColumns)
                        {
                            int centerColumn = (matchIndex + matchEnd) / 2;
                            ScrollColumn = centerColumn - _visibleColumns / 2;
                        }
                        else
                        {
                            ScrollColumn = matchIndex;
                        }
                    }
                    return position;
                }
                startColumn = -1;
            }
            if (!((pass == 0) & wrapAround))
            {
                break;
            }
            textPosition.Line = (searchBackward ? lastLine : firstLine);
            startColumn = -1;
        }
        if (findSelectMode == FindSelectMode.SelectOrClear)
        {
            if (TerminalMode)
            {
                SetSelection(select: false, TextDocument.TextPosition.Invalid);
            }
            else
            {
                SetCaretCore(new TextDocument.TextPosition
                {
                    Column = 0,
                    Line = _document.LineCount
                }, SelectionChangeSource.FindResult);
                SetSelectionCore(select: false, CaretPosition, SelectionChangeSource.FindResult);
            }
        }
        return new TextDocument.TextPosition(-1, -1);
    }

    // Marks every match of the search bar's query in the visible lines (not saved anywhere: it follows the text).
    private TextMatcher _matchHighlighter;

    private readonly Brush _matchHighlightBrush = new SolidBrush(Color.FromArgb(95, 255, 190, 0));

    /// <summary>Highlights all matches of <paramref name="matcher"/> in the text; null removes the highlight.</summary>
    public void SetMatchHighlight(TextMatcher matcher)
    {
        _matchHighlighter = matcher;
        Redraw();
    }

    private void PaintMatches(Graphics graphics)
    {
        TextMatcher matcher = _matchHighlighter;
        if (matcher == null || TerminalMode)
        {
            return;
        }
        try
        {
            for (int row = 0; row < _visibleRows; row++)
            {
                if (_displayRows[row].VirtualLine != null)
                {
                    continue;
                }
                int line = _displayRows[row].Line;
                if (line < 0 || line >= _document.LineCount)
                {
                    continue;
                }
                string text = _document.GetLineText(line);
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }
                int y = Round(RowToY(row));
                int from = 0;
                // A line of a minified file may hold thousands of matches; the first ones are enough to show.
                for (int shown = 0; shown < 500 && from <= text.Length && matcher.Match(text, from, text.Length, backward: false, allowEmpty: false, out int index, out int length); shown++)
                {
                    int startX = Round(ColumnToX(_document.IndexToColumn(line, index, TabSize)));
                    int endX = Round(ColumnToX(_document.IndexToColumn(line, index + length, TabSize)));
                    if (endX > startX)
                    {
                        graphics.FillRectangle(_matchHighlightBrush, startX, y, endX - startX, _charSize.Height);
                    }
                    from = index + length;
                }
            }
        }
        catch (Exception)
        {
            // A painting error must not break the editor (a regular expression that times out, say): stop highlighting.
            _matchHighlighter = null;
        }
    }

    public TextDocument.TextPosition OffsetToPosition(int offset)
    {
        return Document.OffsetToPosition(offset);
    }
}
