using SmarTTY.TextEditing;
using System;

namespace SmarTTY.Terminal.Rendering;

/// <summary>
/// The search of the search bar in the document of a terminal: the loop of <c>ReadonlyTextBrowser.Find</c> (line by line, forward or
/// back, inside a scope or the whole document, with a wrap-around), without what the WinForms view did to its caret and scroll position;
/// the WPF terminal selects the match itself.
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
                string text = document.GetLineText(line);
                if (text == null)
                {
                    continue;
                }
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
                if (from <= to && matcher.Match(text, from, to, searchBackward, allowEmpty: false, out int matchIndex, out int length))
                {
                    matchLength = length;
                    return new TextDocument.TextPosition(matchIndex, line);
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
}
