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

            public ReplaceInLineCommand(EditableTextBrowser editableTextBrowser, int value2, int value3, int value4, string text)
                : base(editableTextBrowser)
            {
                this._newText = text;
                this._line = value2;
                this._removedLength = value4;
                this._column = value3;
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

            public ReplaceRangeCommand(EditableTextBrowser editableTextBrowser, TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, string text, bool flag)
                : base(editableTextBrowser)
            {
                this._start = textPosition;
                this._end = textPosition2;
                this._newText = text;
                this._useExistingLineEnding = flag;
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

            public IndentCommand(EditableTextBrowser editableTextBrowser, int value2)
                : base(editableTextBrowser)
            {
                this._lines = editableTextBrowser.SelectedLines;
                _levels = value2;
            }

            private void ApplyIndent(EditableTextBrowser editableTextBrowser, int value2)
            {
                if (value2 > 0)
                {
                    string text = new string('\t', value2);
                    for (int num = this._lines.First; num <= this._lines.Last; num++)
                    {
                        editableTextBrowser.Doc.ReplaceInLine(num, 0, 0, text);
                        editableTextBrowser.OnLineChanged(num, 0, 0, text.Length);
                        editableTextBrowser.InvalidateLine(num);
                        editableTextBrowser.UpdateLayout();
                    }
                }
                else
                {
                    if (value2 >= 0)
                    {
                        return;
                    }
                    _removedIndents = new string[this._lines.Count];
                    for (int num2 = this._lines.First; num2 <= this._lines.Last; num2++)
                    {
                        string text2 = editableTextBrowser.Doc.GetLineText(num2) + " \t";
                        if (text2.Length == 0)
                        {
                            continue;
                        }
                        int num3 = 0;
                        for (int num4 = 0; num4 < -value2; num4++)
                        {
                            if (text2[num3] == '\t')
                            {
                                num3++;
                                continue;
                            }
                            if (text2[num3] != ' ')
                            {
                                break;
                            }
                            for (int num5 = 0; num5 < editableTextBrowser.TabSize; num5++)
                            {
                                if (text2[++num3] != ' ')
                                {
                                    break;
                                }
                            }
                        }
                        if (num3 >= text2.Length - 2)
                        {
                            num3 = text2.Length - 1 - 2;
                        }
                        _removedIndents[num2 - this._lines.First] = editableTextBrowser.Doc.ReplaceInLine(num2, 0, num3, "");
                        editableTextBrowser.OnLineChanged(num2, 0, num3, 0);
                        editableTextBrowser.InvalidateLine(num2);
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
                    for (int num = this._lines.First; num <= this._lines.Last; num++)
                    {
                        editableTextBrowser.Doc.ReplaceInLine(num, 0, 0, _removedIndents[num - this._lines.First]);
                        editableTextBrowser.OnLineChanged(num, 0, 0, _removedIndents[num - this._lines.First].Length);
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
                for (int num = this._firstLine; num <= _lastLine; num++)
                {
                    editableTextBrowser.Doc.ReplaceInLine(num, 0, 0, _prefix);
                    editableTextBrowser.OnLineChanged(num, 0, 0, _prefix.Length);
                }
                editableTextBrowser.Redraw();
            }

            public override void Undo(EditableTextBrowser editableTextBrowser)
            {
                for (int num = this._firstLine; num <= _lastLine; num++)
                {
                    editableTextBrowser.Doc.ReplaceInLine(num, 0, _prefix.Length, "");
                    editableTextBrowser.OnLineChanged(num, 0, _prefix.Length, 0);
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
                for (int num = _firstLine; num <= _lastLine; num++)
                {
                    int num2 = FindPrefix(editableTextBrowser.EditableDocument.GetLineText(num), _prefix);
                    if (num2 != -1)
                    {
                        _prefixColumns[num - _firstLine] = num2;
                        editableTextBrowser.Doc.ReplaceInLine(num, num2, _prefix.Length, "");
                        editableTextBrowser.OnLineChanged(num, num2, _prefix.Length, 0);
                    }
                }
                editableTextBrowser.Redraw();
            }

            public override void Undo(EditableTextBrowser editableTextBrowser)
            {
                for (int num = _firstLine; num <= _lastLine; num++)
                {
                    editableTextBrowser.Doc.ReplaceInLine(num, _prefixColumns[num - _firstLine], 0, _prefix);
                    editableTextBrowser.OnLineChanged(num, _prefixColumns[num - _firstLine], 0, _prefix.Length);
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

    public delegate void ModifiedChangedHandler(EditableTextBrowser editableTextBrowser, bool flag);

    public delegate void LineChangedHandler(EditableTextBrowser editableTextBrowser, int value2, int value3, int value4, int value5);

    public delegate void RangeChangedHandler(EditableTextBrowser editableTextBrowser, TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, TextDocument.TextPosition textPosition3);

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

    private void DeleteRange(TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2)
    {
        if (textPosition.Line == textPosition2.Line)
        {
            Doc.ReplaceInLine(textPosition.Line, textPosition.Column, textPosition2.Column - textPosition.Column, "");
        }
        else
        {
            Doc.ReplaceInLine(textPosition.Line, textPosition.Column, -1, "");
            Doc.ReplaceInLine(textPosition2.Line, 0, textPosition2.Column, "");
            int num = textPosition2.Line - textPosition.Line - 1;
            if (num != 0)
            {
                Doc.RemoveLines(textPosition.Line + 1, num);
                textPosition2.Line -= num;
            }
            Doc.JoinWithNextLine(textPosition.Line);
        }
        SetCaret(textPosition);
        base._hasSelection = false;
    }

    public virtual void ReplaceText(TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, bool flag, string text)
    {
        if (!_readOnly)
        {
            ReplaceTextCore(textPosition, textPosition2, flag, text);
        }
    }

    protected void ReplaceTextCore(TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, bool flag, string text3)
    {
        if (flag)
        {
            textPosition2 = textPosition;
        }
        if (SingleLine)
        {
            text3 = text3.Replace("\r\n", "").Replace("\n", "");
        }
        if (textPosition.Column == -1)
        {
            textPosition.Column = GetLineOrNull(textPosition.Line).Length;
        }
        if (textPosition2.Column == -1)
        {
            textPosition2.Column = GetLineOrNull(textPosition2.Line).Length;
        }
        if (text3 == "" && textPosition2.Column == 0 && textPosition2.Line == textPosition.Line + 1 && textPosition.Column == EditableDocument.GetLineText(textPosition.Line).Length)
        {
            string text = GetIndentation(textPosition.Line);
            string text2 = EditableDocument.GetLineText(textPosition2.Line);
            int num;
            for (num = 0; num < Math.Min(text2.Length, text.Length) && text2[num] == text[num]; num++)
            {
            }
            textPosition2.Column = num;
        }
        if (textPosition.Line == textPosition2.Line && text3.IndexOfAny(new char[2] { '\r', '\n' }) == -1)
        {
            ExecuteCommand(new EditCommand.ReplaceInLineCommand(this, textPosition.Line, Math.Min(textPosition.Column, textPosition2.Column), Math.Abs(textPosition.Column - textPosition2.Column), text3));
            return;
        }
        TextDocument.TextPosition textPosition3;
        TextDocument.TextPosition textPosition4;
        if (flag)
        {
            textPosition3 = (textPosition4 = textPosition);
        }
        else if (textPosition < textPosition2)
        {
            textPosition3 = textPosition;
            textPosition4 = textPosition2;
        }
        else
        {
            textPosition3 = textPosition2;
            textPosition4 = textPosition;
        }
        ExecuteCommand(new EditCommand.ReplaceRangeCommand(this, textPosition3, textPosition4, text3, _useExistingLineEnding));
    }

    public void Indent(int value2)
    {
        ExecuteCommand(new EditCommand.IndentCommand(this, value2));
    }

    private static int FindPrefix(string text, string text2)
    {
        for (int num = 0; num < text.Length - text2.Length + 1; num++)
        {
            if (text[num] != ' ' && text[num] != '\t')
            {
                if (!(text.Substring(num, text2.Length) == text2))
                {
                    break;
                }
                return num;
            }
        }
        return -1;
    }

    public void ToggleLinePrefix(string text)
    {
        int num = CaretPosition.Line;
        if (base.HasSelection && SelectionStart.Line < num)
        {
            num = SelectionStart.Line;
        }
        if (FindPrefix(EditableDocument.GetLineText(num), text) == -1)
        {
            ExecuteCommand(new EditCommand.AddLinePrefixCommand(this, text));
        }
        else
        {
            ExecuteCommand(new EditCommand.RemoveLinePrefixCommand(this, text));
        }
    }

    protected virtual void OnLineChanged(int value2, int value3, int value4, int value5)
    {
        if (value2 == CaretPosition.Line)
        {
            string text = EditableDocument.GetLineText(value2);
            if (CaretPosition.Column > text.Length)
            {
                SetCaretColumn(text.Length);
            }
        }
        if (LineChanged != null)
        {
            LineChanged(this, value2, value3, value4, value5);
        }
        IsModified = true;
    }

    protected virtual void OnRangeChanged(TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, TextDocument.TextPosition textPosition3)
    {
        if (RangeChanged != null)
        {
            RangeChanged(this, textPosition, textPosition2, textPosition3);
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
