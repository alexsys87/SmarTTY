using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;

namespace SmarTTY.Terminal.Rendering;

/// <summary>
/// The command line of the Smart Terminal on a <see cref="TerminalDisplayModel"/>: the prompt and the text typed after it, at the end of the
/// document, wrapped at the width of the screen, with the caret and a selection inside the text. It is what <c>SshTerminalControl</c> made
/// of its editor (<c>InputRegion</c>, <c>RewrapInput</c> and the editing commands of the text editor): every change writes the rows again
/// (<see cref="TerminalDisplayModel.ReplaceDocumentTail(int, IList{string})"/>), and the places in the text are mapped to the document by <see cref="InputRegionMap"/>.
/// An index is a place in <see cref="Text"/> (0 is the start, <c>Text.Length</c> the end).
/// </summary>
internal sealed class PromptLine
{
    private readonly TerminalDisplayModel _model;

    private string _text = "";

    private int _caretIndex;

    // The other end of a selection (Shift with the arrows, the mouse in the line); -1: none.
    private int _anchor = -1;

    public PromptLine(TerminalDisplayModel model, int startLine, string prompt)
        : this(model, startLine, prompt, promptOnScreen: false)
    {
    }

    /// <summary>
    /// A command line after <paramref name="prompt"/>. With <paramref name="promptOnScreen"/> the prompt is the text the device has drawn on
    /// <paramref name="startLine"/> (as many columns as the prompt is long): it is not written again, so it keeps the colours of the device,
    /// and only what follows it is replaced (what <c>SerialTerminalControl.ReplaceAfterDevicePrompt</c> did).
    /// </summary>
    public PromptLine(TerminalDisplayModel model, int startLine, string prompt, bool promptOnScreen)
    {
        _model = model;
        StartLine = startLine;
        Prompt = prompt ?? "";
        PromptOnScreen = promptOnScreen && Prompt.Length > 0;
        Render();
    }

    /// <summary>The prompt is the device's own text on the screen (see the constructor).</summary>
    public bool PromptOnScreen { get; }

    /// <summary>The column of the start line where the typed text starts (the length of the prompt).</summary>
    public int StartColumn => Prompt.Length;

    /// <summary>
    /// The text changed: the index where it changed, the length that was replaced and the length that took its place (the completion
    /// follows the typing with it, see <c>CompletionSession.OnTextChanged</c>).
    /// </summary>
    public event Action<int, int, int> TextChanged;

    /// <summary>The caret or the selection moved (the grey suggestion follows the caret).</summary>
    public event Action CaretMoved;

    /// <summary>The first line of the document the prompt is on.</summary>
    public int StartLine { get; }

    public string Prompt { get; }

    public string Text => _text;

    public int CaretIndex => _caretIndex;

    public bool HasSelection => _anchor >= 0 && _anchor != _caretIndex;

    /// <summary>The start and the end of the selection in the order of the text (valid when <see cref="HasSelection"/>).</summary>
    public void GetSelection(out int start, out int end)
    {
        start = Math.Min(_anchor, _caretIndex);
        end = Math.Max(_anchor, _caretIndex);
    }

    public string SelectedText
    {
        get
        {
            if (!HasSelection)
            {
                return "";
            }
            GetSelection(out int start, out int end);
            return _text.Substring(start, end - start);
        }
    }

    /// <summary>The caret is at the start of a word (at the start of the line or after a blank).</summary>
    public bool IsAtWordStart => _caretIndex == 0 || (_caretIndex <= _text.Length && _text[_caretIndex - 1] == ' ');

    /// <summary>Replaces the whole text and puts the caret at <paramref name="caretIndex"/> (the end when it is outside the text).</summary>
    public void SetText(string text, int caretIndex = -1)
    {
        text = Clean(text);
        int oldLength = _text.Length;
        _text = text;
        _caretIndex = caretIndex >= 0 && caretIndex <= text.Length ? caretIndex : text.Length;
        _anchor = -1;
        Render();
        TextChanged?.Invoke(0, oldLength, text.Length);
        CaretMoved?.Invoke();
    }

