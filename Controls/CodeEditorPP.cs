using SmarTTY.CodeEditing;
using SmarTTY.Completion;
using SmarTTY.TextEditing;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class CodeEditorPP : TextEditorControl, ICompletionHost, IEditorCommandTarget
{
    public delegate void SuggestionAcceptedHandler(CodeEditorPP codeEditorPP, ISuggestion suggestion, Keys keys);

    internal delegate void TabHandler(ref bool handled);

    public delegate void HoverHandler(TextDocument.TextPosition textPosition, ref Control control);

    private readonly TokenCache _tokenCache = new TokenCache();

    public Brush[] TokenBrushes;

    private InlineMessageProvider _inlineMessageProvider;

    private InlineMessageView _inlineMessageView;

    private FoldingManager _foldingManager;

    public HighlightStyle ParenHighlightStyle = new HighlightStyle(Color.FromArgb(100, Color.LightYellow).ToRgb(), Color.FromArgb(50, Color.Blue).ToRgb(), RgbColor.Empty);

    public HighlightStyle BraceHighlightStyle = new HighlightStyle(Color.FromArgb(50, Color.LightGreen).ToRgb(), Color.FromArgb(50, Color.Blue).ToRgb(), RgbColor.Empty);

    public HighlightStyle BracketHighlightStyle = new HighlightStyle(Color.FromArgb(50, Color.LightBlue).ToRgb(), Color.FromArgb(50, Color.Blue).ToRgb(), RgbColor.Empty);

    public HighlightStyle AngleHighlightStyle = new HighlightStyle(Color.FromArgb(50, Color.LightGray).ToRgb(), Color.FromArgb(50, Color.Blue).ToRgb(), RgbColor.Empty);

    public HighlightStyle UnpairedHighlightStyle = new HighlightStyle(Color.FromArgb(100, Color.Red).ToRgb(), Color.FromArgb(50, Color.Black).ToRgb(), RgbColor.Empty);

    public HighlightStyle DefaultPairHighlightStyle = new HighlightStyle(RgbColor.Empty, RgbColor.Empty, RgbColor.Empty);

    protected KeyBindingMap _keyBindingMap = new KeyBindingMap(EditorCommands.All);

    private int _pairHighlightId = -1;

    public ImageList ImageList;

    // The word list and the parameter hint (made when first used: the base class constructors may type or move the caret).
    private EditorCompletion _completion;

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

    public bool IsSuggestionVisible => Completion.IsSuggestionVisible;

    public bool IsSignatureHintVisible => Completion.IsSignatureHintVisible;

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

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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
        _inlineMessageView = new InlineMessageView(_inlineMessageProvider);
        _foldingManager = new FoldingManager(_tokenCache);
        VirtualLineProviders.Add(_inlineMessageView);
        VirtualLineProviders.Add(new FoldingLineProvider(_foldingManager));
        AddMargin(new FoldingMarginPainter(_foldingManager));
        // The completion popup reads its images from this list; the editor shows words without icons.
        ImageList = new ImageList();
        ImageList.ImageSize = new Size(16, 16);
        ImageList.ColorDepth = ColorDepth.Depth32Bit;
        Disposed += (object sender, EventArgs e) => _inlineMessageView.Dispose();
        SuggestionAccepted += OnSuggestionAccepted;
    }

    protected override void OnTextReplaced()
    {
        // OnTextReplaced may run from the base constructor, before the folding manager exists.
        _foldingManager?.Clear();
        // The cursors were in the old text.
        ClearExtraCursors();
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
            colorSpans[listIndex].Foreground = (TokenBrushes == null || TokenBrushes.Length == 0)
                ? _defaultColors[0].Foreground
                : TokenBrushes[SyntaxColoring.ColorIndex(list[listIndex].Type, TokenBrushes.Length)];
        }
        return colorSpans;
    }

    protected override void OnLineChanged(int line, int column, int addedLength, int removedLength)
    {
        if (!_applyingMultiCursor)
        {
            ClearExtraCursors();
        }
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
        if (!_applyingMultiCursor)
        {
            ClearExtraCursors();
        }
        SyntaxColoring.ChangedLines(start, oldEnd, newEnd, out int changeStartLine, out int removedLineCount, out int addedLineCount);
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
        if (!_applyingMultiCursor)
        {
            // The caret was moved by something that knows of one cursor only (a find, a go to line, a click).
            ClearExtraCursors();
        }
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

    /// <summary>
    /// Adds one message under its line (line from 1, column from 1; a column of 0 means "not known" and centres the
    /// message in its row).
    /// </summary>
    public void AddMessage(int line, InlineMessageProvider.MessageSeverity messageSeverity, string text, int column)
    {
        // A line past the end, which is what a check reports for "unexpected end of file", goes on the last line.
        _inlineMessageProvider.AddReported(line, messageSeverity, text, column, LineCount);
        RefreshMessages();
    }

    /// <summary>
    /// Adds several messages (line from 1, column from 1, or 0 when it is not known) and rebuilds the rows once, so
    /// that a whole list of problems does not redraw the editor for each of them.
    /// </summary>
    public void AddMessages(IEnumerable<InlineMessageProvider.MessageRequest> messages)
    {
        foreach (InlineMessageProvider.MessageRequest message in messages)
        {
            // A message is drawn as a row under its line: the line and the column are 1-based here, and a column of 0 means
            // "not known" (the message is centred in the row).
            _inlineMessageProvider.AddReported(message.Line, message.Severity, message.Text, message.Column, LineCount);
        }
        RefreshMessages();
    }

    /// <summary>Removes the messages shown under the lines (they were answered or the text changed under them).</summary>
    public void ClearMessages()
    {
        // Called before every save: with no message there is nothing to lay out again.
        if (_inlineMessageProvider == null || _inlineMessageProvider.Count == 0)
        {
            return;
        }
        _inlineMessageProvider.Clear();
        RefreshMessages();
    }

    /// <summary>The theme sets the colours the inline messages are drawn with.</summary>
    public void ApplyMessageColors(Color background, Color error, Color warning, Color border)
    {
        _inlineMessageView.BackColor = background;
        _inlineMessageView.ErrorColor = error;
        _inlineMessageView.WarningColor = warning;
        _inlineMessageView.BorderColor = border;
        RefreshMessages();
    }

    private void RefreshMessages()
    {
        if (!IsHandleCreated)
        {
            // Nothing is laid out yet: the messages are drawn on the first paint.
            return;
        }
        RebuildDisplayRows();
        UpdateLayout();
        Redraw();
    }

    protected override void OnWordWrapChanged()
    {
        // A message is a row of its own under a line; the wrapped rows have no such rows, so the messages are
        // dropped when the wrapping is switched on or off (the provider does not take part in wrapped layout).
        ClearMessages();
    }

    public void GoToPosition(int line, int column)
    {
        if (column < 0)
        {
            column = 0;
        }
        if (_foldingManager.ExpandAt(line, column))
        {
            RebuildDisplayRows();
            Redraw();
        }
        SetCaret(new TextDocument.TextPosition(column, line));
    }

    /// <summary>
    /// Ctrl+click on an http, https or ftp address in the text opens it in the browser; while Ctrl is held over one, the pointer is a
    /// hand. A plain click places the caret as always, and Ctrl+Shift or Ctrl+Alt keep their own meaning.
    /// </summary>
    protected override HoverTarget GetHoverTarget(int line, Point point)
    {
        if ((Control.ModifierKeys & (Keys.Control | Keys.Shift | Keys.Alt)) == Keys.Control)
        {
            TextDocument.TextPosition position = HitTest(point);
            string text = position.IsValid ? GetLineOrNull(position.Line) : null;
            string url = text == null ? null : SmarTTY.Utilities.UrlLinks.FindAt(text, position.Column, out _);
            if (url != null)
            {
                return new HoverTarget
                {
                    Cursor = Cursors.Hand,
                    ClickAction = () => UrlOpener.Open(url)
                };
            }
        }
        return base.GetHoverTarget(line, point);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.ControlKey)
        {
            // Ctrl released over an address: the pointer is an I-beam again and a click places the caret.
            RefreshHoverTarget();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.ControlKey)
        {
            // Ctrl pressed while the mouse stands over an address: the pointer becomes a hand without a mouse move.
            RefreshHoverTarget();
        }
        if (_keyBindingMap.Bindings.TryGetValue(new KeyBindingMap.KeyShortcut(e.ToKeyStroke()), out var value))
        {
            if (HasMultipleCursors && !IsMultiCursorCommand(value))
            {
                // A command that works on lines or on the view is for one cursor.
                ClearExtraCursors();
            }
            value.Execute(this);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (HasMultipleCursors && HandleCursorKeyDown(e))
        {
            return;
        }
        if (e.KeyCode == Keys.Escape)
        {
            Completion.OnEscape();
            if (_hoverControl != null)
            {
                _hoverControl.Dispose();
                _hoverControl = null;
            }
        }
        base.OnKeyDown(e);
        Completion.AfterKeyDown(e.KeyCode.ToVirtualKey());
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
                OnKeyPress(new KeyPressEventArgs((char)(byte)(int)message.WParam.ToInt64()));
            }
            else
            {
                WndProc(ref message);
            }
        }
    }

    /// <summary>The words of this language are offered by the completion popup (set by the code that picks the tokenizer).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SyntaxHighlighting.Language CompletionLanguage
    {
        get
        {
            return Completion.Language;
        }
        set
        {
            Completion.Language = value;
        }
    }

    /// <summary>Enables the completion popup (Ctrl+Space and, with <see cref="CompletionOnTyping"/>, typing).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CompletionEnabled
    {
        get
        {
            return Completion.Enabled;
        }
        set
        {
            Completion.Enabled = value;
        }
    }

    /// <summary>Opens the popup by itself once a couple of letters of an identifier have been typed.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CompletionOnTyping
    {
        get
        {
            return Completion.OnTyping;
        }
        set
        {
            Completion.OnTyping = value;
        }
    }

    /// <summary>
    /// The standard API symbols offered in the completion list and used by the parameter hint. Set from
    /// the settings; null switches the feature off (keywords and document words only).
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ApiSymbolDatabase ApiSymbols
    {
        get
        {
            return Completion.ApiSymbols;
        }
        set
        {
            Completion.ApiSymbols = value;
        }
    }

    /// <summary>Shows the completion popup for the word the caret is in (Ctrl+Space, or typing).</summary>
    public void ShowCompletion()
    {
        Completion.ShowCompletion();
    }

    /// <summary>Hides the completion popup, if it is open.</summary>
    public void HideCompletion()
    {
        Completion.HideCompletion();
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
        Completion.OnCharTyped(ch, (Control.ModifierKeys & Keys.Control) != 0);
    }

    public void ShowSuggestions(IEnumerable<ISuggestion> items)
    {
        Completion.ShowSuggestions(items);
    }

    protected override void OnScroll(ScrollEventArgs e)
    {
        base.OnScroll(e);
        Completion.RepositionHint();
        CancelHover();
    }

    public void ShowSignatureHint(ISignatureInfo signatureInfo, int parameterIndex, string text)
    {
        Completion.ShowSignatureHint(signatureInfo, parameterIndex, text);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Completion.OnLostFocus();
        base.OnLostFocus(e);
    }

    public void HideSignatureHint()
    {
        Completion.HideSignatureHint();
    }

    protected virtual bool IsWordSeparator(char ch)
    {
        return "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".IndexOf(ch) == -1;
    }

    protected override void OnCharTyping(char ch, ref string text, ref TextDocument.TextPosition textPosition)
    {
        Completion.OnCharTyping(ch, (Control.ModifierKeys & Keys.Control) != 0);
        base.OnCharTyping(ch, ref text, ref textPosition);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (HasMultipleCursors && HandleCursorKeyPress(e))
        {
            return;
        }
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
        Completion.AfterKeyPress(e.KeyChar);
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
        Completion.SetActiveParameter(parameterIndex);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (HandleCursorMouseDown(e))
        {
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            // A click puts the caret in one place.
            ClearExtraCursors();
        }
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _columnAnchor = new SmarTTY.CodeEditing.TextPoint(CaretPosition.Line, CaretPosition.Column);
        }
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
        if (!TokenMoves.TryFind(_tokenCache, EditableDocument, CaretPosition, searchPrevious, isOpening, callback, out TextDocument.TextPosition target))
        {
            return false;
        }
        SetSelection(selectText, CaretPosition);
        SetCaret(target);
        return true;
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
        if (_foldingManager.CollapseLine(CaretPosition.Line))
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
        _foldingManager.RestoreFolds(items);
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
