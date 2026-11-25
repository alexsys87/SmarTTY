using SmarTTY.Native;
using SmarTTY.Terminal;
using SmarTTY.Terminal.Emulation;
using SmarTTY.Terminal.Input;
using SmarTTY.Terminal.Rendering;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmarTTY.GUI.TerminalView;

/// <summary>The shape of the caret of <see cref="TerminalRenderView"/>.</summary>
internal enum TerminalCursorShape
{
    Block,
    Underline,
    Bar
}

/// <summary>What the last frames of a <see cref="TerminalRenderView"/> cost (for diagnostics).</summary>
internal sealed class TerminalFrameStats
{
    /// <summary>Frames that painted something.</summary>
    public long Frames;

    /// <summary>Rows drawn again (their content changed, or they were new).</summary>
    public long RowsDrawn;

    /// <summary>Rows whose drawing was kept, at the same place or moved (scrolling moves rows, it does not draw them again).</summary>
    public long RowsReused;

    /// <summary>Characters of output parsed.</summary>
    public long CharactersParsed;

    /// <summary>The time of the last frame: parsing the output, then building the rows.</summary>
    public double LastParseMilliseconds;

    public double LastBuildMilliseconds;

    /// <summary>Frames whose parsing stopped at the time budget, the rest of the output waiting for the next frame.</summary>
    public long BudgetHits;

    /// <summary>Frames that threw (written to the debug output and counted).</summary>
    public long FrameErrors;

    /// <summary>The rows drawn and reused in the last frame.</summary>
    public int LastRowsDrawn;

    public int LastRowsReused;

    public void Reset()
    {
        Frames = RowsDrawn = RowsReused = CharactersParsed = BudgetHits = FrameErrors = 0;
        LastParseMilliseconds = LastBuildMilliseconds = 0;
        LastRowsDrawn = LastRowsReused = 0;
    }
}

/// <summary>
/// The WPF view of a terminal (stage 2 of the migration away from WinForms). It draws the cells of <see cref="TerminalDisplayModel"/>
/// itself, without a text layout:
/// <list type="bullet">
/// <item>every row of the screen is a <see cref="DrawingVisual"/> of its own that keeps its drawing; a row is drawn again only when its
/// text or colours change (<see cref="RowLayout.Signature"/>), and when the output scrolls, the rows that are still on the screen are moved,
/// not drawn again;</item>
/// <item>the text is drawn with <see cref="GlyphRun"/> on the grid of a monospaced font (every glyph advances one cell), one glyph run for
/// neighbouring cells of one colour (<see cref="RowRunBuilder"/>);</item>
/// <item>the output of the remote side is queued (from any thread) and parsed and painted once per frame, in
/// <see cref="CompositionTarget.Rendering"/>, with a time budget, so a flood of output never blocks the window; the frame handler is only
/// attached while there is work, so an idle terminal costs nothing;</item>
/// <item>the keyboard is read with KeyDown and TextInput (<see cref="TerminalKeyEncoder"/>): dead keys, the IME (whose window is put at the
/// caret), Alt codes, AltGr and the xterm combinations.</item>
/// </list>
/// Under Remote Desktop and in virtual machines without a GPU WPF renders in software; the view keeps the changed area small there too
/// (one row per changed row, the caret blinks by opacity).
/// </summary>
internal sealed class TerminalRenderView : FrameworkElement
{
    // The longest piece of output parsed at once, and the time a frame may spend parsing before it paints.
    private const int ParseChunk = 16 * 1024;

    private const double ParseBudgetMilliseconds = 12;

    private sealed class RowVisual : DrawingVisual
    {
        public ulong Signature;

        public string Text;
    }

    private readonly VisualCollection _visuals;

    private readonly List<RowVisual> _rowVisuals = new List<RowVisual>();

    private readonly Dictionary<ulong, List<RowVisual>> _reusable = new Dictionary<ulong, List<RowVisual>>();

    private readonly List<RowVisual> _spare = new List<RowVisual>();

    private readonly List<RowLayout> _layouts = new List<RowLayout>();

    private readonly DrawingVisual _selectionVisual = new DrawingVisual();

    private readonly DrawingVisual _cursorVisual = new DrawingVisual();

    private readonly Dictionary<uint, SolidColorBrush> _brushes = new Dictionary<uint, SolidColorBrush>();

    private readonly TerminalColorScheme _scheme = new TerminalColorScheme();

    private readonly FrameOutputQueue _output = new FrameOutputQueue();

    private readonly TerminalKeyEncoder _keys;

    private readonly TerminalKeyOptions _keyOptions = new TerminalKeyOptions();

    private readonly TerminalInputEncoder _mouse = new TerminalInputEncoder();

    private readonly DispatcherTimer _blinkTimer;

    private readonly DispatcherTimer _fallbackTimer;

    private readonly Stopwatch _clock = new Stopwatch();

    private TerminalDisplayModel _model;

    private TerminalFont _font;

    // The font of the WinForms terminal when nothing is set (ReadonlyTextBrowser.Designer: Courier New, 12 points = 16 device-independent pixels).
    private FontFamily _fontFamily = new FontFamily("Courier New");

    private FontWeight _fontWeight = FontWeights.Normal;

    private FontStyle _fontStyle = FontStyles.Normal;

    private double _fontSize = 16;

    private bool _dirty;

    private bool _framesHooked;

    private bool _renderedSinceTick;

    private TimeSpan _lastRenderingTime;

    // What the overlays were drawn for: they are drawn again only when it changes.
    private int _selectionTopLine = -1;

    private int _selectionVersion = -1;

    private string _cursorState;

    private GridPoint _imeCell = new GridPoint(-1, -1);

    private bool _selecting;

    private TextDocument.TextPosition _selectionAnchor;

    private TerminalMouseButton _reportedButton = TerminalMouseButton.None;

