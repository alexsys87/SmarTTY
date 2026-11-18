using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace SmarTTY.Controls;

// Line commands (duplicate, move, delete, comment) and the zoom of the text.
public partial class CodeEditorPP
{
    private const float ZoomStep = 0.1f;

    /// <summary>How the file marks comments (Ctrl+/); null when its language has no comments.</summary>
    public virtual CommentStyle CommentStyle => null;

    /// <summary>The zoom of the text was changed with Ctrl+wheel, Ctrl+0 or the zoom commands.</summary>
    public event EventHandler ZoomChanged;

    private bool CanEditLines => IsEditable && !ReadOnly;

    /// <summary>The first and the last line a line command works on: the lines of the selection, or the line of the caret.</summary>
    private void GetCommandLines(out int first, out int last)
    {
        LineEditing.GetCommandLines(CaretPosition.Line, HasSelection, SelectionStart, CaretPosition, out first, out last);
    }

    private string LineEnd(int line)
    {
        return EditableDocument.GetEffectiveLineTerminator(line);
    }

    private TextDocument.TextPosition EndOfLine(int line)
    {
        return new TextDocument.TextPosition(EditableDocument.GetLineText(line).Length, line);
    }

    /// <summary>Puts the selection (if there was one) and the caret back after an edit, moved by <paramref name="lineShift"/> lines.</summary>
    private void RestoreAfterEdit(bool hadSelection, TextDocument.TextPosition anchor, TextDocument.TextPosition caret, int lineShift)
    {
        anchor.Line += lineShift;
        caret.Line += lineShift;
        SetSelection(hadSelection, anchor);
        SetCaret(caret);
    }

    /// <summary>The selection covers two lines or more (what sorting and removing duplicates work on).</summary>
    public bool HasLineSelection => LineEditing.HasLineSelection(CaretPosition.Line, HasSelection, SelectionStart, CaretPosition);

    /// <summary>
    /// Rewrites lines as <paramref name="kind"/> says (the Text menu): the selected lines, or the whole text when nothing is
    /// selected; sorting and removing duplicates need two lines or more selected. One undo step. Returns false when nothing
    /// could be done (the editor is read-only, or lines were needed and not selected).
    /// </summary>
    public bool ApplyLineTransform(LineTransformKind kind)
    {
        if (!CanEditLines)
        {
            return false;
        }
        if (kind == LineTransformKind.EnsureFinalNewline)
        {
            int lastLine = LineCount - 1;
            // An empty last line means the text already ends with a line break (or is empty).
            if (lastLine < 0 || EditableDocument.GetLineText(lastLine).Length == 0)
            {
                return true;
            }
            ExecuteAsSingleUndoStep(() =>
            {
                ReplaceText(EndOfLine(lastLine), EndOfLine(lastLine), select: false, LineEnd(lastLine));
            });
            UpdateMaxLineWidth(-1);
            return true;
        }
        if (!LineEditing.TryGetTransformLines(kind, LineCount, CaretPosition.Line, HasSelection, SelectionStart, CaretPosition, out int first, out int last))
        {
            return false;
        }
        string[] oldLines = GetLines(first, last);
        string[] newLines = LineTransforms.Apply(kind, oldLines, TabSize);
        if (newLines.Length == oldLines.Length && newLines.SequenceEqual(oldLines))
        {
            return true;
        }
        ReplaceLineTexts(first, oldLines, newLines);
        return true;
    }

    /// <summary>Puts <paramref name="newLines"/> in the place of <paramref name="oldLines"/> (which start at line <paramref name="first"/>) in one undo step.</summary>
    private void ReplaceLineTexts(int first, string[] oldLines, string[] newLines)
    {
        bool hadSelection = HasSelection;
        TextDocument.TextPosition anchor = SelectionStart;
        TextDocument.TextPosition caret = CaretPosition;
        if (newLines.Length == oldLines.Length)
        {
            ExecuteAsSingleUndoStep(() =>
            {
                foreach (LineEditing.TextEdit edit in LineEditing.DiffLines(first, oldLines, newLines))
                {
                    ReplaceText(edit.Start, edit.End, select: false, edit.Text);
                }
            });
            anchor.Column = LineEditing.ShiftColumn(anchor, first, oldLines, newLines);
            caret.Column = LineEditing.ShiftColumn(caret, first, oldLines, newLines);
            RestoreAfterEdit(hadSelection, anchor, caret, 0);
        }
        else
        {
            // Lines were removed: the whole block is replaced, and the new block is selected.
            int last = first + oldLines.Length - 1;
            string lineEnd = LineEnd(first);
            ExecuteAsSingleUndoStep(() =>
            {
                ReplaceText(new TextDocument.TextPosition(0, first), EndOfLine(last), select: false, string.Join(lineEnd, newLines));
            });
            RestoreAfterEdit(true, new TextDocument.TextPosition(0, first), EndOfLine(first + newLines.Length - 1), 0);
        }
        UpdateMaxLineWidth(-1);
    }

