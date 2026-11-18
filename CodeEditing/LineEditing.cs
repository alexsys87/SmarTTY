using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Text;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The part of the line commands of the editor (duplicate, move, delete, comment, the Text menu) that needs no control: which lines a
/// command works on, the text that replaces them, the smallest replacements that turn old lines into new ones, and where a column
/// goes after such a replacement. The editor reads the lines from its document, asks for the answers here and makes the edits.
///
/// The class is free of any UI dependency, so the test harness can check it without a display.
/// </summary>
public static class LineEditing
{
    /// <summary>One replacement of a range of the document by a text.</summary>
    public struct TextEdit
    {
        /// <summary>The start of the replaced range.</summary>
        public TextDocument.TextPosition Start;

        /// <summary>The end of the replaced range.</summary>
        public TextDocument.TextPosition End;

        /// <summary>The text that takes its place.</summary>
        public string Text;
    }

    /// <summary>
    /// The first and the last line a line command works on: the lines of the selection, or the line of the caret. A selection that ends at
    /// the very start of a line does not include that line.
    /// </summary>
    public static void GetCommandLines(int caretLine, bool hasSelection, TextDocument.TextPosition selectionStart, TextDocument.TextPosition caret, out int first, out int last)
    {
        first = caretLine;
        last = first;
        if (!hasSelection)
        {
            return;
        }
        TextDocument.TextPosition start = selectionStart;
        TextDocument.TextPosition end = caret;
        if (end < start)
        {
            TextDocument.TextPosition swap = start;
            start = end;
            end = swap;
        }
        first = start.Line;
        last = end.Line;
        // A selection that ends at the very start of a line does not include that line.
        if (end.Column == 0 && last > first)
        {
            last--;
        }
    }

    /// <summary>The selection covers two lines or more (what sorting and removing duplicates work on).</summary>
    public static bool HasLineSelection(int caretLine, bool hasSelection, TextDocument.TextPosition selectionStart, TextDocument.TextPosition caret)
    {
        if (!hasSelection)
        {
            return false;
        }
        GetCommandLines(caretLine, hasSelection, selectionStart, caret, out int first, out int last);
        return last > first;
    }

    /// <summary>
    /// The lines a Text menu command (<see cref="LineTransforms"/>) works on: the selected lines, or the whole text when nothing is selected;
    /// the commands that need lines to be selected work on a selection of two lines or more. False when lines were needed and not selected.
    /// </summary>
    public static bool TryGetTransformLines(LineTransformKind kind, int lineCount, int caretLine, bool hasSelection, TextDocument.TextPosition selectionStart, TextDocument.TextPosition caret, out int first, out int last)
    {
        if (LineTransforms.NeedsSelection(kind))
        {
            if (!HasLineSelection(caretLine, hasSelection, selectionStart, caret))
            {
                first = 0;
                last = 0;
                return false;
            }
            GetCommandLines(caretLine, hasSelection, selectionStart, caret, out first, out last);
        }
        else if (hasSelection)
        {
            GetCommandLines(caretLine, hasSelection, selectionStart, caret, out first, out last);
        }
        else
        {
            first = 0;
            last = lineCount - 1;
        }
        return true;
    }

    /// <summary>
    /// The smallest replacements that turn <paramref name="oldLines"/> (which start at line <paramref name="first"/>) into
    /// <paramref name="newLines"/>, of the same count: for each line that differs, the part between its common beginning and its common end.
    /// </summary>
    public static List<TextEdit> DiffLines(int first, string[] oldLines, string[] newLines)
    {
        List<TextEdit> edits = new List<TextEdit>();
        for (int i = 0; i < oldLines.Length; i++)
        {
            if (oldLines[i] == newLines[i])
            {
                continue;
            }
            // Only the part that differs is replaced.
            string before = oldLines[i];
            string after = newLines[i];
            int prefix = CommonPrefixLength(before, after);
            int suffix = CommonSuffixLength(before, after, prefix);
            edits.Add(new TextEdit
            {
                Start = new TextDocument.TextPosition(prefix, first + i),
                End = new TextDocument.TextPosition(before.Length - suffix, first + i),
                Text = after.Substring(prefix, after.Length - suffix - prefix)
            });
        }
        return edits;
    }

    /// <summary>
    /// Where a column of the caret or of the selection goes after <see cref="DiffLines"/> was applied: left where it was in front of the
    /// change, moved by the change in length behind it.
    /// </summary>
    public static int ShiftColumn(TextDocument.TextPosition position, int first, string[] oldLines, string[] newLines)
    {
        int index = position.Line - first;
        if (index < 0 || index >= oldLines.Length || oldLines[index] == newLines[index])
        {
            return position.Column;
        }
        int changeStart = CommonPrefixLength(oldLines[index], newLines[index]);
        if (position.Column <= changeStart)
        {
            return position.Column;
        }
        int delta = newLines[index].Length - oldLines[index].Length;
        return Math.Max(changeStart, Math.Min(position.Column + delta, newLines[index].Length));
    }

    /// <summary>How many characters two texts have in common at their beginning.</summary>
    public static int CommonPrefixLength(string a, string b)
    {
        int length = 0;
        while (length < a.Length && length < b.Length && a[length] == b[length])
        {
            length++;
        }
        return length;
    }

    /// <summary>How many characters two texts have in common at their end, not counting the first <paramref name="prefix"/> characters.</summary>
    public static int CommonSuffixLength(string a, string b, int prefix)
    {
        int length = 0;
        while (length < a.Length - prefix && length < b.Length - prefix && a[a.Length - 1 - length] == b[b.Length - 1 - length])
        {
            length++;
        }
        return length;
    }

    /// <summary>The text to insert at the end of the last of <paramref name="lines"/> to duplicate them: each one with a line break in front.</summary>
    public static string DuplicatedText(string[] lines, string lineEnd)
    {
        StringBuilder copy = new StringBuilder();
        foreach (string line in lines)
        {
            copy.Append(lineEnd).Append(line);
        }
        return copy.ToString();
    }

    /// <summary>
    /// The text of a block of lines and the line it changes places with, in the new order: the block above the neighbour when it moves up
    /// (<paramref name="direction"/> is negative), under it when it moves down.
    /// </summary>
    public static string MovedText(string[] block, string neighbor, string lineEnd, int direction)
    {
        StringBuilder text = new StringBuilder();
        if (direction < 0)
        {
            AppendLines(text, block, lineEnd);
            text.Append(lineEnd).Append(neighbor);
        }
        else
        {
            text.Append(neighbor).Append(lineEnd);
            AppendLines(text, block, lineEnd);
        }
        return text.ToString();
    }

    private static void AppendLines(StringBuilder text, string[] lines, string lineEnd)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                text.Append(lineEnd);
            }
            text.Append(lines[i]);
        }
    }
}
