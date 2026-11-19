using System;
using System.Collections.Generic;

namespace SmarTTY.Terminal;

/// <summary>
/// What the grey suggestion of the Smart Terminal command line remembers between two keys: the history, newest first, kept until the history of the
/// session or of the tab has changed, and the answer for the last text looked up (the caret moves on every key, and the history does not need to be
/// searched again for the same text).
/// </summary>
internal sealed class SuggestionLookup
{
    private List<string> _history;
    private object _analyzer;
    private int _historyVersion = -1;
    private int _typedCount = -1;
    private string _query;
    private List<string> _queryHistory;
    private string _result;

    /// <summary>
    /// The kept history, or the one <paramref name="build"/> makes when there is none, when it was made for another analyzer, or when the version of the
    /// history of the session or the number of the commands typed in the tab is not the one it was made at.
    /// </summary>
    public List<string> GetHistory(object analyzer, int historyVersion, int typedCount, Func<List<string>> build)
    {
        if (_history == null || analyzer != _analyzer || historyVersion != _historyVersion || typedCount != _typedCount)
        {
            _history = build();
            _analyzer = analyzer;
            _historyVersion = historyVersion;
            _typedCount = typedCount;
        }
        return _history;
    }

    /// <summary>The rest of the newest command of <paramref name="history"/> that starts with <paramref name="text"/> (see <see cref="HistorySuggestion.Find"/>).</summary>
    public string Find(List<string> history, string text)
    {
        if (text != _query || history != _queryHistory)
        {
            _query = text;
            _queryHistory = history;
            _result = HistorySuggestion.Find(history, text);
        }
        return _result;
    }

    /// <summary>The history was changed from outside (commands were removed): it is made again, and the next text is looked up again.</summary>
    public void Invalidate()
    {
        _history = null;
        _query = null;
    }
}
