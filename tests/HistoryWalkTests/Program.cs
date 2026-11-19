using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.Terminal;

// Checks of Up and Down through the history of a command line edited in place (Terminal/HistoryWalk.cs): the walk by the beginning of the
// line, the edits that are kept on the way, and the new start.
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

    // The commands as the control builds them: newest first, a list of its own every time.
    private static Func<List<string>> History(params string[] newestFirst)
    {
        return () => newestFirst.ToList();
    }

    private static string Up(HistoryWalk walk, string typed, Func<List<string>> history, bool byPrefix = true) => walk.Step(typed, older: true, byPrefix, history);

    private static string Down(HistoryWalk walk, string typed, Func<List<string>> history, bool byPrefix = true) => walk.Step(typed, older: false, byPrefix, history);

    private static void WalkChecks()
    {
        Func<List<string>> history = History("ls -la", "cd /tmp", "cd -", "echo hi");
        HistoryWalk walk = new HistoryWalk();
        Equal(Down(walk, "", history), null, "Down on the line being typed shows nothing");
        Equal(Up(walk, "", history), "ls -la", "Up on an empty line shows the newest command");
        Equal(Up(walk, "ls -la", history), "cd /tmp", "and then the one before");
        Equal(Up(walk, "cd /tmp", history), "cd -", "and the one before that");
        Equal(Up(walk, "cd -", history), "echo hi", "and the oldest");
        Equal(Up(walk, "echo hi", history), null, "Up stops at the oldest");
        Equal(Down(walk, "echo hi", history), "cd -", "Down goes back");
        Equal(Down(walk, "cd -", history), "cd /tmp", "again");
        Equal(Down(walk, "cd /tmp", history), "ls -la", "again");
        Equal(Down(walk, "ls -la", history), "", "past the newest it brings the typed line back");
        Equal(Down(walk, "", history), null, "and there is nothing newer");

        walk = new HistoryWalk();
        Equal(Up(walk, "cd", history), "cd /tmp", "with a prefix, Up shows the newest command that starts with it");
        Equal(Up(walk, "cd /tmp", history), "cd -", "and the next one");
        Equal(Up(walk, "cd -", history), null, "and stops at the oldest match");
        Equal(Down(walk, "cd -", history), "cd /tmp", "Down goes back");
        Equal(Down(walk, "cd /tmp", history), "cd", "and brings the typed text back");

        walk = new HistoryWalk();
        Equal(Up(walk, "cd", history, byPrefix: false), "ls -la", "without the prefix Up shows the newest command whatever was typed");
        Equal(Down(walk, "ls -la", history, byPrefix: false), "cd", "and Down brings the typed text back");

        walk = new HistoryWalk();
        Equal(Up(walk, "zzz", history), null, "no command starts with the text: the line stays");
        Equal(Up(walk, null, history), "ls -la", "a null line is an empty one");
    }

    private static void EditChecks()
    {
        Func<List<string>> history = History("cd -", "cd /tmp");
        HistoryWalk walk = new HistoryWalk();
        Equal(Up(walk, "cd", history), "cd -", "Up recalls a command");
        Equal(Down(walk, "cd -x", history), "cd", "an edit of it, and Down brings the typed text back");
        Equal(Up(walk, "cd", history), "cd -x", "Up shows the edited command: the edit is kept");
        Equal(Down(walk, "", history), "cd", "an emptied recalled line is not kept, Down shows the typed text");
        Equal(Up(walk, "cd", history), "cd -x", "and Up shows the edited command again");

        Equal(Down(walk, "cd -x", history), "cd", "Down from the recalled one brings the typed text back");
        Equal(Up(walk, "cdq", history), null, "the typed text edited after Down is the one the prefix is taken from: nothing starts with it");
        Equal(Up(walk, "cd", history), "cd -x", "and with the letter taken off Up finds the edited command");

        walk = new HistoryWalk();
        Up(walk, "", history);
        Equal(Up(walk, "", history), "cd /tmp", "an empty recalled line at entry 1 is not stored (the second Up starts from the command)");
    }

    private static void ResetChecks()
    {
        List<string> commands = new List<string> { "b", "a" };
        Func<List<string>> history = () => commands.ToList();
        HistoryWalk walk = new HistoryWalk();
        Equal(Up(walk, "", history), "b", "the walk starts");
        commands.Insert(0, "c");
        Equal(Up(walk, "b", history), "a", "a command added meanwhile is not in the walk");
        walk.Reset();
        Equal(Up(walk, "", history), "c", "after a reset the walk starts again from the history as it is");
        Equal(Up(walk, "c", history), "b", "and goes on");
        walk.Reset();
        Equal(Down(walk, "x", history), null, "after a reset Down has nothing newer again");

        history = History("x", "y");
        walk = new HistoryWalk();
        Equal(Up(walk, "x", history), null, "a typed line that is also a command is entry 0 and not shown twice: no other command starts with it");
        Equal(Up(walk, "x", history, byPrefix: false), "y", "without the prefix Up goes on to the next command");
    }

    private static void Main()
    {
        WalkChecks();
        EditChecks();
        ResetChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " history walk checks passed.");
    }
}
