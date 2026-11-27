using System;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

/// <summary>A part of a line of a code block of the Markdown preview drawn in the colour of a token type (0: the plain text).</summary>
public readonly struct MarkdownCodeRun
{
    public MarkdownCodeRun(int start, int end, int type)
    {
        Start = start;
        End = end;
        Type = type;
    }

    /// <summary>The first character of the part in its line.</summary>
    public int Start { get; }

    /// <summary>The character after the part.</summary>
    public int End { get; }

    /// <summary>The token type, an index of the colours of the syntax (see <see cref="SyntaxColoring.ColorIndex"/>).</summary>
    public int Type { get; }
}

/// <summary>
/// The syntax colours of a fenced code block of a Markdown document: the language its info string names (<c>```bash</c>, <c>```python</c>,
/// <c>```json</c>) and the parts of its lines that the tokenizers of the editor colour.
/// </summary>
public static class MarkdownCodeColoring
{
    // The names a code fence gives a language that are not an extension the syntax detection knows.
    private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "bash", "sh" }, { "shell", "sh" }, { "zsh", "sh" }, { "ksh", "sh" }, { "ash", "sh" }, { "console", "sh" }, { "shell-session", "sh" },
        { "sh-session", "sh" }, { "terminal", "sh" }, { "python", "py" }, { "python3", "py" }, { "py3", "py" }, { "javascript", "js" },
        { "node", "js" }, { "typescript", "ts" }, { "csharp", "cs" }, { "c#", "cs" }, { "c++", "cpp" }, { "golang", "go" }, { "rust", "rs" },
        { "ruby", "rb" }, { "perl", "pl" }, { "powershell", "ps1" }, { "pwsh", "ps1" }, { "kotlin", "kt" }, { "batch", "bat" },
        { "patch", "diff" }, { "jsonc", "json" }, { "make", "makefile" }, { "docker", "dockerfile" }, { "dosini", "ini" }, { "systemd", "service" },
        { "markdown", "md" }, { "terraform", "tf" }
    };

    /// <summary>The language of a code block by the first word of its info string; Plain when it names none the editor knows.</summary>
    public static SyntaxHighlighting.Language LanguageOf(string info)
    {
        // ```{.python} and ```.py
        string word = (info ?? "").Trim().TrimStart('{', '.');
        int blank = word.IndexOfAny(new[] { ' ', '\t', '{', '}', ',' });
        if (blank >= 0)
        {
            word = word.Substring(0, blank);
        }
        if (word.Length == 0 || word.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            return SyntaxHighlighting.Language.Plain;
        }
        if (Aliases.TryGetValue(word, out string extension))
        {
            word = extension;
        }
        SyntaxHighlighting.Language language = SyntaxHighlighting.DetectLanguage("code." + word.ToLowerInvariant());
        if (language == SyntaxHighlighting.Language.Plain)
        {
            // A file name: Dockerfile, Makefile, CMakeLists.txt.
            language = SyntaxHighlighting.DetectLanguage(word);
        }
        return language;
    }

    /// <summary>
    /// The coloured parts of each line of a code block (its lines are separated by LF): the tokens of the tokenizer from the start of the
    /// block, in order and without overlaps, with the gaps between them in the plain colour; a line of no text has no part.
    /// </summary>
    public static List<MarkdownCodeRun>[] Color(string code, TokenCache.LineTokenizer tokenizer)
    {
        string[] lines = (code ?? "").Split('\n');
        List<MarkdownCodeRun>[] result = new List<MarkdownCodeRun>[lines.Length];
        int state = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            Sink sink = new Sink(i, i == lines.Length - 1);
            if (tokenizer != null)
            {
                tokenizer(state, line, sink, out state);
            }
            result[i] = Runs(line.Length, sink.Tokens);
        }
        return result;
    }

    private static List<MarkdownCodeRun> Runs(int length, List<MarkdownCodeRun> tokens)
    {
        tokens.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        List<MarkdownCodeRun> runs = new List<MarkdownCodeRun>();
        int pos = 0;
        foreach (MarkdownCodeRun token in tokens)
        {
            int start = Math.Max(token.Start, pos);
            int end = Math.Min(token.End, length);
            if (end <= start)
            {
                continue;
            }
            if (start > pos)
            {
                runs.Add(new MarkdownCodeRun(pos, start, 0));
            }
            runs.Add(new MarkdownCodeRun(start, end, token.Type));
            pos = end;
        }
        if (pos < length)
        {
            runs.Add(new MarkdownCodeRun(pos, length, 0));
        }
        return runs;
    }

    private sealed class Sink : TokenCache.ITokenSink
    {
        public readonly List<MarkdownCodeRun> Tokens = new List<MarkdownCodeRun>();

        public Sink(int line, bool last)
        {
            CurrentLine = line;
            IsLastLine = last;
        }

        public int CurrentLine { get; }

        public bool IsLastLine { get; }

        public void AddToken(int type, int start, int end)
        {
            Tokens.Add(new MarkdownCodeRun(start, end, type));
        }

        public void AddPairedToken(int type, int start, int end, int pairId, bool isOpening, TokenCache.RegionStyle regionStyle)
        {
            Tokens.Add(new MarkdownCodeRun(start, end, type));
        }
    }
}
