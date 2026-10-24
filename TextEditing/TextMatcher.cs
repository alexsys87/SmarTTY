using System;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.TextEditing;

/// <summary>
/// Finds and replaces text inside one line: a plain text (case sensitive or not), optionally only as a whole word, or a
/// regular expression. The editor searches line by line, so a match never contains a line break. It does not depend on
/// the editor controls, which lets the rules be tested without a window.
/// </summary>
public sealed class TextMatcher
{
    // A regular expression that never ends must not freeze the window.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly string _query;

    private readonly StringComparison _comparison;

    // null: a plain text search that needs no regular expression.
    private readonly Regex _regex;

    // The text typed in the Replace box may use $1, ${name} and $& only when the query is a regular expression.
    private readonly bool _expandGroups;

    private Match _lastMatch;

    public string Query => _query;

    public bool IsRegex => _expandGroups;

    private TextMatcher(string query, StringComparison comparison, Regex regex, bool expandGroups)
    {
        _query = query;
        _comparison = comparison;
        _regex = regex;
        _expandGroups = expandGroups;
    }

    /// <summary>A plain text search; the options of the search bar are all off but case.</summary>
    public static TextMatcher Literal(string query, bool ignoreCase)
    {
        return Create(query, matchCase: !ignoreCase, wholeWord: false, useRegex: false);
    }

    /// <summary>
    /// Creates a matcher. Throws <see cref="ArgumentException"/> (its message says what is wrong) when
    /// <paramref name="useRegex"/> is set and the query is not a valid regular expression, or the query is empty.
    /// </summary>
    public static TextMatcher Create(string query, bool matchCase, bool wholeWord, bool useRegex)
    {
        if (string.IsNullOrEmpty(query))
        {
            throw new ArgumentException("Nothing to search for.", nameof(query));
        }
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!useRegex && !wholeWord)
        {
            return new TextMatcher(query, comparison, null, expandGroups: false);
        }
        string pattern = useRegex ? query : Regex.Escape(query);
        if (wholeWord)
        {
            // Not "\b": the query may begin or end with a character that is not a letter or a digit.
            pattern = @"(?<![\p{L}\p{N}_])(?:" + pattern + @")(?![\p{L}\p{N}_])";
        }
        RegexOptions options = RegexOptions.CultureInvariant;
        if (!matchCase)
        {
            options |= RegexOptions.IgnoreCase;
        }
        try
        {
            return new TextMatcher(query, comparison, new Regex(pattern, options, RegexTimeout), expandGroups: useRegex);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(exception.Message, nameof(query), exception);
        }
    }

    /// <summary>
    /// Finds a match that lies inside <c>[from, to]</c> of <paramref name="text"/>: the first one when searching forward,
    /// the last one when searching backward. Empty matches (such as the regular expression <c>^</c>) are returned only
    /// when <paramref name="allowEmpty"/> is set.
    /// </summary>
    public bool Match(string text, int from, int to, bool backward, bool allowEmpty, out int index, out int length)
    {
        index = -1;
        length = 0;
        if (text == null)
        {
            return false;
        }
        from = Math.Max(0, from);
        to = Math.Min(text.Length, to);
        if (from > to)
        {
            return false;
        }
        return backward ? MatchBackward(text, from, to, allowEmpty, out index, out length) : MatchForward(text, from, to, allowEmpty, out index, out length);
    }

    private bool MatchForward(string text, int from, int to, bool allowEmpty, out int index, out int length)
    {
        int position = from;
        while (position <= to && Next(text, position, out index, out length))
        {
            if (index + length > to)
            {
                break;
            }
            if (length > 0 || allowEmpty)
            {
                return true;
            }
            position = index + 1;
        }
        index = -1;
        length = 0;
        return false;
    }

    private bool MatchBackward(string text, int from, int to, bool allowEmpty, out int index, out int length)
    {
        if (_regex == null)
        {
            // The plain text is never empty: look for its last occurrence that ends at or before "to".
            int last = (to - from >= _query.Length) ? text.LastIndexOf(_query, to - 1, to - from, _comparison) : -1;
            if (last >= 0)
            {
                index = last;
                length = _query.Length;
                return true;
            }
            index = -1;
            length = 0;
            return false;
        }
        // A regular expression cannot be run backwards: take the last of the matches that fit.
        int foundIndex = -1;
        int foundLength = 0;
        int position = from;
        while (position <= to && Next(text, position, out int matchIndex, out int matchLength))
        {
            if (matchIndex + matchLength > to)
            {
                break;
            }
            if (matchLength > 0 || allowEmpty)
            {
                foundIndex = matchIndex;
                foundLength = matchLength;
            }
            // Matches do not overlap, as when they are replaced: "333" is one match of \d+, not three.
            position = (matchLength == 0) ? matchIndex + 1 : matchIndex + matchLength;
        }
        if (foundIndex < 0)
        {
            index = -1;
            length = 0;
            return false;
        }
        index = foundIndex;
        length = foundLength;
        Next(text, foundIndex, out _, out _);
        return true;
    }

    // The first match that begins at or after "start", ignoring where it ends.
    private bool Next(string text, int start, out int index, out int length)
    {
        if (_regex == null)
        {
            index = text.IndexOf(_query, start, _comparison);
            length = _query.Length;
            return index >= 0;
        }
        Match match = _regex.Match(text, start);
        _lastMatch = match;
        index = match.Success ? match.Index : -1;
        length = match.Success ? match.Length : 0;
        return match.Success;
    }

    /// <summary>
    /// True when the query matches exactly <c>text[index..end)</c>, which is how a selection is recognised as the current
    /// match before it is replaced. <see cref="Expand"/> then applies to that match.
    /// </summary>
    public bool MatchesExactly(string text, int index, int end)
    {
        return Match(text, index, end, backward: false, allowEmpty: true, out int matchIndex, out int matchLength) && matchIndex == index && matchIndex + matchLength == end;
    }

    /// <summary>
    /// The text that replaces the match found last: the replacement itself, or, for a regular expression, the
    /// replacement with $1, ${name} and $& filled in.
    /// </summary>
    public string Expand(string replacement)
    {
        replacement = replacement ?? "";
        if (_expandGroups && _lastMatch != null && _lastMatch.Success)
        {
            return _lastMatch.Result(replacement);
        }
        return replacement;
    }

    /// <summary>
    /// Replaces every match that lies inside <c>[from, to]</c> of <paramref name="text"/>. Returns the new text, or
    /// <paramref name="text"/> itself when nothing matched; <paramref name="newTo"/> is where <paramref name="to"/> moved.
    /// </summary>
    public string ReplaceAll(string text, int from, int to, string replacement, out int count, out int newTo)
    {
        count = 0;
        newTo = to;
        if (text == null)
        {
            return text;
        }
        from = Math.Max(0, from);
        to = Math.Min(text.Length, to);
        newTo = to;
        if (from > to)
        {
            return text;
        }
        StringBuilder result = null;
        int copied = 0;
        int position = from;
        while (position <= to && Next(text, position, out int index, out int length))
        {
            if (index + length > to)
            {
                break;
            }
            if (result == null)
            {
                result = new StringBuilder(text.Length + 16);
            }
            result.Append(text, copied, index - copied);
            result.Append(Expand(replacement));
            count++;
            copied = index + length;
            position = (length == 0) ? index + 1 : index + length;
        }
        if (count == 0)
        {
            return text;
        }
        result.Append(text, copied, text.Length - copied);
        string replaced = result.ToString();
        newTo = to + (replaced.Length - text.Length);
        return replaced;
    }
}