    public TerminalRenderView(XtermKeyTable keyTable)
    {
        _keys = new TerminalKeyEncoder(keyTable);
        _visuals = new VisualCollection(this) { _selectionVisual, _ghostVisual, _cursorVisual };
        Focusable = true;
        FocusVisualStyle = null;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
        InputMethod.SetIsInputMethodEnabled(this, true);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.None);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Auto);
        RenderOptions.SetEdgeMode(_selectionVisual, EdgeMode.Aliased);
        RenderOptions.SetEdgeMode(_cursorVisual, EdgeMode.Aliased);
        _blinkTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (sender, e) => _cursorVisual.Opacity = _cursorVisual.Opacity > 0 ? 0 : 1;
        // CompositionTarget.Rendering stops while the window is minimized: this timer parses the output then, so the queue does not grow.
        _fallbackTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _fallbackTimer.Tick += OnFallbackTick;
        KeyScanFlags.Install();
        TextCompositionManager.AddPreviewTextInputStartHandler(this, (sender, e) => UpdateImeWindow(force: true));
        Unloaded += (sender, e) => UnhookFrames();
        Loaded += (sender, e) =>
        {
            // The DPI of the monitor is known once the element is in a window: the cells are measured again if it differs.
            if (_font != null && Math.Abs(_font.PixelsPerDip - VisualTreeHelper.GetDpi(this).PixelsPerDip) > 0.001)
            {
                ResetFont();
            }
            OnModelChanged();
        };
        // Frames stop while the element is hidden (another tab): it is painted when it is shown again.
        IsVisibleChanged += (sender, e) =>
        {
            if (IsVisible)
            {
                OnModelChanged();
            }
        };
    }

    /// <summary>Bytes for the remote side (typed, keys, replies, mouse, paste), raised on the thread of the view.</summary>
    public event Action<byte[], TerminalInputKind> DataEntered;

    /// <summary>The number of columns or rows changed (the remote side is told the new size of its PTY).</summary>
    public event Action<int, int> TerminalSizeChanged;

    /// <summary>A frame was painted (the scroll bar of the panel follows it).</summary>
    public event Action FrameRendered;

    /// <summary>A key, before the terminal looks at it: a handler that sets <see cref="RoutedEventArgs.Handled"/> takes it (window shortcuts).</summary>
    public event KeyEventHandler PreviewTerminalKey;

    /// <summary>The bytes of a key or a character, as they were sent (for a key log).</summary>
    public event Action<string, string> InputLogged;

    /// <summary>
    /// A paste was asked for (Shift+Insert, Ctrl+Shift+V, the middle button): the owner pastes through its guard and returns true. Without a
    /// handler the view pastes the clipboard itself.
    /// </summary>
    public Func<bool> PasteRequested;

    /// <summary>Text input before it is sent: a handler that returns true takes it (the command line of the Smart Terminal).</summary>
    public Func<TextCompositionEventArgs, bool> PreviewTerminalText;

    /// <summary>A plain click of the left button (no drag) on a place of the document.</summary>
    public event Action<TextDocument.TextPosition> DocumentClicked;

    /// <summary>A link is opened by a plain click (the Smart Terminal); otherwise it needs Ctrl, because a plain click selects text.</summary>
    public bool LinksNeedControl { get; set; } = true;

    /// <summary>
    /// A grey text drawn after a place of the document (the suggestion of the Smart Terminal from the history: the rest of a command typed
    /// before); null: none.
    /// </summary>
    public void SetGhostText(TextDocument.TextPosition position, string text)
    {
        if (text == _ghostText && position == _ghostPosition)
        {
            return;
        }
        _ghostText = string.IsNullOrEmpty(text) ? null : text;
        _ghostPosition = position;
        _ghostVersion++;
        OnModelChanged();
    }

    private string _ghostText;

    private TextDocument.TextPosition _ghostPosition;

    private int _ghostVersion;

    private int _ghostDrawnVersion = -1;

    private int _ghostDrawnTopLine = -1;

    private readonly DrawingVisual _ghostVisual = new DrawingVisual();

    /// <summary>The colour of the grey suggestion.</summary>
    public Color GhostTextColor { get; set; } = Color.FromRgb(0x80, 0x80, 0x80);

    private void DrawGhostText(TerminalFont font)
    {
        TerminalDisplayModel model = _model;
        if (_ghostDrawnVersion == _ghostVersion && _ghostDrawnTopLine == model.TopLine)
        {
            return;
        }
        _ghostDrawnVersion = _ghostVersion;
        _ghostDrawnTopLine = model.TopLine;
        using (DrawingContext context = _ghostVisual.RenderOpen())
        {
            if (_ghostText == null)
            {
                return;
            }
            int row = _ghostPosition.Line - model.TopLine;
            if (row < 0 || row >= model.Rows)
            {
                return;
            }
            // The rest of the command after the caret; what does not fit on the row is cut (the line wraps when it is taken).
            int column = _ghostPosition.Column;
            int fits = Math.Max(0, model.Columns - column);
            // In cells: a wide character takes two (a filler after it), one that does not fit whole is not drawn, and a character of no width
            // is drawn over the cell before it.
            string cells = CellWidth.ToCells(_ghostText, out string[] marks);
            string text = cells.Length > fits ? cells.Substring(0, fits) : cells;
            if (text.Length > 0 && CellWidth.IsWide(text[text.Length - 1]))
            {
                text = text.Substring(0, text.Length - 1);
            }
            if (text.Length == 0)
            {
                return;
            }
            context.PushTransform(new TranslateTransform(column * font.CellWidth, row * font.CellHeight));
            DrawGlyphs(context, text, 0, text.Length, GetBrush(new RgbColor((uint)(GhostTextColor.A << 24 | GhostTextColor.R << 16 | GhostTextColor.G << 8 | GhostTextColor.B))), font, 0, marks);
            context.Pop();
        }
    }

    /// <summary>Ctrl+Shift+Insert: the owner shows its paste menu.</summary>
    public event Action PasteMenuRequested;

    /// <summary>The right button was released over the view (not reported to the program): the point on the screen, in device pixels.</summary>
    public event Action<Point> ContextMenuRequested;

    /// <summary>Ctrl+Tab (true with Shift): the owner switches tabs.</summary>
    public event Action<bool> CtrlTabPressed;

    /// <summary>The mouse wheel sends Up and Down when the program does not read the mouse (a setting).</summary>
    public bool WheelSendsArrowKeys { get; set; }

    /// <summary>The caret is drawn (hollow) while the view does not have the keyboard focus.</summary>
    public bool ShowCaretWhenUnfocused
    {
        get => _showCaretWhenUnfocused;
        set
        {
            _showCaretWhenUnfocused = value;
            _cursorState = null;
            OnModelChanged();
        }
    }

    private bool _showCaretWhenUnfocused = true;

    /// <summary>The owner hides the caret (an inactive session).</summary>
    public bool CaretHiddenByOwner
    {
        get => _caretHiddenByOwner;
        set
        {
            if (_caretHiddenByOwner != value)
            {
                _caretHiddenByOwner = value;
                _cursorState = null;
                OnModelChanged();
            }
        }
    }

    /// <summary>The words in a selection by double click (the WinForms terminal counted '.' and '-' as letters of a word, for file names).</summary>
    public Func<char, bool> IsWordCharacter { get; set; } = ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '.' || ch == '-';

    private bool _caretHiddenByOwner;

    // The search: the matches of the search bar, highlighted in the rows that are shown, and the ranges it marks.
    private TextMatcher _matchHighlight;

    private readonly Dictionary<int, KeyValuePair<TextDocument.TextPosition, TextDocument.TextPosition>> _highlights = new Dictionary<int, KeyValuePair<TextDocument.TextPosition, TextDocument.TextPosition>>();

    private readonly Dictionary<int, HighlightStyle> _highlightStyles = new Dictionary<int, HighlightStyle>();

    private int _nextHighlight = 1;

    private int _highlightVersion;

    /// <summary>Highlights the matches of the search in the rows that are shown (null: none).</summary>
    public void SetMatchHighlight(TextMatcher matcher)
    {
        _matchHighlight = matcher;
        _highlightVersion++;
        OnModelChanged();
    }

    /// <summary>Marks a range of the document; returns the number to change or remove it with.</summary>
    public int AddHighlight(TextDocument.TextPosition start, TextDocument.TextPosition end, HighlightStyle style)
    {
        int index = _nextHighlight++;
        SetHighlight(index, start, end, style);
        return index;
    }

    public int SetHighlight(int index, TextDocument.TextPosition start, TextDocument.TextPosition end, HighlightStyle style)
    {
        _highlights[index] = new KeyValuePair<TextDocument.TextPosition, TextDocument.TextPosition>(start, end);
        _highlightStyles[index] = style;
        _highlightVersion++;
        OnModelChanged();
        return index;
    }

    public void RemoveHighlight(int index)
    {
        if (_highlights.Remove(index))
        {
            _highlightStyles.Remove(index);
            _highlightVersion++;
            OnModelChanged();
        }
    }

    /// <summary>Parses the output that waits, then <paramref name="output"/>, at once (messages of SmarTTY itself, a replayed log).</summary>
    public void ProcessOutputNow(string output)
    {
        FlushOutput();
        if (_model != null && !string.IsNullOrEmpty(output))
        {
            _model.ProcessOutput(output);
        }
    }

    /// <summary>Parses all the output that waits (before the keys of byte mode are counted on the screen).</summary>
    public void FlushOutput()
    {
        if (_model == null)
        {
            return;
        }
        string piece;
        while ((piece = _output.Take(ParseChunk)) != null)
        {
            _model.ProcessOutput(piece);
            Stats.CharactersParsed += piece.Length;
        }
    }

    /// <summary>Sends text to the remote side as typed (the owner's TypeText and paste).</summary>
    public void SendInput(string text, TerminalInputKind kind)
    {
        SendText(text, kind, kind == TerminalInputKind.Paste ? "paste" : "typed");
    }

    public TerminalFrameStats Stats { get; } = new TerminalFrameStats();

    public TerminalColorScheme ColorScheme => _scheme;

    private byte _backgroundAlpha = 255;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TerminalBackdrop.IsActiveProperty)
        {
            InvalidateVisual();
        }
    }

    /// <summary>
    /// The alpha of the background of the view (255: opaque). Only the background without a colour of its own is drawn with it: the
    /// text, the cells with a background colour, the caret and the selection stay as they are. What is behind the view shows through
    /// (see <c>GUI/TerminalBackdrop</c> for the window); it is used only while the window is see-through (<c>TerminalBackdrop.IsActive</c>).
    /// </summary>
    public byte BackgroundAlpha
    {
        get => _backgroundAlpha;
        set
        {
            if (_backgroundAlpha != value)
            {
                _backgroundAlpha = value;
                InvalidateVisual();
            }
        }
    }

    public TerminalKeyOptions KeyOptions => _keyOptions;

    /// <summary>The encoding of typed and pasted text (UTF-8).</summary>
    public Encoding InputEncoding { get; set; } = Encoding.UTF8;

    /// <summary>What a line break of pasted text is sent as (CR, like Enter).</summary>
    public string PasteLineBreak { get; set; } = "\r";

    /// <summary>The selection is copied when the mouse button is released.</summary>
    public bool CopyOnSelect { get; set; } = true;

    public bool CursorBlink { get; set; } = true;

    public TerminalCursorShape CursorShape { get; set; } = TerminalCursorShape.Block;

    /// <summary>The selection colour (drawn over the text).</summary>
    public Color SelectionColor { get; set; } = Color.FromArgb(0x60, 0x33, 0x99, 0xFF);

    /// <summary>A grid size that does not follow the size of the element (the mirror of another terminal); null: as many cells as fit.</summary>
    public Size? FixedGrid { get; private set; }

    public TerminalDisplayModel Model => _model;

    public TerminalFont TerminalFont => EnsureFont();

    public FontFamily TerminalFontFamily
    {
        get => _fontFamily;
        set
        {
            _fontFamily = value;
            ResetFont();
        }
    }

    /// <summary>Sets the whole font at once (the cells are measured once): the family, the size in device-independent pixels, bold, italic.</summary>
    public void SetFont(FontFamily family, double size, bool bold, bool italic)
    {
        _fontFamily = family;
        _fontSize = size;
        _fontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
        _fontStyle = italic ? FontStyles.Italic : FontStyles.Normal;
        ResetFont();
    }

    public double TerminalFontSize
    {
        get => _fontSize;
        set
        {
            _fontSize = value;
            ResetFont();
        }
    }

    /// <summary>Characters of output that wait to be parsed.</summary>
    public int PendingOutput => _output.Count;

    /// <summary>Shows a terminal (null: none).</summary>
    public void Attach(TerminalDisplayModel model)
    {
        if (_model != null)
        {
            _model.Changed -= OnModelChanged;
            _model.Reply -= OnReply;
            _model.MouseModeChanged -= OnMouseModeChanged;
        }
        _model = model;
        _output.Clear();
        ClearRows();
        if (_model != null)
        {
            _model.FlushPendingOutput = FlushOutput;
            _model.Changed += OnModelChanged;
            _model.Reply += OnReply;
            _model.MouseModeChanged += OnMouseModeChanged;
            UpdateGridSize();
        }
        Invalidate();
    }

    /// <summary>Makes the grid this size whatever the size of the element (null: as many cells as fit).</summary>
    public void SetFixedGrid(int columns, int rows)
    {
        FixedGrid = new Size(columns, rows);
        InvalidateMeasure();
        UpdateGridSize();
    }

    /// <summary>Queues output of the remote side (any thread). It is parsed and painted with the next frame.</summary>
    public void EnqueueOutput(string text)
    {
        if (_output.Append(text))
        {
            if (Dispatcher.CheckAccess())
            {
                RequestFrame();
            }
            else
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RequestFrame));
            }
        }
    }

    /// <summary>Drops the output that waits and resets the terminal (a reset that the user asked for).</summary>
    public void ResetTerminal()
    {
        _output.Clear();
        _model?.Reset();
    }

    /// <summary>Draws everything again (the colours or the font changed).</summary>
    public void Invalidate()
    {
        _brushes.Clear();
        ClearRows();
        _cursorState = null;
        _selectionVersion = -1;
        _ghostDrawnVersion = -1;
        InvalidateVisual();
        RequestFrame();
    }

    /// <summary>Scrolls the view to a line of the document.</summary>
    public void ScrollTo(int topLine)
    {
        if (_model != null)
        {
            _model.TopLine = topLine;
        }
    }

    /// <summary>Copies the selection to the clipboard; false when there is none.</summary>
    public bool CopySelection()
    {
        if (_model == null || !_model.HasSelection)
        {
            return false;
        }
        try
        {
            Win32Clipboard.SetText(WindowHandle, _model.GetSelectedText());
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Sends the text of the clipboard as a paste (bracketed when the program asked for it).</summary>
    public void Paste()
    {
        if (_model == null)
        {
            return;
        }
        string text = Win32Clipboard.GetText(WindowHandle);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        SendText(XtermProtocol.PreparePaste(text, PasteLineBreak, _model.BracketedPaste), TerminalInputKind.Paste, "paste");
    }

    private void RequestPaste()
    {
        if (PasteRequested == null || !PasteRequested())
        {
            Paste();
        }
    }

    private IntPtr WindowHandle => (PresentationSource.FromVisual(this) as HwndSource)?.Handle ?? IntPtr.Zero;

    // Frames.

    private void OnModelChanged()
    {
        _dirty = true;
        if (!_framesHooked)
        {
            RequestFrame();
        }
    }

    private void RequestFrame()
    {
        if (_framesHooked)
        {
            return;
        }
        _framesHooked = true;
        CompositionTarget.Rendering += OnRendering;
        _fallbackTimer.Start();
    }

    private void UnhookFrames()
    {
        if (!_framesHooked)
        {
            return;
        }
        _framesHooked = false;
        CompositionTarget.Rendering -= OnRendering;
        _fallbackTimer.Stop();
    }

    private void OnRendering(object sender, EventArgs e)
    {
        // The event can come more than once for one frame.
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastRenderingTime)
            {
                return;
            }
            _lastRenderingTime = args.RenderingTime;
        }
        _renderedSinceTick = true;
        Pump();
    }

    private void OnFallbackTick(object sender, EventArgs e)
    {
        if (!_renderedSinceTick)
        {
            Pump();
        }
        _renderedSinceTick = false;
    }

    private void Pump()
    {
        bool more;
        try
        {
            more = ParseOutput();
            if (_dirty && _model != null && IsVisible)
            {
                BuildFrame();
            }
        }
        catch (Exception exception)
        {
            // A frame that throws must not stop the next ones (an exception here would reach the dispatcher every frame and the view would
            // stand still): the error is written to the debug output and the rows are drawn anew.
            Debug.WriteLine("TerminalRenderView: " + exception);
            Stats.FrameErrors++;
            more = _output.Count != 0;
            _dirty = false;
            ClearRowsAfterError();
        }
        if (!more && _output.Count == 0 && (!_dirty || !IsVisible))
        {
            UnhookFrames();
        }
    }

    private void ClearRowsAfterError()
    {
        try
        {
            foreach (RowVisual visual in _rowVisuals)
            {
                if (visual != null)
                {
                    _visuals.Remove(visual);
                }
            }
            _rowVisuals.Clear();
            _cursorState = null;
            _selectionVersion = -1;
        }
        catch (Exception)
        {
        }
    }

    // Parses the queued output until it is gone or the time budget of the frame is spent; true when some is left.
    private bool ParseOutput()
    {
        if (_model == null)
        {
            _output.Clear();
            return false;
        }
        _clock.Restart();
        long parsed = 0;
        bool more = false;
        string piece;
        while ((piece = _output.Take(ParseChunk)) != null)
        {
            _model.ProcessOutput(piece);
            parsed += piece.Length;
            if (_clock.Elapsed.TotalMilliseconds >= ParseBudgetMilliseconds)
            {
                more = _output.Count != 0;
                break;
            }
        }
        if (parsed != 0)
        {
            Stats.CharactersParsed += parsed;
            Stats.LastParseMilliseconds = _clock.Elapsed.TotalMilliseconds;
            if (more)
            {
                Stats.BudgetHits++;
            }
            ResetBlink();
        }
        return more;
    }

    // Fonts and brushes.

    private TerminalFont EnsureFont()
    {
        if (_font == null)
        {
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            _font = new TerminalFont(_fontFamily, _fontSize, pixelsPerDip, _fontWeight, _fontStyle);
        }
        return _font;
    }

    private void ResetFont()
    {
        _font = null;
        InvalidateMeasure();
        UpdateGridSize();
        Invalidate();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ResetFont();
    }

    private SolidColorBrush GetBrush(RgbColor color)
    {
        if (!_brushes.TryGetValue(color.Argb, out SolidColorBrush brush))
        {
            brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
            brush.Freeze();
            _brushes[color.Argb] = brush;
        }
        return brush;
    }

    // Layout.

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index)
    {
        return _visuals[index];
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (FixedGrid.HasValue)
        {
            TerminalFont font = EnsureFont();
            return new Size(FixedGrid.Value.Width * font.CellWidth, FixedGrid.Value.Height * font.CellHeight);
        }
        return new Size(0, 0);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateGridSize();
    }

    /// <summary>
    /// The grid keeps its size while this is set (the window is being resized by dragging its edge: the program on the other side would get
    /// a resize for every pixel); it takes the size of the element again when it is cleared.
    /// </summary>
    public bool GridSizeFrozen
    {
        get => _gridSizeFrozen;
        set
        {
            _gridSizeFrozen = value;
            if (!value)
            {
                UpdateGridSize();
            }
        }
    }

    private bool _gridSizeFrozen;

    private void UpdateGridSize()
    {
        if (_model == null || _gridSizeFrozen)
        {
            return;
        }
        int columns;
        int rows;
        if (FixedGrid.HasValue)
        {
            columns = (int)FixedGrid.Value.Width;
            rows = (int)FixedGrid.Value.Height;
        }
        else
        {
            if (ActualWidth <= 0 || ActualHeight <= 0)
            {
                return;
            }
            TerminalFont font = EnsureFont();
            columns = Math.Max(1, (int)Math.Floor((ActualWidth + 0.01) / font.CellWidth));
            rows = Math.Max(1, (int)Math.Floor((ActualHeight + 0.01) / font.CellHeight));
        }
        if (columns != _model.Columns || rows != _model.Rows)
        {
            _model.Resize(columns, rows);
            TerminalSizeChanged?.Invoke(columns, rows);
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        // The background; the rows draw only the cells that have a background of their own.
        RgbColor background = _scheme.DefaultBackground;
        // Only while the window shows what is behind it: over an opaque window the alpha would only mix the colour with the one under it.
        if (_backgroundAlpha != 255 && TerminalBackdrop.GetIsActive(this))
        {
            background = RgbColor.FromArgb(background.A * _backgroundAlpha / 255, background.R, background.G, background.B);
        }
        drawingContext.DrawRectangle(GetBrush(background), null, new Rect(RenderSize));
    }

    // Building a frame.

    private void ClearRows()
    {
        foreach (RowVisual visual in _rowVisuals)
        {
            if (visual != null)
            {
                _visuals.Remove(visual);
            }
        }
        _rowVisuals.Clear();
        foreach (RowVisual visual in _spare)
        {
            _visuals.Remove(visual);
        }
        _spare.Clear();
        _dirty = true;
    }

    private void BuildFrame()
    {
        _dirty = false;
        _clock.Restart();
        TerminalFont font = EnsureFont();
        TerminalDisplayModel model = _model;
        EditableTextDocument document = model.Document;
        int rows = model.Rows;
        int columns = model.Columns;
        int topLine = model.TopLine;
        while (_layouts.Count < rows)
        {
            _layouts.Add(new RowLayout());
        }

        // The rows drawn before, by what they show: a row that shows the same thing again keeps its drawing, wherever it is now.
        _reusable.Clear();
        foreach (RowVisual visual in _rowVisuals)
        {
            if (visual == null)
            {
                continue;
            }
            if (!_reusable.TryGetValue(visual.Signature, out List<RowVisual> list))
            {
                list = new List<RowVisual>(1);
                _reusable[visual.Signature] = list;
            }
            list.Add(visual);
        }
        _rowVisuals.Clear();

        int drawn = 0;
        int reused = 0;
        for (int row = 0; row < rows; row++)
        {
            int line = topLine + row;
            RowLayout layout = _layouts[row];
            string text = line < document.LineCount ? document.GetLineText(line) : "";
            RowRunBuilder.Build(text, model.Lines.GetLine(line), _scheme, columns, layout);
            if (layout.Backgrounds.Count == 0 && layout.Texts.Count == 0)
            {
                // Nothing to draw: the background of the element shows.
                _rowVisuals.Add(null);
                continue;
            }
            RowVisual match = null;
            if (_reusable.TryGetValue(layout.Signature, out List<RowVisual> candidates))
            {
                for (int index = candidates.Count - 1; index >= 0; index--)
                {
                    if (string.Equals(candidates[index].Text, layout.Text, StringComparison.Ordinal))
                    {
                        match = candidates[index];
                        candidates.RemoveAt(index);
                        break;
                    }
                }
            }
            if (match != null)
            {
                reused++;
            }
            _rowVisuals.Add(match);
        }

        // The rows that were not reused are spare; the rows that need a drawing take them first.
        foreach (List<RowVisual> list in _reusable.Values)
        {
            _spare.AddRange(list);
        }
        _reusable.Clear();
        for (int row = 0; row < rows; row++)
        {
            RowLayout layout = _layouts[row];
            RowVisual visual = _rowVisuals[row];
            if (visual == null && (layout.Backgrounds.Count != 0 || layout.Texts.Count != 0))
            {
                if (_spare.Count != 0)
                {
                    visual = _spare[_spare.Count - 1];
                    _spare.RemoveAt(_spare.Count - 1);
                }
                else
                {
                    visual = new RowVisual();
                    RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
                    // Below the selection and the caret.
                    _visuals.Insert(_visuals.Count - 3, visual);
                }
                DrawRow(visual, layout, font);
                _rowVisuals[row] = visual;
                drawn++;
            }
            if (visual != null)
            {
                Vector offset = new Vector(0, row * font.CellHeight);
                if (visual.Offset != offset)
                {
                    visual.Offset = offset;
                }
            }
        }
        // Spare rows are kept for the next frames, but not more of them than one screen.
        while (_spare.Count > rows)
        {
            _visuals.Remove(_spare[_spare.Count - 1]);
            _spare.RemoveAt(_spare.Count - 1);
        }
        foreach (RowVisual visual in _spare)
        {
            if (visual.Signature != 0)
            {
                using (visual.RenderOpen())
                {
                }
                visual.Signature = 0;
                visual.Text = null;
            }
        }

        DrawSelection(font);
        DrawGhostText(font);
        DrawCursor(font);
        UpdateImeWindow(force: false);

        Stats.Frames++;
        Stats.RowsDrawn += drawn;
        Stats.RowsReused += reused;
        Stats.LastRowsDrawn = drawn;
        Stats.LastRowsReused = reused;
        Stats.LastBuildMilliseconds = _clock.Elapsed.TotalMilliseconds;
        FrameRendered?.Invoke();
    }

    private void DrawRow(RowVisual visual, RowLayout layout, TerminalFont font)
    {
        visual.Signature = layout.Signature;
        visual.Text = layout.Text;
        double cellWidth = font.CellWidth;
        double cellHeight = font.CellHeight;
        using (DrawingContext context = visual.RenderOpen())
        {
            foreach (BackgroundRun run in layout.Backgrounds)
            {
                context.DrawRectangle(GetBrush(run.Color), null, new Rect(run.Start * cellWidth, 0, run.Length * cellWidth, cellHeight));
            }
            foreach (TextRun run in layout.Texts)
            {
                SolidColorBrush brush = GetBrush(run.Foreground);
                DrawGlyphs(context, layout.Text, run.Start, run.Length, brush, font, 0, layout.Marks);
                if (run.Underline)
                {
                    context.DrawRectangle(brush, null, new Rect(run.Start * cellWidth, font.UnderlinePosition, run.Length * cellWidth, font.UnderlineThickness));
                }
            }
        }
    }

    // Draws the characters of cells [start, start + length) of a row: one glyph run per face (the main font, or a fallback for a character
    // it lacks). Every glyph advances a whole cell; a surrogate pair, which takes two cells of the grid, and a wide character, which takes its own
    // and the filler of its second cell (CellWidth), two. A filler draws nothing. A cell with characters of no width (marks) is drawn as the
    // character they compose with it when Unicode has one (e and a combining acute: é), otherwise as its character with the marks over it.
    private static void DrawGlyphs(DrawingContext context, string text, int start, int length, Brush brush, TerminalFont font, double y, string[] marks = null)
    {
        List<KeyValuePair<double, string>> overlays = null;
        int end = Math.Min(text.Length, start + length);
        double cellWidth = font.CellWidth;
        GlyphTypeface face = null;
        List<ushort> glyphs = null;
        List<double> advances = null;
        double runX = 0;
        int column = start;
        while (column < end)
        {
            char ch = text[column];
            if (ch == CellWidth.Filler)
            {
                column++;
                continue;
            }
            int codePoint = ch;
            int units = 1;
            if (char.IsHighSurrogate(ch) && column + 1 < text.Length && char.IsLowSurrogate(text[column + 1]))
            {
                codePoint = char.ConvertToUtf32(ch, text[column + 1]);
                units = 2;
            }
            else if (CellWidth.IsSecondCell(text, column + 1))
            {
                units = 2;
            }
            string mark = marks != null && column < marks.Length ? marks[column] : null;
            if (mark != null)
            {
                string composed = (text.Substring(column, units == 2 && char.IsHighSurrogate(ch) ? 2 : 1) + mark).Normalize(NormalizationForm.FormC);
                if (composed.Length == 1)
                {
                    codePoint = composed[0];
                }
                else
                {
                    (overlays ??= new List<KeyValuePair<double, string>>()).Add(new KeyValuePair<double, string>((column + units) * cellWidth, mark));
                }
            }
            else if (ch < ' ')
            {
                codePoint = ' ';
            }
            GlyphTypeface glyphFace = font.GetGlyph(codePoint, out ushort glyph);
            if (glyphs != null && glyphs.Count != 0 && !ReferenceEquals(glyphFace, face))
            {
                FlushGlyphs(context, face, glyphs, advances, runX, y, brush, font);
            }
            if (glyphs == null)
            {
                glyphs = new List<ushort>(end - column);
                advances = new List<double>(end - column);
            }
            if (glyphs.Count == 0)
            {
                face = glyphFace;
                runX = column * cellWidth;
            }
            glyphs.Add(glyph);
            advances.Add(units * cellWidth);
            column += units;
        }
        if (glyphs != null && glyphs.Count != 0)
        {
            FlushGlyphs(context, face, glyphs, advances, runX, y, brush, font);
        }
        if (overlays != null)
        {
            DrawMarks(context, overlays, y, brush, font);
        }
    }

    // The marks that compose with no character, over the cells they go with: each glyph is put so that it ends at the right edge of the
    // cell, which is where a mark of no advance is drawn from (its ink is left of its origin) and over the cell for one of a cell's advance.
    private static void DrawMarks(DrawingContext context, List<KeyValuePair<double, string>> overlays, double y, Brush brush, TerminalFont font)
    {
        foreach (KeyValuePair<double, string> overlay in overlays)
        {
            string mark = overlay.Value;
            for (int index = 0; index < mark.Length; index++)
            {
                int codePoint = mark[index];
                if (char.IsHighSurrogate(mark[index]) && index + 1 < mark.Length && char.IsLowSurrogate(mark[index + 1]))
                {
                    codePoint = char.ConvertToUtf32(mark[index], mark[index + 1]);
                    index++;
                }
                GlyphTypeface face = font.GetGlyph(codePoint, out ushort glyph);
                double advance = face.AdvanceWidths.TryGetValue(glyph, out double width) ? width * font.EmSize : 0;
                GlyphRun run = new GlyphRun(face, 0, false, font.EmSize, (float)font.PixelsPerDip, new[] { glyph }, new Point(overlay.Key - advance, y + font.Baseline),
                    new[] { 0.0 }, null, null, null, null, null, null);
                context.DrawGlyphRun(brush, run);
            }
        }
    }

    private static void FlushGlyphs(DrawingContext context, GlyphTypeface face, List<ushort> glyphs, List<double> advances, double x, double y, Brush brush, TerminalFont font)
    {
        GlyphRun run = new GlyphRun(face, 0, false, font.EmSize, (float)font.PixelsPerDip, glyphs.ToArray(), new Point(x, y + font.Baseline), advances.ToArray(),
            null, null, null, null, null, null);
        context.DrawGlyphRun(brush, run);
        glyphs.Clear();
        advances.Clear();
    }

    private void DrawSelection(TerminalFont font)
    {
        TerminalDisplayModel model = _model;
        bool anyHighlight = _matchHighlight != null || _highlights.Count != 0;
        int version = (model.HasSelection || anyHighlight) ? model.Version * 7919 + _highlightVersion : -2;
        if (version == -2 && _selectionVersion == -2)
        {
            return;
        }
        if (version != -2 && version == _selectionVersion && _selectionTopLine == model.TopLine)
        {
            return;
        }
        _selectionVersion = version;
        _selectionTopLine = model.TopLine;
        using (DrawingContext context = _selectionVisual.RenderOpen())
        {
            DrawHighlights(context, font);
            if (!model.HasSelection)
            {
                return;
            }
            model.GetSelection(out TextDocument.TextPosition start, out TextDocument.TextPosition end);
            Brush brush = new SolidColorBrush(SelectionColor);
            brush.Freeze();
            for (int row = 0; row < model.Rows; row++)
            {
                int line = model.TopLine + row;
                if (line < start.Line || line > end.Line)
                {
                    continue;
                }
                int from = line == start.Line ? start.Column : 0;
                int to = line == end.Line ? end.Column : Math.Max(from, model.Columns);
                if (to <= from)
                {
                    continue;
                }
                context.DrawRectangle(brush, null, new Rect(from * font.CellWidth, row * font.CellHeight, (to - from) * font.CellWidth, font.CellHeight));
            }
        }
    }

    // The ranges the search marks and the matches of the search bar in the rows that are shown, under the selection.
    private void DrawHighlights(DrawingContext context, TerminalFont font)
    {
        TerminalDisplayModel model = _model;
        foreach (KeyValuePair<int, KeyValuePair<TextDocument.TextPosition, TextDocument.TextPosition>> entry in _highlights)
        {
            HighlightStyle style = _highlightStyles[entry.Key];
            TextDocument.TextPosition start = entry.Value.Key;
            TextDocument.TextPosition end = entry.Value.Value;
            if (end < start)
            {
                TextDocument.TextPosition swap = start;
                start = end;
                end = swap;
            }
            Brush fill = style == null || style.FillColor.IsEmpty ? GetBrush(new RgbColor(0x40FFFF00)) : GetBrush(style.FillColor);
            Pen border = style != null && !style.BorderColor.IsEmpty ? new Pen(GetBrush(style.BorderColor), font.OnePixel) : null;
            for (int row = 0; row < model.Rows; row++)
            {
                int line = model.TopLine + row;
                if (line < start.Line || line > end.Line)
                {
                    continue;
                }
                int from = line == start.Line ? start.Column : 0;
                int to = line == end.Line ? end.Column : model.Columns;
                if (to > from)
                {
                    context.DrawRectangle(fill, border, new Rect(from * font.CellWidth, row * font.CellHeight, (to - from) * font.CellWidth, font.CellHeight));
                }
            }
        }
        if (_matchHighlight == null)
        {
            return;
        }
        Brush match = GetBrush(new RgbColor(0x70FFA500));
        for (int row = 0; row < model.Rows; row++)
        {
            int line = model.TopLine + row;
            if (line >= model.Document.LineCount)
            {
                break;
            }
            int y = row;
            DocumentSearch.ForEachMatch(model.Document.GetLineText(line), _matchHighlight, (index, length) =>
                context.DrawRectangle(match, null, new Rect(index * font.CellWidth, y * font.CellHeight, length * font.CellWidth, font.CellHeight)));
        }
    }

    private bool TryGetCaretCell(out GridPoint cell)
    {
        TerminalDisplayModel model = _model;
        TextDocument.TextPosition caret = model.Caret;
        cell = new GridPoint(caret.Column, caret.Line - model.TopLine);
        return !model.CaretHidden && !_caretHiddenByOwner && (ShowCaretWhenUnfocused || IsKeyboardFocused) && cell.Y >= 0 && cell.Y < model.Rows && cell.X >= 0 && cell.X <= model.Columns;
    }

    private void DrawCursor(TerminalFont font)
    {
        bool visible = TryGetCaretCell(out GridPoint cell);
        bool focused = IsKeyboardFocused;
        string under = "";
        if (visible)
        {
            int line = _model.Caret.Line;
            string text = line < _model.Document.LineCount ? _model.Document.GetLineText(line) : "";
            under = " ";
            if (cell.X < text.Length)
            {
                // On a wide character or a surrogate pair the caret covers both of its cells.
                bool pair = CellWidth.IsSecondCell(text, cell.X + 1) || (char.IsHighSurrogate(text[cell.X]) && cell.X + 1 < text.Length && char.IsLowSurrogate(text[cell.X + 1]));
                under = text.Substring(cell.X, pair ? 2 : 1);
            }
        }
        double caretWidth = under.Length * font.CellWidth;
        string state = visible ? cell.X + "," + cell.Y + "," + focused + "," + CursorShape + "," + under : "hidden";
        if (state == _cursorState)
        {
            return;
        }
        _cursorState = state;
        // The caret is drawn at the origin and moved to its cell, so that the glyph under a block caret is drawn like that of a row.
        using (DrawingContext context = _cursorVisual.RenderOpen())
        {
            if (!visible)
            {
                return;
            }
            Brush cursorBrush = GetBrush(_scheme.DefaultForeground);
            if (!focused)
            {
                Pen pen = new Pen(cursorBrush, font.OnePixel);
                pen.Freeze();
                double half = font.OnePixel / 2;
                context.DrawRectangle(null, pen, new Rect(half, half, caretWidth - font.OnePixel, font.CellHeight - font.OnePixel));
            }
            else
            {
                switch (CursorShape)
                {
                    case TerminalCursorShape.Underline:
                        context.DrawRectangle(cursorBrush, null, new Rect(0, font.CellHeight - 2 * font.OnePixel, caretWidth, 2 * font.OnePixel));
                        break;
                    case TerminalCursorShape.Bar:
                        context.DrawRectangle(cursorBrush, null, new Rect(0, 0, 2 * font.OnePixel, font.CellHeight));
                        break;
                    default:
                        context.DrawRectangle(cursorBrush, null, new Rect(0, 0, caretWidth, font.CellHeight));
                        if (under.Trim().Length != 0)
                        {
                            DrawGlyphs(context, under, 0, under.Length, GetBrush(_scheme.DefaultBackground), font, 0);
                        }
                        break;
                }
            }
        }
        if (visible)
        {
            _cursorVisual.Offset = new Vector(Math.Min(cell.X, _model.Columns - 1) * font.CellWidth, cell.Y * font.CellHeight);
        }
    }

    private void ResetBlink()
    {
        _cursorVisual.Opacity = 1;
        if (_blinkTimer.IsEnabled)
        {
            _blinkTimer.Stop();
            _blinkTimer.Start();
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (CursorBlink)
        {
            _blinkTimer.Start();
        }
        _cursorVisual.Opacity = 1;
        _cursorState = null;
        _imeCell = new GridPoint(-1, -1);
        OnModelChanged();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _blinkTimer.Stop();
        _cursorVisual.Opacity = 1;
        _cursorState = null;
        _keys.Reset();
        OnModelChanged();
    }

    // The window of the input method goes where the caret is (WPF does that only for its text boxes).
    private void UpdateImeWindow(bool force)
    {
        if (_model == null || !IsKeyboardFocused || !TryGetCaretCell(out GridPoint cell))
        {
            return;
        }
        if (!force && cell.X == _imeCell.X && cell.Y == _imeCell.Y)
        {
            return;
        }
        _imeCell = cell;
        if (!(PresentationSource.FromVisual(this) is HwndSource source) || source.CompositionTarget == null)
        {
            return;
        }
        TerminalFont font = EnsureFont();
        Point device;
        try
        {
            Point topLeft = TransformToAncestor(source.RootVisual).Transform(new Point(cell.X * font.CellWidth, cell.Y * font.CellHeight));
            device = source.CompositionTarget.TransformToDevice.Transform(topLeft);
        }
        catch (InvalidOperationException)
        {
            // Not in the visual tree of that window (any more).
            return;
        }
        int cellHeight = (int)Math.Round(font.CellHeight * font.PixelsPerDip);
        ImeCompositionWindow.MoveTo(source.Handle, (int)Math.Round(device.X), (int)Math.Round(device.Y), cellHeight, _fontFamily.Source);
    }

    // Keyboard.

    private void SyncKeyModes()
    {
        _keyOptions.ApplicationCursorKeys = _model.ApplicationCursorKeys;
        _keyOptions.ApplicationKeypad = _model.ApplicationKeypad;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_model == null || e.Handled)
        {
            return;
        }
        PreviewTerminalKey?.Invoke(this, e);
        if (e.Handled)
        {
            return;
        }
        Key key = e.Key;
        TerminalKeyKind kind = TerminalKeyKind.Normal;
        if (key == Key.System)
        {
            key = e.SystemKey;
        }
        if (key == Key.ImeProcessed)
        {
            kind = TerminalKeyKind.ImeProcessed;
            key = e.ImeProcessedKey;
        }
        else if (key == Key.DeadCharProcessed)
        {
            kind = TerminalKeyKind.DeadKey;
            key = e.DeadCharProcessedKey;
        }
        ModifierKeys modifiers = e.KeyboardDevice.Modifiers;
        VirtualKey virtualKey = (VirtualKey)KeyInterop.VirtualKeyFromKey(key);
        TerminalKeyEvent keyEvent = new TerminalKeyEvent
        {
            Key = virtualKey,
            Shift = (modifiers & ModifierKeys.Shift) != 0,
            Control = (modifiers & ModifierKeys.Control) != 0,
            Alt = (modifiers & ModifierKeys.Alt) != 0,
            RightAlt = e.KeyboardDevice.IsKeyDown(Key.RightAlt),
            Extended = KeyScanFlags.IsExtended(virtualKey),
            Kind = kind
        };
        if (kind == TerminalKeyKind.Normal && virtualKey == VirtualKey.Tab && keyEvent.Control && !keyEvent.Alt && CtrlTabPressed != null)
        {
            CtrlTabPressed(keyEvent.Shift);
            e.Handled = true;
            return;
        }
        SyncKeyModes();
        TerminalKeyResult result = _keys.KeyDown(keyEvent, _keyOptions);
        switch (result.Action)
        {
            case TerminalKeyAction.Send:
                string sequence = result.Text;
                bool plain = !keyEvent.Shift && !keyEvent.Control && !keyEvent.Alt;
                if (plain && (virtualKey == VirtualKey.Left || virtualKey == VirtualKey.Right || virtualKey == VirtualKey.Delete || virtualKey == VirtualKey.Back))
                {
                    // Byte mode: once per UTF-8 byte of the letter the key crosses.
                    int count = _model.ByteKeyCount(virtualKey);
                    if (count > 1)
                    {
                        sequence = new StringBuilder(sequence.Length * count).Insert(0, sequence, count).ToString();
                    }
                }
                else
                {
                    _model.DropPrediction();
                }
                SendText(sequence, virtualKey == VirtualKey.Back || virtualKey == VirtualKey.Return ? TerminalInputKind.Typed : TerminalInputKind.KeySequence, DescribeKey(keyEvent));
                _model.ScrollToScreen();
                ResetBlink();
                e.Handled = true;
                break;
            case TerminalKeyAction.Copy:
                CopySelection();
                e.Handled = true;
                break;
            case TerminalKeyAction.Paste:
                RequestPaste();
                _model.ScrollToScreen();
                e.Handled = true;
                break;
            case TerminalKeyAction.PasteMenu:
                PasteMenuRequested?.Invoke();
                e.Handled = true;
                break;
            case TerminalKeyAction.ScrollPageUp:
                _model.TopLine -= _model.Rows;
                e.Handled = true;
                break;
            case TerminalKeyAction.ScrollPageDown:
                _model.TopLine += _model.Rows;
                e.Handled = true;
                break;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (_model == null || e.Handled)
        {
            return;
        }
        if (PreviewTerminalText != null && PreviewTerminalText(e))
        {
            e.Handled = true;
            ResetBlink();
            return;
        }
        string text = _keys.TextInput(e.Text, e.SystemText, e.ControlText, _keyOptions);
        if (text != null)
        {
            if (text.Length == 1 && char.IsControl(text[0]))
            {
                _model.DropPrediction();
            }
            else
            {
                _model.TypedText(text);
            }
            SendText(text, TerminalInputKind.Typed, "text " + (string.IsNullOrEmpty(e.Text) ? (string.IsNullOrEmpty(e.SystemText) ? "control" : "system") : "") + " '" + (e.Text + e.SystemText + e.ControlText) + "'");
            _model.ScrollToScreen();
            ResetBlink();
            e.Handled = true;
        }
    }

    private static string DescribeKey(TerminalKeyEvent keyEvent)
    {
        StringBuilder builder = new StringBuilder();
        if (keyEvent.Control)
        {
            builder.Append(keyEvent.RightAlt && keyEvent.Alt ? "AltGr+" : "Ctrl+");
        }
        if (keyEvent.Alt && !(keyEvent.RightAlt && keyEvent.Control))
        {
            builder.Append("Alt+");
        }
        if (keyEvent.Shift)
        {
            builder.Append("Shift+");
        }
        builder.Append(keyEvent.Key);
        if (keyEvent.Extended)
        {
            builder.Append(" (ext)");
        }
        return builder.ToString();
    }

    private void SendText(string text, TerminalInputKind kind, string source)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        InputLogged?.Invoke(source, text);
        DataEntered?.Invoke(InputEncoding.GetBytes(text), kind);
    }

    private void SendBytes(byte[] bytes, TerminalInputKind kind, string source)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return;
        }
        InputLogged?.Invoke(source, Encoding.ASCII.GetString(bytes));
        DataEntered?.Invoke(bytes, kind);
    }

    private void OnReply(string reply)
    {
        SendText(reply, TerminalInputKind.KeySequence, "reply");
    }

    // Mouse.

    private void OnMouseModeChanged()
    {
        _mouse.ResetMouseMotion();
        Cursor = _model.MouseMode == MouseReportingMode.Off ? Cursors.IBeam : Cursors.Arrow;
    }

    private bool ReportsMouse()
    {
        return _model != null && _model.MouseMode != MouseReportingMode.Off && (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
    }

    private static int MouseModifierBits()
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        int bits = 0;
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            bits |= 4;
        }
        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            bits |= 8;
        }
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            bits |= 0x10;
        }
        return bits;
    }

    private static TerminalMouseButton ToButton(MouseButton button)
    {
        switch (button)
        {
            case MouseButton.Left:
                return TerminalMouseButton.Left;
            case MouseButton.Middle:
                return TerminalMouseButton.Middle;
            case MouseButton.Right:
                return TerminalMouseButton.Right;
        }
        return TerminalMouseButton.Other;
    }

    private GridPoint HitTestCell(Point point)
    {
        TerminalFont font = EnsureFont();
        int column = (int)Math.Floor(point.X / font.CellWidth);
        int row = (int)Math.Floor(point.Y / font.CellHeight);
        return new GridPoint(Math.Max(0, Math.Min(column, _model.Columns - 1)), Math.Max(0, Math.Min(row, _model.Rows - 1)));
    }

    // The place in the document between two cells that is nearest to a point (a selection starts and ends between cells).
    private TextDocument.TextPosition HitTestDocument(Point point)
    {
        TerminalFont font = EnsureFont();
        int column = (int)Math.Round(point.X / font.CellWidth);
        int row = (int)Math.Floor(point.Y / font.CellHeight);
        column = Math.Max(0, Math.Min(column, _model.Columns));
        return new TextDocument.TextPosition(column, Math.Max(0, _model.TopLine + row));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_model == null)
        {
            return;
        }
        Focus();
        Point point = e.GetPosition(this);
        if (ReportsMouse())
        {
            byte[] report = _mouse.MouseButton(_model.MouseMode, ToButton(e.ChangedButton), pressed: true, MouseModifierBits(), HitTestCell(point), _model.Columns, _model.Rows, _model.SgrMouse);
            if (report != null)
            {
                _reportedButton = ToButton(e.ChangedButton);
                CaptureMouse();
                SendBytes(report, TerminalInputKind.MouseReport, "mouse");
            }
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && TryGetLinkAt(point, out ILinkTarget link))
        {
            // Plain clicks select text, so a link needs Ctrl.
            link.Activate();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left)
        {
            TextDocument.TextPosition position = HitTestDocument(point);
            if (e.ClickCount == 2)
            {
                SelectWord(position);
            }
            else if (e.ClickCount >= 3)
            {
                _model.Select(new TextDocument.TextPosition(0, position.Line), new TextDocument.TextPosition(int.MaxValue, position.Line));
            }
            else
            {
                _selectionAnchor = position;
                _model.Select(position, position);
                _selecting = true;
                CaptureMouse();
            }
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Middle && MiddleButtonPastes)
        {
            RequestPaste();
            e.Handled = true;
        }
    }

    /// <summary>The middle button pastes (a setting).</summary>
    public bool MiddleButtonPastes { get; set; } = true;

    // The active link under a point while Ctrl (without Shift) is held.
    private bool TryGetLinkAt(Point point, out ILinkTarget link)
    {
        link = null;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (_model == null || !_model.LinksAvailable || (LinksNeedControl && (modifiers & ModifierKeys.Control) == 0) || (modifiers & ModifierKeys.Shift) != 0)
        {
            return false;
        }
        TerminalFont font = EnsureFont();
        int column = (int)Math.Floor(point.X / font.CellWidth);
        int line = _model.TopLine + (int)Math.Floor(point.Y / font.CellHeight);
        link = _model.GetLinkAt(line, column);
        return link != null && link.IsActive;
    }

    private void UpdateMouseCursor(Point point)
    {
        if (_model == null)
        {
            return;
        }
        Cursor = TryGetLinkAt(point, out _) ? Cursors.Hand : (_model.MouseMode == MouseReportingMode.Off ? Cursors.IBeam : Cursors.Arrow);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
        {
            UpdateMouseCursor(Mouse.GetPosition(this));
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if ((e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl) && IsMouseOver)
        {
            UpdateMouseCursor(Mouse.GetPosition(this));
        }
    }

    // A cell of a word: a word character, or the second cell of a wide one.
    private bool IsWordCell(string text, int column)
    {
        return CellWidth.IsSecondCell(text, column) ? IsWordCharacter(text[column - 1]) : IsWordCharacter(text[column]);
    }

    private void SelectWord(TextDocument.TextPosition position)
    {
        if (position.Line >= _model.Document.LineCount)
        {
            return;
        }
        string text = _model.Document.GetLineText(position.Line);
        int column = Math.Min(position.Column, Math.Max(0, text.Length - 1));
        if (text.Length == 0 || !IsWordCell(text, column))
        {
            return;
        }
        int start = column;
        while (start > 0 && IsWordCell(text, start - 1))
        {
            start--;
        }
        int end = column;
        while (end < text.Length && IsWordCell(text, end))
        {
            end++;
        }
        _model.Select(new TextDocument.TextPosition(start, position.Line), new TextDocument.TextPosition(end, position.Line));
        if (CopyOnSelect)
        {
            CopySelection();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_model == null)
        {
            return;
        }
        Point point = e.GetPosition(this);
        if (!_selecting && e.LeftButton != MouseButtonState.Pressed)
        {
            UpdateMouseCursor(point);
        }
        if (_selecting)
        {
            // Dragging past the top or the bottom scrolls.
            if (point.Y < 0)
            {
                _model.TopLine -= 1;
            }
            else if (point.Y > ActualHeight)
            {
                _model.TopLine += 1;
            }
            _model.Select(_selectionAnchor, HitTestDocument(point));
            return;
        }
        if (ReportsMouse())
        {
            TerminalMouseButton button = e.LeftButton == MouseButtonState.Pressed ? TerminalMouseButton.Left
                : e.MiddleButton == MouseButtonState.Pressed ? TerminalMouseButton.Middle
                : e.RightButton == MouseButtonState.Pressed ? TerminalMouseButton.Right : TerminalMouseButton.None;
            byte[] report = _mouse.MouseMove(_model.MouseMode, button, MouseModifierBits(), HitTestCell(point), _model.Columns, _model.Rows, _model.SgrMouse);
            SendBytes(report, TerminalInputKind.MouseReport, "mouse");
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_model == null)
        {
            return;
        }
        if (_selecting && e.ChangedButton == MouseButton.Left)
        {
            _selecting = false;
            ReleaseMouseCapture();
            if (!_model.HasSelection)
            {
                DocumentClicked?.Invoke(_selectionAnchor);
            }
            if (CopyOnSelect)
            {
                CopySelection();
            }
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Right && _reportedButton == TerminalMouseButton.None && !ReportsMouse())
        {
            // The context menu (or what the settings say the right button does) belongs to the owner.
            if (ContextMenuRequested != null)
            {
                Point screen = PointToScreen(e.GetPosition(this));
                ContextMenuRequested(screen);
                e.Handled = true;
            }
            return;
        }
        if (_reportedButton != TerminalMouseButton.None || ReportsMouse())
        {
            byte[] report = _mouse.MouseButton(_model.MouseMode, ToButton(e.ChangedButton), pressed: false, MouseModifierBits(), HitTestCell(e.GetPosition(this)), _model.Columns, _model.Rows, _model.SgrMouse);
            _reportedButton = TerminalMouseButton.None;
            ReleaseMouseCapture();
            SendBytes(report, TerminalInputKind.MouseReport, "mouse");
            // The context menu does not open while the program reads the mouse.
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _selecting = false;
        _reportedButton = TerminalMouseButton.None;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_model == null)
        {
            return;
        }
        if (ReportsMouse())
        {
            // A step of 0 counts as down, as in the WinForms terminal (its mouse golden has it).
            byte[] report = _mouse.MouseWheel(_model.MouseMode, e.Delta <= 0, MouseModifierBits(), HitTestCell(e.GetPosition(this)), _model.Columns, _model.Rows, _model.SgrMouse);
            SendBytes(report, TerminalInputKind.MouseReport, "wheel");
        }
        else if (WheelSendsArrowKeys && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            SyncKeyModes();
            TerminalKeyResult arrow = _keys.KeyDown(new TerminalKeyEvent { Key = e.Delta < 0 ? VirtualKey.Down : VirtualKey.Up, Extended = true }, _keyOptions);
            SendText(arrow.Text, TerminalInputKind.KeySequence, "wheel");
        }
        else
        {
            int lines = SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3;
            _model.TopLine -= Math.Sign(e.Delta) * lines;
        }
        e.Handled = true;
    }
}
