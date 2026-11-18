using SmarTTY.TextEditing;

namespace SmarTTY.Terminal.Emulation;

/// <summary>
/// What the store of the line information needs to know of the terminal that owns it: where a screen row is in the text (the rows
/// scrolled off the screen are counted), whether lines are kept even when they have no style, and how wide the screen is.
/// </summary>
public interface ILineInfoHost
{
    /// <summary>The position in the text of a screen cell (the row is counted from the top of the screen of the active buffer).</summary>
    TextDocument.TextPosition ToDocumentPosition(GridPoint point);

    /// <summary>Lines are kept even when nothing on them has a style.</summary>
    bool AlwaysTrackLines { get; }

    /// <summary>Margins (line numbers, time stamps) are hidden: a line without a style needs no information.</summary>
    bool HideMargins { get; }

    /// <summary>The number of columns of the screen.</summary>
    int VisibleColumns { get; }
}
