using System.Text;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The text decisions of the indent commands and of the join of two lines of the text view: the indentation inserted for some levels, the width an
/// unindent removes from a line, and how much of the next line's indentation a join drops. It is the part of <c>IndentCommand</c> and of
/// <c>ReplaceTextCore</c> of <c>Controls/EditableTextBrowser</c> that reads only the text, moved here as it was (stage 3C).
/// </summary>
public static class IndentEdits
{
    /// <summary>One indentation level: a tab, or <paramref name="tabSize"/> spaces (at least one) when <paramref name="insertSpaces"/>.</summary>
    public static string IndentUnit(bool insertSpaces, int tabSize)
    {
        return insertSpaces ? new string(' ', System.Math.Max(1, tabSize)) : "\t";
    }

    /// <summary>The indentation of <paramref name="levels"/> levels (empty for none or fewer).</summary>
    public static string IndentText(string unit, int levels)
    {
        StringBuilder text = new StringBuilder();
        for (int i = 0; i < levels; i++)
        {
            text.Append(unit);
        }
        return text.ToString();
    }

    /// <summary>
    /// The width an unindent by <paramref name="levels"/> levels removes from the start of <paramref name="line"/>: a tab or up to
    /// <paramref name="tabSize"/> + 1 spaces a level, never more than the line.
    /// </summary>
    public static int UnindentWidth(string line, int levels, int tabSize)
    {
        string lineText = line + " \t";
        int indentWidth = 0;
        for (int level = 0; level < levels; level++)
        {
            if (lineText[indentWidth] == '\t')
            {
                indentWidth++;
                continue;
            }
            if (lineText[indentWidth] != ' ')
            {
                break;
            }
            for (int tabOffset = 0; tabOffset < tabSize; tabOffset++)
            {
                if (lineText[++indentWidth] != ' ')
                {
                    break;
                }
            }
        }
        // lineText carries 2 sentinel characters; never remove more than the real line
        // (a whitespace-only line such as a single tab must still be unindented).
        if (indentWidth > lineText.Length - 2)
        {
            indentWidth = lineText.Length - 2;
        }
        return indentWidth;
    }

    /// <summary>
    /// How many characters at the start of <paramref name="nextLine"/> a join after a line indented by <paramref name="indentation"/> drops: the
    /// start the two share.
    /// </summary>
    public static int SharedIndentLength(string indentation, string nextLine)
    {
        int commonPrefixLength;
        for (commonPrefixLength = 0; commonPrefixLength < System.Math.Min(nextLine.Length, indentation.Length) && nextLine[commonPrefixLength] == indentation[commonPrefixLength]; commonPrefixLength++)
        {
        }
        return commonPrefixLength;
    }
}
