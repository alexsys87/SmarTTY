using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;

namespace SmarTTY.Terminal.Rendering;

/// <summary>Cells next to each other that are painted with one background rectangle.</summary>
public struct BackgroundRun
{
    public int Start;

    public int Length;

    public RgbColor Color;
}

/// <summary>Cells next to each other whose text is drawn in one pass (one glyph run): the same colour and the same underline.</summary>
public struct TextRun
{
    public int Start;

    public int Length;

    public RgbColor Foreground;

    public bool Underline;
}

/// <summary>
/// What one row of the screen is painted with: the background rectangles and the text runs, and a signature of all of it (the text, the
/// colours, the underline) that tells a view whether a row it painted before can be reused as it is.
/// </summary>
public sealed class RowLayout
{
    public readonly List<BackgroundRun> Backgrounds = new List<BackgroundRun>();

    public readonly List<TextRun> Texts = new List<TextRun>();

    /// <summary>The text of the row that the runs cover (cut to the number of columns that are painted).</summary>
    public string Text = "";

    /// <summary>The characters of no width drawn over the cells of <see cref="Text"/> (<see cref="LineInfo.Marks"/>); null when there are none.</summary>
    public string[] Marks;

    /// <summary>A 64-bit hash of the text and the runs. Two rows with the same signature and the same <see cref="Text"/> look the same.</summary>
    public ulong Signature;

    public void Clear()
    {
        Backgrounds.Clear();
        Texts.Clear();
        Text = "";
        Marks = null;
        Signature = 0;
    }
}

/// <summary>
/// Turns a line of the terminal (its text and the style of every cell) into the runs it is painted with. Neighbouring cells of one colour
/// make one run, so a line of plain text is one glyph run and one coloured word is one more; a blank (without an underline) never splits a
/// run, because it draws nothing, and no run starts or ends with one. Background rectangles are merged the same way, across the text runs.
/// No text layout is done: every character takes one cell of the grid, a wide one two (itself and the filler of its second cell, which is in the
/// run with it, see <see cref="CellWidth"/>); a filler that does not follow a wide character is an empty cell, a blank. A cell with characters of
/// no width (<see cref="LineInfo.Marks"/>) is drawn with them, a blank one too.
/// </summary>
public static class RowRunBuilder
{
    private const ulong FnvOffset = 14695981039346656037UL;

    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Fills <paramref name="layout"/> for a line. <paramref name="line"/> may be null (no style: all default); a dimmed line is painted in
    /// <see cref="TerminalColorScheme.Inactive"/> without backgrounds. Only the first <paramref name="columns"/> cells are painted.
    /// </summary>
    public static void Build(string text, LineInfo line, TerminalColorScheme scheme, int columns, RowLayout layout)
    {
        layout.Clear();
        text ??= "";
        int length = text.Length < columns ? text.Length : columns;
        if (length < 0)
        {
            length = 0;
        }
        layout.Text = length == text.Length ? text : text.Substring(0, length);
        CellStyle[] cells = line?.Cells;
        string[] marks = line?.Marks;
        layout.Marks = marks != null && Array.Exists(marks, mark => mark != null) ? marks : null;
        marks = layout.Marks;
        bool dimmed = line != null && line.IsDimmed;
        RgbColor defaultForeground = scheme.DefaultForeground;

        // The run being built (Length 0: none) and the last cell that is not a blank in it.
        TextRun text_ = default;
        int textEnd = -1;
        BackgroundRun background = default;
        bool haveBackground = false;

        CellStyle previousStyle = default;
        ResolvedCellColors colors = default;
        bool haveColors = false;
        for (int column = 0; column < length; column++)
        {
            if (dimmed)
            {
                colors = new ResolvedCellColors { Foreground = scheme.Inactive };
            }
            else if (cells == null || column >= cells.Length)
            {
                colors = default;
            }
            else if (!haveColors || !previousStyle.StyleEquals(cells[column]))
            {
                previousStyle = cells[column];
                colors = scheme.Resolve(previousStyle);
                haveColors = true;
            }

            // Backgrounds.
            if (colors.Background.HasValue)
            {
                RgbColor color = colors.Background.Value;
                if (haveBackground && background.Color == color && background.Start + background.Length == column)
                {
                    background.Length++;
                }
                else
                {
                    if (haveBackground)
                    {
                        layout.Backgrounds.Add(background);
                    }
                    background = new BackgroundRun { Start = column, Length = 1, Color = color };
                    haveBackground = true;
                }
            }
            else if (haveBackground)
            {
                layout.Backgrounds.Add(background);
                haveBackground = false;
            }

            // Text.
            char ch = text[column];
            bool blank = (ch == ' ' || ch == '\0' || (ch == CellWidth.Filler && !CellWidth.IsSecondCell(text, column)))
                && (marks == null || column >= marks.Length || marks[column] == null);
            if (blank && !colors.Underline)
            {
                // A blank draws nothing: it does not end a run without an underline, but it is not under the line of an underlined one.
                if (text_.Length != 0 && text_.Underline)
                {
                    FlushText(layout, ref text_, textEnd);
                }
                continue;
            }
            RgbColor foreground = colors.Foreground ?? defaultForeground;
            if (text_.Length != 0 && text_.Foreground == foreground && text_.Underline == colors.Underline && (colors.Underline ? textEnd == column - 1 : true))
            {
                textEnd = column;
                text_.Length = column - text_.Start + 1;
            }
            else
            {
                FlushText(layout, ref text_, textEnd);
                text_ = new TextRun { Start = column, Length = 1, Foreground = foreground, Underline = colors.Underline };
                textEnd = column;
            }
        }
        FlushText(layout, ref text_, textEnd);
        if (haveBackground)
        {
            layout.Backgrounds.Add(background);
        }
        layout.Signature = ComputeSignature(layout, dimmed);
    }

    private static void FlushText(RowLayout layout, ref TextRun run, int lastColumn)
    {
        if (run.Length == 0)
        {
            return;
        }
        // The blanks after the last character are not part of the run (an underlined run has no blanks it does not own).
        run.Length = lastColumn - run.Start + 1;
        layout.Texts.Add(run);
        run = default;
    }

    private static ulong Mix(ulong hash, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            hash ^= (value >> shift) & 0xFF;
            hash *= FnvPrime;
        }
        return hash;
    }

    private static ulong ComputeSignature(RowLayout layout, bool dimmed)
    {
        ulong hash = FnvOffset;
        string text = layout.Text;
        for (int index = 0; index < text.Length; index++)
        {
            hash = Mix(hash, text[index]);
        }
        hash = Mix(hash, (uint)text.Length | (dimmed ? 0x80000000u : 0u));
        string[] marks = layout.Marks;
        for (int column = 0; marks != null && column < text.Length && column < marks.Length; column++)
        {
            string mark = marks[column];
            if (mark != null)
            {
                hash = Mix(hash, (uint)column | 0x40000000u);
                foreach (char ch in mark)
                {
                    hash = Mix(hash, ch);
                }
            }
        }
        foreach (BackgroundRun run in layout.Backgrounds)
        {
            hash = Mix(hash, (uint)run.Start);
            hash = Mix(hash, (uint)run.Length);
            hash = Mix(hash, run.Color.Argb);
        }
        hash = Mix(hash, 0xB0B0B0B0u);
        foreach (TextRun run in layout.Texts)
        {
            hash = Mix(hash, (uint)run.Start);
            hash = Mix(hash, (uint)run.Length | (run.Underline ? 0x80000000u : 0u));
            hash = Mix(hash, run.Foreground.Argb);
        }
        return hash;
    }
}
