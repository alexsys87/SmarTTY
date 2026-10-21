using System;
using System.Collections.Generic;

namespace SmarTTY.Terminal;

/// <summary>
/// Incremental history search of the Smart Terminal (Ctrl+R), as in bash's reverse-i-search: the typed query is
/// searched in the history from the newest command to the oldest, Ctrl+R again moves to the next older match and
/// Ctrl+S back to a newer one. When nothing matches, the last match stays and the search is marked as failed.
/// </summary>
internal sealed class HistorySearch
{
    // Newest first.
    private readonly IReadOnlyList<string> _history;

    private int _index = -1;

    public string Query { get; private set; } = "";

    public bool Failed { get; private set; }

    /// <summary>The command found so far, or null before anything has been found.</summary>
    public string Match => _index >= 0 ? _history[_index] : null;

    /// <summary>Where the query was found in <see cref="Match"/>, for placing the caret; -1 without a match.</summary>
    public int MatchPosition => (Match != null && Query.Length > 0) ? Match.IndexOf(Query, StringComparison.Ordinal) : -1;

    public HistorySearch(IReadOnlyList<string> newestFirst)
    {
        _history = newestFirst ?? new List<string>();
    }

    /// <summary>The prompt shown while searching, as bash shows it.</summary>
    public string Prompt => (Failed ? "(failed reverse-i-search)`" : "(reverse-i-search)`") + Query + "': ";

    public void Append(char ch)
    {
        SetQuery(Query + ch);
    }

    public void Append(string text)
    {
        SetQuery(Query + text);
    }

    public void RemoveLast()
    {
        if (Query.Length > 0)
        {
            // Like bash, a shorter query searches again from the newest command.
            _index = -1;
            SetQuery(Query.Substring(0, Query.Length - 1));
        }
    }

    /// <summary>The current match still contains a longer query; otherwise the search goes on to older commands.</summary>
    private void SetQuery(string query)
    {
        Query = query;
        if (Query.Length == 0)
        {
            Failed = false;
            return;
        }
        int start = (_index >= 0) ? _index : 0;
        int found = Find(start, older: true);
        Failed = found == -1;
        if (found != -1)
        {
            _index = found;
        }
    }

    /// <summary>Ctrl+R: the next older command that contains the query.</summary>
    public void Older()
    {
        Step(older: true);
    }

    /// <summary>Ctrl+S: the next newer command that contains the query.</summary>
    public void Newer()
    {
        Step(older: false);
    }

    private void Step(bool older)
    {
        if (Query.Length == 0)
        {
            return;
        }
        int start = (_index == -1) ? (older ? 0 : _history.Count - 1) : (_index + (older ? 1 : -1));
        int found = Find(start, older);
        Failed = found == -1;
        if (found != -1)
        {
            _index = found;
        }
    }

    private int Find(int start, bool older)
    {
        for (int index = start; index >= 0 && index < _history.Count; index += older ? 1 : -1)
        {
            if (_history[index] != null && _history[index].IndexOf(Query, StringComparison.Ordinal) != -1)
            {
                return index;
            }
        }
        return -1;
    }
}
