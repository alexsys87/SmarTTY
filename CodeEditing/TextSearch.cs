using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

/// <summary>The columns of a match of the highlight in a row: where it starts and where it ends in the row (the end brought into the row).</summary>
public struct MatchSpan
{
    public int StartColumn;

    public int EndColumn;
}

/// <summary>
/// The search of a text view: the next match from a position, line by line, in a range or in the whole text, with a wrap; the column the view
/// scrolls to so that a match shows; and the matches of the highlight in a row (a long line is searched only around the screen, and only the first
/// 500 matches are shown). Moved as it was from <c>ReadonlyTextBrowser.Find</c> and <c>PaintMatches</c> (stage 3E), so that the WPF text view
/// searches the same way; the views select the match, scroll and paint.
/// </summary>
public static class TextSearch
{
    // A line longer than this is searched for matches only where it is on the screen, with this margin on each side.
    private const int LongLineChars = 4096;

    private const int LongLineMargin = 2048;

    // A line of a minified file may hold thousands of matches; the first ones are enough to show.
    private const int MaxShownMatches = 500;

    /// <summary>
    /// Finds the next match of <paramref name="matcher"/> (plain text, whole word or regular expression), line by line.
    /// A valid <paramref name="scopeStart"/> and <paramref name="scopeEnd"/> limit the search to that range (the search
    /// wraps around inside it). False when there is none.
    /// </summary>
    public static bool TryFind(TextDocument document, TextDocument.TextPosition textPosition, TextMatcher matcher, bool searchBackward, bool wrapAround,
        TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd, out int matchLine, out int matchIndex, out int matchLength)
    {
        matchLine = -1;
        matchIndex = -1;
        matchLength = 0;
        if (document == null || document.LineCount == 0 || matcher == null)
        {
            return false;
        }
        bool scoped = scopeStart.IsValid && scopeEnd.IsValid;
        int firstLine = scoped ? Math.Max(0, scopeStart.Line) : 0;
        int lastLine = scoped ? Math.Min(scopeEnd.Line, document.LineCount - 1) : (document.LineCount - 1);
        int direction = ((!searchBackward) ? 1 : (-1));
        int startColumn = textPosition.Column;
        // A caret that is outside the lines to search (after a failed search it stands behind the last line): the search goes on from
        // the edge of the range, in its direction, instead of finding nothing without a wrap-around.
        if (textPosition.Line > lastLine && searchBackward)
        {
            textPosition.Line = lastLine;
            startColumn = -1;
        }
        else if (textPosition.Line < firstLine && !searchBackward)
        {
            textPosition.Line = firstLine;
            startColumn = -1;
        }
        for (int pass = 0; pass < 2; pass++)
        {
            for (int line = textPosition.Line; line >= firstLine && line <= lastLine; line += direction)
            {
                string text = document.GetLineText(line);
                if (text == null)
                {
                    continue;
                }
                // The part of the line that may hold a match: the whole line, or what the scope leaves of it.
                int lower = (scoped && line == firstLine) ? scopeStart.Column : 0;
                int upper = (scoped && line == lastLine) ? Math.Min(scopeEnd.Column, text.Length) : text.Length;
                int from;
                int to;
                if (searchBackward)
                {
                    from = lower;
                    to = (startColumn == -1) ? upper : Math.Min(startColumn, upper);
                }
                else
                {
                    from = (startColumn == -1) ? lower : Math.Max(startColumn, lower);
                    to = upper;
                }
                if (matcher.Match(text, from, to, searchBackward, allowEmpty: false, out matchIndex, out matchLength))
                {
                    matchLine = line;
                    return true;
                }
                startColumn = -1;
            }
            if (!((pass == 0) & wrapAround))
            {
                break;
            }
            textPosition.Line = (searchBackward ? lastLine : firstLine);
            startColumn = -1;
        }
        matchIndex = -1;
        matchLength = 0;
        return false;
    }

    /// <summary>
    /// The first column the view shows after a match was selected: the start of the line when the match ends in the first screen, the end of a
    /// line that ends within a screen after the match, the middle of the match when it is narrower than the screen, else its start.
    /// </summary>
    public static int ScrollColumnFor(int matchIndex, int matchLength, int textLength, int visibleColumns)
    {
        int matchEnd = matchIndex + matchLength;
        if (matchEnd <= visibleColumns)
        {
            return 0;
        }
        if (textLength - matchIndex < visibleColumns && textLength > visibleColumns)
        {
            return textLength - visibleColumns;
        }
        if (matchLength < visibleColumns)
        {
            int centerColumn = (matchIndex + matchEnd) / 2;
            return centerColumn - visibleColumns / 2;
        }
        return matchIndex;
    }

    /// <summary>
    /// The matches of the highlight in a row of <paramref name="line"/> that shows its columns from <paramref name="rowFirst"/> to before
    /// <paramref name="rowEnd"/> (<see cref="int.MaxValue"/> for a row that is not cut by a wrap); <paramref name="lastColumn"/> is the column
    /// up to which a long line is searched (the end of the row, or the right edge of the screen and one more). A match outside the row is left out.
    /// </summary>
    public static void RowMatches(TextDocument document, int line, TextMatcher matcher, int tabSize, int rowFirst, int rowEnd, int lastColumn, List<MatchSpan> spans)
    {
        spans.Clear();
        string text = document.GetLineText(line);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        // The part of the line to look at: all of it, or, in a long line (a minified file), what is on the screen with a margin.
        // Looking at the whole of a line of megabytes on every paint, and counting its columns from its start for every match,
        // took seconds.
        int from = 0;
        int scanEnd = text.Length;
        if (text.Length > LongLineChars)
        {
            int firstIndex = document.ColumnToIndex(line, rowFirst, tabSize);
            int lastIndex = document.ColumnToIndex(line, lastColumn, tabSize);
            from = Math.Max(0, firstIndex - LongLineMargin);
            scanEnd = Math.Min(text.Length, lastIndex + LongLineMargin);
        }
        // The column of an index, counted on from the last one asked for (the matches come in order).
        int countedIndex = from;
        int countedColumn = (from == 0) ? 0 : document.IndexToColumn(line, from, tabSize);
        for (int shown = 0; shown < MaxShownMatches && from <= scanEnd && matcher.Match(text, from, scanEnd, backward: false, allowEmpty: false, out int index, out int length); shown++)
        {
            int matchStart = AdvanceColumn(text, tabSize, ref countedIndex, ref countedColumn, index);
            int matchEnd = AdvanceColumn(text, tabSize, ref countedIndex, ref countedColumn, index + length);
            if (!(matchStart >= rowEnd || matchEnd <= rowFirst))
            {
                spans.Add(new MatchSpan { StartColumn = matchStart, EndColumn = Math.Min(matchEnd, rowEnd) });
            }
            from = index + length;
        }
    }

    /// <summary>The column of <paramref name="target"/>, going on from <paramref name="index"/> and <paramref name="column"/> (as IndexToColumn counts: a tab goes to the next tab stop).</summary>
    private static int AdvanceColumn(string text, int tabSize, ref int index, ref int column, int target)
    {
        while (index < target && index < text.Length)
        {
            column = (text[index] != '\t') ? (column + 1) : (column + (tabSize - column % tabSize));
            index++;
        }
        return column;
    }
}
