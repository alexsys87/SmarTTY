using SmarTTY.TextEditing;
using System;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The text editor as its typing sees it (made of the calls <c>Controls/TextEditorControl</c> made in its key handlers): the document, the caret and
/// the selection, the typing mode and the indentation settings, what a word is, the indentation of a line, where a caret move goes, the edits (the
/// view may route them, as the terminals do), the hooks of a typed character, and the commands of the keys (the clipboard and the undo are the view's).
/// </summary>
public interface ITextTypingHost
{
    EditableTextDocument Document { get; }

    TextDocument.TextPosition CaretPosition { get; }

    bool HasSelection { get; }

    TextDocument.TextPosition SelectionStart { get; }

    TextDocument.TextPosition SelectionEnd { get; }

    bool OverwriteMode { get; }

    bool InsertSpaces { get; }

    int TabSize { get; }

    /// <summary>The text can be changed by the keys.</summary>
    bool IsEditable { get; }

    /// <summary>Ctrl+C without a selection copies the caret line.</summary>
    bool CopyLineWhenNoSelection { get; }

    bool IsWordChar(char ch);

    string GetIndentation(int line);

    int IndexToColumn(int line, int index);

    /// <summary>Where a caret move takes the caret now.</summary>
    TextDocument.TextPosition GetMovedPosition(CaretMove move);

    void ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text);

    void Indent(int levels);

    void SetCaret(TextDocument.TextPosition position);

    /// <summary>The longest line may have changed.</summary>
    void UpdateMaxLineWidth(int line);

    /// <summary>A character is about to be typed: the hook may change the text (null: nothing) and the place of the caret after it.</summary>
    void OnCharTyping(char ch, ref string text, ref TextDocument.TextPosition textPosition);

    void OnCharTyped(char ch, TextDocument.TextPosition textPosition);

    void CopySelection(bool select);

    void Paste();

    void Cut();

    void Undo();

    void Redo();
}

/// <summary>What the view does with a key after <see cref="TextTyping.KeyDown"/>.</summary>
[Flags]
public enum TypingKeyResult
{
    None = 0,

    /// <summary>The key was used (the view marks it handled).</summary>
    Handled = 1,

    /// <summary>The character the key would type is dropped too.</summary>
    SuppressKeyPress = 2
}

/// <summary>
/// What typing and the edit keys do to the text: a character (over the selection, in overwrite mode, spaces for a tab, the hooks), Enter with the
/// indentation carried over, Backspace (a level of space indentation), Tab over a selection of lines, Delete, Ctrl+Delete and Ctrl+Backspace (a
/// word and its blanks), the commands of Ctrl and Shift chords, removing lines, the word at the caret. It is what <c>Controls/TextEditorControl</c>
/// did in <c>OnKeyPress</c>, <c>OnKeyDown</c>, <c>DeleteWord</c>, <c>RemoveLines</c>, <c>DeleteSelection</c>, <c>InsertText</c>,
/// <c>GetWordAtCaret</c> and <c>ReplaceWordAtCaret</c>, moved here as it was (stage 3D); the view maps its key events and keeps the clipboard.
/// </summary>
public sealed class TextTyping
{
    private const string WordCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

    private readonly ITextTypingHost _host;

