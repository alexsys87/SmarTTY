using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;

namespace SmarTTY.Controls;

public partial class EditableTextBrowser : ReadonlySmartScrollBrowser
{
    private abstract class EditCommand
    {
        public class ReplaceInLineCommand : EditCommand
        {
            private string _removedText;

            private string _newText;

            private int _line;

            private int _column;

            private int _removedLength;

            public ReplaceInLineCommand(EditableTextBrowser editableTextBrowser, int line, int column, int removedLength, string text)
                : base(editableTextBrowser)
            {
                this._newText = text;
                this._line = line;
                this._removedLength = removedLength;
                this._column = column;
            }

            public override void Undo(EditableTextBrowser editableTextBrowser)
            {
                editableTextBrowser.Doc.ReplaceInLine(_line, this._column, _newText.Length, _removedText);
                editableTextBrowser.OnLineChanged(_line, this._column, _newText.Length, _removedText.Length);
                RestoreSelection(editableTextBrowser);
            }

            public override void Execute(EditableTextBrowser editableTextBrowser)
            {
                _removedText = editableTextBrowser.Doc.ReplaceInLine(_line, this._column, _removedLength, _newText);
                editableTextBrowser.OnLineChanged(_line, this._column, _removedText.Length, _newText.Length);
                ((ReadonlyTextBrowser)editableTextBrowser)._hasSelection = false;
                editableTextBrowser.SetCaretColumn(this._column + _newText.Length);
                editableTextBrowser.InvalidateLine(_line);
                editableTextBrowser.UpdateLayout();
            }
        }

        public class ReplaceRangeCommand : EditCommand
        {
            private TextDocument.TextPosition _start;

            private TextDocument.TextPosition _end;

            private TextDocument.TextPosition _newEnd;

            private string _removedText;

            private string _newText;

            private bool _useExistingLineEnding;

            public ReplaceRangeCommand(EditableTextBrowser editableTextBrowser, TextDocument.TextPosition start, TextDocument.TextPosition end, string text, bool modified)
                : base(editableTextBrowser)
            {
                this._start = start;
                this._end = end;
                this._newText = text;
                this._useExistingLineEnding = modified;
            }

            public override void Execute(EditableTextBrowser editableTextBrowser)
            {
                if (_start != _end)
                {
                    _removedText = editableTextBrowser.Doc.GetRangeText(_start, _end);
                    editableTextBrowser.DeleteRange(_start, _end);
                }
                TextDocument.TextPosition textPosition = ((_newText != null && _newText != "") ? (_newEnd = editableTextBrowser.Doc.InsertText(_start, _newText, this._useExistingLineEnding)) : (_newEnd = _start));
                editableTextBrowser.OnRangeChanged(_start, _end, _newEnd);
                editableTextBrowser.UpdateLayout();
                editableTextBrowser.SetCaret(textPosition);
                editableTextBrowser.Redraw();
                base._hadSelection = false;
            }

            public override void Undo(EditableTextBrowser editableTextBrowser)
            {
                if (_start != _newEnd)
                {
                    editableTextBrowser.DeleteRange(_start, _newEnd);
                }
                if (_removedText != null && _removedText != "")
                {
                    editableTextBrowser.Doc.InsertText(_start, _removedText);
                }
                editableTextBrowser.OnRangeChanged(_start, _newEnd, _end);
                RestoreSelection(editableTextBrowser);
            }
        }

        public class IndentCommand : EditCommand
        {
            private int _levels;

            private LineRange _lines;

            private string[] _removedIndents;

            public IndentCommand(EditableTextBrowser editableTextBrowser, int levels)
                : base(editableTextBrowser)
            {
                this._lines = editableTextBrowser.SelectedLines;
                _levels = levels;
            }

