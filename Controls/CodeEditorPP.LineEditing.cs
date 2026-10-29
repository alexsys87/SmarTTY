using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
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
        first = CaretPosition.Line;
        last = first;
        if (!HasSelection)
        {
            return;
        }
        TextDocument.TextPosition start = SelectionStart;
        TextDocument.TextPosition end = CaretPosition;
        if (end < start)
        {
            TextDocument.TextPosition swap = start;
            start = end;
            end = swap;
        }
        first = start.Line;
        last = end.Line;
        // A selection that ends at the very start of a line does not include that line.
        if (end.Column == 0 && last > first)
        {
            last--;
        }
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

    /// <summary>Inserts a copy of the selected lines (or of the caret line) below them.</summary>
    public void DuplicateLines()
    {
        if (!CanEditLines)
        {
            return;
        }
        GetCommandLines(out int first, out int last);
        StringBuilder copy = new StringBuilder();
        for (int line = first; line <= last; line++)
        {
            copy.Append(LineEnd(last)).Append(EditableDocument.GetLineText(line));
        }
        // The copy goes in front of the line break of the last line, so a file without a final line break stays without it.
        bool hadSelection = HasSelection;
        TextDocument.TextPosition anchor = SelectionStart;
        TextDocument.TextPosition caret = CaretPosition;
        ReplaceText(EndOfLine(last), EndOfLine(last), select: false, copy.ToString());
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
        StringBuilder text = new StringBuilder();
        if (direction < 0)
        {
            AppendLines(text, first, last, lineEnd);
            text.Append(lineEnd).Append(EditableDocument.GetLineText(neighbor));
        }
        else
        {
            text.Append(EditableDocument.GetLineText(neighbor)).Append(lineEnd);
            AppendLines(text, first, last, lineEnd);
        }
        bool hadSelection = HasSelection;
        TextDocument.TextPosition anchor = SelectionStart;
        TextDocument.TextPosition caret = CaretPosition;
        ReplaceText(new TextDocument.TextPosition(0, top), EndOfLine(bottom), select: false, text.ToString());
        RestoreAfterEdit(hadSelection, anchor, caret, direction);
    }

    private void AppendLines(StringBuilder text, int first, int last, string lineEnd)
    {
        for (int line = first; line <= last; line++)
        {
            if (line > first)
            {
                text.Append(lineEnd);
            }
            text.Append(EditableDocument.GetLineText(line));
        }
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
        string[] oldLines = new string[last - first + 1];
        for (int i = 0; i < oldLines.Length; i++)
        {
            oldLines[i] = EditableDocument.GetLineText(first + i);
        }
        string[] newLines = CommentStyle.Toggle(oldLines);
        bool hadSelection = HasSelection;
        TextDocument.TextPosition anchor = SelectionStart;
        TextDocument.TextPosition caret = CaretPosition;
        ExecuteAsSingleUndoStep(() =>
        {
            for (int i = 0; i < oldLines.Length; i++)
            {
                if (oldLines[i] == newLines[i])
                {
                    continue;
                }
                // Only the part that differs is replaced.
                string before = oldLines[i];
                string after = newLines[i];
                int prefix = CommonPrefixLength(before, after);
                int suffix = CommonSuffixLength(before, after, prefix);
                ReplaceText(new TextDocument.TextPosition(prefix, first + i), new TextDocument.TextPosition(before.Length - suffix, first + i), select: false,
                    after.Substring(prefix, after.Length - suffix - prefix));
            }
        });
        anchor.Column = ShiftColumn(anchor, first, oldLines, newLines);
        caret.Column = ShiftColumn(caret, first, oldLines, newLines);
        RestoreAfterEdit(hadSelection, anchor, caret, 0);
        UpdateMaxLineWidth(-1);
        return true;
    }

    private static int ShiftColumn(TextDocument.TextPosition position, int first, string[] oldLines, string[] newLines)
    {
        int index = position.Line - first;
        if (index < 0 || index >= oldLines.Length || oldLines[index] == newLines[index])
        {
            return position.Column;
        }
        int changeStart = CommonPrefixLength(oldLines[index], newLines[index]);
        if (position.Column <= changeStart)
        {
            return position.Column;
        }
        int delta = newLines[index].Length - oldLines[index].Length;
        return Math.Max(changeStart, Math.Min(position.Column + delta, newLines[index].Length));
    }

    private static int CommonPrefixLength(string a, string b)
    {
        int length = 0;
        while (length < a.Length && length < b.Length && a[length] == b[length])
        {
            length++;
        }
        return length;
    }

    private static int CommonSuffixLength(string a, string b, int prefix)
    {
        int length = 0;
        while (length < a.Length - prefix && length < b.Length - prefix && a[a.Length - 1 - length] == b[b.Length - 1 - length])
        {
            length++;
        }
        return length;
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
