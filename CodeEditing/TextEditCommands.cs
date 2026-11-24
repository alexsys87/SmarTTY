using SmarTTY.TextEditing;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The text view as its edit commands see it (made of the calls the commands of <c>Controls/EditableTextBrowser</c> made on it): the document, the
/// caret and the selection, the typing mode, the indentation settings, and what the view does after an edit (its change events, the caret, the
/// redraw and the layout).
/// </summary>
public interface ITextEditView
{
    EditableTextDocument Document { get; }

    TextDocument.TextPosition CaretPosition { get; }

    /// <summary>There is a selection (an edit switches it off).</summary>
    bool HasSelection { get; set; }

    TextDocument.TextPosition SelectionStart { get; }

    bool OverwriteMode { get; }

    /// <summary>Tab and Indent insert spaces instead of a tab character.</summary>
    bool InsertSpaces { get; }

    int TabSize { get; }

    /// <summary>The lines the selection covers (none: 0 and -1).</summary>
    void GetSelectedLines(out int first, out int last);

    void SetSelection(bool select, TextDocument.TextPosition start);

    void SetCaret(TextDocument.TextPosition position);

    /// <summary>Moves the caret to a column of the line it is on.</summary>
    void SetCaretColumn(int column);

    /// <summary>A part of a line changed (the view's event and what follows it).</summary>
    void OnLineChanged(int line, int column, int addedLength, int removedLength);

    /// <summary>A range of the text changed (the view's event and what follows it).</summary>
    void OnRangeChanged(TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd);

    void InvalidateLine(int line);

    void UpdateLayout();

    void Redraw();
}

/// <summary>
/// The edits of the text view that the undo history records: a replacement within a line, a replacement of a range (which may cross lines), and the
/// indent or unindent of lines; each knows how to undo itself and puts the caret and the selection back as they were. They are the nested
/// <c>EditCommand</c> classes of <c>Controls/EditableTextBrowser</c> with its <c>DeleteRange</c>, moved here as they were (stage 3D) so that a text
/// view of another toolkit edits with the same code; the view is reached through <see cref="ITextEditView"/>.
/// </summary>
public abstract class TextEditCommand : IUndoStep
{
    private readonly ITextEditView _view;

    private readonly TextDocument.TextPosition _caretBefore;

    private readonly TextDocument.TextPosition _selectionStartBefore;

    // Whether there was a selection before the edit (a range replacement drops it, so that the undo puts none back).
    protected bool _hadSelection;

    protected TextEditCommand(ITextEditView view)
    {
        _view = view;
        _caretBefore = view.CaretPosition;
        _hadSelection = view.HasSelection;
        _selectionStartBefore = view.SelectionStart;
    }

    protected ITextEditView View => _view;

    protected EditableTextDocument Doc => _view.Document;

    /// <summary>Puts the selection and the caret back as they were before the edit.</summary>
    protected void RestoreSelection()
    {
        _view.SetSelection(_hadSelection, _selectionStartBefore);
        _view.SetCaret(_caretBefore);
    }

    public abstract void Execute();

    public abstract void Undo();

    /// <summary>Removes the text between two positions and puts the caret at the start (the selection goes).</summary>
    protected void DeleteRange(TextDocument.TextPosition start, TextDocument.TextPosition end)
    {
        if (start.Line == end.Line)
        {
            Doc.ReplaceInLine(start.Line, start.Column, end.Column - start.Column, "");
        }
        else
        {
            Doc.ReplaceInLine(start.Line, start.Column, -1, "");
            Doc.ReplaceInLine(end.Line, 0, end.Column, "");
            int removedLineCount = end.Line - start.Line - 1;
            if (removedLineCount != 0)
            {
                Doc.RemoveLines(start.Line + 1, removedLineCount);
                end.Line -= removedLineCount;
            }
            Doc.JoinWithNextLine(start.Line);
        }
        _view.SetCaret(start);
        _view.HasSelection = false;
    }
}

/// <summary>Replaces a part of one line.</summary>
public sealed class ReplaceInLineCommand : TextEditCommand
{
    private string _removedText;

    private readonly string _newText;

    private readonly int _line;

    private readonly int _column;

    private readonly int _removedLength;

    public ReplaceInLineCommand(ITextEditView view, int line, int column, int removedLength, string text)
        : base(view)
    {
        _newText = text;
        _line = line;
        _removedLength = removedLength;
        _column = column;
    }

    public override void Undo()
    {
        Doc.ReplaceInLine(_line, _column, _newText.Length, _removedText);
        View.OnLineChanged(_line, _column, _newText.Length, _removedText.Length);
        RestoreSelection();
    }

    public override void Execute()
    {
        _removedText = Doc.ReplaceInLine(_line, _column, _removedLength, _newText);
        View.OnLineChanged(_line, _column, _removedText.Length, _newText.Length);
        View.HasSelection = false;
        View.SetCaretColumn(_column + _newText.Length);
        View.InvalidateLine(_line);
        View.UpdateLayout();
    }
}

