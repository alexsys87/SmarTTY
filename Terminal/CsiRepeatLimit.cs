using System;

namespace SmarTTY.Terminal;

/// <summary>
/// The count of a CSI command (the number in ESC [ Pn L, M, S, T, @, b, I, Z and the cursor movements) comes from the output of the
/// remote side and can be anything up to int.MaxValue. Used as it was, "ESC [ 2000000000 L" asked for a string of two billion line
/// breaks (an OutOfMemoryException, or gigabytes and a frozen window for a few hundred million), "ESC [ 2000000000 I" looped two billion
/// times on the UI thread, and a cursor movement by int.MaxValue wrapped around and put the cursor at the left edge. A count beyond what
/// the screen can show does what a count of the whole screen does, so it is cut to that, as xterm does.
/// </summary>
public static class CsiRepeatLimit
{
    /// <summary>The most UTF-8 bytes of one character: in byte mode a cursor movement counts bytes, not columns.</summary>
    private const int MaxBytesPerColumn = 4;

    /// <summary>No count is larger than this, whatever the size of the window (a string of that many characters is two megabytes).</summary>
    public const int MaxCount = 1 << 20;

    /// <summary>
    /// The count to use for <paramref name="command"/>: at least 1, and no more than the screen can use. Commands that are not here
    /// (G, `, d: a position that the cursor clamps; P, X: bounded by the line; the others take no count) are returned as they are.
    /// </summary>
    public static int Clamp(char command, int count, int rows, int columns)
    {
        long limit;
        long rowCount = Math.Max(rows, 1);
        long columnCount = Math.Max(columns, 1);
        switch (command)
        {
            case 'A':
            case 'B':
            case 'E':
            case 'F':
            case 'L':
            case 'M':
            case 'S':
            case 'T':
                limit = rowCount;
                break;
            case '@':
            case 'I':
            case 'Z':
                limit = columnCount;
                break;
            case 'C':
            case 'D':
                limit = columnCount * MaxBytesPerColumn;
                break;
            case 'b':
                limit = rowCount * columnCount;
                break;
            default:
                return count;
        }
        return (int)Math.Max(1, Math.Min(count, Math.Min(limit, MaxCount)));
    }
}
