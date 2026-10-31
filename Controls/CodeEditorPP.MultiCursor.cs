using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SmarTTY.Controls;

// Several cursors: Alt+click adds one, Ctrl+D selects the next occurrence of the word, Ctrl+Alt+Up/Down add a cursor on the line above or
// below. Typing, Backspace, Delete, Enter, Tab, the arrow keys, copy, cut and paste then work at every cursor, and one Undo takes back
// what they did. The model is in CodeEditing/MultiCursor.cs (free of the UI and checked by tests/MultiCursorTests); this part keeps the
// cursors besides the main one, draws them and feeds the keys and the mouse to the model.
//
// The main cursor is the caret and the selection of the control itself; the others are in _extraCursors. Anything that is not done by the
// code here (a click, a find, an undo, a command from a menu) puts the control back to a single cursor.
public partial class CodeEditorPP
{
    // The cursors besides the main one, in no particular order; the model sorts them together with the main one.
    private readonly List<TextCursor> _extraCursors = new List<TextCursor>();

    // True while this code moves the caret or edits the text, so that the hooks that clear the extra cursors leave them alone.
    private bool _applyingMultiCursor;

    private bool _wholeWordOccurrences;

    // The column the main cursor returns to when it moves up or down over shorter lines.
    private int _primaryGoal = -1;

    // Where Alt+Shift+click draws a rectangle from: the last place that was clicked.
    private TextPoint? _columnAnchor;

    private sealed class DocumentText : IMultiCursorText
    {
        private readonly EditableTextDocument _document;

        public DocumentText(EditableTextDocument document)
        {
            _document = document;
        }

        public int LineCount => _document.LineCount;

        public string GetLine(int line) => _document.GetLineText(line);
    }

    /// <summary>The control has more than one cursor.</summary>
    public bool HasMultipleCursors => _extraCursors.Count > 0;

    /// <summary>The number of cursors (1 when there is just the caret).</summary>
    public int CursorCount => 1 + _extraCursors.Count;

    private static bool IsMultiCursorCommand(KeyBindingMap.EditorCommand command)
    {
        switch (command.Name)
        {
            case "AddNextOccurrence":
            case "SelectAllOccurrences":
            case "AddCursorAbove":
            case "AddCursorBelow":
                return true;
            default:
                return false;
        }
    }

    private bool CanUseCursors => IsEditable && !ReadOnly && EditableDocument != null;

    private IMultiCursorText CursorText => new DocumentText(EditableDocument);

    private static TextPoint ToPoint(TextDocument.TextPosition position) => new TextPoint(position.Line, position.Column);

    private static TextDocument.TextPosition ToPosition(TextPoint point) => new TextDocument.TextPosition(point.Column, point.Line);

    private CursorSet BuildCursorSet()
    {
        TextPoint caret = ToPoint(CaretPosition);
        TextPoint anchor = HasSelection ? ToPoint(SelectionStart) : caret;
        List<TextCursor> all = new List<TextCursor>(_extraCursors) { new TextCursor(anchor, caret, _primaryGoal) };
        return CursorSet.Create(all, all.Count - 1, _wholeWordOccurrences);
    }

    // Makes the cursors of the set the cursors of the control: the primary one becomes the caret and the selection.
    private void ShowCursorSet(CursorSet set)
    {
        bool wasApplying = _applyingMultiCursor;
        _applyingMultiCursor = true;
        try
        {
            TextCursor primary = set.PrimaryCursor;
            _extraCursors.Clear();
            for (int i = 0; i < set.Count; i++)
            {
                if (i != set.Primary)
                {
                    _extraCursors.Add(set.Items[i]);
                }
            }
            _wholeWordOccurrences = set.WholeWord;
            _primaryGoal = primary.Goal;
            SetSelection(primary.HasSelection, ToPosition(primary.Anchor));
            SetCaret(ToPosition(primary.Caret));
        }
        finally
        {
            _applyingMultiCursor = wasApplying;
        }
        Redraw();
    }

