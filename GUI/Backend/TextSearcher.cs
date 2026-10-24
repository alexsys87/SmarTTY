using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;

namespace SmarTTY.GUI.Backend;

public class TextSearcher : INotifyPropertyChanged
{
    private readonly Func<TextEditorControl> _GetEditor;

    private readonly Func<string, string> _NormalizeQuery;

    private bool _IsVisible;

    private string _SearchedText;

    private bool _CaseSensitive;

    private bool _WholeWord;

    private bool _UseRegex;

    private bool _InSelection;

    private bool _IsReplaceVisible;

    private string _ReplacementText;

    private string _ResultText;

    private string _QueryError;

    // The range "Selection" limits the search to, and the editor that holds it (see InSelection).
    private TextDocument.TextPosition _scopeStart;

    private TextDocument.TextPosition _scopeEnd;

    private TextEditorControl _scopeEditor;

    private int _scopeHighlightId = -1;

    // The searcher itself is editing the text: that must not end the search in the selection.
    private bool _applying;

    private static readonly HighlightStyle ScopeStyle = new HighlightStyle(new SolidBrush(Color.FromArgb(45, 90, 140, 255)), new Pen(Color.FromArgb(150, 70, 120, 230), 1f), null);

    public bool IsVisible
    {
        get
        {
            return _IsVisible;
        }
        set
        {
            _IsVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsVisible"));
            if (value)
            {
                UpdateHighlight();
            }
            else
            {
                ClearHighlights();
            }
        }
    }

    public string SearchedText
    {
        get
        {
            return _SearchedText;
        }
        set
        {
            _SearchedText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SearchedText"));
            UpdateHighlight();
        }
    }

    public bool CaseSensitive
    {
        get
        {
            return _CaseSensitive;
        }
        set
        {
            _CaseSensitive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CaseSensitive"));
            UpdateHighlight();
        }
    }

