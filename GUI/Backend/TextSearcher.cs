using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.ComponentModel;

namespace SmarTTY.GUI.Backend;

public class TextSearcher : INotifyPropertyChanged
{
    private TextEditorControl _Editor;

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
    {
        _Editor = e;
    }

    public void Search(bool up)
    {
        try
        {
            TextEditorControl editor = _Editor;
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
            if (!editor.Find(textPosition, SearchedText, up, !CaseSensitive, wrapAround: true, ReadonlyTextBrowser.FindSelectMode.SelectOrClear).IsValid)
            {
                MessageBoxes.Info("Cannot find \"" + SearchedText + "\"");
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
