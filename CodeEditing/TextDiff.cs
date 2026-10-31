using System;
using System.Collections.Generic;
using System.Text;

namespace SmarTTY.CodeEditing;

/// <summary>
/// A line diff in the unified format, for the "Show Changes" window of the file editor: what the text in the editor
/// differs in from the version that was loaded from (or last saved to) the server.
///
/// The class is free of any UI dependency, so the test harness can check it without a display.
/// </summary>
public static class TextDiff
{
    public sealed class Result
    {
        /// <summary>The diff in the unified format (with the --- and +++ lines); empty when the texts are the same.</summary>
        public string UnifiedText = "";

        public int AddedLines;

        public int RemovedLines;

        /// <summary>Both texts have line breaks, and not of the same kind (CRLF against LF). The diff ignores the difference.</summary>
        public bool LineEndingsDiffer;

        /// <summary>The changes were too many to compare line by line: the changed middle of the text is shown as replaced.</summary>
        public bool Coarse;

        public bool IsIdentical => AddedLines == 0 && RemovedLines == 0;
    }

    // The longest edit script (added plus removed lines) that is searched for; the memory use grows with its square.
    private const int MaxEditDistance = 2000;

    // The marker that the last line of a text has no line break after it. It takes part in the comparison, so that
    // adding or removing the final line break shows up as a change, and it never reaches the output.
    private const char NoEolMarker = '\0';

    private enum OpKind
    {
        Equal,
        Delete,
        Insert
    }

    private struct Op
    {
        public OpKind Kind;

        public string Text;
    }

    /// <summary>
    /// Compares two texts line by line. Line breaks (CRLF, LF, CR) are not compared, only the lines; a missing final
    /// line break is. <paramref name="contextLines"/> lines around each change are kept in the output.
    /// </summary>
    public static Result Compare(string oldText, string newText, string oldLabel, string newLabel, int contextLines = 3)
    {
        oldText = oldText ?? "";
        newText = newText ?? "";
        if (contextLines < 0)
        {
            contextLines = 0;
        }
        Result result = new Result();
        int oldKind = BreakKind(oldText);
        int newKind = BreakKind(newText);
        result.LineEndingsDiffer = oldKind != 0 && newKind != 0 && oldKind != newKind;

        string[] a = SplitLines(oldText);
        string[] b = SplitLines(newText);
        List<Op> ops = Diff(a, b, out bool coarse);
        result.Coarse = coarse;

        // Where each op sits in the old and in the new text (the number of lines before it).
        int[] oldBefore = new int[ops.Count + 1];
        int[] newBefore = new int[ops.Count + 1];
        for (int i = 0; i < ops.Count; i++)
        {
            oldBefore[i + 1] = oldBefore[i] + (ops[i].Kind != OpKind.Insert ? 1 : 0);
            newBefore[i + 1] = newBefore[i] + (ops[i].Kind != OpKind.Delete ? 1 : 0);
            if (ops[i].Kind == OpKind.Delete)
            {
                result.RemovedLines++;
            }
            else if (ops[i].Kind == OpKind.Insert)
            {
                result.AddedLines++;
            }
        }
        if (result.IsIdentical)
        {
            return result;
        }

        StringBuilder text = new StringBuilder();
        text.Append("--- ").Append(oldLabel).Append('\n');
        text.Append("+++ ").Append(newLabel).Append('\n');

        int index = 0;
        while (index < ops.Count)
        {
            if (ops[index].Kind == OpKind.Equal)
            {
                index++;
                continue;
            }
            // A hunk: the changes that are closer than two contexts to each other go together.
            int first = index;
            int last = index;
            int scan = index + 1;
            while (scan < ops.Count)
            {
                if (ops[scan].Kind != OpKind.Equal)
                {
                    last = scan;
                    scan++;
                    continue;
                }
                int run = 0;
                int probe = scan;
                while (probe < ops.Count && ops[probe].Kind == OpKind.Equal)
                {
                    run++;
                    probe++;
                }
                if (probe >= ops.Count || run > 2 * contextLines)
                {
                    break;
                }
                scan = probe;
            }
            int start = Math.Max(0, first - contextLines);
            int end = Math.Min(ops.Count, last + 1 + contextLines);
            AppendHunk(text, ops, oldBefore, newBefore, start, end);
            index = end;
        }
        result.UnifiedText = text.ToString();
        return result;
    }

    private static void AppendHunk(StringBuilder text, List<Op> ops, int[] oldBefore, int[] newBefore, int start, int end)
    {
        int oldCount = oldBefore[end] - oldBefore[start];
        int newCount = newBefore[end] - newBefore[start];
        // An empty range is named by the line before it, as diff does.
        int oldStart = oldCount == 0 ? oldBefore[start] : oldBefore[start] + 1;
        int newStart = newCount == 0 ? newBefore[start] : newBefore[start] + 1;
        text.Append("@@ -").Append(oldStart);
        if (oldCount != 1)
        {
            text.Append(',').Append(oldCount);
        }
        text.Append(" +").Append(newStart);
        if (newCount != 1)
        {
            text.Append(',').Append(newCount);
        }
        text.Append(" @@\n");
        for (int i = start; i < end; i++)
        {
            string line = ops[i].Text;
            bool noEol = line.Length > 0 && line[line.Length - 1] == NoEolMarker;
            if (noEol)
            {
                line = line.Substring(0, line.Length - 1);
            }
            text.Append(ops[i].Kind == OpKind.Equal ? ' ' : (ops[i].Kind == OpKind.Delete ? '-' : '+'));
            text.Append(line).Append('\n');
            if (noEol)
            {
                text.Append("\\ No newline at end of file\n");
            }
        }
    }