    /// <summary>Find only whole words (not "cat" in "concat").</summary>
    public bool WholeWord
    {
        get
        {
            return _WholeWord;
        }
        set
        {
            _WholeWord = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("WholeWord"));
            UpdateHighlight();
        }
    }

    /// <summary>The searched text is a regular expression; the replacement may use $1, ${name}.</summary>
    public bool UseRegex
    {
        get
        {
            return _UseRegex;
        }
        set
        {
            _UseRegex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("UseRegex"));
            UpdateHighlight();
        }
    }

    /// <summary>
    /// Search and replace only in the text that was selected when this was switched on. It ends when the text is
    /// edited by anything but the replace commands.
    /// </summary>
    public bool InSelection
    {
        get
        {
            return _InSelection;
        }
        set
        {
            if (value == _InSelection)
            {
                return;
            }
            if (value && !BeginScope())
            {
                // Nothing is selected: the switch springs back (after the binding is done setting it).
                RaiseLater("InSelection");
                return;
            }
            if (!value)
            {
                EndScope();
            }
            _InSelection = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("InSelection"));
        }
    }

    /// <summary>The searched view is an editable document, so the Replace row may be shown (file editor tabs).</summary>
    public bool CanReplace { get; set; }

    public bool IsReplaceVisible
    {
        get
        {
            return _IsReplaceVisible;
        }
        set
        {
            _IsReplaceVisible = value && CanReplace;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsReplaceVisible"));
        }
    }

    public string ReplacementText
    {
        get
        {
            return _ReplacementText;
        }
        set
        {
            _ReplacementText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ReplacementText"));
        }
    }

    /// <summary>What the last search or replace found, shown under the search bar.</summary>
    public string ResultText
    {
        get
        {
            return _ResultText;
        }
        private set
        {
            _ResultText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ResultText"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HasResult"));
        }
    }

    public bool HasResult => !string.IsNullOrEmpty(_ResultText);

    /// <summary>Why the searched text cannot be used (an invalid regular expression), or null.</summary>
    public string QueryError
    {
        get
        {
            return _QueryError;
        }
        private set
        {
            if (_QueryError != value)
            {
                _QueryError = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("QueryError"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HasQueryError"));
            }
        }
    }

    public bool HasQueryError => _QueryError != null;

    public event PropertyChangedEventHandler PropertyChanged;

    public TextSearcher(TextEditorControl e)
        : this(() => e, null)
    {
    }

    /// <summary>
    /// Creates a searcher that resolves the editor lazily on every search (so a terminal tab can switch between its
    /// ASCII text view and its HEX dump view) and optionally rewrites the query before searching (the HEX view
    /// expects lowercase byte pairs separated by spaces, e.g. "0A0D" becomes "0a 0d").
    /// </summary>
    public TextSearcher(Func<TextEditorControl> getEditor, Func<string, string> normalizeQuery)
    {
        _GetEditor = getEditor;
        _NormalizeQuery = normalizeQuery;
    }

    private void RaiseLater(string propertyName)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName))));
    }

    /// <summary>
    /// The matcher for the searched text and the options of the bar, or null when there is nothing to search for or
    /// the regular expression is invalid (<see cref="QueryError"/> says why).
    /// </summary>
    private TextMatcher CreateMatcher()
    {
        string query = SearchedText;
        if (_NormalizeQuery != null && !string.IsNullOrEmpty(query))
        {
            query = _NormalizeQuery(query);
        }
        if (string.IsNullOrEmpty(query))
        {
            QueryError = null;
            return null;
        }
        // The HEX view searches for byte pairs: whole word and regular expressions mean nothing there.
        bool plain = _NormalizeQuery != null;
        try
        {
            TextMatcher matcher = TextMatcher.Create(query, CaseSensitive, !plain && WholeWord, !plain && UseRegex);
            QueryError = null;
            return matcher;
        }
        catch (ArgumentException exception)
        {
            QueryError = exception.Message;
            return null;
        }
    }

    public void Search(bool up)
    {
        try
        {
            Func<TextEditorControl> getEditor = _GetEditor;
            TextEditorControl editor = (getEditor != null) ? getEditor() : null;
            if (editor == null)
            {
                return;
            }
            TextMatcher matcher = CreateMatcher();
            if (matcher == null)
            {
                ResultText = (QueryError != null) ? ("Invalid regular expression: " + QueryError) : "";
                return;
            }
            TextDocument.TextPosition textPosition = new TextDocument.TextPosition(0, 0);
            if (editor.SelectionStart.IsValid)
            {
                textPosition = editor.SelectionStart;
            }
            if (up)
            {
                if (editor.SelectionEnd.IsValid && editor.SelectionEnd < textPosition)
                {
                    textPosition = editor.SelectionEnd;
                }
            }
            else if (editor.SelectionEnd.IsValid && editor.SelectionEnd > textPosition)
            {
                textPosition = editor.SelectionEnd;
            }
            bool scoped = _InSelection && _scopeEditor == editor;
            TextDocument.TextPosition found = editor.Find(textPosition, matcher, up, wrapAround: true, ReadonlyTextBrowser.FindSelectMode.SelectOrClear, scoped ? _scopeStart : TextDocument.TextPosition.Invalid, scoped ? _scopeEnd : TextDocument.TextPosition.Invalid);
            if (found.IsValid)
            {
                ResultText = "";
            }
            else if (CanReplace)
            {
                ResultText = "Cannot find \"" + matcher.Query + "\"" + (scoped ? " in the selection." : ".");
            }
            else
            {
                MessageBoxes.Info("Cannot find \"" + matcher.Query + "\"");
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    /// <summary>
    /// Replaces the current match (the selection, when the searched text matches exactly it) and selects the next one.
    /// </summary>
    public void Replace()
    {
        try
        {
            TextEditorControl editor = _GetEditor?.Invoke();
            if (editor == null || !CanReplace || string.IsNullOrEmpty(SearchedText))
            {
                return;
            }
            TextMatcher matcher = CreateMatcher();
            if (matcher == null)
            {
                ResultText = (QueryError != null) ? ("Invalid regular expression: " + QueryError) : "";
                return;
            }
            TextDocument.TextPosition start = editor.SelectionStart;
            TextDocument.TextPosition end = editor.SelectionEnd;
            if (editor.HasSelection && start.IsValid && end.IsValid && start.Line == end.Line && start != end && IsInScope(editor, start, end))
            {
                string line = editor.EditableDocument.GetLineText(start.Line);
                if (end.Column <= line.Length && matcher.MatchesExactly(line, start.Column, end.Column))
                {
                    string replacement = matcher.Expand(ReplacementText ?? "");
                    _applying = true;
                    try
                    {
                        editor.ReplaceText(end, start, select: false, replacement);
                    }
                    finally
                    {
                        _applying = false;
                    }
                    // The range ends later or earlier by what the replacement added or removed.
                    if (_InSelection && _scopeEditor == editor && _scopeEnd.Line == start.Line && _scopeEnd.Column >= end.Column)
                    {
                        _scopeEnd.Column += replacement.Length - (end.Column - start.Column);
                        ShowScope();
                    }
                }
            }
            Search(up: false);
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    public void ReplaceAll()
    {
        try
        {
            TextEditorControl editor = _GetEditor?.Invoke();
            if (editor == null || !CanReplace || string.IsNullOrEmpty(SearchedText))
            {
                return;
            }
            TextMatcher matcher = CreateMatcher();
            if (matcher == null)
            {
                ResultText = (QueryError != null) ? ("Invalid regular expression: " + QueryError) : "";
                return;
            }
            bool scoped = _InSelection && _scopeEditor == editor;
            int count;
            TextDocument.TextPosition newScopeEnd;
            _applying = true;
            try
            {
                count = editor.ReplaceAll(matcher, ReplacementText ?? "", scoped ? _scopeStart : TextDocument.TextPosition.Invalid, scoped ? _scopeEnd : TextDocument.TextPosition.Invalid, out newScopeEnd);
            }
            finally
            {
                _applying = false;
            }
            if (scoped)
            {
                _scopeEnd = newScopeEnd;
                ShowScope();
            }
            ResultText = (count == 0)
                ? ("Cannot find \"" + matcher.Query + "\"" + (scoped ? " in the selection." : "."))
                : ("Replaced " + count + " occurrence" + ((count == 1) ? "" : "s") + (scoped ? " in the selection." : "."));
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    // ---- the selection the search is limited to

    private bool IsInScope(TextEditorControl editor, TextDocument.TextPosition start, TextDocument.TextPosition end)
    {
        if (!_InSelection || _scopeEditor != editor)
        {
            return true;
        }
        TextDocument.TextPosition first = (start < end) ? start : end;
        TextDocument.TextPosition last = (start < end) ? end : start;
        // TextPosition has < and > only.
        return !(first < _scopeStart) && !(last > _scopeEnd);
    }

    private bool BeginScope()
    {
        TextEditorControl editor = _GetEditor?.Invoke();
        if (editor == null || !editor.HasSelection || !editor.SelectionStart.IsValid || !editor.SelectionEnd.IsValid || editor.SelectionStart == editor.SelectionEnd)
        {
            ResultText = "Select the text to search in first.";
            return false;
        }
        TextDocument.TextPosition start = editor.SelectionStart;
        TextDocument.TextPosition end = editor.SelectionEnd;
        _scopeStart = (start < end) ? start : end;
        _scopeEnd = (start < end) ? end : start;
        _scopeEditor = editor;
        editor.RangeChanged += OnScopeEditorTextChanged;
        ShowScope();
        ResultText = "";
        return true;
    }

    private void EndScope()
    {
        TextEditorControl editor = _scopeEditor;
        _scopeEditor = null;
        if (editor == null)
        {
            return;
        }
        editor.RangeChanged -= OnScopeEditorTextChanged;
        try
        {
            editor.RemoveHighlight(_scopeHighlightId);
        }
        catch (Exception)
        {
            // The editor dropped its highlights meanwhile (another document was loaded).
        }
        _scopeHighlightId = -1;
    }

    private void ShowScope()
    {
        if (_scopeEditor == null)
        {
            return;
        }
        try
        {
            _scopeHighlightId = _scopeEditor.SetHighlight(_scopeHighlightId, _scopeStart, _scopeEnd, ScopeStyle);
        }
        catch (Exception)
        {
            _scopeHighlightId = _scopeEditor.AddHighlight(_scopeStart, _scopeEnd, ScopeStyle);
        }
    }

    // The range is a position in the text: once somebody else edits the text it points to the wrong place.
    private void OnScopeEditorTextChanged(EditableTextBrowser editableTextBrowser, TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd)
    {
        if (!_applying)
        {
            ResultText = "The selection changed, the search covers the whole text again.";
            InSelection = false;
        }
    }

    // ---- highlighting of the matches

    /// <summary>Marks all matches of the searched text in the editor (file editor tabs only).</summary>
    private void UpdateHighlight()
    {
        if (!CanReplace)
        {
            return;
        }
        TextEditorControl editor = _GetEditor?.Invoke();
        if (editor == null)
        {
            return;
        }
        editor.SetMatchHighlight(_IsVisible ? CreateMatcher() : null);
    }

    private void ClearHighlights()
    {
        EndScope();
        _InSelection = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("InSelection"));
        QueryError = null;
        ResultText = "";
        if (CanReplace)
        {
            _GetEditor?.Invoke()?.SetMatchHighlight(null);
        }
    }

    public void Activate()
    {
        IsVisible = false;
        IsVisible = true;
    }

    /// <summary>Shows the search bar with its Replace row (Ctrl+H), starting from the selected text.</summary>
    public void ActivateReplace()
    {
        if (!CanReplace)
        {
            Activate();
            return;
        }
        TextEditorControl editor = _GetEditor?.Invoke();
        if (editor != null && editor.HasSelection && editor.SelectionStart.Line == editor.SelectionEnd.Line && editor.SelectionStart != editor.SelectionEnd)
        {
            SearchedText = editor.EditableDocument.GetRangeText(editor.SelectionStart, editor.SelectionEnd);
        }
        IsReplaceVisible = true;
        Activate();
    }

    public void Hide()
    {
        IsVisible = false;
        IsReplaceVisible = false;
    }
}
