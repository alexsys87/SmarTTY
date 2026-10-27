using System;
using System.Collections.Generic;

namespace SmarTTY.Terminal;

/// <summary>How the text of the clipboard is pasted.</summary>
public enum PasteMode
{
    /// <summary>As it is (after the cleaning of the settings).</summary>
    Normal,
    /// <summary>One command line, the lines joined with a space.</summary>
    OneLineSpaces,
    /// <summary>One command line, the lines joined with &amp;&amp;.</summary>
    OneLineAnd,
    /// <summary>Smart Terminal: one command after another, the next one when the previous has ended.</summary>
    LineByLine,
    /// <summary>COM, TCP and Telnet: in small pieces with pauses, and waiting for the echo or the prompt.</summary>
    Slow
}

/// <summary>One piece of a paste that is sent in several steps (see <see cref="PasteModes.BuildSteps"/>).</summary>
public sealed class PasteStep
{
    public readonly string Text;

    /// <summary>True when the piece ends a line (it ends with the line break), so the pause after a line applies.</summary>
    public readonly bool EndsLine;

    public PasteStep(string text, bool endsLine)
    {
        Text = text;
        EndsLine = endsLine;
    }
}

/// <summary>Free of WinForms (compiled by the Mono test harness): cutting a paste into the pieces that are sent one by one.</summary>
public static class PasteModes
{
    /// <summary>
    /// Cuts <paramref name="text"/> (with the line break that will be sent already in it) into pieces of at most
    /// <paramref name="chunkSize"/> characters (0 or less: no limit). A piece never splits a surrogate pair or a line break
    /// of two characters. With <paramref name="splitLines"/> every line (with its line break) starts a piece of its own,
    /// so that a pause or a wait for the device can follow each line; without it the text is only cut by size and
    /// <see cref="PasteStep.EndsLine"/> tells which pieces happen to end with a line break.
    /// </summary>
    public static List<PasteStep> BuildSteps(string text, int chunkSize, string lineBreak, bool splitLines)
    {
        List<PasteStep> steps = new List<PasteStep>();
        if (string.IsNullOrEmpty(text))
        {
            return steps;
        }
        if (string.IsNullOrEmpty(lineBreak))
        {
            lineBreak = "\n";
        }
        int limit = chunkSize > 0 ? chunkSize : int.MaxValue;
        int position = 0;
        while (position < text.Length)
        {
            int end = text.Length;
            if (splitLines)
            {
                int found = text.IndexOf(lineBreak, position, StringComparison.Ordinal);
                if (found != -1)
                {
                    end = found + lineBreak.Length;
                }
            }
            // The size limit cuts a line (or the whole text) into pieces.
            while (position < end)
            {
                int pieceEnd = (end - position > limit) ? position + limit : end;
                pieceEnd = AvoidSplit(text, position, pieceEnd, end, lineBreak);
                string piece = text.Substring(position, pieceEnd - position);
                steps.Add(new PasteStep(piece, piece.EndsWith(lineBreak, StringComparison.Ordinal)));
                position = pieceEnd;
            }
        }
        return steps;
    }

    // Moves the end of a piece so that it does not fall inside a surrogate pair or inside the line break.
    private static int AvoidSplit(string text, int start, int end, int lineEnd, string lineBreak)
    {
        if (end >= lineEnd || end <= start)
        {
            return end;
        }
        if (char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
        {
            end = end - 1 > start ? end - 1 : end + 1;
        }
        else if (lineBreak.Length > 1)
        {
            // A line break of several characters is sent in one piece: cut in front of it, or after it when it is the
            // only thing in the piece.
            for (int offset = 1; offset < lineBreak.Length; offset++)
            {
                int breakStart = end - offset;
                if (breakStart >= start && breakStart + lineBreak.Length <= text.Length &&
                    string.CompareOrdinal(text, breakStart, lineBreak, 0, lineBreak.Length) == 0)
                {
                    return breakStart > start ? breakStart : breakStart + lineBreak.Length;
                }
            }
        }
        return end;
    }

    /// <summary>The number of characters of all the steps.</summary>
    public static int TotalLength(IList<PasteStep> steps)
    {
        int total = 0;
        foreach (PasteStep step in steps)
        {
            total += step.Text.Length;
        }
        return total;
    }
}
