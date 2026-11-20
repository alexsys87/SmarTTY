using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using K = System.Windows.Forms.Keys;

// Drives the real text view (Controls/ReadonlyTextBrowser.cs, through a TextEditorControl shown in a form) and writes down where its caret goes,
// so that the moves of the caret and the wrap map can be taken out of the control (stage 3C) without changing what they do:
//   - for every position of a document of 30 lines (empty lines, tabs, words with punctuation, letters that are not ASCII, blanks, a line longer
//     than the wrap width) the target of each navigation key (Left, Right, Up, Down, Home, End, PageUp, PageDown, as the key handler finds it),
//     of the word moves and of Ctrl+Home and Ctrl+End (asked of ComputeMovedPosition with the keys, because a test cannot hold Ctrl), and the
//     word under the caret (TryGetWordBounds);
//   - runs of keys through the real key handler, where Up, Down and the pages keep the column the caret came from;
//   - the same with word wrap at 20 columns, and the wrap map (the breaks of every line, the line and the row of every row index, the row index
//     of positions).
//   - the selection (3C-2): the clicks, the margin, a drag, the buttons, a key without Shift, Select all, a range of lines, the setters and the
//     selected lines, also in terminal mode (SelectionRuns).
//   - the edits and their undo (3C-3): ReplaceText, Indent, Undo, Redo, the modified state, single steps, Replace All, read-only (EditRuns).
//   - the typing and the edit keys (3D-1): characters, Enter, Backspace, Tab, Delete, the word keys, the hook of typing (TypingRuns).
// The view is sized until it shows exactly 8 rows (and wraps at 20 columns), so that the lines do not depend on the fonts.
// TextViewGolden.txt is what the code wrote before the move; it is never written again. Since the move (3C-1) the accessors read the kept column
// and the wrap map of the model (Navigator, WrapLayout) instead of the fields of the view.
//   xvfb-run -a mono tests/TextViewTests/bin/Release/net48/TextViewTests.exe            compares with the golden file
//   ... TextViewTests.exe --record <file>                                                writes the golden file (old code only)
internal static class Program
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly Type View = typeof(ReadonlyTextBrowser);

    private static readonly List<string> Lines = new List<string>();

    private static TextEditorControl _editor;

    private static Form _form;

    private static readonly string[] DocumentLines = BuildLines();

    private static string[] BuildLines()
    {
        List<string> lines = new List<string>
        {
            "",
            "abc def_ghi  jkl",
            "\tindent\tx",
            "  two spaces",
            "foo.bar(baz, 42);",
            "the quick brown fox jumps over the lazy dog ok",
            "x",
            "été ünï",
            "    ",
            "end",
            "a\tb\tc\td"
        };
        for (int i = lines.Count; i < 30; i++)
        {
            lines.Add("line " + i.ToString(CultureInfo.InvariantCulture) + new string('.', (i * 7) % 23));
        }
        return lines.ToArray();
    }

    private static T Field<T>(string name)
    {
        return (T)View.GetField(name, Private).GetValue(_editor);
    }

    private static void SetField(string name, object value)
    {
        View.GetField(name, Private).SetValue(_editor, value);
    }

    private static object Call(string name, params object[] arguments)
    {
        return View.GetMethod(name, Private).Invoke(_editor, arguments);
    }

    private static T Property<T>(string name)
    {
        return (T)View.GetProperty(name, Private).GetValue(_editor, null);
    }

    private static string P(TextDocument.TextPosition position)
    {
        return position.Line.ToString(CultureInfo.InvariantCulture) + ":" + position.Column.ToString(CultureInfo.InvariantCulture);
    }

    private static TextDocument.TextPosition At(int line, int column)
    {
        return new TextDocument.TextPosition(column, line);
    }

    // The target of a navigation key from a position, as the key handler finds it (the column to keep is taken anew).
    private static string Target(TextDocument.TextPosition from, Keys key)
    {
        _editor.SetCaret(from);
        _editor.Navigator.DesiredColumn = -1;
        object[] arguments = { new KeyEventArgs(key), false, null, null };
        bool found = (bool)View.GetMethod("TryGetNavigationTarget", Private).Invoke(_editor, arguments);
        return found ? P((TextDocument.TextPosition)arguments[2]) : "-";
    }

    // CaretMove was a protected enum of the view (CodeEditing/CaretNavigator since 3C).
    private static string Moved(TextDocument.TextPosition from, string move, Keys keys)
    {
        _editor.SetCaret(from);
        _editor.Navigator.DesiredColumn = -1;
        object caretMove = Enum.Parse(typeof(SmarTTY.CodeEditing.CaretMove), move);
        try
        {
            return P((TextDocument.TextPosition)View.GetMethod("ComputeMovedPosition", Private).Invoke(_editor, new object[] { caretMove, keys, from, false }));
        }
        catch (TargetInvocationException exception)
        {
            // Recorded as it is: a fault of the old code is kept by the move.
            return "throws:" + exception.InnerException.GetType().Name;
        }
    }

    private static string Word(TextDocument.TextPosition at)
    {
        object[] arguments = { at, null, null };
        bool found = (bool)View.GetMethod("TryGetWordBounds", Private).Invoke(_editor, arguments);
        return found ? P((TextDocument.TextPosition)arguments[1]) + "-" + P((TextDocument.TextPosition)arguments[2]) : "none";
    }

    private static void Moves(string title)
    {
        Lines.Add("== moves " + title);
        for (int line = 0; line < DocumentLines.Length; line++)
        {
            for (int column = 0; column <= DocumentLines[line].Length; column++)
            {
                TextDocument.TextPosition from = At(line, column);
                Lines.Add(P(from)
                    + " L=" + Target(from, Keys.Left) + " R=" + Target(from, Keys.Right)
                    + " U=" + Target(from, Keys.Up) + " D=" + Target(from, Keys.Down)
                    + " H=" + Target(from, Keys.Home) + " E=" + Target(from, Keys.End)
                    + " PU=" + Target(from, Keys.Prior) + " PD=" + Target(from, Keys.Next)
                    + " WL=" + Moved(from, "WordLeft", Keys.None) + " WR=" + Moved(from, "WordRight", Keys.None)
                    + " CH=" + Moved(from, "Home", Keys.Control) + " CE=" + Moved(from, "End", Keys.Control)
                    + " W=" + Word(from));
            }
        }
        Lines.Add("other key: " + Target(At(3, 2), K.A) + ", outside the text: " + Moved(At(-1, 0), "Left", Keys.None));
    }

    private static void Press(string title, TextDocument.TextPosition start, params Keys[] keys)
    {
        _editor.SetCaret(start);
        _editor.Navigator.DesiredColumn = -1;
        List<string> steps = new List<string>();
        MethodInfo keyDown = View.GetMethod("OnKeyDown", Private);
        foreach (Keys key in keys)
        {
            keyDown.Invoke(_editor, new object[] { new KeyEventArgs(key) });
            steps.Add(key + ">" + P(_editor.CaretPosition) + "@" + Property<int>("ScrollLine").ToString(CultureInfo.InvariantCulture));
        }
        Lines.Add("keys " + title + " from " + P(start) + ": " + string.Join(" ", steps));
    }

    private static void Runs(string title)
    {
        Lines.Add("== runs " + title);
        Press("down through short lines", At(5, 30), K.Down, K.Down, K.Down, K.Down, K.Down, K.Up, K.Up, K.Up);
        Press("a column kept across tabs", At(1, 9), K.Down, K.Down, K.Up, K.Up);
        Press("right forgets the column", At(5, 30), K.Down, K.Right, K.Up);
        Press("pages", At(2, 3), K.Next, K.Next, K.Next, K.Next, K.Prior, K.Prior, K.Prior, K.Prior, K.Prior);
        Press("down to the end and past it", At(25, 2), K.Down, K.Down, K.Down, K.Down, K.Down, K.Down);
        Press("up to the start and past it", At(2, 4), K.Up, K.Up, K.Up, K.Up);
        Press("home and end", At(3, 6), K.Home, K.Home, K.Home, K.End, K.Left, K.Right, K.Right, K.Left);
        Press("left and right across lines", At(6, 0), K.Left, K.Left, K.Right, K.Right, K.Right);
        Press("a line off the screen", At(20, 3), K.Up, K.Down, K.Down);
    }

    private static void WrapMap()
    {
        Lines.Add("== wrap map");
        int[][] breaks = _editor.WrapLayout.Breaks;
        int[] firstRow = _editor.WrapLayout.FirstRows;
        for (int line = 0; line < DocumentLines.Length; line++)
        {
            Lines.Add("line " + line + " first row " + firstRow[line] + " breaks " + (breaks[line] == null ? "none" : string.Join(",", Array.ConvertAll(breaks[line], b => b.ToString(CultureInfo.InvariantCulture)))));
        }
        int rows = Property<int>("ScrollRowCount");
        Lines.Add("rows " + rows + ", last entry " + firstRow[DocumentLines.Length]);
        List<string> rowLines = new List<string>();
        for (int row = -1; row <= rows + 1; row++)
        {
            object[] arguments = { row, null };
            int line = (int)View.GetMethod("RowIndexToLine", Private).Invoke(_editor, arguments);
            rowLines.Add(row + ">" + line + "." + arguments[1]);
        }
        Lines.Add("row index to line: " + string.Join(" ", rowLines));
        List<string> rowIndexes = new List<string>();
        foreach (int line in new[] { -3, 0, 1, 2, 5, 10, 29, 40 })
        {
            foreach (int column in new[] { -1, 0, 7, 19, 20, 21, 39, 40, 45, 100 })
            {
                rowIndexes.Add(line + ":" + column + ">" + Call("PositionToRowIndex", line, column) + "/" + Call("WrapSegmentOf", line, column));
            }
        }
        Lines.Add("position to row index / segment: " + string.Join(" ", rowIndexes));
        List<string> segments = new List<string>();
        foreach (int line in new[] { 0, 2, 5, 10 })
        {
            for (int segment = 0; segment < 4; segment++)
            {
                int end = (breaks[line] == null || segment >= breaks[line].Length) ? int.MaxValue : breaks[line][segment];
                if (breaks[line] != null && segment > breaks[line].Length)
                {
                    break;
                }
                segments.Add(line + "." + segment + ">" + Call("WrapSegmentStart", line, segment) + "-" + (end == int.MaxValue ? "max" : ((int)Call("WrapSegmentEnd", line, segment)).ToString(CultureInfo.InvariantCulture)));
            }
        }
        Lines.Add("segments: " + string.Join(" ", segments));
    }

    private static void SizeTo(int rows, int wrapWidth)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            Application.DoEvents();
            int visibleRows = Field<int>("_visibleRows");
            int width = Field<int>("_wrapWidth");
            bool rowsDone = visibleRows == rows;
            bool widthDone = wrapWidth == 0 || width == wrapWidth;
            if (rowsDone && widthDone)
            {
                return;
            }
            System.Drawing.Size size = _form.ClientSize;
            int height = size.Height + (rowsDone ? 0 : (visibleRows < rows ? 1 : -1));
            int formWidth = size.Width + (widthDone ? 0 : (width < wrapWidth ? 1 : -1));
            _form.ClientSize = new System.Drawing.Size(formWidth, height);
        }
        throw new InvalidOperationException("the view could not be sized to " + rows + " rows and " + wrapWidth + " columns: " + Field<int>("_visibleRows") + ", " + Field<int>("_wrapWidth"));
    }

    private static void Run()
    {
        _form = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(0, 0), ClientSize = new System.Drawing.Size(900, 150) };
        _editor = new TextEditorControl { Dock = DockStyle.Fill };
        _form.Controls.Add(_editor);
        _editor.SelectionChanged += (view, previous, current, has) =>
        {
            _selectionEvents++;
            _lastSelectionEvent = P(previous) + ">" + P(current) + (has ? "+sel" : "");
        };
        _editor.SetDocument(new EditableTextDocument(DocumentLines, TextDocument.LineEnding.Lf));
        _editor.TabSize = 4;
        _form.Show();
        SizeTo(8, 0);
        Lines.Add("rows " + Field<int>("_visibleRows") + ", wrapping " + Bool(_editor.IsWrapping));
        Moves("without wrap");
        Runs("without wrap");
        SelectionRuns("without wrap");

        _editor.WordWrap = true;
        _form.ClientSize = new System.Drawing.Size(300, _form.ClientSize.Height);
        SizeTo(8, 20);
        Lines.Add("rows " + Field<int>("_visibleRows") + ", wrapping " + Bool(_editor.IsWrapping) + " at " + Field<int>("_wrapWidth"));
        WrapMap();
        Moves("with wrap");
        Runs("with wrap");
        Press("down through the rows of a wrapped line", At(4, 15), K.Down, K.Down, K.Down, K.Down, K.Down, K.Up, K.Up, K.Up, K.Up);
        Press("the end of a row", At(5, 19), K.Down, K.Up, K.End, K.Home, K.Down);

        SelectionRuns("with wrap");

        // The tab size changes the breaks; a text that changes too.
        _editor.TabSize = 8;
        WrapMap();
        _editor.SetDocument(new EditableTextDocument(new[] { "short", new string('w', 61), "\t\t\t\t\tafter tabs that pass the width" }, TextDocument.LineEnding.Lf));
        Application.DoEvents();
        WrapMap2(3);
        EditRuns();
        TypingRuns();
        ReplaceRuns();
        _form.Dispose();
    }

    // The wrap map of a document of a few lines.
    private static void WrapMap2(int lineCount)
    {
        int[][] breaks = _editor.WrapLayout.Breaks;
        Call("EnsureWrapMap");
        breaks = _editor.WrapLayout.Breaks;
        int[] firstRow = _editor.WrapLayout.FirstRows;
        List<string> parts = new List<string>();
        for (int line = 0; line < lineCount; line++)
        {
            parts.Add(line + ":" + firstRow[line] + "[" + (breaks[line] == null ? "" : string.Join(",", Array.ConvertAll(breaks[line], b => b.ToString(CultureInfo.InvariantCulture)))) + "]");
        }
        Lines.Add("== another text: " + string.Join(" ", parts) + " rows " + firstRow[lineCount]);
    }

    // ---- 3C-2: the selection, written by the code before the selection moved to the model.
    // The mouse (single, double and triple clicks, the margin, a drag with the left button, a drag after a double click, the right button), a
    // key without Shift, Select all, a range of lines, the programmatic setters and the selected lines; and the same in terminal mode, where the
    // selection has an end of its own. Shift cannot be held by a test (the view reads Control.ModifierKeys), so Shift with the keys and the mouse
    // is not here. The clicks are given at the left edge of a character cell, found from the view (ColumnToX and the row of the line).

    private static int _selectionEvents;

    private static string _lastSelectionEvent = "-";

    private static System.Drawing.Point CellPoint(int line, int column)
    {
        float y = (float)View.GetMethod("LineToY", Private).Invoke(_editor, new object[] { line });
        return new System.Drawing.Point((int)Math.Ceiling(_editor.ColumnToX(column)) + 1, (int)y + 2);
    }

    private static void ForgetClicks()
    {
        SetField("_lastClickTime", DateTime.MinValue);
        SetField("_lastDoubleClickTime", DateTime.MinValue);
        SetField("_lastClickPoint", new System.Drawing.Point(-100, -100));
    }

    private static void Mouse(string method, MouseButtons button, System.Drawing.Point point)
    {
        View.GetMethod(method, Private).Invoke(_editor, new object[] { new MouseEventArgs(button, 1, point.X, point.Y, 0) });
    }

    private static string SelectionState()
    {
        ReadonlyTextBrowser.LineRange lines = _editor.SelectedLines;
        string text = "has=" + Bool(_editor.HasSelection) + " start=" + P(_editor.SelectionStart) + " end=" + P(_editor.SelectionEnd) + " caret=" + P(_editor.CaretPosition)
            + " lines=" + lines.First + ".." + lines.Last + " events=" + _selectionEvents + " last=" + _lastSelectionEvent;
        _selectionEvents = 0;
        _lastSelectionEvent = "-";
        return text;
    }

    private static void Sel(string what)
    {
        Lines.Add("sel " + what + ": " + SelectionState());
    }

    private static void SelectionRuns(string title)
    {
        Lines.Add("== selection " + title);
        _editor.SetCaret(At(0, 0));
        _editor.ClearSelection();
        ForgetClicks();
        SelectionState();
        System.Drawing.Point word = CellPoint(1, 5);
        Mouse("OnMouseDown", MouseButtons.Left, word);
        Mouse("OnMouseUp", MouseButtons.Left, word);
        Sel("a click in a word");
        Mouse("OnMouseDown", MouseButtons.Left, word);
        Mouse("OnMouseUp", MouseButtons.Left, word);
        Sel("a second click at once: the word");
        Mouse("OnMouseDown", MouseButtons.Left, word);
        Mouse("OnMouseUp", MouseButtons.Left, word);
        Sel("a third click at once");
        ForgetClicks();
        Mouse("OnMouseDown", MouseButtons.Left, CellPoint(1, 12));
        Mouse("OnMouseDown", MouseButtons.Left, CellPoint(1, 12));
        Sel("a double click on a blank between words");
        ForgetClicks();
        Mouse("OnMouseDown", MouseButtons.Left, CellPoint(0, 0));
        Mouse("OnMouseDown", MouseButtons.Left, CellPoint(0, 0));
        Sel("a double click on an empty line");
        ForgetClicks();
        Mouse("OnMouseUp", MouseButtons.Left, CellPoint(0, 0));

        System.Drawing.Point from = CellPoint(3, 2);
        Mouse("OnMouseDown", MouseButtons.Left, from);
        Sel("a press for a drag");
        Mouse("OnMouseMove", MouseButtons.Left, CellPoint(5, 10));
        Sel("dragged down");
        Mouse("OnMouseMove", MouseButtons.Left, CellPoint(2, 1));
        Sel("dragged above the start");
        Mouse("OnMouseMove", MouseButtons.Left, from);
        Sel("dragged back to the start");
        Mouse("OnMouseMove", MouseButtons.None, CellPoint(6, 0));
        Sel("moved without a button");
        Mouse("OnMouseUp", MouseButtons.Left, from);
        ForgetClicks();

        Mouse("OnMouseDown", MouseButtons.Left, word);
        Mouse("OnMouseDown", MouseButtons.Left, word);
        Mouse("OnMouseMove", MouseButtons.Left, CellPoint(4, 3));
        Sel("moved after a double click (no drag)");
        Mouse("OnMouseUp", MouseButtons.Left, CellPoint(4, 3));
        Mouse("OnMouseMove", MouseButtons.Left, CellPoint(4, 3));
        Sel("moved with the button after the release");
        Mouse("OnMouseUp", MouseButtons.Left, CellPoint(4, 3));
        ForgetClicks();

        int marginX = ((System.Drawing.Rectangle)View.GetProperty("TextArea", Private).GetValue(_editor, null)).Left - 1;
        Mouse("OnMouseDown", MouseButtons.Left, new System.Drawing.Point(marginX, CellPoint(2, 0).Y));
        Sel("a click left of the text");
        Mouse("OnMouseUp", MouseButtons.Left, new System.Drawing.Point(marginX, CellPoint(2, 0).Y));
        ForgetClicks();
        Mouse("OnMouseDown", MouseButtons.Right, CellPoint(4, 1));
        Sel("the right button with a selection");
        _editor.ClearSelection();
        Sel("cleared");
        _editor.ClearSelection();
        Sel("cleared again");
        Mouse("OnMouseDown", MouseButtons.Right, CellPoint(4, 1));
        Sel("the right button without a selection");
        Mouse("OnMouseDown", MouseButtons.Middle, CellPoint(6, 0));
        Sel("the middle button");
        ForgetClicks();

        _editor.SetSelection(true, At(1, 0));
        _editor.SetCaret(At(1, 5));
        Sel("set programmatically");
        _editor.SetSelection(true, At(1, 0));
        Sel("set again the same");
        _editor.SetSelection(false, At(1, 0));
        Sel("set off");
        _editor.SetSelection(true, At(2, 3));
        View.GetMethod("OnKeyDown", Private).Invoke(_editor, new object[] { new KeyEventArgs(K.Right) });
        Sel("a key without Shift drops it");
        _editor.SelectAllText();
        Sel("select all");
        View.GetMethod("SelectLineRange", Private).Invoke(_editor, new object[] { 4, 2 });
        Sel("lines 4 to 2");
        View.GetMethod("SelectLineRange", Private).Invoke(_editor, new object[] { -3, 100 });
        Sel("lines -3 to 100");
        View.GetMethod("SelectLineRange", Private).Invoke(_editor, new object[] { 5, 5 });
        Sel("line 5");
        foreach (int[] pair in new[] { new[] { 1, 0, 3, 0 }, new[] { 3, 0, 1, 0 }, new[] { 1, 2, 3, 4 }, new[] { 3, 4, 1, 2 }, new[] { 2, 2, 2, 2 }, new[] { 0, 0, 1, 0 } })
        {
            _editor.SetCaret(At(pair[2], pair[3]));
            _editor.SetSelection(true, At(pair[0], pair[1]));
            Sel("from " + pair[0] + ":" + pair[1] + " to the caret");
        }
        _editor.SetSelectionEnd(At(6, 2));
        Sel("an end set outside terminal mode");
        _editor.ClearSelection();
        SelectionState();

        _editor.TerminalMode = true;
        Sel("terminal mode");
        ForgetClicks();
        Mouse("OnMouseDown", MouseButtons.Left, CellPoint(2, 3));
        Sel("terminal: a press");
        Mouse("OnMouseMove", MouseButtons.Left, CellPoint(4, 1));
        Sel("terminal: dragged");
        Mouse("OnMouseMove", MouseButtons.Left, CellPoint(2, 3));
        Sel("terminal: dragged back");
        Mouse("OnMouseUp", MouseButtons.Left, CellPoint(2, 3));
        ForgetClicks();
        Mouse("OnMouseDown", MouseButtons.Left, CellPoint(3, 4));
        Mouse("OnMouseDown", MouseButtons.Left, CellPoint(3, 4));
        Sel("terminal: a double click selects the line");
        Mouse("OnMouseUp", MouseButtons.Left, CellPoint(3, 4));
        ForgetClicks();
        _editor.SelectAllText();
        Sel("terminal: select all does nothing");
        _editor.SetSelectionEnd(At(5, 2));
        _editor.SetSelection(true, At(1, 1));
        Sel("terminal: an end set and a start");
        Mouse("OnMouseDown", MouseButtons.Right, CellPoint(6, 0));
        Sel("terminal: the right button with a selection");
        _editor.ClearSelection();
        Mouse("OnMouseDown", MouseButtons.Right, CellPoint(6, 1));
        Sel("terminal: the right button without one");
        _editor.TerminalMode = false;
        _editor.ClearSelection();
        SelectionState();
    }

    // ---- 3C-3: the edits and their undo, written by the code before the undo history moved to the model.
    // ReplaceText in a line, across lines, with the start only (select), an empty edit, the join of two lines that drops the indentation of
    // the second, Indent and unindent with and without a selection (tabs, spaces, blank lines), Undo and Redo at the ends of the history, an
    // edit after an undo (the redo is gone), the modified state with IsModified, CurrentVersion and MarkSaved, a change made outside an edit
    // command, ExecuteAsSingleUndoStep (also nested), Replace All (literal, scoped, with line breaks), read-only, and a new document. Every
    // step writes the text, the caret, the selection, the modified state, what can be undone and redone, and the events.

    private static readonly List<string> EditEvents = new List<string>();

    private static string Escape(string text)
    {
        return text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private static void Edit(string what)
    {
        Lines.Add("edit " + what + ": text=" + Escape(_editor.EditableDocument.GetText()) + " caret=" + P(_editor.CaretPosition)
            + " sel=" + (_editor.HasSelection ? P(_editor.SelectionStart) : "-") + " mod=" + Bool(_editor.IsModified)
            + " undo=" + Bool(_editor.CanUndo) + " redo=" + Bool(_editor.CanRedo) + " events=" + string.Join(";", EditEvents));
        EditEvents.Clear();
    }

    private static void EditRuns()
    {
        Lines.Add("== edits");
        _editor.WordWrap = false;
        _editor.TabSize = 4;
        _editor.SetDocument(new EditableTextDocument(new[] { "alpha beta", "    gamma", "delta", "", "\tepsilon" }, TextDocument.LineEnding.Lf));
        _editor.SetCaret(At(0, 0));
        _editor.ClearSelection();
        _editor.IsModified = false;
        _editor.ModifiedChanged += (view, modified) => EditEvents.Add("mod:" + Bool(modified));
        _editor.LineChanged += (view, line, column, added, removed) => EditEvents.Add("line:" + line + "," + column + "," + added + "," + removed);
        _editor.RangeChanged += (view, start, oldEnd, newEnd) => EditEvents.Add("range:" + P(start) + "," + P(oldEnd) + "," + P(newEnd));
        EditEvents.Clear();
        Edit("a new document");
        _editor.ReplaceText(At(0, 6), At(0, 10), false, "BETA!");
        Edit("a word replaced in a line");
        _editor.ReplaceText(At(0, 0), At(0, 0), true, "");
        Edit("an empty edit");
        _editor.ReplaceText(At(0, 3), At(2, 2), false, "-");
        Edit("a range across lines replaced");
        _editor.Undo();
        Edit("undone");
        _editor.Undo();
        Edit("undone again: the start");
        _editor.Undo();
        Edit("nothing more to undo");
        _editor.Redo();
        Edit("redone");
        _editor.Redo();
        Edit("redone again");
        _editor.Redo();
        Edit("nothing more to redo");
        _editor.Undo();
        _editor.ReplaceText(At(1, 0), At(1, 0), true, "X");
        Edit("an edit after an undo drops the redo");
        _editor.ReplaceText(At(0, 2), At(0, 5), false, "1\n2");
        Edit("a replacement with a line break");
        _editor.ReplaceText(At(1, 5), At(1, 1), false, "<>");
        Edit("an end before the start");
        _editor.ReplaceText(At(2, 1), At(2, -1), false, "!");
        Edit("an end column of -1 is the end of the line");
        _editor.ReplaceText(At(3, -1), At(3, -1), true, "?");
        Edit("a start column of -1 too");
        _editor.SetCaret(At(3, 1));
        _editor.ReplaceText(At(2, _editor.EditableDocument.GetLineText(2).Length), At(3, 0), false, "");
        Edit("the line break at the end of a line deleted (the next line joined)");
        _editor.SetDocument(new EditableTextDocument(new[] { "    first", "    second", "", "third" }, TextDocument.LineEnding.Lf));
        EditEvents.Clear();
        Edit("another document");
        _editor.ReplaceText(At(0, 9), At(1, 0), false, "");
        Edit("a join that drops the indentation the lines share");
        _editor.Undo();
        Edit("and its undo");

        _editor.InsertSpaces = false;
        _editor.SetSelection(true, At(0, 2));
        _editor.SetCaret(At(3, 0));
        _editor.Indent(1);
        Edit("indent of lines 0 to 2 (a selection that ends at the start of a line) with a tab");
        _editor.Indent(-1);
        Edit("unindent of them");
        _editor.Undo();
        Edit("the unindent undone");
        _editor.Undo();
        Edit("the indent undone");
        _editor.ClearSelection();
        _editor.SetCaret(At(1, 3));
        _editor.InsertSpaces = true;
        _editor.Indent(2);
        Edit("indent of the caret line by two levels of spaces");
        _editor.Indent(-1);
        Edit("unindent of the caret line by one");
        _editor.Indent(-5);
        Edit("unindent by more than there is");
        _editor.Undo();
        _editor.Undo();
        _editor.Undo();
        Edit("all three undone");
        _editor.Redo();
        Edit("the indent redone");

        _editor.SetDocument(new EditableTextDocument(new[] { "one", "two" }, TextDocument.LineEnding.Lf));
        EditEvents.Clear();
        Edit("a third document");
        object saved = _editor.CurrentVersion;
        _editor.ReplaceText(At(0, 3), At(0, 3), false, "!");
        Edit("an edit");
        _editor.MarkSaved(_editor.CurrentVersion);
        Edit("marked saved");
        _editor.ReplaceText(At(1, 0), At(1, 0), false, "+");
        Edit("another edit");
        _editor.Undo();
        Edit("undone back to the saved text");
        _editor.Undo();
        Edit("undone past it");
        _editor.Redo();
        Edit("redone to the saved text");
        _editor.MarkSaved(saved);
        Edit("marked saved at the version of the start");
        _editor.Undo();
        Edit("undone to that version");
        _editor.IsModified = true;
        Edit("set modified");
        _editor.Redo();
        _editor.Undo();
        Edit("redone and undone: still modified (never saved)");
        _editor.IsModified = false;
        Edit("set saved");
        typeof(EditableTextBrowser).GetMethod("OnLineChanged", Private).Invoke(_editor, new object[] { 0, 0, 0, 0 });
        Edit("a change made outside an edit command");
        _editor.Undo();
        Edit("undone after it: still modified");

        _editor.SetDocument(new EditableTextDocument(new[] { "aaa", "bab", "cac" }, TextDocument.LineEnding.Lf));
        _editor.IsModified = false;
        EditEvents.Clear();
        _editor.ExecuteAsSingleUndoStep(() =>
        {
            _editor.ReplaceText(At(0, 0), At(0, 1), false, "A");
            _editor.ExecuteAsSingleUndoStep(() => _editor.ReplaceText(At(1, 0), At(1, 1), false, "B"));
            _editor.ReplaceText(At(2, 0), At(2, 1), false, "C");
        });
        Edit("three edits as one step (one of them nested)");
        _editor.Undo();
        Edit("undone at once");
        _editor.Redo();
        Edit("redone at once");
        _editor.ExecuteAsSingleUndoStep(() => { });
        Edit("an empty step");
        _editor.Undo();
        Edit("the empty step left no undo of its own");
        _editor.Redo();
        _editor.SetCaret(At(2, 3));
        Edit("before Replace All");
        Lines.Add("replace all a: " + _editor.ReplaceAll("a", "<a>", false));
        Edit("Replace All");
        _editor.Undo();
        Edit("Replace All undone");
        Lines.Add("replace all x: " + _editor.ReplaceAll("x", "y", false));
        Edit("Replace All of nothing");
        Lines.Add("replace all empty: " + _editor.ReplaceAll("", "y", false));
        TextDocument.TextPosition newEnd;
        Lines.Add("scoped: " + _editor.ReplaceAll(TextMatcher.Literal("A", true), "1\n2", At(0, 1), At(1, 2), out newEnd) + " end " + P(newEnd));
        Edit("Replace All in a range, with a line break in the replacement");
        _editor.Undo();

        _editor.ReadOnly = true;
        _editor.ReplaceText(At(0, 0), At(0, 0), false, "no");
        _editor.Indent(1);
        _editor.ExecuteAsSingleUndoStep(() => _editor.ReplaceText(At(0, 0), At(0, 0), false, "no"));
        Lines.Add("replace all read-only: " + _editor.ReplaceAll("a", "b", false));
        _editor.Undo();
        Edit("read-only: no edit, no undo, no redo");
        _editor.ReadOnly = false;
        Edit("editable again");
        _editor.IsModified = true;
        _editor.SetDocument(new EditableTextDocument(new[] { "new" }, TextDocument.LineEnding.Lf));
        Edit("a new document while modified");
        _editor.IsModified = false;
        _editor.SetDocument(new EditableTextDocument(new[] { "newer" }, TextDocument.LineEnding.Lf));
        Edit("a new document while saved");
    }

    // ---- 3D-1: the typing and the edit keys of TextEditorControl, written by the code before they moved to the model.
    // Characters (with and without a selection, overwrite, a control character), Enter (the indentation carried over, a caret inside the
    // indentation, a selection, a line without a line break of its own), Backspace (a character, a selection, a level of space indentation,
    // the start of a line, the start of the text), Tab (a tab, spaces to the next tab stop, a multi-line selection indents), Delete,
    // Ctrl+Delete and Ctrl+Backspace (words, blanks, the ends of a line and of the text), Undo and Redo by the keys, the CharTyping hook that
    // changes the text and the caret, InsertText, GetWordAtCaret, ReplaceWordAtCaret, RemoveLines and DeleteSelection. Shift cannot be held by a
    // test (Shift+Tab reads Control.ModifierKeys), and the clipboard is not used. Every step writes what the edits run writes.

    private static readonly MethodInfo KeyPressMethod = typeof(TextEditorControl).GetMethod("OnKeyPress", Private);

    private static readonly MethodInfo EditorKeyDownMethod = typeof(TextEditorControl).GetMethod("OnKeyDown", Private);

    private static void Type(string text)
    {
        foreach (char ch in text)
        {
            KeyPressMethod.Invoke(_editor, new object[] { new KeyPressEventArgs(ch) });
        }
    }

    private static void Key(Keys keyData)
    {
        EditorKeyDownMethod.Invoke(_editor, new object[] { new KeyEventArgs(keyData) });
    }

    private static void Start(params string[] lines)
    {
        _editor.SetDocument(new EditableTextDocument(lines, TextDocument.LineEnding.Lf));
        _editor.ClearSelection();
        _editor.SetCaret(At(0, 0));
        _editor.IsModified = false;
        EditEvents.Clear();
    }

    private static void TypingRuns()
    {
        Lines.Add("== typing");
        _editor.InsertSpaces = false;
        _editor.OverwriteMode = false;
        Start("abc", "    indented", "\tx", "");
        _editor.SetCaret(At(0, 3));
        Type("de");
        Edit("two characters typed at the end");
        _editor.SetSelection(true, At(0, 1));
        _editor.SetCaret(At(0, 4));
        Type("Z");
        Edit("a character typed over a selection");
        Type("\u0001");
        Edit("a control character does nothing");
        _editor.OverwriteMode = true;
        _editor.SetCaret(At(0, 0));
        Type("xy");
        Edit("overwrite");
        _editor.SetCaret(At(0, 4));
        Type("!");
        Edit("overwrite at the end of a line inserts");
        _editor.OverwriteMode = false;
        _editor.SetCaret(At(1, 12));
        Type("\r");
        Edit("Enter after an indented line carries the indentation");
        _editor.SetCaret(At(1, 2));
        Type("\r");
        Edit("Enter inside the indentation carries the part before the caret");
        _editor.SetSelection(true, At(0, 1));
        _editor.SetCaret(At(0, 3));
        Type("\r");
        Edit("Enter over a selection");
        Start("one");
        _editor.SetCaret(At(0, 3));
        Type("\r");
        Edit("Enter on a line without a line break of its own");
        Start("abc", "      sp", "x");
        _editor.SetCaret(At(0, 2));
        Type("\b");
        Edit("Backspace removes a character");
        _editor.SetCaret(At(1, 0));
        Type("\b");
        Edit("Backspace at the start of a line joins it");
        Type("\b\b\b");
        _editor.SetCaret(At(0, 0));
        Type("\b");
        Edit("three more Backspaces, then one at the start of the text, which does nothing");
        Start("x", "      sp");
        _editor.InsertSpaces = true;
        _editor.SetCaret(At(1, 6));
        Type("\b");
        Edit("Backspace in space indentation goes back to the tab stop");
        Type("\b");
        Edit("and again");
        _editor.SetCaret(At(1, 3));
        Type("\t");
        Edit("Tab with spaces goes to the next tab stop");
        _editor.InsertSpaces = false;
        Type("\t");
        Edit("Tab without spaces types a tab");
        _editor.SetSelection(true, At(0, 0));
        _editor.SetCaret(At(1, 1));
        Type("\t");
        Edit("Tab over a selection of two lines indents them");
        _editor.SetSelection(true, At(1, 0));
        _editor.SetCaret(At(1, 2));
        Type("\t");
        Edit("Tab over a selection in one line replaces it");
        _editor.SetSelection(true, At(0, 0));
        _editor.SetCaret(At(1, 1));
        Type("\b");
        Edit("Backspace over a selection removes it");

        Start("foo  bar.baz", "next");
        _editor.SetCaret(At(0, 0));
        Key(K.Delete);
        Edit("Delete removes a character");
        Key(K.Delete | K.Control);
        Edit("Ctrl+Delete removes a word and the blanks before the next");
        Key(K.Delete | K.Control);
        Edit("Ctrl+Delete removes the next word");
        _editor.SetCaret(At(0, _editor.EditableDocument.GetLineText(0).Length));
        Key(K.Delete | K.Control);
        Edit("Ctrl+Delete at the end of a line removes the line break");
        _editor.SetCaret(At(0, _editor.EditableDocument.GetLineText(0).Length));
        Key(K.Delete | K.Control);
        Edit("Ctrl+Delete at the end of the text does nothing");
        Key(K.Back | K.Control);
        Edit("Ctrl+Backspace removes the word in front of the caret");
        Key(K.Back | K.Control);
        Edit("and the one before it");
        _editor.SetCaret(At(0, 0));
        Key(K.Back | K.Control);
        Edit("Ctrl+Backspace at the start of the text does nothing");
        Start("a b", "c");
        _editor.SetCaret(At(1, 0));
        Key(K.Back | K.Control);
        Edit("Ctrl+Backspace at the start of a line removes the line break");
        _editor.SetSelection(true, At(0, 0));
        _editor.SetCaret(At(0, 2));
        Key(K.Delete | K.Control);
        Edit("Ctrl+Delete over a selection removes it");
        _editor.SetSelection(true, At(0, 0));
        _editor.SetCaret(At(0, 1));
        Key(K.Delete);
        Edit("Delete over a selection removes it");
        Key(K.Z | K.Control);
        Edit("Ctrl+Z undoes");
        Key(K.Y | K.Control);
        Edit("Ctrl+Y redoes");
        Key(K.Z | K.Control);
        Key(K.Z | K.Control | K.Shift);
        Edit("Ctrl+Shift+Z redoes too");
        Key(K.Z | K.Control | K.Alt);
        Edit("Ctrl+Alt+Z (AltGr) does not undo");
        _editor.SetCaret(At(0, 1));
        Key(K.Back | K.Control | K.Alt);
        Edit("Ctrl+Alt+Backspace (AltGr) does not remove a word");

        Start("word here", "x");
        TextEditorControl.CharTypingHandler hook = (TextEditorControl view, char ch, ref string text, ref TextDocument.TextPosition position) =>
        {
            if (ch == '(')
            {
                text = "()";
                position = new TextDocument.TextPosition(view.CaretPosition.Column + 1, view.CaretPosition.Line);
            }
            else if (ch == '#')
            {
                text = null;
            }
        };
        string typed = "";
        TextEditorControl.CharTypedHandler after = (view, ch, position) => typed += ch + "@" + P(position) + " ";
        _editor.CharTyping += hook;
        _editor.CharTyped += after;
        _editor.SetCaret(At(0, 4));
        Type("(");
        Edit("a hook that types a pair and puts the caret inside it");
        Type("#");
        Edit("a hook that types nothing");
        _editor.CharTyping -= hook;
        _editor.CharTyped -= after;
        Lines.Add("typed: " + typed);

        Start("alpha beta_2 gamma", "x");
        foreach (int column in new[] { 0, 3, 5, 6, 12, 18 })
        {
            _editor.SetCaret(At(0, column));
            string word = _editor.GetWordAtCaret(out int start, out int length);
            Lines.Add("word at " + column + ": " + (word ?? "null") + " " + start + "+" + length);
        }
        _editor.SetCaret(At(0, 8));
        _editor.ReplaceWordAtCaret("BETA");
        Edit("the word at the caret replaced");
        _editor.SetCaret(At(0, 5));
        _editor.ReplaceWordAtCaret("+");
        Edit("a replacement after a word");
        _editor.SetCaret(At(1, 0));
        _editor.ReplaceWordAtCaret("y");
        Edit("no word at the caret: the text is inserted");
        _editor.InsertText("ins");
        Edit("InsertText");
        Start("l0", "l1", "l2", "l3");
        typeof(TextEditorControl).GetMethod("RemoveLines", Private).Invoke(_editor, new object[] { 1, 2 });
        Edit("lines 1 and 2 removed");
        typeof(TextEditorControl).GetMethod("RemoveLines", Private).Invoke(_editor, new object[] { 1, 1 });
        Edit("the last line removed (the line break before it goes)");
        typeof(TextEditorControl).GetMethod("RemoveLines", Private).Invoke(_editor, new object[] { 0, 0 });
        Edit("the only line removed");
        Start("abc def");
        _editor.DeleteSelection();
        Edit("DeleteSelection without a selection");
        _editor.SetSelection(true, At(0, 4));
        _editor.SetCaret(At(0, 7));
        _editor.DeleteSelection();
        Edit("DeleteSelection");
    }

    private static string Bool(bool value)
    {
        return value ? "T" : "F";
    }

    private static int Main(string[] args)
    {
        try
        {
            Run();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            Console.WriteLine("after: " + (Lines.Count > 0 ? Lines[Lines.Count - 1] : "nothing"));
            return 1;
        }
        string text = string.Join("\n", Lines) + "\n";
        if (args.Length == 2 && args[0] == "--record")
        {
            File.WriteAllText(args[1], text, new UTF8Encoding(false));
            Console.WriteLine("Recorded " + Lines.Count + " lines in " + args[1]);
            return 0;
        }
        string goldenPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TextViewGolden.txt");
        if (!File.Exists(goldenPath))
        {
            Console.WriteLine("FAIL: " + goldenPath + " is missing");
            return 1;
        }
        string[] expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        string[] actual = text.TrimEnd('\n').Split('\n');
        int mismatches = 0;
        for (int i = 0; i < Math.Max(expected.Length, actual.Length); i++)
        {
            string expectedLine = i < expected.Length ? expected[i] : "(none)";
            string actualLine = i < actual.Length ? actual[i] : "(none)";
            if (expectedLine != actualLine)
            {
                if (mismatches < 10)
                {
                    Console.WriteLine("FAIL: line " + (i + 1));
                    Console.WriteLine("  expected: " + expectedLine);
                    Console.WriteLine("  actual:   " + actualLine);
                }
                mismatches++;
            }
        }
        if (mismatches > 0)
        {
            Console.WriteLine("FAIL: " + mismatches + " of " + actual.Length + " lines differ from the golden file");
            return 1;
        }
        Console.WriteLine("PASS: all " + actual.Length + " lines of the text view match the golden file");
        Console.WriteLine();
        Console.WriteLine("All text view checks passed.");
        return 0;
    }

    // ---- 3D-2: what ReplaceText and Replace All decide that the edits run did not reach, written by the code before it moved to the model:
    // a single-line view (line breaks dropped), a lone CR in an edit within a line, the line endings of an insertion with and without the
    // existing ones, Replace All limited to the end of a range, a replacement with line breaks whose new lines would match again (LF and a lone
    // CR), the end of the range after it, and the caret brought into a line that became shorter. A fault it found, kept as it is: when a line of the
    // range before the last one gets line breaks, the last line of the range is looked for at its old index, so its limit and the new end of the
    // range are worked out for another line.

    private static void ReplaceRuns()
    {
        Lines.Add("== replace");
        Start("one two", "three");
        _editor.SingleLine = true;
        _editor.ReplaceText(At(0, 3), At(0, 3), false, "+a\nb\r\nc+");
        Edit("single line: the line breaks of the text are dropped");
        _editor.SingleLine = false;
        Start("one two", "three");
        _editor.ReplaceText(At(0, 3), At(0, 3), false, "x\ry");
        Edit("a lone CR in an edit within a line breaks the line");
        Start("one two", "three");
        _editor.ReplaceText(At(0, 3), At(0, 4), false, "A\r\nB");
        Edit("an insertion with CRLF into an LF text takes the existing line ending");
        FieldInfo existing = typeof(EditableTextBrowser).GetField("_useExistingLineEnding", Private);
        existing.SetValue(_editor, false);
        Start("one two", "three");
        _editor.ReplaceText(At(0, 3), At(0, 4), false, "A\r\nB");
        Edit("and without the existing line ending its own");
        existing.SetValue(_editor, true);
        TextDocument.TextPosition newEnd;
        Start("aXa aXa", "aXa");
        Lines.Add("scoped to a column: " + _editor.ReplaceAll(TextMatcher.Literal("a", false), "b", At(0, 1), At(1, 2), out newEnd) + " end " + P(newEnd));
        Edit("Replace All up to the end of the range only");
        Start("aaaa", "z");
        Lines.Add("lines that would match again: " + _editor.ReplaceAll("aa", "a\na", false));
        Edit("the inserted lines are not searched again");
        Start("aaaa", "z");
        Lines.Add("with a lone CR: " + _editor.ReplaceAll("aa", "a\ra", false));
        Edit("nor after a lone CR");
        Start("xaay", "aa end");
        Lines.Add("scoped with breaks: " + _editor.ReplaceAll(TextMatcher.Literal("aa", false), "1\n22", At(0, 0), At(1, 4), out newEnd) + " end " + P(newEnd));
        Edit("line breaks in a line before the last: the last line is taken for another one (recorded as it is)");
        Start("xy", "aa bb");
        Lines.Add("breaks in the last line: " + _editor.ReplaceAll(TextMatcher.Literal("aa", false), "1\n22", At(0, 0), At(1, 5), out newEnd) + " end " + P(newEnd));
        Edit("the end of the range after line breaks in the last line");
        Start("xaay", "aa end");
        Lines.Add("scoped in one line: " + _editor.ReplaceAll(TextMatcher.Literal("aa", false), "123", At(0, 0), At(1, 4), out newEnd) + " end " + P(newEnd));
        Edit("the end of the range in the same line");
        Start("aaaaaa", "z");
        _editor.SetCaret(At(0, 6));
        Lines.Add("shorter: " + _editor.ReplaceAll("aa", "", false));
        Edit("the caret brought into the shorter line");
    }
}
