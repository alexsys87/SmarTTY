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

    public void Activate()
    {
        IsVisible = false;
        IsVisible = true;
    }

    public void Hide()
    {
        IsVisible = false;
    }
}
