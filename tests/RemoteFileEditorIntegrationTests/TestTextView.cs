using SmarTTY.CodeEditing;
using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

// The text view the stand gives the remote editor (stage 3E-5: the WinForms view, RemoteTextView, is gone and the WPF view cannot be made under
// Mono). It is built on the models the WPF view uses: the document, the undo history and the edit commands (UndoHistory, TextReplace,
// TextEditCommands), the selection, the search (TextSearch), the folds (FoldingManager on a token cache without a tokenizer) and the messages
// (InlineMessageProvider). What a window did is done here as simply as it can be: the posts and the questions of the loading go to the UI thread
// through a WinForms control that only marshals (the stand pumps its messages), the colours and the font are values.
internal sealed class TestTextView : IRemoteTextView, ITextReplaceHost, ISearchTarget, ILineCommandHost
{
    private EditableTextDocument _document = new EditableTextDocument(new[] { "" }, TextDocument.LineEnding.CrLf);

    private readonly UndoHistory _history;

    private readonly TextReplace _replace;

    private readonly LineCommands _lineCommands;

    private readonly TextSelection _selection = new TextSelection();

    private readonly TokenCache _tokens = new TokenCache();

    private readonly FoldingManager _folding;

    private readonly InlineMessageProvider _messages = new InlineMessageProvider();

    private readonly Dictionary<int, HighlightStyle> _highlights = new Dictionary<int, HighlightStyle>();

    private int _lastHighlightId;

    // Takes the posts and the questions of the loading thread to the UI thread.
    private readonly Control _marshal = new Control();

    private TextDocument.TextPosition _caret;

    private bool _readOnly;

    private bool _closed;

    private RgbColor _background = RgbColor.FromArgb(255, 255, 255);

    private RgbColor _foreground = RgbColor.FromArgb(0, 0, 0);

    private float _fontZoom = 1f;

    public TestTextView()
    {
        _history = new UndoHistory(OnModifiedChanged);
        _replace = new TextReplace(this, _history);
        _lineCommands = new LineCommands(this);
        _tokens.SetDocument(_document, null);
        _folding = new FoldingManager(_tokens);
        _marshal.CreateControl();
        IntPtr handle = _marshal.Handle;
    }

    /// <summary>The modified state of the text changed (sender, modified).</summary>
    public event Action<object, bool> ModifiedChanged;

    public event TextRangeChangedHandler RangeChanged;

    public event EventHandler AppearanceChanged;

    public event EventHandler Closed;

    public EditableTextDocument Document => _document;

    public bool IsTextModified
    {
        get => _history.IsModified;
        set => _history.IsModified = value;
    }

    public bool ReadOnly
    {
        get => _readOnly;
        set => _readOnly = value;
    }

    public TextDocument.TextPosition CaretPosition => _caret;

    public bool HasSelection => _selection.HasSelection;

    public TextDocument.TextPosition SelectionStart => _selection.Anchor;

    public TextDocument.TextPosition SelectionEnd => _caret;

    public void SetCaret(TextDocument.TextPosition position)
    {
        _folding.OnCaretMoved(position.Line, position.Column);
        int line = Math.Max(0, Math.Min(position.Line, _document.LineCount - 1));
        int column = Math.Max(0, Math.Min(position.Column, _document.GetLineText(line).Length));
        _caret = new TextDocument.TextPosition(column, line);
    }

    /// <summary>Starts or drops the selection; it goes from <paramref name="start"/> to the caret.</summary>
    public void SetSelection(bool select, TextDocument.TextPosition start)
    {
        _selection.HasSelection = select;
        _selection.Anchor = start;
    }

    public object CurrentVersion => _history.CurrentVersion;

    public void SetDocument(EditableTextDocument document)
    {
        _document = document ?? new EditableTextDocument(new[] { "" }, TextDocument.LineEnding.CrLf);
        if (_document.LineCount == 0)
        {
            _document.SetText("");
        }
        _folding.Clear();
        _tokens.SetDocument(_document, null);
        _selection.Clear();
        _caret = new TextDocument.TextPosition(0, 0);
        _history.Reset();
    }