            private void ApplyIndent(EditableTextBrowser editableTextBrowser, int levels)
            {
                if (levels > 0)
                {
                    string text = new string('\t', levels);
                    for (int line = this._lines.First; line <= this._lines.Last; line++)
                    {
                        editableTextBrowser.Doc.ReplaceInLine(line, 0, 0, text);
                        editableTextBrowser.OnLineChanged(line, 0, 0, text.Length);
                        editableTextBrowser.InvalidateLine(line);
                        editableTextBrowser.UpdateLayout();
                    }
                }
                else
                {
                    if (levels >= 0)
                    {
                        return;
                    }
                    _removedIndents = new string[this._lines.Count];
                    for (int currentLine = this._lines.First; currentLine <= this._lines.Last; currentLine++)
                    {
                        string lineText = editableTextBrowser.Doc.GetLineText(currentLine) + " \t";
                        if (lineText.Length == 0)
                        {
                            continue;
                        }
                        int indentWidth = 0;
                        for (int level = 0; level < -levels; level++)
                        {
                            if (lineText[indentWidth] == '\t')
                            {
                                indentWidth++;
                                continue;
                            }
                            if (lineText[indentWidth] != ' ')
                            {
                                break;
                            }
                            for (int tabOffset = 0; tabOffset < editableTextBrowser.TabSize; tabOffset++)
                            {
                                if (lineText[++indentWidth] != ' ')
                                {
                                    break;
                                }
                            }
                        }
                        if (indentWidth >= lineText.Length - 2)
                        {
                            indentWidth = lineText.Length - 1 - 2;
                        }
                        _removedIndents[currentLine - this._lines.First] = editableTextBrowser.Doc.ReplaceInLine(currentLine, 0, indentWidth, "");
                        editableTextBrowser.OnLineChanged(currentLine, 0, indentWidth, 0);
                        editableTextBrowser.InvalidateLine(currentLine);
                        editableTextBrowser.UpdateLayout();
                    }
                }
            }

            public override void Execute(EditableTextBrowser editableTextBrowser)
            {
                ApplyIndent(editableTextBrowser, _levels);
            }

            public override void Undo(EditableTextBrowser editableTextBrowser)
            {
                if (_levels < 0)
                {
                    for (int line = this._lines.First; line <= this._lines.Last; line++)
                    {
                        editableTextBrowser.Doc.ReplaceInLine(line, 0, 0, _removedIndents[line - this._lines.First]);
                        editableTextBrowser.OnLineChanged(line, 0, 0, _removedIndents[line - this._lines.First].Length);
                    }
                }
                else
                {
                    ApplyIndent(editableTextBrowser, -_levels);
                }
                RestoreSelection(editableTextBrowser);
            }
        }

        public class AddLinePrefixCommand : EditCommand
        {
            private string _prefix;

            private int _firstLine;

            private int _lastLine;

            public AddLinePrefixCommand(EditableTextBrowser editableTextBrowser, string text)
                : base(editableTextBrowser)
            {
                _prefix = text;
                if (_hadSelection)
                {
                    if (_caretBefore.Line < _selectionStartBefore.Line)
                    {
                        this._firstLine = _caretBefore.Line;
                        if (_selectionStartBefore.Column == 0)
                        {
                            this._lastLine = _selectionStartBefore.Line - 1;
                        }
                        else
                        {
                            this._lastLine = _selectionStartBefore.Line;
                        }
                    }
                    else
                    {
                        this._firstLine = _selectionStartBefore.Line;
                        if (_caretBefore.Column == 0)
                        {
                            this._lastLine = _caretBefore.Line - 1;
                        }
                        else
                        {
                            this._lastLine = _caretBefore.Line;
                        }
                    }
                }
                else
                {
                    this._firstLine = (this._lastLine = _caretBefore.Line);
                }
            }

            public override void Execute(EditableTextBrowser editableTextBrowser)
            {
                for (int line = this._firstLine; line <= _lastLine; line++)
                {
                    editableTextBrowser.Doc.ReplaceInLine(line, 0, 0, _prefix);
                    editableTextBrowser.OnLineChanged(line, 0, 0, _prefix.Length);
                }
                editableTextBrowser.Redraw();
            }

            public override void Undo(EditableTextBrowser editableTextBrowser)
            {
                for (int line = this._firstLine; line <= _lastLine; line++)
                {
                    editableTextBrowser.Doc.ReplaceInLine(line, 0, _prefix.Length, "");
                    editableTextBrowser.OnLineChanged(line, 0, _prefix.Length, 0);
                }
                RestoreSelection(editableTextBrowser);
            }
        }

        public class RemoveLinePrefixCommand : EditCommand
        {
            private string _prefix;

            private int _firstLine;

            private int _lastLine;

            private int[] _prefixColumns;