    /// <summary>Goes back to a single cursor (the main one stays).</summary>
    public void ClearExtraCursors()
    {
        if (_extraCursors.Count == 0)
        {
            return;
        }
        _extraCursors.Clear();
        _wholeWordOccurrences = false;
        _primaryGoal = -1;
        Redraw();
    }

    /// <summary>Ctrl+D: selects the word under the caret; with a selection, adds a cursor on its next occurrence.</summary>
    public void AddNextOccurrence()
    {
        ChangeCursors((IMultiCursorText text, CursorSet set) => MultiCursor.AddNextOccurrence(text, set));
    }

    /// <summary>Ctrl+Shift+L: a cursor on every occurrence of the selected text (or of the word under the caret).</summary>
    public void SelectAllOccurrences()
    {
        ChangeCursors((IMultiCursorText text, CursorSet set) => MultiCursor.SelectAllOccurrences(text, set));
    }

    public void AddCursorAbove()
    {
        ChangeCursors((IMultiCursorText text, CursorSet set) => MultiCursor.AddOnAdjacentLine(text, set, below: false));
    }

    public void AddCursorBelow()
    {
        ChangeCursors((IMultiCursorText text, CursorSet set) => MultiCursor.AddOnAdjacentLine(text, set, below: true));
    }

    private void ChangeCursors(Func<IMultiCursorText, CursorSet, CursorSet> change)
    {
        if (!CanUseCursors)
        {
            return;
        }
        CursorSet before = BuildCursorSet();
        CursorSet after = change(CursorText, before);
        if (!ReferenceEquals(before, after))
        {
            HideCompletion();
            ShowCursorSet(after);
        }
    }

    // Makes the edits of the plan in the document (last first, so that the places of the earlier ones hold) as one undo step.
    private void ApplyCursorPlan(EditPlan plan)
    {
        if (!CanUseCursors)
        {
            return;
        }
        bool wasApplying = _applyingMultiCursor;
        _applyingMultiCursor = true;
        try
        {
            ExecuteAsSingleUndoStep(() =>
            {
                for (int i = plan.Edits.Count - 1; i >= 0; i--)
                {
                    TextEdit edit = plan.Edits[i];
                    if (edit.Start == edit.End && edit.Text.Length == 0)
                    {
                        continue;
                    }
                    ReplaceText(ToPosition(edit.Start), ToPosition(edit.End), select: false, edit.Text);
                }
            });
            ShowCursorSet(ClampToText(plan.Result));
            UpdateMaxLineWidth(-1);
        }
        finally
        {
            _applyingMultiCursor = wasApplying;
        }
    }

    // The editor can change the text a little differently from the plan (it never should); a cursor must not end up outside the text.
    private CursorSet ClampToText(CursorSet set)
    {
        IMultiCursorText text = CursorText;
        bool changed = false;
        TextCursor[] items = (TextCursor[])set.Items.Clone();
        for (int i = 0; i < items.Length; i++)
        {
            TextPoint caret = ClampPoint(text, items[i].Caret);
            TextPoint anchor = ClampPoint(text, items[i].Anchor);
            if (caret != items[i].Caret || anchor != items[i].Anchor)
            {
                items[i] = new TextCursor(anchor, caret);
                changed = true;
            }
        }
        return changed ? CursorSet.Create(items, set.Primary) : set;
    }

    private static TextPoint ClampPoint(IMultiCursorText text, TextPoint point)
    {
        int line = Math.Max(0, Math.Min(point.Line, text.LineCount - 1));
        return new TextPoint(line, Math.Max(0, Math.Min(point.Column, text.GetLine(line).Length)));
    }

    private string CursorLineBreak()
    {
        string lineBreak = EditableDocument.GetEffectiveLineTerminator(CaretPosition.Line);
        return string.IsNullOrEmpty(lineBreak) ? "\n" : lineBreak;
    }

