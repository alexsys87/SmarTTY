using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

/// <summary>A mark of a character that leaves no ink of its own, at the column it is drawn in.</summary>
public struct BlankMark
{
    public int Column;

    public string Marker;
}

/// <summary>
/// The marks a text view draws when it shows all characters: a middle dot for a space, an arrow for a tab, a pilcrow for a line break, a control
/// picture for a control character, a ring for a no-break space, a replacement mark for an invisible format character. Moved as it was from
/// <c>ReadonlyTextBrowser.GetInvisibleCharacterMarker</c> and <c>PaintInvisibleCharacters</c> (stage 3E), so that the WPF text view draws the
/// same marks; the views draw them.
/// </summary>
public static class BlankMarks
{
    /// <summary>The mark of a character; null for a character that shows itself.</summary>
    public static string Marker(char ch)
    {
        if (ch == ' ')
        {
            return "·";
        }
        if (ch == '\u00A0')
        {
            // A no-break space looks like a space but is another character; show it as a ring.
            return "°";
        }
        if (char.IsControl(ch))
        {
            if (ch == '\u007F')
            {
                return "\u2421";
            }
            if (ch < '\u0020')
            {
                return ((char)(0x2400 + ch)).ToString();
            }
            // C1 control characters have no control picture: a replacement mark still shows them.
            return "\uFFFD";
        }
        if (char.IsWhiteSpace(ch))
        {
            return "·";
        }
        if (char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format)
        {
            // Zero-width joiner, BOM, soft hyphen and the bidirectional marks: invisible but present.
            return "\uFFFD";
        }
        return null;
    }

    /// <summary>
    /// The marks of a line between the columns <paramref name="firstColumn"/> and <paramref name="lastColumn"/>, and the pilcrow after its text
    /// when the line has a line break (the last line of a text has none, even when the model gives it the ending of the line above).
    /// </summary>
    public static void LineMarks(string text, int tabSize, int firstColumn, int lastColumn, bool hasLineBreak, List<BlankMark> marks)
    {
        marks.Clear();
        int column = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            int nextColumn = (ch == '\t') ? (column + tabSize - column % tabSize) : (column + 1);
            if (column >= firstColumn && column <= lastColumn)
            {
                if (ch == '\t')
                {
                    marks.Add(new BlankMark { Column = column, Marker = "→" });
                }
                else
                {
                    string marker = Marker(ch);
                    if (marker != null)
                    {
                        marks.Add(new BlankMark { Column = column, Marker = marker });
                    }
                }
            }
            column = nextColumn;
            if (column > lastColumn)
            {
                // The rest of the line, including its line break, is off screen.
                return;
            }
        }
        if (column >= firstColumn && column <= lastColumn && hasLineBreak)
        {
            marks.Add(new BlankMark { Column = column, Marker = "¶" });
        }
    }
}
