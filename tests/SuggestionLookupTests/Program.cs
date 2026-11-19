using System;
using System.Collections.Generic;
using SmarTTY.Terminal;

// Checks of what the grey suggestion of the Smart Terminal command line keeps between two keys (Terminal/SuggestionLookup.cs): the history it was made
// for, when it is made again, and the answer for the last text.
internal static class Program
{
    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static void HistoryChecks()
    {
        SuggestionLookup lookup = new SuggestionLookup();
        object analyzer = new object();
        int built = 0;
        Func<List<string>> build = () => { built++; return new List<string> { "uptime", "uname -a" }; };

        List<string> first = lookup.GetHistory(analyzer, 1, 0, build);
        Equal(built, 1, "the history is made the first time");
        Equal(first.Count, 2, "it is what was built");
        Check(ReferenceEquals(lookup.GetHistory(analyzer, 1, 0, build), first), "the same history is kept for the same analyzer, version and count");
        Equal(built, 1, "and it is not made again");
        lookup.GetHistory(analyzer, 2, 0, build);
        Equal(built, 2, "a new version of the history of the session makes it again");
        lookup.GetHistory(analyzer, 2, 1, build);
        Equal(built, 3, "a command typed in the tab makes it again");
        lookup.GetHistory(new object(), 2, 1, build);
        Equal(built, 4, "another analyzer makes it again");
        lookup.GetHistory(null, 2, 1, build);
        Equal(built, 5, "no analyzer is another analyzer");
        lookup.GetHistory(null, 2, 1, build);
        Equal(built, 5, "but it is kept as well");
        lookup.GetHistory(null, 0, 0, build);
        Equal(built, 6, "the count falling makes it again (a command was removed)");
    }

    private static void FindChecks()
    {
        SuggestionLookup lookup = new SuggestionLookup();
        List<string> history = new List<string> { "uptime", "uname -a" };
        Equal(lookup.Find(history, "un"), "ame -a", "the rest of the newest command that starts with the text");
        history.Insert(0, "unzip x");
        Equal(lookup.Find(history, "un"), "ame -a", "the answer for the same text and the same list is kept, even if the list changed in place");
        Equal(lookup.Find(new List<string>(history), "un"), "zip x", "another list is looked up again");
        Equal(lookup.Find(history, "up"), "time", "another text is looked up again");
        Equal(lookup.Find(history, "zz"), null, "nothing matches");
        Equal(lookup.Find(history, "zz"), null, "and the same answer is kept");
    }

    private static void InvalidateChecks()
    {
        SuggestionLookup lookup = new SuggestionLookup();
        List<string> history = new List<string> { "uptime" };
        Equal(lookup.Find(history, "up"), "time", "a suggestion");
        history.Clear();
        Equal(lookup.Find(history, "up"), "time", "the kept answer does not see a change in place");
        lookup.Invalidate();
        Equal(lookup.Find(history, "up"), null, "Invalidate makes the next text looked up again");
        int built = 0;
        Func<List<string>> build = () => { built++; return new List<string>(); };
        object analyzer = new object();
        lookup.GetHistory(analyzer, 1, 0, build);
        lookup.Invalidate();
        lookup.GetHistory(analyzer, 1, 0, build);
        Equal(built, 2, "and makes the history again, though nothing else changed");
    }

    private static int Main()
    {
        try
        {
            HistoryChecks();
            FindChecks();
            InvalidateChecks();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
        return 0;
    }
}
