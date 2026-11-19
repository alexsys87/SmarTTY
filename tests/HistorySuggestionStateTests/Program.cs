using System;
using System.Collections.Generic;
using SmarTTY.Terminal;

// Checks of the state of the grey suggestion of a local input line (Terminal/HistorySuggestionState.cs): when one is offered, what hides it,
// and when the view has to paint again.
internal static class Program
{
    private static readonly string[] History = { "uptime", "ls -la /tmp", "uname -a", "ls" };

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

    // The defaults are a line that may show a suggestion; a check changes one thing.
    private static string Compute(HistorySuggestionState state, string text, bool hasInputLine = true, bool inputDisabled = false,
        bool hasSelection = false, int columns = 80, bool enabled = true, bool secret = false, Func<bool> caretAtEnd = null)
    {
        return state.Compute(hasInputLine, inputDisabled, hasSelection, columns, enabled, secret, text, caretAtEnd ?? (() => true), History);
    }

    private static void ComputeChecks()
    {
        HistorySuggestionState state = new HistorySuggestionState();
        Equal(Compute(state, "un"), "ame -a", "the rest of the newest command that starts with the text");
        Equal(Compute(state, "ls"), " -la /tmp", "the newest of two matches, not the exact one");
        Equal(Compute(state, "u"), "ptime", "the newest of two commands that start the same way");
        Equal(Compute(state, ""), null, "nothing is offered for an empty line");
        Equal(Compute(state, "zz"), null, "nothing is offered when nothing matches");
        Equal(Compute(state, "un", hasInputLine: false), null, "nothing without an input line");
        Equal(Compute(state, "un", inputDisabled: true), null, "nothing while the input line is disabled");
        Equal(Compute(state, "un", hasSelection: true), null, "nothing while there is a selection");
        Equal(Compute(state, "un", columns: 0), null, "nothing when the view has no columns");
        Equal(Compute(state, "un", enabled: false), null, "nothing when the suggestions are switched off");
        Equal(Compute(state, "un", secret: true), null, "nothing at a prompt for a secret");
        Equal(Compute(state, "un", caretAtEnd: () => false), null, "nothing when the caret is not after the last typed character");
    }

    private static void CaretIsAskedLastChecks()
    {
        HistorySuggestionState state = new HistorySuggestionState();
        int asked = 0;
        Func<bool> caret = () => { asked++; return true; };
        Compute(state, "", caretAtEnd: caret);
        Compute(state, "un", enabled: false, caretAtEnd: caret);
        Compute(state, "un", secret: true, caretAtEnd: caret);
        Equal(asked, 0, "the caret is not read when an earlier rule says no");
        Compute(state, "un", caretAtEnd: caret);
        Equal(asked, 1, "the caret is read once when the rest allows a suggestion");
        Func<bool> throwing = () => throw new InvalidOperationException("the line is being redrawn");
        bool thrown = false;
        try
        {
            Compute(state, "un", caretAtEnd: throwing);
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }
        Check(thrown, "what the caret read throws reaches the caller, which handles it");
    }

    private static void DismissChecks()
    {
        HistorySuggestionState state = new HistorySuggestionState();
        Check(state.DismissedFor == null, "nothing is hidden at the start");
        state.Dismiss("un");
        Equal(state.DismissedFor, "un", "the text the suggestion was hidden for");
        Equal(Compute(state, "un"), null, "the suggestion stays hidden for that text");
        Equal(Compute(state, "unam"), "e -a", "a changed line shows one again");
        Equal(Compute(state, "un"), null, "and the old text is still hidden");
        state.Clear();
        Check(state.DismissedFor == null && state.Current == null, "a new input line clears both");
        Equal(Compute(state, "un"), "ame -a", "after that the text shows one");
    }

    private static void ShowChecks()
    {
        HistorySuggestionState state = new HistorySuggestionState();
        Check(state.Current == null, "none is shown at the start");
        Check(!state.Show(null), "nothing to paint when none was shown and none is");
        Check(state.Show("ame -a"), "a new suggestion is painted");
        Equal(state.Current, "ame -a", "and it is the shown one");
        Check(!state.Show("ame -a"), "the same suggestion is not painted again");
        Check(state.Show("ptime"), "another one is painted");
        Check(state.Show(null), "hiding the shown one is painted");
        Check(state.Current == null, "and none is shown");
        state.Show("x");
        state.Clear();
        Check(state.Current == null, "Clear drops the shown suggestion");
        Check(!state.Show(null), "without a paint request (the new line is painted by its owner)");
    }

    private static int Main()
    {
        try
        {
            ComputeChecks();
            CaretIsAskedLastChecks();
            DismissChecks();
            ShowChecks();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
        return 0;
    }
}
