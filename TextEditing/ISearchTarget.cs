namespace SmarTTY.TextEditing;

/// <summary>The range of the text that an edit changed: where it begins, where it ended, where it ends now.</summary>
public delegate void TextRangeChangedHandler(TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd);

/// <summary>
/// The text a search bar works on: what <c>TextSearcher</c> asked of the editor control (the selection, the search and the replacements, the
/// highlight of the matches and of the range the search is limited to, and a note when the text is edited). Implemented by
/// <c>EditableTextBrowser</c>. A view that searches a stream of bytes is an <see cref="IByteSearchTarget"/>.
/// </summary>
public interface ISearchTarget
{
    /// <summary>The selection has something selected.</summary>
    bool HasSelection { get; }

    /// <summary>One end of the selection (the caret is at the other end); invalid when nothing is selected.</summary>
    TextDocument.TextPosition SelectionStart { get; }

    /// <summary>The other end of the selection.</summary>
    TextDocument.TextPosition SelectionEnd { get; }

    /// <summary>The text of a line, without its line break.</summary>
    string GetLineText(int line);

    /// <summary>The text between two positions.</summary>
    string GetRangeText(TextDocument.TextPosition start, TextDocument.TextPosition end);

    /// <summary>
    /// Finds the next match from <paramref name="textPosition"/> and does with the selection what <paramref name="findSelectMode"/> says. A valid
    /// <paramref name="scopeStart"/>/<paramref name="scopeEnd"/> limits the search to that range. Returns where the match begins, or an invalid position.
    /// </summary>
    TextDocument.TextPosition Find(TextDocument.TextPosition textPosition, TextMatcher matcher, bool searchBackward, bool wrapAround, FindSelectMode findSelectMode, TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd);

    /// <summary>Replaces the text between two positions.</summary>
    void ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text);

    /// <summary>Replaces every match (in the range when one is given) in one undo step; returns how many there were.</summary>
    int ReplaceAll(TextMatcher matcher, string replacement, TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd, out TextDocument.TextPosition newScopeEnd);

    /// <summary>Marks the matches of <paramref name="matcher"/> (null: none).</summary>
    void SetMatchHighlight(TextMatcher matcher);

    /// <summary>Adds a highlighted range and returns its number.</summary>
    int AddHighlight(TextDocument.TextPosition start, TextDocument.TextPosition end, HighlightStyle highlightStyle);

    /// <summary>Moves or restyles a highlighted range; returns its number.</summary>
    int SetHighlight(int highlightIndex, TextDocument.TextPosition start, TextDocument.TextPosition end, HighlightStyle highlightStyle);

    /// <summary>Removes a highlighted range.</summary>
    void RemoveHighlight(int highlightIndex);

    /// <summary>Raised when the text is edited.</summary>
    event TextRangeChangedHandler RangeChanged;
}
