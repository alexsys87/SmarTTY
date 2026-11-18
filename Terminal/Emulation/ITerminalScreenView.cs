using SmarTTY.TextEditing;

namespace SmarTTY.Terminal.Emulation;

/// <summary>
/// What <see cref="TerminalScreen"/> needs of the control that shows it: the size of the view, the document it shows, the caret, the scroll
/// position, the selection, and the calls that make the view draw again. Every member is one that the screen operations called on the control
/// before. All calls are synchronous and come from the thread that called the screen.
/// </summary>
internal interface ITerminalScreenView
{
    /// <summary>The number of rows of the view.</summary>
    int VisibleRows { get; }

    /// <summary>The number of columns of the view.</summary>
    int VisibleColumns { get; }

    /// <summary>The document that the view shows (the one of the active buffer, once <see cref="SetDocument"/> has been called).</summary>
    EditableTextDocument Document { get; }

    /// <summary>The first line of the document that is in the view (the setter keeps it between 0 and the end of the scroll bar).</summary>
    int TopLine { get; set; }

    /// <summary>Lines are kept even when nothing on them has a style.</summary>
    bool AlwaysTrackLines { get; }

    /// <summary>Margins (line numbers, time stamps) are hidden: a line without a style needs no information.</summary>
    bool HideMargins { get; }

    /// <summary>The start of the selection (the position the selection goes back to when it is cleared).</summary>
    TextDocument.TextPosition SelectionStart { get; }

    /// <summary>The caret is hidden (CSI ? 25 l).</summary>
    bool HideCaret { set; }

    /// <summary>The control writes a Debug trace of the text it gets (false in a Release build, so that no text for it is made).</summary>
    bool TraceEnabled { get; }

    /// <summary>Shows another document (the buffer was switched).</summary>
    void SetDocument(EditableTextDocument document);

    /// <summary>Puts the caret of the view in a place of the document.</summary>
    void SetCaret(TextDocument.TextPosition position);

    /// <summary>Makes the view take the caret position again on its next move, even to the same place.</summary>
    void ForceCaretUpdate();

    /// <summary>Sets whether there is a selection (false drops it) and the position it is anchored at.</summary>
    void SetSelection(bool select, TextDocument.TextPosition position);

    /// <summary>The text of the document was replaced as a whole (lines were removed or inserted, the document was cleared).</summary>
    void OnTextReplaced();

    /// <summary>The width of a line changed (no wrap mode): the view finds its widest line again. -1: all lines.</summary>
    void UpdateMaxLineWidth(int line);

    /// <summary>The number of lines or the widths changed: the scroll bars and the metrics are set again.</summary>
    void UpdateLayout();

    /// <summary>Draws the view again.</summary>
    void Redraw();

    /// <summary>The scanner that finds links in the text written next, or null.</summary>
    ILinkScanner CreateLinkScanner();

    /// <summary>Writes a line of the Debug trace.</summary>
    void Trace(string text);
}
