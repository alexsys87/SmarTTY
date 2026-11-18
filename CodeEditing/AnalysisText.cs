using SmarTTY.TextEditing;
using System;
using System.Text;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The text the completion and the parameter hint of the editor work on. A document up to <see cref="BudgetChars"/> is given whole; a larger
/// one as the lines around the caret (the words and the functions of the rest are then not offered).
/// </summary>
public static class AnalysisText
{
    /// <summary>
    /// The most text the completion list and the parameter hint read at a time. They went through a copy of the whole
    /// document on every trigger and typed bracket, which for a file of tens of megabytes (the editor opens up to 64 MB) took
    /// seconds each time and used up the address space of a 32-bit process.
    /// </summary>
    public const int BudgetChars = 500000;

    /// <summary>The text for the analysis, and where the caret is in it.</summary>
    public static string Get(TextDocument document, int caretLine, int caretColumn, out int analysisCaretLine, out int analysisCaretColumn)
    {
        analysisCaretLine = caretLine;
        analysisCaretColumn = caretColumn;
        int count = document.LineCount;
        long total = 0;
        for (int i = 0; i < count && total <= BudgetChars; i++)
        {
            total += document.GetLineLengthWithTerminator(i);
        }
        if (total <= BudgetChars)
        {
            return document.GetText();
        }

        int line = Math.Max(0, Math.Min(caretLine, count - 1));
        string lineText = document.GetLineText(line);
        if (lineText.Length > BudgetChars)
        {
            // One huge line (a minified file): the part of it around the caret.
            int start = Math.Max(0, Math.Min(caretColumn - BudgetChars / 2, lineText.Length - BudgetChars));
            analysisCaretLine = 0;
            analysisCaretColumn = caretColumn - start;
            return lineText.Substring(start, BudgetChars);
        }
        int first = line;
        int last = line;
        long size = lineText.Length;
        while (size < BudgetChars && (first > 0 || last < count - 1))
        {
            if (first > 0 && (last >= count - 1 || line - first <= last - line))
            {
                first--;
                size += document.GetLineLengthWithTerminator(first);
            }
            else
            {
                last++;
                size += document.GetLineLengthWithTerminator(last);
            }
        }
        StringBuilder window = new StringBuilder((int)Math.Min(size + 16, int.MaxValue));
        for (int i = first; i <= last; i++)
        {
            window.Append(document.GetLineText(i));
            if (i < last)
            {
                window.Append('\n');
            }
        }
        analysisCaretLine = line - first;
        return window.ToString();
    }
}