    public void MarkSaved(object version)
    {
        _history.MarkSaved(version);
    }

    public void RaiseModifiedChanged()
    {
        OnModifiedChanged();
    }

    private void OnModifiedChanged()
    {
        ModifiedChanged?.Invoke(this, _history.IsModified);
    }

    public void FocusText()
    {
    }

    public bool WordWrap { get; set; }

    public bool ShowAllCharacters { get; set; }

    public int LineCount => _document.LineCount;

    public bool CanUndo => !_readOnly && _history.HasUndo;

    public bool CanRedo => !_readOnly && _history.HasRedo;

    public void Undo()
    {
        if (!_readOnly)
        {
            _history.Undo();
        }
    }

    public void Redo()
    {
        if (!_readOnly)
        {
            _history.Redo();
        }
    }

    public bool HasLineSelection => _lineCommands.HasLineSelection;

    public bool ApplyLineTransform(LineTransformKind kind)
    {
        return _lineCommands.ApplyLineTransform(kind);
    }

    bool ILineCommandHost.CanEditLines => !_readOnly;

    CommentStyle ILineCommandHost.CommentStyle => null;

    void ILineCommandHost.ExecuteAsSingleUndoStep(Action edits)
    {
        if (!_readOnly)
        {
            _history.ExecuteAsSingleStep(edits);
        }
    }

    // As TextTyping.RemoveLines.
    void ILineCommandHost.RemoveLines(int first, int last)
    {
        TextDocument.TextPosition start;
        TextDocument.TextPosition end;
        if (last < _document.LineCount - 1)
        {
            start = new TextDocument.TextPosition(0, first);
            end = new TextDocument.TextPosition(0, last + 1);
        }
        else
        {
            start = (first > 0) ? new TextDocument.TextPosition(_document.GetLineText(first - 1).Length, first - 1) : new TextDocument.TextPosition(0, 0);
            end = new TextDocument.TextPosition(_document.GetLineText(last).Length, last);
        }
        ReplaceText(start, end, select: false, "");
    }

    void ILineCommandHost.UpdateMaxLineWidth(int line)
    {
    }

    public void GoToPosition(int line, int column)
    {
        SetCaret(new TextDocument.TextPosition(Math.Max(0, column), line));
    }

    public void ClearMessages()
    {
        _messages.Clear();
    }

    public void AddMessages(IEnumerable<InlineMessageProvider.MessageRequest> messages)
    {
        foreach (InlineMessageProvider.MessageRequest message in messages)
        {
            _messages.AddReported(message.Line, message.Severity, message.Text, message.Column, _document.LineCount);
        }
    }

    /// <summary>The number of inline messages shown.</summary>
    public int MessageCount => _messages.Count;

    public void ApplyMessageColors(RgbColor background, RgbColor error, RgbColor warning, RgbColor border)
    {
    }

    public FoldedLineInfo[] GetFoldState()
    {
        return _folding.GetCollapsedRegions();
    }

    public void RestoreFoldState(FoldedLineInfo[] items)
    {
        _folding.RestoreFolds(items);
    }

    public SyntaxHighlighting.Language CompletionLanguage { get; set; }

    public bool InsertSpaces { get; set; }

    public int TabSize { get; set; } = 4;

    // The clipboard is a string here.
    public string Clipboard { get; set; } = "";

    public void CopySelection(bool select)
    {
        if (HasSelection)
        {
            Clipboard = _document.GetRangeText(SelectionStart, _caret);
        }
    }

    public void CopyAll()
    {
        Clipboard = _document.GetText();
    }

    public void Cut()
    {
        if (!_readOnly && HasSelection)
        {
            CopySelection(true);
            DeleteSelection();
        }
    }

