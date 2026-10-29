using SmarTTY.CodeEditing;
using SmarTTY.Completion;
using SmarTTY.TextEditing;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class CodeEditorPP : TextEditorControl, ICompletionHost
{
    public delegate void SuggestionAcceptedHandler(CodeEditorPP codeEditorPP, ISuggestion suggestion, Keys keys);

    internal delegate void TabHandler(ref bool handled);

    public delegate bool TokenPredicate(string text);

    public delegate void HoverHandler(TextDocument.TextPosition textPosition, ref Control control);

    private readonly TokenCache _tokenCache = new TokenCache();

    public Brush[] TokenBrushes;

    private InlineMessageProvider _inlineMessageProvider;

    private FoldingManager _foldingManager;

    public HighlightStyle ParenHighlightStyle = new HighlightStyle(new SolidBrush(Color.FromArgb(100, Color.LightYellow)), new Pen(Color.FromArgb(50, Color.Blue), 1f), null);

    public HighlightStyle BraceHighlightStyle = new HighlightStyle(new SolidBrush(Color.FromArgb(50, Color.LightGreen)), new Pen(Color.FromArgb(50, Color.Blue), 1f), null);

    public HighlightStyle BracketHighlightStyle = new HighlightStyle(new SolidBrush(Color.FromArgb(50, Color.LightBlue)), new Pen(Color.FromArgb(50, Color.Blue), 1f), null);

    public HighlightStyle AngleHighlightStyle = new HighlightStyle(new SolidBrush(Color.FromArgb(50, Color.LightGray)), new Pen(Color.FromArgb(50, Color.Blue), 1f), null);

    public HighlightStyle UnpairedHighlightStyle = new HighlightStyle(new SolidBrush(Color.FromArgb(100, Color.Red)), new Pen(Color.FromArgb(50, Color.Black), 1f), null);

    public HighlightStyle DefaultPairHighlightStyle = new HighlightStyle(null, null, null);

    protected KeyBindingMap _keyBindingMap = new KeyBindingMap(EditorCommands.All);

    private int _pairHighlightId = -1;

    public ImageList ImageList;

    private SuggestionController _suggestionController;

    // The list the completion popup offers. The catalog is the popup's source: it is filled with the built list
    // and the popup takes a snapshot of it.
    private readonly SuggestionCatalog _suggestionCatalog = new SuggestionCatalog();

    private SignatureHintController _signatureHintController;

    // The name of the signature the hint currently shows, so typing inside one call does not rebuild it.
    private string _signatureHintName;

    private SyntaxHighlighting.Language _completionLanguage = SyntaxHighlighting.Language.Plain;

    private bool _completionEnabled = true;

    private bool _completionOnTyping;

    // The standard API symbols (Python builtins, the C library, the shell builtins, ...). Null when the
    // feature is switched off, so nothing is loaded and the completion list stays keywords + document words.
    private ApiSymbolDatabase _apiSymbols;

    private bool _suppressHintClose;

    internal TabHandler _tabHandler;

    private bool _hoverPending;

    private Control _hoverControl;

    private Point _mousePoint;

    private DateTime _lastMouseMove;

    private TokenCache.Token _hoverToken;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IEnumerable<KeyValuePair<string, KeyBindingMap.KeyShortcut>> KeyBindings
    {
        get
        {
            List<KeyValuePair<string, KeyBindingMap.KeyShortcut>> list = new List<KeyValuePair<string, KeyBindingMap.KeyShortcut>>();
            foreach (KeyValuePair<KeyBindingMap.KeyShortcut, KeyBindingMap.EditorCommand> item in _keyBindingMap.Bindings)
            {
                list.Add(new KeyValuePair<string, KeyBindingMap.KeyShortcut>(item.Value.Name, item.Key));
            }
            return list;
        }
        set
        {
            _keyBindingMap.Bindings.Clear();
            foreach (KeyValuePair<string, KeyBindingMap.KeyShortcut> item in value)
            {
                _keyBindingMap.Bindings[item.Value] = _keyBindingMap.FindCommand(item.Key);
            }
        }
    }

    public Control HostControl => this;

    public string CurrentText
    {
        get
        {
            if (base.EditableDocument == null || CaretPosition.Line == -1)
            {
                return null;
            }
            // The identifier being typed: the completion popup filters its list by it.
            return GetWordAtCaret(out int ignoredColumn, out int ignoredLength) ?? "";
        }
    }

    public bool IsSuggestionVisible
    {
        get
        {
            if (_suggestionController != null)
            {
                return _suggestionController.IsVisible;
            }
            return false;
        }
    }

    public bool IsSignatureHintVisible
    {
        get
        {
            if (_signatureHintController != null)
            {
                return _signatureHintController.IsVisible;
            }
            return false;
        }
    }

    public Rectangle CaretScreenBounds
    {
        get
        {
            if (!base.TryGetCaretColumn(CaretPosition, out int caretColumn))
            {
                return RectangleToScreen(DisplayRectangle);
            }
            Point point = Point.Round(PositionToPoint(caretColumn, CaretPosition.Line));
            Rectangle rectangle = new Rectangle(point.X, point.Y, TextArea.Right - point.X, (int)((ReadonlyTextBrowser)this).CharSize.Height);
            return RectangleToScreen(rectangle);
        }
    }

    public float CharHeight => ((ReadonlyTextBrowser)this).CharSize.Height;

    public Color SuggestionBackColor { get; set; } = SystemColors.Window;

    public event TokenCache.LineTokenizer Tokenizing;

    public event SuggestionAcceptedHandler SuggestionAccepted;

    public event HoverHandler HoverRequested;

    public static bool IsWindows()
    {
        PlatformID platform = Environment.OSVersion.Platform;
        if (platform == PlatformID.Unix || platform == PlatformID.MacOSX)
        {
            return false;
        }
        return true;
    }

    public CodeEditorPP()
    {
        InitializeComponent();
        _inlineMessageProvider = new InlineMessageProvider();
        _foldingManager = new FoldingManager(_tokenCache);
        VirtualLineProviders.Add(_inlineMessageProvider);
        VirtualLineProviders.Add(_foldingManager);
        AddMargin(new FoldingManager.FoldingMargin(_foldingManager));
        // The completion popup reads its images from this list; the editor shows words without icons.
        ImageList = new ImageList();
        ImageList.ImageSize = new Size(16, 16);
        ImageList.ColorDepth = ColorDepth.Depth32Bit;
        SuggestionAccepted += OnSuggestionAccepted;
    }

    protected override void OnTextReplaced()
    {
        // OnTextReplaced may run from the base constructor, before the folding manager exists.
        _foldingManager?.Clear();
        base.OnTextReplaced();
        _tokenCache.SetDocument(base.EditableDocument, Tokenizing);
    }

    /// <summary>
    /// Rebuilds the tokens (e.g. after the highlighting settings changed) without treating the text as
    /// replaced, which would also wipe the undo history.
    /// </summary>
    public void Retokenize()
    {
        bool hadFolds = _foldingManager.Clear();
        _tokenCache.SetDocument(base.EditableDocument, Tokenizing);
        if (hadFolds)
        {
            RebuildDisplayRows();
            UpdateLayout();
        }
        Redraw();
    }

    /// <summary>
    /// Задаёт токенизатор для подсветки синтаксиса. null — отключить подсветку.
    /// Событие Tokenizing — событие (field-like) класса, поэтому присваивание
    /// допустимо только внутри CodeEditorPP; внешний код обращается через этот метод.
    /// </summary>
    public void SetTokenizer(TokenCache.LineTokenizer tokenizer)
    {
        Tokenizing = tokenizer;
        Retokenize();
    }

    protected override ColorSpan[] GetLineColorSpans(int line)
    {
        if (line >= _tokenCache.LineCount)
        {
            return null;
        }
        List<TokenCache.Token> list = _tokenCache.GetLineTokens(line);
        ColorSpan[] colorSpans = new ColorSpan[list.Count];
        for (int listIndex = 0; listIndex < list.Count; listIndex++)
        {
            colorSpans[listIndex].Start = list[listIndex].Start;
            colorSpans[listIndex].End = list[listIndex].End;
            if (TokenBrushes == null || TokenBrushes.Length == 0)
            {
                colorSpans[listIndex].Foreground = _defaultColors[0].Foreground;
            }
            else
            {
                colorSpans[listIndex].Foreground = ((list[listIndex].Type >= TokenBrushes.Length) ? TokenBrushes[0] : TokenBrushes[list[listIndex].Type]);
            }
        }
        return colorSpans;
    }

    protected override void OnLineChanged(int line, int column, int addedLength, int removedLength)
    {
        DateTime now = DateTime.Now;
        int linesRetokenized = _tokenCache.RetokenizeLine(line, Tokenizing);
        Value = (DateTime.Now - now).TotalMilliseconds;
        if (linesRetokenized > 1)
        {
            InvalidateLines(line, line + linesRetokenized - 1);
        }
        // The tokens of the line are new ones: a folded block that started or ended there refers to the old tokens.
        _foldingManager.RemoveStaleRegions(line, 0, 0, out bool staleRegionsRemoved);
        base.OnLineChanged(line, column, addedLength, removedLength);
        if (staleRegionsRemoved)
        {
            UpdateLayout();
        }
        CancelHover();
    }

    protected override void OnRangeChanged(TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd)
    {
        int changeStartLine = start.Line;
        int removedLineCount = oldEnd.Line - start.Line;
        int addedLineCount = newEnd.Line - start.Line;
        if (start.Column == 0 && oldEnd == start && newEnd.Line == start.Line + 1 && newEnd.Column == 0 && changeStartLine > 0)
        {
            changeStartLine--;
        }
        if (start.Column == 0 && newEnd == start && oldEnd.Line == start.Line + 1 && oldEnd.Column == 0 && changeStartLine > 0)
        {
            changeStartLine--;
        }
        _inlineMessageProvider.ShiftLines(changeStartLine, removedLineCount, addedLineCount);
        DateTime now = DateTime.Now;
        _tokenCache.OnLinesChanged(changeStartLine, removedLineCount, addedLineCount, Tokenizing);
        Value = (DateTime.Now - now).TotalMilliseconds;
        _foldingManager.RemoveStaleRegions(changeStartLine, removedLineCount, addedLineCount, out var staleRegionsRemoved);
        base.OnRangeChanged(start, oldEnd, newEnd);
        if (staleRegionsRemoved)
        {
            UpdateLayout();
        }
        CancelHover();
    }

    protected override void OnSelectionChanged(TextDocument.TextPosition previousCaret, TextDocument.TextPosition currentCaret, bool hasSelection, SelectionChangeSource selectionChangeSource)
    {
        CancelHover();
        if (!hasSelection)
        {
            TokenCache.Token token = _tokenCache.FindNearestToken(currentCaret.Line, currentCaret.Column);
            if (token == null || token.Kind != TokenCache.Token.TokenKind.Paired || CaretPosition.Line != token.LineNumber || CaretPosition.Column != token.End)
            {
                RemoveHighlight(_pairHighlightId);
                _pairHighlightId = -1;
            }
            else
            {
                string text = base.EditableDocument.GetLineText(token.LineNumber);
                string tokenText = "";
                if (token.End <= text.Length)
                {
                    tokenText = text.Substring(token.Start, token.End - token.Start);
                }
                if (tokenText.Length >= 1)
                {
                    HighlightStyle highlightStyle;
                    switch (tokenText[0])
                    {
                        case '(':
                        case ')':
                            highlightStyle = this.ParenHighlightStyle;
                            break;
                        case '[':
                        case ']':
                            highlightStyle = BracketHighlightStyle;
                            break;
                        case '{':
                        case '}':
                            highlightStyle = BraceHighlightStyle;
                            break;
                        case '<':
                        case '>':
                            highlightStyle = AngleHighlightStyle;
                            break;
                        default:
                            highlightStyle = DefaultPairHighlightStyle;
                            break;
                    }
                    TextDocument.TextPosition pairStart;
                    TextDocument.TextPosition pairEnd;
                    if (token.Partner == null)
                    {
                        highlightStyle = UnpairedHighlightStyle;
                        pairStart = token.StartPosition;
                        pairEnd = token.EndPosition;
                        if (pairStart < pairEnd)
                        {
                            pairEnd = token.StartPosition;
                            pairStart = token.EndPosition;
                        }
                    }
                    else if (token.StartPosition < token.Partner.StartPosition)
                    {
                        pairStart = token.StartPosition;
                        pairEnd = token.Partner.EndPosition;
                    }
                    else
                    {
                        pairStart = token.Partner.StartPosition;
                        pairEnd = token.EndPosition;
                    }
                    _pairHighlightId = SetHighlight(_pairHighlightId, pairStart, pairEnd, highlightStyle);
                }
            }
        }
        base.OnSelectionChanged(previousCaret, currentCaret, hasSelection, selectionChangeSource);
    }

    public void AddMessage(int line, InlineMessageProvider.MessageSeverity messageSeverity, string text, int column)
    {
        _inlineMessageProvider.AddMessage(line - 1, messageSeverity, text, column);
    }

    public void GoToPosition(int line, int column)
    {
        if (column < 0)
        {
            column = 0;
        }
        line = _inlineMessageProvider.GetCurrentLine(line);
        if (_foldingManager.ExpandAt(line, column))
        {
            RebuildDisplayRows();
            Redraw();
        }
        SetCaret(new TextDocument.TextPosition(column, line));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_keyBindingMap.Bindings.TryGetValue(new KeyBindingMap.KeyShortcut(e), out var value))
        {
            value.Execute(this);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Escape)
        {
            HideCompletion();
            HideSignatureHint();
            if (_hoverControl != null)
            {
                _hoverControl.Dispose();
                _hoverControl = null;
            }
        }
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
            case Keys.Home:
            case Keys.End:
            case Keys.Back:
            case Keys.Delete:
                UpdateSignatureHint();
                break;
        }
    }

    public void AcceptSuggestion(ISuggestion suggestion, Keys keys)
    {
        if (SuggestionAccepted != null)
        {
            SuggestionAccepted(this, suggestion, keys);
        }
    }

    public void ForwardMessage(Message message)
    {
        message.HWnd = Handle;
        if (!PreProcessMessage(ref message))
        {
            if (!IsWindows() && message.Msg == 258)
            {
                OnKeyPress(new KeyPressEventArgs((char)(byte)(int)message.WParam));
            }
            else
            {
                WndProc(ref message);
            }
        }
    }

    /// <summary>The words of this language are offered by the completion popup (set by the code that picks the tokenizer).</summary>
    public SyntaxHighlighting.Language CompletionLanguage
    {
        get
        {
            return _completionLanguage;
        }
        set
        {
            _completionLanguage = value;
            if (_suggestionController != null && _suggestionController.IsVisible)
            {
                HideCompletion();
            }
        }
    }

    /// <summary>Enables the completion popup (Ctrl+Space and, with <see cref="CompletionOnTyping"/>, typing).</summary>
    public bool CompletionEnabled
    {
        get
        {
            return _completionEnabled;
        }
        set
        {
            _completionEnabled = value;
            if (!value)
            {
                HideCompletion();
            }
        }
    }

    /// <summary>Opens the popup by itself once a couple of letters of an identifier have been typed.</summary>
    public bool CompletionOnTyping
    {
        get
        {
            return _completionOnTyping;
        }
        set
        {
            _completionOnTyping = value;
        }
    }

    /// <summary>The number of typed letters that open the popup by themselves.</summary>
    private const int AutoTriggerLength = 2;

    /// <summary>
    /// The standard API symbols offered in the completion list and used by the parameter hint. Set from
    /// the settings; null switches the feature off (keywords and document words only).
    /// </summary>
    public ApiSymbolDatabase ApiSymbols
    {
        get
        {
            return _apiSymbols;
        }
        set
        {
            _apiSymbols = value;
        }
    }

    /// <summary>Shows the completion popup for the word the caret is in (Ctrl+Space, or typing).</summary>
    public void ShowCompletion()
    {
        if (!_completionEnabled || base.EditableDocument == null)
        {
            return;
        }
        // The whole list is passed to the popup: it narrows itself down to the typed prefix, and can widen
        // again when letters are deleted, which a list filtered here could not.
        List<ISuggestion> items = EditorCompletionSource.Build(_completionLanguage, base.EditableDocument.GetText(), "", _apiSymbols);
        if (items.Count == 0)
        {
            return;
        }
        HideSignatureHint();
        ShowSuggestions(items);
    }

    /// <summary>Hides the completion popup, if it is open.</summary>
    public void HideCompletion()
    {
        if (_suggestionController != null)
        {
            _suggestionController.Hide();
        }
    }

    /// <summary>The user accepted an item of the popup: the word replaces the one at the caret.</summary>
    private void OnSuggestionAccepted(CodeEditorPP codeEditorPP, ISuggestion suggestion, Keys keys)
    {
        if (suggestion != null)
        {
            ReplaceWordAtCaret(suggestion.DisplayName);
        }
    }

    protected override void OnCharTyped(char ch, TextDocument.TextPosition textPosition)
    {
        base.OnCharTyped(ch, textPosition);
        if (_completionOnTyping && _completionEnabled && !IsSuggestionVisible
            && EditorCompletionSource.IsIdentifierChar(ch) && (Control.ModifierKeys & Keys.Control) == 0)
        {
            string word = CurrentText;
            if (word != null && word.Length >= AutoTriggerLength)
            {
                ShowCompletion();
            }
        }
        // Only these characters change the call the caret is in; other letters leave the hint alone.
        if (ch == '(' || ch == ',' || ch == ')')
        {
            UpdateSignatureHint();
        }
    }

    public void ShowSuggestions(IEnumerable<ISuggestion> items)
    {
        if (_suggestionController == null)
        {
            _suggestionController = new SuggestionController(this);
        }
        _suggestionController.BackColor = SuggestionBackColor;
        // The catalog is the source of the popup. The whole list is handed over (no prefix): the popup narrows it
        // to the typed word itself and can widen it again when letters are deleted.
        _suggestionCatalog.Clear();
        _suggestionCatalog.Load(items);
        _suggestionController.Show(_suggestionCatalog, null, autoSize: true, allowMultiSelect: false);
        UpdateSuggestions();
    }

    protected override void OnScroll(ScrollEventArgs e)
    {
        base.OnScroll(e);
        if (IsSignatureHintVisible)
        {
            _signatureHintController.Reposition();
        }
        CancelHover();
    }

    public void ShowSignatureHint(ISignatureInfo signatureInfo, int parameterIndex, string text)
    {
        HideSignatureHint();
        _signatureHintController = new SignatureHintController(this);
        _suppressHintClose = true;
        Control control = null;
        if (!IsWindows())
        {
            control = this;
        }
        _signatureHintController.Show(signatureInfo, parameterIndex, (text == null) ? null : ("Press TAB to " + text), control);
        Focus();
        _suppressHintClose = false;
    }

    protected override void OnLostFocus(EventArgs e)
    {
        if (!_suppressHintClose)
        {
            HideSignatureHint();
        }
        base.OnLostFocus(e);
    }

    public void HideSignatureHint()
    {
        _signatureHintName = null;
        if (_signatureHintController != null)
        {
            _signatureHintController.Hide();
            _signatureHintController = null;
        }
    }

    /// <summary>
    /// Shows, updates or hides the parameter hint for the call the caret is inside. The definitions are
    /// read from the document, so the hint appears as soon as an opening parenthesis is typed after a
    /// function the file defines.
    /// </summary>
    private void UpdateSignatureHint()
    {
        if (!_completionEnabled || base.EditableDocument == null || CaretPosition.Line < 0)
        {
            HideSignatureHint();
            return;
        }
        // The word list and the parameter hint would overlap; the list wins while it is open.
        if (IsSuggestionVisible)
        {
            HideSignatureHint();
            return;
        }
        ParameterSignature signature = EditorSignatureSource.FindSignature(
            _completionLanguage, base.EditableDocument.GetText(), CaretPosition.Line, CaretPosition.Column, _apiSymbols, out int activeParameter);
        if (signature == null)
        {
            HideSignatureHint();
            return;
        }
        if (IsSignatureHintVisible && _signatureHintName == signature.Name)
        {
            SetActiveParameter(activeParameter);
            return;
        }
        ShowSignatureHint(signature, activeParameter, null);
        _signatureHintName = signature.Name;
    }

    protected virtual bool IsWordSeparator(char ch)
    {
        return "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".IndexOf(ch) == -1;
    }

    protected override void OnCharTyping(char ch, ref string text, ref TextDocument.TextPosition textPosition)
    {
        if (_suggestionController != null && _suggestionController.IsVisible && IsWordSeparator(ch) && (Control.ModifierKeys & Keys.Control) == 0)
        {
            _suggestionController.AcceptSelectedIfMatches(CurrentText);
        }
        base.OnCharTyping(ch, ref text, ref textPosition);
    }

    private void UpdateSuggestions()
    {
        if (_suggestionController != null && _suggestionController.IsVisible)
        {
            string text = CurrentText;
            if (text != null && text.Length > 0 && text[0] >= '0' && text[0] <= '9')
            {
                _suggestionController.Hide();
            }
            else
            {
                _suggestionController.SelectMatch(text, exactMatch: true);
            }
        }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (e.KeyChar == '\t' && _tabHandler != null)
        {
            bool popupActive = false;
            _tabHandler(ref popupActive);
            if (popupActive)
            {
                return;
            }
        }
        base.OnKeyPress(e);
        if (!char.IsControl(e.KeyChar) || e.KeyChar == '\b')
        {
            UpdateSuggestions();
        }
    }

    public void SetPopupActive(bool active)
    {
        ShowCaretWhenUnfocused = active;
        if (!active)
        {
            Focus();
        }
    }

    public void SetActiveParameter(int parameterIndex)
    {
        if (IsSignatureHintVisible)
        {
            _signatureHintController.SetActiveParameter(parameterIndex);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!IsWindows())
        {
            Focus();
        }
    }

    public bool MoveToToken(bool searchPrevious, bool isOpening, bool selectText, TokenPredicate callback)
    {
        if (_tokenCache == null)
        {
            return false;
        }
        int step = ((!searchPrevious) ? 1 : (-1));
        int limitLine = (searchPrevious ? (-1) : LineCount);
        List<TokenCache.Token> tokens = _tokenCache.GetLineTokens(CaretPosition.Line);
        int tokenIdx = _tokenCache.FindTokenIndex(tokens, CaretPosition.Column, (!searchPrevious) ? TokenCache.TokenSearchMode.Next : TokenCache.TokenSearchMode.Previous);
        int currentLine = CaretPosition.Line;
        if (tokenIdx == -1)
        {
            currentLine += step;
        }
        for (int line = currentLine; line != limitLine; line += step)
        {
            tokens = _tokenCache.GetLineTokens(line);
            int limitToken = (searchPrevious ? (-1) : tokens.Count);
            if (tokenIdx == -1)
            {
                tokenIdx = tokens.Count - 1;
            }
            bool jumped = false;
            for (; tokenIdx != limitToken && tokenIdx != -1 && tokenIdx < tokens.Count; tokenIdx += step)
            {
                TokenCache.Token token = tokens[tokenIdx];
                if (token.Partner == null)
                {
                    continue;
                }
                if ((token.Partner.StartPosition > token.StartPosition) != searchPrevious)
                {
                    tokenIdx = token.Partner.Index + step;
                    line = token.Partner.LineNumber - step;
                    jumped = true;
                    break;
                }
                string text = GetLineOrNull(token.LineNumber).Substring(token.Start, token.End - token.Start);
                if (callback(text))
                {
                    TextDocument.TextPosition targetPos = ((searchPrevious == isOpening) ? token.StartPosition : token.EndPosition);
                    if (!(targetPos == CaretPosition))
                    {
                        SetSelection(selectText, CaretPosition);
                        SetCaret(targetPos);
                        return true;
                    }
                }
            }
            if (!jumped)
            {
                tokenIdx = (searchPrevious ? (-1) : 0);
            }
        }
        return false;
    }

    public void ExpandCurrentFold()
    {
        if (IsWrapping)
        {
            return;
        }
        if (_foldingManager.ExpandLine(CaretPosition.Line))
        {
            UpdateLayout();
            Redraw();
        }
    }

    public void CollapseCurrentFold()
    {
        if (IsWrapping)
        {
            return;
        }
        if (_foldingManager.CollapseLine(CaretPosition.Line, this))
        {
            UpdateLayout();
            Redraw();
        }
    }

    public FoldedLineInfo[] GetFoldState()
    {
        return _foldingManager.GetCollapsedRegions();
    }

    public void RestoreFoldState(FoldedLineInfo[] items)
    {
        foreach (FoldedLineInfo foldedLineInfo in items)
        {
            int foldedLine = foldedLineInfo.Line;
            if (foldedLine >= _tokenCache.LineCount || _tokenCache.Document.GetLineText(foldedLine) != foldedLineInfo.Text)
            {
                int matchIndex = -1;
                bool foundMatch = false;
                for (int documentLine = 0; documentLine < _tokenCache.Document.LineCount; documentLine++)
                {
                    if (_tokenCache.Document.GetLineText(documentLine) == foldedLineInfo.Text)
                    {
                        if (matchIndex != -1)
                        {
                            foundMatch = true;
                            break;
                        }
                        matchIndex = documentLine;
                    }
                }
                if (foundMatch)
                {
                    continue;
                }
                foldedLine = matchIndex;
            }
            if (foldedLine != -1)
            {
                _foldingManager.CollapseLine(foldedLine, this);
            }
        }
        UpdateLayout();
        Redraw();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _lastMouseMove = DateTime.Now;
        _mousePoint = e.Location;
        if (_hoverControl != null)
        {
            TextDocument.TextPosition textPosition = HitTest(_mousePoint);
            if (!textPosition.IsValid || _tokenCache.FindNearestToken(textPosition.Line, textPosition.Column) != _hoverToken)
            {
                _hoverControl.Dispose();
                _hoverControl = null;
            }
        }
        _hoverPending = true;
    }

    private void OnHoverTimerTick(object sender, EventArgs e)
    {
        if (!_hoverPending || !((DateTime.Now - _lastMouseMove).TotalMilliseconds >= 300.0) || HoverRequested == null)
        {
            return;
        }
        TextDocument.TextPosition textPosition = HitTest(_mousePoint);
        _hoverPending = false;
        if (!textPosition.IsValid)
        {
            return;
        }
        TokenCache.Token token = _tokenCache.FindNearestToken(textPosition.Line, textPosition.Column);
        if (token == _hoverToken && _hoverControl != null)
        {
            return;
        }
        _hoverToken = token;
        if (_hoverControl != null)
        {
            _hoverControl.Dispose();
            _hoverControl = null;
        }
        HoverRequested(textPosition, ref _hoverControl);
        if (_hoverControl != null)
        {
            _hoverControl.Parent = this;
            if (_hoverToken != null)
            {
                _hoverControl.Left = (int)PositionToPoint(base.EditableDocument.IndexToColumn(_hoverToken.LineNumber, _hoverToken.Start, TabSize), _hoverToken.LineNumber).X + 5;
            }
            else
            {
                _hoverControl.Left = _mousePoint.X + 10;
            }
            _hoverControl.Top = (int)(PositionToPoint(0, textPosition.Line).Y + ((ReadonlyTextBrowser)this).CharSize.Height) + 5;
            _hoverControl.Show();
        }
    }

    private void CancelHover()
    {
        _lastMouseMove = DateTime.Now;
        if (_hoverControl != null && _hoverControl.Visible)
        {
            _hoverControl.Dispose();
            _hoverControl = null;
        }
    }
}
