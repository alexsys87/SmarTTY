using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.Terminal;

// Checks of the history of the commands typed in a locally edited input line (Terminal/LineHistory.cs): what is kept, Up and Down
// by the beginning of the line, the Ctrl+R search, and when each of them starts again.
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

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static LineHistory Make(params string[] commands)
    {
        LineHistory history = new LineHistory();
        foreach (string command in commands)
        {
            history.Add(command);
        }
        return history;
    }

    private static string Join(IEnumerable<string> items) => string.Join("|", items);

    private static void ListChecks()
    {
        LineHistory history = Make("  ls  ", "", "   ", null, "cd /tmp");
        Equal(Join(history.Items), "ls|cd /tmp", "a command is trimmed and an empty one is not added");
        history.Add("cd /tmp");
        Equal(history.Count, 2, "a command that repeats the last one is not added");
        history.Add("ls");
        history.Add("cd /tmp");
        Equal(history.Count, 4, "an older command may be added again");
        Equal(Join(history.NewestFirst), "cd /tmp|ls|cd /tmp|ls", "the list the newest first");
        Equal(Join(history.Distinct()), "cd /tmp|ls", "every command once for the window, the newest first");

        history = new LineHistory();
        for (int i = 0; i < LineHistory.MaxSize + 5; i++)
        {
            history.Add("c" + i);
        }
        Equal(history.Count, LineHistory.MaxSize, "no more than the limit is kept");
        Equal(history.Items[0], "c5", "the oldest go first");
        Equal(history.Items[history.Count - 1], "c" + (LineHistory.MaxSize + 4), "and the newest stays");

        history = Make("a", "b", "a", "c");
        Check(!history.Remove(null), "nothing to remove (null)");
        Check(!history.Remove(new string[0]), "nothing to remove (empty)");
        Check(!history.Remove(new[] { "zzz" }), "nothing to remove (not in the list)");
        Check(history.Remove(new[] { "a" }), "a command is removed");
        Equal(Join(history.Items), "b|c", "every copy of it");
        history.Navigate("", older(), true);
        history.Clear();
        Equal(history.Navigate("", forward(), true), null, "clearing the history ends Up and Down through it");
        Equal(history.Count, 0, "the list is cleared");
        Equal(history.Items.Count, 0, "and the items are empty");
    }

    private static void NewestFirstChecks()
    {
        LineHistory history = Make("a", "b");
        List<string> first = history.NewestFirst;
        Check(ReferenceEquals(first, history.NewestFirst), "the copy is kept while the history does not change");
        history.Add("c");
        Equal(Join(history.NewestFirst), "c|b|a", "a command added makes a new copy");
        List<string> second = history.NewestFirst;
        history.Add("c");
        Check(ReferenceEquals(second, history.NewestFirst), "a command that is not added does not");
        history.Remove(new[] { "b" });
        Equal(Join(history.NewestFirst), "c|a", "a command removed does");
        List<string> third = history.NewestFirst;
        history.Clear();
        Check(!ReferenceEquals(third, history.NewestFirst) && history.NewestFirst.Count == 0, "and clearing");
    }

    private static void NavigateChecks()
    {
        LineHistory history = Make("uname -a", "uptime", "ls -la /tmp");
        Equal(history.Navigate("", forward(), true), null, "Down on the line being typed shows nothing");
        Equal(history.Navigate("", older(), true), "ls -la /tmp", "Up shows the newest command");
        Equal(history.Navigate("", older(), true), "uptime", "Up again the one before");
        Equal(history.Navigate("", older(), true), "uname -a", "and the oldest");
        Equal(history.Navigate("", older(), true), null, "Up stops at the oldest");
        Equal(history.Navigate("", forward(), true), "uptime", "Down goes back to the newer");
        Equal(history.Navigate("", forward(), true), "ls -la /tmp", "and the newest");
        Equal(history.Navigate("", forward(), true), "", "past the newest it brings the typed line back");
        Equal(history.Navigate("", forward(), true), null, "and then there is nothing newer");

        history = Make("uname -a", "uptime", "ls -la /tmp");
        Equal(history.Navigate("u", older(), true), "uptime", "with the prefix search only the commands that start with the text are shown");
        Equal(history.Navigate("u", older(), true), "uname -a", "again");
        Equal(history.Navigate("u", older(), true), null, "the oldest match stops it");
        Equal(history.Navigate("u", forward(), true), "uptime", "Down goes back");
        Equal(history.Navigate("u", forward(), true), "u", "and past the newest match brings the typed text back");

        history = Make("uname -a", "uptime", "ls -la /tmp");
        Equal(history.Navigate("u", older(), false), "ls -la /tmp", "with the prefix search off Up shows the newest command whatever was typed");
        Equal(history.Navigate("u", forward(), false), "u", "and Down brings the typed text back");

        history = Make("a", "b", "a", "c");
        Equal(history.Navigate("", older(), true), "c", "the commands that repeat are shown once (newest)");
        Equal(history.Navigate("", older(), true), "a", "(the copy before the newest)");
        Equal(history.Navigate("", older(), true), "b", "(and the other one)");
        Equal(history.Navigate("", older(), true), null, "(then nothing: the oldest a is a copy)");

        history = Make("xa", "xb", "ya");
        Equal(history.Navigate("x", older(), true), "xb", "Up by the prefix x");
        Equal(history.Navigate("x", forward(), true), "x", "Down brings the typed line back");
        Equal(history.Navigate("y", older(), true), "ya", "the line edited there is the one the prefix is taken from");

        history = Make("x", "y");
        history.Navigate("", older(), true);
        history.ResetNavigation();
        Equal(history.Navigate("", forward(), true), null, "after a reset Down has nothing newer again");
        Equal(history.Navigate("", older(), true), "y", "and Up starts from the newest");
        history.Add("z");
        Equal(history.Navigate("", older(), true), "z", "a command added starts it again, too");
    }

    private static bool older() => true;

    private static bool forward() => false;

    private static void SearchChecks()
    {
        LineHistory history = Make("uname -a", "uptime", "ls -la /tmp");
        Check(!history.IsSearching, "no search at first");
        Equal(history.SearchOriginalInput, null, "and no original input");
        Equal(history.NextSearchMatch("t"), "ls -la /tmp", "Ctrl+R finds the newest command that contains the text");
        Check(history.IsSearching, "a search is going on");
        Equal(history.SearchOriginalInput, "t", "it knows what was typed");
        Equal(history.NextSearchMatch("changed"), "uptime", "the next press shows the one before (the text is the one of the start)");
        Equal(history.SearchOriginalInput, "t", "it keeps the original input");
        Equal(history.NextSearchMatch("t"), "ls -la /tmp", "and goes round to the newest");
        history.EndSearch();
        Check(!history.IsSearching, "the search ends");
        Equal(history.NextSearchMatch("UNAME"), "uname -a", "the case of the text does not matter");
        history.EndSearch();
        Equal(history.NextSearchMatch("zzz"), null, "no command contains it");
        Check(history.IsSearching, "yet the search goes on (it ends with Esc)");
        Equal(history.NextSearchMatch("zzz"), null, "and finds nothing again");
        history.EndSearch();

        history.NextSearchMatch("u");
        Equal(history.Navigate("u", older(), true), "uptime", "Up ends the search");
        Check(!history.IsSearching, "(it is over)");
        history.NextSearchMatch("u");
        history.Remove(new[] { "uptime" });
        Check(!history.IsSearching, "removing a command ends it");
        history.NextSearchMatch("u");
        history.Add("unzip x");
        Check(history.IsSearching, "adding one does not (the matches were found when it started)");
        Equal(history.NextSearchMatch("u"), "uname -a", "it goes on with the matches it had");
        history.Clear();
        Check(history.IsSearching, "clearing the history does not end it, as it never did");
    }

    private static void Main()
    {
        ListChecks();
        NewestFirstChecks();
        NavigateChecks();
        SearchChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " line history checks passed.");
    }
}
