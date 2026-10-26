using System;
using System.Collections.Generic;
using System.Text;

namespace SmarTTY.Terminal;

/// <summary>
/// The part of POSIX shell syntax the Smart Terminal needs: it edits the command line locally, so it has to find
/// the word under the caret, tell a command name from an argument, quote completed paths and recognise the few
/// commands (cd, export, unset, ...) whose effect it keeps itself because every command runs in a new channel.
/// </summary>
internal static class ShellCommandLine
{
    public struct Token
    {
        public int Start;

        public int End;

        public string Text;

        /// <summary>A run of the operator characters | &amp; ; &lt; &gt; ( ) outside quotes.</summary>
        public bool IsOperator;

        public bool IsRedirection => IsOperator && Text.IndexOfAny(new char[] { '<', '>' }) != -1;

        /// <summary>An operator that ends a simple command (|, ||, &amp;&amp;, ;, &amp;, ( and the like).</summary>
        public bool IsSeparator => IsOperator && !IsRedirection;

        public override string ToString()
        {
            return Text;
        }
    }

    public struct CompletionContext
    {
        public CompletionTarget Target;

        /// <summary>The word to replace; empty (Start == End) when the caret is not inside a word.</summary>
        public int TokenStart;

        public int TokenEnd;

        /// <summary>Where the command of the simple command under the caret starts (for option lookup).</summary>
        public int CommandStart;

        public string CommandName;

        /// <summary>
        /// For <see cref="CompletionTarget.Variable"/> and <see cref="CompletionTarget.Host"/>: the text kept in front of
        /// the completed name ("$", "${", "root@"); the popup is filtered by what follows it.
        /// </summary>
        public string InsertPrefix;

        /// <summary>For "${NAME}": the closing brace added after the completed name.</summary>
        public string InsertSuffix;
    }

    private const string OperatorChars = "|&;<>()";

    // Characters that lose their special meaning only when escaped in an unquoted word.
    private const string SpecialChars = " \t'\"\\$`&;|<>()*?[]{}!#";

    private static readonly HashSet<string> PrefixCommands = new HashSet<string>
    {
        "sudo", "time", "nohup", "exec", "command", "nice", "xargs", "watch", "env", "builtin", "!"
    };

    private static readonly HashSet<string> DirectoryCommands = new HashSet<string> { "cd", "pushd", "rmdir" };

    // Commands whose arguments are host names (with an optional user@).
    private static readonly HashSet<string> HostCommands = new HashSet<string>
    {
        "ssh", "sftp", "slogin", "mosh", "ssh-copy-id", "ping", "ping6", "telnet", "traceroute", "tracepath", "mtr", "host", "nslookup", "dig", "nc", "ftp"
    };

    // Commands whose arguments are local paths or host:path.
    private static readonly HashSet<string> HostOrPathCommands = new HashSet<string> { "scp", "rsync" };

    public static bool IsOperatorChar(char ch)
    {
        return OperatorChars.IndexOf(ch) != -1;
    }

