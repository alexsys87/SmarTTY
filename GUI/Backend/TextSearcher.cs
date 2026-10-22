using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.ComponentModel;

namespace SmarTTY.GUI.Backend;

public class TextSearcher : INotifyPropertyChanged
{
    private readonly Func<TextEditorControl> _GetEditor;

    private readonly Func<string, string> _NormalizeQuery;

    private bool _IsVisible;

    private string _SearchedText;

    private bool _CaseSensitive;

    private bool _IsReplaceVisible;

    private string _ReplacementText;

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
            string query = SearchedText;
            Func<string, string> normalizeQuery = _NormalizeQuery;
            if (normalizeQuery != null)
            {
                query = normalizeQuery(query);
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
            if (!editor.Find(textPosition, query, up, !CaseSensitive, wrapAround: true, ReadonlyTextBrowser.FindSelectMode.SelectOrClear).IsValid)
            {
                MessageBoxes.Info("Cannot find \"" + query + "\"");
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    /// <summary>
    /// Replaces the current match (the selection, when it equals the searched text) and selects the next one.
    /// </summary>
    public void Replace()
    {
        try
        {
            TextEditorControl editor = _GetEditor?.Invoke();
            string query = SearchedText;
            if (editor == null || !CanReplace || string.IsNullOrEmpty(query))
            {
                return;
            }
            if (editor.HasSelection && editor.SelectionStart != editor.SelectionEnd)
            {
                string selected = editor.EditableDocument.GetRangeText(editor.SelectionStart, editor.SelectionEnd);
                if (string.Equals(selected, query, CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                {
                    editor.ReplaceText(editor.SelectionEnd, editor.SelectionStart, select: false, ReplacementText ?? "");
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
            string query = SearchedText;
            if (editor == null || !CanReplace || string.IsNullOrEmpty(query))
            {
                return;
            }
            int count = editor.ReplaceAll(query, ReplacementText ?? "", !CaseSensitive);
            MessageBoxes.Info((count == 0) ? ("Cannot find \"" + query + "\"") : ("Replaced " + count + " occurrence" + ((count == 1) ? "" : "s") + "."));
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
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
