using System;
using SmarTTY.Terminal;

// Regression harness for the history of pasted texts (Terminal/PasteHistory.cs): order, duplicates, capacity and the
// one-line preview of the menu.
// Run with: dotnet run --project tests/PasteHistoryTests
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
        Check(actual == expected, name + (actual == expected ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private static void HistoryChecks()
    {
        PasteHistory.Clear();
        PasteHistory.Add("one", 3);
        PasteHistory.Add("two", 3);
        PasteHistory.Add("three", 3);
        string[] items = PasteHistory.Snapshot(10);
        Check(items.Length == 3 && items[0] == "three" && items[2] == "one", "newest first");

        PasteHistory.Add("one", 3);
        items = PasteHistory.Snapshot(10);
        Check(items.Length == 3 && items[0] == "one" && items[1] == "three" && items[2] == "two", "an equal entry moves up instead of being doubled");

        PasteHistory.Add("four", 3);
        items = PasteHistory.Snapshot(10);
        Check(items.Length == 3 && items[0] == "four" && items[2] == "three", "the oldest entry is dropped beyond the capacity");

        PasteHistory.Add("Four", 3);
        Check(PasteHistory.Count == 3 && PasteHistory.Snapshot(1)[0] == "Four", "entries differing in case are different");

        PasteHistory.Add("   ", 3);
        PasteHistory.Add("", 3);
        PasteHistory.Add(null, 3);
        Check(PasteHistory.Count == 3, "blank text is not remembered");

        Check(PasteHistory.Snapshot(2).Length == 2, "a snapshot is limited to the requested count");
        Check(PasteHistory.Snapshot(0).Length == 0, "a snapshot of zero entries");

        PasteHistory.Add("five", 2);
        Check(PasteHistory.Count == 2, "a smaller capacity shrinks the history");

        PasteHistory.Add("six", 0);
        Check(PasteHistory.Count == 0, "capacity 0 (history off) forgets everything");

        PasteHistory.Add("seven", 500);
        for (int i = 0; i < 100; i++)
        {
            PasteHistory.Add("item " + i, 500);
        }
        Check(PasteHistory.Count == PasteHistory.MaxCapacity, "the history never exceeds the hard limit");

        PasteHistory.Clear();
        Check(PasteHistory.Count == 0, "clear");
    }

    private static void PreviewChecks()
    {
        Equal(PasteHistory.Preview("ls -la", 40), "ls -la", "a short line as it is");
        Equal(PasteHistory.Preview("a\nb", 40), "a\u21b5b  [2 lines]", "a line break is shown as an arrow, with the line count");
        Equal(PasteHistory.Preview("a\r\nb\r\n", 40), "a\u21b5b  [2 lines]", "CR LF counts once, the final line break is ignored");
        Equal(PasteHistory.Preview("a\tb", 40), "a b", "a tab becomes a space");
        Equal(PasteHistory.Preview("abcdefghij", 5), "abcde\u2026", "a long line is cut");
        Equal(PasteHistory.Preview("", 10), "", "empty text");
        Equal(PasteHistory.Preview("x\u001by", 10), "x y", "a control character becomes a space");
    }

    private static void Main()
    {
        HistoryChecks();
        PreviewChecks();
        Console.WriteLine($"All {_passed} checks passed.");
    }
}
