using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SmarTTY.CodeEditing;

/// <summary>The text commands of the editor that rewrite whole lines (the Text menu of the context menu).</summary>
public enum LineTransformKind
{
    TrimTrailingWhitespace,
    SortAscending,
    SortDescending,
    RemoveDuplicateLines,
    IndentationToSpaces,
    IndentationToTabs,
    EnsureFinalNewline
}

/// <summary>
/// Rewrites lines of text for the Text menu of the editor: trailing blanks, order, duplicates, and the blanks that indent a
/// line. Each function takes the lines (without their line breaks) and returns the new ones, as <see cref="CommentStyle"/> does
/// for comments, so the editor can replace them in one undo step.
///
/// The class is free of any UI dependency, so the test harness can check it without a display.
/// </summary>
public static class LineTransforms
{
    public const int MinTabSize = 1;

    public const int MaxTabSize = 16;

    /// <summary>The text of the menu item.</summary>
    public static string Title(LineTransformKind kind)
    {
        switch (kind)
        {
            case LineTransformKind.TrimTrailingWhitespace:
                return "Trim Trailing Whitespace";
            case LineTransformKind.SortAscending:
                return "Sort Lines Ascending";
            case LineTransformKind.SortDescending:
                return "Sort Lines Descending";
            case LineTransformKind.RemoveDuplicateLines:
                return "Remove Duplicate Lines";
            case LineTransformKind.IndentationToSpaces:
                return "Indentation: Tabs to Spaces";
            case LineTransformKind.IndentationToTabs:
                return "Indentation: Spaces to Tabs";
            case LineTransformKind.EnsureFinalNewline:
                return "Ensure Final Newline";
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>
    /// The transformation needs lines that were selected: sorting or de-duplicating a whole file by accident would break it. The
    /// others work on the selected lines, or on the whole text when nothing is selected.
    /// </summary>
    public static bool NeedsSelection(LineTransformKind kind)
    {
        return kind == LineTransformKind.SortAscending || kind == LineTransformKind.SortDescending || kind == LineTransformKind.RemoveDuplicateLines;
    }

    /// <summary>
    /// The lines after the transformation. <see cref="LineTransformKind.EnsureFinalNewline"/> is about the end of the text and
    /// is done by the editor; here it leaves the lines as they are.
    /// </summary>
    public static string[] Apply(LineTransformKind kind, string[] lines, int tabSize)
    {
        if (lines == null)
        {
            return new string[0];
        }
        tabSize = Math.Max(MinTabSize, Math.Min(MaxTabSize, tabSize));
        switch (kind)
        {
            case LineTransformKind.TrimTrailingWhitespace:
                return TrimTrailingWhitespace(lines);
            case LineTransformKind.SortAscending:
                return Sort(lines, descending: false);
            case LineTransformKind.SortDescending:
                return Sort(lines, descending: true);
            case LineTransformKind.RemoveDuplicateLines:
                return RemoveDuplicates(lines);
            case LineTransformKind.IndentationToSpaces:
                return lines.Select(line => IndentationToSpaces(line, tabSize)).ToArray();
            case LineTransformKind.IndentationToTabs:
                return lines.Select(line => IndentationToTabs(line, tabSize)).ToArray();
            default:
                return (string[])lines.Clone();
        }
    }

    /// <summary>Removes the spaces and tabs at the end of each line.</summary>
    public static string[] TrimTrailingWhitespace(string[] lines)
    {
        return lines.Select(line => line.TrimEnd(' ', '\t')).ToArray();
    }

    /// <summary>
    /// Sorts the lines without regard to case (a and A are neighbours); lines that differ only in case stay in a fixed order,
    /// upper case first, and equal lines keep the order they had.
    /// </summary>
    public static string[] Sort(string[] lines, bool descending)
    {
        IComparer<string> comparer = Comparer<string>.Create((a, b) =>
        {
            int result = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            if (result == 0)
            {
                result = string.CompareOrdinal(a, b);
            }
            return descending ? -result : result;
        });
        // OrderBy is stable.
        return lines.OrderBy(line => line, comparer).ToArray();
    }

    /// <summary>Keeps the first of each group of identical lines (the case matters; empty lines count too).</summary>
    public static string[] RemoveDuplicates(string[] lines)
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        List<string> result = new List<string>(lines.Length);
        foreach (string line in lines)
        {
            if (seen.Add(line))
            {
                result.Add(line);
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// The blanks in front of the text, as spaces: a tab goes to the next multiple of <paramref name="tabSize"/>, as the editor
    /// shows it. Blanks inside the line are not touched.
    /// </summary>
    public static string IndentationToSpaces(string line, int tabSize)
    {
        int end = LeadingBlankLength(line);
        if (end == 0 || line.IndexOf('\t', 0, end) < 0)
        {
            return line;
        }
        return new string(' ', IndentWidth(line, end, tabSize)) + line.Substring(end);
    }

    /// <summary>The blanks in front of the text, as tabs for each <paramref name="tabSize"/> columns and spaces for the rest.</summary>
    public static string IndentationToTabs(string line, int tabSize)
    {
        int end = LeadingBlankLength(line);
        if (end == 0)
        {
            return line;
        }
        int width = IndentWidth(line, end, tabSize);
        string indent = new string('\t', width / tabSize) + new string(' ', width % tabSize);
        return indent + line.Substring(end);
    }

    private static int LeadingBlankLength(string line)
    {
        int end = 0;
        while (end < line.Length && (line[end] == ' ' || line[end] == '\t'))
        {
            end++;
        }
        return end;
    }

    private static int IndentWidth(string line, int end, int tabSize)
    {
        int width = 0;
        for (int i = 0; i < end; i++)
        {
            width = line[i] == '\t' ? (width / tabSize + 1) * tabSize : width + 1;
        }
        return width;
    }
}
