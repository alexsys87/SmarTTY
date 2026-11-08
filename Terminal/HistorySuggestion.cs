using System;
using System.Collections.Generic;

namespace SmarTTY.Terminal;

/// <summary>
/// The grey suggestion of the Smart Terminal command line (as in fish): the rest of the newest command in the history that
/// starts with what has been typed. Right or End at the end of the line take all of it, Ctrl+Right takes the next word.
/// </summary>
internal static class HistorySuggestion
{
    /// <summary>
    /// The part of the newest matching command that follows <paramref name="typed"/>, or null when nothing matches. The match
    /// is case-sensitive, as in the shell. Commands of several lines or with control characters (a tab, an escape) are never
    /// suggested: they would not fit on the line or would be drawn wrongly.
    /// </summary>
    public static string Find(IReadOnlyList<string> newestFirst, string typed)
    {
        if (newestFirst == null || string.IsNullOrEmpty(typed) || typed.Trim().Length == 0)
        {
            return null;
        }
        foreach (string command in newestFirst)
        {
            if (command != null && command.Length > typed.Length && command.StartsWith(typed, StringComparison.Ordinal) && !HasControlCharacters(command))
            {
                return command.Substring(typed.Length);
            }
        }
        return null;
    }

    /// <summary>
    /// The beginning of <paramref name="suggestion"/> that Ctrl+Right takes: the spaces in front of the next word and the
    /// word itself ("checkout main" gives "checkout", " -la /tmp" gives " -la").
    /// </summary>
    public static string NextWord(string suggestion)
    {
        if (string.IsNullOrEmpty(suggestion))
        {
            return "";
        }
        int index = 0;
        while (index < suggestion.Length && suggestion[index] == ' ')
        {
            index++;
        }
        while (index < suggestion.Length && suggestion[index] != ' ')
        {
            index++;
        }
        return suggestion.Substring(0, index);
    }

    /// <summary>How much of the suggestion fits on the screen line when the typed text ends at <paramref name="column"/>.</summary>
    public static string FitToWidth(string suggestion, int column, int visibleColumns)
    {
        if (string.IsNullOrEmpty(suggestion) || column < 0 || column >= visibleColumns)
        {
            return "";
        }
        int room = visibleColumns - column;
        return suggestion.Length <= room ? suggestion : suggestion.Substring(0, room);
    }

    private static bool HasControlCharacters(string text)
    {
        foreach (char ch in text)
        {
            if (ch < ' ' || ch == '\u007f')
            {
                return true;
            }
        }
        return false;
    }
}
