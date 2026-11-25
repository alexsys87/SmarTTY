using System.Text;

namespace SmarTTY.Terminal.Emulation;

/// <summary>
/// How many cells of the screen a character takes, as the programs on the other side count them (<c>wcwidth</c>): the East Asian wide and
/// fullwidth characters of the Basic Multilingual Plane (CJK ideographs, kana, Hangul syllables, the fullwidth forms, the emoji of that plane)
/// take two cells, everything else one. A character outside the plane is a surrogate pair, which takes two cells as two units of the text
/// (right for the wide ones, the emoji and the rare ideographs; a narrow one there takes a cell too many). Combining marks and other characters
/// of no width take a cell each, as before.
/// <para>A wide character is kept in the text of its line as itself and a <see cref="Filler"/> after it, so that a column of the screen stays an
/// index of the text (the styles, the selection, the cursor and the screen operations count cells that way). A filler that does not follow a
/// wide character is a cell left empty in front of a wide character that did not fit at the end of a row. Nothing that leaves the screen as text
/// (a copy, a search, a link) has fillers in it: see <see cref="Strip"/>.</para>
/// </summary>
public static class CellWidth
{
    /// <summary>The second cell of a wide character (a noncharacter of Unicode, which no text holds; one that arrives is shown as U+FFFD).</summary>
    public const char Filler = '\uFFFF';

    // The wide (W) and fullwidth (F) ranges of EastAsianWidth.txt of Unicode 15.1 below U+10000, first and last of each.
    private static readonly int[] WideRanges =
    {
        0x1100, 0x115F, 0x231A, 0x231B, 0x2329, 0x232A, 0x23E9, 0x23EC, 0x23F0, 0x23F0, 0x23F3, 0x23F3,
        0x25FD, 0x25FE, 0x2614, 0x2615, 0x2648, 0x2653, 0x267F, 0x267F, 0x2693, 0x2693, 0x26A1, 0x26A1,
        0x26AA, 0x26AB, 0x26BD, 0x26BE, 0x26C4, 0x26C5, 0x26CE, 0x26CE, 0x26D4, 0x26D4, 0x26EA, 0x26EA,
        0x26F2, 0x26F3, 0x26F5, 0x26F5, 0x26FA, 0x26FA, 0x26FD, 0x26FD, 0x2705, 0x2705, 0x270A, 0x270B,
        0x2728, 0x2728, 0x274C, 0x274C, 0x274E, 0x274E, 0x2753, 0x2755, 0x2757, 0x2757, 0x2795, 0x2797,
        0x27B0, 0x27B0, 0x27BF, 0x27BF, 0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55, 0x2E80, 0x2E99,
        0x2E9B, 0x2EF3, 0x2F00, 0x2FD5, 0x2FF0, 0x303E, 0x3041, 0x3096, 0x3099, 0x30FF, 0x3105, 0x312F,
        0x3131, 0x318E, 0x3190, 0x31E3, 0x31EF, 0x321E, 0x3220, 0x3247, 0x3250, 0x4DBF, 0x4E00, 0xA48C,
        0xA490, 0xA4C6, 0xA960, 0xA97C, 0xAC00, 0xD7A3, 0xF900, 0xFAFF, 0xFE10, 0xFE19, 0xFE30, 0xFE52,
        0xFE54, 0xFE66, 0xFE68, 0xFE6B, 0xFF01, 0xFF60, 0xFFE0, 0xFFE6,
    };

    /// <summary>The character takes two cells.</summary>
    public static bool IsWide(char ch)
    {
        if (ch < WideRanges[0])
        {
            return false;
        }
        int low = 0;
        int high = WideRanges.Length / 2 - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (ch < WideRanges[middle * 2])
            {
                high = middle - 1;
            }
            else if (ch > WideRanges[middle * 2 + 1])
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The cells of a text: a filler after every wide character; a filler that came with the text becomes U+FFFD.</summary>
    public static string ToCells(string text)
    {
        StringBuilder cells = null;
        for (int index = 0; index < text.Length; index++)
        {
            char ch = text[index];
            bool wide = IsWide(ch);
            if (cells == null)
            {
                if (!wide && ch != Filler)
                {
                    continue;
                }
                cells = new StringBuilder(text.Length + 8);
                cells.Append(text, 0, index);
            }
            if (ch == Filler)
            {
                cells.Append('\uFFFD');
                continue;
            }
            cells.Append(ch);
            if (wide)
            {
                cells.Append(Filler);
            }
        }
        return cells?.ToString() ?? text;
    }

    /// <summary>The cell at <paramref name="index"/> is the second cell of the wide character before it.</summary>
    public static bool IsSecondCell(string cells, int index)
    {
        return index > 0 && index < cells.Length && cells[index] == Filler && IsWide(cells[index - 1]);
    }

    /// <summary>The text of cells without the fillers.</summary>
    public static string Strip(string cells)
    {
        if (cells == null || cells.IndexOf(Filler) < 0)
        {
            return cells;
        }
        return cells.Replace(Filler.ToString(), "");
    }
}
