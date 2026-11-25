using System;

namespace SmarTTY.Terminal.Emulation;

public class LineInfo
{
    public CellStyle[] Cells;

    /// <summary>
    /// The characters of no width that follow the character of each cell (combining marks, variation selectors, joiners, see
    /// <see cref="CellWidth.IsZeroWidth"/>); null for a cell without any, and for the whole line when none of its cells has one.
    /// </summary>
    public string[] Marks;

    public bool IsDimmed;

    public bool IsWrapped;

    public DateTime Timestamp;
}
