using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace SmarTTY.Controls;

// Line commands (duplicate, move, delete, comment) and the zoom of the text.
public partial class CodeEditorPP : ILineCommandHost
{
    /// <summary>How the file marks comments (Ctrl+/); null when its language has no comments.</summary>
    public virtual CommentStyle CommentStyle => null;

    /// <summary>The zoom of the text was changed with Ctrl+wheel, Ctrl+0 or the zoom commands.</summary>
    public event EventHandler ZoomChanged;

    // The line commands (made when first used: the base class constructors may already run edits).
    private LineCommands _lineCommands;

    private LineCommands LineCommands => _lineCommands ?? (_lineCommands = new LineCommands(this));

    /// <summary>The selection covers two lines or more (what sorting and removing duplicates work on).</summary>
    public bool HasLineSelection => LineCommands.HasLineSelection;

    /// <summary>
    /// Rewrites lines as <paramref name="kind"/> says (the Text menu): the selected lines, or the whole text when nothing is
    /// selected; sorting and removing duplicates need two lines or more selected. One undo step. Returns false when nothing
    /// could be done (the editor is read-only, or lines were needed and not selected).
    /// </summary>
    public bool ApplyLineTransform(LineTransformKind kind)
    {
        return LineCommands.ApplyLineTransform(kind);
    }

    /// <summary>Inserts a copy of the selected lines (or of the caret line) below them.</summary>
    public void DuplicateLines()
    {
        LineCommands.DuplicateLines();
    }

    /// <summary>Moves the selected lines (or the caret line) one line up (-1) or down (1).</summary>
    public void MoveLines(int direction)
    {
        LineCommands.MoveLines(direction);
    }

    /// <summary>Removes the selected lines (or the caret line) together with their line breaks.</summary>
    public void DeleteLines()
    {
        LineCommands.DeleteLines();
    }

    /// <summary>
    /// Comments out the selected lines (or the caret line), or uncomments them when they are all commented.
    /// Returns false when the file has no comment syntax known.
    /// </summary>
    public bool ToggleComment()
    {
        return LineCommands.ToggleComment();
    }

    EditableTextDocument ILineCommandHost.Document => EditableDocument;

    bool ILineCommandHost.CanEditLines => IsEditable && !ReadOnly;

    void ILineCommandHost.RemoveLines(int first, int last)
    {
        RemoveLines(first, last);
    }

    void ILineCommandHost.SetCaret(TextDocument.TextPosition position)
    {
        SetCaret(position);
    }

    void ILineCommandHost.UpdateMaxLineWidth(int line)
    {
        UpdateMaxLineWidth(line);
    }

    /// <summary>Makes the text bigger (1) or smaller (-1) by a tenth of its size.</summary>
    public void ZoomBy(int steps)
    {
        SetZoom(EditorZoom.By(FontZoom, steps));
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
        FontZoom = EditorZoom.Round(zoom);
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
