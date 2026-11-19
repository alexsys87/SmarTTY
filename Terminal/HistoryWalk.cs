using System;
using System.Collections.Generic;

namespace SmarTTY.Terminal;

/// <summary>
/// Up and Down through the history of a command line that is edited in place (the Smart Terminal): the line as it was typed is entry 0, the commands
/// follow, the newest first. The edits made on the way are kept in the entries, so that walking back shows them again, and the walk starts
/// again from the current history when <see cref="Reset"/> is called (a command was added or removed).
/// </summary>
public sealed class HistoryWalk
{
    private List<string> _history;

    private int _historyIndex;

    /// <summary>Starts the walk again at the next Up or Down.</summary>
    public void Reset()
    {
        _history = null;
        _historyIndex = 0;
    }

    /// <summary>
    /// Up (<paramref name="older"/>) or Down: the line to show, or null when the line stays. With <paramref name="byPrefix"/> only the commands that
    /// start with the line as it was typed are shown (as history-search-backward in bash); on an empty line that is every command.
    /// <paramref name="buildHistory"/> makes the commands, newest first, when the walk starts (it must make a list of its own: the line as it was typed
    /// is put in front of it).
    /// </summary>
    public string Step(string typed, bool older, bool byPrefix, Func<List<string>> buildHistory)
    {
        string text = typed ?? "";
        if (_history == null)
        {
            _history = buildHistory();
            _history.Remove(text);
            _history.Insert(0, text);
            _historyIndex = 0;
        }
        else if (_historyIndex >= 0 && _historyIndex < _history.Count && (_historyIndex == 0 || text != ""))
        {
            // Edits of the line being typed (entry 0) and of recalled commands are kept while navigating.
            _history[_historyIndex] = text;
        }
        // Like bash, Up stops at the oldest command and Down at the line being typed: wrapping around used to
        // replace the typed line with the oldest command when Down was pressed at the prompt.
        int newIndex = HistorySuggestion.StepByPrefix(_history, _historyIndex, older, byPrefix ? _history[0] : "");
        if (newIndex < 0)
        {
            return null;
        }
        _historyIndex = newIndex;
        return _history[_historyIndex];
    }
}