    public TextTyping(ITextTypingHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    private EditableTextDocument Document => _host.Document;

    /// <summary>A character typed (the KeyPress of the view); <paramref name="shift"/>: Shift is held (Shift+Tab unindents).</summary>
    public void TypeCharacter(char keyChar, bool shift)
    {
        // Tab indents a multi-line selection; a selection inside one line is replaced by the tab as usual.
        if (keyChar == '\t' && ((_host.HasSelection && _host.SelectionStart.Line != _host.CaretPosition.Line) || shift))
        {
            int value = 1;
            if (shift)
            {
                value = -1;
            }
            _host.Indent(value);
            return;
        }
        switch (keyChar)
        {
            case '\r':
                {
                    string content = Document.GetLineTerminator(_host.CaretPosition.Line, "\n");
                    if (content.Length == 0)
                    {
                        // The line has an inherited ending (an empty file or a file with a single line
                        // and no trailing newline): Enter must still insert a line break.
                        content = TextDocument.GetLineEndingText(Document.DefaultLineEnding);
                    }
                    // Only the indentation in front of the caret is carried over: with the caret inside it, the
                    // rest of the indentation stays with the text that moves to the new line.
                    string indentation = _host.GetIndentation(_host.CaretPosition.Line);
                    if (_host.CaretPosition.Column < indentation.Length)
                    {
                        indentation = indentation.Substring(0, _host.CaretPosition.Column);
                    }
                    _host.ReplaceText(_host.CaretPosition, _host.SelectionStart, !_host.HasSelection, content + indentation);
                    break;
                }
            case '\b':
                if (_host.HasSelection)
                {
                    _host.ReplaceText(_host.SelectionEnd, _host.SelectionStart, select: false, "");
                }
                else if (SpaceIndentBackspaceLength() > 0)
                {
                    // In space indentation Backspace removes one level, back to the previous tab stop.
                    int spaces = SpaceIndentBackspaceLength();
                    _host.ReplaceText(_host.CaretPosition, new TextDocument.TextPosition(_host.CaretPosition.Column - spaces, _host.CaretPosition.Line), select: false, "");
                }
                else
                {
                    _host.ReplaceText(_host.CaretPosition, _host.GetMovedPosition(CaretMove.Left), select: false, "");
                }
                _host.UpdateMaxLineWidth(_host.CaretPosition.Line);
                break;
            default:
                if (char.IsControl(keyChar) && keyChar != '\t')
                {
                    break;
                }
                if (!_host.HasSelection && _host.OverwriteMode && _host.CaretPosition.Column < Document.GetLineText(_host.CaretPosition.Line).Length)
                {
                    _host.ReplaceText(_host.CaretPosition, new TextDocument.TextPosition(_host.CaretPosition.Column + 1, _host.CaretPosition.Line), select: false, new string(keyChar, 1));
                }
                else
                {
                    string text = new string(keyChar, 1);
                    if (keyChar == '\t' && _host.InsertSpaces)
                    {
                        text = SpacesToNextTabStop();
                    }
                    TextDocument.TextPosition textPosition = new TextDocument.TextPosition(-1, -1);
                    _host.OnCharTyping(keyChar, ref text, ref textPosition);
                    if (text != null)
                    {
                        if (!_host.HasSelection)
                        {
                            _host.ReplaceText(_host.CaretPosition, _host.SelectionStart, select: true, text);
                        }
                        else
                        {
                            _host.ReplaceText(_host.SelectionEnd, _host.SelectionStart, select: false, text);
                        }
                    }
                    if (textPosition.IsValid)
                    {
                        _host.SetCaret(textPosition);
                    }
                    _host.OnCharTyped(keyChar, _host.CaretPosition);
                }
                _host.UpdateMaxLineWidth(_host.CaretPosition.Line);
                break;
        }
    }

    /// <summary>A key pressed (the KeyDown of the view): the edit keys and the commands of the Ctrl and Shift chords.</summary>
    public TypingKeyResult KeyDown(VirtualKey key, bool control, bool alt, bool shift)
    {
        TypingKeyResult result = TypingKeyResult.None;
        if (!_host.IsEditable)
        {
            return result;
        }
        // Shift+Delete is Cut (handled below); deleting here first would lose the selection and
        // make Cut remove the whole caret line as well.
        if (key == VirtualKey.Back && control && !alt && !shift)
        {
            // Ctrl+Backspace removes the word in front of the caret (the control character it types is ignored).
            DeleteWord(forward: false);
            result |= TypingKeyResult.Handled | TypingKeyResult.SuppressKeyPress;
        }
        else if (key == VirtualKey.Delete && !shift)
        {
            if (_host.HasSelection)
            {
                _host.ReplaceText(_host.SelectionEnd, _host.SelectionStart, select: false, "");
            }
            else if (control && !alt)
            {
                DeleteWord(forward: true);
                result |= TypingKeyResult.Handled;
            }
            else
            {
                _host.ReplaceText(_host.CaretPosition, _host.GetMovedPosition(CaretMove.Right), select: false, "");
            }
            _host.UpdateMaxLineWidth(_host.CaretPosition.Line);
        }
        // Ctrl+Alt is AltGr on many keyboards: AltGr+C, V, X, Z, Y type letters there (ć, ż, ...), not commands.
        bool command = control && !alt;
        if (command && (key == VirtualKey.Insert || key == VirtualKey.C))
        {
            _host.CopySelection(_host.CopyLineWhenNoSelection);
            result |= TypingKeyResult.Handled;
        }
        else if ((shift && key == VirtualKey.Insert) || (command && key == VirtualKey.V))
        {
            _host.Paste();
            result |= TypingKeyResult.Handled;
        }
        else if ((shift && key == VirtualKey.Delete) || (command && key == VirtualKey.X))
        {
            _host.Cut();
            result |= TypingKeyResult.Handled;
        }
        else if (!shift && command && key == VirtualKey.Z)
        {
            _host.Undo();
            result |= TypingKeyResult.Handled;
        }
        else if ((shift && command && key == VirtualKey.Z) || (command && key == VirtualKey.Y))
        {
            _host.Redo();
            result |= TypingKeyResult.Handled;
        }
        return result;
    }

    /// <summary>
    /// Removes the selection, or the word on one side of the caret together with the spaces next to it; at the end or
    /// the start of a line it removes the line break.
    /// </summary>
    public void DeleteWord(bool forward)
    {
        if (_host.HasSelection)
        {
            _host.ReplaceText(_host.SelectionEnd, _host.SelectionStart, select: false, "");
            _host.UpdateMaxLineWidth(_host.CaretPosition.Line);
            return;
        }
        TextDocument.TextPosition caret = _host.CaretPosition;
        string text = Document.GetLineText(caret.Line);
        TextDocument.TextPosition other = caret;
        if (forward)
        {
            if (caret.Column >= text.Length)
            {
                if (caret.Line >= Document.LineCount - 1)
                {
                    return;
                }
                other = new TextDocument.TextPosition(0, caret.Line + 1);
            }
            else
            {
                int i = caret.Column;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }
                if (i < text.Length)
                {
                    bool word = _host.IsWordChar(text[i]);
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && _host.IsWordChar(text[i]) == word)
                    {
                        i++;
                    }
                }
                other = new TextDocument.TextPosition(i, caret.Line);
            }
        }
        else if (caret.Column == 0)
        {
            if (caret.Line == 0)
            {
                return;
            }
            other = new TextDocument.TextPosition(Document.GetLineText(caret.Line - 1).Length, caret.Line - 1);
        }
        else
        {
            int i = Math.Min(caret.Column, text.Length);
            while (i > 0 && char.IsWhiteSpace(text[i - 1]))
            {
                i--;
            }
            if (i > 0)
            {
                bool word = _host.IsWordChar(text[i - 1]);
                while (i > 0 && !char.IsWhiteSpace(text[i - 1]) && _host.IsWordChar(text[i - 1]) == word)
                {
                    i--;
                }
            }
            other = new TextDocument.TextPosition(i, caret.Line);
        }
        _host.ReplaceText(caret, other, select: false, "");
        _host.UpdateMaxLineWidth(_host.CaretPosition.Line);
    }

    /// <summary>Removes the lines together with their line breaks (the line break before them when they are the last ones).</summary>
    public void RemoveLines(int first, int last)
    {
        TextDocument.TextPosition start;
        TextDocument.TextPosition end;
        if (last < Document.LineCount - 1)
        {
            start = new TextDocument.TextPosition(0, first);
            end = new TextDocument.TextPosition(0, last + 1);
        }
        else
        {
            // The last lines have no line break of their own: the one before them goes.
            start = (first > 0)
                ? new TextDocument.TextPosition(Document.GetLineText(first - 1).Length, first - 1)
                : new TextDocument.TextPosition(0, 0);
            end = new TextDocument.TextPosition(Document.GetLineText(last).Length, last);
        }
        _host.ReplaceText(start, end, select: false, "");
    }

    /// <summary>Removes the selected text; does nothing without a selection (unlike Cut, it leaves the clipboard alone).</summary>
    public void DeleteSelection()
    {
        if (_host.IsEditable && _host.HasSelection)
        {
            _host.ReplaceText(_host.SelectionEnd, _host.SelectionStart, select: false, "");
            _host.UpdateMaxLineWidth(_host.CaretPosition.Line);
        }
    }

    /// <summary>Puts a text at the caret, over the selection when there is one.</summary>
    public void InsertText(string text)
    {
        _host.ReplaceText(_host.CaretPosition, _host.SelectionStart, !_host.HasSelection, text);
    }

    /// <summary>The word of letters, digits and underscores around the caret, its start and its length; null at the start of a line.</summary>
    public string GetWordAtCaret(out int column, out int value)
    {
        column = -1;
        value = 0;
        string text = Document.GetLineText(_host.CaretPosition.Line);
        if (_host.CaretPosition.Column == 0)
        {
            return null;
        }
        column = _host.CaretPosition.Column;
        while (column > 0 && WordCharacters.IndexOf(text[column - 1]) != -1)
        {
            column--;
        }
        int itemIndex;
        for (itemIndex = _host.CaretPosition.Column; itemIndex < text.Length && WordCharacters.IndexOf(text[itemIndex]) != -1; itemIndex++)
        {
        }
        value = itemIndex - column;
        return text.Substring(column, value);
    }

    /// <summary>Replaces the word at the caret by <paramref name="text"/>, or inserts it when there is no word.</summary>
    public void ReplaceWordAtCaret(string text)
    {
        if (GetWordAtCaret(out var item, out var entry) == null || entry == 0)
        {
            InsertText(text);
        }
        else
        {
            _host.ReplaceText(new TextDocument.TextPosition(item, _host.CaretPosition.Line), new TextDocument.TextPosition(item + entry, _host.CaretPosition.Line), select: false, text);
        }
    }

    /// <summary>The spaces that move the insertion point (the caret or the start of the selection) to the next tab stop.</summary>
    public string SpacesToNextTabStop()
    {
        TextDocument.TextPosition position = _host.CaretPosition;
        if (_host.HasSelection && _host.SelectionStart < position)
        {
            position = _host.SelectionStart;
        }
        int tabSize = Math.Max(1, _host.TabSize);
        int column = _host.IndexToColumn(position.Line, position.Column);
        return new string(' ', tabSize - column % tabSize);
    }

    /// <summary>
    /// With InsertSpaces, the number of spaces Backspace removes when the caret is inside leading spaces:
    /// back to the previous tab stop. 0 when Backspace should remove a single character.
    /// </summary>
    public int SpaceIndentBackspaceLength()
    {
        if (!_host.InsertSpaces || _host.CaretPosition.Column == 0)
        {
            return 0;
        }
        string lineText = Document.GetLineText(_host.CaretPosition.Line);
        if (_host.CaretPosition.Column > lineText.Length)
        {
            return 0;
        }
        for (int i = 0; i < _host.CaretPosition.Column; i++)
        {
            if (lineText[i] != ' ')
            {
                return 0;
            }
        }
        int tabSize = Math.Max(1, _host.TabSize);
        return (_host.CaretPosition.Column - 1) % tabSize + 1;
    }
}