    /// <summary>Typing with several cursors; true when the key was dealt with.</summary>
    private bool HandleCursorKeyPress(KeyPressEventArgs e)
    {
        if (!HasMultipleCursors || !CanUseCursors)
        {
            return false;
        }
        IMultiCursorText text = CursorText;
        CursorSet set = BuildCursorSet();
        EditPlan plan;
        switch (e.KeyChar)
        {
            case '\r':
                plan = MultiCursor.NewLine(text, set, CursorLineBreak());
                break;
            case '\b':
                plan = MultiCursor.Backspace(text, set, InsertSpaces ? Math.Max(1, TabSize) : 0);
                break;
            case '\t':
                if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift)
                {
                    // Shift+Tab takes the indentation of the lines away: that is for one cursor.
                    ClearExtraCursors();
                    return false;
                }
                plan = MultiCursor.Tab(text, set, TabSize, InsertSpaces);
                break;
            default:
                if (char.IsControl(e.KeyChar))
                {
                    // Ctrl+letter: the commands are dealt with in OnKeyDown.
                    e.Handled = true;
                    return true;
                }
                plan = MultiCursor.Insert(text, set, new string(e.KeyChar, 1));
                break;
        }
        HideCompletion();
        ApplyCursorPlan(plan);
        e.Handled = true;
        return true;
    }

    /// <summary>The keys that move the cursors and delete with several cursors; true when the key was dealt with.</summary>
    private bool HandleCursorKeyDown(KeyEventArgs e)
    {
        if (!HasMultipleCursors || !CanUseCursors)
        {
            return false;
        }
        bool ctrl = e.Control && !e.Alt;
        bool plain = !e.Control && !e.Alt;
        IMultiCursorText text = CursorText;
        if (e.KeyCode == Keys.Escape)
        {
            ClearExtraCursors();
            HideCompletion();
            e.Handled = true;
            return true;
        }
        if (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown
            || (ctrl && (e.KeyCode == Keys.A || e.KeyCode == Keys.Z || e.KeyCode == Keys.Y || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)))
        {
            // These work on the whole text or on the view: one cursor, then the usual command.
            ClearExtraCursors();
            return false;
        }
        CursorMove? move = null;
        switch (e.KeyCode)
        {
            case Keys.Left:
                move = ctrl ? CursorMove.WordLeft : (plain ? CursorMove.Left : (CursorMove?)null);
                break;
            case Keys.Right:
                move = ctrl ? CursorMove.WordRight : (plain ? CursorMove.Right : (CursorMove?)null);
                break;
            case Keys.Up:
                move = plain ? CursorMove.Up : (CursorMove?)null;
                break;
            case Keys.Down:
                move = plain ? CursorMove.Down : (CursorMove?)null;
                break;
            case Keys.Home:
                move = plain ? CursorMove.Home : (CursorMove?)null;
                break;
            case Keys.End:
                move = plain ? CursorMove.End : (CursorMove?)null;
                break;
            case Keys.Back:
                if (ctrl && !e.Shift)
                {
                    HideCompletion();
                    ApplyCursorPlan(MultiCursor.DeleteWord(text, BuildCursorSet(), forward: false));
                    e.Handled = true;
                    // Ctrl+Backspace types a control character that must not reach the text.
                    e.SuppressKeyPress = true;
                    return true;
                }
                return false;
            case Keys.Delete:
                if (!e.Shift && !e.Alt)
                {
                    HideCompletion();
                    CursorSet set = BuildCursorSet();
                    ApplyCursorPlan(e.Control ? MultiCursor.DeleteWord(text, set, forward: true) : MultiCursor.Delete(text, set));
                    e.Handled = true;
                    return true;
                }
                return false;
        }
        if (move.HasValue)
        {
            HideCompletion();
            ShowCursorSet(MultiCursor.Move(text, BuildCursorSet(), move.Value, e.Shift));
            e.Handled = true;
            return true;
        }
        return false;
    }

    private static string ForClipboard(IEnumerable<string> texts)
    {
        return string.Join("\r\n", texts.Select(item => item.Replace("\r\n", "\n").Replace("\n", "\r\n")));
    }

    /// <summary>With several cursors: the selected texts, one under the other.</summary>
    private bool TryCopyCursors(out List<string> texts)
    {
        texts = null;
        if (!HasMultipleCursors)
        {
            return false;
        }
        texts = MultiCursor.SelectedTexts(CursorText, BuildCursorSet());
        return texts.Count > 0;
    }

    public override void CopySelection(bool select = true)
    {
        if (TryCopyCursors(out List<string> texts))
        {
            TryCopyToClipboard(ForClipboard(texts));
            return;
        }
        base.CopySelection(select);
    }

    public override void Cut()
    {
        if (CanUseCursors && TryCopyCursors(out List<string> texts))
        {
            if (TryCopyToClipboard(ForClipboard(texts)))
            {
                ApplyCursorPlan(MultiCursor.DeleteSelections(CursorText, BuildCursorSet()));
            }
            return;
        }
        base.Cut();
    }

    public override void PasteText(string text)
    {
        if (!HasMultipleCursors || !CanUseCursors || string.IsNullOrEmpty(text))
        {
            base.PasteText(text);
            return;
        }
        CursorSet set = BuildCursorSet();
        // A text with a line for each cursor (what Copy puts on the clipboard at several cursors) gives each cursor its own line.
        string[] parts = MultiCursor.SplitForCursors(text, set.Count);
        HideCompletion();
        ApplyCursorPlan(parts != null ? MultiCursor.InsertPerCursor(CursorText, set, parts) : MultiCursor.Insert(CursorText, set, text));
    }

    /// <summary>Alt+click adds a cursor (or takes one away); Alt+Shift+click makes a cursor on every line down to the click. True when dealt with.</summary>
    private bool HandleCursorMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || (Control.ModifierKeys & Keys.Alt) == 0 || !CanUseCursors)
        {
            return false;
        }
        TextDocument.TextPosition hit = HitTest(e.Location, allowPastEnd: true);
        if (!hit.IsValid)
        {
            return false;
        }
        IMultiCursorText text = CursorText;
        TextPoint point = ToPoint(hit);
        CursorSet set;
        if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift)
        {
            set = MultiCursor.SelectColumns(text, _columnAnchor ?? ToPoint(CaretPosition), point);
        }
        else
        {
            set = MultiCursor.ToggleAt(text, BuildCursorSet(), point);
            _columnAnchor = point;
        }
        HideCompletion();
        ShowCursorSet(set);
        SuppressMouseSelection = true;
        Focus();
        return true;
    }

    protected override void PaintExtraSelections(Graphics graphics, Brush[,] backgroundBrushes)
    {
        foreach (TextCursor cursor in _extraCursors)
        {
            if (cursor.HasSelection)
            {
                PaintSelection(graphics, ToPosition(cursor.Start), ToPosition(cursor.End), SelectionStyle, backgroundBrushes);
            }
        }
    }

    protected override void PaintExtraCarets(Graphics graphics)
    {
        if (_extraCursors.Count == 0)
        {
            return;
        }
        int width = Math.Max(1, (int)Math.Round(CharSize.Width / 8f));
        using (Brush brush = new SolidBrush(ForeColor))
        {
            foreach (TextCursor cursor in _extraCursors)
            {
                TextDocument.TextPosition caret = ToPosition(cursor.Caret);
                if (caret.Line >= EditableDocument.LineCount || !TryGetCaretColumn(caret, out int column))
                {
                    continue;
                }
                PointF point = PositionToPoint(column, caret.Line);
                // Not above the first row, and not over the margins when the text is scrolled sideways.
                if (point.Y < 0f || point.X < TextArea.Left || point.X > TextArea.Right)
                {
                    continue;
                }
                graphics.FillRectangle(brush, point.X, point.Y, width, CharSize.Height);
            }
        }
    }
}
