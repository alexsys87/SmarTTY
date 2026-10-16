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
            scrollBar.ValueChanged += (object obj, EventArgs e) =>
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

        public LineRange(int value2, int value3)
        {
            First = value2;
            Last = value3;
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

    public struct ColorSpan(int value2, int value3, Brush brush)
    {
        public int Start = value2;

        public int End = value3;

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

        public void Append(Graphics graphics, Font font, string text, float value2, float value3, int value4, Brush brush, Brush brush2, StringFormat stringFormat, SizeF sizeF)
        {
            if (this._lastIndex != value4 || this._y != value3 || this._foreground != brush || this._stringFormat != stringFormat || _background != brush2)
            {
                this.FlushPending(graphics);
            }
            if (this._text == null)
            {
                this._text = text;
                this._x = value2;
            }
            else
            {
                this._text += text;
            }
            this._charSize = sizeF;
            this._lastIndex = value4 + 1;
            this._y = value3;
            this._foreground = brush;
            this._font = font;
            this._stringFormat = stringFormat;
            _background = brush2;
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

        public Highlight(TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, HighlightStyle highlightStyle)
        {
            this.Start = textPosition;
            this.End = textPosition2;
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
        void Draw(Graphics graphics, ReadonlyTextBrowser readonlyTextBrowser, int value2, int value3, int value4, Rectangle rectangle, SizeF sizeF);
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

        int GetNextSpecialLine(int value2);

        int GetVirtualRowCount(int value2);

        void GetVirtualLines(int value2, List<IVirtualLine> list);

        int GetHiddenLineCount(int value2);

        ILineDecorator GetDecorator(int value2);

        bool OnCaretMoved(int value2, int value3);
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
            int num = 0;
            for (int num2 = 0; num2 < _providers.Count; num2++)
            {
                num += _providers[num2].GetTotalVirtualRows();
            }
            return num;
        }

        public int GetTotalHiddenLines()
        {
            int num = 0;
            for (int num2 = 0; num2 < _providers.Count; num2++)
            {
                num += _providers[num2].GetTotalHiddenLines();
            }
            return num;
        }

        public int GetNextSpecialLine(int value2)
        {
            int num = int.MaxValue;
            for (int num2 = 0; num2 < this._providers.Count; num2++)
            {
                int num3 = this._providers[num2].GetNextSpecialLine(value2);
                if (num3 < num)
                {
                    num = num3;
                }
            }
            return num;
        }

        public int GetVirtualRowCount(int value2)
        {
            int num = 0;
            for (int num2 = 0; num2 < this._providers.Count; num2++)
            {
                num += this._providers[num2].GetVirtualRowCount(value2);
            }
            return num;
        }

        public void GetVirtualLines(int value2, List<IVirtualLine> list)
        {
            for (int num = 0; num < this._providers.Count; num++)
            {
                this._providers[num].GetVirtualLines(value2, list);
            }
        }

        public int GetHiddenLineCount(int value2)
        {
            int num = 0;
            for (int num2 = 0; num2 < this._providers.Count; num2++)
            {
                num += this._providers[num2].GetHiddenLineCount(value2);
            }
            return num;
        }

        public ILineDecorator GetDecorator(int value2)
        {
            for (int num = 0; num < this._providers.Count; num++)
            {
                ILineDecorator lineDecorator = this._providers[num].GetDecorator(value2);
                if (lineDecorator != null)
                {
                    return lineDecorator;
                }
            }
            return null;
        }

        public bool OnCaretMoved(int value2, int value3)
        {
            bool result = false;
            foreach (IVirtualLineProvider item in this._providers)
            {
                if (item.OnCaretMoved(value2, value3))
                {
                    result = true;
                }
            }
            return result;
        }
    }

    public delegate void SelectionChangedHandler(ReadonlyTextBrowser readonlyTextBrowser, TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, bool flag);

    public enum SelectionChangeSource
    {
        FindResult,
        Programmatic
    }

    public delegate void CtrlTabHandler(ReadonlyTextBrowser readonlyTextBrowser, bool flag);

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

    public Brush Brush2 = Brushes.DarkGray;

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
            bool flag = (_caret.UnderlineStyle = value);
            _terminalMode = flag;
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

    public override Font Font
    {
        get
        {
            return base.Font;
        }
        set
        {
            base.Font = value;
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
        int num = LineToRow(textPosition.Line);
        if (num == -1)
        {
            return textPosition;
        }
        if (_displayRows[num].VirtualLine != null)
        {
            textPosition.Line = _displayRows[num].Line;
        }
        if (_displayRows[num].Decorator != null)
        {
            textPosition = _displayRows[num].Decorator.MapPosition(_document.GetLineText(textPosition.Line), textPosition);
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
            TextDocument.TextPosition textPosition2 = _caretPosition;
            _caretPosition = NormalizePosition(textPosition);
            OnSelectionChanged(textPosition2, _caretPosition, _hasSelection, selectionChangeSource);
            EnsureCaretVisible(_caretPosition);
            UpdateCaret();
            _forceCaretUpdate = false;
        }
    }

    public void SetSelection(bool flag, TextDocument.TextPosition textPosition)
    {
        SetSelectionCore(flag, textPosition, SelectionChangeSource.Programmatic);
    }

    protected void SetSelectionCore(bool flag2, TextDocument.TextPosition textPosition, SelectionChangeSource selectionChangeSource)
    {
        bool flag = false;
        TextDocument.TextPosition textPosition2 = _caretPosition;
        if (_hasSelection != flag2)
        {
            flag = true;
            _hasSelection = flag2;
        }
        if (_selectionStart != textPosition)
        {
            _selectionStart = textPosition;
            OnSelectionStartChanged();
            flag = true;
        }
        if (flag)
        {
            Redraw();
            OnSelectionChanged(textPosition2, _caretPosition, _hasSelection, selectionChangeSource);
        }
    }

    protected void SetCaretColumn(int value2)
    {
        TextDocument.TextPosition textPosition = _caretPosition;
        _caretPosition.Column = value2;
        _caretPosition = NormalizePosition(_caretPosition);
        EnsureCaretVisible(_caretPosition);
        OnSelectionChanged(textPosition, _caretPosition, _hasSelection, SelectionChangeSource.Programmatic);
        UpdateCaret();
    }

    private int Round(float value2)
    {
        return (int)Math.Round(value2);
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

    protected void UpdateMaxLineWidth(int value2)
    {
        if (value2 == -1)
        {
            _maxLineWidth = -1;
        }
        else if (_maxLineWidth != -1)
        {
            int num = _document.IndexToColumn(value2, _document.GetLineText(value2).Length - 1, TabSize);
            if (num > _maxLineWidth)
            {
                _maxLineWidth = num;
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
            int num = 0;
            if (!_hideMargins && _margins.Count > 0)
            {
                if (graphics == null)
                {
                    graphics = Graphics.FromHwnd(Handle);
                }
                for (int num2 = 0; num2 < _margins.Count; num2++)
                {
                    _margins[num2].Left = num;
                    _margins[num2].Right = _margins[num2].Left + _margins[num2].Margin.MeasureWidth(this, graphics, _document.LineCount);
                    num = _margins[num2].Right;
                }
            }
            int num3 = (_verticalScrollBar.Visible ? _verticalScrollBar.Width : 0);
            int num4 = (_horizontalScrollBar.Visible ? _horizontalScrollBar.Height : 0);
            int num5 = ((!NoLeftPadding) ? _leftPadding : 0);
            if (_singleLine)
            {
                int num6 = (int)((float)(ClientRectangle.Height - num4) - _charSize.Height) / 2;
                _textArea = new Rectangle(ClientRectangle.Left + num + num5, ClientRectangle.Top + num6, ClientRectangle.Width - (num3 + num + num5), (int)_charSize.Height);
            }
            else
            {
                _textArea = new Rectangle(ClientRectangle.Left + num + num5, ClientRectangle.Top, ClientRectangle.Width - (num3 + num + num5), ClientRectangle.Height - num4);
            }
            int num7 = _visibleRows;
            int num8 = _visibleColumns;
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
            if (_visibleRows != num7 || _visibleColumns != num8)
            {
                AddRef();
            }
            int num9 = _document.LineCount;
            if (_virtualLines != null)
            {
                num9 += _virtualLines.GetTotalVirtualRows() - _virtualLines.GetTotalHiddenLines();
            }
            if (num9 < 0)
            {
                num9 = 0;
            }
            if (_singleLine && num9 > 1)
            {
                num9 = 1;
            }
            if (NativeScrollSemantics)
            {
                _verticalScrollBar.Limits = new ScrollLimits
                {
                    Maximum = Math.Max(num9, 0),
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
                    Maximum = Math.Max(num9 - _visibleRows + 1, 0),
                    LargeChange = _visibleRows
                };
                _horizontalScrollBar.Limits = new ScrollLimits
                {
                    Maximum = Math.Max(_maxLineWidth - _visibleColumns + 1, 0),
                    LargeChange = _visibleColumns
                };
            }
            int num10 = _maxLineWidth - _visibleColumns + 2;
            if (num10 < 0)
            {
                num10 = 0;
            }
            ScrollColumn = Math.Min(ScrollColumn, num10);
            int num11 = _document.LineCount - _visibleRows + 2;
            if (num11 < 0)
            {
                num11 = 0;
            }
            ScrollLine = Math.Min(ScrollLine, num11);
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
        if (!TryGetCaretColumn(CaretPosition, out var num))
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
        Point point = Point.Round(PositionToPoint(num, CaretPosition.Line));
        point.Offset(dx, 0);
        _caret.SetPosition(point);
        if (!_caretHidden)
        {
            _caret.Enable();
        }
    }

    protected bool TryGetCaretColumn(TextDocument.TextPosition textPosition, out int value2)
    {
        value2 = 0;
        if (textPosition.Line < _firstVisibleLine || textPosition.Line > _lastVisibleLine)
        {
            return false;
        }
        value2 = _document.IndexToColumn(textPosition.Line, textPosition.Column, TabSize);
        if (value2 < ScrollColumn || value2 >= ScrollColumn + _visibleColumns)
        {
            return false;
        }
        return true;
    }

    public float ColumnToX(int value2)
    {
        int num = value2 - ScrollColumn;
        if (num < 0)
        {
            num = 0;
        }
        if (num > _visibleColumns)
        {
            num = _visibleColumns;
        }
        return (float)_textArea.Left + (float)num * _charSize.Width;
    }

    private float RowToY(int value2)
    {
        return (float)_textArea.Top + (float)value2 * _charSize.Height;
    }

    private float LineToY(int value2)
    {
        int num = -1;
        for (int num2 = 0; num2 < _displayRows.Length; num2++)
        {
            if (_displayRows[num2].Line == value2)
            {
                num = num2;
                break;
            }
        }
        if (num == -1)
        {
            return -1f;
        }
        if (num < 0)
        {
            num = 0;
        }
        if (num >= this._visibleRows)
        {
            num = this._visibleRows - 1;
        }
        return RowToY(num);
    }

    protected PointF PositionToPoint(int value2, int value3)
    {
        return new PointF(ColumnToX(value2), LineToY(value3));
    }

    private int YToRow(float value2)
    {
        value2 -= (float)_textArea.Top;
        if (value2 < 0f)
        {
            value2 = 0f;
        }
        if (value2 > (float)_textArea.Height)
        {
            value2 = _textArea.Height;
        }
        int num = (int)(value2 / _charSize.Height);
        if (num >= this._visibleRows && this._visibleRows != 0)
        {
            num = this._visibleRows - 1;
        }
        return num;
    }

    protected int YToLine(float value2)
    {
        int num = YToRow(value2);
        if (_displayRows[num].VirtualLine != null)
        {
            return -1;
        }
        return _displayRows[num].Line;
    }

    private int XToColumn(float value2)
    {
        value2 -= (float)_textArea.Left;
        if (value2 < 0f)
        {
            value2 = 0f;
        }
        return ScrollColumn + (int)((value2 + _charSize.Width / 2f) / _charSize.Width);
    }

    protected TextDocument.TextPosition HitTest(Point point, bool flag2 = false)
    {
        int num = YToLine(point.Y);
        if (num == -1)
        {
            return new TextDocument.TextPosition(-1, -1);
        }
        TextDocument.TextPosition result = new TextDocument.TextPosition(_document.ColumnToIndex(num, XToColumn(point.X), TabSize, out var flag), num);
        if (result.Line >= _document.LineCount || (!flag2 & flag))
        {
            return new TextDocument.TextPosition(-1, -1);
        }
        int length = _document.GetLineText(result.Line).Length;
        if (result.Column > length)
        {
            if (!flag2)
            {
                return new TextDocument.TextPosition(-1, -1);
            }
            result.Column = length;
        }
        return result;
    }

    private void MoveCaretToPoint(Point point)
    {
        TextDocument.TextPosition textPosition = HitTest(point, flag2: true);
        if (textPosition.IsValid)
        {
            SetCaret(textPosition);
        }
    }

    public virtual string GetIndentation(int value2)
    {
        string text = Document.GetLineText(value2);
        int num;
        for (num = 0; num < text.Length && (text[num] == ' ' || text[num] == '\t'); num++)
        {
        }
        return text.Substring(0, num);
    }

    public string GetLineOrNull(int value2)
    {
        if (value2 >= _document.LineCount)
        {
            return null;
        }
        return _document.GetLineText(value2);
    }

    public int ColumnToIndex(int value2, int value3)
    {
        return _document.ColumnToIndex(value2, value3, TabSize);
    }

    public int IndexToColumn(int value2, int value3)
    {
        return _document.IndexToColumn(value2, value3, TabSize);
    }

    private bool EnsureCaretVisible(TextDocument.TextPosition textPosition)
    {
        return EnsureVisible(textPosition, flag3: false);
    }

    private bool EnsureVisible(TextDocument.TextPosition textPosition, bool flag3)
    {
        int num = _firstVisibleLine;
        int num2 = _lastVisibleLine;
        int num3 = ScrollColumn;
        int num4 = num3 + _visibleColumns - 1;
        bool flag = true;
        if (!flag3)
        {
            if (textPosition.Line < num)
            {
                TopLine = LineToRowIndex(textPosition.Line);
            }
            else if (textPosition.Line > num2 || (_allowPartialRows && textPosition.Line == num2 && LineToRow(textPosition.Line) >= this._visibleRows - 1))
            {
                TopLine = LineToRowIndex(textPosition.Line) - this._visibleRows + 2;
            }
            else
            {
                flag = false;
            }
        }
        else
        {
            int num5 = LineToRowIndex(textPosition.Line) - this._visibleRows / 2;
            if (num5 < 0)
            {
                num5 = 0;
            }
            TopLine = num5;
            flag = true;
        }
        bool flag2 = true;
        if (!_terminalMode)
        {
            int num6 = _document.IndexToColumn(textPosition.Line, textPosition.Column, TabSize);
            if (num6 < num3)
            {
                ScrollColumn = num6;
            }
            else if (num6 >= num4)
            {
                ScrollColumn = num6 - _visibleColumns + 2;
            }
            else
            {
                flag2 = false;
            }
        }
        return flag | flag2;
    }

    protected virtual bool OnTripleClick(MouseEventArgs e)
    {
        return false;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        bool flag = false;
        if (_hoverTarget?.ClickAction != null && e.Button == MouseButtons.Left)
        {
            _hoverTarget.ClickAction();
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            bool flag2 = (DateTime.Now - _lastDoubleClickTime).TotalMilliseconds <= (double)_doubleClickTime;
            if (flag2 && OnTripleClick(e))
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
                        if (flag2)
                        {
                            SetSelection(flag: true, new TextDocument.TextPosition(0, textPosition.Line));
                            SetSelectionEnd(new TextDocument.TextPosition(GetLineOrNull(textPosition.Line).Length, textPosition.Line));
                            InvalidateLine(textPosition.Line);
                        }
                        else
                        {
                            TextDocument.TextPosition textPosition2 = ComputeMovedPosition(CaretMove.WordLeft, Control.ModifierKeys, textPosition);
                            TextDocument.TextPosition textPosition3 = ComputeMovedPosition(CaretMove.WordRight, Control.ModifierKeys, textPosition);
                            if (textPosition2.Line == textPosition3.Line || AllowCrossLineWordSelection)
                            {
                                SetSelection(flag: true, textPosition2);
                                SetSelectionEnd(textPosition3);
                                InvalidateLine(textPosition2.Line);
                            }
                        }
                    }
                }
                else
                {
                    TextDocument.TextPosition textPosition4 = GetMovedPosition(CaretMove.WordLeft);
                    TextDocument.TextPosition textPosition5 = GetMovedPosition(CaretMove.WordRight);
                    if (textPosition4.Line == CaretPosition.Line && textPosition5.Line == CaretPosition.Line)
                    {
                        bool flag3 = true;
                        if (_hasSelection && _selectionStart.Line != CaretPosition.Line)
                        {
                            flag3 = false;
                        }
                        _selectionStart = textPosition4;
                        _hasSelection = true;
                        OnSelectionStartChanged();
                        if (!NavigationLocked)
                        {
                            SetCaret(textPosition5);
                        }
                        if (flag3)
                        {
                            InvalidateLine(textPosition4.Line);
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
                bool flag4 = false;
                if (!_hideMargins)
                {
                    foreach (MarginSlot item in _margins)
                    {
                        if (e.X >= item.Left && e.X <= item.Right)
                        {
                            flag4 = item.Margin.OnMouseDown(new Point(e.X - item.Left, e.Y - _textArea.Top), this, ScrollLine, this._visibleRows, _charSize, YToRow(e.Y));
                            if (flag4)
                            {
                                flag = true;
                                break;
                            }
                        }
                    }
                }
                int num = YToLine(e.Location.Y);
                if (!flag4 && num != -1)
                {
                    _hasSelection = true;
                    _selectionStart = new TextDocument.TextPosition(0, num);
                    OnSelectionStartChanged();
                    if (!_terminalMode && !NavigationLocked)
                    {
                        SetCaret(new TextDocument.TextPosition(_document.GetLineText(num).Length, num));
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
            if (flag)
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
            UpdateSelectionFromPoint(flag2: false, point, flag3: false);
        }
        else if (!_navigationLocked)
        {
            MoveCaretToPoint(point);
        }
    }

    protected virtual void AdjustSelectionEnd(ref TextDocument.TextPosition textPosition)
    {
    }

    private void UpdateSelectionFromPoint(bool flag2, Point point, bool flag3)
    {
        TextDocument.TextPosition textPosition = HitTest(point, flag2: true);
        if (!textPosition.IsValid)
        {
            return;
        }
        bool flag = false;
        TextDocument.TextPosition textPosition2 = _caretPosition;
        if (_hasSelection != flag3)
        {
            flag = true;
            _hasSelection = flag3;
        }
        if (flag2)
        {
            AdjustSelectionEnd(ref textPosition);
            if (_selectionStart != textPosition)
            {
                _selectionStart = textPosition;
                flag = true;
                OnSelectionStartChanged();
            }
        }
        else if (_selectionEnd != textPosition)
        {
            _selectionEnd = textPosition;
            flag = true;
        }
        if (flag)
        {
            Redraw();
            OnSelectionChanged(textPosition2, _caretPosition, _hasSelection, SelectionChangeSource.Programmatic);
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
            if (!_hasSelection && (!_terminalMode || HitTest(e.Location, flag2: true).IsValid))
            {
                _selectionStart = CaretPosition;
                _hasSelection = true;
                OnSelectionStartChanged();
            }
            TextDocument.TextPosition textPosition = CaretPosition;
            if (_terminalMode)
            {
                UpdateSelectionFromPoint(flag2: true, e.Location, flag3: true);
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
            int num = YToLine(e.Y);
            if (num != -1)
            {
                _hoverTarget = GetHoverTarget(num, e.Location);
                Cursor = _hoverTarget?.Cursor ?? Cursors.IBeam;
                base.OnMouseMove(e);
                return;
            }
        }
        Cursor = Cursors.Arrow;
        base.OnMouseMove(e);
    }

    protected virtual HoverTarget GetHoverTarget(int value2, Point point)
    {
        return null;
    }

    protected TextDocument.TextPosition GetMovedPosition(CaretMove caretMove, bool flag = false)
    {
        return ComputeMovedPosition(caretMove, Control.ModifierKeys, CaretPosition, flag);
    }

    protected virtual bool IsWordChar(char ch)
    {
        return "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".IndexOf(ch) != -1;
    }

    protected virtual void OnMoveBeyondDocument(ref TextDocument.TextPosition textPosition, int value2)
    {
    }

    protected virtual TextDocument.TextPosition ComputeMovedPosition(CaretMove caretMove, Keys keys, TextDocument.TextPosition textPosition, bool flag = false)
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
                    int num2 = _document.ColumnToIndex(textPosition.Line, _desiredColumn, TabSize);
                    textPosition.Column = Math.Min(num2, _document.GetLineText(textPosition.Line).Length);
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
                    int num3 = _document.ColumnToIndex(textPosition.Line, _desiredColumn, TabSize);
                    textPosition.Column = Math.Min(num3, _document.GetLineText(textPosition.Line).Length);
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
                    string text2 = _document.GetLineText(textPosition.Line);
                    int num;
                    for (num = 0; num < text2.Length && (text2[num] == ' ' || text2[num] == '\t'); num++)
                    {
                    }
                    if (textPosition.Column == num)
                    {
                        textPosition.Column = 0;
                    }
                    else
                    {
                        textPosition.Column = num;
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
                    int num4 = _document.ColumnToIndex(textPosition.Line, _desiredColumn, TabSize);
                    textPosition.Column = Math.Min(num4, _document.GetLineText(textPosition.Line).Length);
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
                    int num5 = _document.ColumnToIndex(textPosition.Line, _desiredColumn, TabSize);
                    textPosition.Column = Math.Min(num5, _document.GetLineText(textPosition.Line).Length);
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

    private bool TryGetNavigationTarget(KeyEventArgs e2, bool flag, out TextDocument.TextPosition textPosition, out CaretDirection caretDirection)
    {
        caretDirection = CaretDirection.Unspecified;
        Keys keyCode = e2.KeyCode;
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
        switch (e2.KeyCode)
        {
            case Keys.Left:
                if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
                {
                    textPosition = GetMovedPosition(CaretMove.WordLeft, flag);
                }
                else
                {
                    textPosition = GetMovedPosition(CaretMove.Left, flag);
                }
                caretDirection = CaretDirection.Backward;
                return true;
            case Keys.Right:
                if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
                {
                    textPosition = GetMovedPosition(CaretMove.WordRight, flag);
                }
                else
                {
                    textPosition = GetMovedPosition(CaretMove.Right, flag);
                }
                caretDirection = CaretDirection.Forward;
                return true;
            case Keys.Up:
                textPosition = GetMovedPosition(CaretMove.Up, flag);
                return true;
            case Keys.Down:
                textPosition = GetMovedPosition(CaretMove.Down, flag);
                return true;
            case Keys.Home:
                textPosition = GetMovedPosition(CaretMove.Home, flag);
                return true;
            case Keys.End:
                textPosition = GetMovedPosition(CaretMove.End, flag);
                return true;
            case Keys.Prior:
                textPosition = GetMovedPosition(CaretMove.PageUp, flag);
                return true;
            case Keys.Next:
                textPosition = GetMovedPosition(CaretMove.PageDown, flag);
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
        bool flag = false;
        bool flag2 = false;
        if (!e.Handled && !_navigationLocked)
        {
            CaretDirection caretDirection;
            if ((Control.ModifierKeys & Keys.Shift) != Keys.Shift)
            {
                if (TryGetNavigationTarget(e, flag: true, out var textPosition, out caretDirection))
                {
                    flag = true;
                    if (_hasSelection)
                    {
                        if (_selectionStart.Line != textPosition.Line)
                        {
                            flag2 = true;
                        }
                        _hasSelection = false;
                        OnSelectionStartChanged();
                    }
                    if (textPosition.Line != CaretPosition.Line)
                    {
                        flag2 = true;
                    }
                    if (!NavigationLocked)
                    {
                        SetCaret(textPosition, caretDirection);
                    }
                    e.Handled = true;
                    if (EnsureCaretVisible(CaretPosition))
                    {
                        flag2 = true;
                    }
                }
            }
            else
            {
                bool flag3 = false;
                if (!_hasSelection)
                {
                    _hasSelection = true;
                    _selectionStart = CaretPosition;
                    flag3 = true;
                    OnSelectionStartChanged();
                }
                if (TryGetNavigationTarget(e, flag: true, out var textPosition2, out caretDirection))
                {
                    flag = true;
                    if (textPosition2.Line != CaretPosition.Line)
                    {
                        flag2 = true;
                    }
                    if (!NavigationLocked)
                    {
                        SetCaret(textPosition2, caretDirection);
                    }
                    e.Handled = true;
                    if (EnsureCaretVisible(CaretPosition))
                    {
                        flag2 = true;
                    }
                }
                else if (flag3)
                {
                    _hasSelection = false;
                    OnSelectionStartChanged();
                }
            }
        }
        if (flag2)
        {
            Redraw();
        }
        else if (flag)
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

    private void PaintSelection(Graphics graphics, TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, HighlightStyle highlightStyle, Brush[,] items)
    {
        if (textPosition == textPosition2)
        {
            return;
        }
        TextDocument.TextPosition textPosition3;
        TextDocument.TextPosition textPosition4;
        if (textPosition < textPosition2)
        {
            textPosition3 = textPosition;
            textPosition4 = textPosition2;
        }
        else
        {
            textPosition3 = textPosition2;
            textPosition4 = textPosition;
        }
        int num = ScrollColumn;
        int num2 = num + _visibleColumns - 1;
        int num3 = -1;
        int num4 = -1;
        for (int num5 = 0; num5 < this._visibleRows; num5++)
        {
            int num6 = _displayRows[num5].Line;
            if (num6 < textPosition3.Line)
            {
                continue;
            }
            if (num6 > textPosition4.Line)
            {
                break;
            }
            string text = _document.GetLineText(num6);
            int num7 = _document.IndexToColumn(num6, text.Length, TabSize);
            int num8 = 0;
            int num9 = num7;
            if (num6 == textPosition3.Line)
            {
                num8 = _document.IndexToColumn(num6, textPosition3.Column, TabSize);
                if (num8 > num7)
                {
                    num8 = num7;
                }
            }
            if (num6 == textPosition4.Line)
            {
                num9 = _document.IndexToColumn(num6, textPosition4.Column, TabSize);
                if (num9 > num7)
                {
                    num9 = num7;
                }
            }
            if (textPosition3.Line == textPosition4.Line && _displayRows[num5].Line == textPosition3.Line && _displayRows[num5].VirtualLine != null)
            {
                break;
            }
            if (highlightStyle.TextBrush != null)
            {
                for (int num10 = Math.Max(num8, num); num10 < Math.Min(num9, num2 + 1); num10++)
                {
                    items[num5, num10 - num] = highlightStyle.TextBrush;
                }
            }
            int num11 = Round(ColumnToX(num8));
            int num12 = Round(ColumnToX(num9));
            if ((num6 != textPosition3.Line || num6 != textPosition4.Line) && num8 == num9)
            {
                num12 = num11 + Round(_charSize.Width / 4f);
            }
            if (_displayRows[num5].VirtualLine != null)
            {
                num11 = num3;
                num12 = num4;
            }
            int num13 = Round(RowToY(num5));
            if (highlightStyle.FillBrush != null)
            {
                graphics.FillRectangle(highlightStyle.FillBrush, num11, num13, num12 - num11, _charSize.Height);
            }
            if (highlightStyle.BorderPen != null)
            {
                if (num6 == textPosition3.Line)
                {
                    graphics.DrawLine(highlightStyle.BorderPen, num11, num13, num12, num13);
                }
                else
                {
                    if (num3 != num11)
                    {
                        graphics.DrawLine(highlightStyle.BorderPen, num11, num13, num3, num13);
                    }
                    if (num4 != num12)
                    {
                        graphics.DrawLine(highlightStyle.BorderPen, num12, num13, num4, num13);
                    }
                }
                if (num6 == textPosition4.Line)
                {
                    graphics.DrawLine(highlightStyle.BorderPen, num11, (float)num13 + _charSize.Height, num12, (float)num13 + _charSize.Height);
                }
                graphics.DrawLine(highlightStyle.BorderPen, num11, num13, num11, (float)num13 + _charSize.Height);
                graphics.DrawLine(highlightStyle.BorderPen, num12, num13, num12, (float)num13 + _charSize.Height);
            }
            num3 = num11;
            num4 = num12;
        }
    }

    protected void InvalidateLine(int value2)
    {
        if (value2 >= _firstVisibleLine && value2 <= _lastVisibleLine)
        {
            int num = LineToRow(value2);
            if (num != -1)
            {
                Invalidate(new Rectangle(0, Round((float)_textArea.Top + (float)num * _charSize.Height), Width, (int)Math.Ceiling(_charSize.Height)));
            }
        }
    }

    protected void InvalidateLines(int value2, int value3)
    {
        if (value2 > value3)
        {
            int num = value2;
            value2 = value3;
            value3 = num;
        }
        if (value2 < _firstVisibleLine)
        {
            value2 = _firstVisibleLine;
        }
        if (value3 > _lastVisibleLine)
        {
            value3 = _lastVisibleLine;
        }
        int num2 = LineToRow(value2);
        int num3 = LineToRow(value3);
        if (num2 != -1 && num3 != -1 && num3 >= num2)
        {
            Invalidate(new Rectangle(Left, Round((float)_textArea.Top + (float)num2 * _charSize.Height), Width, (int)Math.Ceiling((float)(num3 - num2 + 1) * _charSize.Height + 1f)));
        }
    }

    protected virtual ColorSpan[] GetLineColorSpans(int value2)
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

    private void PaintLine(Graphics graphics, int value2, int value3, int value4, Brush[,] items)
    {
        int num = 0;
        int num2 = 0;
        CharacterRange characterRange = default;
        Brush brush = null;
        string text = _document.GetLineText(value2);
        if (_displayRows[value3].Decorator != null)
        {
            text = _displayRows[value3].Decorator.Transform(text, out characterRange, out brush);
        }
        text = TextDocument.SubstringFromColumn(text, value4, TabSize, out num, out num2);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        int num3 = 0;
        if (_groupBinaryDigits && IsBinaryDigit(text[0]))
        {
            string text2 = _document.GetLineText(value2);
            int num4 = num2;
            while (num4 > 0 && IsBinaryDigit(text2[num4]))
            {
                num3++;
                num4--;
            }
        }
        num3 %= 4;
        if (num2 > characterRange.First)
        {
            characterRange.First = num2;
        }
        int num5 = characterRange.First - num2;
        if (characterRange.Length > text.Length - num5)
        {
            characterRange.Length = text.Length - num5;
        }
        int left = _textArea.Left;
        int num6 = Round((float)_textArea.Top + (float)value3 * _charSize.Height);
        int num7 = -1;
        ColorSpan[] array = ((brush == null) ? GetLineColorSpans(value2) : new ColorSpan[1]
        {
            new ColorSpan(0, int.MaxValue, brush)
        });
        if (array == null || array.Length == 0)
        {
            array = _defaultColors;
        }
        int num8 = 0;
        List<KeyValuePair<int, int>> list = null;
        Pen pen = null;
        Brush brush2 = null;
        int num9 = 0;
        int num10 = 0;
        int num11 = num;
        while (num10 < text.Length && num11 < _visibleColumns)
        {
            int num12 = num11 + value4;
            int num13 = num10 + num2;
            num9 = num11;
            Brush brush3 = _defaultColors[0].Foreground;
            while (array[num8].End <= num13)
            {
                if (++num8 >= array.Length)
                {
                    array = _defaultColors;
                    num8 = 0;
                    break;
                }
            }
            bool flag = false;
            if (array[num8].Start <= num13)
            {
                brush3 = array[num8].Foreground;
                if (brush3 == null)
                {
                    brush3 = _defaultColors[0].Foreground;
                }
                brush2 = array[num8].Background;
                flag = array[num8].Underline;
            }
            else
            {
                brush2 = null;
            }
            if (items != null && items[value3, num11] != null)
            {
                brush3 = items[value3, num11];
                brush2 = null;
            }
            if (_showTabs)
            {
                if (text[num10] == '\t')
                {
                    if (num7 == -1)
                    {
                        num7 = num10;
                    }
                }
                else if (num7 != -1)
                {
                    float num14 = (float)num6 + _charSize.Height / 2f;
                    graphics.DrawLine(Pens.DarkGray, (float)left + (float)num7 * _charSize.Width, num14, (float)left + (float)num11 * _charSize.Width, num14);
                    num7 = -1;
                }
            }
            if (text[num10] == '\t')
            {
                num11 += TabSize - num12 % TabSize - 1;
            }
            else
            {
                _textBatch.Append(graphics, Font, text.Substring(num10, 1), (float)left + (float)num11 * _charSize.Width, num6, num10, brush3, brush2, _stringFormat, _charSize);
            }
            if (num10 == characterRange.First && _displayRows[value3].Decorator != null)
            {
                _displayRows[value3].Decorator.DrawDecoration(graphics, _charSize, new Rectangle(left + (int)((float)num11 * _charSize.Width), num6, (int)(_charSize.Width * (float)characterRange.Length), (int)_charSize.Height), characterRange);
            }
            bool flag2 = false;
            if (_groupBinaryDigits)
            {
                num3 = (IsBinaryDigit(text[num10]) ? (num3 + 1) : 0);
                if (num3 >= 4)
                {
                    flag2 = true;
                }
            }
            if (flag)
            {
                flag2 = true;
            }
            if (flag2)
            {
                if (list == null)
                {
                    list = new List<KeyValuePair<int, int>>();
                    pen = new Pen((brush3 as SolidBrush)?.Color ?? Color.Black);
                }
                int num15 = (int)((float)left + (float)num11 * _charSize.Width);
                int value = (int)((float)left + (float)(num11 + 1) * _charSize.Width);
                if (list.Count > 0 && num15 == list[list.Count - 1].Value)
                {
                    num15 = list[list.Count - 1].Key;
                    list.RemoveAt(list.Count - 1);
                }
                list.Add(new KeyValuePair<int, int>(num15, value));
                num3 = 0;
            }
            num10++;
            num11++;
        }
        _textBatch.Flush(graphics);
        if (brush2 != null && FillLineBackground && num9 != _visibleColumns)
        {
            graphics.FillRectangle(brush2, ColumnToX(num9 + 1 + ScrollColumn), LineToY(value2), _charSize.Width * (float)(_visibleColumns - num9), _charSize.Height);
        }
        if (_showTabs && num7 != -1)
        {
            float num16 = (float)num6 + _charSize.Height / 2f;
            graphics.DrawLine(Pens.DarkGray, (float)left + (float)num7 * _charSize.Width, num16, (float)left + (float)text.Length * _charSize.Width, num16);
        }
        PaintLineOverlay(graphics, value2, value4, left, num6, (int)Math.Ceiling(_charSize.Height));
        if (list == null || pen == null)
        {
            return;
        }
        int num17 = num6 + (int)_charSize.Height;
        foreach (KeyValuePair<int, int> item in list)
        {
            graphics.DrawLine(pen, item.Key, num17 - 1, item.Value, num17 - 1);
        }
    }

    protected virtual void PaintLineOverlay(Graphics graphics, int value2, int value3, int value4, int value5, int value6)
    {
    }

    public int AddHighlight(TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, HighlightStyle highlightStyle)
    {
        return SetHighlight(-1, textPosition, textPosition2, highlightStyle);
    }

    public void RemoveHighlight(int value2)
    {
        if (value2 != -1)
        {
            Highlight highlight = _highlights[value2];
            InvalidateLines(highlight.Start.Line, highlight.End.Line);
            _highlights.Remove(value2);
        }
    }

    public int SetHighlight(int value2, TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, HighlightStyle highlightStyle)
    {
        if (value2 == -1)
        {
            value2 = ++_lastHighlightId;
        }
        else
        {
            Highlight highlight = _highlights[value2];
            InvalidateLines(highlight.Start.Line, highlight.End.Line);
        }
        Highlight highlight2 = new Highlight(textPosition, textPosition2, highlightStyle);
        _highlights[value2] = highlight2;
        InvalidateLines(highlight2.Start.Line, highlight2.End.Line);
        return value2;
    }

    public void Redraw()
    {
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e3)
    {
        if (_flashBitmap != null)
        {
            e3.Graphics.DrawImage(_flashBitmap, 0, 0);
            PaintFlash(e3.Graphics);
            return;
        }
        if (!_hideMargins)
        {
            foreach (MarginSlot item in _margins)
            {
                item.Margin.Paint(e3.Graphics, new Rectangle(item.Left, 0, item.Right - item.Left, Height), _charSize, _document.LineCount, ScrollLine, this._visibleRows, this);
            }
        }
        Brush[,] array = new Brush[this._visibleRows, _visibleColumns];
        foreach (Highlight value in _highlights.Values)
        {
            PaintSelection(e3.Graphics, value.Start, value.End, value.Style, array);
        }
        if (_hasSelection)
        {
            PaintSelection(e3.Graphics, _selectionStart, SelectionEnd, _selectionStyle, array);
        }
        int num = ScrollColumn;
        int num2 = ((e3.ClipRectangle.Top >= _textArea.Top) ? ((int)Math.Floor((float)(e3.ClipRectangle.Top - _textArea.Top) / _charSize.Height)) : 0);
        int num5 = (int)Math.Ceiling(((float)(e3.ClipRectangle.Bottom - _textArea.Top) - _charSize.Height) / _charSize.Height);
        for (int num3 = num2; num3 <= Math.Min(this._visibleRows - 1, num5); num3++)
        {
            if (_displayRows[num3].VirtualLine == null)
            {
                int num4 = _displayRows[num3].Line;
                if (num4 >= _document.LineCount)
                {
                    break;
                }
                if (num4 != -1)
                {
                    PaintLine(e3.Graphics, num4, num3, num, array);
                }
            }
            else
            {
                Rectangle rectangle = new Rectangle(_textArea.Left, (int)RowToY(num3), _textArea.Width, (int)_charSize.Height);
                _displayRows[num3].VirtualLine.Draw(e3.Graphics, this, num3, ScrollColumn, _displayRows[num3].Line, rectangle, _charSize);
            }
        }
        base.OnPaint(e3);
    }

    public void LoadFile(string text)
    {
        _document.LoadFromFile(text);
        OnTextReplaced();
    }

    public void SaveToFile(string text)
    {
        StreamWriter streamWriter = new StreamWriter(text, append: false);
        for (int num = 0; num < Document.LineCount; num++)
        {
            if (num < Document.LineCount - 1)
            {
                streamWriter.WriteLine(_document.GetLineText(num));
            }
            else
            {
                streamWriter.Write(_document.GetLineText(num));
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
        int num;
        if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift)
        {
            scrollBar = _horizontalScrollBar;
            num = _horizontalWheelStep;
        }
        else
        {
            scrollBar = _verticalScrollBar;
            num = _verticalWheelStep;
        }
        if (e.Delta > 0)
        {
            num = -num;
        }
        if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
        {
            num *= _ctrlWheelMultiplier;
        }
        ScrollLimits limits = scrollBar.Limits;
        int num2 = (NativeScrollSemantics ? Math.Max(scrollBar.Maximum - limits.LargeChange + 1, 0) : Math.Max(scrollBar.Maximum, 0));
        int num3 = scrollBar.Value + num;
        if (num3 > num2)
        {
            num3 = num2;
        }
        if (num3 < limits.Minimum)
        {
            num3 = limits.Minimum;
        }
        scrollBar.Value = num3;
        Redraw();
        base.OnMouseWheel(e);
    }

    protected virtual void OnTextReplaced()
    {
        _maxLineWidth = -1;
        UpdateLayout();
        Redraw();
    }

    private int LineToRow(int value2)
    {
        for (int num = 0; num < _displayRows.Length; num++)
        {
            if (_displayRows[num].Line == value2 && _displayRows[num].VirtualLine == null)
            {
                return num;
            }
        }
        return -1;
    }

    public int GetPreviousRealLine(int value2)
    {
        int num = LineToRow(value2);
        while (--num >= 0)
        {
            if (_displayRows[num].VirtualLine == null)
            {
                return _displayRows[num].Line;
            }
        }
        return _previousRealLine;
    }

    public int GetNextRealLine(int value2)
    {
        int num = LineToRow(value2);
        while (++num < this._visibleRows)
        {
            if (_displayRows[num].VirtualLine == null)
            {
                return _displayRows[num].Line;
            }
        }
        return _nextRealLine;
    }

    private int RowToLine(int value2, out int value3, out int value4)
    {
        int num = 0;
        int num2 = 0;
        int num3 = 0;
        while (true)
        {
            if (num2 == value2)
            {
                value3 = -1;
                value4 = num3;
                return num;
            }
            if (num2 > value2)
            {
                break;
            }
            num3 = num;
            int num4 = _virtualLines.GetNextSpecialLine(Math.Min(num, 0));
            if (num4 == -1)
            {
                num4 = _document.LineCount;
            }
            int num5 = num4 - num;
            if (num5 > 0)
            {
                int num6 = Math.Min(num5, value2 - num2);
                num += num6;
                num2 += num6;
                num3 = num - 1;
            }
            else
            {
                num += _virtualLines.GetHiddenLineCount(num);
                num2 += _virtualLines.GetVirtualRowCount(num);
                num++;
                num2++;
            }
        }
        value3 = num2 - value2;
        value4 = num3;
        return num3;
    }

    private int LineToRowIndex(int value2)
    {
        int num = 0;
        int num2 = 0;
        while (num < value2)
        {
            int num3 = _virtualLines.GetNextSpecialLine(num);
            if (num3 == -1)
            {
                num3 = _document.LineCount;
            }
            int num4 = num3 - num;
            if (num4 > 0)
            {
                int num5 = Math.Min(num4, value2 - num);
                num += num5;
                num2 += num5;
            }
            else
            {
                num += _virtualLines.GetHiddenLineCount(num);
                num2 += _virtualLines.GetVirtualRowCount(num);
                num++;
                num2++;
            }
        }
        return num2;
    }

    internal void RebuildDisplayRows()
    {
        if (_displayRows == null || _displayRows.Length != _visibleRows)
        {
            _displayRows = new DisplayRow[_visibleRows];
        }
        int num = ScrollLine;
        if (_virtualLines == null)
        {
            _firstVisibleLine = num;
            _lastVisibleLine = num + _visibleRows;
            for (int num2 = 0; num2 < _visibleRows; num2++)
            {
                _displayRows[num2].Line = num + num2;
                _displayRows[num2].VirtualLine = null;
                _displayRows[num2].Decorator = null;
            }
            _previousRealLine = ((num == 0) ? num : (num - 1));
            _nextRealLine = num + _visibleRows;
            return;
        }
        int num3 = RowToLine(num, out var num4, out _previousRealLine);
        int num5 = 0;
        List<IVirtualLine> list = new List<IVirtualLine>();
        if (num4 != -1)
        {
            _virtualLines.GetVirtualLines(num3, list);
            for (int num6 = list.Count - num4; num6 < list.Count; num6++)
            {
                _displayRows[num5].Line = num3;
                _displayRows[num5].VirtualLine = list[num6];
                _displayRows[num5].Decorator = null;
                num5++;
            }
            num3 += 1 + _virtualLines.GetHiddenLineCount(num3);
        }
        _firstVisibleLine = num3;
        list.Clear();
        int num7 = num3;
        while (num5 < _visibleRows)
        {
            _displayRows[num5].VirtualLine = null;
            _displayRows[num5].Line = num3;
            _displayRows[num5].Decorator = _virtualLines.GetDecorator(num3);
            _virtualLines.GetVirtualLines(num3, list);
            if (list.Count > 0)
            {
                for (int num8 = 0; num8 < list.Count; num8++)
                {
                    if (++num5 >= _visibleRows)
                    {
                        break;
                    }
                    _displayRows[num5].VirtualLine = list[num8];
                    _displayRows[num5].Line = num3;
                    _displayRows[num5].Decorator = null;
                }
                list.Clear();
            }
            num7 = num3;
            num3 += 1 + _virtualLines.GetHiddenLineCount(num3);
            num5++;
            if (num3 >= _document.LineCount)
            {
                for (; num5 < _visibleRows; num5++)
                {
                    _displayRows[num5].VirtualLine = null;
                    _displayRows[num5].Line = -1;
                    _displayRows[num5].Decorator = null;
                }
            }
        }
        _nextRealLine = num3;
        _lastVisibleLine = num7;
    }

    protected virtual void OnSelectionChanged(TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, bool flag, SelectionChangeSource selectionChangeSource)
    {
        if (SelectionChanged != null)
        {
            SelectionChanged(this, textPosition, textPosition2, flag);
        }
    }

    protected virtual void OnCtrlTab(bool flag)
    {
        if (CtrlTabPressed != null)
        {
            CtrlTabPressed(this, flag);
        }
    }

    private Rectangle InflateFlashRect(Rectangle rectangle, Rectangle rectangle2, int value2, int value3)
    {
        int num = Math.Max(rectangle.Top - rectangle2.Top, rectangle2.Bottom - rectangle.Bottom);
        int num2 = Math.Min(rectangle.Left - rectangle2.Left, rectangle2.Right - rectangle.Right);
        num2 = (num = Math.Min(num2, num));
        num2 = num2 * value2 / value3;
        num = num * value2 / value3;
        return new Rectangle(rectangle.Left - num2, rectangle.Top - num, rectangle.Width + 2 * num2, rectangle.Height + 2 * num);
    }

    private void PaintFlash(Graphics graphics)
    {
        graphics.DrawRectangle(Pens.Black, _flashRect);
        for (int num = 0; num < 3; num++)
        {
            int num2 = _flashStep - num * 2;
            if (num2 >= 0 && num2 <= 10)
            {
                graphics.DrawRectangle(_flashPen, InflateFlashRect(_flashRect, ClientRectangle, num2, 10));
            }
        }
    }

    public void StartFlash(int value2)
    {
        _flashTimer.Interval = (int)Math.Ceiling((double)value2 / 16.0);
        _flashStep = 16;
        _flashTimer.Enabled = true;
        Bitmap bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, ClientRectangle);
        _flashBitmap = bitmap;
        if (!TryGetCaretColumn(CaretPosition, out var num))
        {
            num = 0;
        }
        Point point = Point.Round(PositionToPoint(num, CaretPosition.Line));
        TextDocument.TextPosition textPosition = GetMovedPosition(CaretMove.WordRight);
        num = ((textPosition.Line != CaretPosition.Line) ? _document.IndexToColumn(textPosition.Line, int.MaxValue, TabSize) : _document.IndexToColumn(textPosition.Line, textPosition.Column, TabSize));
        int num2 = (int)ColumnToX(num);
        _flashRect = new Rectangle(point.X, point.Y, num2 - point.X, (int)_charSize.Height);
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
        EnsureVisible(CaretPosition, flag3: true);
    }

    public void MoveCaretToEnd(bool flag)
    {
        TextDocument.TextPosition textPosition = new TextDocument.TextPosition
        {
            Line = _document.LineCount - 1
        };
        if (flag)
        {
            textPosition.Column = 0;
        }
        else
        {
            textPosition.Column = _document.GetLineText(textPosition.Line).Length;
        }
        if (_hasSelection)
        {
            SetSelection(flag: false, default);
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

    public TextDocument.TextPosition GetSelectionBoundary(bool flag2)
    {
        if (!_hasSelection)
        {
            return CaretPosition;
        }
        bool flag = SelectionEnd > SelectionStart;
        if (flag2)
        {
            flag = !flag;
        }
        if (!flag)
        {
            return SelectionEnd;
        }
        return SelectionStart;
    }

    public TextDocument.TextPosition Find(TextDocument.TextPosition textPosition, string text2, bool flag, bool flag2, bool flag3, FindSelectMode findSelectMode = FindSelectMode.None)
    {
        if (_document == null || _document.LineCount == 0 || string.IsNullOrEmpty(text2))
        {
            return new TextDocument.TextPosition(-1, -1);
        }
        int num = ((!flag) ? 1 : (-1));
        int num2 = textPosition.Column;
        for (int num3 = 0; num3 < 2; num3++)
        {
            for (int num4 = textPosition.Line; num4 >= 0 && num4 < _document.LineCount; num4 += num)
            {
                string text = _document.GetLineText(num4);
                if (text == null)
                {
                    continue;
                }
                int num6;
                if (flag)
                {
                    int num5 = ((num2 == -1) ? text.Length : num2);
                    num6 = text.LastIndexOf(text2, num5, num5 + 1, flag2 ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture);
                }
                else
                {
                    int startIndex = ((num2 != -1) ? num2 : 0);
                    num6 = text.IndexOf(text2, startIndex, flag2 ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture);
                }
                if (num6 != -1)
                {
                    TextDocument.TextPosition result = new TextDocument.TextPosition
                    {
                        Column = num6,
                        Line = num4
                    };
                    if (findSelectMode != FindSelectMode.None)
                    {
                        if (TerminalMode)
                        {
                            SetSelectionEnd(result);
                            SetSelectionCore(flag2: true, new TextDocument.TextPosition
                            {
                                Line = num4,
                                Column = num6 + text2.Length
                            }, SelectionChangeSource.FindResult);
                            EnsureCaretVisible(result);
                        }
                        else
                        {
                            SetCaretCore(result, SelectionChangeSource.FindResult);
                            SetSelectionCore(flag2: true, new TextDocument.TextPosition
                            {
                                Line = num4,
                                Column = num6 + text2.Length
                            }, SelectionChangeSource.FindResult);
                        }
                        int num7 = num6 + text2.Length;
                        if (num7 <= _visibleColumns)
                        {
                            ScrollColumn = 0;
                        }
                        else if (text.Length - num6 < _visibleColumns && text.Length > _visibleColumns)
                        {
                            ScrollColumn = text.Length - _visibleColumns;
                        }
                        else if (text2.Length < _visibleColumns)
                        {
                            int num8 = (num6 + num7) / 2;
                            ScrollColumn = num8 - _visibleColumns / 2;
                        }
                        else
                        {
                            ScrollColumn = num6;
                        }
                    }
                    return result;
                }
                num2 = -1;
            }
            if (!((num3 == 0) & flag3))
            {
                break;
            }
            textPosition.Line = (flag ? (_document.LineCount - 1) : 0);
            num2 = -1;
        }
        if (findSelectMode == FindSelectMode.SelectOrClear)
        {
            if (TerminalMode)
            {
                SetSelection(flag: false, TextDocument.TextPosition.Invalid);
            }
            else
            {
                SetCaretCore(new TextDocument.TextPosition
                {
                    Column = 0,
                    Line = _document.LineCount
                }, SelectionChangeSource.FindResult);
                SetSelectionCore(flag2: false, CaretPosition, SelectionChangeSource.FindResult);
            }
        }
        return new TextDocument.TextPosition(-1, -1);
    }

    public TextDocument.TextPosition OffsetToPosition(int value2)
    {
        return Document.OffsetToPosition(value2);
    }
}