    /// <summary>Replaces <paramref name="length"/> characters at <paramref name="start"/> with <paramref name="text"/>; the caret goes after it.</summary>
    public void Replace(int start, int length, string text)
    {
        text = Clean(text);
        start = Math.Max(0, Math.Min(start, _text.Length));
        length = Math.Max(0, Math.Min(length, _text.Length - start));
        _text = _text.Substring(0, start) + text + _text.Substring(start + length);
        _caretIndex = start + text.Length;
        _anchor = -1;
        Render();
        TextChanged?.Invoke(start, length, text.Length);
        CaretMoved?.Invoke();
    }

    /// <summary>Types text at the caret: in place of the selection, if there is one.</summary>
    public void Insert(string text)
    {
        if (HasSelection)
        {
            GetSelection(out int start, out int end);
            Replace(start, end - start, text);
        }
        else
        {
            Replace(_caretIndex, 0, text);
        }
    }

    /// <summary>Backspace: the selection, or the character before the caret (with <paramref name="word"/> the word before it).</summary>
    public void DeleteBackward(bool word)
    {
        if (DeleteSelection())
        {
            return;
        }
        if (_caretIndex == 0)
        {
            return;
        }
        int start = word ? FindWordBoundary(_caretIndex, backward: true) : PreviousIndex(_caretIndex);
        Replace(start, _caretIndex - start, "");
    }

    /// <summary>Delete: the selection, or the character after the caret (with <paramref name="word"/> the word after it).</summary>
    public void DeleteForward(bool word)
    {
        if (DeleteSelection())
        {
            return;
        }
        if (_caretIndex >= _text.Length)
        {
            return;
        }
        int end = word ? FindWordBoundary(_caretIndex, backward: false) : NextIndex(_caretIndex);
        Replace(_caretIndex, end - _caretIndex, "");
    }

    /// <summary>Removes the selection; false when there is none.</summary>
    public bool DeleteSelection()
    {
        if (!HasSelection)
        {
            return false;
        }
        GetSelection(out int start, out int end);
        Replace(start, end - start, "");
        return true;
    }

    /// <summary>Moves the caret (with <paramref name="extend"/> the selection follows it).</summary>
    public void MoveCaret(int index, bool extend)
    {
        index = Math.Max(0, Math.Min(index, _text.Length));
        if (extend)
        {
            if (_anchor < 0)
            {
                _anchor = _caretIndex;
            }
        }
        else
        {
            _anchor = -1;
        }
        _caretIndex = index;
        UpdateCaret();
        CaretMoved?.Invoke();
    }

    /// <summary>Selects the whole text.</summary>
    public void SelectAll()
    {
        _anchor = 0;
        _caretIndex = _text.Length;
        UpdateCaret();
        CaretMoved?.Invoke();
    }

    /// <summary>Left and Right move by one index (a surrogate pair is one character); with <paramref name="word"/> to the next word boundary.</summary>
    public int StepIndex(bool forward, bool word)
    {
        if (word)
        {
            return FindWordBoundary(_caretIndex, !forward);
        }
        return forward ? NextIndex(_caretIndex) : PreviousIndex(_caretIndex);
    }

    private int NextIndex(int index)
    {
        if (index >= _text.Length)
        {
            return _text.Length;
        }
        return char.IsHighSurrogate(_text[index]) && index + 1 < _text.Length && char.IsLowSurrogate(_text[index + 1]) ? index + 2 : index + 1;
    }

    private int PreviousIndex(int index)
    {
        if (index <= 0)
        {
            return 0;
        }
        return char.IsLowSurrogate(_text[index - 1]) && index >= 2 && char.IsHighSurrogate(_text[index - 2]) ? index - 2 : index - 1;
    }

