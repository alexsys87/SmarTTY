using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.CodeEditing;
using SmarTTY.Controls;

// Regression harness for the rows of the text view when a provider adds rows under lines (the inline messages) and another hides
// lines (folded blocks): which line a row shows (VirtualRowLayout) and the contract of the message provider. The expected rows
// are built from scratch, line by line, and compared with what the layout says for every row and every line.
// Run with: dotnet run --project tests/VirtualRowTests
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

    // The fold manager as it answers the provider questions (FoldingManager): header line -> number of hidden lines; no nesting here.
    private sealed class FoldFake : ReadonlyTextBrowser.IVirtualLineProvider
    {
        public readonly Dictionary<int, int> Regions = new Dictionary<int, int>();

        public int GetTotalVirtualRows() { return 0; }

        public int GetTotalHiddenLines() { return Regions.Values.Sum(); }

        public int GetNextSpecialLine(int lineIndex)
        {
            int minLine = int.MaxValue;
            foreach (int key in Regions.Keys)
            {
                if (key >= lineIndex && key < minLine)
                {
                    minLine = key;
                }
            }
            return minLine;
        }

        public int GetVirtualRowCount(int lineIndex) { return 0; }

        public void GetVirtualLines(int lineIndex, List<ReadonlyTextBrowser.IVirtualLine> list) { }

        public int GetHiddenLineCount(int lineIndex) { return Regions.TryGetValue(lineIndex, out int hidden) ? hidden : 0; }

        public ReadonlyTextBrowser.ILineDecorator GetDecorator(int lineIndex) { return null; }

        public bool OnCaretMoved(int lineIndex, int columnIndex) { return false; }
    }

    // The composite of ReadonlyTextBrowser, as it combines the answers of the providers (CompositeVirtualLineProvider).
    private sealed class Composite : ReadonlyTextBrowser.IVirtualLineProvider
    {
        private readonly List<ReadonlyTextBrowser.IVirtualLineProvider> _providers;

        public Composite(params ReadonlyTextBrowser.IVirtualLineProvider[] providers) { _providers = providers.ToList(); }

        public int GetTotalVirtualRows() { return _providers.Sum(p => p.GetTotalVirtualRows()); }

        public int GetTotalHiddenLines() { return _providers.Sum(p => p.GetTotalHiddenLines()); }

        public int GetNextSpecialLine(int line)
        {
            int next = int.MaxValue;
            foreach (ReadonlyTextBrowser.IVirtualLineProvider provider in _providers)
            {
                int answer = provider.GetNextSpecialLine(line);
                if (answer >= 0 && answer < next)
                {
                    next = answer;
                }
            }
            return next;
        }

        public int GetVirtualRowCount(int line) { return _providers.Sum(p => p.GetVirtualRowCount(line)); }

        public void GetVirtualLines(int line, List<ReadonlyTextBrowser.IVirtualLine> list) { foreach (var p in _providers) p.GetVirtualLines(line, list); }

        public int GetHiddenLineCount(int line) { return _providers.Sum(p => p.GetHiddenLineCount(line)); }

        public ReadonlyTextBrowser.ILineDecorator GetDecorator(int line) { return null; }

        public bool OnCaretMoved(int line, int column) { return false; }
    }

    // All the rows of the text, top to bottom: a line, the message rows under it, then the lines it hides are skipped.
    private static List<(int Line, bool Message)> Rows(int lineCount, HashSet<int> messageLines, Dictionary<int, int> regions)
    {
        List<(int, bool)> rows = new List<(int, bool)>();
        int line = 0;
        while (line < lineCount)
        {
            rows.Add((line, false));
            if (messageLines.Contains(line))
            {
                rows.Add((line, true));
            }
            line += 1 + (regions.TryGetValue(line, out int hidden) ? hidden : 0);
        }
        return rows;
    }

    private static string Describe(int lineCount, HashSet<int> messages, Dictionary<int, int> regions)
    {
        return lineCount + " lines, messages on [" + string.Join(",", messages.OrderBy(x => x)) + "], folds [" + string.Join(",", regions.OrderBy(x => x.Key).Select(x => x.Key + "+" + x.Value)) + "]";
    }

    // Compares RowToLine, LineToRowIndex and the scroll extent with the rows built from scratch. Returns what differs, or "".
    private static string Compare(int lineCount, HashSet<int> messageLines, Dictionary<int, int> regions)
    {
        InlineMessageProvider messages = new InlineMessageProvider();
        foreach (int line in messageLines)
        {
            messages.AddMessage(line, InlineMessageProvider.MessageSeverity.Error, "m", 0);
        }
        FoldFake folds = new FoldFake();
        foreach (KeyValuePair<int, int> region in regions)
        {
            folds.Regions[region.Key] = region.Value;
        }
        Composite provider = new Composite(messages, folds);
        List<(int Line, bool Message)> rows = Rows(lineCount, messageLines, regions);

        int extent = lineCount + provider.GetTotalVirtualRows() - provider.GetTotalHiddenLines();
        if (extent != rows.Count)
        {
            return "the scroll extent is " + extent + " rows, the text has " + rows.Count;
        }
        for (int row = 0; row < rows.Count; row++)
        {
            int line = VirtualRowLayout.RowToLine(provider, lineCount, row, out int remaining, out _);
            if (line != rows[row].Line)
            {
                return "row " + row + " shows line " + line + ", expected " + rows[row].Line;
            }
            // For a row under a line: how many of that line's rows are left, this one included.
            int expectedRemaining = rows[row].Message ? 1 : -1;
            if (remaining != expectedRemaining)
            {
                return "row " + row + ": " + remaining + " rows left, expected " + expectedRemaining;
            }
        }
        for (int row = 0; row < rows.Count; row++)
        {
            if (rows[row].Message)
            {
                continue;
            }
            int index = VirtualRowLayout.LineToRowIndex(provider, lineCount, rows[row].Line);
            if (index != row)
            {
                return "line " + rows[row].Line + " is at row " + index + ", expected " + row;
            }
        }
        return "";
    }

    private static void Same(string name, int lineCount, int[] messages, Dictionary<int, int> regions)
    {
        HashSet<int> set = new HashSet<int>(messages);
        string difference = Compare(lineCount, set, regions);
        Check(difference == "", name + (difference == "" ? "" : " (" + Describe(lineCount, set, regions) + "): " + difference));
    }

    private static Dictionary<int, int> Folds(params (int Header, int Hidden)[] regions)
    {
        return regions.ToDictionary(r => r.Header, r => r.Hidden);
    }

    private static void ProviderChecks()
    {
        InlineMessageProvider none = new InlineMessageProvider();
        Check(none.Count == 0, "a new provider has no messages");
        Check(none.GetNextSpecialLine(0) == int.MaxValue, "no messages: no special line ahead (int.MaxValue, as the fold manager says)");
        Check(none.GetTotalVirtualRows() == 0 && none.GetVirtualRowCount(3) == 0, "no messages: no rows");

        InlineMessageProvider provider = new InlineMessageProvider();
        provider.AddMessage(4, InlineMessageProvider.MessageSeverity.Error, "a", 0);
        provider.AddMessage(9, InlineMessageProvider.MessageSeverity.Warning, "b", 2);
        Check(provider.Count == 2, "two messages");
        Check(provider.GetNextSpecialLine(0) == 4 && provider.GetNextSpecialLine(4) == 4, "the nearest line with a message, the line itself included");
        Check(provider.GetNextSpecialLine(5) == 9 && provider.GetNextSpecialLine(9) == 9, "the next one after a line");
        Check(provider.GetNextSpecialLine(10) == int.MaxValue, "after the last message: nothing ahead, and not -1, which would hide a fold ahead from the composite");
        Check(provider.GetVirtualRowCount(4) == 1 && provider.GetVirtualRowCount(5) == 0, "one row under a line with a message");
        Check(provider.GetTotalVirtualRows() == 2, "two rows in all");
        Check(provider.GetHiddenLineCount(4) == 0 && provider.GetTotalHiddenLines() == 0, "a message hides nothing");

        // Lines inserted above move the messages; lines deleted take the messages on them away.
        provider.ShiftLines(1, 0, 3);
        Check(provider.GetNextSpecialLine(0) == 7 && provider.GetNextSpecialLine(8) == 12, "three lines inserted above: the messages are 3 lines lower");
        provider.ShiftLines(5, 3, 0);
        Check(provider.Count == 1 && provider.GetNextSpecialLine(0) == 9, "three lines deleted around the first message: it is gone, the other moves up");

        provider.Clear();
        Check(provider.Count == 0 && provider.GetNextSpecialLine(0) == int.MaxValue, "cleared");
        provider.Dispose();
        provider.Dispose();
        Check(true, "disposing twice is harmless");
    }

    private static void LayoutChecks()
    {
        Same("no messages, no folds", 10, new int[0], Folds());
        Same("one message", 10, new[] { 3 }, Folds());
        Same("a message on the first line", 10, new[] { 0 }, Folds());
        Same("a message on the last line", 10, new[] { 9 }, Folds());
        Same("messages on neighbouring lines", 10, new[] { 3, 4, 5 }, Folds());
        Same("a fold only", 20, new int[0], Folds((5, 4)));
        Same("a message above a fold", 20, new[] { 2 }, Folds((10, 4)));
        Same("a fold above a message", 20, new[] { 15 }, Folds((5, 4)));
        Same("a message on the header of a fold", 20, new[] { 5 }, Folds((5, 4)));
        Same("a message on the header, another below", 30, new[] { 5, 20 }, Folds((5, 4), (12, 3)));
        Same("a message, then a fold far below the last message", 30, new[] { 3 }, Folds((12, 5)));
        Same("two folds around a message", 40, new[] { 20 }, Folds((5, 4), (30, 6)));
        Same("a fold at the very end", 20, new[] { 1 }, Folds((17, 2)));
    }

    private static void RandomChecks()
    {
        Random random = new Random(7);
        int checkedScenarios = 0;
        for (int n = 0; n < 3000; n++)
        {
            int lineCount = random.Next(5, 60);
            Dictionary<int, int> regions = new Dictionary<int, int>();
            HashSet<int> hidden = new HashSet<int>();
            int foldCount = random.Next(0, 4);
            for (int f = 0; f < foldCount; f++)
            {
                int header = random.Next(0, lineCount - 2);
                int length = random.Next(1, Math.Min(6, lineCount - 1 - header));
                bool overlaps = false;
                for (int line = header; line <= header + length; line++)
                {
                    if (hidden.Contains(line) || regions.ContainsKey(line))
                    {
                        overlaps = true;
                    }
                }
                if (overlaps)
                {
                    continue;
                }
                regions[header] = length;
                for (int line = header + 1; line <= header + length; line++)
                {
                    hidden.Add(line);
                }
            }
            HashSet<int> messages = new HashSet<int>();
            int messageCount = random.Next(0, 5);
            for (int m = 0; m < messageCount; m++)
            {
                int line = random.Next(0, lineCount);
                if (!hidden.Contains(line))
                {
                    messages.Add(line);
                }
            }
            string difference = Compare(lineCount, messages, regions);
            if (difference != "")
            {
                throw new Exception("FAIL: random layout (" + Describe(lineCount, messages, regions) + "): " + difference);
            }
            checkedScenarios++;
        }
        _passed++;
        Console.WriteLine("PASS: " + checkedScenarios + " random texts with messages and folds: every row and every line is where it should be");
    }

    private static void Main()
    {
        ProviderChecks();
        LayoutChecks();
        RandomChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " virtual row checks passed.");
    }
}