            public RemoveLinePrefixCommand(EditableTextBrowser editableTextBrowser, string text)
                : base(editableTextBrowser)
            {
                _prefix = text;
                if (_hadSelection)
                {
                    if (_caretBefore.Line < _selectionStartBefore.Line)
                    {
                        _firstLine = _caretBefore.Line;
                        if (_selectionStartBefore.Column == 0)
                        {
                            this._lastLine = _selectionStartBefore.Line - 1;
                        }
                        else
                        {
                            this._lastLine = _selectionStartBefore.Line;
                        }
                    }
                    else
                    {
                        _firstLine = _selectionStartBefore.Line;
                        if (_caretBefore.Column == 0)
                        {
                            this._lastLine = _caretBefore.Line - 1;
                        }
                        else
                        {
                            this._lastLine = _caretBefore.Line;
                        }
                    }
                }
                else
                {
                    _firstLine = (this._lastLine = _caretBefore.Line);
                }
            }

            public override void Execute(EditableTextBrowser editableTextBrowser)
            {
                _prefixColumns = new int[_lastLine - _firstLine + 1];
                for (int line = _firstLine; line <= _lastLine; line++)
                {
                    int prefixColumn = FindPrefix(editableTextBrowser.EditableDocument.GetLineText(line), _prefix);
                    if (prefixColumn != -1)
                    {
                        _prefixColumns[line - _firstLine] = prefixColumn;
                        editableTextBrowser.Doc.ReplaceInLine(line, prefixColumn, _prefix.Length, "");
                        editableTextBrowser.OnLineChanged(line, prefixColumn, _prefix.Length, 0);
                    }
                }
                editableTextBrowser.Redraw();
            }

            public override void Undo(EditableTextBrowser editableTextBrowser)
            {
                for (int line = _firstLine; line <= _lastLine; line++)
                {
                    editableTextBrowser.Doc.ReplaceInLine(line, _prefixColumns[line - _firstLine], 0, _prefix);
                    editableTextBrowser.OnLineChanged(line, _prefixColumns[line - _firstLine], 0, _prefix.Length);
                }
                RestoreSelection(editableTextBrowser);
            }
        }

        protected TextDocument.TextPosition _caretBefore;

        protected TextDocument.TextPosition _selectionStartBefore;

        protected bool _hadSelection;

        protected bool _overwriteBefore;

        private EditCommand(EditableTextBrowser editableTextBrowser)
        {
            _overwriteBefore = editableTextBrowser.OverwriteMode;
            _caretBefore = editableTextBrowser.CaretPosition;
            _hadSelection = ((ReadonlyTextBrowser)editableTextBrowser)._hasSelection;
            _selectionStartBefore = editableTextBrowser.SelectionStart;
        }

        protected void RestoreSelection(EditableTextBrowser editableTextBrowser)
        {
            editableTextBrowser.SetSelection(_hadSelection, _selectionStartBefore);
            editableTextBrowser.SetCaret(_caretBefore);
        }

        public abstract void Undo(EditableTextBrowser editableTextBrowser);

        public abstract void Execute(EditableTextBrowser editableTextBrowser);
    }

    public delegate void ModifiedChangedHandler(EditableTextBrowser editableTextBrowser, bool modified);

    public delegate void LineChangedHandler(EditableTextBrowser editableTextBrowser, int line, int column, int addedLength, int removedLength);

    public delegate void RangeChangedHandler(EditableTextBrowser editableTextBrowser, TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd);

    private Stack<EditCommand> _undoStack = new Stack<EditCommand>();

    private Stack<EditCommand> _redoStack = new Stack<EditCommand>();

    private bool _readOnly;

    protected bool _useExistingLineEnding = true;

    private bool _modified;

    public bool ReadOnly
    {
        get
        {
            return _readOnly;
        }
        set
        {
            _readOnly = value;
        }
    }

    private EditableTextDocument Doc => Document;

    protected internal EditableTextDocument EditableDocument => Document;

    public bool IsModified
    {
        get
        {
            return _modified;
        }
        set
        {
            if (value != _modified)
            {
                _modified = value;
                OnModifiedChanged();
            }
        }
    }

    public event ModifiedChangedHandler ModifiedChanged;

    public event LineChangedHandler LineChanged;

    public event RangeChangedHandler RangeChanged;

    private void ExecuteCommand(EditCommand editCommand)
    {
        editCommand.Execute(this);
        if (_redoStack.Count > 0)
        {
            _redoStack.Clear();
        }
        _undoStack.Push(editCommand);
    }

    public void Undo()
    {
        if (_undoStack.Count > 0)
        {
            EditCommand editCommand = _undoStack.Pop();
            editCommand.Undo(this);
            _redoStack.Push(editCommand);
            Redraw();
        }
    }

