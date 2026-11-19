using System;
using System.Collections.Generic;

namespace SmarTTY.Terminal;

/// <summary>
/// The grey suggestion of the input line of a serial or TCP terminal (the rest of a command typed before, drawn after the caret): the suggestion
/// that is shown, the text that Esc hid it for, and the rules that decide when one is offered. The control reads the view (the selection, the
/// caret, the width) and passes it in; it keeps the repaint.
/// </summary>
internal sealed class HistorySuggestionState
{
    /// <summary>The suggestion that is shown (the rest of a command); null when none is shown.</summary>
    public string Current { get; private set; }

    /// <summary>Esc hides the suggestion until the line changes: the text it was hidden for (null when none).</summary>
    public string DismissedFor { get; private set; }

    /// <summary>Nothing is shown and nothing is hidden: a new input line starts.</summary>
    public void Clear()
    {
        Current = null;
        DismissedFor = null;
    }

    /// <summary>Hides the suggestion for the line as it is typed now.</summary>
    public void Dismiss(string text)
    {
        DismissedFor = text;
    }

    /// <summary>
    /// What would be suggested for the line now, or null. <paramref name="caretAtEnd"/> is asked last, only when everything else allows a suggestion
    /// (it reads the caret of the view and may throw while the line is being redrawn: the caller handles that).
    /// </summary>
    public string Compute(bool hasInputLine, bool inputDisabled, bool hasSelection, int visibleColumns, bool enabled, bool secretPrompt,
        string text, Func<bool> caretAtEnd, IReadOnlyList<string> newestFirst)
    {
        // Nothing at "Password:": the suggestion would show other commands on the screen while a secret is typed.
        if (!hasInputLine || inputDisabled || hasSelection || visibleColumns <= 0 || !enabled || secretPrompt)
        {
            return null;
        }
        if (text.Length == 0 || text == DismissedFor)
        {
            return null;
        }
        // The arrows move only the caret on the screen: the suggestion is shown while it is after the last typed character.
        if (!caretAtEnd())
        {
            return null;
        }
        return HistorySuggestion.Find(newestFirst, text);
    }

    /// <summary>Sets the suggestion that is shown; true when it is not the one that was shown (the view paints again).</summary>
    public bool Show(string suggestion)
    {
        if (suggestion == Current)
        {
            return false;
        }
        Current = suggestion;
        return true;
    }
}
