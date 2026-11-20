using SmarTTY.TextEditing;

namespace SmarTTY.CodeEditing;

/// <summary>
/// What the code editor decides about the colours of the syntax and the lines an edit changed: which lines the token cache, the inline messages and
/// the folds are told about after a range of the text changed (a line break put in at the start of a line, or an empty line taken out, counts from
/// the line before, so that a message or a fold goes with the text of its line), and which of the colours of the token types a token is drawn
/// with. It is what <c>Controls/CodeEditorPP</c> did in <c>OnRangeChanged</c> and <c>GetLineColorSpans</c>, moved here as it was (stage 3D) so
/// that a text view of another toolkit colours the text with the same rules.
/// </summary>
public static class SyntaxColoring
{
    /// <summary>
    /// The lines an edit of the range from <paramref name="start"/> to <paramref name="oldEnd"/>, which now ends at <paramref name="newEnd"/>,
    /// changed: the first one, how many lines it took and how many it put in place of them.
    /// </summary>
    public static void ChangedLines(TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd, out int changeStartLine, out int removedLineCount, out int addedLineCount)
    {
        changeStartLine = start.Line;
        removedLineCount = oldEnd.Line - start.Line;
        addedLineCount = newEnd.Line - start.Line;
        if (start.Column == 0 && oldEnd == start && newEnd.Line == start.Line + 1 && newEnd.Column == 0 && changeStartLine > 0)
        {
            changeStartLine--;
        }
        if (start.Column == 0 && newEnd == start && oldEnd.Line == start.Line + 1 && oldEnd.Column == 0 && changeStartLine > 0)
        {
            changeStartLine--;
        }
    }

    /// <summary>
    /// Which of <paramref name="colorCount"/> colours (one a token type, the first for the plain text) a token of <paramref name="tokenType"/> is
    /// drawn with: its own, the first for a type past them, or -1 (the colour of the text) when there are none.
    /// </summary>
    public static int ColorIndex(int tokenType, int colorCount)
    {
        if (colorCount == 0)
        {
            return -1;
        }
        return (tokenType >= colorCount) ? 0 : tokenType;
    }
}
