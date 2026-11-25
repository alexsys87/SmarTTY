using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;
using System;
using System.Text;

namespace SmarTTY.Terminal.Rendering;

/// <summary>
/// The search of the search bar in the document of a terminal: the loop of <c>ReadonlyTextBrowser.Find</c> (line by line, forward or
/// back, inside a scope or the whole document, with a wrap-around), without what the WinForms view did to its caret and scroll position;
/// the WPF terminal selects the match itself. A wide character is one character of the text that is searched (the second of its cells is not
/// in it, see <see cref="CellWidth"/>), and the places and lengths it gives are cells.
/// </summary>
public static class DocumentSearch
{
    /// <summary>Where the next match begins, and its length; an invalid position when there is none.</summary>
    public static TextDocument.TextPosition Find(TextDocument document, TextDocument.TextPosition textPosition, TextMatcher matcher, bool searchBackward, bool wrapAround,
        TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd, out int matchLength)
    {
        matchLength = 0;
        if (document == null || document.LineCount == 0 || matcher == null)
        {
            return new TextDocument.TextPosition(-1, -1);
        }
        bool scoped = scopeStart.IsValid && scopeEnd.IsValid;
        int firstLine = scoped ? Math.Max(0, scopeStart.Line) : 0;
        int lastLine = scoped ? Math.Min(scopeEnd.Line, document.LineCount - 1) : (document.LineCount - 1);
        int direction = searchBackward ? -1 : 1;
        int startColumn = textPosition.Column;
        // A caret outside the lines to search: the search goes on from the edge of the range, in its direction.
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
                string cells = document.GetLineText(line);
                if (cells == null)
                {
                    continue;
                }
                string text = SearchText(cells, out int[] cellOf);
                int lower = (scoped && line == firstLine) ? scopeStart.Column : 0;
                int upper = (scoped && line == lastLine) ? Math.Min(scopeEnd.Column, cells.Length) : cells.Length;
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
                if (from <= to && matcher.Match(text, IndexAt(cellOf, text.Length, from), IndexAt(cellOf, text.Length, to), searchBackward, allowEmpty: false, out int matchIndex, out int length))
                {
                    int start = CellOf(cellOf, matchIndex);
                    matchLength = CellOf(cellOf, matchIndex + length) - start;
                    return new TextDocument.TextPosition(start, line);
                }
                startColumn = -1;
            }
            if (!((pass == 0) & wrapAround))
            {
                break;
            }
            textPosition.Line = searchBackward ? lastLine : firstLine;
            startColumn = -1;
        }
        return new TextDocument.TextPosition(-1, -1);
    }

    /// <summary>Every match in a line, one after another (the highlight of all the matches): its first cell and the cells it takes.</summary>
    public static void ForEachMatch(string cells, TextMatcher matcher, Action<int, int> match)
    {
        string text = SearchText(cells ?? "", out int[] cellOf);
        int from = 0;
        while (from < text.Length && matcher.Match(text, from, text.Length, backward: false, allowEmpty: false, out int index, out int length))
        {
            int start = CellOf(cellOf, index);
            match(start, CellOf(cellOf, index + length) - start);
            from = index + Math.Max(length, 1);
        }
    }

    // The text of a line as it is searched, without the fillers of the wide characters; cellOf gives the cell of every character of it, and
    // the length of the line after the last one (null: the text is the line itself).
    private static string SearchText(string cells, out int[] cellOf)
    {
        cellOf = null;
        if (cells.IndexOf(CellWidth.Filler) < 0)
        {
            return cells;
        }
        StringBuilder text = new StringBuilder(cells.Length);
        cellOf = new int[cells.Length + 1];
        for (int column = 0; column < cells.Length; column++)
        {
            if (cells[column] != CellWidth.Filler)
            {
                cellOf[text.Length] = column;
                text.Append(cells[column]);
            }
        }
        cellOf[text.Length] = cells.Length;
        return text.ToString();
    }

    // The index of the first character of the searched text at or after a cell.
    private static int IndexAt(int[] cellOf, int length, int column)
    {
        if (cellOf == null)
        {
            return column;
        }
        int index = 0;
        while (index < length && cellOf[index] < column)
        {
            index++;
        }
        return index;
    }

    private static int CellOf(int[] cellOf, int index)
    {
        return cellOf == null ? index : cellOf[index];
    }
}
