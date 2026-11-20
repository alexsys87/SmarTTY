using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

namespace SmarTTY.Controls;

// The edit commands are CodeEditing/TextEditCommands (stage 3D); this partial gives them what they use of the view.
public partial class EditableTextBrowser
{
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
