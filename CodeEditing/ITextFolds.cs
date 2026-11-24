namespace SmarTTY.CodeEditing;

/// <summary>The folded blocks as the layout sees them: the lines a folded header hides after it, and the text the header shows instead of its own.</summary>
public interface ITextFolds
{
    /// <summary>How many lines after <paramref name="line"/> its folded block hides (0: the line is not a folded header).</summary>
    int GetHiddenLineCount(int line);

    /// <summary>
    /// The text a folded header shows instead of <paramref name="lineText"/> and where its label is in it (an index and a length); null when the line
    /// is not a folded header.
    /// </summary>
    string GetFoldedText(int line, string lineText, out int labelStart, out int labelLength);
}
