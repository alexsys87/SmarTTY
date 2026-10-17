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

    private class SuggestionSource : ISuggestionSource
    {
        private CodeEditorPP _editor;

        private IEnumerable<ISuggestion> _items;

        public IEnumerable Images => _editor.ImageList.Images;

        public IEnumerable<ISuggestion> Items => _items;

        public SuggestionSource(CodeEditorPP codeEditorPP, IEnumerable<ISuggestion> items)
        {
            this._editor = codeEditorPP;
            this._items = items;
        }
    }

    internal delegate void TabHandler(ref bool flag);

    public delegate void PlaceholderChangedHandler(CodeEditorPP codeEditorPP, int value2, int value3, int value4, object obj, bool flag);

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

    private SignatureHintController _signatureHintController;

    private bool _suppressHintClose;

    internal TabHandler _tabHandler;

    internal TemplateSession _templateSession;

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
            return base.EditableDocument.GetLineText(CaretPosition.Line);
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
            if (!base.TryGetCaretColumn(CaretPosition, out int num))
            {
                return RectangleToScreen(DisplayRectangle);
            }
            Point point = Point.Round(PositionToPoint(num, CaretPosition.Line));
            Rectangle rectangle = new Rectangle(point.X, point.Y, TextArea.Right - point.X, (int)((ReadonlyTextBrowser)this).CharSize.Height);
            return RectangleToScreen(rectangle);
        }
    }

    public float CharHeight => ((ReadonlyTextBrowser)this).CharSize.Height;

    public Color SuggestionBackColor { get; set; } = SystemColors.Window;

    public event TokenCache.LineTokenizer Tokenizing;

    public event SuggestionAcceptedHandler SuggestionAccepted;

    public event PlaceholderChangedHandler PlaceholderChanged;

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
    }

    protected override void OnTextReplaced()
    {
        base.OnTextReplaced();
        _tokenCache.SetDocument(base.EditableDocument, Tokenizing);
    }

    public void Retokenize()
    {
        OnTextReplaced();
    }

    protected override ColorSpan[] GetLineColorSpans(int value2)
    {
        if (value2 >= _tokenCache.LineCount)
        {
            return null;
        }
        List<TokenCache.Token> list = _tokenCache.GetLineTokens(value2);
        ColorSpan[] array = new ColorSpan[list.Count];
        for (int num = 0; num < list.Count; num++)
        {
            array[num].Start = list[num].Start;
            array[num].End = list[num].End;
            if (TokenBrushes == null || TokenBrushes.Length == 0)
            {
                array[num].Foreground = _defaultColors[0].Foreground;
            }
            else
            {
                array[num].Foreground = ((list[num].Type >= TokenBrushes.Length) ? TokenBrushes[0] : TokenBrushes[list[num].Type]);
            }
        }
        return array;
    }

    protected override void OnLineChanged(int value2, int value3, int value4, int value5)
    {
        DateTime now = DateTime.Now;
        int num = _tokenCache.RetokenizeLine(value2, Tokenizing);
        Value = (DateTime.Now - now).TotalMilliseconds;
        if (num > 1)
        {
            InvalidateLines(value2, value2 + num - 1);
        }
        base.OnLineChanged(value2, value3, value4, value5);
        CancelHover();
    }

    protected override void OnRangeChanged(TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, TextDocument.TextPosition textPosition3)
    {
        int num = textPosition.Line;
        int num2 = textPosition2.Line - textPosition.Line;
        int num3 = textPosition3.Line - textPosition.Line;
        if (textPosition.Column == 0 && textPosition2 == textPosition && textPosition3.Line == textPosition.Line + 1 && textPosition3.Column == 0 && num > 0)
        {
            num--;
        }
        if (textPosition.Column == 0 && textPosition3 == textPosition && textPosition2.Line == textPosition.Line + 1 && textPosition2.Column == 0 && num > 0)
        {
            num--;
        }
        _inlineMessageProvider.ShiftLines(num, num2, num3);
        DateTime now = DateTime.Now;
        _tokenCache.OnLinesChanged(num, num2, num3, Tokenizing);
        Value = (DateTime.Now - now).TotalMilliseconds;
        _foldingManager.RemoveStaleRegions(num, num2, num3, out var flag);
        base.OnRangeChanged(textPosition, textPosition2, textPosition3);
        if (flag)
        {
            UpdateLayout();
        }
        CancelHover();
    }

    public void EndTemplate()
    {
        if (_templateSession != null)
        {
            _templateSession.RequestEnd();
            _templateSession = null;
        }
    }

    protected override void OnSelectionChanged(TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, bool flag, SelectionChangeSource selectionChangeSource)
    {
        CancelHover();
        if (!flag)
        {
            TokenCache.Token token = _tokenCache.FindNearestToken(textPosition2.Line, textPosition2.Column);
            if (token == null || token.Kind != TokenCache.Token.TokenKind.Paired || CaretPosition.Line != token.LineNumber || CaretPosition.Column != token.End)
            {
                RemoveHighlight(_pairHighlightId);
                _pairHighlightId = -1;
            }
            else
            {
                string text = base.EditableDocument.GetLineText(token.LineNumber);
                string text2 = "";
                if (token.End <= text.Length)
                {
                    text2 = text.Substring(token.Start, token.End - token.Start);
                }
                if (text2.Length >= 1)
                {
                    HighlightStyle highlightStyle;
                    switch (text2[0])
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
                    TextDocument.TextPosition textPosition3;
                    TextDocument.TextPosition textPosition4;
                    if (token.Partner == null)
                    {
                        highlightStyle = UnpairedHighlightStyle;
                        textPosition3 = token.StartPosition;
                        textPosition4 = token.EndPosition;
                        if (textPosition3 < textPosition4)
                        {
                            textPosition4 = token.StartPosition;
                            textPosition3 = token.EndPosition;
                        }
                    }
                    else if (token.StartPosition < token.Partner.StartPosition)
                    {
                        textPosition3 = token.StartPosition;
                        textPosition4 = token.Partner.EndPosition;
                    }
                    else
                    {
                        textPosition3 = token.Partner.StartPosition;
                        textPosition4 = token.EndPosition;
                    }
                    _pairHighlightId = SetHighlight(_pairHighlightId, textPosition3, textPosition4, highlightStyle);
                }
            }
        }
        base.OnSelectionChanged(textPosition, textPosition2, flag, selectionChangeSource);
    }

    public void ClearMessages()
    {
        _inlineMessageProvider.Clear();
        Redraw();
        UpdateLayout();
    }

    public void AddMessage(int value2, InlineMessageProvider.MessageSeverity messageSeverity, string text, int value3)
    {
        _inlineMessageProvider.AddMessage(value2 - 1, messageSeverity, text, value3);
    }

    public void RefreshMessages()
    {
        Redraw();
        UpdateLayout();
    }

    public void GoToPosition(int value2, int value3)
    {
        if (value3 < 0)
        {
            value3 = 0;
        }
        value2 = _inlineMessageProvider.GetCurrentLine(value2);
        if (_foldingManager.ExpandAt(value2, value3))
        {
            RebuildDisplayRows();
            Redraw();
        }
        SetCaret(new TextDocument.TextPosition(value3, value2));
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
            HideSignatureHint();
            if (_hoverControl != null)
            {
                _hoverControl.Dispose();
                _hoverControl = null;
            }
        }
        base.OnKeyDown(e);
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

    public void ShowSuggestions(IEnumerable<ISuggestion> items)
    {
        if (_suggestionController == null)
        {
            _suggestionController = new SuggestionController(this);
        }
        _suggestionController.BackColor = SuggestionBackColor;
        _suggestionController.Show(new SuggestionSource(this, items), autoSize: true, allowMultiSelect: false);
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

    public void ShowSignatureHint(ISignatureInfo signatureInfo, int value2, string text)
    {
        HideSignatureHint();
        _signatureHintController = new SignatureHintController(this);
        _suppressHintClose = true;
        Control control = null;
        if (!IsWindows())
        {
            control = this;
        }
        _signatureHintController.Show(signatureInfo, value2, (text == null) ? null : ("Press TAB to " + text), control);
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
        if (_signatureHintController != null)
        {
            _signatureHintController.Hide();
            _signatureHintController = null;
        }
    }

    protected virtual bool IsWordSeparator(char ch)
    {
        return "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".IndexOf(ch) == -1;
    }

    protected override void OnCharTyping(char ch, ref string text, ref TextDocument.TextPosition textPosition)
    {
        if (_suggestionController != null && _suggestionController.IsVisible && IsWordSeparator(ch) && (Control.ModifierKeys & Keys.Control) == 0)
        {
            _suggestionController.AcceptSelected();
        }
        base.OnCharTyping(ch, ref text, ref textPosition);
    }

    private void UpdateSuggestions()
    {
        if (_suggestionController != null && _suggestionController.IsVisible)
        {
            string text = GetCurrentTokenText();
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
            bool flag = false;
            _tabHandler(ref flag);
            if (flag)
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

    public TokenCache.Token GetCurrentToken()
    {
        return _tokenCache.FindNearestToken(CaretPosition.Line, CaretPosition.Column);
    }

    public virtual string GetCurrentTokenText()
    {
        TokenCache.Token token = _tokenCache.FindNearestToken(CaretPosition.Line, CaretPosition.Column);
        if (token == null)
        {
            return null;
        }
        return base.EditableDocument.GetLineText(token.LineNumber).Substring(token.Start, token.End - token.Start);
    }

    public void SetPopupActive(bool flag)
    {
        ShowCaretWhenUnfocused = flag;
        if (!flag)
        {
            Focus();
        }
    }

    public void SetActiveParameter(int value2)
    {
        if (IsSignatureHintVisible)
        {
            _signatureHintController.SetActiveParameter(value2);
        }
    }

    public void InsertTemplate(CodeTemplate codeTemplate)
    {
        codeTemplate.Insert(this, CaretPosition);
    }

    internal virtual void RaisePlaceholderChanged(int value2, int value3, int value4, object obj, bool flag)
    {
        if (PlaceholderChanged != null)
        {
            PlaceholderChanged(this, value2, value3, value4, obj, flag);
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
        if (_foldingManager.ExpandLine(CaretPosition.Line))
        {
            UpdateLayout();
            Redraw();
        }
    }

    public void CollapseCurrentFold()
    {
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
            int num2 = foldedLineInfo.Line;
            if (num2 >= _tokenCache.LineCount || _tokenCache.Document.GetLineText(num2) != foldedLineInfo.Text)
            {
                int num3 = -1;
                bool flag = false;
                for (int num4 = 0; num4 < _tokenCache.Document.LineCount; num4++)
                {
                    if (_tokenCache.Document.GetLineText(num4) == foldedLineInfo.Text)
                    {
                        if (num3 != -1)
                        {
                            flag = true;
                            break;
                        }
                        num3 = num4;
                    }
                }
                if (flag)
                {
                    continue;
                }
                num2 = num3;
            }
            if (num2 != -1)
            {
                _foldingManager.CollapseLine(num2, this);
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

    public int GetLineLength(int value2)
    {
        return base.EditableDocument.GetLineLengthWithTerminator(value2);
    }
}