/// <summary>Replaces a range of the text, which may cross lines.</summary>
public sealed class ReplaceRangeCommand : TextEditCommand
{
    private readonly TextDocument.TextPosition _start;

    private readonly TextDocument.TextPosition _end;

    private TextDocument.TextPosition _newEnd;

    private string _removedText;

    private readonly string _newText;

    private readonly bool _useExistingLineEnding;

    public ReplaceRangeCommand(ITextEditView view, TextDocument.TextPosition start, TextDocument.TextPosition end, string text, bool useExistingLineEnding)
        : base(view)
    {
        _start = start;
        _end = end;
        _newText = text;
        _useExistingLineEnding = useExistingLineEnding;
    }

    public override void Execute()
    {
        if (_start != _end)
        {
            // Keep the real line terminators: with the default "\r\n" an undo of a multi-line
            // deletion would put CRLF endings into an LF file.
            _removedText = Doc.GetRangeText(_start, _end, Doc.GetEffectiveLineTerminator);
            DeleteRange(_start, _end);
        }
        TextDocument.TextPosition textPosition = ((_newText != null && _newText != "") ? (_newEnd = Doc.InsertText(_start, _newText, _useExistingLineEnding)) : (_newEnd = _start));
        View.OnRangeChanged(_start, _end, _newEnd);
        View.UpdateLayout();
        View.SetCaret(textPosition);
        View.Redraw();
        _hadSelection = false;
    }

    public override void Undo()
    {
        if (_start != _newEnd)
        {
            DeleteRange(_start, _newEnd);
        }
        if (_removedText != null && _removedText != "")
        {
            Doc.InsertText(_start, _removedText);
        }
        View.OnRangeChanged(_start, _newEnd, _end);
        RestoreSelection();
    }
}

/// <summary>Indents or unindents the selected lines (the caret line without a selection).</summary>
public sealed class IndentCommand : TextEditCommand
{
    private readonly int _levels;

    private readonly int _first;

    private readonly int _last;

    private string[] _removedIndents;

    // One indentation level: a tab or TabSize spaces (InsertSpaces).
    private readonly string _indentUnit;

    // Lines that got an indent (blank lines are left alone), so that undo removes exactly that.
    private bool[] _indentedLines;

    public IndentCommand(ITextEditView view, int levels)
        : base(view)
    {
        _indentUnit = IndentEdits.IndentUnit(view.InsertSpaces, view.TabSize);
        view.GetSelectedLines(out _first, out _last);
        if (_last < _first)
        {
            // Without a selection (Shift+Tab) the caret line is (un)indented.
            int caretLine = view.CaretPosition.Line;
            _first = caretLine;
            _last = caretLine;
        }
        _levels = levels;
    }

    private int Count => _last - _first + 1;

    private void ApplyIndent(int levels)
    {
        if (levels > 0)
        {
            string text = IndentEdits.IndentText(_indentUnit, levels);
            _indentedLines = new bool[Count];
            for (int line = _first; line <= _last; line++)
            {
                if (Doc.GetLineText(line).Trim().Length == 0)
                {
                    // Indenting a blank line would only add trailing whitespace.
                    continue;
                }
                _indentedLines[line - _first] = true;
                Doc.ReplaceInLine(line, 0, 0, text);
                View.OnLineChanged(line, 0, 0, text.Length);
                View.InvalidateLine(line);
                View.UpdateLayout();
            }
        }
        else
        {
            if (levels >= 0)
            {
                return;
            }
            _removedIndents = new string[Count];
            for (int currentLine = _first; currentLine <= _last; currentLine++)
            {
                int indentWidth = IndentEdits.UnindentWidth(Doc.GetLineText(currentLine), -levels, View.TabSize);
                _removedIndents[currentLine - _first] = Doc.ReplaceInLine(currentLine, 0, indentWidth, "");
                View.OnLineChanged(currentLine, 0, indentWidth, 0);
                View.InvalidateLine(currentLine);
                View.UpdateLayout();
            }
        }
    }

    public override void Execute()
    {
        ApplyIndent(_levels);
    }

    public override void Undo()
    {
        if (_levels < 0)
        {
            for (int line = _first; line <= _last; line++)
            {
                Doc.ReplaceInLine(line, 0, 0, _removedIndents[line - _first]);
                View.OnLineChanged(line, 0, 0, _removedIndents[line - _first].Length);
            }
        }
        else if (_indentedLines != null)
        {
            // Remove exactly the indent that Execute inserted.
            int length = IndentEdits.IndentText(_indentUnit, _levels).Length;
            for (int line = _first; line <= _last; line++)
            {
                if (_indentedLines[line - _first])
                {
                    Doc.ReplaceInLine(line, 0, length, "");
                    View.OnLineChanged(line, 0, length, 0);
                    View.InvalidateLine(line);
                }
            }
            View.UpdateLayout();
        }
        RestoreSelection();
    }
}
