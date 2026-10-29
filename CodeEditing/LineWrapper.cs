using System;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing
{
    /// <summary>Splits a long line into display rows for the word wrap of the editor.</summary>
    public static class LineWrapper
    {
        /// <summary>
        /// The display columns where the rows after the first one start, or null when the line fits one row. A row breaks
        /// after the last space or tab that still fits; a word longer than the row breaks where the row ends.
        /// </summary>
        public static int[] ComputeBreaks(string text, int width, int tabSize)
        {
            width = Math.Max(width, 2);
            tabSize = Math.Max(tabSize, 1);
            if (text.Length <= width && text.IndexOf('\t') < 0)
            {
                return null;
            }
            List<int> breaks = null;
            int rowStart = 0;
            int column = 0;
            int afterSpace = -1;
            foreach (char ch in text)
            {
                int charWidth = (ch == '\t') ? (tabSize - column % tabSize) : 1;
                if (column + charWidth - rowStart > width && column > rowStart)
                {
                    int breakColumn = (afterSpace > rowStart) ? afterSpace : column;
                    if (breaks == null)
                    {
                        breaks = new List<int>();
                    }
                    breaks.Add(breakColumn);
                    rowStart = breakColumn;
                    afterSpace = -1;
                }
                column += charWidth;
                if (ch == ' ' || ch == '\t')
                {
                    afterSpace = column;
                }
            }
            return breaks?.ToArray();
        }
    }
}