    // What SshTerminalControl.FindWordBoundary did with IsWordChar of the terminal (letters, digits, '_', '.', '-').
    private int FindWordBoundary(int position, bool backward)
    {
        string text = _text;
        if (backward)
        {
            do
            {
                position--;
            }
            while (position > 0 && IsWordChar(text[position - 1]) && IsWordChar(text[position]));
            return Math.Max(position, 0);
        }
        do
        {
            position++;
        }
        while (position > 0 && position < text.Length && IsWordChar(text[position - 1]) && IsWordChar(text[position]));
        return Math.Min(position, text.Length);
    }

    /// <summary>A character of a word for Ctrl+Left, Ctrl+Right and Ctrl+Backspace.</summary>
    public static bool IsWordChar(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch == '_' || ch == '.' || ch == '-';
    }

    /// <summary>The place in the document of an index of the text.</summary>
    public TextDocument.TextPosition PositionAt(int index)
    {
        return InputRegionMap.PositionAt(_model.Document, StartLine, Prompt.Length, index);
    }

    /// <summary>The index of the text at a place of the document, brought into the text (a place in the prompt is its start).</summary>
    public int IndexOf(TextDocument.TextPosition position)
    {
        if (!Contains(position))
        {
            return position.Line < StartLine || (position.Line == StartLine && position.Column < Prompt.Length) ? 0 : _text.Length;
        }
        int index = InputRegionMap.IndexOf(_model.Document, StartLine, Prompt.Length, position);
        return Math.Max(0, Math.Min(index < 0 ? _text.Length : index, _text.Length));
    }

    /// <summary>The place is in the text of the line (not in the prompt, not in the output above).</summary>
    public bool Contains(TextDocument.TextPosition position)
    {
        return position.Line > StartLine || (position.Line == StartLine && position.Column >= Prompt.Length);
    }

    /// <summary>Writes the rows again (the screen got another width).</summary>
    public void Render()
    {
        WriteRows(_text);
        UpdateCaret();
    }

    /// <summary>
    /// Takes the typed text off the screen and leaves the prompt: the device's prompt stays as it was drawn, a local prompt goes with the
    /// text when <paramref name="keepLocalPrompt"/> is false (the old control replaced its whole line then). The caret is left where it was;
    /// the owner gives it back to the screen.
    /// </summary>
    public void Erase(bool keepLocalPrompt)
    {
        if (PromptOnScreen || keepLocalPrompt)
        {
            WriteRows("");
        }
        else
        {
            _model.ReplaceDocumentTail(StartLine, new[] { "" });
        }
    }

    private void WriteRows(string text)
    {
        int columns = Math.Max(1, _model.Columns);
        string all = Prompt + text;
        List<string> rows = new List<string>();
        for (int offset = 0; offset < all.Length; offset += columns)
        {
            rows.Add(all.Substring(offset, Math.Min(columns, all.Length - offset)));
        }
        if (rows.Count == 0)
        {
            rows.Add("");
        }
        if (PromptOnScreen && StartColumn < columns)
        {
            // The device's prompt stays as the device drew it, only the text after it is written again.
            rows[0] = rows[0].Substring(Math.Min(StartColumn, rows[0].Length));
            _model.ReplaceDocumentTail(StartLine, StartColumn, rows);
        }
        else
        {
            _model.ReplaceDocumentTail(StartLine, rows);
        }
    }

    private void UpdateCaret()
    {
        _model.SetPromptCaret(PositionAt(_caretIndex));
        if (HasSelection)
        {
            GetSelection(out int start, out int end);
            _model.Select(PositionAt(start), PositionAt(end));
        }
        else if (_model.HasSelection)
        {
            // A selection of the command line ends with it; one of the output above is left alone (to be copied).
            _model.GetSelection(out TextDocument.TextPosition start, out TextDocument.TextPosition end);
            if (Contains(start) && Contains(end))
            {
                _model.ClearSelection();
            }
        }
    }

    // The command line is one line: the line breaks of a pasted or completed text are not part of it.
    private static string Clean(string text)
    {
        return (text ?? "").Replace("\r", "").Replace("\n", "");
    }
}
