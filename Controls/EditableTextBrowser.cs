using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Text;

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
                    // Keep the real line terminators: with the default "\r\n" an undo of a multi-line
                    // deletion would put CRLF endings into an LF file.
                    _removedText = editableTextBrowser.Doc.GetRangeText(_start, _end, editableTextBrowser.Doc.GetEffectiveLineTerminator);
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

            // One indentation level: a tab or TabSize spaces (InsertSpaces).
            private readonly string _indentUnit;

            // Lines that got an indent (blank lines are left alone), so that undo removes exactly that.
            private bool[] _indentedLines;

            public IndentCommand(EditableTextBrowser editableTextBrowser, int levels)
                : base(editableTextBrowser)
            {
                _indentUnit = editableTextBrowser.InsertSpaces ? new string(' ', Math.Max(1, editableTextBrowser.TabSize)) : "\t";
                this._lines = editableTextBrowser.SelectedLines;
                if (this._lines.Count == 0)
                {
                    // Without a selection (Shift+Tab) the caret line is (un)indented.
                    int caretLine = editableTextBrowser.CaretPosition.Line;
                    this._lines = new LineRange(caretLine, caretLine);
                }
                _levels = levels;
            }

            private void ApplyIndent(EditableTextBrowser editableTextBrowser, int levels)
            {
                if (levels > 0)
                {
                    string text = IndentText(levels);
                    _indentedLines = new bool[this._lines.Count];
                    for (int line = this._lines.First; line <= this._lines.Last; line++)
                    {
                        if (editableTextBrowser.Doc.GetLineText(line).Trim().Length == 0)
                        {
                            // Indenting a blank line would only add trailing whitespace.
                            continue;
                        }
                        _indentedLines[line - this._lines.First] = true;
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
                        // lineText carries 2 sentinel characters; never remove more than the real line
                        // (a whitespace-only line such as a single tab must still be unindented).
                        if (indentWidth > lineText.Length - 2)
                        {
                            indentWidth = lineText.Length - 2;
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
                else if (_indentedLines != null)
                {
                    // Remove exactly the indent that Execute inserted.
                    int length = IndentText(_levels).Length;
                    for (int line = this._lines.First; line <= this._lines.Last; line++)
                    {
                        if (_indentedLines[line - this._lines.First])
                        {
                            editableTextBrowser.Doc.ReplaceInLine(line, 0, length, "");
                            editableTextBrowser.OnLineChanged(line, 0, length, 0);
                            editableTextBrowser.InvalidateLine(line);
                        }
                    }
                    editableTextBrowser.UpdateLayout();
                }
                RestoreSelection(editableTextBrowser);
            }

            private string IndentText(int levels)
            {
                StringBuilder text = new StringBuilder();
                for (int i = 0; i < levels; i++)
                {
                    text.Append(_indentUnit);
                }
                return text.ToString();
            }
        }

        /// <summary>Several edits undone and redone as one step (e.g. Replace All).</summary>
        public class CompoundCommand : EditCommand
        {
            public readonly List<EditCommand> Commands = new List<EditCommand>();

            public CompoundCommand(EditableTextBrowser editableTextBrowser)
                : base(editableTextBrowser)
            {
            }

            public override void Execute(EditableTextBrowser editableTextBrowser)
            {
                foreach (EditCommand command in Commands)
                {
                    command.Execute(editableTextBrowser);
                }
            }

            public override void Undo(EditableTextBrowser editableTextBrowser)
            {
                for (int i = Commands.Count - 1; i >= 0; i--)
                {
                    Commands[i].Undo(editableTextBrowser);
                }
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

    /// <summary>Tab and Indent insert TabSize spaces instead of a tab character.</summary>
    public bool InsertSpaces { get; set; }

    private bool _modified;

    // The undo step the text matched when it was last saved (null: the empty undo history). Undoing or
    // redoing back to it clears IsModified. NeverSaved is a marker no undo step can match.
    private static readonly object NeverSaved = new object();

    private object _savedUndoStep;

    // > 0 while an edit command runs: its own change notifications must not overwrite the saved marker.
    private int _commandDepth;

    // Collects the edits of ExecuteAsSingleUndoStep.
    private EditCommand.CompoundCommand _compoundCommand;

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

    public virtual bool IsModified
    {
        get
        {
            return _modified;
        }
        set
        {
            _savedUndoStep = value ? NeverSaved : CurrentUndoStep;
            SetModified(value);
        }
    }

    private object CurrentUndoStep => (_undoStack.Count > 0) ? _undoStack.Peek() : null;

    /// <summary>
    /// Identifies the current text for <see cref="MarkSaved"/>. A save that runs in the background takes it
    /// before reading the text, so edits made meanwhile keep the document modified.
    /// </summary>
    public object CurrentVersion => CurrentUndoStep;

    /// <summary>Records that the text of <paramref name="version"/> (from <see cref="CurrentVersion"/>) has been saved.</summary>
    public void MarkSaved(object version)
    {
        _savedUndoStep = version;
        SetModified(CurrentUndoStep != version);
    }

    private void SetModified(bool modified)
    {
        if (modified != _modified)
        {
            _modified = modified;
            OnModifiedChanged();
        }
    }

    private void RunCommand(Action action)
    {
        _commandDepth++;
        try
        {
            action();
        }
        finally
        {
            _commandDepth--;
        }
        SetModified(CurrentUndoStep != _savedUndoStep);
    }

    public event ModifiedChangedHandler ModifiedChanged;

    public event LineChangedHandler LineChanged;

    public event RangeChangedHandler RangeChanged;

    private void ExecuteCommand(EditCommand editCommand)
    {
        if (_compoundCommand != null)
        {
            editCommand.Execute(this);
            _compoundCommand.Commands.Add(editCommand);
            return;
        }
        RunCommand(() =>
        {
            editCommand.Execute(this);
            if (_redoStack.Count > 0)
            {
                _redoStack.Clear();
            }
            _undoStack.Push(editCommand);
        });
    }

    /// <summary>Runs several edits (ReplaceText calls) so that a single Undo reverts all of them.</summary>
    public void ExecuteAsSingleUndoStep(Action edits)
    {
        if (_readOnly)
        {
            return;
        }
        if (_compoundCommand != null)
        {
            edits();
            return;
        }
        EditCommand.CompoundCommand compound = new EditCommand.CompoundCommand(this);
        RunCommand(() =>
        {
            _compoundCommand = compound;
            try
            {
                edits();
            }
            finally
            {
                _compoundCommand = null;
                if (compound.Commands.Count > 0)
                {
                    _redoStack.Clear();
                    _undoStack.Push(compound);
                }
            }
        });
    }

    /// <summary>
    /// Replaces every occurrence of <paramref name="searchText"/> as one undo step and returns how many were replaced.
    /// The caret stays where it was (clamped to its line).
    /// </summary>
    public int ReplaceAll(string searchText, string replacement, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(searchText))
        {
            return 0;
        }
        return ReplaceAll(TextMatcher.Literal(searchText, ignoreCase), replacement, TextDocument.TextPosition.Invalid, TextDocument.TextPosition.Invalid, out _);
    }

    /// <summary>
    /// Replaces every match of <paramref name="matcher"/> (a regular expression replacement may use $1, ${name}) as one
    /// undo step and returns how many were replaced. A valid <paramref name="scopeStart"/> and <paramref name="scopeEnd"/>
    /// limit it to that range, and <paramref name="newScopeEnd"/> is where the end of the range is afterwards.
    /// The caret stays where it was (clamped to its line).
    /// </summary>
    public int ReplaceAll(TextMatcher matcher, string replacement, TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd, out TextDocument.TextPosition newScopeEnd)
    {
        newScopeEnd = scopeEnd;
        if (_readOnly || matcher == null)
        {
            return 0;
        }
        replacement = replacement ?? "";
        bool scoped = scopeStart.IsValid && scopeEnd.IsValid;
        TextDocument.TextPosition caret = CaretPosition;
        int count = 0;
        int firstLine = scoped ? Math.Max(0, scopeStart.Line) : 0;
        int lastLine = scoped ? scopeEnd.Line : (Doc.LineCount - 1);
        int endLine = scopeEnd.Line;
        int endColumn = scopeEnd.Column;
        ExecuteAsSingleUndoStep(() =>
        {
            for (int line = firstLine; line <= lastLine && line < Doc.LineCount; line++)
            {
                string text = Doc.GetLineText(line);
                int from = (scoped && line == scopeStart.Line) ? scopeStart.Column : 0;
                int to = (scoped && line == scopeEnd.Line) ? Math.Min(scopeEnd.Column, text.Length) : text.Length;
                string replaced = matcher.ReplaceAll(text, from, to, replacement, out int lineCount, out int newTo);
                if (lineCount == 0)
                {
                    continue;
                }
                count += lineCount;
                ReplaceText(new TextDocument.TextPosition(0, line), new TextDocument.TextPosition(text.Length, line), select: false, replaced);
                // A replacement with line breaks adds lines: do not search the inserted text again.
                int added = CountLineBreaks(replaced);
                if (scoped && line == scopeEnd.Line)
                {
                    int lastBreak = replaced.LastIndexOfAny(new[] { '\r', '\n' });
                    endLine = scopeEnd.Line + added;
                    endColumn = (added == 0) ? newTo : Math.Max(0, newTo - (lastBreak + 1));
                }
                line += added;
                lastLine += added;
            }
        });
        if (scoped)
        {
            newScopeEnd = new TextDocument.TextPosition(endColumn, endLine);
        }
        if (count > 0)
        {
            int caretLine = Math.Min(caret.Line, Math.Max(0, Doc.LineCount - 1));
            SetSelection(false, CaretPosition);
            SetCaret(new TextDocument.TextPosition(Math.Min(caret.Column, Doc.GetLineText(caretLine).Length), caretLine));
            Redraw();
        }
        return count;
    }

    private static int CountLineBreaks(string text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>There is an edit that <see cref="Undo"/> would revert.</summary>
    public bool CanUndo => !_readOnly && _undoStack.Count > 0;

    /// <summary>There is an undone edit that <see cref="Redo"/> would repeat.</summary>
    public bool CanRedo => !_readOnly && _redoStack.Count > 0;

    public void Undo()
    {
        if (!_readOnly && _undoStack.Count > 0)
        {
            RunCommand(() =>
            {
                EditCommand editCommand = _undoStack.Pop();
                editCommand.Undo(this);
                _redoStack.Push(editCommand);
            });
            Redraw();
        }
    }

    public void Redo()
    {
        if (!_readOnly && _redoStack.Count > 0)
        {
            RunCommand(() =>
            {
                EditCommand editCommand = _redoStack.Pop();
                editCommand.Execute(this);
                _undoStack.Push(editCommand);
            });
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
        if (start == end && string.IsNullOrEmpty(replacement))
        {
            // Nothing to change (e.g. Backspace at the start of the document): do not mark the
            // file as modified and do not record an empty undo step.
            return;
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
        if (_readOnly)
        {
            return;
        }
        ExecuteCommand(new EditCommand.IndentCommand(this, levels));
    }

    protected virtual void OnLineChanged(int line, int column, int addedLength, int removedLength)
    {
        InvalidateWrapMap();
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
        OnTextChangedOutsideCommand();
    }

    protected virtual void OnRangeChanged(TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd)
    {
        InvalidateWrapMap();
        if (RangeChanged != null)
        {
            RangeChanged(this, start, oldEnd, newEnd);
        }
        OnTextChangedOutsideCommand();
    }

    private void OnTextChangedOutsideCommand()
    {
        // Edit commands decide IsModified when they finish; a change made any other way can never be
        // undone back to the saved text.
        if (_commandDepth == 0)
        {
            IsModified = true;
        }
    }

    protected virtual void OnModifiedChanged()
    {
        if (ModifiedChanged != null)
        {
            // IsModified is virtual: the remote file editor reports the state of its hex view from this event too.
            ModifiedChanged(this, IsModified);
        }
    }

    protected override void OnTextReplaced()
    {
        base.OnTextReplaced();
        _undoStack.Clear();
        _redoStack.Clear();
        // A new document: its saved state is the empty undo history, unless it was already modified.
        _savedUndoStep = _modified ? NeverSaved : null;
    }
}