    /// <summary>Inserts a copy of the selected lines (or of the caret line) below them.</summary>
    public void DuplicateLines()
    {
        if (!CanEditLines)
        {
            return;
        }
        GetCommandLines(out int first, out int last);
        string copy = LineEditing.DuplicatedText(GetLines(first, last), LineEnd(last));
        // The copy goes in front of the line break of the last line, so a file without a final line break stays without it.
        bool hadSelection = HasSelection;
        TextDocument.TextPosition anchor = SelectionStart;
        TextDocument.TextPosition caret = CaretPosition;
        ReplaceText(EndOfLine(last), EndOfLine(last), select: false, copy);
        RestoreAfterEdit(hadSelection, anchor, caret, last - first + 1);
        UpdateMaxLineWidth(-1);
    }

    /// <summary>Moves the selected lines (or the caret line) one line up (-1) or down (1).</summary>
    public void MoveLines(int direction)
    {
        if (!CanEditLines)
        {
            return;
        }
        GetCommandLines(out int first, out int last);
        if ((direction < 0 && first == 0) || (direction > 0 && last >= LineCount - 1))
        {
            return;
        }
        int top = direction < 0 ? first - 1 : first;
        int bottom = direction < 0 ? last : last + 1;
        string lineEnd = LineEnd(top);
        // The block and the line it swaps places with, in the new order.
        int neighbor = direction < 0 ? first - 1 : last + 1;
        string text = LineEditing.MovedText(GetLines(first, last), EditableDocument.GetLineText(neighbor), lineEnd, direction);
        bool hadSelection = HasSelection;
        TextDocument.TextPosition anchor = SelectionStart;
        TextDocument.TextPosition caret = CaretPosition;
        ReplaceText(new TextDocument.TextPosition(0, top), EndOfLine(bottom), select: false, text);
        RestoreAfterEdit(hadSelection, anchor, caret, direction);
    }

    /// <summary>The texts of the lines <paramref name="first"/> to <paramref name="last"/>, without their line breaks.</summary>
    private string[] GetLines(int first, int last)
    {
        string[] lines = new string[last - first + 1];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = EditableDocument.GetLineText(first + i);
        }
        return lines;
    }

    /// <summary>Removes the selected lines (or the caret line) together with their line breaks.</summary>
    public void DeleteLines()
    {
        if (!CanEditLines)
        {
            return;
        }
        GetCommandLines(out int first, out int last);
        RemoveLines(first, last);
        SetSelection(false, CaretPosition);
        UpdateMaxLineWidth(-1);
    }

    /// <summary>
    /// Comments out the selected lines (or the caret line), or uncomments them when they are all commented.
    /// Returns false when the file has no comment syntax known.
    /// </summary>
    public bool ToggleComment()
    {
        if (!CanEditLines || CommentStyle == null)
        {
            return false;
        }
        GetCommandLines(out int first, out int last);
        string[] oldLines = GetLines(first, last);
        string[] newLines = CommentStyle.Toggle(oldLines);
        bool hadSelection = HasSelection;
        TextDocument.TextPosition anchor = SelectionStart;
        TextDocument.TextPosition caret = CaretPosition;
        ExecuteAsSingleUndoStep(() =>
        {
            foreach (LineEditing.TextEdit edit in LineEditing.DiffLines(first, oldLines, newLines))
            {
                ReplaceText(edit.Start, edit.End, select: false, edit.Text);
            }
        });
        anchor.Column = LineEditing.ShiftColumn(anchor, first, oldLines, newLines);
        caret.Column = LineEditing.ShiftColumn(caret, first, oldLines, newLines);
        RestoreAfterEdit(hadSelection, anchor, caret, 0);
        UpdateMaxLineWidth(-1);
        return true;
    }

    /// <summary>Makes the text bigger (1) or smaller (-1) by a tenth of its size.</summary>
    public void ZoomBy(int steps)
    {
        SetZoom((float)Math.Round(FontZoom / ZoomStep + steps) * ZoomStep);
    }

    /// <summary>Shows the text in the size of the font again.</summary>
    public void ResetZoom()
    {
        SetZoom(1f);
    }

    /// <summary>Sets the zoom (1 is the size of the font); the value is kept between 50% and 400%.</summary>
    public void SetZoom(float zoom)
    {
        float before = FontZoom;
        FontZoom = (float)Math.Round(zoom, 2);
        if (FontZoom != before)
        {
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((Control.ModifierKeys & Keys.Control) == Keys.Control && e.Delta != 0)
        {
            ZoomBy(e.Delta > 0 ? 1 : -1);
            if (e is HandledMouseEventArgs handled)
            {
                handled.Handled = true;
            }
            return;
        }
        base.OnMouseWheel(e);
    }
}
