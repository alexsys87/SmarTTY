using SmarTTY.Completion;
using SmarTTY.TextEditing;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

/// <summary>What the completion of a code editor reads of its view and asks of it (the calls <c>CodeEditorPP</c> made on itself).</summary>
internal interface IEditorCompletionHost
{
    /// <summary>The document; null before one is set.</summary>
    EditableTextDocument Document { get; }

    TextDocument.TextPosition CaretPosition { get; }

    /// <summary>The word before the caret that the list is filtered by; null without a document or a caret.</summary>
    string CurrentText { get; }

    /// <summary>The background of the word list.</summary>
    RgbColor SuggestionBackColor { get; }

    /// <summary>Makes the windows of the word list (asked once, when the list is first shown).</summary>
    ISuggestionPopupFactory CreatePopupFactory();

    /// <summary>Makes the window of a parameter hint.</summary>
    IHintView CreateHintView();

    /// <summary>Gives the keyboard back to the editor after the hint window was shown.</summary>
    void FocusView();

    /// <summary>A typed character that ends a word (it accepts the selected word of the list when that word continues the typed one).</summary>
    bool IsWordSeparator(char ch);
}

/// <summary>
/// The completion of a code editor: the word list (opened by Ctrl+Space or, with <see cref="OnTyping"/>, by typing a couple of letters of a
/// word, narrowed to the typed word, accepted by a separator, closed by a word that starts with a digit) and the parameter hint of the call
/// the caret is in (opened by a parenthesis, the active parameter after a comma, hidden while the list is open). What <c>CodeEditorPP</c>
/// decided about them, moved here as it was (stage 3D), so that the WPF text view uses the same rules; the view makes the windows and tells
/// this class about the keys and the focus.
/// </summary>
internal sealed class EditorCompletion
{
    /// <summary>The number of typed letters that open the popup by themselves.</summary>
    private const int AutoTriggerLength = 2;

    private readonly IEditorCompletionHost _host;

    private SuggestionController _suggestionController;

    // The list the completion popup offers. The catalog is the popup's source: it is filled with the built list
    // and the popup takes a snapshot of it.
    private readonly SuggestionCatalog _suggestionCatalog = new SuggestionCatalog();

    private SignatureHintController _signatureHintController;

    // The name of the signature the hint currently shows, so typing inside one call does not rebuild it.
    private string _signatureHintName;

    private SyntaxHighlighting.Language _completionLanguage = SyntaxHighlighting.Language.Plain;

    private bool _completionEnabled = true;

    private bool _suppressHintClose;

