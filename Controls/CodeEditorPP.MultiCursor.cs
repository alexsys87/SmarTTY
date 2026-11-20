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
// what they did. The operations on the cursors are CodeEditing/MultiCursor.cs and what the editor does with them is
// CodeEditing/MultiCursorSession.cs (free of the UI, checked by tests/MultiCursorTests and tests/MultiCursorSessionTests); this part draws
// them and feeds the keys, the mouse and the clipboard to the session.
//
// The main cursor is the caret and the selection of the control itself; the others are in the session. Anything that is not done by the
// session (a click, a find, an undo, a command from a menu) puts the control back to a single cursor.
public partial class CodeEditorPP : IMultiCursorHost
{
    // The cursors (made when first used: the base class constructors may already edit the text or move the caret).
    private MultiCursorSession _cursors;

    private MultiCursorSession CursorSession => _cursors ?? (_cursors = new MultiCursorSession(this));

    /// <summary>The control has more than one cursor.</summary>
    public bool HasMultipleCursors => CursorSession.HasMultipleCursors;

    /// <summary>The number of cursors (1 when there is just the caret).</summary>
    public int CursorCount => CursorSession.CursorCount;

    private static bool IsMultiCursorCommand(KeyBindingMap.EditorCommand command)
    {
        return MultiCursorSession.IsMultiCursorCommand(command.Name);
    }

    private static TextDocument.TextPosition ToPosition(TextPoint point) => new TextDocument.TextPosition(point.Column, point.Line);

    EditableTextDocument IMultiCursorHost.Document => EditableDocument;

    bool IMultiCursorHost.CanUseCursors => IsEditable && !ReadOnly && EditableDocument != null;

    void IMultiCursorHost.SetCaret(TextDocument.TextPosition position)
    {
        SetCaret(position);
    }

    void IMultiCursorHost.UpdateMaxLineWidth(int line)
    {
        UpdateMaxLineWidth(line);
    }

    /// <summary>Goes back to a single cursor (the main one stays).</summary>
    public void ClearExtraCursors()
    {
        CursorSession.ClearExtraCursors();
    }

    /// <summary>Ctrl+D: selects the word under the caret; with a selection, adds a cursor on its next occurrence.</summary>
    public void AddNextOccurrence()
    {
        CursorSession.AddNextOccurrence();
    }

    /// <summary>Ctrl+Shift+L: a cursor on every occurrence of the selected text (or of the word under the caret).</summary>
    public void SelectAllOccurrences()
    {
        CursorSession.SelectAllOccurrences();
    }

    public void AddCursorAbove()
    {
        CursorSession.AddCursorAbove();
    }

    public void AddCursorBelow()
    {
        CursorSession.AddCursorBelow();
    }

    /// <summary>Typing with several cursors; true when the key was dealt with.</summary>
    private bool HandleCursorKeyPress(KeyPressEventArgs e)
    {
        if (!CursorSession.TypeCharacter(e.KeyChar, (Control.ModifierKeys & Keys.Shift) == Keys.Shift))
        {
            return false;
        }
        e.Handled = true;
        return true;
    }

    /// <summary>The keys that move the cursors and delete with several cursors; true when the key was dealt with.</summary>
    private bool HandleCursorKeyDown(KeyEventArgs e)
    {
        CursorKeyResult result = CursorSession.KeyDown(e.KeyCode.ToVirtualKey(), e.Control, e.Shift, e.Alt);
        if (result == CursorKeyResult.NotHandled)
        {
            return false;
        }
        e.Handled = true;
        if (result == CursorKeyResult.HandledSuppressKeyPress)
        {
            e.SuppressKeyPress = true;
        }
        return true;
    }

    public override void CopySelection(bool select = true)
    {
        if (CursorSession.TryCopyCursors(out List<string> texts))
        {
            TryCopyToClipboard(MultiCursorSession.ForClipboard(texts));
            return;
        }
        base.CopySelection(select);
    }

    public override void Cut()
    {
        if (CursorSession.TryCut(TryCopyToClipboard))
        {
            return;
        }
        base.Cut();
    }

    public override void PasteText(string text)
    {
        if (!CursorSession.TryPaste(text))
        {
            base.PasteText(text);
        }
    }

    /// <summary>Alt+click adds a cursor (or takes one away); Alt+Shift+click makes a cursor on every line down to the click. True when dealt with.</summary>
    private bool HandleCursorMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || (Control.ModifierKeys & Keys.Alt) == 0 || !((IMultiCursorHost)this).CanUseCursors)
        {
            return false;
        }
        TextDocument.TextPosition hit = HitTest(e.Location, allowPastEnd: true);
        if (!hit.IsValid)
        {
            return false;
        }
        CursorSession.AltClick(hit, (Control.ModifierKeys & Keys.Shift) == Keys.Shift);
        SuppressMouseSelection = true;
        Focus();
        return true;
    }

    protected override void PaintExtraSelections(Graphics graphics, Brush[,] backgroundBrushes)
    {
        foreach (TextCursor cursor in CursorSession.ExtraCursors)
        {
            if (cursor.HasSelection)
            {
                PaintSelection(graphics, ToPosition(cursor.Start), ToPosition(cursor.End), SelectionStyle, backgroundBrushes);
            }
        }
    }

    protected override void PaintExtraCarets(Graphics graphics)
    {
        if (CursorSession.ExtraCursors.Count == 0)
        {
            return;
        }
        int width = Math.Max(1, (int)Math.Round(CharSize.Width / 8f));
        using (Brush brush = new SolidBrush(ForeColor))
        {
            foreach (TextCursor cursor in CursorSession.ExtraCursors)
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
