using System;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing
{
    /// <summary>
    /// Syntax highlighting for the built-in SmarTTY editor.
    ///
    /// The class provides:
    ///   * token type constants (indices into CodeEditorPP.TokenBrushes);
    ///   * language detection from the file name (and the "#!" line of files without an extension);
    ///   * line tokenizers (TokenCache.LineTokenizer delegates).
    ///
    /// Tokenizers emit color tokens (ITokenSink.AddToken) and, for languages with balanced brackets,
    /// paired bracket tokens without a fold style, so the editor can highlight the matching bracket.
    /// State that spans lines (Python triple quotes, block comments, ...) is kept in the integer
    /// startState/endState that TokenCache stores for every line.
    /// </summary>
    public static class SyntaxHighlighting
    {
        // ------------------------------------------------------------------
        // Token types: indices into TokenBrushes. Index 0 must be the plain text
        // color (used as a fallback when TokenBrushes is empty).
        // ------------------------------------------------------------------
        public const int TypeNormal = 0;        // plain text
        public const int TypeKeyword = 1;       // keywords, tag names
        public const int TypeString = 2;        // strings, removed diff lines
        public const int TypeComment = 3;       // comments, added diff lines
        public const int TypeNumber = 4;        // numbers
        public const int TypeVariable = 5;      // shell variables, configuration keys
        public const int TypeSection = 6;       // [section] headers, YAML document markers, diff hunks
        public const int TypePreprocessor = 7;  // shebang, #include, Python decorators
        public const int TypeCount = 8;         // palette size

        // Pair ids of bracket tokens: TokenCache matches an opening token with a closing token of the same id.
        private const int PairParenthesis = 1;
        private const int PairSquareBracket = 2;
        private const int PairCurlyBrace = 3;

        /// <summary>Supported languages.</summary>
        public enum Language
        {
            Plain,   // no highlighting
            Python,
            Bash,
            Config,  // INI / .conf / .properties / YAML / TOML / systemd units
            CLike,   // C, C++, C#, Java, JavaScript, Go, Rust, CSS, JSON, ...: // and /* */ comments
            Script,  // Makefile, Dockerfile, Ruby, Perl, ...: # comments
            Sql,     // SQL, Lua: -- comments
            Markup,  // HTML, XML: <!-- --> comments and tag names
            Diff     // unified diffs
        }

        /// <summary>Detects the language from the file name (path). Extensions are case insensitive.</summary>
        public static Language DetectLanguage(string remotePath)
        {
            return DetectLanguage(remotePath, null);
        }

        /// <summary>
        /// Detects the language from the file name, falling back to the interpreter named by the "#!"
        /// line for files whose name says nothing (e.g. /usr/local/bin/backup).
        /// </summary>
        public static Language DetectLanguage(string remotePath, string firstLine)
        {
            Language language = DetectLanguageFromName(remotePath);
            if (language == Language.Plain && firstLine != null)
            {
                language = DetectLanguageFromShebang(firstLine);
            }
            return language;
        }

        private static Language DetectLanguageFromName(string remotePath)
        {
            if (string.IsNullOrEmpty(remotePath))
            {
                return Language.Plain;
            }

            string name = remotePath.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }
            if (string.IsNullOrEmpty(name))
            {
                return Language.Plain;
            }
            name = name.ToLowerInvariant();

            // Well-known file names without a meaningful extension.
            switch (name)
            {
                case ".bashrc":
                case ".bash_profile":
                case ".bash_login":
                case ".bash_logout":
                case ".bash_aliases":
                case ".bash_functions":
                case ".profile":
                case ".zshrc":
                case ".zprofile":
                case ".zshenv":
                case ".kshrc":
                case "pkgbuild":
                case "apkbuild":
                    return Language.Bash;

                case "makefile":
                case "gnumakefile":
                case "dockerfile":
                case "containerfile":
                case "cmakelists.txt":
                case "requirements.txt":
                case ".gitignore":
                case ".hgignore":
                case ".dockerignore":
                    return Language.Script;

                case ".gitconfig":
                case ".gitmodules":
                case ".editorconfig":
                case ".npmrc":
                case ".inputrc":
                case ".env":
                case "fstab":
                case "crontab":
                case "hosts":
                case "passwd":
                case "group":
                case "ssh_config":
                case "sshd_config":
                case "config":
                    return Language.Config;
            }

            if (name.StartsWith(".bash", StringComparison.Ordinal) ||
                name.StartsWith(".zsh", StringComparison.Ordinal) ||
                name.StartsWith(".ksh", StringComparison.Ordinal))
            {
                return Language.Bash;
            }

            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1)
            {
                return Language.Plain;
            }

            switch (name.Substring(dot + 1))
            {
                case "py":
                case "pyw":
                case "pyi":
                case "pyx":
                    return Language.Python;

                case "sh":
                case "bash":
                case "zsh":
                case "ksh":
                    return Language.Bash;

                case "ini":
                case "conf":
                case "cfg":
                case "cnf":
                case "config":
                case "properties":
                case "inf":
                case "editorconfig":
                case "yml":
                case "yaml":
                case "toml":
                case "env":
                case "service":
                case "socket":
                case "timer":
                case "mount":
                case "target":
                case "path":
                case "network":
                case "netdev":
                case "link":
                case "desktop":
                case "rules":
                    return Language.Config;

                case "c":
                case "h":
                case "cpp":
                case "hpp":
                case "hh":
                case "cc":
                case "cxx":
                case "ino":
                case "cs":
                case "java":
                case "kt":
                case "scala":
                case "groovy":
                case "gradle":
                case "go":
                case "rs":
                case "swift":
                case "dart":
                case "js":
                case "mjs":
                case "cjs":
                case "jsx":
                case "ts":
                case "tsx":
                case "php":
                case "css":
                case "scss":
                case "less":
                case "json":
                case "proto":
                case "dts":
                case "dtsi":
                    return Language.CLike;

                case "rb":
                case "pl":
                case "pm":
                case "r":
                case "tcl":
                case "awk":
                case "cmake":
                case "mk":
                case "make":
                case "dockerfile":
                case "nix":
                    return Language.Script;

                case "sql":
                case "lua":
                    return Language.Sql;

                case "html":
                case "htm":
                case "xhtml":
                case "xml":
                case "xsd":
                case "xsl":
                case "svg":
                case "plist":
                    return Language.Markup;

                case "diff":
                case "patch":
                    return Language.Diff;

                // Prose (txt, md, log, csv, keys and certificates) stays plain: apostrophes and
                // "//" in URLs would otherwise be colored as strings and comments.
                default:
                    return Language.Plain;
            }
        }

        private static Language DetectLanguageFromShebang(string firstLine)
        {
            if (!firstLine.StartsWith("#!", StringComparison.Ordinal))
            {
                return Language.Plain;
            }
            string[] words = firstLine.Substring(2).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                return Language.Plain;
            }
            string program = ProgramName(words[0]);
            if (program == "env")
            {
                // "#!/usr/bin/env [-S] python3": the interpreter is the first word that is not an option.
                program = "";
                for (int i = 1; i < words.Length; i++)
                {
                    if (!words[i].StartsWith("-", StringComparison.Ordinal))
                    {
                        program = ProgramName(words[i]);
                        break;
                    }
                }
            }
            if (program.StartsWith("python", StringComparison.Ordinal))
            {
                return Language.Python;
            }
            switch (program)
            {
                case "sh":
                case "bash":
                case "zsh":
                case "ksh":
                case "dash":
                case "ash":
                    return Language.Bash;
            }
            if (program.StartsWith("perl", StringComparison.Ordinal) ||
                program.StartsWith("ruby", StringComparison.Ordinal) ||
                program.EndsWith("awk", StringComparison.Ordinal) ||
                program == "make" || program == "tclsh")
            {
                return Language.Script;
            }
            return Language.Plain;
        }

        private static string ProgramName(string path)
        {
            int slash = path.LastIndexOf('/');
            return path.Substring(slash + 1).ToLowerInvariant();
        }

        /// <summary>Returns the tokenizer for a language; null for Plain (no highlighting).</summary>
        public static TokenCache.LineTokenizer GetTokenizer(Language language)
        {
            switch (language)
            {
                case Language.Python: return TokenizePython;
                case Language.Bash: return TokenizeBash;
                case Language.Config: return TokenizeConfig;
                case Language.CLike: return CLikeTokenizer;
                case Language.Script: return ScriptTokenizer;
                case Language.Sql: return SqlTokenizer;
                case Language.Markup: return MarkupTokenizer;
                case Language.Diff: return TokenizeDiff;
                default: return null;
            }
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        private static bool IsIdStart(char c)
        {
            return char.IsLetter(c) || c == '_';
        }

        private static bool IsIdChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static bool IsBlank(char c)
        {
            return c == ' ' || c == '\t';
        }

        private static int SkipBlanks(string text, int index)
        {
            while (index < text.Length && IsBlank(text[index]))
            {
                index++;
            }
            return index;
        }

        private static bool StartsWithAt(string text, int index, string value)
        {
            return index + value.Length <= text.Length && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;
        }

        /// <summary>
        /// Leaves [start, end) in the plain text color and moves <paramref name="last"/> to end.
        /// No token is emitted: the editor paints text between tokens with the default color, and
        /// skipping these tokens halves the number of objects TokenCache keeps for a large file.
        /// </summary>
        private static void Normal(ref int last, int start, int end, TokenCache.ITokenSink sink)
        {
            last = end;
        }

        /// <summary>Finds the end of a quoted string honoring backslash escapes.
        /// Returns the index AFTER the closing quote or -1 if the string is not closed on this line.</summary>
        private static int FindQuoteEnd(string text, int start, char quote)
        {
            int len = text.Length;
            int i = start + 1;
            while (i < len)
            {
                char c = text[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == quote)
                {
                    return i + 1;
                }
                i++;
            }
            return -1;
        }

        private static bool TryGetBracketPair(char c, out int pairId, out bool isOpening)
        {
            switch (c)
            {
                case '(': pairId = PairParenthesis; isOpening = true; return true;
                case ')': pairId = PairParenthesis; isOpening = false; return true;
                case '[': pairId = PairSquareBracket; isOpening = true; return true;
                case ']': pairId = PairSquareBracket; isOpening = false; return true;
                case '{': pairId = PairCurlyBrace; isOpening = true; return true;
                case '}': pairId = PairCurlyBrace; isOpening = false; return true;
                default: pairId = 0; isOpening = false; return false;
            }
        }

        /// <summary>Emits text[index] as a paired bracket token when it is a bracket.</summary>
        private static bool TryAddBracket(string text, int index, ref int last, TokenCache.ITokenSink sink)
        {
            if (!TryGetBracketPair(text[index], out int pairId, out bool isOpening))
            {
                return false;
            }
            Normal(ref last, last, index, sink);
            // No region style: the pair is only used for matching-bracket highlighting, not for folding.
            sink.AddPairedToken(TypeNormal, index, index + 1, pairId, isOpening, null);
            last = index + 1;
            return true;
        }

        private static bool TryAddShebang(string text, TokenCache.ITokenSink sink)
        {
            if (sink.CurrentLine == 0 && text.StartsWith("#!", StringComparison.Ordinal))
            {
                sink.AddToken(TypePreprocessor, 0, text.Length);
                return true;
            }
            return false;
        }

        // ==================================================================
        // Python
        // ==================================================================

        // Line states:
        //   0x01 - inside a """ string
        //   0x02 - inside a ''' string
        private const int StPyTripleDouble = 0x01;
        private const int StPyTripleSingle = 0x02;

        private static readonly HashSet<string> PyKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "and", "as", "assert", "async", "await", "break", "class", "continue",
            "def", "del", "elif", "else", "except", "False", "finally", "for", "from",
            "global", "if", "import", "in", "is", "lambda", "None", "nonlocal", "not",
            "or", "pass", "raise", "return", "True", "try", "while", "with", "yield"
        };

        /// <summary>Returns the index after the closing triple quote (honoring escapes) or -1.</summary>
        private static int FindTripleQuoteEnd(string text, int start, string quote)
        {
            int i = start;
            while (i < text.Length)
            {
                if (text[i] == '\\')
                {
                    i += 2;
                    continue;
                }
                if (StartsWithAt(text, i, quote))
                {
                    return i + quote.Length;
                }
                i++;
            }
            return -1;
        }

        private static bool TokenizePython(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int i = 0;
            int last = 0;

            if (startState == StPyTripleDouble || startState == StPyTripleSingle)
            {
                // Continuation of a triple-quoted string from the previous line.
                string quote = (startState == StPyTripleDouble) ? "\"\"\"" : "'''";
                int close = FindTripleQuoteEnd(text, 0, quote);
                if (close < 0)
                {
                    if (len > 0)
                    {
                        sink.AddToken(TypeString, 0, len);
                    }
                    endState = startState;
                    return true;
                }
                sink.AddToken(TypeString, 0, close);
                i = close;
                last = i;
            }
            else if (TryAddShebang(text, sink))
            {
                endState = 0;
                return true;
            }

            int firstNonBlank = SkipBlanks(text, 0);
            while (i < len)
            {
                char c = text[i];

                // Comment up to the end of the line.
                if (c == '#')
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    endState = 0;
                    return true;
                }

                // Strings.
                if (c == '"' || c == '\'')
                {
                    bool triple = i + 2 < len && text[i + 1] == c && text[i + 2] == c;
                    Normal(ref last, last, i, sink);
                    if (triple)
                    {
                        string quote = (c == '"') ? "\"\"\"" : "'''";
                        int close = FindTripleQuoteEnd(text, i + 3, quote);
                        if (close < 0)
                        {
                            sink.AddToken(TypeString, i, len);
                            endState = (c == '"') ? StPyTripleDouble : StPyTripleSingle;
                            return true;
                        }
                        sink.AddToken(TypeString, i, close);
                        i = close;
                        last = i;
                        continue;
                    }
                    int end = FindQuoteEnd(text, i, c);
                    sink.AddToken(TypeString, i, (end < 0) ? len : end);
                    i = (end < 0) ? len : end;
                    last = i;
                    continue;
                }

                // Decorators: @name or @module.name at the start of a line.
                if (c == '@' && i == firstNonBlank && i + 1 < len && IsIdStart(text[i + 1]))
                {
                    int s = i;
                    i++;
                    while (i < len && (IsIdChar(text[i]) || text[i] == '.'))
                    {
                        i++;
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(TypePreprocessor, s, i);
                    last = i;
                    continue;
                }

                // Numbers (including 0x.., 0o.., 0b.., 1_000, 1e-5, 1.5j).
                if (IsDigit(c) || (c == '.' && i + 1 < len && IsDigit(text[i + 1])))
                {
                    int s = i;
                    while (i < len)
                    {
                        char n = text[i];
                        if (IsDigit(n) || n == '_' || n == '.' || n == 'x' || n == 'X' ||
                            n == 'o' || n == 'O' || n == 'j' || n == 'J' ||
                            (n >= 'a' && n <= 'f') || (n >= 'A' && n <= 'F') ||
                            ((n == '+' || n == '-') && i > s && (text[i - 1] == 'e' || text[i - 1] == 'E')))
                        {
                            i++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(TypeNumber, s, i);
                    last = i;
                    continue;
                }

                // Words: keywords; other identifiers and string prefixes (f, r, b, rf) stay plain.
                if (IsIdStart(c))
                {
                    int s = i;
                    while (i < len && IsIdChar(text[i]))
                    {
                        i++;
                    }
                    if (PyKeywords.Contains(text.Substring(s, i - s)))
                    {
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeKeyword, s, i);
                        last = i;
                    }
                    continue;
                }

                if (TryAddBracket(text, i, ref last, sink))
                {
                    i++;
                    continue;
                }

                i++;
            }

            Normal(ref last, last, len, sink);
            endState = 0;
            return true;
        }

        // ==================================================================
        // Bash
        // ==================================================================

        private const int StBashDouble = 0x01;
        private const int StBashSingle = 0x02;

        private static readonly HashSet<string> BashKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "then", "elif", "else", "fi", "for", "while", "until", "do", "done",
            "case", "esac", "in", "function", "select", "time", "local", "export",
            "readonly", "declare", "typeset", "return", "break", "continue", "source", "shift",
            "unset", "alias", "echo", "printf", "read", "trap", "exec", "exit", "set",
            "let", "eval", "test"
        };

        /// <summary>Characters after which a word (a command, a keyword, a '#' comment) starts.</summary>
        private static bool IsBashWordBreak(char c)
        {
            return c == ' ' || c == '\t' || c == ';' || c == '&' || c == '|' || c == '(' || c == ')' || c == '`' || c == '{' || c == '}';
        }

        private static bool TokenizeBash(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int i = 0;
            int last = 0;

            if (startState == StBashDouble || startState == StBashSingle)
            {
                // Continuation of a quoted string from the previous line.
                int end;
                if (startState == StBashDouble)
                {
                    end = FindQuoteEnd(text, -1, '"');
                }
                else
                {
                    int close = text.IndexOf('\'');
                    end = (close < 0) ? -1 : (close + 1);
                }
                if (end < 0)
                {
                    if (len > 0)
                    {
                        sink.AddToken(TypeString, 0, len);
                    }
                    endState = startState;
                    return true;
                }
                sink.AddToken(TypeString, 0, end);
                i = end;
                last = i;
            }
            else if (TryAddShebang(text, sink))
            {
                endState = 0;
                return true;
            }

            while (i < len)
            {
                char c = text[i];

                // A backslash escapes the next character (\", \#, \$ ...).
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }

                // Comment: '#' at the start of a word.
                if (c == '#' && (i == 0 || IsBashWordBreak(text[i - 1])))
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    endState = 0;
                    return true;
                }

                // Single quotes (no escapes inside, may span lines).
                if (c == '\'')
                {
                    int close = text.IndexOf('\'', i + 1);
                    Normal(ref last, last, i, sink);
                    if (close < 0)
                    {
                        sink.AddToken(TypeString, i, len);
                        endState = StBashSingle;
                        return true;
                    }
                    sink.AddToken(TypeString, i, close + 1);
                    i = close + 1;
                    last = i;
                    continue;
                }

                // Double quotes (with escapes, may span lines).
                if (c == '"')
                {
                    int end = FindQuoteEnd(text, i, '"');
                    Normal(ref last, last, i, sink);
                    if (end < 0)
                    {
                        sink.AddToken(TypeString, i, len);
                        endState = StBashDouble;
                        return true;
                    }
                    sink.AddToken(TypeString, i, end);
                    i = end;
                    last = i;
                    continue;
                }

                // Variables: $var, ${var}, $1, $#, $?, $$, $@, $*, $!, $-.
                if (c == '$' && i + 1 < len)
                {
                    int s = i + 1;
                    int e = -1;
                    if (text[s] == '{')
                    {
                        int close = text.IndexOf('}', s + 1);
                        e = (close < 0) ? len : (close + 1);
                    }
                    else if (IsIdStart(text[s]))
                    {
                        e = s;
                        while (e < len && IsIdChar(text[e]))
                        {
                            e++;
                        }
                    }
                    else if (IsDigit(text[s]) || "@*#?$!-".IndexOf(text[s]) >= 0)
                    {
                        e = s + 1;
                    }
                    if (e > 0)
                    {
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeVariable, i, e);
                        i = e;
                        last = i;
                        continue;
                    }
                    i++;
                    continue;
                }

                // Numbers.
                if (IsDigit(c))
                {
                    int s = i;
                    while (i < len && IsDigit(text[i]))
                    {
                        i++;
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(TypeNumber, s, i);
                    last = i;
                    continue;
                }

                // Words: keywords/builtins and assignments (name=value, name+=value).
                if (IsIdStart(c))
                {
                    int s = i;
                    while (i < len && IsIdChar(text[i]))
                    {
                        i++;
                    }
                    // "--set", "/usr/bin/test" or "file.done" are arguments, not keywords or assignments.
                    bool startsWord = s == 0 || IsBashWordBreak(text[s - 1]);
                    if (!startsWord)
                    {
                        continue;
                    }
                    if (i < len && (text[i] == '=' || (text[i] == '+' && i + 1 < len && text[i + 1] == '=')))
                    {
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeVariable, s, i);
                        last = i;
                        continue;
                    }
                    if ((i == len || IsBashWordBreak(text[i])) && BashKeywords.Contains(text.Substring(s, i - s)))
                    {
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeKeyword, s, i);
                        last = i;
                    }
                    continue;
                }

                i++;
            }

            Normal(ref last, last, Math.Min(i, len), sink);
            endState = 0;
            return true;
        }

        // ==================================================================
        // Config (INI / .conf / .properties / YAML / TOML / systemd units)
        // ==================================================================

        private static readonly HashSet<string> ConfigConstants = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "true", "false", "yes", "no", "on", "off", "null", "none"
        };

        private static bool IsConfigKeyChar(char c)
        {
            return IsIdChar(c) || c == '.' || c == '-';
        }

        private static bool TokenizeConfig(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int last = 0;
            int i = SkipBlanks(text, 0);
            endState = 0;

            if (i < len)
            {
                char first = text[i];

                // Whole-line comments: '#' and ';' at the start of a line.
                if (first == '#' || first == ';')
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    return true;
                }

                if (first == '[')
                {
                    // [section] / [[table]] headers.
                    int close = text.LastIndexOf(']');
                    if (close > i)
                    {
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeSection, i, close + 1);
                        i = close + 1;
                        last = i;
                    }
                }
                else if ((StartsWithAt(text, i, "---") || StartsWithAt(text, i, "...")) && SkipBlanks(text, i + 3) == len)
                {
                    // YAML document markers.
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeSection, i, i + 3);
                    i += 3;
                    last = i;
                }
                else
                {
                    // The first word of a line is a key when a value follows: "key = value",
                    // "key: value", "- key: value" (YAML list item) or "Key value" (sshd_config, nginx).
                    int keyStart = i;
                    if (first == '-' && i + 1 < len && text[i + 1] == ' ')
                    {
                        keyStart = SkipBlanks(text, i + 1);
                    }
                    int keyEnd = keyStart;
                    if (keyStart < len && IsIdStart(text[keyStart]))
                    {
                        while (keyEnd < len && IsConfigKeyChar(text[keyEnd]))
                        {
                            keyEnd++;
                        }
                    }
                    if (keyEnd > keyStart)
                    {
                        int probe = SkipBlanks(text, keyEnd);
                        bool isKey = probe < len && (text[probe] == '=' || text[probe] == ':' || (probe > keyEnd && text[probe] != '#'));
                        if (isKey)
                        {
                            Normal(ref last, last, keyStart, sink);
                            sink.AddToken(TypeVariable, keyStart, keyEnd);
                            i = keyEnd;
                            last = i;
                        }
                    }
                }
            }

            while (i < len)
            {
                char c = text[i];

                // Trailing comment: '#' after whitespace ("key = value # note"); "#fff" stays a value.
                if (c == '#' && i > 0 && IsBlank(text[i - 1]))
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    return true;
                }

                // Quoted strings.
                if (c == '"' || c == '\'')
                {
                    int end = FindQuoteEnd(text, i, c);
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeString, i, (end < 0) ? len : end);
                    i = (end < 0) ? len : end;
                    last = i;
                    continue;
                }

                // Variable references: ${NAME}, $NAME.
                if (c == '$' && i + 1 < len && (text[i + 1] == '{' || IsIdStart(text[i + 1])))
                {
                    int e;
                    if (text[i + 1] == '{')
                    {
                        int close = text.IndexOf('}', i + 2);
                        e = (close < 0) ? len : (close + 1);
                    }
                    else
                    {
                        e = i + 1;
                        while (e < len && IsIdChar(text[e]))
                        {
                            e++;
                        }
                    }
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeVariable, i, e);
                    i = e;
                    last = i;
                    continue;
                }

                // Numbers, IP addresses and versions (22, 0.5, 192.168.0.1).
                if (IsDigit(c))
                {
                    int s = i;
                    while (i < len && (IsDigit(text[i]) || text[i] == '.'))
                    {
                        i++;
                    }
                    if (i < len && IsIdChar(text[i]))
                    {
                        // "10M", "3des": part of a word, not a number.
                        while (i < len && IsConfigKeyChar(text[i]))
                        {
                            i++;
                        }
                        continue;
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(TypeNumber, s, i);
                    last = i;
                    continue;
                }

                // Words: yes/no/true/false/on/off are colored as constants.
                if (IsIdStart(c))
                {
                    int s = i;
                    while (i < len && IsConfigKeyChar(text[i]))
                    {
                        i++;
                    }
                    if (ConfigConstants.Contains(text.Substring(s, i - s)))
                    {
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeKeyword, s, i);
                        last = i;
                    }
                    continue;
                }

                i++;
            }

            Normal(ref last, last, len, sink);
            return true;
        }

        // ==================================================================
        // Diff
        // ==================================================================

        private static bool TokenizeDiff(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            endState = 0;
            if (text.Length == 0)
            {
                return true;
            }
            int type;
            if (text.StartsWith("diff ", StringComparison.Ordinal) || text.StartsWith("index ", StringComparison.Ordinal) ||
                text.StartsWith("+++", StringComparison.Ordinal) || text.StartsWith("---", StringComparison.Ordinal))
            {
                type = TypeKeyword;
            }
            else if (text.StartsWith("@@", StringComparison.Ordinal))
            {
                type = TypeSection;
            }
            else if (text[0] == '+')
            {
                type = TypeComment;  // added lines: the green comment color in both themes
            }
            else if (text[0] == '-')
            {
                type = TypeString;   // removed lines: the red string color
            }
            else
            {
                type = TypeNormal;
            }
            sink.AddToken(type, 0, text.Length);
            return true;
        }

        // ==================================================================
        // Generic code (C-like, script, SQL and markup languages)
        // ==================================================================

        // Line state: inside a block comment (/* ... */ or <!-- ... -->).
        private const int StBlockComment = 0x04;

        private static readonly HashSet<string> CLikeKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "else", "for", "foreach", "while", "do", "switch", "case", "default", "break", "continue",
            "return", "goto", "try", "catch", "finally", "throw", "throws", "new", "delete", "class", "struct",
            "union", "enum", "interface", "namespace", "using", "package", "import", "export", "from",
            "public", "private", "protected", "internal", "static", "const", "constexpr", "volatile", "extern",
            "inline", "virtual", "override", "abstract", "sealed", "final", "readonly", "typedef", "typename",
            "template", "this", "base", "super", "void", "int", "char", "short", "long", "float", "double",
            "bool", "boolean", "byte", "unsigned", "signed", "auto", "var", "let", "val", "fn", "func", "function",
            "def", "impl", "trait", "mut", "pub", "self", "async", "await", "yield", "typeof", "sizeof",
            "instanceof", "in", "is", "as", "true", "false", "null", "nullptr", "nil", "undefined", "string",
            "defer"
        };

        private static readonly HashSet<string> ScriptKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "elif", "elsif", "else", "unless", "end", "endif", "for", "foreach", "while", "until", "do",
            "done", "case", "when", "then", "begin", "rescue", "ensure", "return", "break", "next", "last",
            "redo", "def", "class", "module", "sub", "my", "our", "local", "use", "require", "include",
            "import", "export", "true", "false", "nil", "undef", "and", "or", "not", "in", "function",
            "ifeq", "ifneq", "ifdef", "ifndef", "define", "endef", "override",
            "FROM", "RUN", "CMD", "LABEL", "EXPOSE", "ENV", "ADD", "COPY", "ENTRYPOINT", "VOLUME", "USER",
            "WORKDIR", "ARG", "ONBUILD", "STOPSIGNAL", "HEALTHCHECK", "SHELL"
        };

        private static readonly HashSet<string> SqlKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "select", "from", "where", "and", "or", "not", "insert", "into", "values", "update", "set", "delete",
            "create", "table", "drop", "alter", "add", "index", "view", "join", "left", "right", "inner", "outer",
            "on", "as", "group", "by", "order", "having", "limit", "offset", "union", "all", "distinct", "null",
            "is", "in", "like", "between", "exists", "primary", "key", "foreign", "references", "default",
            "case", "when", "then", "else", "end", "begin", "commit", "rollback", "grant", "revoke", "true", "false",
            "local", "function", "if", "elseif", "for", "while", "do", "repeat", "until", "return", "break", "nil"
        };

        private static readonly HashSet<string> PreprocessorDirectives = new HashSet<string>(StringComparer.Ordinal)
        {
            "include", "define", "undef", "if", "ifdef", "ifndef", "elif", "else", "endif", "pragma",
            "error", "warning", "line", "import", "region", "endregion", "nullable"
        };

        private sealed class GenericSyntax
        {
            public bool HashComments;          // '#' starts a comment at the start of a line or after whitespace
            public bool SlashComments;         // "//" comments
            public bool DashDashComments;      // "--" comments
            public bool Preprocessor;          // #include-style directives at the start of a line
            public bool SingleQuoteStrings = true;
            public bool MarkupTags;            // <tag> and </tag> names are colored as keywords
            public bool Brackets;              // emit paired bracket tokens
            public string BlockCommentStart;
            public string BlockCommentEnd;
            public HashSet<string> Keywords;

            private bool IsLineComment(string text, int i)
            {
                char c = text[i];
                if (SlashComments && c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    return true;
                }
                if (DashDashComments && c == '-' && i + 1 < text.Length && text[i + 1] == '-')
                {
                    return true;
                }
                return HashComments && c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1]));
            }

            public bool Tokenize(int startState, string text, TokenCache.ITokenSink sink, out int endState)
            {
                int len = text.Length;
                int i = 0;
                int last = 0;

                if ((startState & StBlockComment) != 0 && BlockCommentEnd != null)
                {
                    int close = text.IndexOf(BlockCommentEnd, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        if (len > 0)
                        {
                            sink.AddToken(TypeComment, 0, len);
                        }
                        endState = StBlockComment;
                        return true;
                    }
                    i = close + BlockCommentEnd.Length;
                    sink.AddToken(TypeComment, 0, i);
                    last = i;
                }

                if (Preprocessor && i == 0)
                {
                    int hash = SkipBlanks(text, 0);
                    if (hash < len && text[hash] == '#')
                    {
                        int wordStart = SkipBlanks(text, hash + 1);
                        int wordEnd = wordStart;
                        while (wordEnd < len && IsIdChar(text[wordEnd]))
                        {
                            wordEnd++;
                        }
                        if (wordEnd > wordStart && PreprocessorDirectives.Contains(text.Substring(wordStart, wordEnd - wordStart)))
                        {
                            Normal(ref last, last, hash, sink);
                            sink.AddToken(TypePreprocessor, hash, wordEnd);
                            i = wordEnd;
                            last = i;
                        }
                    }
                }

                while (i < len)
                {
                    char c = text[i];

                    if (BlockCommentStart != null && StartsWithAt(text, i, BlockCommentStart))
                    {
                        Normal(ref last, last, i, sink);
                        int close = text.IndexOf(BlockCommentEnd, i + BlockCommentStart.Length, StringComparison.Ordinal);
                        if (close < 0)
                        {
                            sink.AddToken(TypeComment, i, len);
                            endState = StBlockComment;
                            return true;
                        }
                        int end = close + BlockCommentEnd.Length;
                        sink.AddToken(TypeComment, i, end);
                        i = end;
                        last = i;
                        continue;
                    }

                    if (IsLineComment(text, i))
                    {
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeComment, i, len);
                        endState = 0;
                        return true;
                    }

                    if (c == '"' || (c == '\'' && SingleQuoteStrings))
                    {
                        int end = FindQuoteEnd(text, i, c);
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeString, i, (end < 0) ? len : end);
                        i = (end < 0) ? len : end;
                        last = i;
                        continue;
                    }

                    if (MarkupTags && c == '<' && i + 1 < len && (IsIdStart(text[i + 1]) || text[i + 1] == '/' || text[i + 1] == '?'))
                    {
                        int s = i;
                        i++;
                        if (text[i] == '/' || text[i] == '?')
                        {
                            i++;
                        }
                        while (i < len && (IsIdChar(text[i]) || text[i] == ':' || text[i] == '-' || text[i] == '.'))
                        {
                            i++;
                        }
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeKeyword, s, i);
                        last = i;
                        continue;
                    }

                    if (IsDigit(c))
                    {
                        int s = i;
                        while (i < len && (IsIdChar(text[i]) || (text[i] == '.' && i + 1 < len && IsDigit(text[i + 1]))))
                        {
                            i++;
                        }
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeNumber, s, i);
                        last = i;
                        continue;
                    }

                    if (IsIdStart(c))
                    {
                        int s = i;
                        while (i < len && IsIdChar(text[i]))
                        {
                            i++;
                        }
                        if (Keywords != null && Keywords.Contains(text.Substring(s, i - s)))
                        {
                            Normal(ref last, last, s, sink);
                            sink.AddToken(TypeKeyword, s, i);
                            last = i;
                        }
                        continue;
                    }

                    if (Brackets && TryAddBracket(text, i, ref last, sink))
                    {
                        i++;
                        continue;
                    }

                    i++;
                }

                Normal(ref last, last, len, sink);
                endState = 0;
                return true;
            }
        }

        private static readonly TokenCache.LineTokenizer CLikeTokenizer = new GenericSyntax
        {
            SlashComments = true,
            Preprocessor = true,
            Brackets = true,
            BlockCommentStart = "/*",
            BlockCommentEnd = "*/",
            Keywords = CLikeKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer ScriptTokenizer = new GenericSyntax
        {
            HashComments = true,
            Keywords = ScriptKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer SqlTokenizer = new GenericSyntax
        {
            DashDashComments = true,
            Brackets = true,
            BlockCommentStart = "/*",
            BlockCommentEnd = "*/",
            Keywords = SqlKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer MarkupTokenizer = new GenericSyntax
        {
            // Apostrophes are common in text content, so only "..." is a string.
            SingleQuoteStrings = false,
            MarkupTags = true,
            BlockCommentStart = "<!--",
            BlockCommentEnd = "-->"
        }.Tokenize;
    }
}
