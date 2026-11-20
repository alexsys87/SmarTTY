using SmarTTY.CodeEditing;
using SmarTTY.Completion;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TextSearch = SmarTTY.CodeEditing.TextSearch;

namespace SmarTTY.GUI.TextView;

/// <summary>
/// The text view on WPF (stage 3, step 3D-2): a view of our own on the models of the text view, not AvalonEdit. The caret moves are
/// <see cref="CaretNavigator"/>, the wrap map <see cref="WrapMap"/>, the selection <see cref="TextSelection"/>, the undo history
/// <see cref="UndoHistory"/>, the edits <see cref="TextReplace"/> with the commands of <c>TextEditCommands</c>, typing and the edit keys
/// <see cref="TextTyping"/>: the code the WinForms view (<c>Controls/TextEditorControl</c>) runs. The layout and the mouse selection are
/// <see cref="TextViewLayout"/> and <see cref="TextPointer"/>. It draws itself in whole pixels of the device: the layout counts in device pixels,
/// the drawing is scaled back by the DPI, and every row is one <see cref="GlyphRun"/> a colour on the grid of the characters of a monospaced font.
/// The syntax colours, the folds and the inline messages are those of <c>CodeEditorPP</c> (3D-3 to 3D-5), the word list and the parameter hint
/// are <see cref="EditorCompletion"/> with WPF windows (<see cref="WpfSuggestionList"/>, <see cref="WpfHintView"/>, 3D-6).
/// Not in it yet: the marks of the blanks, the search highlights, characters that the font has no glyph for (they are drawn as "?") and
/// characters of two cells; it is tried in Tools > WPF text editor lab (<see cref="TextLabWindow"/>) before it goes into the editor tab.
/// </summary>
internal sealed class WpfTextEditor : FrameworkElement, ITextLayoutView, ICaretNavigationHost, ITextReplaceHost, ITextTypingHost, IEditorCompletionHost,
    IWpfSuggestionHost, ILineCommandHost, IEditorCommandTarget, ISearchTarget
{
    // What a word is for Ctrl+Left/Right, a double click and Ctrl+Backspace (as ReadonlyTextBrowser.IsWordChar).
    private const string WordCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

    private EditableTextDocument _document;

    private readonly TextViewLayout _layout;

    private readonly CaretNavigator _navigator;

    private readonly TextSelection _selection = new TextSelection();

    private readonly UndoHistory _history;

    private readonly TextReplace _replace;

    private readonly TextTyping _typing;

    private readonly TextPointer _pointer;

    // The word list and the parameter hint, with the rules of CodeEditorPP.
    private readonly EditorCompletion _completion;

    // The word list the completion made last (it makes a new one each time it opens the list).
    private WpfSuggestionList _suggestionList;

    // The tokens of the syntax of the lines (as CodeEditorPP keeps them) and the colours of the token types (none: the colour of the text).
    private readonly TokenCache _tokens = new TokenCache();

    // The folded blocks, on the tokens (as CodeEditorPP keeps them); they hide lines only while the lines are not wrapped.
    private readonly FoldingManager _folding;

    // The messages shown in rows of their own under their lines (the problems a check found), as CodeEditorPP keeps them.
    private readonly InlineMessageProvider _messages = new InlineMessageProvider();

    private RgbColor _messageBackColor = RgbColor.FromArgb(255, 211, 211, 211);

    private RgbColor _messageErrorColor = RgbColor.FromArgb(255, 255, 0, 0);

    private RgbColor _messageWarningColor = RgbColor.FromArgb(255, 184, 134, 11);

    private RgbColor _messageBorderColor = RgbColor.FromArgb(255, 0, 0, 0);

    private ImageSource _errorIcon;

    private ImageSource _warningIcon;

    private bool _iconsLoaded;

    private TokenCache.LineTokenizer _tokenizer;

    private RgbColor[] _tokenColors = new RgbColor[0];

    private readonly List<TextRun> _lineRuns = new List<TextRun>();

    private readonly VisualCollection _children;

    private readonly ScrollBar _verticalBar = new ScrollBar { Orientation = Orientation.Vertical, Visibility = Visibility.Collapsed };

    private readonly ScrollBar _horizontalBar = new ScrollBar { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };

    private readonly DispatcherTimer _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };

    // Scrolls while a selection is dragged beyond the edge of the text.
    private readonly DispatcherTimer _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };

    private readonly Dictionary<uint, SolidColorBrush> _brushes = new Dictionary<uint, SolidColorBrush>();

    private TextDocument.TextPosition _caret;

    private bool _caretVisible = true;

    private bool _readOnly;

    private bool _wordWrap;

    private bool _showLineNumbers = true;

    private bool _overwrite;

    private int _tabSize = 4;

    private bool _updatingScrollBars;

    private bool _updatingLayout;

    private RgbColor _backColor = RgbColor.FromArgb(255, 255, 255);

    private RgbColor _foreColor = RgbColor.FromArgb(0, 0, 0);

    private RgbColor _gutterBackColor = RgbColor.FromArgb(240, 240, 240);

    private RgbColor _gutterForeColor = RgbColor.FromArgb(128, 128, 128);

    private string _fontFamily = "Courier New";

    private double _fontPoints = 12;

    // The user's zoom of the text (Ctrl+wheel and the zoom commands), as FontZoom of the WinForms view.
    private float _fontZoom = 1f;

    // The commands of the editor by their keys, as CodeEditorPP keeps them (the defaults of EditorCommands until the settings give others).
    private readonly KeyBindingMap _keyBindingMap = new KeyBindingMap(EditorCommands.All);

    private readonly LineCommands _lineCommands;

    // The highlighted ranges (the range a search is limited to, the pair of the bracket at the caret) by their numbers, as ReadonlyTextBrowser keeps them.
    private readonly Dictionary<int, TextHighlight> _highlights = new Dictionary<int, TextHighlight>();

    private int _lastHighlightId;

    // The highlight of the pair of the bracket at the caret (-1: none), as CodeEditorPP keeps it.
    private int _pairHighlightId = -1;

    // The query of the search bar whose matches are highlighted (null: none).
    private TextMatcher _matchHighlighter;

    private readonly List<MatchSpan> _matchSpans = new List<MatchSpan>();

    private readonly List<BlankMark> _blankMarks = new List<BlankMark>();

    private bool _showAllCharacters;

    private bool _showTabs;

    // The colour of the highlight of the matches (as ReadonlyTextBrowser paints it) and of the marks of the blanks (DarkGray).
    private static readonly RgbColor MatchHighlightColor = RgbColor.FromArgb(95, 255, 190, 0);

    private static readonly RgbColor BlankMarkColor = RgbColor.FromArgb(255, 169, 169, 169);

    private sealed class TextHighlight
    {
        public TextDocument.TextPosition Start;

        public TextDocument.TextPosition End;

        public HighlightStyle Style;
    }

    private GlyphTypeface _glyphTypeface;

    // The size of the font in device pixels.
    private double _emSize;

    private double _baseline;

    private double _scale = 1;

    /// <summary>The caret, the selection, the modified state or a mode changed.</summary>
    public event EventHandler StateChanged;

    /// <summary>The text became modified or saved.</summary>
    public event EventHandler ModifiedChanged;

    /// <summary>Ctrl+S.</summary>
    public event EventHandler SaveRequested;

    public WpfTextEditor()
    {
        _document = new EditableTextDocument(new[] { "" }, TextDocument.LineEnding.CrLf);
        _layout = new TextViewLayout(this);
        _navigator = new CaretNavigator(this);
        _history = new UndoHistory(OnModifiedChanged);
        _replace = new TextReplace(this, _history);
        _typing = new TextTyping(this);
        _completion = new EditorCompletion(this);
        _lineCommands = new LineCommands(this);
        _pointer = new TextPointer(_selection, _navigator, () => _document);
        _tokens.SetDocument(_document, null);
        _folding = new FoldingManager(_tokens);
        _children = new VisualCollection(this) { _verticalBar, _horizontalBar };
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        _verticalBar.ValueChanged += (sender, e) => OnScrollBarChanged();
        _horizontalBar.ValueChanged += (sender, e) => OnScrollBarChanged();
        // The scroll bars keep their own pointer.
        _verticalBar.Cursor = Cursors.Arrow;
        _horizontalBar.Cursor = Cursors.Arrow;
        _caretTimer.Tick += (sender, e) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        };
        _autoScrollTimer.Tick += (sender, e) => OnAutoScrollTick();
        ContextMenu = BuildContextMenu();
        UpdateMetrics();
        // The DPI of the monitor is known once the view is in a window (OnDpiChanged comes only when it changes later).
        Loaded += (sender, e) => OnLayoutOptionChanged();
    }

    // ---- the text and its state

    /// <summary>The text being edited. A new one starts with the caret at its start, no selection and an empty undo history.</summary>
    public EditableTextDocument Document
    {
        get
        {
            return _document;
        }
        set
        {
            EditableTextDocument document = value ?? new EditableTextDocument(new[] { "" }, TextDocument.LineEnding.CrLf);
            if (document.LineCount == 0)
            {
                // The view always has a line to put the caret in.
                document.SetText("");
            }
            _document = document;
            _folding.Clear();
            _tokens.SetDocument(_document, _tokenizer);
            _selection.Clear();
            _pointer.Release();
            _caret = new TextDocument.TextPosition(0, 0);
            _navigator.DesiredColumn = -1;
            _layout.OnDocumentChanged();
            _layout.FirstRow = 0;
            _layout.ScrollX = 0;
            // Its saved state is the empty undo history, unless the text was modified (as the WinForms view does).
            _history.Reset();
            UpdateScrollBars();
            InvalidateVisual();
            OnStateChanged();
        }
    }

    /// <summary>The caret (a column is an index into the line).</summary>
    public TextDocument.TextPosition CaretPosition => _caret;

    public bool HasSelection
    {
        get => _selection.HasSelection;
        set
        {
            _selection.HasSelection = value;
            InvalidateVisual();
        }
    }

    /// <summary>Where the selection starts; the caret is its other end.</summary>
    public TextDocument.TextPosition SelectionStart => _selection.Anchor;

    public TextDocument.TextPosition SelectionEnd => _caret;

    /// <summary>The selected text (lines end with CR LF), empty without a selection.</summary>
    public string SelectedText => HasSelection ? _document.GetRangeText(SelectionStart, _caret) : "";

    public bool IsModified
    {
        get => _history.IsModified;
        set => _history.IsModified = value;
    }

    public bool CanUndo => !_readOnly && _history.HasUndo;

    public bool CanRedo => !_readOnly && _history.HasRedo;

    // ---- options

    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            OnStateChanged();
        }
    }

    /// <summary>Typing replaces the character after the caret (Insert switches it).</summary>
    public bool OverwriteMode
    {
        get => _overwrite;
        set
        {
            _overwrite = value;
            InvalidateVisual();
            OnStateChanged();
        }
    }

    /// <summary>Tab and Indent insert TabSize spaces instead of a tab character.</summary>
    public bool InsertSpaces { get; set; }

    public int TabSize
    {
        get => _tabSize;
        set
        {
            _tabSize = Math.Max(1, value);
            _layout.UpdateMaxColumns(-1);
            _layout.Wrap.Invalidate();
            OnLayoutOptionChanged();
        }
    }

    /// <summary>Long lines are wrapped at the right edge instead of scrolling sideways.</summary>
    public bool WordWrap
    {
        get => _wordWrap;
        set
        {
            if (_wordWrap == value)
            {
                return;
            }
            // The first line in view stays in view: the rows count differently.
            int firstLine = _layout.RowCount > 0 ? _layout.RowToLine(_layout.FirstRow, out _, out _) : 0;
            _wordWrap = value;
            // A message is a row of its own under a line, which the wrapped rows do not have: the messages are dropped (as CodeEditorPP does).
            _messages.Clear();
            _layout.Wrap.Invalidate();
            _layout.InvalidateRows();
            _layout.ScrollX = 0;
            _layout.FirstRow = _layout.ClampFirstRow(_layout.PositionToRow(new TextDocument.TextPosition(0, firstLine)));
            OnLayoutOptionChanged();
        }
    }

    public bool ShowLineNumbers
    {
        get => _showLineNumbers;
        set
        {
            _showLineNumbers = value;
            OnLayoutOptionChanged();
        }
    }

    /// <summary>The colours of the text and of the margin of the line numbers.</summary>
    public void SetColors(RgbColor background, RgbColor foreground, RgbColor marginBackground, RgbColor marginForeground)
    {
        _backColor = background;
        _foreColor = foreground;
        _gutterBackColor = marginBackground;
        _gutterForeColor = marginForeground;
        InvalidateVisual();
    }

    /// <summary>
    /// The syntax: the tokenizer of a language (null: none) and the colours of its token types, the first for the plain text (none: everything in
    /// the colour of the text); a token type past them takes the first, as in CodeEditorPP.
    /// </summary>
    public void SetSyntax(TokenCache.LineTokenizer tokenizer, RgbColor[] colors)
    {
        _tokenizer = tokenizer;
        _tokenColors = colors ?? new RgbColor[0];
        // The folds refer to the tokens of the old tokenizer (as CodeEditorPP.Retokenize).
        _folding.Clear();
        _tokens.SetDocument(_document, tokenizer);
        _layout.InvalidateRows();
        UpdateScrollBars();
        InvalidateVisual();
    }

    /// <summary>
    /// Adds messages under their lines, as a check reports them: the line and the column counted from 1 (a column of 0: the message goes in the
    /// middle of its row), a line past the text on the last line.
    /// </summary>
    public void AddMessages(IEnumerable<InlineMessageProvider.MessageRequest> messages)
    {
        foreach (InlineMessageProvider.MessageRequest message in messages)
        {
            _messages.AddReported(message.Line, message.Severity, message.Text, message.Column, _document.LineCount);
        }
        AfterMessageChange();
    }

    /// <summary>Adds a message under a line (the line and the column counted from 1).</summary>
    public void AddMessage(int line, InlineMessageProvider.MessageSeverity severity, string text, int column)
    {
        _messages.AddReported(line, severity, text, column, _document.LineCount);
        AfterMessageChange();
    }

    /// <summary>Removes the messages.</summary>
    public void ClearMessages()
    {
        if (_messages.Count == 0)
        {
            return;
        }
        _messages.Clear();
        AfterMessageChange();
    }

    /// <summary>How many messages are shown.</summary>
    public int MessageCount => _messages.Count;

    /// <summary>The colours of the messages: the band, the text of an error and of a warning, the frame of the band.</summary>
    public void SetMessageColors(RgbColor background, RgbColor error, RgbColor warning, RgbColor border)
    {
        _messageBackColor = background;
        _messageErrorColor = error;
        _messageWarningColor = warning;
        _messageBorderColor = border;
        InvalidateVisual();
    }

    private void AfterMessageChange()
    {
        _layout.InvalidateRows();
        UpdateScrollBars();
        InvalidateVisual();
    }

    /// <summary>Folds the block of the caret line (nothing while the lines are wrapped).</summary>
    public void CollapseCurrentFold()
    {
        if (!_wordWrap && _folding.CollapseLine(_caret.Line))
        {
            AfterFoldChange();
        }
    }

    /// <summary>Unfolds the block of the caret line.</summary>
    public void ExpandCurrentFold()
    {
        if (!_wordWrap && _folding.ExpandLine(_caret.Line))
        {
            AfterFoldChange();
        }
    }

    private void AfterFoldChange()
    {
        _layout.InvalidateRows();
        UpdateScrollBars();
        if (_layout.EnsureVisible(_caret))
        {
            UpdateScrollBarValues();
        }
        InvalidateVisual();
    }

    /// <summary>The font: a monospaced family and its size in points (before the scale of the monitor).</summary>
    // ---- the search, the highlights and the marks (the search target of the search bar, as EditableTextBrowser is)

    /// <summary>The styles of the highlight of a pair of brackets at the caret, by kind (those of CodeEditorPP).</summary>
    public HighlightStyle ParenHighlightStyle = new HighlightStyle(RgbColor.FromArgb(100, 255, 255, 224), RgbColor.FromArgb(50, 0, 0, 255), RgbColor.Empty);

    public HighlightStyle BraceHighlightStyle = new HighlightStyle(RgbColor.FromArgb(50, 144, 238, 144), RgbColor.FromArgb(50, 0, 0, 255), RgbColor.Empty);

    public HighlightStyle BracketHighlightStyle = new HighlightStyle(RgbColor.FromArgb(50, 173, 216, 230), RgbColor.FromArgb(50, 0, 0, 255), RgbColor.Empty);

    public HighlightStyle AngleHighlightStyle = new HighlightStyle(RgbColor.FromArgb(50, 211, 211, 211), RgbColor.FromArgb(50, 0, 0, 255), RgbColor.Empty);

    public HighlightStyle UnpairedHighlightStyle = new HighlightStyle(RgbColor.FromArgb(100, 255, 0, 0), RgbColor.FromArgb(50, 0, 0, 0), RgbColor.Empty);

    public HighlightStyle DefaultPairHighlightStyle = new HighlightStyle(RgbColor.Empty, RgbColor.Empty, RgbColor.Empty);

    /// <summary>The text was edited (the range an edit changed), for the search bar.</summary>
    public event TextRangeChangedHandler RangeChanged;

    /// <summary>Draws spaces, tabs, line ends and the characters that leave no ink as marks (those of <see cref="BlankMarks"/>).</summary>
    public bool ShowAllCharacters
    {
        get => _showAllCharacters;
        set
        {
            _showAllCharacters = value;
            InvalidateVisual();
        }
    }

    /// <summary>Draws a grey line under each tab, so that tab-indented lines are easy to tell from space-indented ones.</summary>
    public bool ShowTabs
    {
        get => _showTabs;
        set
        {
            _showTabs = value;
            InvalidateVisual();
        }
    }

    public string GetLineText(int line)
    {
        return _document.GetLineText(line);
    }

    public string GetRangeText(TextDocument.TextPosition start, TextDocument.TextPosition end)
    {
        return _document.GetRangeText(start, end);
    }

    /// <summary>
    /// Finds the next match from <paramref name="textPosition"/> (<see cref="TextSearch.TryFind"/>) and selects it as the search mode says: the
    /// caret at its start, the selection to its end, the view scrolled to it (as ReadonlyTextBrowser.Find). A failed search that clears the
    /// selection puts the caret at the end of the text (the WinForms view put it below the last line, which this view does not have).
    /// </summary>
    public TextDocument.TextPosition Find(TextDocument.TextPosition textPosition, TextMatcher matcher, bool searchBackward, bool wrapAround, FindSelectMode findSelectMode,
        TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd)
    {
        if (TextSearch.TryFind(_document, textPosition, matcher, searchBackward, wrapAround, scopeStart, scopeEnd, out int line, out int matchIndex, out int matchLength))
        {
            TextDocument.TextPosition position = new TextDocument.TextPosition(matchIndex, line);
            if (findSelectMode != FindSelectMode.None)
            {
                SetCaret(position);
                ((ITextEditView)this).SetSelection(true, new TextDocument.TextPosition(matchIndex + matchLength, line));
                if (!_wordWrap)
                {
                    int column = TextSearch.ScrollColumnFor(matchIndex, matchLength, _document.GetLineText(line).Length, Math.Max(1, (_layout.VisibleWidth - _layout.TextLeft) / _layout.CharWidth));
                    _layout.ScrollX = _layout.ClampScrollX(column * _layout.CharWidth);
                    UpdateScrollBarValues();
                }
                AfterEdit();
            }
            return position;
        }
        if (matcher != null && findSelectMode == FindSelectMode.SelectOrClear)
        {
            int last = _document.LineCount - 1;
            SetCaret(new TextDocument.TextPosition(_document.GetLineText(last).Length, last));
            ((ITextEditView)this).SetSelection(false, _caret);
            AfterEdit();
        }
        return new TextDocument.TextPosition(-1, -1);
    }

    /// <summary>Replaces every match (in the range when one is given) in one undo step; returns how many there were.</summary>
    public int ReplaceAll(TextMatcher matcher, string replacement, TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd, out TextDocument.TextPosition newScopeEnd)
    {
        newScopeEnd = scopeEnd;
        if (_readOnly)
        {
            return 0;
        }
        int count = _replace.ReplaceAll(matcher, replacement, scopeStart, scopeEnd, out newScopeEnd);
        AfterEdit();
        return count;
    }

    /// <summary>Highlights every match of <paramref name="matcher"/> in the rows in view; null removes the highlight.</summary>
    public void SetMatchHighlight(TextMatcher matcher)
    {
        _matchHighlighter = matcher;
        InvalidateVisual();
    }

    public int AddHighlight(TextDocument.TextPosition start, TextDocument.TextPosition end, HighlightStyle highlightStyle)
    {
        return SetHighlight(-1, start, end, highlightStyle);
    }

    public int SetHighlight(int highlightIndex, TextDocument.TextPosition start, TextDocument.TextPosition end, HighlightStyle highlightStyle)
    {
        if (highlightIndex == -1)
        {
            highlightIndex = ++_lastHighlightId;
        }
        _highlights[highlightIndex] = new TextHighlight { Start = start, End = end, Style = highlightStyle };
        InvalidateVisual();
        return highlightIndex;
    }

    public void RemoveHighlight(int highlightIndex)
    {
        if (highlightIndex != -1 && _highlights.Remove(highlightIndex))
        {
            InvalidateVisual();
        }
    }

    // The highlight of the pair of the bracket at the caret, as CodeEditorPP.OnSelectionChanged keeps it (nothing changes while a selection is made).
    private void UpdatePairHighlight()
    {
        if (HasSelection && SelectionStart != _caret)
        {
            return;
        }
        BracketPairKind kind = BracketPair.Find(_tokens, _document, _caret, _caret, out TextDocument.TextPosition pairStart, out TextDocument.TextPosition pairEnd);
        if (kind == BracketPairKind.None)
        {
            RemoveHighlight(_pairHighlightId);
            _pairHighlightId = -1;
        }
        else if (kind != BracketPairKind.Keep)
        {
            _pairHighlightId = SetHighlight(_pairHighlightId, pairStart, pairEnd, PairHighlightStyle(kind));
        }
    }

    private HighlightStyle PairHighlightStyle(BracketPairKind kind)
    {
        switch (kind)
        {
            case BracketPairKind.Paren:
                return ParenHighlightStyle;
            case BracketPairKind.Bracket:
                return BracketHighlightStyle;
            case BracketPairKind.Brace:
                return BraceHighlightStyle;
            case BracketPairKind.Angle:
                return AngleHighlightStyle;
            case BracketPairKind.Unpaired:
                return UnpairedHighlightStyle;
            default:
                return DefaultPairHighlightStyle;
        }
    }

    /// <summary>Enables the word list (Ctrl+Space and, with <see cref="CompletionOnTyping"/>, typing) and the parameter hint.</summary>
    public bool CompletionEnabled
    {
        get => _completion.Enabled;
        set => _completion.Enabled = value;
    }

    /// <summary>Opens the word list by itself once a couple of letters of an identifier have been typed.</summary>
    public bool CompletionOnTyping
    {
        get => _completion.OnTyping;
        set => _completion.OnTyping = value;
    }

    /// <summary>The words of this language are offered by the word list.</summary>
    public SyntaxHighlighting.Language CompletionLanguage
    {
        get => _completion.Language;
        set => _completion.Language = value;
    }

    /// <summary>The standard API symbols offered in the list and used by the hint; null: keywords and the words of the text only.</summary>
    public ApiSymbolDatabase ApiSymbols
    {
        get => _completion.ApiSymbols;
        set => _completion.ApiSymbols = value;
    }

    public bool IsSuggestionVisible => _completion.IsSuggestionVisible;

    public bool IsSignatureHintVisible => _completion.IsSignatureHintVisible;

    /// <summary>Opens the word list for the word the caret is in (Ctrl+Space).</summary>
    public void ShowCompletion()
    {
        _completion.ShowCompletion();
    }

    public void SetFont(string familyName, double sizeInPoints)
    {
        _fontFamily = string.IsNullOrEmpty(familyName) ? "Courier New" : familyName;
        _fontPoints = sizeInPoints > 0 ? sizeInPoints : 12;
        _layout.Wrap.Invalidate();
        OnLayoutOptionChanged();
    }

    /// <summary>The zoom of the text was changed with Ctrl+wheel or the zoom commands.</summary>
    public event EventHandler ZoomChanged;

    /// <summary>The zoom of the text (1 is the size of the font), kept between the limits of <see cref="EditorZoom"/>.</summary>
    public float FontZoom
    {
        get
        {
            return _fontZoom;
        }
        set
        {
            value = EditorZoom.Clamp(value);
            if (value == _fontZoom)
            {
                return;
            }
            _fontZoom = value;
            _layout.Wrap.Invalidate();
            OnLayoutOptionChanged();
        }
    }

    /// <summary>Makes the text bigger (1) or smaller (-1) by a tenth of its size.</summary>
    public void ZoomBy(int steps)
    {
        SetZoom(EditorZoom.By(FontZoom, steps));
    }

    /// <summary>Shows the text in the size of the font again.</summary>
    public void ResetZoom()
    {
        SetZoom(1f);
    }

    /// <summary>Sets the zoom (1 is the size of the font), rounded to hundredths and kept between its limits, as CodeEditorPP.SetZoom.</summary>
    public void SetZoom(float zoom)
    {
        float before = FontZoom;
        FontZoom = EditorZoom.Round(zoom);
        if (FontZoom != before)
        {
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>How the text marks comments (Ctrl+/); null when its language has none.</summary>
    public CommentStyle CommentStyle { get; set; }

    /// <summary>The keys of the commands of the editor (the names of <see cref="EditorCommands"/> with their keys, as the settings keep them).</summary>
    public void SetKeyBindings(IEnumerable<KeyValuePair<string, KeyBindingMap.KeyShortcut>> bindings)
    {
        foreach (KeyValuePair<string, KeyBindingMap.KeyShortcut> item in bindings)
        {
            _keyBindingMap.Bindings[item.Value] = _keyBindingMap.FindCommand(item.Key);
        }
    }

    // ---- line commands (CodeEditing/LineCommands, as CodeEditorPP runs them)

    /// <summary>The selection covers two lines or more (what sorting and removing duplicates work on).</summary>
    public bool HasLineSelection => _lineCommands.HasLineSelection;

    /// <summary>Rewrites lines as the Text menu says; false when nothing could be done.</summary>
    public bool ApplyLineTransform(LineTransformKind kind)
    {
        bool done = _lineCommands.ApplyLineTransform(kind);
        AfterEdit();
        return done;
    }

    public void DuplicateLines()
    {
        _lineCommands.DuplicateLines();
        AfterEdit();
    }

    public void MoveLines(int direction)
    {
        _lineCommands.MoveLines(direction);
        AfterEdit();
    }

    public void DeleteLines()
    {
        _lineCommands.DeleteLines();
        AfterEdit();
    }

    public bool ToggleComment()
    {
        bool done = _lineCommands.ToggleComment();
        AfterEdit();
        return done;
    }

    /// <summary>Moves the caret to the next bracket the predicate takes, forward or back, selecting the way when asked (as CodeEditorPP.MoveToToken).</summary>
    public bool MoveToToken(bool searchPrevious, bool isOpening, bool selectText, TokenPredicate callback)
    {
        if (!TokenMoves.TryFind(_tokens, _document, _caret, searchPrevious, isOpening, callback, out TextDocument.TextPosition target))
        {
            return false;
        }
        ((ITextEditView)this).SetSelection(selectText, _caret);
        SetCaret(target);
        return true;
    }

    /// <summary>Builds the tokens again without treating the text as replaced (as CodeEditorPP.Retokenize: the folds go).</summary>
    public void Retokenize()
    {
        _folding.Clear();
        _tokens.SetDocument(_document, _tokenizer);
        _layout.InvalidateRows();
        UpdateScrollBars();
        InvalidateVisual();
    }

    // The commands of several cursors come with 3E-3; until then their keys do nothing here.
    public void AddNextOccurrence()
    {
    }

    public void SelectAllOccurrences()
    {
    }

    public void AddCursorAbove()
    {
    }

    public void AddCursorBelow()
    {
    }

    EditableTextDocument ILineCommandHost.Document => _document;

    bool ILineCommandHost.CanEditLines => !_readOnly;

    void ILineCommandHost.ExecuteAsSingleUndoStep(Action edits)
    {
        if (!_readOnly)
        {
            _history.ExecuteAsSingleStep(edits);
        }
    }

    void ILineCommandHost.SetSelection(bool select, TextDocument.TextPosition anchor)
    {
        ((ITextEditView)this).SetSelection(select, anchor);
    }

    void ILineCommandHost.RemoveLines(int first, int last)
    {
        _typing.RemoveLines(first, last);
    }

    void ILineCommandHost.UpdateMaxLineWidth(int line)
    {
        _layout.UpdateMaxColumns(line);
    }

    // ---- commands

    public void Undo()
    {
        if (!_readOnly && _history.Undo())
        {
            AfterEdit();
        }
    }

    public void Redo()
    {
        if (!_readOnly)
        {
            _history.Redo();
        }
        AfterEdit();
    }

    public void SelectAll()
    {
        _selection.Anchor = new TextDocument.TextPosition(0, 0);
        SetCaret(TextSelection.DocumentEnd(_document));
        _selection.HasSelection = true;
        InvalidateVisual();
        OnStateChanged();
    }

    /// <summary>Puts the selection, or the caret line with its line break when nothing is selected and <paramref name="select"/>, on the clipboard.</summary>
    public void CopySelection(bool select = true)
    {
        if (!HasSelection && !select)
        {
            return;
        }
        TryCopyToClipboard(GetTextToCopy());
    }

    public void Cut()
    {
        // The text is removed only when the clipboard has it.
        if (!TryCopyToClipboard(GetTextToCopy()))
        {
            return;
        }
        if (!HasSelection)
        {
            _typing.RemoveLines(_caret.Line, _caret.Line);
        }
        else
        {
            ReplaceText(_caret, SelectionStart, select: false, "");
        }
    }

    public void Paste()
    {
        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return;
        }
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        try
        {
            ReplaceText(_caret, SelectionStart, !HasSelection, text);
            UpdateMaxLineWidth(-1);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>Removes the selected text (the clipboard is left alone).</summary>
    public void DeleteSelection()
    {
        _typing.DeleteSelection();
    }

    /// <summary>Replaces every occurrence of a text as one undo step and returns how many were replaced.</summary>
    public int ReplaceAll(string searchText, string replacement, bool ignoreCase)
    {
        if (_readOnly || string.IsNullOrEmpty(searchText))
        {
            return 0;
        }
        int count = _replace.ReplaceAll(TextMatcher.Literal(searchText, ignoreCase), replacement, TextDocument.TextPosition.Invalid, TextDocument.TextPosition.Invalid, out _);
        AfterEdit();
        return count;
    }

    /// <summary>Replaces the text between two positions (in either order); with <paramref name="select"/> the text goes in at <paramref name="start"/>.</summary>
    public void ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text)
    {
        if (!_readOnly)
        {
            _replace.Replace(start, end, select, text);
        }
    }

    /// <summary>Indents (or, by a negative number, unindents) the selected lines or the caret line.</summary>
    public void Indent(int levels)
    {
        if (_readOnly)
        {
            return;
        }
        _history.Execute(new IndentCommand(this, levels));
    }

    /// <summary>Moves the caret (brought into the text) and scrolls it into view; the selection, if any, now ends there.</summary>
    public void SetCaret(TextDocument.TextPosition position)
    {
        // A caret put inside a folded block unfolds it (as the WinForms view tells its folds before it moves the caret).
        if (_folding.OnCaretMoved(position.Line, position.Column))
        {
            _layout.InvalidateRows();
            UpdateScrollBars();
        }
        int line = Math.Max(0, Math.Min(position.Line, _document.LineCount - 1));
        int column = Math.Max(0, Math.Min(position.Column, _document.GetLineText(line).Length));
        _caret = new TextDocument.TextPosition(column, line);
        _caretVisible = true;
        RestartCaretBlink();
        if (_layout.EnsureVisible(_caret))
        {
            UpdateScrollBarValues();
        }
        InvalidateVisual();
        OnStateChanged();
    }

    private void AfterEdit()
    {
        UpdateScrollBars();
        InvalidateVisual();
        OnStateChanged();
    }

    private string GetTextToCopy()
    {
        return HasSelection ? _document.GetRangeText(SelectionStart, _caret) : _document.GetLineText(_caret.Line) + "\r\n";
    }

    private bool TryCopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
    }

    private void ShowError(Exception exception)
    {
        MessageDialog.Show(exception.Message, "Error", MessageDialogIcon.Error, MessageDialogButtons.Ok, defaultResult: null, owner: Window.GetWindow(this));
    }

    private ContextMenu BuildContextMenu()
    {
        ContextMenu menu = new ContextMenu();
        MenuItem undo = Item("_Undo", "Ctrl+Z", Undo);
        MenuItem redo = Item("_Redo", "Ctrl+Y", Redo);
        MenuItem cut = Item("Cu_t", "Ctrl+X", Cut);
        MenuItem copy = Item("_Copy", "Ctrl+C", () => CopySelection());
        MenuItem paste = Item("_Paste", "Ctrl+V", Paste);
        MenuItem delete = Item("_Delete", "Del", DeleteSelection);
        MenuItem selectAll = Item("Select _All", "Ctrl+A", SelectAll);
        MenuItem collapse = Item("_Fold", "", CollapseCurrentFold);
        MenuItem expand = Item("U_nfold", "", ExpandCurrentFold);
        menu.Items.Add(undo);
        menu.Items.Add(redo);
        menu.Items.Add(new Separator());
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(delete);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(collapse);
        menu.Items.Add(expand);
        menu.Opened += (sender, e) =>
        {
            FoldingManager.GutterState state = _wordWrap ? FoldingManager.GutterState.Plain : _folding.GetGutterState(_caret.Line);
            collapse.IsEnabled = state == FoldingManager.GutterState.Expandable;
            expand.IsEnabled = state == FoldingManager.GutterState.Collapsed;
            undo.IsEnabled = CanUndo;
            redo.IsEnabled = CanRedo;
            cut.IsEnabled = !_readOnly;
            paste.IsEnabled = !_readOnly;
            delete.IsEnabled = !_readOnly && HasSelection;
        };
        return menu;
    }

    private MenuItem Item(string header, string gesture, Action action)
    {
        MenuItem item = new MenuItem { Header = header, InputGestureText = gesture };
        item.Click += (sender, e) =>
        {
            action();
            Focus();
        };
        return item;
    }

    // ---- keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed || key == Key.DeadCharProcessed)
        {
            return;
        }
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool control = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        bool alt = (modifiers & ModifierKeys.Alt) != 0;
        VirtualKey virtualKey = (VirtualKey)KeyInterop.VirtualKeyFromKey(key);
        // The open word list takes its keys first, as the WinForms list, which has the keyboard while it is open, does.
        if (_suggestionList != null && _suggestionList.IsVisible
            && _suggestionList.HandleKey(virtualKey, () => ((IEditorCompletionHost)this).CurrentText, out bool typeBlank))
        {
            if (typeBlank)
            {
                TypeCharacters(" ");
            }
            e.Handled = true;
            return;
        }
        // The key bindings of CodeEditorPP: a key that has a command runs it and nothing else (Ctrl+Space opens the word list, Ctrl+Shift+D
        // duplicates the lines, Alt+Up moves them, Ctrl+/ comments them, Ctrl+Plus zooms, Ctrl+Alt+9 goes to a bracket and the others).
        VirtualKey keyData = virtualKey | (control ? VirtualKey.Control : 0) | (shift ? VirtualKey.Shift : 0) | (alt ? VirtualKey.Alt : 0);
        if (_keyBindingMap.Bindings.TryGetValue(new KeyBindingMap.KeyShortcut(new KeyStroke(keyData)), out KeyBindingMap.EditorCommand command))
        {
            command.Execute(this);
            e.Handled = true;
            return;
        }
        if (virtualKey == VirtualKey.Escape)
        {
            _completion.OnEscape();
        }
        // The edit keys and the Ctrl and Shift chords first, then the view's keys, as in TextEditorControl and its base class.
        TypingKeyResult result = _typing.KeyDown(virtualKey, control, alt, shift);
        bool handled = (result & TypingKeyResult.Handled) != 0 || (virtualKey == VirtualKey.Delete && !shift);
        if (!handled && control)
        {
            switch (virtualKey)
            {
                case VirtualKey.Up:
                case VirtualKey.Down:
                    {
                        // Ctrl+Up and Ctrl+Down scroll by a row and leave the caret where it is.
                        int firstRow = _layout.FirstRow;
                        SetFirstRow(firstRow + (virtualKey == VirtualKey.Up ? -1 : 1));
                        e.Handled = _layout.FirstRow != firstRow;
                        _completion.AfterKeyDown(virtualKey);
                        return;
                    }
                case VirtualKey.A:
                    // Ctrl+Alt is AltGr on many keyboards: AltGr+A types a letter there (ą, å).
                    if (!alt)
                    {
                        SelectAll();
                        handled = true;
                    }
                    break;
                case VirtualKey.S:
                    if (!alt && SaveRequested != null)
                    {
                        SaveRequested(this, EventArgs.Empty);
                        handled = true;
                    }
                    break;
            }
        }
        if (!handled)
        {
            handled = Navigate(virtualKey, control, shift);
        }
        if (!handled && virtualKey == VirtualKey.Insert)
        {
            if (!shift && !control)
            {
                OverwriteMode = !OverwriteMode;
            }
            handled = true;
        }
        if (!handled && !control && !alt)
        {
            // The characters the WinForms view gets as KeyPress: WPF sends them as text only sometimes (and Tab moves the focus).
            char typed = virtualKey == VirtualKey.Back ? '\b' : (virtualKey == VirtualKey.Return ? '\r' : (virtualKey == VirtualKey.Tab ? '\t' : '\0'));
            if (typed != '\0')
            {
                _typing.TypeCharacter(typed, shift);
                _completion.AfterKeyPress(typed);
                handled = true;
            }
        }
        if (handled)
        {
            // A key the editor took must not also be typed as text (WPF then raises no TextInput for it).
            e.Handled = true;
            AfterEdit();
        }
        // As CodeEditorPP after its base class: the keys that move the caret or delete look at the call the caret is in.
        _completion.AfterKeyDown(virtualKey);
    }

    // Characters typed as the WinForms view gets them as KeyPress: the edit, then the word list follows the typed word.
    private void TypeCharacters(string text)
    {
        foreach (char character in text)
        {
            _typing.TypeCharacter(character, false);
            _completion.AfterKeyPress(character);
        }
        AfterEdit();
    }

    // The navigation keys of ReadonlyTextBrowser.OnKeyDown: without Shift the selection goes, with Shift it starts at the caret or goes on.
    private bool Navigate(VirtualKey key, bool control, bool shift)
    {
        if (!shift)
        {
            if (!TryGetNavigationTarget(key, control, out TextDocument.TextPosition target))
            {
                return false;
            }
            _selection.HasSelection = false;
            SetCaret(target);
            return true;
        }
        bool started = _selection.StartIfNone(_caret);
        if (TryGetNavigationTarget(key, control, out TextDocument.TextPosition extended))
        {
            SetCaret(extended);
            return true;
        }
        if (started)
        {
            _selection.HasSelection = false;
        }
        return false;
    }

    private bool TryGetNavigationTarget(VirtualKey key, bool control, out TextDocument.TextPosition target)
    {
        _navigator.BeginMove(CaretNavigator.KeepsColumn(key), _caret);
        if (!CaretNavigator.TryMapKey(key, control, out CaretMove move, out _))
        {
            target = _caret;
            return false;
        }
        target = _navigator.Move(move, control, _caret);
        return true;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text))
        {
            return;
        }
        bool typed = false;
        foreach (char character in e.Text)
        {
            // Enter, Tab and Backspace were taken by the key; the other control characters type nothing.
            if (char.IsControl(character))
            {
                continue;
            }
            _typing.TypeCharacter(character, false);
            _completion.AfterKeyPress(character);
            typed = true;
        }
        if (typed)
        {
            e.Handled = true;
            AfterEdit();
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _caretVisible = true;
        _caretTimer.Start();
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _caretTimer.Stop();
        // The hint closes as in CodeEditorPP; the list closes too, as the WinForms list does when its window is left.
        _completion.OnLostFocus();
        _completion.HideCompletion();
        InvalidateVisual();
    }

    private void RestartCaretBlink()
    {
        _caretTimer.Stop();
        if (IsKeyboardFocused)
        {
            _caretTimer.Start();
        }
    }

    // ---- mouse (points in device pixels, as the layout counts)

    private void DevicePoint(Point point, out int x, out int y)
    {
        x = (int)Math.Floor(point.X * _scale);
        y = (int)Math.Floor(point.Y * _scale);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled)
        {
            return;
        }
        Focus();
        // A click in the text closes the word list, as the WinForms list closes when its window is left.
        _completion.HideCompletion();
        DevicePoint(e.GetPosition(this), out int x, out int y);
        if (x >= _layout.VisibleWidth || y >= _layout.VisibleHeight)
        {
            // The corner between the scroll bars.
            return;
        }
        TextDocument.TextPosition position = _layout.HitTest(x, y);
        if (e.ChangedButton == MouseButton.Left && _layout.IsInFoldMargin(x))
        {
            // A click on the marks folds or unfolds the block of the row (as FoldingMarginPainter: the click goes no further).
            if (y < (_layout.RowCount - _layout.FirstRow) * _layout.RowHeight && _folding.ClickMark(position.Line, _wordWrap, _caret, out TextDocument.TextPosition? caretTarget))
            {
                if (caretTarget.HasValue)
                {
                    SetCaret(caretTarget.Value);
                }
                AfterFoldChange();
            }
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool margin = x < _layout.NumbersWidth;
            SetCaret(_pointer.Press(position, e.ClickCount, shift, _caret, lineUnit: margin));
            CaptureMouse();
            _autoScrollTimer.Start();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right && !IsInSelection(position))
        {
            // The context menu works on what is under the mouse.
            _selection.Clear();
            SetCaret(position);
        }
    }

    private bool IsInSelection(TextDocument.TextPosition position)
    {
        if (!HasSelection)
        {
            return false;
        }
        TextDocument.TextPosition start = SelectionStart < _caret ? SelectionStart : _caret;
        TextDocument.TextPosition end = SelectionStart < _caret ? _caret : SelectionStart;
        return !(position < start) && position < end;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        DevicePoint(e.GetPosition(this), out int x, out int y);
        if (!_pointer.Dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            Cursor = x < _layout.GutterWidth ? Cursors.Arrow : Cursors.IBeam;
            return;
        }
        // Beyond the text the selection ends at its edge and the timer does the scrolling.
        DragTo(x, y);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    private void EndDrag()
    {
        _pointer.Release();
        _autoScrollTimer.Stop();
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    private void DragTo(int x, int y)
    {
        x = Math.Max(0, Math.Min(x, _layout.VisibleWidth - 1));
        y = Math.Max(0, Math.Min(y, _layout.VisibleRows * _layout.RowHeight - 1));
        TextDocument.TextPosition? caret = _pointer.DragTo(_layout.HitTest(x, y));
        if (caret.HasValue)
        {
            SetCaret(caret.Value);
        }
    }

    private void OnAutoScrollTick()
    {
        if (!_pointer.Dragging || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            // The button was let go where the view did not see it.
            EndDrag();
            return;
        }
        DevicePoint(Mouse.GetPosition(this), out int x, out int y);
        _layout.AutoScrollStep(x, y, out int rows, out int pixels);
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
            _layout.ScrollX = _layout.ClampScrollX(_layout.ScrollX + pixels);
            UpdateScrollBarValues();
        }
        DragTo(x, y);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != 0 && e.Delta != 0)
        {
            // Ctrl+wheel zooms the text, as in CodeEditorPP.
            ZoomBy(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }
        if (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Shift)
        {
            base.OnMouseWheel(e);
            return;
        }
        e.Handled = true;
        int rows = _layout.WheelRows(e.Delta);
        if (rows == 0)
        {
            return;
        }
        if (modifiers == ModifierKeys.Shift)
        {
            _layout.ScrollX = _layout.ClampScrollX(_layout.ScrollX - rows * _layout.CharWidth * 2);
            UpdateScrollBarValues();
        }
        else
        {
            SetFirstRow(_layout.FirstRow - rows);
        }
        InvalidateVisual();
    }

    // ---- what the models read of the view

    TextDocument ITextLayoutView.Document => _document;

    TextDocument ICaretNavigationHost.Document => _document;

    int ITextLayoutView.ClientWidth => (int)Math.Round(ActualWidth * _scale);

    int ITextLayoutView.ClientHeight => (int)Math.Round(ActualHeight * _scale);

    int ITextLayoutView.VerticalBarWidth => (int)Math.Round(SystemParameters.VerticalScrollBarWidth * _scale);

    int ITextLayoutView.HorizontalBarHeight => (int)Math.Round(SystemParameters.HorizontalScrollBarHeight * _scale);

    bool ITextLayoutView.VerticalBarVisible => _verticalBar.Visibility == Visibility.Visible;

    bool ITextLayoutView.HorizontalBarVisible => _horizontalBar.Visibility == Visibility.Visible;

    int ICaretNavigationHost.VisibleRows => _layout.VisibleRows;

    bool ICaretNavigationHost.IsWrapping => _wordWrap;

    WrapMap ICaretNavigationHost.Wrap => _layout.Wrap;

    public bool IsWordChar(char ch)
    {
        return WordCharacters.IndexOf(ch) != -1;
    }

    // The lines shown before and after a line: past the lines a fold hides (no rows between the lines yet).
    int ICaretNavigationHost.GetPreviousRealLine(int line)
    {
        return _layout.LineBefore(line);
    }

    int ICaretNavigationHost.GetNextRealLine(int line)
    {
        return _layout.LineAfter(line);
    }

    ITextFolds ITextLayoutView.Folds => _folding;

    ILineMessages ITextLayoutView.Messages => _messages;

    void ICaretNavigationHost.OnMoveBeyondDocument(ref TextDocument.TextPosition textPosition, int direction)
    {
    }

    void ITextEditView.GetSelectedLines(out int first, out int last)
    {
        _selection.GetSelectedLines(_caret, out first, out last);
    }

    void ITextEditView.SetSelection(bool select, TextDocument.TextPosition start)
    {
        _selection.HasSelection = select;
        _selection.Anchor = start;
        InvalidateVisual();
    }

    void ITextEditView.SetCaretColumn(int column)
    {
        SetCaret(new TextDocument.TextPosition(column, _caret.Line));
    }

    void ITextEditView.OnLineChanged(int line, int column, int addedLength, int removedLength)
    {
        _tokens.RetokenizeLine(line, _tokenizer);
        // The tokens of the line are new ones: a folded block that started or ended there refers to the old tokens (as CodeEditorPP).
        _folding.RemoveStaleRegions(line, 0, 0, out bool staleRegionsRemoved);
        if (staleRegionsRemoved)
        {
            _layout.InvalidateRows();
        }
        _layout.Wrap.Invalidate();
        if (line == _caret.Line && _caret.Column > _document.GetLineText(line).Length)
        {
            SetCaret(new TextDocument.TextPosition(_document.GetLineText(line).Length, line));
        }
        _layout.UpdateMaxColumns(line);
        OnTextChangedOutsideEdit();
    }

    void ITextEditView.OnRangeChanged(TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd)
    {
        SyntaxColoring.ChangedLines(start, oldEnd, newEnd, out int firstLine, out int removedLines, out int addedLines);
        _messages.ShiftLines(firstLine, removedLines, addedLines);
        _tokens.OnLinesChanged(firstLine, removedLines, addedLines, _tokenizer);
        _folding.RemoveStaleRegions(firstLine, removedLines, addedLines, out _);
        _layout.InvalidateRows();
        _layout.Wrap.Invalidate();
        _layout.UpdateMaxColumns(-1);
        RangeChanged?.Invoke(start, oldEnd, newEnd);
        OnTextChangedOutsideEdit();
    }

    // The edit commands decide the modified state when they finish; a change made any other way can never be undone back to the saved text.
    private void OnTextChangedOutsideEdit()
    {
        if (!_history.InEdit)
        {
            _history.IsModified = true;
        }
    }

    void ITextEditView.InvalidateLine(int line)
    {
        InvalidateVisual();
    }

    void ITextEditView.UpdateLayout()
    {
        UpdateScrollBars();
    }

    void ITextEditView.Redraw()
    {
        InvalidateVisual();
    }

    bool ITextReplaceHost.SingleLine => false;

    bool ITextReplaceHost.UseExistingLineEnding => true;

    string ITextReplaceHost.GetLineOrNull(int line)
    {
        return line >= _document.LineCount ? null : _document.GetLineText(line);
    }

    public string GetIndentation(int line)
    {
        string text = _document.GetLineText(line);
        int length = 0;
        while (length < text.Length && (text[length] == ' ' || text[length] == '\t'))
        {
            length++;
        }
        return text.Substring(0, length);
    }

    bool ITextTypingHost.IsEditable => true;

    bool ITextTypingHost.CopyLineWhenNoSelection => true;

    int ITextTypingHost.IndexToColumn(int line, int index)
    {
        return _document.IndexToColumn(line, index, _tabSize);
    }

    TextDocument.TextPosition ITextTypingHost.GetMovedPosition(CaretMove move)
    {
        return _navigator.Move(move, (Keyboard.Modifiers & ModifierKeys.Control) != 0, _caret);
    }

    void ITextTypingHost.UpdateMaxLineWidth(int line)
    {
        _layout.UpdateMaxColumns(line);
    }

    private void UpdateMaxLineWidth(int line)
    {
        _layout.UpdateMaxColumns(line);
    }

    void ITextTypingHost.OnCharTyping(char ch, ref string text, ref TextDocument.TextPosition textPosition)
    {
        _completion.OnCharTyping(ch, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    void ITextTypingHost.OnCharTyped(char ch, TextDocument.TextPosition textPosition)
    {
        _completion.OnCharTyped(ch, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    // ---- completion

    EditableTextDocument IEditorCompletionHost.Document => _document;

    TextDocument.TextPosition IEditorCompletionHost.CaretPosition => _caret;

    string IEditorCompletionHost.CurrentText
    {
        get
        {
            if (_document == null || _caret.Line == -1)
            {
                return null;
            }
            return _typing.GetWordAtCaret(out _, out _) ?? "";
        }
    }

    RgbColor IEditorCompletionHost.SuggestionBackColor => _backColor;

    ISuggestionPopupFactory IEditorCompletionHost.CreatePopupFactory()
    {
        return new ListFactory(this);
    }

    IHintView IEditorCompletionHost.CreateHintView()
    {
        return new WpfHintView(this);
    }

    void IEditorCompletionHost.FocusView()
    {
        Focus();
    }

    bool IEditorCompletionHost.IsWordSeparator(char ch)
    {
        return WordCharacters.IndexOf(ch) == -1;
    }

    UIElement IWpfSuggestionHost.PlacementTarget => this;

    Rect IWpfSuggestionHost.CaretBounds
    {
        get
        {
            _layout.PositionToPoint(_caret, out int x, out int y);
            return new Rect(x / _scale, y / _scale, _layout.CharWidth / _scale, _layout.RowHeight / _scale);
        }
    }

    RgbColor IWpfSuggestionHost.ItemForeColor => _foreColor;

    FontFamily IWpfSuggestionHost.ItemFontFamily => new FontFamily(_fontFamily);

    double IWpfSuggestionHost.ItemFontSize => _fontPoints * _fontZoom * 96.0 / 72.0;

    void IWpfSuggestionHost.AcceptSuggestion(ISuggestion suggestion, VirtualKey key)
    {
        // As CodeEditorPP.OnSuggestionAccepted: the word replaces the one at the caret.
        if (suggestion != null && !_readOnly)
        {
            _typing.ReplaceWordAtCaret(suggestion.DisplayName);
            AfterEdit();
        }
    }

    // Keeps the list the completion made, so that the keys can reach it.
    private sealed class ListFactory : ISuggestionPopupFactory
    {
        private readonly WpfTextEditor _editor;

        public ListFactory(WpfTextEditor editor)
        {
            _editor = editor;
        }

        public ISuggestionPopup Create(ISuggestionSource suggestionSource, bool allowMultiSelect)
        {
            _editor._suggestionList = new WpfSuggestionList(_editor, suggestionSource, allowMultiSelect);
            return _editor._suggestionList;
        }
    }

    // ---- layout and scrolling

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index)
    {
        return _children[index];
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _verticalBar.Measure(availableSize);
        _horizontalBar.Measure(availableSize);
        double width = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double barWidth = SystemParameters.VerticalScrollBarWidth;
        double barHeight = SystemParameters.HorizontalScrollBarHeight;
        bool vertical = _verticalBar.Visibility == Visibility.Visible;
        bool horizontal = _horizontalBar.Visibility == Visibility.Visible;
        _verticalBar.Arrange(new Rect(Math.Max(0, finalSize.Width - barWidth), 0, barWidth, Math.Max(0, finalSize.Height - (horizontal ? barHeight : 0))));
        _horizontalBar.Arrange(new Rect(0, Math.Max(0, finalSize.Height - barHeight), Math.Max(0, finalSize.Width - (vertical ? barWidth : 0)), barHeight));
        return finalSize;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateScrollBars();
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        OnLayoutOptionChanged();
    }

    /// <summary>The font, the tab size, the wrap or the margin changed: the rows and the columns move.</summary>
    private void OnLayoutOptionChanged()
    {
        UpdateMetrics();
        UpdateScrollBars();
        if (_layout.EnsureVisible(_caret))
        {
            UpdateScrollBarValues();
        }
        InvalidateVisual();
        OnStateChanged();
    }

    private void UpdateMetrics()
    {
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_scale <= 0)
        {
            _scale = 1;
        }
        _glyphTypeface = FindGlyphTypeface(_fontFamily) ?? FindGlyphTypeface("Courier New") ?? FindGlyphTypeface("Consolas");
        // Points to device pixels: 96 DIPs an inch, 72 points an inch.
        _emSize = _fontPoints * _fontZoom * 96.0 / 72.0 * _scale;
        int charWidth = 8;
        int textHeight = 16;
        if (_glyphTypeface != null && _glyphTypeface.CharacterToGlyphMap.TryGetValue('0', out ushort zero))
        {
            charWidth = (int)Math.Ceiling(_glyphTypeface.AdvanceWidths[zero] * _emSize);
            textHeight = (int)Math.Ceiling(_glyphTypeface.Height * _emSize);
            _baseline = Math.Round(_glyphTypeface.Baseline * _emSize);
        }
        _layout.SetMetrics(charWidth, textHeight);
        // The margin of the fold marks is 12 pixels at 96 DPI, as in the WinForms view.
        _layout.FoldMarginWidth = (int)Math.Round(12 * _scale);
    }

    private static GlyphTypeface FindGlyphTypeface(string familyName)
    {
        Typeface typeface = new Typeface(new FontFamily(familyName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        return typeface.TryGetGlyphTypeface(out GlyphTypeface glyphTypeface) ? glyphTypeface : null;
    }

    private void OnScrollBarChanged()
    {
        if (_updatingScrollBars)
        {
            return;
        }
        _layout.FirstRow = _layout.ClampFirstRow((int)Math.Round(_verticalBar.Value));
        _layout.ScrollX = _layout.ClampScrollX((int)Math.Round(_horizontalBar.Value));
        InvalidateVisual();
        // As CodeEditorPP.OnScroll: the hint follows the caret.
        _completion.RepositionHint();
    }

    private void SetFirstRow(int row)
    {
        _layout.FirstRow = _layout.ClampFirstRow(row);
        _updatingScrollBars = true;
        try
        {
            _verticalBar.Value = _layout.FirstRow;
        }
        finally
        {
            _updatingScrollBars = false;
        }
        InvalidateVisual();
        _completion?.RepositionHint();
    }

    private void UpdateScrollBars()
    {
        if (_updatingLayout)
        {
            return;
        }
        _updatingLayout = true;
        try
        {
            _layout.ChooseScrollBars(out bool needVertical, out bool needHorizontal);
            Visibility vertical = needVertical ? Visibility.Visible : Visibility.Collapsed;
            Visibility horizontal = needHorizontal ? Visibility.Visible : Visibility.Collapsed;
            if (_verticalBar.Visibility != vertical || _horizontalBar.Visibility != horizontal)
            {
                _verticalBar.Visibility = vertical;
                _horizontalBar.Visibility = horizontal;
                InvalidateArrange();
            }
            UpdateScrollBarValues();
        }
        finally
        {
            _updatingLayout = false;
        }
    }

    // A WPF scroll bar goes from 0 to the last first row (or offset), with the visible part as its viewport.
    private void UpdateScrollBarValues()
    {
        _updatingScrollBars = true;
        try
        {
            int rowCount = _layout.RowCount;
            int visibleRows = _layout.VisibleRows;
            _verticalBar.Minimum = 0;
            _verticalBar.Maximum = Math.Max(0, rowCount - visibleRows);
            _verticalBar.ViewportSize = Math.Max(1, visibleRows);
            _verticalBar.LargeChange = Math.Max(1, visibleRows);
            _verticalBar.SmallChange = 1;
            _layout.FirstRow = _layout.ClampFirstRow(_layout.FirstRow);
            _verticalBar.Value = _layout.FirstRow;

            int width = _layout.VisibleWidth;
            _horizontalBar.Minimum = 0;
            _horizontalBar.Maximum = _wordWrap ? 0 : Math.Max(0, _layout.ContentWidth - width);
            _horizontalBar.ViewportSize = Math.Max(1, width);
            _horizontalBar.LargeChange = Math.Max(1, width);
            _horizontalBar.SmallChange = Math.Max(1, _layout.CharWidth);
            _layout.ScrollX = _layout.ClampScrollX(_layout.ScrollX);
            _horizontalBar.Value = _layout.ScrollX;
        }
        finally
        {
            _updatingScrollBars = false;
        }
        InvalidateVisual();
        _completion?.RepositionHint();
    }

    // ---- changes

    private void OnModifiedChanged()
    {
        ModifiedChanged?.Invoke(this, EventArgs.Empty);
        OnStateChanged();
    }

    private void OnStateChanged()
    {
        UpdatePairHighlight();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- drawing (in device pixels: the drawing is scaled back by the DPI)

    private SolidColorBrush Brush(RgbColor color)
    {
        if (!_brushes.TryGetValue(color.Argb, out SolidColorBrush brush))
        {
            brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
            brush.Freeze();
            _brushes[color.Argb] = brush;
        }
        return brush;
    }

    private static RgbColor FromWpf(Color color)
    {
        return RgbColor.FromArgb(color.A, color.R, color.G, color.B);
    }

    // The coloured parts of a line: its tokens with the colour of their type.
    private List<TextRun> LineRuns(int line)
    {
        _lineRuns.Clear();
        if (line < _tokens.LineCount)
        {
            foreach (TokenCache.Token token in _tokens.GetLineTokens(line))
            {
                _lineRuns.Add(new TextRun(token.Start, token.End, SyntaxColoring.ColorIndex(token.Type, _tokenColors.Length)));
            }
        }
        return _lineRuns;
    }

    // Draws the characters from..to of a row text from x on the grid of the characters, the top of the row at y; only the ones in view.
    // The matches of the query of the search bar in a row, as ReadonlyTextBrowser.PaintMatches (a matcher that throws stops the highlight).
    private void DrawMatches(DrawingContext context, int line, int rowStart, int rowEndColumn, int firstColumn, int visibleColumns, int origin, int textLeft, int y)
    {
        TextMatcher matcher = _matchHighlighter;
        if (matcher == null)
        {
            return;
        }
        try
        {
            int rowEnd = _wordWrap ? rowEndColumn : int.MaxValue;
            TextSearch.RowMatches(_document, line, matcher, _tabSize, firstColumn, rowEnd, _wordWrap ? rowEnd : firstColumn + visibleColumns + 1, _matchSpans);
        }
        catch (Exception)
        {
            _matchHighlighter = null;
            return;
        }
        int charWidth = _layout.CharWidth;
        foreach (MatchSpan span in _matchSpans)
        {
            if (span.EndColumn > span.StartColumn)
            {
                context.DrawRectangle(Brush(MatchHighlightColor), null, new Rect(textLeft + (span.StartColumn - origin) * charWidth, y, (span.EndColumn - span.StartColumn) * charWidth, _layout.RowHeight));
            }
        }
    }

    // The highlighted ranges in a row: the fill and a frame of a pixel, in the colours of their styles (an empty colour draws nothing).
    private void DrawHighlights(DrawingContext context, int row, int textLeft, int y)
    {
        int charWidth = _layout.CharWidth;
        foreach (TextHighlight highlight in _highlights.Values)
        {
            TextDocument.TextPosition start = highlight.Start < highlight.End ? highlight.Start : highlight.End;
            TextDocument.TextPosition end = highlight.Start < highlight.End ? highlight.End : highlight.Start;
            if (!_layout.SelectedColumns(row, start, end, out int from, out int to, out _) || to <= from)
            {
                continue;
            }
            Rect rect = new Rect(textLeft + from * charWidth, y, (to - from) * charWidth, _layout.RowHeight);
            if (!highlight.Style.FillColor.IsEmpty)
            {
                context.DrawRectangle(Brush(highlight.Style.FillColor), null, rect);
            }
            if (!highlight.Style.BorderColor.IsEmpty)
            {
                Pen pen = new Pen(Brush(highlight.Style.BorderColor), 1);
                context.DrawRectangle(null, pen, new Rect(rect.X + 0.5, rect.Y + 0.5, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1)));
            }
        }
    }

    // The grey line under the tabs of a row (ShowTabs), at the middle of the row, one line for each run of tabs, as the WinForms view draws it.
    private void DrawTabLines(DrawingContext context, int line, int rowStart, int rowEndColumn, int origin, int textLeft, int y)
    {
        string text = _document.GetLineText(line);
        int charWidth = _layout.CharWidth;
        double lineY = y + _layout.RowHeight / 2 + 0.5;
        Pen pen = new Pen(Brush(BlankMarkColor), 1);
        int column = 0;
        int runStart = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool tab = i < text.Length && text[i] == '\t';
            if (tab && runStart == -1)
            {
                runStart = column;
            }
            else if (!tab && runStart != -1)
            {
                int from = Math.Max(runStart, rowStart);
                int to = Math.Min(column, rowEndColumn);
                if (to > from)
                {
                    context.DrawLine(pen, new Point(textLeft + (from - origin) * charWidth, lineY), new Point(textLeft + (to - origin) * charWidth, lineY));
                }
                runStart = -1;
            }
            if (i < text.Length)
            {
                column = tab ? column + _tabSize - column % _tabSize : column + 1;
            }
        }
    }

    private void DrawText(DrawingContext context, string text, int from, int to, int x, int y, int left, int right, RgbColor color)
    {
        int charWidth = _layout.CharWidth;
        // The columns of the row that are in view.
        from = Math.Max(from, (left - x) / charWidth - 1);
        to = Math.Min(to, (right - x) / charWidth + 1);
        if (_glyphTypeface == null || from >= to)
        {
            return;
        }
        int count = to - from;
        ushort[] glyphs = new ushort[count];
        double[] advances = new double[count];
        IDictionary<int, ushort> map = _glyphTypeface.CharacterToGlyphMap;
        map.TryGetValue('?', out ushort missing);
        for (int i = 0; i < count; i++)
        {
            glyphs[i] = map.TryGetValue(text[from + i], out ushort glyph) ? glyph : missing;
            advances[i] = charWidth;
        }
        int textY = y + Math.Max(0, (_layout.RowHeight - _layout.TextHeight) / 2);
        GlyphRun run = new GlyphRun(_glyphTypeface, 0, false, _emSize, (float)_scale, glyphs, new Point(x + from * charWidth, textY + _baseline), advances, null, null, null, null, null, null);
        context.DrawGlyphRun(Brush(color), run);
    }

    private static readonly RgbColor FoldLabelGrey = RgbColor.FromArgb(255, 169, 169, 169);

    private Pen _framePen;

    private Pen FramePen()
    {
        if (_framePen == null)
        {
            _framePen = new Pen(Brush(FoldLabelGrey), 1);
            _framePen.Freeze();
        }
        return _framePen;
    }

    // The marks of the folds, as FoldingMarginPainter draws them, in device pixels: a line down the margin, a box with a minus at a block that
    // can be folded (a plus when it is folded), a stroke to the right at the end of a block. While the lines are wrapped every row is plain.
    private void DrawFoldMarks(DrawingContext context, int height)
    {
        int left = _layout.NumbersWidth;
        int rowHeight = _layout.RowHeight;
        int unit = Math.Max(1, (int)Math.Round(_scale));
        int box = 10 * unit;
        int xCenter = left + unit + 5 * unit;
        Brush pen = Brush(RgbColor.FromArgb(170, _foreColor.R, _foreColor.G, _foreColor.B));
        int rowCount = _layout.RowCount;
        for (int row = _layout.FirstRow; row < rowCount; row++)
        {
            int y = (row - _layout.FirstRow) * rowHeight;
            if (y >= height)
            {
                break;
            }
            FoldingManager.GutterState state = FoldingManager.GutterState.Plain;
            if (!_wordWrap)
            {
                state = _folding.GetGutterState(_layout.RowToLine(row, out _, out _));
            }
            int boxTop = y + (rowHeight - box) / 2;
            switch (state)
            {
                case FoldingManager.GutterState.Plain:
                case FoldingManager.GutterState.SectionEnd:
                    context.DrawRectangle(pen, null, new Rect(xCenter, y, unit, rowHeight));
                    if (state == FoldingManager.GutterState.SectionEnd)
                    {
                        context.DrawRectangle(pen, null, new Rect(xCenter, y + rowHeight / 2, left + _layout.FoldMarginWidth - xCenter, unit));
                    }
                    break;
                case FoldingManager.GutterState.Expandable:
                case FoldingManager.GutterState.Collapsed:
                    context.DrawRectangle(pen, null, new Rect(xCenter, y, unit, boxTop - y));
                    context.DrawRectangle(pen, null, new Rect(xCenter, boxTop + box, unit, y + rowHeight - boxTop - box));
                    // The box: four sides one device pixel thick.
                    context.DrawRectangle(pen, null, new Rect(xCenter - 5 * unit, boxTop, box + unit, unit));
                    context.DrawRectangle(pen, null, new Rect(xCenter - 5 * unit, boxTop + box, box + unit, unit));
                    context.DrawRectangle(pen, null, new Rect(xCenter - 5 * unit, boxTop, unit, box));
                    context.DrawRectangle(pen, null, new Rect(xCenter + 5 * unit, boxTop, unit, box));
                    context.DrawRectangle(pen, null, new Rect(xCenter - 3 * unit, boxTop + 5 * unit, 7 * unit, unit));
                    if (state == FoldingManager.GutterState.Collapsed)
                    {
                        context.DrawRectangle(pen, null, new Rect(xCenter, boxTop + 2 * unit, unit, box - 4 * unit + unit));
                    }
                    break;
            }
        }
    }

    // The row of a message as InlineMessageView draws it: the band under the column of the message (or in the middle of the row), the icon of an
    // error or a warning, the text in the colour of its kind in a serif font of five eighths of the row, and the frame of the band.
    private void DrawMessageRow(DrawingContext context, int line, int y, int width)
    {
        if (!_messages.TryGetMessage(line, out InlineMessageProvider.InlineMessage message))
        {
            return;
        }
        int rowHeight = _layout.RowHeight;
        int left = _layout.TextLeft;
        int charWidth = _layout.CharWidth;
        int scrollColumn = _wordWrap ? 0 : _layout.ScrollX / charWidth;
        int indentColumn = InlineMessageBand.IndentColumn(message.Column, _document.IndexToColumn(line, message.Column, _tabSize), scrollColumn);
        // The size of the WinForms font: rowHeight * 5 / 8 points, at the DPI of the device.
        double emSize = (rowHeight * 5 / 8) * 96.0 / 72.0;
        bool error = message.Severity == InlineMessageProvider.MessageSeverity.Error;
        FormattedText text = new FormattedText(message.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Times New Roman"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), emSize,
            Brush(error ? _messageErrorColor : _messageWarningColor), _scale);
        InlineMessageBand band = InlineMessageBand.Place(left, Math.Max(1, width - left), rowHeight, (float)text.WidthIncludingTrailingWhitespace, indentColumn, charWidth);
        context.DrawRectangle(Brush(_messageBackColor), null, new Rect(band.BandLeft, y, band.BandWidth, rowHeight));
        LoadIcons();
        ImageSource icon = error ? _errorIcon : _warningIcon;
        if (icon != null)
        {
            context.DrawImage(icon, new Rect(band.IconLeft, y, rowHeight, rowHeight));
        }
        text.MaxTextWidth = Math.Max(1, band.TextWidth);
        text.MaxLineCount = 1;
        context.PushClip(new RectangleGeometry(new Rect(band.TextLeft, y, Math.Max(0, band.TextWidth), rowHeight)));
        context.DrawText(text, new Point(band.TextLeft, y));
        context.Pop();
        Pen border = new Pen(Brush(_messageBorderColor), 1);
        border.Freeze();
        context.DrawRectangle(null, border, new Rect(band.BandLeft + 0.5, y + 0.5, band.BandWidth, rowHeight - 1));
    }

    // The icons of the WinForms view (its resources), with their magenta made transparent as its colour key does.
    private void LoadIcons()
    {
        if (_iconsLoaded)
        {
            return;
        }
        _iconsLoaded = true;
        _errorIcon = LoadIcon("message-error");
        _warningIcon = LoadIcon("message-warning");
    }

    private static ImageSource LoadIcon(string name)
    {
        try
        {
            using (System.IO.Stream stream = typeof(WpfTextEditor).Assembly.GetManifestResourceStream("SmarTTY.Icons." + name + ".png"))
            {
                if (stream == null)
                {
                    return null;
                }
                System.Windows.Media.Imaging.BitmapSource source = System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                System.Windows.Media.Imaging.FormatConvertedBitmap bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                int stride = bgra.PixelWidth * 4;
                byte[] pixels = new byte[stride * bgra.PixelHeight];
                bgra.CopyPixels(pixels, stride, 0);
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i] == 255 && pixels[i + 1] == 0 && pixels[i + 2] == 255)
                    {
                        pixels[i + 3] = 0;
                    }
                }
                System.Windows.Media.Imaging.BitmapSource image = System.Windows.Media.Imaging.BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                image.Freeze();
                return image;
            }
        }
        catch (Exception)
        {
            // No icon: the message is drawn without it.
            return null;
        }
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRectangle(Brush(_backColor), null, new Rect(0, 0, ActualWidth, ActualHeight));
        context.PushTransform(new ScaleTransform(1 / _scale, 1 / _scale));
        try
        {
            Render(context);
        }
        finally
        {
            context.Pop();
        }
    }

    private void Render(DrawingContext context)
    {
        int width = _layout.VisibleWidth;
        int height = _layout.VisibleHeight;
        int gutter = _layout.GutterWidth;
        int charWidth = _layout.CharWidth;
        int rowHeight = _layout.RowHeight;
        int rowCount = _layout.RowCount;
        int textLeft = _layout.TextLeft - (_wordWrap ? 0 : _layout.ScrollX);
        bool focused = IsKeyboardFocused;
        RgbColor highlight = FromWpf(SystemColors.HighlightColor);
        RgbColor selection = focused ? highlight : HexByteText.Blend(_backColor, highlight, 0.45f);
        RgbColor selectionText = focused ? FromWpf(SystemColors.HighlightTextColor) : _foreColor;
        bool selected = HasSelection && SelectionStart != _caret;

        context.PushClip(new RectangleGeometry(new Rect(gutter, 0, Math.Max(0, width - gutter), height)));
        try
        {
            for (int row = _layout.FirstRow; row < rowCount; row++)
            {
                int y = (row - _layout.FirstRow) * rowHeight;
                if (y >= height)
                {
                    break;
                }
                if (_layout.IsMessageRow(row))
                {
                    DrawMessageRow(context, _layout.RowToLine(row, out _, out _), y, width);
                    continue;
                }
                string text = _layout.RowText(row);
                int line = _layout.RowToLine(row, out int rowStart, out int rowEndColumn);
                // The x of a column of the line in this row is textLeft + (column - origin) * charWidth.
                int origin = _wordWrap ? rowStart : 0;
                int firstColumn = _wordWrap ? rowStart : _layout.ScrollX / charWidth;
                int visibleColumns = Math.Max(1, (width - _layout.TextLeft) / charWidth + 1);
                DrawMatches(context, line, rowStart, rowEndColumn, firstColumn, visibleColumns, origin, textLeft, y);
                DrawHighlights(context, row, textLeft, y);
                int selectedFrom = 0;
                int selectedTo = 0;
                if (selected && _layout.SelectedColumns(row, SelectionStart, _caret, out selectedFrom, out selectedTo, out bool pastEnd))
                {
                    // The line break is drawn as one cell after the line.
                    int selectedWidth = (selectedTo - selectedFrom + (pastEnd ? 1 : 0)) * charWidth;
                    context.DrawRectangle(Brush(selection), null, new Rect(textLeft + selectedFrom * charWidth, y, selectedWidth, rowHeight));
                }
                // The colours of the syntax, and the selected part of each run in the colour of a selected text.
                foreach (TextRun run in _layout.RowRuns(row, LineRuns(_layout.RowToLine(row, out _, out _))))
                {
                    RgbColor color = (run.Color < 0 || run.Color >= _tokenColors.Length) ? _foreColor : _tokenColors[run.Color];
                    if (run.Color == TextViewLayout.FoldLabelColor)
                    {
                        // The label of a folded block: grey, in a grey frame (as FoldingLineProvider draws it).
                        color = FoldLabelGrey;
                        context.DrawRectangle(null, FramePen(), new Rect(textLeft + run.From * charWidth + 0.5, y + 0.5, (run.To - run.From) * charWidth - 1, rowHeight - 1));
                    }
                    int from = Math.Min(run.To, Math.Max(run.From, selectedFrom));
                    int to = Math.Max(from, Math.Min(run.To, selectedTo));
                    DrawText(context, text, run.From, from, textLeft, y, gutter, width, color);
                    DrawText(context, text, from, to, textLeft, y, gutter, width, selectionText);
                    DrawText(context, text, to, run.To, textLeft, y, gutter, width, color);
                }
                if (_showTabs || _showAllCharacters)
                {
                    DrawTabLines(context, line, rowStart, rowEndColumn, origin, textLeft, y);
                }
                if (_showAllCharacters)
                {
                    // As ReadonlyTextBrowser.PaintInvisibleCharacters: the columns in view (of the row, when wrapped), the line break but the last.
                    int lastColumn = _wordWrap ? Math.Min(rowEndColumn - 1, rowStart + visibleColumns - 1) : firstColumn + visibleColumns - 1;
                    BlankMarks.LineMarks(_document.GetLineText(line), _tabSize, firstColumn, lastColumn, line < _document.LineCount - 1, _blankMarks);
                    foreach (BlankMark mark in _blankMarks)
                    {
                        DrawText(context, mark.Marker, 0, mark.Marker.Length, textLeft + (mark.Column - origin) * charWidth, y, gutter, width, BlankMarkColor);
                    }
                }
            }
            if (focused && _caretVisible)
            {
                _layout.PositionToPoint(_caret, out int caretX, out int caretY);
                if (caretY >= 0 && caretY < height)
                {
                    if (_overwrite)
                    {
                        // Overwrite: a block over the character that typing replaces.
                        context.DrawRectangle(Brush(RgbColor.FromArgb(110, _foreColor.R, _foreColor.G, _foreColor.B)), null, new Rect(caretX, caretY, charWidth, rowHeight));
                    }
                    else
                    {
                        int caretWidth = Math.Max(1, (int)Math.Round(1.5 * _scale));
                        context.DrawRectangle(Brush(_foreColor), null, new Rect(caretX, caretY, caretWidth, rowHeight));
                    }
                }
            }
        }
        finally
        {
            context.Pop();
        }

        if (gutter > 0)
        {
            context.DrawRectangle(Brush(_gutterBackColor), null, new Rect(0, 0, gutter, height));
            DrawFoldMarks(context, height);
            int caretLine = _caret.Line;
            for (int row = _layout.FirstRow; row < rowCount; row++)
            {
                int y = (row - _layout.FirstRow) * rowHeight;
                if (y >= height)
                {
                    break;
                }
                string number = _layout.LineNumberText(row);
                if (number == null)
                {
                    continue;
                }
                // Right-aligned, with a blank cell before the marks; the caret line in the colour of the text.
                int numbersWidth = _layout.NumbersWidth;
                int x = numbersWidth - (number.Length + 1) * charWidth;
                bool caretRow = _layout.RowToLine(row, out _, out _) == caretLine;
                DrawText(context, number, 0, number.Length, x, y, 0, numbersWidth, caretRow ? _foreColor : _gutterForeColor);
            }
        }
    }
}
