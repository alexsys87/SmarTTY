using System;

namespace SmarTTY.Controls;

/// <summary>
/// Which line a row of the text view shows when providers add rows between the lines (the messages of
/// <see cref="CodeEditing.InlineMessageProvider"/>) or hide lines (folded blocks), and the other way round.
///
/// A line is followed by the rows its provider adds, and then by the lines it hides: the rows belong to the line that
/// stands first, before the hidden ones. The class is free of any UI dependency, so the test harness can check it.
/// </summary>
internal static class VirtualRowLayout
{
    /// <summary>
    /// The line that the row <paramref name="row"/> shows, counted from the top of the text. When the row is one that a provider
    /// added, the line it follows is returned and <paramref name="remainingRows"/> is how many of that line's added rows
    /// are left from this one on (the last one: 1); otherwise <paramref name="remainingRows"/> is -1.
    /// <paramref name="previousLine"/> is the line shown before.
    /// </summary>
    public static int RowToLine(ReadonlyTextBrowser.IVirtualLineProvider virtualLines, int lineCount, int row, out int remainingRows, out int previousLine)
    {
        int line = 0;
        int rowIndex = 0;
        int previousVisibleLine = 0;
        while (true)
        {
            if (rowIndex == row)
            {
                remainingRows = -1;
                previousLine = previousVisibleLine;
                return line;
            }
            if (rowIndex > row)
            {
                break;
            }
            previousVisibleLine = line;
            int nextSpecialLine = virtualLines.GetNextSpecialLine(Math.Min(line, 0));
            if (nextSpecialLine == -1)
            {
                nextSpecialLine = lineCount;
            }
            int lineSpan = nextSpecialLine - line;
            if (lineSpan > 0)
            {
                int step = Math.Min(lineSpan, row - rowIndex);
                line += step;
                rowIndex += step;
                previousVisibleLine = line - 1;
            }
            else
            {
                // The rows added under a line are asked for before the lines it hides are skipped: they belong to this line.
                rowIndex += virtualLines.GetVirtualRowCount(line);
                line += virtualLines.GetHiddenLineCount(line);
                line++;
                rowIndex++;
            }
        }
        remainingRows = rowIndex - row;
        previousLine = previousVisibleLine;
        return previousVisibleLine;
    }

    /// <summary>The row (counted from the top of the text) that shows <paramref name="line"/>.</summary>
    public static int LineToRowIndex(ReadonlyTextBrowser.IVirtualLineProvider virtualLines, int lineCount, int line)
    {
        int currentLine = 0;
        int rowIndex = 0;
        while (currentLine < line)
        {
            int nextSpecialLine = virtualLines.GetNextSpecialLine(currentLine);
            if (nextSpecialLine == -1)
            {
                nextSpecialLine = lineCount;
            }
            int lineSpan = nextSpecialLine - currentLine;
            if (lineSpan > 0)
            {
                int step = Math.Min(lineSpan, line - currentLine);
                currentLine += step;
                rowIndex += step;
            }
            else
            {
                rowIndex += virtualLines.GetVirtualRowCount(currentLine);
                currentLine += virtualLines.GetHiddenLineCount(currentLine);
                currentLine++;
                rowIndex++;
            }
        }
        return rowIndex;
    }
}