    public EditorCompletion(IEditorCompletionHost host)
    {
        _host = host;
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

    /// <summary>The name of the signature the hint shows; null when none is shown.</summary>
    public string SignatureHintName => _signatureHintName;

    /// <summary>The words of this language are offered by the completion popup; a new one closes an open list.</summary>
    public SyntaxHighlighting.Language Language
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

    /// <summary>Enables the completion popup (Ctrl+Space and, with <see cref="OnTyping"/>, typing); switched off it closes the list.</summary>
    public bool Enabled
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
    public bool OnTyping { get; set; }

    /// <summary>
    /// The standard API symbols offered in the completion list and used by the parameter hint. Null switches the feature off (keywords
    /// and document words only).
    /// </summary>
    public ApiSymbolDatabase ApiSymbols { get; set; }

    /// <summary>The text the completion and the parameter hint work on, and where the caret is in it (see <see cref="AnalysisText"/>).</summary>
    private string GetAnalysisText(out int caretLine, out int caretColumn)
    {
        return AnalysisText.Get(_host.Document, _host.CaretPosition.Line, _host.CaretPosition.Column, out caretLine, out caretColumn);
    }

    /// <summary>Shows the completion popup for the word the caret is in (Ctrl+Space, or typing).</summary>
    public void ShowCompletion()
    {
        if (!_completionEnabled || _host.Document == null)
        {
            return;
        }
        // The whole list is passed to the popup: it narrows itself down to the typed prefix, and can widen
        // again when letters are deleted, which a list filtered here could not.
        List<ISuggestion> items = EditorCompletionSource.Build(_completionLanguage, GetAnalysisText(out _, out _), "", ApiSymbols);
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

    /// <summary>A character was typed (after the edit); <paramref name="control"/>: Ctrl is held.</summary>
    public void OnCharTyped(char ch, bool control)
    {
        if (OnTyping && _completionEnabled && !IsSuggestionVisible
            && EditorCompletionSource.IsIdentifierChar(ch) && !control)
        {
            string word = _host.CurrentText;
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
            _suggestionController = new SuggestionController(_host.CreatePopupFactory());
        }
        _suggestionController.BackColor = _host.SuggestionBackColor;
        // The catalog is the source of the popup. The whole list is handed over (no prefix): the popup narrows it
        // to the typed word itself and can widen it again when letters are deleted.
        _suggestionCatalog.Clear();
        _suggestionCatalog.Load(items);
        _suggestionController.Show(_suggestionCatalog, null, autoSize: true, allowMultiSelect: false);
        UpdateSuggestions();
    }

    /// <summary>The view scrolled: the hint follows the caret.</summary>
    public void RepositionHint()
    {
        if (IsSignatureHintVisible)
        {
            _signatureHintController.Reposition();
        }
    }

    public void ShowSignatureHint(ISignatureInfo signatureInfo, int parameterIndex, string text)
    {
        HideSignatureHint();
        _signatureHintController = new SignatureHintController(_host.CreateHintView);
        _suppressHintClose = true;
        _signatureHintController.Show(signatureInfo, parameterIndex, (text == null) ? null : ("Press TAB to " + text));
        _host.FocusView();
        _suppressHintClose = false;
    }

    /// <summary>The editor lost the keyboard: the hint closes, unless it is the hint window being shown that took it.</summary>
    public void OnLostFocus()
    {
        if (!_suppressHintClose)
        {
            HideSignatureHint();
        }
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
    public void UpdateSignatureHint()
    {
        if (!_completionEnabled || _host.Document == null || _host.CaretPosition.Line < 0)
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
        string analysisText = GetAnalysisText(out int analysisLine, out int analysisColumn);
        ParameterSignature signature = EditorSignatureSource.FindSignature(
            _completionLanguage, analysisText, analysisLine, analysisColumn, ApiSymbols, out int activeParameter);
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

    /// <summary>A character is about to be typed (before the edit); <paramref name="control"/>: Ctrl is held.</summary>
    public void OnCharTyping(char ch, bool control)
    {
        if (_suggestionController != null && _suggestionController.IsVisible && _host.IsWordSeparator(ch) && !control)
        {
            _suggestionController.AcceptSelectedIfMatches(_host.CurrentText);
        }
    }

    private void UpdateSuggestions()
    {
        if (_suggestionController != null && _suggestionController.IsVisible)
        {
            string text = _host.CurrentText;
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

    /// <summary>A character was handled by the editor (its KeyPress): a printable one or Backspace narrows or widens the list.</summary>
    public void AfterKeyPress(char keyChar)
    {
        if (!char.IsControl(keyChar) || keyChar == '\b')
        {
            UpdateSuggestions();
        }
    }

    /// <summary>Escape: the list and the hint close.</summary>
    public void OnEscape()
    {
        HideCompletion();
        HideSignatureHint();
    }

    /// <summary>A key was handled by the editor (its KeyDown): the keys that move the caret or delete update the hint.</summary>
    public void AfterKeyDown(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.Left:
            case VirtualKey.Right:
            case VirtualKey.Up:
            case VirtualKey.Down:
            case VirtualKey.Home:
            case VirtualKey.End:
            case VirtualKey.Back:
            case VirtualKey.Delete:
                UpdateSignatureHint();
                break;
        }
    }

    public void SetActiveParameter(int parameterIndex)
    {
        if (IsSignatureHintVisible)
        {
            _signatureHintController.SetActiveParameter(parameterIndex);
        }
    }
}