    /// <summary>
    /// Splits a command line into words and operators. Quotes and backslashes keep their shell meaning, an unterminated
    /// quote runs to the end of the line (the user is still typing it).
    /// </summary>
    public static List<Token> Tokenize(string text)
    {
        List<Token> tokens = new List<Token>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }
        int wordStart = -1;
        int operatorStart = -1;
        bool inSingle = false;
        bool inDouble = false;
        bool escaped = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            bool quotedOrEscaped = escaped || inSingle || inDouble;
            if (!quotedOrEscaped && IsOperatorChar(ch))
            {
                if (wordStart != -1)
                {
                    tokens.Add(MakeToken(text, wordStart, i, isOperator: false));
                    wordStart = -1;
                }
                if (operatorStart == -1)
                {
                    operatorStart = i;
                }
                continue;
            }
            if (operatorStart != -1)
            {
                tokens.Add(MakeToken(text, operatorStart, i, isOperator: true));
                operatorStart = -1;
            }
            if (!quotedOrEscaped && (ch == ' ' || ch == '\t'))
            {
                if (wordStart != -1)
                {
                    tokens.Add(MakeToken(text, wordStart, i, isOperator: false));
                    wordStart = -1;
                }
                continue;
            }
            if (wordStart == -1)
            {
                wordStart = i;
            }
            if (escaped)
            {
                escaped = false;
            }
            else if (inSingle)
            {
                inSingle = ch != '\'';
            }
            else if (ch == '\\')
            {
                escaped = true;
            }
            else if (inDouble)
            {
                inDouble = ch != '"';
            }
            else if (ch == '"')
            {
                inDouble = true;
            }
            else if (ch == '\'')
            {
                inSingle = true;
            }
        }
        if (wordStart != -1)
        {
            tokens.Add(MakeToken(text, wordStart, text.Length, isOperator: false));
        }
        if (operatorStart != -1)
        {
            tokens.Add(MakeToken(text, operatorStart, text.Length, isOperator: true));
        }
        return tokens;
    }

    private static Token MakeToken(string text, int start, int end, bool isOperator)
    {
        return new Token
        {
            Start = start,
            End = end,
            Text = text.Substring(start, end - start),
            IsOperator = isOperator
        };
    }

    /// <summary>True if the line contains |, ||, &amp;&amp;, ; or &amp; outside quotes, i.e. more than one simple command.</summary>
    public static bool HasSeparators(List<Token> tokens)
    {
        foreach (Token token in tokens)
        {
            if (token.IsSeparator)
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsLetter(name[0]) || name[0] == '_') || name[0] > 127)
        {
            return false;
        }
        foreach (char ch in name)
        {
            if (ch > 127 || !(char.IsLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>NAME=value, the form of a variable assignment in front of a command.</summary>
    public static bool IsAssignment(string word)
    {
        int equalsIndex = word?.IndexOf('=') ?? -1;
        return equalsIndex > 0 && IsValidName(word.Substring(0, equalsIndex));
    }

    /// <summary>
    /// Finds out what the word under the caret is: a command name (at the start of a simple command, after a
    /// separator, sudo or an assignment), a directory (an argument of cd) or any other path.
    /// </summary>
    public static CompletionContext Analyze(string text, int caretIndex)
    {
        text = text ?? "";
        caretIndex = Math.Max(0, Math.Min(caretIndex, text.Length));
        List<Token> tokens = Tokenize(text);
        int tokenIndex = tokens.Count;
        int tokenStart = caretIndex;
        int tokenEnd = caretIndex;
        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];
            if (!token.IsOperator && token.Start <= caretIndex && caretIndex <= token.End)
            {
                tokenIndex = i;
                tokenStart = token.Start;
                tokenEnd = token.End;
                break;
            }
            if (token.Start >= caretIndex)
            {
                tokenIndex = i;
                break;
            }
        }
        bool expectCommand = true;
        bool afterRedirection = false;
        int commandIndex = -1;
        for (int i = 0; i < tokenIndex; i++)
        {
            Token token = tokens[i];
            if (token.IsOperator)
            {
                if (token.IsRedirection)
                {
                    afterRedirection = true;
                }
                else
                {
                    expectCommand = true;
                    afterRedirection = false;
                    commandIndex = -1;
                }
                continue;
            }
            if (afterRedirection)
            {
                afterRedirection = false;
                continue;
            }
            if (!expectCommand)
            {
                continue;
            }
            if (IsAssignment(token.Text))
            {
                continue;
            }
            commandIndex = i;
            if (!PrefixCommands.Contains(token.Text))
            {
                expectCommand = false;
            }
        }
        CompletionContext context = new CompletionContext
        {
            TokenStart = tokenStart,
            TokenEnd = tokenEnd,
            CommandStart = (commandIndex >= 0 && !expectCommand) ? tokens[commandIndex].Start : tokenStart,
            CommandName = (commandIndex >= 0 && !expectCommand) ? tokens[commandIndex].Text : null
        };
        if (TryGetVariableContext(text, tokenStart, caretIndex, out int variableStart, out int variableEnd, out bool braced))
        {
            // "$HO" or "${HO" anywhere in a word (not in single quotes): a variable name.
            context.Target = CompletionTarget.Variable;
            context.TokenStart = variableStart;
            context.TokenEnd = variableEnd;
            context.InsertPrefix = braced ? "${" : "$";
            context.InsertSuffix = braced ? "}" : "";
            return context;
        }
        string word = text.Substring(tokenStart, tokenEnd - tokenStart);
        if (afterRedirection)
        {
            context.Target = CompletionTarget.Path;
        }
        else if (expectCommand)
        {
            context.Target = CompletionTarget.Command;
        }
        else if (DirectoryCommands.Contains(context.CommandName))
        {
            context.Target = CompletionTarget.Directory;
        }
        else if (!word.StartsWith("-", StringComparison.Ordinal) && HostCommands.Contains(context.CommandName))
        {
            context.Target = CompletionTarget.Host;
            context.InsertPrefix = HostUserPrefix(word);
        }
        else if (!word.StartsWith("-", StringComparison.Ordinal) && HostOrPathCommands.Contains(context.CommandName) && word.IndexOf(':') == -1)
        {
            // "user@ho" can only be a host; a word with '/' only a path; anything else may be either.
            if (word.IndexOf('@') != -1 && word.IndexOf('/') == -1)
            {
                context.Target = CompletionTarget.Host;
                context.InsertPrefix = HostUserPrefix(word);
            }
            else
            {
                context.Target = word.IndexOf('/') == -1 && !word.StartsWith(".", StringComparison.Ordinal) && !word.StartsWith("~", StringComparison.Ordinal) ? CompletionTarget.HostOrPath : CompletionTarget.Path;
            }
        }
        else
        {
            context.Target = CompletionTarget.Path;
        }
        return context;
    }

    // "root@" of "root@ho"; "" for a word without a user.
    private static string HostUserPrefix(string word)
    {
        int at = word.LastIndexOf('@');
        return at == -1 ? "" : word.Substring(0, at + 1);
    }

    /// <summary>
    /// Finds a variable reference that ends at the caret ("$HO", "${HO"), outside single quotes and not escaped.
    /// <paramref name="end"/> also covers the rest of the name after the caret (and the closing brace).
    /// </summary>
    private static bool TryGetVariableContext(string text, int wordStart, int caretIndex, out int start, out int end, out bool braced)
    {
        start = end = -1;
        braced = false;
        int nameStart = caretIndex;
        while (nameStart > wordStart && IsNameChar(text[nameStart - 1]))
        {
            nameStart--;
        }
        if (nameStart > wordStart && text[nameStart - 1] == '{' && nameStart - 2 >= wordStart && text[nameStart - 2] == '$')
        {
            braced = true;
            start = nameStart - 2;
        }
        else if (nameStart > wordStart && text[nameStart - 1] == '$')
        {
            start = nameStart - 1;
        }
        else
        {
            return false;
        }
        if (nameStart < caretIndex && char.IsDigit(text[nameStart]))
        {
            // $1, $2: positional parameters, not names.
            return false;
        }
        // Quoting state of the word up to the '$': a backslash or single quotes make it literal.
        bool inSingle = false;
        bool inDouble = false;
        bool escaped = false;
        for (int i = wordStart; i < start; i++)
        {
            char ch = text[i];
            if (escaped)
            {
                escaped = false;
            }
            else if (inSingle)
            {
                inSingle = ch != '\'';
            }
            else if (ch == '\\')
            {
                escaped = true;
            }
            else if (ch == '"')
            {
                inDouble = !inDouble;
            }
            else if (ch == '\'' && !inDouble)
            {
                inSingle = true;
            }
        }
        if (inSingle || escaped)
        {
            return false;
        }
        end = caretIndex;
        while (end < text.Length && IsNameChar(text[end]))
        {
            end++;
        }
        if (braced && end < text.Length && text[end] == '}')
        {
            end++;
        }
        return true;
    }

    private static bool IsNameChar(char ch)
    {
        return ch < 128 && (char.IsLetterOrDigit(ch) || ch == '_');
    }

    /// <summary>
    /// Removes quotes and backslashes from a (possibly unfinished) word, giving the text the shell would see.
    /// Tilde and variable expansion are not performed.
    /// </summary>
    public static string Unquote(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return word ?? "";
        }
        StringBuilder result = new StringBuilder(word.Length);
        bool inSingle = false;
        bool inDouble = false;
        for (int i = 0; i < word.Length; i++)
        {
            char ch = word[i];
            if (inSingle)
            {
                if (ch == '\'')
                {
                    inSingle = false;
                }
                else
                {
                    result.Append(ch);
                }
            }
            else if (ch == '\\' && i + 1 < word.Length && (!inDouble || "\\\"$`".IndexOf(word[i + 1]) != -1))
            {
                result.Append(word[++i]);
            }
            else if (ch == '\\' && i + 1 == word.Length && !inDouble)
            {
                // A trailing backslash escapes the character that has not been typed yet.
            }
            else if (ch == '"')
            {
                inDouble = !inDouble;
            }
            else if (ch == '\'' && !inDouble)
            {
                inSingle = true;
            }
            else
            {
                result.Append(ch);
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// Escapes a literal path for an unquoted shell word. A leading "~" or "~/" is kept as is so that the shell still
    /// expands it to the home directory.
    /// </summary>
    public static string EscapeWord(string literal)
    {
        if (string.IsNullOrEmpty(literal))
        {
            return literal ?? "";
        }
        StringBuilder result = new StringBuilder(literal.Length + 8);
        int start = 0;
        if (literal[0] == '~' && (literal.Length == 1 || literal[1] == '/'))
        {
            result.Append('~');
            start = 1;
        }
        for (int i = start; i < literal.Length; i++)
        {
            char ch = literal[i];
            if (SpecialChars.IndexOf(ch) != -1 || (ch == '~' && i == 0) || (ch == '=' && i == 0))
            {
                result.Append('\\');
            }
            result.Append(ch);
        }
        return result.ToString();
    }

    /// <summary>Escapes a literal for the inside of a quoted word opened with <paramref name="quote"/>.</summary>
    public static string EscapeInQuotes(string literal, char quote)
    {
        literal = literal ?? "";
        if (quote == '\'')
        {
            return literal.Replace("'", "'\\''");
        }
        return literal.Replace("\\", "\\\\").Replace("$", "\\$").Replace("`", "\\`").Replace("\"", "\\\"");
    }

    /// <summary>Replaces the home directory at the start of <paramref name="path"/> with "~" (only at a path boundary).</summary>
    public static string AbbreviateHome(string path, string home)
    {
        path = path ?? "";
        if (string.IsNullOrEmpty(home) || home == "/")
        {
            return path;
        }
        home = home.TrimEnd('/');
        if (path == home)
        {
            return "~";
        }
        if (path.StartsWith(home + "/", StringComparison.Ordinal))
        {
            return "~" + path.Substring(home.Length);
        }
        return path;
    }

    /// <summary>
    /// Turns pasted text into a single command line: the Smart Terminal edits one line, and joining the lines without
    /// a separator (what used to happen) silently produced a different command. A line continued with a backslash is
    /// joined directly, a line that ends with an operator (|, &amp;, ;) or an opening keyword is joined with a space, other lines
    /// with "; " as a shell would run them one after another. Tabs become spaces.
    /// A command that was only wrapped by the place it was copied from (a long command broken into several lines) must not
    /// be cut into several commands, so a line is joined with a space to the previous one when it continues it: it is
    /// indented deeper than the previous command, starts with an operator (|, &amp;&amp;, &gt;, ...) or an option (-x), or the
    /// previous line leaves a quote open.
    /// </summary>
    public static string JoinPastedLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Split('\n');
        StringBuilder result = new StringBuilder(text.Length);
        bool continued = false;
        // Indent of the line that started the command being built: a wrapped rest is indented deeper than that.
        int commandIndent = 0;
        foreach (string rawLine in lines)
        {
            string line = rawLine;
            if (!continued && line.Trim().Length == 0)
            {
                continue;
            }
            if (result.Length > 0 && !continued)
            {
                string previous = result.ToString().TrimEnd();
                string trimmed = line.TrimStart();
                int indent = line.Length - trimmed.Length;
                bool wrapped = indent > commandIndent || StartsWithContinuation(trimmed) || HasOpenQuote(previous);
                result.Length = previous.Length;
                result.Append(wrapped || EndsWithJoiningOperator(previous) ? " " : "; ");
                if (!wrapped)
                {
                    commandIndent = indent;
                }
                line = trimmed;
            }
            else if (result.Length == 0)
            {
                line = line.TrimStart();
                commandIndent = rawLine.Length - line.Length;
            }
            continued = false;
            if (EndsWithContinuation(line))
            {
                line = line.Substring(0, line.Length - 1);
                continued = true;
            }
            result.Append(line);
        }
        return result.ToString().TrimEnd();
    }

    // A line that cannot start a command, so it goes on with the previous one: "| grep x", "&& make", "> file", "-v".
    private static bool StartsWithContinuation(string line)
    {
        if (line.Length == 0)
        {
            return false;
        }
        char first = line[0];
        if (first == '|' || first == '&' || first == ';' || first == '<' || first == '>' || first == ')')
        {
            return true;
        }
        return first == '-' && line.Length > 1 && !char.IsWhiteSpace(line[1]);
    }

    // True when a ' or " opened in the text is not closed yet (an escaped quote does not count).
    private static bool HasOpenQuote(string text)
    {
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quote == '\'')
            {
                if (ch == '\'')
                {
                    quote = '\0';
                }
            }
            else if (ch == '\\')
            {
                i++;
            }
            else if (quote == '"')
            {
                if (ch == '"')
                {
                    quote = '\0';
                }
            }
            else if (ch == '\'' || ch == '"')
            {
                quote = ch;
            }
        }
        return quote != '\0';
    }

    private static bool EndsWithContinuation(string line)
    {
        int backslashes = 0;
        for (int i = line.Length - 1; i >= 0 && line[i] == '\\'; i--)
        {
            backslashes++;
        }
        return backslashes % 2 == 1;
    }

    private static bool EndsWithJoiningOperator(string line)
    {
        if (line.Length == 0)
        {
            return true;
        }
        // "a &" followed by "; b" would be a syntax error, so a trailing & joins with a space as well.
        char lastChar = line[line.Length - 1];
        if (lastChar == '|' || lastChar == '&' || lastChar == ';' || lastChar == '(' || lastChar == '{')
        {
            return true;
        }
        List<Token> tokens = Tokenize(line);
        if (tokens.Count == 0)
        {
            return false;
        }
        string last = tokens[tokens.Count - 1].Text;
        return last == "then" || last == "do" || last == "else" || last == "in";
    }

    /// <summary>
    /// Parses the arguments of "export" or "unset" when all of them are plain assignments (or names), so that the
    /// Smart Terminal can keep them for later commands. Returns false for anything else (options such as -p, invalid
    /// names), which is then run by the remote shell. A null value means "unset".
    /// </summary>
    public static bool TryParseVariableCommand(string commandLine, out List<KeyValuePair<string, string>> assignments)
    {
        assignments = null;
        List<Token> tokens = Tokenize(commandLine);
        if (tokens.Count < 2 || tokens[0].IsOperator || (tokens[0].Text != "export" && tokens[0].Text != "unset"))
        {
            return false;
        }
        bool isUnset = tokens[0].Text == "unset";
        List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
        for (int i = 1; i < tokens.Count; i++)
        {
            Token token = tokens[i];
            if (token.IsOperator)
            {
                return false;
            }
            if (isUnset)
            {
                if (!IsValidName(token.Text))
                {
                    return false;
                }
                result.Add(new KeyValuePair<string, string>(token.Text, null));
                continue;
            }
            int equalsIndex = token.Text.IndexOf('=');
            if (equalsIndex == -1)
            {
                // "export NAME" of a variable that the next commands would not have anyway: nothing to keep.
                if (!IsValidName(token.Text))
                {
                    return false;
                }
                continue;
            }
            string name = token.Text.Substring(0, equalsIndex);
            if (!IsValidName(name))
            {
                return false;
            }
            // The value is kept as typed (quotes, $VAR) and evaluated by the remote shell before every command.
            result.Add(new KeyValuePair<string, string>(name, token.Text.Substring(equalsIndex + 1)));
        }
        assignments = result;
        return true;
    }

    /// <summary>
    /// Extracts a command from a line of a history file: skips the "#1700000000" time stamps bash writes with
    /// HISTTIMEFORMAT and strips the ": 1700000000:0;" prefix of zsh's extended history. Returns null for lines
    /// that are not commands.
    /// </summary>
    public static string ParseHistoryLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }
        if (line.Length > 1 && line[0] == '#' && IsAllDigits(line, 1, line.Length))
        {
            return null;
        }
        if (line.StartsWith(": ", StringComparison.Ordinal))
        {
            int semicolon = line.IndexOf(';');
            int colon = line.IndexOf(':', 2);
            if (semicolon > 2 && colon > 2 && colon < semicolon && IsAllDigits(line, 2, colon) && IsAllDigits(line, colon + 1, semicolon))
            {
                line = line.Substring(semicolon + 1);
                return string.IsNullOrWhiteSpace(line) ? null : line;
            }
        }
        return line;
    }

    private static bool IsAllDigits(string text, int start, int end)
    {
        if (end <= start)
        {
            return false;
        }
        for (int i = start; i < end; i++)
        {
            if (text[i] < '0' || text[i] > '9')
            {
                return false;
            }
        }
        return true;
    }
}
