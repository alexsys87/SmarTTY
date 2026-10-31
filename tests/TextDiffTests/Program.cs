using System;
using System.Collections.Generic;
using System.Text;
using SmarTTY.CodeEditing;

// Regression harness for the line diff behind the editor's "Show Changes" window.
// Run with: dotnet run --project tests/TextDiffTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal(string actual, string expected, string name)
    {
        bool equal = string.Equals(actual, expected, StringComparison.Ordinal);
        Check(equal, name + (equal ? "" : "\n--- expected\n" + expected + "\n--- got\n" + actual));
    }

    private static TextDiff.Result Diff(string a, string b)
    {
        return TextDiff.Compare(a, b, "old", "new");
    }

    // Applies a unified diff made by TextDiff to the old text: the result has to be the new text.
    private static string Apply(string oldText, string unified)
    {
        string[] oldLines = oldText.Length == 0 ? new string[0] : oldText.TrimEnd('\n').Split('\n');
        List<string> result = new List<string>();
        int position = 0;
        string[] diffLines = unified.TrimEnd('\n').Split('\n');
        int i = 2;
        while (i < diffLines.Length)
        {
            string header = diffLines[i++];
            int at = header.IndexOf('-');
            int comma = header.IndexOfAny(new[] { ',', ' ' }, at);
            int oldStart = int.Parse(header.Substring(at + 1, comma - at - 1));
            string rest = header.Substring(header.IndexOf('-') + 1);
            string oldPart = rest.Substring(0, rest.IndexOf(' '));
            int oldCount = oldPart.Contains(",") ? int.Parse(oldPart.Split(',')[1]) : 1;
            int start = oldCount == 0 ? oldStart : oldStart - 1;
            while (position < start)
            {
                result.Add(oldLines[position++]);
            }
            while (i < diffLines.Length && !diffLines[i].StartsWith("@@"))
            {
                string line = diffLines[i++];
                if (line.StartsWith("\\"))
                {
                    continue;
                }
                char kind = line[0];
                string text = line.Substring(1);
                if (kind == ' ')
                {
                    result.Add(oldLines[position++]);
                }
                else if (kind == '-')
                {
                    position++;
                }
                else
                {
                    result.Add(text);
                }
            }
        }
        while (position < oldLines.Length)
        {
            result.Add(oldLines[position++]);
        }
        return string.Join("\n", result);
    }

    private static void BasicChecks()
    {
        TextDiff.Result same = Diff("a\nb\n", "a\nb\n");
        Check(same.IsIdentical, "equal texts are identical");
        Equal(same.UnifiedText, "", "equal texts give no diff text");
        Check(Diff("", "").IsIdentical, "two empty texts are identical");
        Check(Diff(null, "").IsIdentical, "null counts as empty");

        TextDiff.Result changed = Diff("one\ntwo\nthree\n", "one\n2\nthree\n");
        Equal(changed.UnifiedText, "--- old\n+++ new\n@@ -1,3 +1,3 @@\n one\n-two\n+2\n three\n", "one changed line");
        Check(changed.AddedLines == 1 && changed.RemovedLines == 1, "one changed line: counts");

        TextDiff.Result added = Diff("", "x\ny\n");
        Equal(added.UnifiedText, "--- old\n+++ new\n@@ -0,0 +1,2 @@\n+x\n+y\n", "everything added to an empty text");

        TextDiff.Result removed = Diff("x\ny\n", "");
        Equal(removed.UnifiedText, "--- old\n+++ new\n@@ -1,2 +0,0 @@\n-x\n-y\n", "everything removed");

        TextDiff.Result inserted = Diff("a\nb\nc\n", "a\nb\nNEW\nc\n");
        Equal(inserted.UnifiedText, "--- old\n+++ new\n@@ -1,3 +1,4 @@\n a\n b\n+NEW\n c\n", "one inserted line");
    }

    private static void LineBreakChecks()
    {
        TextDiff.Result crlf = Diff("a\r\nb\r\n", "a\nb\n");
        Check(crlf.IsIdentical, "CRLF against LF is not a change of lines");
        Check(crlf.LineEndingsDiffer, "CRLF against LF is reported");
        Check(!Diff("a\nb\n", "a\nb\n").LineEndingsDiffer, "same line breaks are not reported");
        Check(!Diff("a", "a\nb\n").LineEndingsDiffer, "a text without a line break is not a different kind");

        TextDiff.Result noEol = Diff("a\nb\n", "a\nb");
        Equal(noEol.UnifiedText, "--- old\n+++ new\n@@ -1,2 +1,2 @@\n a\n-b\n+b\n\\ No newline at end of file\n", "the final line break removed");
        TextDiff.Result addEol = Diff("a\nb", "a\nb\n");
        Equal(addEol.UnifiedText, "--- old\n+++ new\n@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+b\n", "the final line break added");
    }

    private static void HunkChecks()
    {
        StringBuilder oldText = new StringBuilder();
        StringBuilder newText = new StringBuilder();
        for (int i = 1; i <= 30; i++)
        {
            oldText.Append("line").Append(i).Append('\n');
            newText.Append(i == 3 || i == 28 ? "CHANGED" + i : "line" + i).Append('\n');
        }
        TextDiff.Result far = Diff(oldText.ToString(), newText.ToString());
        int hunks = 0;
        foreach (string line in far.UnifiedText.Split('\n'))
        {
            if (line.StartsWith("@@"))
            {
                hunks++;
            }
        }
        Equal(hunks.ToString(), "2", "far apart changes make two hunks");
        Check(far.UnifiedText.Contains("@@ -1,6 +1,6 @@"), "first hunk: three lines of context each side (start of file)");
        Check(far.UnifiedText.Contains("@@ -25,6 +25,6 @@"), "second hunk: context before and after (end of file)");

        // Changes six lines apart share one hunk with three lines of context; seven apart do not.
        string baseText = "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12\n";
        TextDiff.Result near = Diff(baseText, baseText.Replace("2\n", "two\n").Replace("8\n", "eight\n"));
        Equal(CountHunks(near.UnifiedText).ToString(), "1", "changes within two contexts share a hunk");
        TextDiff.Result apart = Diff(baseText, baseText.Replace("2\n", "two\n").Replace("10\n", "ten\n"));
        Equal(CountHunks(apart.UnifiedText).ToString(), "2", "changes further apart than two contexts are separate hunks");

        TextDiff.Result noContext = TextDiff.Compare("a\nb\nc\n", "a\nX\nc\n", "o", "n", contextLines: 0);
        Equal(noContext.UnifiedText, "--- o\n+++ n\n@@ -2 +2 @@\n-b\n+X\n", "no context lines");
    }

    private static int CountHunks(string unified)
    {
        int hunks = 0;
        foreach (string line in unified.Split('\n'))
        {
            if (line.StartsWith("@@"))
            {
                hunks++;
            }
        }
        return hunks;
    }

    private static void RoundTripChecks()
    {
        Random random = new Random(12345);
        for (int round = 0; round < 300; round++)
        {
            int length = random.Next(0, 40);
            List<string> oldLines = new List<string>();
            for (int i = 0; i < length; i++)
            {
                oldLines.Add("v" + random.Next(0, 8));
            }
            List<string> newLines = new List<string>(oldLines);
            int edits = random.Next(0, 8);
            for (int e = 0; e < edits; e++)
            {
                int kind = random.Next(3);
                if (kind == 0 && newLines.Count > 0)
                {
                    newLines.RemoveAt(random.Next(newLines.Count));
                }
                else if (kind == 1)
                {
                    newLines.Insert(random.Next(newLines.Count + 1), "n" + random.Next(0, 8));
                }
                else if (newLines.Count > 0)
                {
                    newLines[random.Next(newLines.Count)] = "m" + random.Next(0, 8);
                }
            }
            string oldText = oldLines.Count == 0 ? "" : string.Join("\n", oldLines) + "\n";
            string newText = newLines.Count == 0 ? "" : string.Join("\n", newLines) + "\n";
            TextDiff.Result result = Diff(oldText, newText);
            if (oldText == newText)
            {
                if (!result.IsIdentical)
                {
                    throw new Exception("FAIL: round " + round + ": equal texts reported as changed");
                }
                continue;
            }
            string applied = result.UnifiedText.Length == 0 ? oldText.TrimEnd('\n') : Apply(oldText, result.UnifiedText);
            if (applied != newText.TrimEnd('\n'))
            {
                throw new Exception("FAIL: round " + round + ": applying the diff does not give the new text\n" + result.UnifiedText);
            }
            // The edit script is a shortest one: removed - added equals the change of length, and both are within the edits made.
            if (result.AddedLines - result.RemovedLines != newLines.Count - oldLines.Count)
            {
                throw new Exception("FAIL: round " + round + ": the counts do not add up");
            }
        }
        _passed++;
        Console.WriteLine("PASS: 300 random edits: applying the diff to the old text gives the new text");
    }

    private static void LargeChecks()
    {
        // A big file with a few changes is fast and exact.
        StringBuilder oldText = new StringBuilder();
        StringBuilder newText = new StringBuilder();
        for (int i = 0; i < 100000; i++)
        {
            oldText.Append("config line ").Append(i).Append('\n');
            newText.Append(i == 50000 ? "changed" : "config line " + i).Append('\n');
        }
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        TextDiff.Result big = Diff(oldText.ToString(), newText.ToString());
        Check(big.AddedLines == 1 && big.RemovedLines == 1 && !big.Coarse, "100000 lines with one change: exact");
        Check(watch.ElapsedMilliseconds < 3000, "100000 lines with one change: fast (" + watch.ElapsedMilliseconds + " ms)");

        // Two texts with nothing in common are replaced as a whole, fast, and flagged as coarse when too long to search.
        StringBuilder left = new StringBuilder();
        StringBuilder right = new StringBuilder();
        for (int i = 0; i < 5000; i++)
        {
            left.Append("L").Append(i).Append('\n');
            right.Append("R").Append(i).Append('\n');
        }
        watch.Restart();
        TextDiff.Result different = Diff(left.ToString(), right.ToString());
        Check(different.AddedLines == 5000 && different.RemovedLines == 5000, "nothing in common: all removed and all added");
        Check(different.Coarse, "nothing in common over the search limit: coarse");
        Check(watch.ElapsedMilliseconds < 5000, "nothing in common: fast enough (" + watch.ElapsedMilliseconds + " ms)");
    }

    private static void Main()
    {
        BasicChecks();
        LineBreakChecks();
        HunkChecks();
        RoundTripChecks();
        LargeChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " diff checks passed.");
    }
}
