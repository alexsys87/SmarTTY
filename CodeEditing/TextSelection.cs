using SmarTTY.TextEditing;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The selection of the text view: whether there is one, where it starts (the anchor; the caret is its other end) and, in terminal mode, its own
/// end (the caret of a terminal is the cursor of the screen, not an end of the selection). The rules the view applies to it: a selection that a
/// key with Shift, a click with Shift or a drag starts only when there is none, the selected lines, the range of a line selection, the end of
/// Select all. It is what <c>Controls/ReadonlyTextBrowser</c> kept in <c>_hasSelection</c>, <c>_selectionStart</c> and <c>_selectionEnd</c> and did
/// in <c>SelectionEnd</c>, <c>ClearSelection</c>, <c>SelectedLines</c>, <c>SetSelectionCore</c>, <c>UpdateSelectionFromPoint</c>,
/// <c>SelectLineRange</c> and <c>SelectAll</c>, moved here as it was (stage 3C); the view handles the mouse, the keys, the clicks and their timing,
/// redraws and raises its events.
/// </summary>
public sealed class TextSelection
{
    /// <summary>There is a selection.</summary>
    public bool HasSelection { get; set; }

    /// <summary>Where the selection starts.</summary>
    public TextDocument.TextPosition Anchor { get; set; }

    /// <summary>The end of the selection in terminal mode.</summary>
    public TextDocument.TextPosition TerminalEnd { get; set; }

    /// <summary>The other end of the selection: its own end in terminal mode, else the caret.</summary>
    public TextDocument.TextPosition End(bool terminalMode, TextDocument.TextPosition caret)
    {
        if (terminalMode)
        {
            return TerminalEnd;
        }
        return caret;
    }

    /// <summary>Switches the selection on or off; true when that changed it.</summary>
    public bool SetActive(bool select)
    {
        if (HasSelection == select)
        {
            return false;
        }
        HasSelection = select;
        return true;
    }

    /// <summary>Moves the start of the selection; true when it moved.</summary>
    public bool MoveAnchor(TextDocument.TextPosition anchor)
    {
        if (Anchor == anchor)
        {
            return false;
        }
        Anchor = anchor;
        return true;
    }

    /// <summary>Moves the end of a terminal selection; true when it moved.</summary>
    public bool MoveTerminalEnd(TextDocument.TextPosition end)
    {
        if (TerminalEnd == end)
        {
            return false;
        }
        TerminalEnd = end;
        return true;
    }

    /// <summary>Removes the selection; false when there was none.</summary>
    public bool Clear()
    {
        if (!HasSelection)
        {
            return false;
        }
        HasSelection = false;
        return true;
    }

    /// <summary>Starts a selection at <paramref name="anchor"/> when there is none (a key or a click with Shift, a drag); true when it started one.</summary>
    public bool StartIfNone(TextDocument.TextPosition anchor)
    {
        if (HasSelection)
        {
            return false;
        }
        HasSelection = true;
        Anchor = anchor;
        return true;
    }

    /// <summary>
    /// The lines the selection covers, from <paramref name="first"/> to <paramref name="last"/> (none: 0 and -1); a selection that ends at the start
    /// of a line does not take that line.
    /// </summary>
    public void GetSelectedLines(TextDocument.TextPosition end, out int first, out int last)
    {
        if (!HasSelection || end == Anchor)
        {
            first = 0;
            last = -1;
            return;
        }
        if (end < Anchor)
        {
            first = end.Line;
            last = Anchor.LastFullLine;
            return;
        }
        first = Anchor.Line;
        last = end.LastFullLine;
    }

    /// <summary>Puts a range of lines in order and into the text; false when nothing of it is left.</summary>
    public static bool ClampLineRange(ref int firstLine, ref int lastLine, int lineCount)
    {
        if (firstLine > lastLine)
        {
            int swap = firstLine;
            firstLine = lastLine;
            lastLine = swap;
        }
        if (firstLine < 0)
        {
            firstLine = 0;
        }
        if (lastLine >= lineCount)
        {
            lastLine = lineCount - 1;
        }
        return lastLine >= firstLine;
    }

    /// <summary>The end of the text, where Select all puts the caret.</summary>
    public static TextDocument.TextPosition DocumentEnd(TextDocument document)
    {
        return new TextDocument.TextPosition(document.GetLineText(document.LineCount - 1).Length, document.LineCount - 1);
    }
}