    public void Paste()
    {
        if (!_readOnly && !string.IsNullOrEmpty(Clipboard))
        {
            ReplaceText(_caret, HasSelection ? SelectionStart : _caret, select: false, Clipboard);
        }
    }

    public void DeleteSelection()
    {
        if (!_readOnly && HasSelection)
        {
            ReplaceText(SelectionStart, _caret, select: false, "");
        }
    }

    public void SelectAllText()
    {
        _selection.Anchor = new TextDocument.TextPosition(0, 0);
        SetCaret(TextSelection.DocumentEnd(_document));
        _selection.HasSelection = true;
    }

    // The colours and the font a hex view takes: Courier New of 12 points at the zoom, as the WinForms editor had.
    public string FontFamily => "Courier New";

    public float FontSize => 12f;

    public RgbColor BackColor
    {
        get => _background;
        set
        {
            _background = value;
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public RgbColor ForeColor
    {
        get => _foreground;
        set
        {
            _foreground = value;
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public float FontZoom
    {
        get => _fontZoom;
        set
        {
            _fontZoom = value;
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void GetAppearance(out RgbColor background, out RgbColor foreground, out string fontFamily, out float fontSizeInPoints)
    {
        background = _background;
        foreground = _foreground;
        fontFamily = FontFamily;
        fontSizeInPoints = FontSize * _fontZoom;
    }

    public bool IsClosed => _closed;

    public void Close()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        Closed?.Invoke(this, EventArgs.Empty);
        _marshal.Dispose();
    }

    public bool Post(Action action)
    {
        try
        {
            _marshal.BeginInvoke((ThreadStart)(() => action()));
            return true;
        }
        catch
        {
            // The view was closed meanwhile.
            return false;
        }
    }

    public IUiDispatcher UiDispatcher => new Dispatcher(this);

    public IUserPrompt UserPrompt => new Prompt(this);

    public IWin32Window MessageOwner => null;

    private sealed class Dispatcher : IUiDispatcher
    {
        private readonly TestTextView _view;

        public Dispatcher(TestTextView view)
        {
            _view = view;
        }

        public bool Post(Action action)
        {
            return !_view._closed && _view.Post(action);
        }
    }

    private sealed class Prompt : IUserPrompt
    {
        private readonly TestTextView _view;

        public Prompt(TestTextView view)
        {
            _view = view;
        }

        public PromptAnswer Ask(string text, PromptButtons buttons)
        {
            if (_view._closed)
            {
                return PromptAnswer.Cancel;
            }
            DialogButtons boxButtons = buttons == PromptButtons.OkCancel ? DialogButtons.OkCancel
                : (buttons == PromptButtons.YesNo ? DialogButtons.YesNo : DialogButtons.YesNoCancel);
            try
            {
                DialogAnswer result = (DialogAnswer)_view._marshal.Invoke((Func<DialogAnswer>)(() => UserDialogs.Show(text, "SmarTTY", boxButtons, DialogIcon.Warning)));
                switch (result)
                {
                    case DialogAnswer.Ok:
                        return PromptAnswer.Ok;
                    case DialogAnswer.Yes:
                        return PromptAnswer.Yes;
                    case DialogAnswer.No:
                        return PromptAnswer.No;
                    default:
                        return PromptAnswer.Cancel;
                }
            }
            catch (InvalidOperationException)
            {
                return PromptAnswer.Cancel;
            }
        }
    }

    // ---- the edits (ITextReplaceHost and ITextEditView, as the WPF view implements them)

    public void ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text)
    {
        if (!_readOnly)
        {
            _replace.Replace(start, end, select, text);
        }
    }

    bool ITextReplaceHost.SingleLine => false;

    bool ITextReplaceHost.UseExistingLineEnding => true;

    string ITextReplaceHost.GetLineOrNull(int line)
    {
        return line >= _document.LineCount ? null : _document.GetLineText(line);
    }

    string ITextReplaceHost.GetIndentation(int line)
    {
        string text = _document.GetLineText(line);
        int length = 0;
        while (length < text.Length && (text[length] == ' ' || text[length] == '\t'))
        {
            length++;
        }
        return text.Substring(0, length);
    }

    bool ITextEditView.HasSelection
    {
        get => _selection.HasSelection;
        set => _selection.HasSelection = value;
    }

    bool ITextEditView.OverwriteMode => false;

    void ITextEditView.GetSelectedLines(out int first, out int last)
    {
        _selection.GetSelectedLines(_caret, out first, out last);
    }

    void ITextEditView.SetCaretColumn(int column)
    {
        SetCaret(new TextDocument.TextPosition(column, _caret.Line));
    }

    void ITextEditView.OnLineChanged(int line, int column, int addedLength, int removedLength)
    {
        _tokens.RetokenizeLine(line, null);
        _folding.RemoveStaleRegions(line, 0, 0, out _);
        if (line == _caret.Line && _caret.Column > _document.GetLineText(line).Length)
        {
            SetCaret(new TextDocument.TextPosition(_document.GetLineText(line).Length, line));
        }
        if (!_history.InEdit)
        {
            _history.IsModified = true;
        }
    }

    void ITextEditView.OnRangeChanged(TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd)
    {
        SyntaxColoring.ChangedLines(start, oldEnd, newEnd, out int firstLine, out int removedLines, out int addedLines);
        _messages.ShiftLines(firstLine, removedLines, addedLines);
        _tokens.OnLinesChanged(firstLine, removedLines, addedLines, null);
        _folding.RemoveStaleRegions(firstLine, removedLines, addedLines, out _);
        RangeChanged?.Invoke(start, oldEnd, newEnd);
        if (!_history.InEdit)
        {
            _history.IsModified = true;
        }
    }

    void ITextEditView.InvalidateLine(int line)
    {
    }

    void ITextEditView.UpdateLayout()
    {
    }

    void ITextEditView.Redraw()
    {
    }

    // ---- the search bar (ISearchTarget, as the WPF view implements it)

    public string GetLineText(int line)
    {
        return _document.GetLineText(line);
    }

    public string GetRangeText(TextDocument.TextPosition start, TextDocument.TextPosition end)
    {
        return _document.GetRangeText(start, end);
    }

    public TextDocument.TextPosition Find(TextDocument.TextPosition textPosition, TextMatcher matcher, bool searchBackward, bool wrapAround, FindSelectMode findSelectMode,
        TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd)
    {
        if (TextSearch.TryFind(_document, textPosition, matcher, searchBackward, wrapAround, scopeStart, scopeEnd, out int line, out int matchIndex, out int matchLength))
        {
            TextDocument.TextPosition position = new TextDocument.TextPosition(matchIndex, line);
            if (findSelectMode != FindSelectMode.None)
            {
                SetCaret(position);
                SetSelection(true, new TextDocument.TextPosition(matchIndex + matchLength, line));
            }
            return position;
        }
        if (matcher != null && findSelectMode == FindSelectMode.SelectOrClear)
        {
            int last = _document.LineCount - 1;
            SetCaret(new TextDocument.TextPosition(_document.GetLineText(last).Length, last));
            SetSelection(false, _caret);
        }
        return new TextDocument.TextPosition(-1, -1);
    }

    public int ReplaceAll(TextMatcher matcher, string replacement, TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd, out TextDocument.TextPosition newScopeEnd)
    {
        newScopeEnd = scopeEnd;
        if (_readOnly)
        {
            return 0;
        }
        return _replace.ReplaceAll(matcher, replacement, scopeStart, scopeEnd, out newScopeEnd);
    }

    public void SetMatchHighlight(TextMatcher matcher)
    {
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
        _highlights[highlightIndex] = highlightStyle;
        return highlightIndex;
    }

    public void RemoveHighlight(int highlightIndex)
    {
        _highlights.Remove(highlightIndex);
    }
}
