using System;
using System.Collections.Generic;
using System.Linq;

namespace SmarTTY.Terminal;

/// <summary>
/// The commands that were typed in the input line of a terminal that edits its line locally: the list (the oldest first, at most
/// <see cref="MaxSize"/>, a command that repeats the last one is not added), Up and Down through it, and the search of Ctrl+R. Each of the
/// two ways of finding a command keeps its place between key presses and starts again when the list changes.
/// </summary>
public sealed class LineHistory
{
    /// <summary>The most commands kept.</summary>
    public const int MaxSize = 1000;

    private readonly List<string> _items = new List<string>();

    // Up/Down: the line as it was typed followed by the history, newest first, built at the first Up (null until then: a new line,
    // a command added), and the entry shown.
    private List<string> _navigationEntries;

    private int _navigationIndex;

    // Changes with every change of the history, so the newest-first copy for the grey suggestion is built again.
    private int _version;

    private List<string> _newestFirst;

    private int _newestFirstVersion = -1;

    private HistorySearchSession _search;

    private sealed class HistorySearchSession
    {
        public readonly List<string> Matches;

        public int CurrentIndex;

        public readonly string OriginalInput;

        public HistorySearchSession(List<string> history, string searchTerm, string originalInput)
        {
            OriginalInput = originalInput;
            Matches = history
                .Where(cmd => cmd.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                .Reverse()
                .ToList();
            CurrentIndex = -1;
        }

        public string GetNextMatch()
        {
            if (Matches.Count == 0) return null;
            CurrentIndex = (CurrentIndex + 1) % Matches.Count;
            return Matches[CurrentIndex];
        }
    }

    /// <summary>The commands, the oldest first.</summary>
    public IReadOnlyList<string> Items => _items.AsReadOnly();

    /// <summary>The number of commands.</summary>
    public int Count => _items.Count;

    /// <summary>A Ctrl+R search is going on.</summary>
    public bool IsSearching => _search != null;

    /// <summary>What was typed when the search started (null when none is going on).</summary>
    public string SearchOriginalInput => _search?.OriginalInput;

    /// <summary>Adds a command (trimmed). Nothing is added when it is empty or the same as the last one.</summary>
    public void Add(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        string trimmed = command.Trim();
        if (_items.Count > 0 && _items[_items.Count - 1] == trimmed)
            return; // Don't add duplicates

        _items.Add(trimmed);
        if (_items.Count > MaxSize)
        {
            _items.RemoveAt(0);
        }
        _navigationEntries = null;
        _version++;
    }

    /// <summary>Forgets every command (a search that is going on is left as it is).</summary>
    public void Clear()
    {
        _items.Clear();
        _navigationEntries = null;
        _version++;
    }

    /// <summary>Removes the commands. False when there was nothing to remove.</summary>
    public bool Remove(IEnumerable<string> commands)
    {
        HashSet<string> set = new HashSet<string>(commands ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        if (set.Count == 0 || _items.RemoveAll(set.Contains) == 0)
        {
            return false;
        }
        _navigationEntries = null;
        _search = null;
        _version++;
        return true;
    }

    /// <summary>Every command once, the newest first (the "Command history" window).</summary>
    public List<string> Distinct()
    {
        return CommandHistoryEditing.Distinct(Enumerable.Reverse(_items));
    }

    /// <summary>The commands, the newest first (built again only when the history has changed).</summary>
    public List<string> NewestFirst
    {
        get
        {
            if (_newestFirst == null || _newestFirstVersion != _version)
            {
                _newestFirst = Enumerable.Reverse(_items).ToList();
                _newestFirstVersion = _version;
            }
            return _newestFirst;
        }
    }

    /// <summary>Up and Down start again from the line as it is typed then (the line was changed by something else than the history).</summary>
    public void ResetNavigation()
    {
        _navigationEntries = null;
    }

    /// <summary>The Ctrl+R search ends (the next one starts again from what is typed then).</summary>
    public void EndSearch()
    {
        _search = null;
    }

    /// <summary>
    /// Ctrl+R: the newest command that contains <paramref name="currentText"/> (what was typed when the search started), and at every next call the
    /// one before it, round to the newest. Null when no command contains it.
    /// </summary>
    public string NextSearchMatch(string currentText)
    {
        if (_search == null)
        {
            _search = new HistorySearchSession(_items, currentText, currentText);
        }
        return _search.GetNextMatch();
    }

    /// <summary>
    /// Up (<paramref name="older"/>) and Down: the line to show, or null when the line stays. With <paramref name="prefixSearch"/> only the commands that
    /// start with what was typed are shown, as history-search-backward does in bash; Down past the newest one brings the typed line back. It ends a
    /// Ctrl+R search.
    /// </summary>
    public string Navigate(string typed, bool older, bool prefixSearch)
    {
        // Cancel any active history search
        _search = null;

        if (_navigationEntries == null)
        {
            if (!older)
            {
                // Down on the line being typed: there is nothing newer.
                return null;
            }
            _navigationEntries = BuildNavigationEntries(typed);
            _navigationIndex = 0;
        }
        else if (_navigationIndex == 0)
        {
            // The typed line may have been edited since Down brought it back.
            _navigationEntries[0] = typed;
        }
        string prefix = prefixSearch ? _navigationEntries[0] : "";
        int newIndex = HistorySuggestion.StepByPrefix(_navigationEntries, _navigationIndex, older, prefix);
        if (newIndex < 0) return null;
        _navigationIndex = newIndex;
        return _navigationEntries[newIndex];
    }

    // The typed line, then every command of the history once, newest first.
    private List<string> BuildNavigationEntries(string typed)
    {
        typed = typed ?? "";
        List<string> entries = new List<string> { typed };
        HashSet<string> seen = new HashSet<string> { typed };
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (seen.Add(_items[i]))
            {
                entries.Add(_items[i]);
            }
        }
        return entries;
    }
}
