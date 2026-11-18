using System;

namespace SmarTTY.Terminal.Emulation;

public class LineInfo
{
    public CellStyle[] Cells;

    public bool IsDimmed;

    public bool IsWrapped;

    public DateTime Timestamp;
}