    /// <summary>0: no line break, 1: CRLF, 2: LF, 3: CR.</summary>
    private static int BreakKind(string text)
    {
        if (text.IndexOf("\r\n", StringComparison.Ordinal) >= 0)
        {
            return 1;
        }
        if (text.IndexOf('\n') >= 0)
        {
            return 2;
        }
        return text.IndexOf('\r') >= 0 ? 3 : 0;
    }

    /// <summary>The lines of a text; the last one carries <see cref="NoEolMarker"/> when no line break follows it.</summary>
    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return new string[0];
        }
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        bool endsWithBreak = normalized[normalized.Length - 1] == '\n';
        if (endsWithBreak)
        {
            normalized = normalized.Substring(0, normalized.Length - 1);
        }
        string[] lines = normalized.Split('\n');
        if (!endsWithBreak)
        {
            lines[lines.Length - 1] += NoEolMarker;
        }
        return lines;
    }

    private static List<Op> Diff(string[] a, string[] b, out bool coarse)
    {
        coarse = false;
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && string.Equals(a[prefix], b[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix
            && string.Equals(a[a.Length - 1 - suffix], b[b.Length - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        List<Op> middle = DiffMiddle(a, prefix, a.Length - prefix - suffix, b, prefix, b.Length - prefix - suffix, out coarse);
        List<Op> ops = new List<Op>(prefix + middle.Count + suffix);
        for (int i = 0; i < prefix; i++)
        {
            ops.Add(new Op { Kind = OpKind.Equal, Text = a[i] });
        }
        ops.AddRange(middle);
        for (int i = a.Length - suffix; i < a.Length; i++)
        {
            ops.Add(new Op { Kind = OpKind.Equal, Text = a[i] });
        }
        return ops;
    }

    /// <summary>
    /// The shortest edit script between a[aStart..aStart+n) and b[bStart..bStart+m) (Myers, O(ND)). When it is longer
    /// than <see cref="MaxEditDistance"/> the whole range is reported as replaced.
    /// </summary>
    private static List<Op> DiffMiddle(string[] a, int aStart, int n, string[] b, int bStart, int m, out bool coarse)
    {
        coarse = false;
        List<Op> ops = new List<Op>();
        if (n == 0 || m == 0)
        {
            for (int i = 0; i < n; i++)
            {
                ops.Add(new Op { Kind = OpKind.Delete, Text = a[aStart + i] });
            }
            for (int j = 0; j < m; j++)
            {
                ops.Add(new Op { Kind = OpKind.Insert, Text = b[bStart + j] });
            }
            return ops;
        }

        int limit = Math.Min(n + m, MaxEditDistance);
        int offset = limit + 1;
        int[] v = new int[2 * limit + 3];
        List<int[]> trace = new List<int[]>();
        int found = -1;
        for (int d = 0; d <= limit && found < 0; d++)
        {
            // What the round before left in v, for the walk back: only the diagonals -d..d can matter.
            int[] snapshot = new int[2 * d + 1];
            for (int k = -d; k <= d; k++)
            {
                snapshot[k + d] = v[offset + k];
            }
            trace.Add(snapshot);
            for (int k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                {
                    x = v[offset + k + 1];
                }
                else
                {
                    x = v[offset + k - 1] + 1;
                }
                int y = x - k;
                while (x < n && y < m && string.Equals(a[aStart + x], b[bStart + y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }
        }

        if (found < 0)
        {
            coarse = true;
            for (int i = 0; i < n; i++)
            {
                ops.Add(new Op { Kind = OpKind.Delete, Text = a[aStart + i] });
            }
            for (int j = 0; j < m; j++)
            {
                ops.Add(new Op { Kind = OpKind.Insert, Text = b[bStart + j] });
            }
            return ops;
        }

        // Walk back from the end, collecting the ops in reverse.
        List<Op> reversed = new List<Op>();
        int px = n;
        int py = m;
        for (int d = found; d > 0; d--)
        {
            int[] snapshot = trace[d];
            int k = px - py;
            int previousK;
            if (k == -d || (k != d && snapshot[k - 1 + d] < snapshot[k + 1 + d]))
            {
                previousK = k + 1;
            }
            else
            {
                previousK = k - 1;
            }
            int previousX = snapshot[previousK + d];
            int previousY = previousX - previousK;
            while (px > previousX && py > previousY)
            {
                reversed.Add(new Op { Kind = OpKind.Equal, Text = a[aStart + px - 1] });
                px--;
                py--;
            }
            if (px == previousX)
            {
                reversed.Add(new Op { Kind = OpKind.Insert, Text = b[bStart + py - 1] });
            }
            else
            {
                reversed.Add(new Op { Kind = OpKind.Delete, Text = a[aStart + px - 1] });
            }
            px = previousX;
            py = previousY;
        }
        while (px > 0 && py > 0)
        {
            reversed.Add(new Op { Kind = OpKind.Equal, Text = a[aStart + px - 1] });
            px--;
            py--;
        }
        for (int i = reversed.Count - 1; i >= 0; i--)
        {
            ops.Add(reversed[i]);
        }
        return ops;
    }
}
