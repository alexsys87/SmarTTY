using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.Terminal;

// Checks of the state of a paste line by line (Terminal/LinePasteRun.cs): when it is over, the next command and what the progress window says, the note
// after a command, and the cancel.
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

    private static LinePasteRun Make(bool stopOnError, params string[] commands) => new LinePasteRun(commands.ToList(), stopOnError);

    private static void StateChecks()
    {
        LinePasteRun run = Make(true, "a", "b", "c");
        Equal(run.Total, 3, "the number of commands");
        Equal(run.Pending.Count, 3, "all are pending");
        Equal(run.Started, 0, "none was started");
        Check(!run.Cancelled, "the paste is not cancelled");
        Check(run.StopOnError, "it stops on an error");
        Check(!Make(false, "a").StopOnError, "or it does not");
        Check(!run.IsOver(false), "it is not over at the start");
        Check(run.IsOver(true), "it is over when the command before failed and the paste stops then");
        Check(!Make(false, "a", "b").IsOver(true), "but not when it is set not to stop");

        Equal(run.TakeNext(out string text), "a", "the first command is taken");
        Equal(text, "Command 1 of 3: a", "the progress window says which one");
        Equal(run.Started, 1, "one was started");
        Equal(run.Pending.Count, 2, "two are pending");
        Equal(run.TakeNext(out text), "b", "the next one");
        Equal(text, "Command 2 of 3: b", "and says so");
        Equal(run.TakeNext(out text), "c", "the last one");
        Check(run.IsOver(false), "the paste is over when nothing is left");
        Equal(run.Started, 3, "all were started");

        string sixty = new string('x', 60);
        string sixtyOne = new string('y', 61);
        run = Make(false, sixty, sixtyOne);
        run.TakeNext(out text);
        Equal(text, "Command 1 of 2: " + sixty, "a command of 60 characters is shown whole");
        run.TakeNext(out text);
        Equal(text, "Command 2 of 2: " + new string('y', 57) + "...", "a longer one is cut to 57 characters and three dots");
    }

    private static void NoteChecks()
    {
        string note;
        LinePasteRun run = Make(true, "a", "b", "c");
        run.TakeNext(out _);
        Equal(run.AfterCommand(false, out note), LinePasteStep.Continue, "after a command that went well the paste goes on");
        Equal(note, null, "with no note");
        Equal(run.AfterCommand(true, out note), LinePasteStep.EndWithNote, "after a command that failed it stops");
        Equal(note, "[paste stopped after a failed command, 2 command(s) not run]", "and says how many were not run");

        run = Make(false, "a", "b", "c");
        run.TakeNext(out _);
        Equal(run.AfterCommand(true, out note), LinePasteStep.Continue, "when it is set not to stop a failed command does not stop it");
        Equal(note, null, "and nothing is said");

        run = Make(true, "a", "b", "c", "d");
        run.TakeNext(out _);
        Check(run.Cancel(), "the paste is cancelled");
        Equal(run.AfterCommand(false, out note), LinePasteStep.EndWithNote, "after the command it ends");
        Equal(note, "[paste cancelled, 3 command(s) not run]", "and says that it was cancelled");

        run = Make(true, "a", "b");
        run.TakeNext(out _);
        run.Cancel();
        run.AfterCommand(true, out note);
        Equal(note, "[paste cancelled, 1 command(s) not run]", "a cancel is said before a failure when both are there");

        run = Make(true, "a");
        run.TakeNext(out _);
        Equal(run.AfterCommand(true, out note), LinePasteStep.EndSilently, "when the last command ends the paste is over, failed or not");
        Equal(note, null, "with no note");
        run = Make(true, "a");
        run.TakeNext(out _);
        run.Cancel();
        Equal(run.AfterCommand(false, out note), LinePasteStep.EndSilently, "a cancel after the last command has nothing to say either");
    }

    private static void CancelChecks()
    {
        LinePasteRun run = Make(true, "a", "b");
        Check(run.Cancel(), "the first cancel takes effect");
        Check(run.Cancelled, "the paste is cancelled");
        Check(!run.Cancel(), "a second cancel does not");
        Check(run.IsOver(false), "a cancelled paste is over at the next prompt");
        Equal(run.Pending.Count, 2, "the pending commands are kept (they are counted in the note)");
    }

    private static void Main()
    {
        StateChecks();
        NoteChecks();
        CancelChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " line paste checks passed.");
    }
}
