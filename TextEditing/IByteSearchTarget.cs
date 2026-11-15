namespace SmarTTY.TextEditing;

/// <summary>
/// A view that shows a stream of bytes in rows (the HEX terminal) and finds a byte sequence in the stream itself, so
/// that a sequence that goes on in the next row is found too. The search bar uses it instead of the search in the
/// lines of the text when the searched text is a sequence of HEX bytes.
/// </summary>
public interface IByteSearchTarget
{
    /// <summary>
    /// Finds the next (or the previous) match from the caret or from the match found last, selects it and scrolls
    /// it into view. Returns false, and removes the selection, when there is none.
    /// </summary>
    bool FindNextBytes(byte[] pattern, bool backward, bool wrapAround);

    /// <summary>Marks the matches of <paramref name="pattern"/> in the visible rows; null removes the marks.</summary>
    void SetByteHighlight(byte[] pattern);
}
