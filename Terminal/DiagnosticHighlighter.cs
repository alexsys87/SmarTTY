using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SmarTTY.Terminal;

/// <summary>
/// Finds the words in a line of build output that mark an error, a warning or a note: "error:" in
/// "main.c:12:5: error: expected ';'", "undefined reference to", the "Error 2" at the end of a make failure. The terminal
/// colours them when the compiler did not (its own colours always win), so that problems stand out in output that was
/// not written to a terminal: through a pipe, by ninja, by tools that do not colour.
///
/// Only a keyword in the place where the tools put it counts, "file:line:col: error:" or "error:" at the start of the
/// line, so that the same word in a sentence stays as it is.
///
/// The class is free of any UI dependency, so the test harness can check it without a display.
/// </summary>
internal static class DiagnosticHighlighter
{
    public const byte None = 0;

    public const byte Note = 1;

    public const byte Warning = 2;

    public const byte Error = 3;

    public struct Span
    {
        public int Start;

        public int Length;

        public byte Severity;
    }

    // error:, warning:, note:, fatal error:, and rustc's error[E0425]:
    private static readonly Regex Keyword = new Regex(
        @"(?<![\w\-])(?<kw>fatal error|error|warning|note)(?<code>\[[A-Za-z0-9]+\])?:",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // What may stand in front of the keyword: nothing, "= " (cargo), or a program or a file with the position,
    // "gcc: ", "make[1]: ", "main.c:12:5: ".
    private static readonly Regex LeftContext = new Regex(
        @"^\s*(?:=\s+)?(?:[^\s:]+(?::\d+){0,2}:\s+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MakeFailure = new Regex(
        @"\]\s+(?<what>Error\s+\d+)\s*$|^\s*make(?:\[\d+\])?:\s+(?<what>\*\*\*)|^(?<what>FAILED:)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Log levels written in capitals: ERROR, WARN, FATAL, ... A longer word (ERROR_CODE) is not a level.
    private static readonly Regex LogWord = new Regex(
        @"(?<!\w)(?<w>FATAL|CRITICAL|CRIT|SEVERE|EMERG|ALERT|ERROR|ERR|WARNING|WARN|NOTICE)(?!\w)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Log levels written in lower case where a log puts them: nginx's [error], logfmt's level=warn, a syslog tag's "sshd[12]: error".
    private static readonly Regex LogMarked = new Regex(
        @"\[(?<w>emerg|alert|crit|error|err|warn|warning|notice)\]|\blevel=(?<w>error|fatal|panic|warn|warning)\b|(?<=\]: )(?<w>error|warning|warn|fatal|critical)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] ErrorPhrases = { "undefined reference to", "multiple definition of" };

    /// <summary>
    /// The first log level in <paramref name="line"/> (ERROR, WARN, [error], level=warn, ...): where it starts, and how bad it is.
    /// A log line is coloured from there to its end, so the timestamp in front of the level stays as it is.
    /// </summary>
    public static bool TryFindLogLevel(string line, out int index, out byte severity)
    {
        index = -1;
        severity = None;
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }
        Match word = LogWord.Match(line);
        Match marked = LogMarked.Match(line);
        Match first = word.Success && (!marked.Success || word.Index <= marked.Index) ? word : (marked.Success ? marked : null);
        if (first == null)
        {
            return false;
        }
        index = first.Index;
        switch (first.Groups["w"].Value.ToLowerInvariant())
        {
            case "warn":
            case "warning":
                severity = Warning;
                break;
            case "notice":
                severity = Note;
                break;
            default:
                severity = Error;
                break;
        }
        return true;
    }

    /// <summary>The spans of <paramref name="line"/> that mark an error, a warning or a note, in order, without overlap.</summary>
    public static List<Span> FindSpans(string line)
    {
        List<Span> spans = new List<Span>();
        if (string.IsNullOrEmpty(line))
        {
            return spans;
        }
        // Most lines of output are neither: a keyword needs a ':', the other cases a word of their own.
        bool colon = line.IndexOf(':') >= 0;
        if (colon)
        {
            foreach (Match match in Keyword.Matches(line))
            {
                string left = line.Substring(0, match.Index);
                if (!LeftContext.IsMatch(left))
                {
                    continue;
                }
                string keyword = match.Groups["kw"].Value;
                spans.Add(new Span
                {
                    Start = match.Index,
                    Length = match.Length,
                    Severity = keyword == "warning" ? Warning : (keyword == "note" ? Note : Error)
                });
            }
        }
        foreach (string phrase in ErrorPhrases)
        {
            int index = line.IndexOf(phrase, StringComparison.Ordinal);
            if (index >= 0)
            {
                spans.Add(new Span { Start = index, Length = phrase.Length, Severity = Error });
            }
        }
        if (colon || line.IndexOf("***", StringComparison.Ordinal) >= 0)
        {
            foreach (Match failure in MakeFailure.Matches(line))
            {
                Group what = failure.Groups["what"];
                spans.Add(new Span { Start = what.Index, Length = what.Length, Severity = Error });
            }
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        // Two rules never meet on the same word in practice; if they do, the first one stays.
        for (int i = spans.Count - 1; i > 0; i--)
        {
            if (spans[i].Start < spans[i - 1].Start + spans[i - 1].Length)
            {
                spans.RemoveAt(i);
            }
        }
        return spans;
    }
}