    public void Redo()
    {
        if (_redoStack.Count > 0)
        {
            EditCommand editCommand = _redoStack.Pop();
            editCommand.Execute(this);
            _undoStack.Push(editCommand);
        }
        Redraw();
    }

    public EditableTextBrowser()
    {
        InitializeComponent();
    }

    private void DeleteRange(TextDocument.TextPosition start, TextDocument.TextPosition end)
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
        SetCaret(start);
        base._hasSelection = false;
    }

    public virtual void ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text)
    {
        if (!_readOnly)
        {
            ReplaceTextCore(start, end, select, text);
        }
    }

    protected void ReplaceTextCore(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string replacement)
    {
        if (select)
        {
            end = start;
        }
        if (SingleLine)
        {
            replacement = replacement.Replace("\r\n", "").Replace("\n", "");
        }
        if (start.Column == -1)
        {
            start.Column = GetLineOrNull(start.Line).Length;
        }
        if (end.Column == -1)
        {
            end.Column = GetLineOrNull(end.Line).Length;
        }
        if (replacement == "" && end.Column == 0 && end.Line == start.Line + 1 && start.Column == EditableDocument.GetLineText(start.Line).Length)
        {
            string text = GetIndentation(start.Line);
            string lineText = EditableDocument.GetLineText(end.Line);
            int commonPrefixLength;
            for (commonPrefixLength = 0; commonPrefixLength < Math.Min(lineText.Length, text.Length) && lineText[commonPrefixLength] == text[commonPrefixLength]; commonPrefixLength++)
            {
            }
            end.Column = commonPrefixLength;
        }
        if (start.Line == end.Line && replacement.IndexOfAny(new char[2] { '\r', '\n' }) == -1)
        {
            ExecuteCommand(new EditCommand.ReplaceInLineCommand(this, start.Line, Math.Min(start.Column, end.Column), Math.Abs(start.Column - end.Column), replacement));
            return;
        }
        TextDocument.TextPosition rangeStart;
        TextDocument.TextPosition rangeEnd;
        if (select)
        {
            rangeStart = (rangeEnd = start);
        }
        else if (start < end)
        {
            rangeStart = start;
            rangeEnd = end;
        }
        else
        {
            rangeStart = end;
            rangeEnd = start;
        }
        ExecuteCommand(new EditCommand.ReplaceRangeCommand(this, rangeStart, rangeEnd, replacement, _useExistingLineEnding));
    }

    public void Indent(int levels)
    {
        ExecuteCommand(new EditCommand.IndentCommand(this, levels));
    }

    private static int FindPrefix(string text, string prefix)
    {
        for (int offset = 0; offset < text.Length - prefix.Length + 1; offset++)
        {
            if (text[offset] != ' ' && text[offset] != '\t')
            {
                if (!(text.Substring(offset, prefix.Length) == prefix))
                {
                    break;
                }
                return offset;
            }
        }
        return -1;
    }

    public void ToggleLinePrefix(string text)
    {
        int line = CaretPosition.Line;
        if (base.HasSelection && SelectionStart.Line < line)
        {
            line = SelectionStart.Line;
        }
        if (FindPrefix(EditableDocument.GetLineText(line), text) == -1)
        {
            ExecuteCommand(new EditCommand.AddLinePrefixCommand(this, text));
        }
        else
        {
            ExecuteCommand(new EditCommand.RemoveLinePrefixCommand(this, text));
        }
    }

    protected virtual void OnLineChanged(int line, int column, int addedLength, int removedLength)
    {
        if (line == CaretPosition.Line)
        {
            string text = EditableDocument.GetLineText(line);
            if (CaretPosition.Column > text.Length)
            {
                SetCaretColumn(text.Length);
            }
        }
        if (LineChanged != null)
        {
            LineChanged(this, line, column, addedLength, removedLength);
        }
        IsModified = true;
    }

    protected virtual void OnRangeChanged(TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd)
    {
        if (RangeChanged != null)
        {
            RangeChanged(this, start, oldEnd, newEnd);
        }
        IsModified = true;
    }

    protected virtual void OnModifiedChanged()
    {
        if (ModifiedChanged != null)
        {
            ModifiedChanged(this, _modified);
        }
    }

    protected override void OnTextReplaced()
    {
        base.OnTextReplaced();
        _undoStack.Clear();
        _redoStack.Clear();
    }

    protected void ClearUndo()
    {
        _undoStack.Clear();
    }
}
