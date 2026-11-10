using System;

namespace SmarTTY.Utilities;

/// <summary>
/// The addresses that a click opens in the browser: in the device terminals (COM, Telnet, Raw TCP), in the Smart Terminal and the
/// classic SSH tabs, and with Ctrl+click in the editor. One set of rules for all of them: which schemes are opened, where an address
/// ends, and which characters at its end belong to the sentence around it rather than to the address. Free of any UI dependency,
/// so the test harnesses check it without a window.
/// </summary>
public static class UrlLinks
{
    private static readonly string[] OpenedSchemes = { "http://", "https://", "ftp://" };

    /// <summary>True for an http, https or ftp address: the ones that a click hands to the program Windows has for them.</summary>
    public static bool IsOpenable(string text)
    {
        if (text == null)
        {
            return false;
        }
        foreach (string scheme in OpenedSchemes)
        {
            if (text.Length > scheme.Length && text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="text"/> starts with the scheme of an opened address and its "://" ("https://"), even with nothing
    /// after it yet: from there on, the characters of an address are read as such (see <see cref="EndsUrl"/>).
    /// </summary>
    public static bool StartsWithOpenableScheme(string text)
    {
        if (text == null)
        {
            return false;
        }
        foreach (string scheme in OpenedSchemes)
        {
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// No address holds a blank or a control character, and a quote, a backtick or an angle bracket is not written in one either: it
    /// ends an address that was put in quotes or in "&lt;...&gt;". A closing ")" or "]" can be in an address, so it does not end it
    /// (<see cref="Trim"/> takes it off when it closes a bracket that was opened before the address).
    /// </summary>
    public static bool EndsUrl(char ch)
    {
        return char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '"' || ch == '\'' || ch == '<' || ch == '>' || ch == '`';
    }

    /// <summary>
    /// The address without what belongs to the sentence around it: a full stop, a comma, a colon and the like at its end ("see
    /// https://example.com/a."), and a ")" or "]" that closes a bracket opened before the address ("(https://example.com/a)"),
    /// while "https://en.wikipedia.org/wiki/C_(language)" keeps its own.
    /// </summary>
    public static string Trim(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }
        int end = url.Length;
        while (end > 0)
        {
            char last = url[end - 1];
            if (".,;:!?".IndexOf(last) >= 0)
            {
                end--;
            }
            else if ((last == ')' && Count(url, end, '(') < Count(url, end, ')'))
                || (last == ']' && Count(url, end, '[') < Count(url, end, ']'))
                || (last == '}' && Count(url, end, '{') < Count(url, end, '}')))
            {
                end--;
            }
            else
            {
                break;
            }
        }
        return url.Substring(0, end);
    }

    /// <summary>
    /// The opened address (<see cref="IsOpenable"/>) in <paramref name="line"/> that covers the character at <paramref name="column"/>,
    /// already trimmed (<see cref="Trim"/>), or null; <paramref name="start"/> is where it starts. For the editor, which looks at
    /// the line under the mouse pointer when Ctrl is held.
    /// </summary>
    public static string FindAt(string line, int column, out int start)
    {
        start = -1;
        if (string.IsNullOrEmpty(line) || column < 0 || column >= line.Length)
        {
            return null;
        }
        int from = 0;
        while (from < line.Length)
        {
            int found = IndexOfScheme(line, from);
            if (found < 0 || found > column)
            {
                return null;
            }
            int end = found;
            while (end < line.Length && !EndsUrl(line[end]))
            {
                end++;
            }
            string url = Trim(line.Substring(found, end - found));
            if (column < found + url.Length && IsOpenable(url))
            {
                start = found;
                return url;
            }
            from = Math.Max(end, found + 1);
        }
        return null;
    }

    // The first place at or after "from" where an opened scheme starts and is not the tail of a longer word ("xhttp://").
    private static int IndexOfScheme(string line, int from)
    {
        int best = -1;
        foreach (string scheme in OpenedSchemes)
        {
            int index = from;
            while ((index = line.IndexOf(scheme, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (index == 0 || !char.IsLetterOrDigit(line[index - 1]))
                {
                    break;
                }
                index++;
            }
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
            }
        }
        return best;
    }

    private static int Count(string text, int length, char ch)
    {
        int count = 0;
        for (int i = 0; i < length; i++)
        {
            if (text[i] == ch)
            {
                count++;
            }
        }
        return count;
    }
}
