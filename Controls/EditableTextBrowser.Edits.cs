using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

namespace SmarTTY.Controls;

// The edit commands are CodeEditing/TextEditCommands and what an edit becomes is CodeEditing/TextReplace (stage 3D); this partial gives them what
// they use of the view.
public partial class EditableTextBrowser
{
    // Made when it is first used: the base classes may ask before the constructor has run.
    private TextReplace _replace;

    /// <summary>What an edit becomes, and Replace All.</summary>
    internal TextReplace Replacing => _replace ?? (_replace = new TextReplace(this, History));

    bool ITextReplaceHost.UseExistingLineEnding => _useExistingLineEnding;

    EditableTextDocument ITextEditView.Document => EditableDocument;

    bool ITextEditView.HasSelection
    {
        get => _hasSelection;
        set => _hasSelection = value;
    }

    void ITextEditView.GetSelectedLines(out int first, out int last)
    {
        LineRange lines = SelectedLines;
        first = lines.First;
        last = lines.Last;
    }

    void ITextEditView.SetCaret(TextDocument.TextPosition position)
    {
        SetCaret(position);
    }

    void ITextEditView.SetCaretColumn(int column)
    {
        SetCaretColumn(column);
    }

    void ITextEditView.OnLineChanged(int line, int column, int addedLength, int removedLength)
    {
        OnLineChanged(line, column, addedLength, removedLength);
    }

    void ITextEditView.OnRangeChanged(TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd)
    {
        OnRangeChanged(start, oldEnd, newEnd);
    }

    void ITextEditView.InvalidateLine(int line)
    {
        InvalidateLine(line);
    }
}
