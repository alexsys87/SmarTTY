using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

namespace SmarTTY.Controls;

// What typing and the edit keys do is CodeEditing/TextTyping (stage 3D); this partial gives it what it uses of the editor.
public partial class TextEditorControl : ITextTypingHost
{
    // Made when it is first used: the base classes may ask before the constructor has run.
    private TextTyping _typing;

    /// <summary>What typing and the edit keys do to the text.</summary>
    internal TextTyping Typing => _typing ?? (_typing = new TextTyping(this));

    EditableTextDocument ITextTypingHost.Document => EditableDocument;

    bool ITextTypingHost.IsEditable => IsEditable;

    bool ITextTypingHost.CopyLineWhenNoSelection => CopyLineWhenNoSelection;

    bool ITextTypingHost.IsWordChar(char ch)
    {
        return IsWordChar(ch);
    }

    TextDocument.TextPosition ITextTypingHost.GetMovedPosition(CaretMove move)
    {
        return GetMovedPosition(move);
    }

    void ITextTypingHost.SetCaret(TextDocument.TextPosition position)
    {
        SetCaret(position);
    }

    void ITextTypingHost.UpdateMaxLineWidth(int line)
    {
        UpdateMaxLineWidth(line);
    }

    void ITextTypingHost.OnCharTyping(char ch, ref string text, ref TextDocument.TextPosition textPosition)
    {
        OnCharTyping(ch, ref text, ref textPosition);
    }

    void ITextTypingHost.OnCharTyped(char ch, TextDocument.TextPosition textPosition)
    {
        OnCharTyped(ch, textPosition);
    }
}
