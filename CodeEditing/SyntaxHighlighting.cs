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
            Config,  // INI / .conf / .properties / TOML / systemd units
            CLike,   // C, C++, C#, Java, JavaScript, Go, Rust, CSS, ...: // and /* */ comments
            Script,  // Dockerfile, CMake, Ruby, Perl, ...: # comments
            Sql,     // SQL: -- comments
            Markup,  // HTML, XML: <!-- --> comments and tag names
            Diff,    // unified diffs
            Lua,     // Lua: -- comments, [[long strings]], foldable function/if/for/while/do/repeat blocks
            Json,    // JSON: keys, strings, numbers, foldable { } and [ ] blocks
            Makefile,// Makefile: variables, targets, foldable ifeq/define blocks
            Uci,     // OpenWrt UCI (/etc/config/*): config/option/list, foldable config blocks
            Yaml     // YAML: the colors of Config, blocks that fold by indentation
        }

        /// <summary>The languages offered in the syntax menu, in the order of the menu.</summary>
        public static readonly Language[] SelectableLanguages =
        {
            Language.Plain, Language.Bash, Language.Uci, Language.Lua, Language.Makefile, Language.Json, Language.Config,
            Language.Python, Language.Yaml, Language.CLike, Language.Script, Language.Sql, Language.Markup, Language.Diff
        };

        /// <summary>The name of a language in the syntax menu and the status bar.</summary>
        public static string GetDisplayName(Language language)
        {
            switch (language)
            {
                case Language.Plain: return "Plain text";
                case Language.Bash: return "Shell (sh, bash)";
                case Language.Uci: return "UCI (OpenWrt config)";
                case Language.Lua: return "Lua";
                case Language.Makefile: return "Makefile";
                case Language.Json: return "JSON";
                case Language.Config: return "Config (INI, conf, properties)";
                case Language.Yaml: return "YAML";
                case Language.Python: return "Python";
                case Language.CLike: return "C, C++, C#, Java, JavaScript";
                case Language.Script: return "Script (Perl, Ruby, Dockerfile)";
                case Language.Sql: return "SQL";
                case Language.Markup: return "HTML, XML";
                case Language.Diff: return "Diff, patch";
                default: return language.ToString();
            }
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

            // OpenWrt keeps UCI configuration files without an extension in /etc/config.
            string directory = remotePath.Replace('\\', '/').ToLowerInvariant();
            if (directory.Contains("/etc/config/") && !name.Contains(".") )
            {
                return Language.Uci;
            }

            Language byName = DetectLanguageFromNameOnly(name);
            if (byName == Language.Plain && IsOpenWrtShellDirectory(directory))
            {
                return Language.Bash;
            }
            return byName;
        }

        /// <summary>OpenWrt scripts that have no extension: init scripts, hotplug handlers, uci-defaults.</summary>
        private static bool IsOpenWrtShellDirectory(string lowerCasePath)
        {
            return lowerCasePath.Contains("/etc/init.d/") || lowerCasePath.Contains("/etc/hotplug.d/") ||
                lowerCasePath.Contains("/etc/uci-defaults/") || lowerCasePath.Contains("/lib/functions/") ||
                lowerCasePath.Contains("/lib/netifd/") || lowerCasePath.Contains("/lib/upgrade/");
        }

        private static Language DetectLanguageFromNameOnly(string name)
        {
            // Well-known file names without a meaningful extension.
            switch (name)
            {
                case ".bashrc":
                case "profile":
                case "rc.local":
                case "rc.common":
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
                case "bsdmakefile":
                    return Language.Makefile;

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
                case "dockerfile":
                case "nix":
                    return Language.Script;

                case "sql":
                    return Language.Sql;

                case "lua":
                    return Language.Lua;

                case "json":
                case "jsonc":
                case "json5":
                case "geojson":
                case "webmanifest":
                    return Language.Json;

                case "mk":
                case "make":
                    return Language.Makefile;

                case "uci":
                    return Language.Uci;

                case "yml":
                case "yaml":
                    return Language.Yaml;

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
            if (program == "make" || program == "gmake")
            {
                return Language.Makefile;
            }
            if (program.StartsWith("lua", StringComparison.Ordinal) || program == "luajit")
            {
                return Language.Lua;
            }
            if (program.StartsWith("perl", StringComparison.Ordinal) ||
                program.StartsWith("ruby", StringComparison.Ordinal) ||
                program.EndsWith("awk", StringComparison.Ordinal) ||
                program == "tclsh")
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
                case Language.Lua: return TokenizeLua;
                case Language.Json: return TokenizeJson;
                case Language.Makefile: return TokenizeMakefile;
                case Language.Uci: return TokenizeUci;
                case Language.Yaml: return TokenizeYaml;
                default: return null;
            }
        }

        // ---- Word lists for the editor's autocompletion (one list per language) ----

        private static readonly HashSet<string> JsonLiterals = new HashSet<string>(StringComparer.Ordinal)
        {
            "true", "false", "null"
        };

        private static readonly HashSet<string> UciKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "config", "option", "list", "package"
        };

        /// <summary>
        /// The words the editor offers as completions for a language: the language keywords (the same sets the
        /// tokenizers highlight with) plus the common literals and directives. Plain text and Diff have none.
        /// </summary>
        public static IReadOnlyCollection<string> GetKeywords(Language language)
        {
            switch (language)
            {
                case Language.Python:
                    return Combine(PyKeywords, PyBlockKeywords);
                case Language.Bash:
                    return Combine(BashKeywords);
                case Language.Lua:
                    return Combine(LuaKeywords);
                case Language.CLike:
                    return Combine(CLikeKeywords, PreprocessorDirectives);
                case Language.Script:
                    return Combine(ScriptKeywords);
                case Language.Sql:
                    return Combine(SqlKeywords);
                case Language.Makefile:
                    return Combine(MakeDirectives);
                case Language.Json:
                    return Combine(JsonLiterals);
                case Language.Config:
                    return Combine(ConfigConstants);
                case Language.Yaml:
                    return Combine(ConfigConstants);
                case Language.Uci:
                    return Combine(UciKeywords);
                default:
                    return EmptyKeywords;
            }
        }

        private static readonly string[] EmptyKeywords = new string[0];

        private static IReadOnlyCollection<string> Combine(params IEnumerable<string>[] sets)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
            foreach (IEnumerable<string> set in sets)
            {
                foreach (string word in set)
                {
                    result.Add(word);
                }
            }
            return result;
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

        /// <summary>
        /// The fold of a multi-line { } block: "if (x) {...}". A "{" alone on its line folds from the line above
        /// (Allman style), and the blank line after the block stays visible.
        /// </summary>
        private static readonly TokenCache.RegionStyle CurlyBraceRegion = new TokenCache.RegionStyle(
            TokenCache.RegionStyle.FoldBoundary.TokenEnd, TokenCache.RegionStyle.FoldBoundary.TokenEnd, unusedFlag: false,
            TokenCache.RegionStyle.LabelSource.FixedText, "...", openingOnNextLine: true, trailingBlankLines: 0);

        /// <summary>
        /// Emits text[index] as a paired bracket token when it is a bracket. With <paramref name="foldCurlyBraces"/>
        /// a { } pair (and a [ ] pair with <paramref name="foldSquareBrackets"/>) spanning lines can be folded; other pairs are only used for matching-bracket highlighting.
        /// </summary>
        private static bool TryAddBracket(string text, int index, ref int last, TokenCache.ITokenSink sink, bool foldCurlyBraces = false, bool foldSquareBrackets = false)
        {
            if (!TryGetBracketPair(text[index], out int pairId, out bool isOpening))
            {
                return false;
            }
            Normal(ref last, last, index, sink);
            TokenCache.RegionStyle regionStyle = ((foldCurlyBraces && pairId == PairCurlyBrace) || (foldSquareBrackets && pairId == PairSquareBracket)) ? CurlyBraceRegion : null;
            sink.AddPairedToken(TypeNormal, index, index + 1, pairId, isOpening, regionStyle);
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
            int stringState = startState & StPyStringMask;
            int depth = (startState >> PyDepthShift) & 7;          // open indented blocks
            int bracket = (startState >> PyBracketShift) & 3;      // unclosed ( [ {
            int header = (startState >> PyHeaderShift) & 31;       // indent + 1 of a block header whose statement goes on
            int levels = (startState >> PyLevelShift) & 0xFFFFF;   // indent increase of every open block

            int Pack(int stringBits)
            {
                return stringBits | (depth << PyDepthShift) | (bracket << PyBracketShift) | (header << PyHeaderShift) | (levels << PyLevelShift);
            }

            // At the end of the document every block that is still open ends.
            void CloseAtEnd()
            {
                if (!sink.IsLastLine)
                {
                    return;
                }
                for (; depth > 0; depth--)
                {
                    sink.AddPairedToken(TypeNormal, len, len, PairIndentBlock, false, null);
                    levels &= ~(0xF << (4 * (depth - 1)));
                }
            }

            // The statement ends here (before a comment or at the end of the line): "def f(x):" opens a block.
            void FinishStatement(int codeEnd)
            {
                if (bracket != 0 || header == 0)
                {
                    return;
                }
                int end = codeEnd;
                while (end > 0 && IsBlank(text[end - 1]))
                {
                    end--;
                }
                if (end > 0 && text[end - 1] == ':' && depth < PyMaxLevels)
                {
                    int cumulative = IndentLevelSum(levels, depth, 0xF);
                    int delta = Math.Max(0, Math.Min(15, (header - 1) - cumulative));
                    levels |= delta << (4 * depth);
                    depth++;
                    sink.AddPairedToken(TypeNormal, end, end, PairIndentBlock, true, ExclusiveBlockRegion);
                }
                header = 0;
            }

            if (stringState != 0)
            {
                // Continuation of a triple-quoted string from the previous line.
                string quote = (stringState == StPyTripleDouble) ? "\"\"\"" : "'''";
                int close = FindTripleQuoteEnd(text, 0, quote);
                if (close < 0)
                {
                    if (len > 0)
                    {
                        sink.AddToken(TypeString, 0, len);
                    }
                    CloseAtEnd();
                    endState = Pack(stringState);
                    return true;
                }
                sink.AddToken(TypeString, 0, close);
                i = close;
                last = i;
            }
            else if (TryAddShebang(text, sink))
            {
                endState = Pack(0);
                return true;
            }
            else
            {
                // A new statement: the blocks that are indented as deep or deeper than this line end here.
                int first = SkipBlanks(text, 0);
                if (bracket == 0 && first < len)
                {
                    int indent = IndentColumns(text, first);
                    while (depth > 0 && IndentLevelSum(levels, depth, 0xF) >= indent)
                    {
                        sink.AddPairedToken(TypeNormal, 0, 0, PairIndentBlock, false, null);
                        levels &= ~(0xF << (4 * (depth - 1)));
                        depth--;
                    }
                    if (text[first] != '#')
                    {
                        header = IsPythonBlockHeader(text, first) ? Math.Min(31, indent + 1) : 0;
                    }
                }
            }

            int firstNonBlank = SkipBlanks(text, 0);
            while (i < len)
            {
                char c = text[i];

                // Comment up to the end of the line.
                if (c == '#')
                {
                    FinishStatement(i);
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    CloseAtEnd();
                    endState = Pack(0);
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
                            CloseAtEnd();
                            endState = Pack((c == '"') ? StPyTripleDouble : StPyTripleSingle);
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
                    // Statements go on over lines while a bracket is open.
                    if (c == '(' || c == '[' || c == '{')
                    {
                        bracket = Math.Min(3, bracket + 1);
                    }
                    else
                    {
                        bracket = Math.Max(0, bracket - 1);
                    }
                    i++;
                    continue;
                }

                i++;
            }

            FinishStatement(len);
            Normal(ref last, last, len, sink);
            CloseAtEnd();
            endState = Pack(0);
            return true;
        }

        // ==================================================================
        // Bash
        // ==================================================================

        private const int StBashDouble = 0x01;
        private const int StBashSingle = 0x02;

        // Inside a here document: the lines up to the terminator are text, whatever quotes they hold. The state also
        // carries a hash of the terminator word (from bit 4) and whether "<<-" lets the terminator be indented with tabs.
        private const int StBashHeredoc = 0x04;
        private const int StBashHeredocStrip = 0x08;
        private const int BashHeredocHashShift = 4;

        private static int BashHeredocHash(string word)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in word)
                {
                    hash = hash * 31 + c;
                }
                return hash & 0x7FFFFFF;
            }
        }

        /// <summary>
        /// Reads a here document opener ("&lt;&lt;EOF", "&lt;&lt;-'EOF'") at <paramref name="start"/>. <paramref name="end"/> is
        /// the index after it. A shift in arithmetic ("$((1 &lt;&lt; n))") or a here string is not an opener.
        /// </summary>
        private static bool TryParseBashHeredoc(string text, int start, out int end, out int state)
        {
            end = start + 2;
            state = 0;
            int arithmetic = text.LastIndexOf("((", start, StringComparison.Ordinal);
            if (arithmetic >= 0 && text.IndexOf("))", arithmetic, start - arithmetic, StringComparison.Ordinal) < 0)
            {
                return false;
            }
            int j = start + 2;
            bool strip = false;
            if (j < text.Length && text[j] == '-')
            {
                strip = true;
                j++;
            }
            j = SkipBlanks(text, j);
            if (j < text.Length && text[j] == '\\')
            {
                j++;
            }
            string word;
            bool quoted = false;
            if (j < text.Length && (text[j] == '\'' || text[j] == '"'))
            {
                int close = text.IndexOf(text[j], j + 1);
                if (close < 0)
                {
                    return false;
                }
                quoted = true;
                word = text.Substring(j + 1, close - j - 1);
                j = close + 1;
            }
            else
            {
                int wordStart = j;
                while (j < text.Length && !IsBlank(text[j]) && ";|&<>()".IndexOf(text[j]) < 0)
                {
                    j++;
                }
                word = text.Substring(wordStart, j - wordStart);
            }
            // A word that is not quoted must look like a name: "<< 3" is a shift.
            if (word.Length == 0 || (!quoted && !IsIdStart(word[0])))
            {
                return false;
            }
            end = j;
            state = StBashHeredoc | (strip ? StBashHeredocStrip : 0) | (BashHeredocHash(word) << BashHeredocHashShift);
            return true;
        }

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
            LineBuffer buffer = new LineBuffer(sink);
            bool result = TokenizeBashLine(startState, text, buffer, out endState);
            // "(" and "[" count only when closed on the same line: case patterns "a)" and globs "[a-z" are not brackets.
            buffer.Flush(PairParenthesis, PairSquareBracket);
            return result;
        }

        private static bool TokenizeBashLine(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            bool seenCase = false;
            bool seenElif = false;
            int i = 0;
            int last = 0;
            // A here document opened on this line starts with the next one.
            int pendingHeredoc = 0;

            if ((startState & StBashHeredoc) != 0)
            {
                string body = ((startState & StBashHeredocStrip) != 0) ? text.TrimStart('\t') : text;
                if (BashHeredocHash(body) == (startState >> BashHeredocHashShift))
                {
                    // The terminator.
                    sink.AddToken(TypeKeyword, 0, len);
                    endState = 0;
                    return true;
                }
                if (len > 0)
                {
                    sink.AddToken(TypeString, 0, len);
                }
                endState = startState;
                return true;
            }

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
                    endState = pendingHeredoc;
                    return true;
                }

                // Here document: "<<EOF", "<<-EOF", "<<'EOF'". The lines that follow are text up to the terminator.
                if (c == '<' && i + 1 < len && text[i + 1] == '<')
                {
                    if (i + 2 < len && text[i + 2] == '<')
                    {
                        // A here string, "<<<".
                        i += 3;
                        continue;
                    }
                    if (pendingHeredoc == 0 && TryParseBashHeredoc(text, i, out int openerEnd, out int heredocState))
                    {
                        pendingHeredoc = heredocState;
                        i = openerEnd;
                        continue;
                    }
                    i += 2;
                    continue;
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
                        string word = text.Substring(s, i - s);
                        switch (word)
                        {
                            case "elif":
                                seenElif = true;
                                break;
                            case "then":
                                // "elif ...; then" continues the block that "if" opened.
                                if (!seenElif)
                                {
                                    AddKeywordPair(text, s, i, PairThenBlock, true, ref last, sink);
                                    continue;
                                }
                                seenElif = false;
                                break;
                            case "fi":
                                AddKeywordPair(text, s, i, PairThenBlock, false, ref last, sink);
                                continue;
                            case "do":
                                AddKeywordPair(text, s, i, PairLoopBlock, true, ref last, sink);
                                continue;
                            case "done":
                                AddKeywordPair(text, s, i, PairLoopBlock, false, ref last, sink);
                                continue;
                            case "case":
                                seenCase = true;
                                break;
                            case "in":
                                if (seenCase)
                                {
                                    seenCase = false;
                                    AddKeywordPair(text, s, i, PairCaseBlock, true, ref last, sink);
                                    continue;
                                }
                                break;
                            case "esac":
                                AddKeywordPair(text, s, i, PairCaseBlock, false, ref last, sink);
                                continue;
                        }
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeKeyword, s, i);
                        last = i;
                    }
                    continue;
                }

                // Brackets. A "{" / "}" that stands alone is a brace group or a function body; "{a,b}" and "{}" are not.
                if (c == '{' && (i + 1 == len || IsBlank(text[i + 1])) && (i == 0 || IsBashWordBreak(text[i - 1])))
                {
                    TryAddBracket(text, i, ref last, sink, foldCurlyBraces: true);
                    i++;
                    continue;
                }
                if (c == '}' && (i == 0 || IsBlank(text[i - 1]) || text[i - 1] == ';') && (i + 1 == len || IsBashWordBreak(text[i + 1])))
                {
                    TryAddBracket(text, i, ref last, sink, foldCurlyBraces: true);
                    i++;
                    continue;
                }
                if (c == '(' || c == ')' || c == '[' || c == ']')
                {
                    TryAddBracket(text, i, ref last, sink);
                    i++;
                    continue;
                }

                i++;
            }

            Normal(ref last, last, Math.Min(i, len), sink);
            endState = pendingHeredoc;
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
            public bool FoldCurlyBraces;       // multi-line { } blocks can be folded
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

                    if (Brackets && TryAddBracket(text, i, ref last, sink, FoldCurlyBraces))
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
            FoldCurlyBraces = true,
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

        // ==================================================================
        // Block keywords: fold regions of "then ... fi", "do ... done", "function ... end"
        // ==================================================================

        // Pair ids of keyword blocks (bracket ids are 1..3).
        private const int PairThenBlock = 4;     // Bash then...fi, Lua then/do/function...end
        private const int PairCaseBlock = 5;     // Bash case ... in ... esac
        private const int PairRepeatBlock = 6;   // Lua repeat...until
        private const int PairLoopBlock = 7;     // Bash do...done
        private const int PairConditional = 8;   // Makefile ifeq...endif
        private const int PairDefineBlock = 9;   // Makefile define...endef
        private const int PairConfigBlock = 10;  // UCI "config" block up to the blank line

        /// <summary>The fold of a keyword block: "if x; then...fi". The text after the closing keyword stays visible.</summary>
        private static readonly TokenCache.RegionStyle KeywordBlockRegion = new TokenCache.RegionStyle(
            TokenCache.RegionStyle.FoldBoundary.TokenEnd, TokenCache.RegionStyle.FoldBoundary.TokenEnd, unusedFlag: false,
            TokenCache.RegionStyle.LabelSource.FixedText, "...", openingOnNextLine: false, trailingBlankLines: 0);

        private static void AddKeywordPair(string text, int start, int end, int pairId, bool isOpening, ref int last, TokenCache.ITokenSink sink)
        {
            Normal(ref last, last, start, sink);
            sink.AddPairedToken(TypeKeyword, start, end, pairId, isOpening, isOpening ? KeywordBlockRegion : null);
            last = end;
        }

        /// <summary>Opens a block at the end of the code on the line (a zero-width token): "function f(a)" ... "end".</summary>
        private static void AddEndOfLineOpener(string text, int codeEnd, int pairId, TokenCache.ITokenSink sink, TokenCache.RegionStyle style = null)
        {
            int end = codeEnd;
            while (end > 0 && IsBlank(text[end - 1]))
            {
                end--;
            }
            sink.AddPairedToken(TypeNormal, end, end, pairId, true, style ?? KeywordBlockRegion);
        }

        /// <summary>
        /// Collects the tokens of one line so that brackets that are not closed on the same line can be dropped:
        /// shell code is full of unbalanced ")" (case patterns) and "[" (globs) that would break the pairing
        /// of the enclosing blocks.
        /// </summary>
        private sealed class LineBuffer : TokenCache.ITokenSink
        {
            private struct Item
            {
                public int Type, Start, End, PairId;
                public bool IsPaired, IsOpening, Keep;
                public TokenCache.RegionStyle Style;
            }

            private readonly TokenCache.ITokenSink _target;
            private readonly List<Item> _items = new List<Item>();

            public LineBuffer(TokenCache.ITokenSink target)
            {
                _target = target;
            }

            public int CurrentLine => _target.CurrentLine;

            public bool IsLastLine => _target.IsLastLine;

            public void AddToken(int type, int start, int end)
            {
                _items.Add(new Item { Type = type, Start = start, End = end, Keep = true });
            }

            /// <summary>Adds a zero-width paired token in front of the tokens that start at or after the position.</summary>
            public void InsertPairedToken(int position, int pairId, bool isOpening, TokenCache.RegionStyle regionStyle)
            {
                int index = 0;
                while (index < _items.Count && _items[index].Start < position)
                {
                    index++;
                }
                _items.Insert(index, new Item { Type = TypeNormal, Start = position, End = position, PairId = pairId, IsPaired = true, IsOpening = isOpening, Style = regionStyle, Keep = true });
            }

            public void AddPairedToken(int type, int start, int end, int pairId, bool isOpening, TokenCache.RegionStyle regionStyle)
            {
                _items.Add(new Item { Type = type, Start = start, End = end, PairId = pairId, IsPaired = true, IsOpening = isOpening, Style = regionStyle, Keep = true });
            }

            /// <summary>Sends the tokens to the target; brackets with a "local" pair id need a partner on this line.</summary>
            public void Flush(int localPairIdA, int localPairIdB)
            {
                List<int> stackA = new List<int>();
                List<int> stackB = new List<int>();
                for (int n = 0; n < _items.Count; n++)
                {
                    Item item = _items[n];
                    if (!item.IsPaired || (item.PairId != localPairIdA && item.PairId != localPairIdB))
                    {
                        continue;
                    }
                    List<int> stack = (item.PairId == localPairIdA) ? stackA : stackB;
                    if (item.IsOpening)
                    {
                        item.Keep = false;
                        _items[n] = item;
                        stack.Add(n);
                    }
                    else if (stack.Count > 0)
                    {
                        int open = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                        Item opening = _items[open];
                        opening.Keep = true;
                        _items[open] = opening;
                    }
                    else
                    {
                        item.Keep = false;
                        _items[n] = item;
                    }
                }
                foreach (Item item in _items)
                {
                    if (!item.Keep)
                    {
                        continue;
                    }
                    if (item.IsPaired)
                    {
                        _target.AddPairedToken(item.Type, item.Start, item.End, item.PairId, item.IsOpening, item.Style);
                    }
                    else
                    {
                        _target.AddToken(item.Type, item.Start, item.End);
                    }
                }
            }
        }

        // ==================================================================
        // Lua
        // ==================================================================

        // Line state: 0x10 - inside a long comment --[==[ ... ]==], 0x20 - inside a long string [==[ ... ]==];
        // the level (number of "=") is stored in bits 8 and up.
        private const int StLuaComment = 0x10;
        private const int StLuaString = 0x20;

        private static readonly HashSet<string> LuaKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
            "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while"
        };

        /// <summary>Level of the long bracket "[==[" that starts at index, or -1.</summary>
        private static int LuaLongBracketLevel(string text, int index)
        {
            if (index >= text.Length || text[index] != '[')
            {
                return -1;
            }
            int j = index + 1;
            int level = 0;
            while (j < text.Length && text[j] == '=')
            {
                level++;
                j++;
            }
            return (j < text.Length && text[j] == '[') ? level : -1;
        }

        /// <summary>Index after the closing "]==]" of the given level, or -1.</summary>
        private static int FindLuaLongClose(string text, int from, int level)
        {
            string close = "]" + new string('=', level) + "]";
            int position = text.IndexOf(close, from, StringComparison.Ordinal);
            return (position < 0) ? -1 : (position + close.Length);
        }

        private static bool TokenizeLua(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int i = 0;
            int last = 0;
            int openFunctions = 0;   // "function" keywords on this line that still wait for their "end"
            bool afterElseif = false;

            if ((startState & (StLuaComment | StLuaString)) != 0)
            {
                int level = startState >> 8;
                int type = ((startState & StLuaComment) != 0) ? TypeComment : TypeString;
                int close = FindLuaLongClose(text, 0, level);
                if (close < 0)
                {
                    if (len > 0)
                    {
                        sink.AddToken(type, 0, len);
                    }
                    endState = startState;
                    return true;
                }
                sink.AddToken(type, 0, close);
                i = close;
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

                // Comments: "-- text" and "--[==[ long ]==]".
                if (c == '-' && i + 1 < len && text[i + 1] == '-')
                {
                    for (; openFunctions > 0; openFunctions--)
                    {
                        AddEndOfLineOpener(text, i, PairThenBlock, sink);
                    }
                    Normal(ref last, last, i, sink);
                    int level = LuaLongBracketLevel(text, i + 2);
                    if (level >= 0)
                    {
                        int close = FindLuaLongClose(text, i + level + 4, level);
                        if (close < 0)
                        {
                            sink.AddToken(TypeComment, i, len);
                            endState = StLuaComment | (level << 8);
                            return true;
                        }
                        sink.AddToken(TypeComment, i, close);
                        i = close;
                        last = i;
                        continue;
                    }
                    sink.AddToken(TypeComment, i, len);
                    endState = 0;
                    return true;
                }

                // Long strings [[ ... ]] and [==[ ... ]==].
                if (c == '[')
                {
                    int level = LuaLongBracketLevel(text, i);
                    if (level >= 0)
                    {
                        Normal(ref last, last, i, sink);
                        int close = FindLuaLongClose(text, i + level + 2, level);
                        if (close < 0)
                        {
                            sink.AddToken(TypeString, i, len);
                            endState = StLuaString | (level << 8);
                            return true;
                        }
                        sink.AddToken(TypeString, i, close);
                        i = close;
                        last = i;
                        continue;
                    }
                }

                if (c == '"' || c == '\'')
                {
                    int end = FindQuoteEnd(text, i, c);
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeString, i, (end < 0) ? len : end);
                    i = (end < 0) ? len : end;
                    last = i;
                    continue;
                }

                if (IsDigit(c) || (c == '.' && i + 1 < len && IsDigit(text[i + 1])))
                {
                    int s = i;
                    while (i < len && (IsIdChar(text[i]) || (text[i] == '.' && !(i + 1 < len && text[i + 1] == '.')) ||
                        ((text[i] == '+' || text[i] == '-') && i > s && "eEpP".IndexOf(text[i - 1]) >= 0)))
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
                    string word = text.Substring(s, i - s);
                    // "t.end" and "obj:then" are fields, not keywords.
                    if (!LuaKeywords.Contains(word) || (s > 0 && (text[s - 1] == '.' || text[s - 1] == ':')))
                    {
                        continue;
                    }
                    switch (word)
                    {
                        case "function":
                            openFunctions++;
                            break;
                        case "elseif":
                            afterElseif = true;
                            break;
                        case "then":
                            if (afterElseif)
                            {
                                afterElseif = false;
                                break;
                            }
                            AddKeywordPair(text, s, i, PairThenBlock, true, ref last, sink);
                            continue;
                        case "do":
                            AddKeywordPair(text, s, i, PairThenBlock, true, ref last, sink);
                            continue;
                        case "repeat":
                            AddKeywordPair(text, s, i, PairRepeatBlock, true, ref last, sink);
                            continue;
                        case "until":
                            AddKeywordPair(text, s, i, PairRepeatBlock, false, ref last, sink);
                            continue;
                        case "end":
                            if (openFunctions > 0)
                            {
                                // "function() ... end" on one line: the block is closed where it was opened.
                                openFunctions--;
                                break;
                            }
                            AddKeywordPair(text, s, i, PairThenBlock, false, ref last, sink);
                            continue;
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(TypeKeyword, s, i);
                    last = i;
                    continue;
                }

                if (TryAddBracket(text, i, ref last, sink, foldCurlyBraces: true))
                {
                    i++;
                    continue;
                }

                i++;
            }

            for (; openFunctions > 0; openFunctions--)
            {
                AddEndOfLineOpener(text, len, PairThenBlock, sink);
            }
            Normal(ref last, last, len, sink);
            endState = 0;
            return true;
        }

        // ==================================================================
        // JSON (comments are tolerated, so .jsonc files work too)
        // ==================================================================

        private static bool TokenizeJson(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int i = 0;
            int last = 0;

            if (startState == StBlockComment)
            {
                int close = text.IndexOf("*/", StringComparison.Ordinal);
                if (close < 0)
                {
                    if (len > 0)
                    {
                        sink.AddToken(TypeComment, 0, len);
                    }
                    endState = StBlockComment;
                    return true;
                }
                i = close + 2;
                sink.AddToken(TypeComment, 0, i);
                last = i;
            }

            while (i < len)
            {
                char c = text[i];

                if (c == '/' && i + 1 < len && text[i + 1] == '/')
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    endState = 0;
                    return true;
                }

                if (c == '/' && i + 1 < len && text[i + 1] == '*')
                {
                    Normal(ref last, last, i, sink);
                    int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        sink.AddToken(TypeComment, i, len);
                        endState = StBlockComment;
                        return true;
                    }
                    sink.AddToken(TypeComment, i, close + 2);
                    i = close + 2;
                    last = i;
                    continue;
                }

                if (c == '"')
                {
                    int end = FindQuoteEnd(text, i, '"');
                    int stop = (end < 0) ? len : end;
                    // A string followed by ':' is an object key.
                    bool isKey = SkipBlanks(text, stop) < len && text[SkipBlanks(text, stop)] == ':';
                    Normal(ref last, last, i, sink);
                    sink.AddToken(isKey ? TypeVariable : TypeString, i, stop);
                    i = stop;
                    last = i;
                    continue;
                }

                if (IsDigit(c) || (c == '-' && i + 1 < len && IsDigit(text[i + 1])))
                {
                    int s = i;
                    i++;
                    while (i < len && (IsIdChar(text[i]) || text[i] == '.' ||
                        ((text[i] == '+' || text[i] == '-') && (text[i - 1] == 'e' || text[i - 1] == 'E'))))
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
                    string word = text.Substring(s, i - s);
                    if (word == "true" || word == "false" || word == "null")
                    {
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeKeyword, s, i);
                        last = i;
                    }
                    continue;
                }

                if (TryAddBracket(text, i, ref last, sink, foldCurlyBraces: true, foldSquareBrackets: true))
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
        // Makefile
        // ==================================================================

        private static readonly HashSet<string> MakeDirectives = new HashSet<string>(StringComparer.Ordinal)
        {
            "include", "-include", "sinclude", "export", "unexport", "override", "vpath", "undefine",
            "else", "ifeq", "ifneq", "ifdef", "ifndef", "endif", "define", "endef"
        };

        /// <summary>Index after the closing bracket of "$(...)" / "${...}" that starts at index, or the end of the line.</summary>
        private static int FindMakeReferenceEnd(string text, int index)
        {
            char open = text[index + 1];
            char close = (open == '(') ? ')' : '}';
            int depth = 0;
            for (int j = index + 1; j < text.Length; j++)
            {
                if (text[j] == open)
                {
                    depth++;
                }
                else if (text[j] == close && --depth == 0)
                {
                    return j + 1;
                }
            }
            return text.Length;
        }

        // Line state: 0x01 - inside a rule (its recipe lines follow), 0x02 - inside "define ... endef".
        private const int StMakeRule = 0x01;
        private const int StMakeDefine = 0x02;

        private static bool TokenizeMakefile(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int state = startState & (StMakeRule | StMakeDefine);
            bool result = TokenizeMakefileLine(ref state, text, sink);
            // The end of the document ends the rule that is open.
            if ((state & StMakeRule) != 0 && sink.IsLastLine)
            {
                sink.AddPairedToken(TypeNormal, text.Length, text.Length, PairMakeRule, false, null);
                state &= ~StMakeRule;
            }
            endState = state;
            return result;
        }

        private static bool TokenizeMakefileLine(ref int state, string text, TokenCache.ITokenSink sink)
        {
            int len = text.Length;
            int last = 0;
            int i = 0;
            int pendingOpener = 0;  // pair id of a block that opens at the end of this line
            bool inDefine = (state & StMakeDefine) != 0;

            if (TryAddShebang(text, sink))
            {
                state = 0;
                return true;
            }

            bool recipe = len > 0 && text[0] == '\t';
            int first = SkipBlanks(text, 0);

            // A rule goes on while its recipe lines (they start with a tab) follow; a blank line or any other line ends it.
            if ((state & StMakeRule) != 0 && !inDefine && (first == len || !recipe))
            {
                sink.AddPairedToken(TypeNormal, 0, 0, PairMakeRule, false, null);
                state &= ~StMakeRule;
            }

            if (!recipe && first < len)
            {
                if (text[first] == '#')
                {
                    sink.AddToken(TypeComment, first, len);
                    return true;
                }

                int wordEnd = first;
                while (wordEnd < len && (IsIdChar(text[wordEnd]) || (wordEnd == first && text[wordEnd] == '-')))
                {
                    wordEnd++;
                }
                string word = text.Substring(first, wordEnd - first);
                bool isDirective = MakeDirectives.Contains(word) && (wordEnd == len || IsBlank(text[wordEnd]) || text[wordEnd] == '(');
                if (inDefine && word != "endef")
                {
                    isDirective = false;
                }
                if (isDirective)
                {
                    switch (word)
                    {
                        case "ifeq":
                        case "ifneq":
                        case "ifdef":
                        case "ifndef":
                            pendingOpener = PairConditional;
                            Normal(ref last, last, first, sink);
                            sink.AddToken(TypeKeyword, first, wordEnd);
                            break;
                        case "define":
                            pendingOpener = PairDefineBlock;
                            state |= StMakeDefine;
                            Normal(ref last, last, first, sink);
                            sink.AddToken(TypeKeyword, first, wordEnd);
                            break;
                        case "endif":
                            AddKeywordPair(text, first, wordEnd, PairConditional, false, ref last, sink);
                            break;
                        case "endef":
                            state &= ~StMakeDefine;
                            AddKeywordPair(text, first, wordEnd, PairDefineBlock, false, ref last, sink);
                            break;
                        default:
                            Normal(ref last, last, first, sink);
                            sink.AddToken(TypeKeyword, first, wordEnd);
                            break;
                    }
                    last = wordEnd;
                    i = wordEnd;
                }
                else if (!inDefine)
                {
                    // "VAR = value", "VAR := value", "VAR ?= value" and "target: deps".
                    int j = first;
                    int depth = 0;
                    while (j < len)
                    {
                        char c = text[j];
                        if (c == '$' && j + 1 < len && (text[j + 1] == '(' || text[j + 1] == '{'))
                        {
                            j = FindMakeReferenceEnd(text, j);
                            continue;
                        }
                        if (c == '#' || c == '"' || c == '\'')
                        {
                            j = len;
                            break;
                        }
                        if (depth == 0 && (c == '=' || c == ':'))
                        {
                            break;
                        }
                        j++;
                    }
                    if (j < len)
                    {
                        int keyEnd = j;
                        bool assignment = text[j] == '=';
                        if (text[j] == ':' && j + 1 < len && (text[j + 1] == '=' || (text[j + 1] == ':' && j + 2 < len && text[j + 2] == '=')))
                        {
                            assignment = true;
                        }
                        if (assignment)
                        {
                            while (keyEnd > first && ("+?!:".IndexOf(text[keyEnd - 1]) >= 0 || IsBlank(text[keyEnd - 1])))
                            {
                                keyEnd--;
                            }
                        }
                        else
                        {
                            while (keyEnd > first && IsBlank(text[keyEnd - 1]))
                            {
                                keyEnd--;
                            }
                        }
                        string name = text.Substring(first, keyEnd - first);
                        if (keyEnd > first && !assignment)
                        {
                            // The recipe lines below the rule fold.
                            pendingOpener = PairMakeRule;
                            state |= StMakeRule;
                        }
                        if (keyEnd > first && name.IndexOf('$') < 0)
                        {
                            Normal(ref last, last, first, sink);
                            sink.AddToken(assignment ? TypeVariable : TypeSection, first, keyEnd);
                            last = keyEnd;
                            i = keyEnd;
                        }
                    }
                }
            }

            while (i < len)
            {
                char c = text[i];

                if (c == '#' && (i == 0 || IsBlank(text[i - 1]) || recipe))
                {
                    if (pendingOpener != 0)
                    {
                        AddEndOfLineOpener(text, i, pendingOpener, sink, (pendingOpener == PairMakeRule) ? ExclusiveBlockRegion : null);
                        pendingOpener = 0;
                    }
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    return true;
                }

                if (c == '$' && i + 1 < len)
                {
                    int e;
                    if (text[i + 1] == '(' || text[i + 1] == '{')
                    {
                        e = FindMakeReferenceEnd(text, i);
                    }
                    else
                    {
                        e = i + 2;   // $@ $< $^ $* $? $$ $1
                    }
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeVariable, i, e);
                    i = e;
                    last = i;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    int end = FindQuoteEnd(text, i, c);
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeString, i, (end < 0) ? len : end);
                    i = (end < 0) ? len : end;
                    last = i;
                    continue;
                }

                i++;
            }

            if (pendingOpener != 0)
            {
                AddEndOfLineOpener(text, len, pendingOpener, sink, (pendingOpener == PairMakeRule) ? ExclusiveBlockRegion : null);
            }
            Normal(ref last, last, len, sink);
            return true;
        }

        // ==================================================================
        // Blocks that follow the indentation (Python, YAML) and blocks that end at the next header
        // ==================================================================

        private const int PairIndentBlock = 11;  // Python def/class/if..., YAML "key:" and "- item"
        private const int PairMakeRule = 12;     // Makefile "target:" with its recipe

        /// <summary>
        /// The fold of a block that has no closing keyword: its closing token is a zero-width token at the start of the
        /// first line after the block, and that line stays visible: "def f(x):..." / "config interface 'lan'...".
        /// </summary>
        private static readonly TokenCache.RegionStyle ExclusiveBlockRegion = new TokenCache.RegionStyle(
            TokenCache.RegionStyle.FoldBoundary.TokenEnd, TokenCache.RegionStyle.FoldBoundary.TokenEnd, unusedFlag: false,
            TokenCache.RegionStyle.LabelSource.FixedText, "...", openingOnNextLine: false, trailingBlankLines: 0,
            endsBeforeClosingLine: true);

        /// <summary>The width of the indentation of a line in columns; a tab counts as 4.</summary>
        private static int IndentColumns(string text, int firstNonBlank)
        {
            int columns = 0;
            for (int i = 0; i < firstNonBlank; i++)
            {
                columns += (text[i] == '\t') ? 4 : 1;
            }
            return columns;
        }

        /// <summary>The indentation of the innermost of <paramref name="count"/> open blocks; every block stores its increase in 4 bits.</summary>
        private static int IndentLevelSum(int levels, int count, int deltaMask)
        {
            int sum = 0;
            for (int level = 0; level < count && level < 8; level++)
            {
                sum += (levels >> (4 * level)) & deltaMask;
            }
            return sum;
        }

        private const int PyMaxLevels = 5;
        private const int StPyStringMask = 0x03;
        private const int PyDepthShift = 2;     // 3 bits
        private const int PyBracketShift = 5;   // 2 bits
        private const int PyHeaderShift = 7;    // 5 bits
        private const int PyLevelShift = 12;    // 5 levels of 4 bits

        private static readonly HashSet<string> PyBlockKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "def", "class", "if", "elif", "else", "for", "while", "try", "except", "finally", "with", "async", "match", "case"
        };

        /// <summary>Whether the statement that starts at index starts with a keyword that opens a block.</summary>
        private static bool IsPythonBlockHeader(string text, int index)
        {
            int end = index;
            while (end < text.Length && IsIdChar(text[end]))
            {
                end++;
            }
            return end > index && PyBlockKeywords.Contains(text.Substring(index, end - index));
        }

        // YAML line state: bits 0-2 - open blocks (up to 6), then 4 bits per block:
        // bits 0-2 the increase of the indentation, bit 3 - the block is a "key:" (a list may sit at the same indentation).
        private const int YamlMaxLevels = 6;
        private const int YamlLevelShift = 3;

        /// <summary>The end of the code of a YAML line (the comment and the blanks before it are not included).</summary>
        private static int YamlCodeEnd(string text, int first)
        {
            char quote = '\0';
            int end = text.Length;
            for (int i = first; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == '\\' && quote == '"')
                    {
                        i++;
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if ((c == '"' || c == '\'') && (i == first || IsBlank(text[i - 1]) || text[i - 1] == ':' || text[i - 1] == '-' || text[i - 1] == '[' || text[i - 1] == '{' || text[i - 1] == ','))
                {
                    quote = c;
                }
                else if (c == '#' && (i == first || IsBlank(text[i - 1])))
                {
                    end = i;
                    break;
                }
            }
            while (end > first && IsBlank(text[end - 1]))
            {
                end--;
            }
            return end;
        }

        /// <summary>Whether the text contains "key:" (a colon followed by a blank or the end) outside quotes.</summary>
        private static bool HasYamlKey(string code, int start)
        {
            char quote = '\0';
            for (int i = start; i < code.Length; i++)
            {
                char c = code[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == ':' && (i + 1 == code.Length || IsBlank(code[i + 1])))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>"key: |", "key: >-", "- |2": the text of a block scalar follows on the next lines.</summary>
        private static bool IsYamlBlockScalarHeader(string code)
        {
            int space = code.LastIndexOf(' ');
            int wordStart = space + 1;
            if (wordStart >= code.Length || (code[wordStart] != '|' && code[wordStart] != '>'))
            {
                return false;
            }
            for (int i = wordStart + 1; i < code.Length; i++)
            {
                if (code[i] != '+' && code[i] != '-' && !IsDigit(code[i]))
                {
                    return false;
                }
            }
            string before = code.Substring(0, Math.Max(0, space)).TrimEnd();
            return before.EndsWith(":", StringComparison.Ordinal) || before == "-";
        }

        /// <summary>YAML: the colors of the config tokenizer, and a fold for every "key:", "- key: value" and block scalar.</summary>
        private static bool TokenizeYaml(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int depth = startState & 7;
            int levels = (startState >> YamlLevelShift) & 0xFFFFFF;
            LineBuffer buffer = new LineBuffer(sink);
            int first = SkipBlanks(text, 0);
            bool code = first < len && text[first] != '#';

            int codeEnd = len;
            bool isItem = false;
            if (code)
            {
                int indent = IndentColumns(text, first);
                isItem = text[first] == '-' && (first + 1 == len || IsBlank(text[first + 1]));
                // The blocks indented as deep or deeper than this line end here; a list at the indentation of its "key:" belongs to the key.
                while (depth > 0)
                {
                    int cumulative = IndentLevelSum(levels, depth, 0x7);
                    if (cumulative < indent)
                    {
                        break;
                    }
                    bool keyBlock = ((levels >> (4 * (depth - 1))) & 8) != 0;
                    if (isItem && keyBlock && cumulative == indent)
                    {
                        break;
                    }
                    buffer.AddPairedToken(TypeNormal, 0, 0, PairIndentBlock, false, null);
                    levels &= ~(0xF << (4 * (depth - 1)));
                    depth--;
                }
            }

            TokenizeConfig(0, text, buffer, out int ignored);

            if (code)
            {
                codeEnd = YamlCodeEnd(text, first);
                string statement = text.Substring(first, codeEnd - first);
                bool endsWithColon = statement.EndsWith(":", StringComparison.Ordinal);
                bool opensBlock = endsWithColon || IsYamlBlockScalarHeader(statement) || (isItem && HasYamlKey(statement, 1));
                if (opensBlock && depth < YamlMaxLevels)
                {
                    int indent = IndentColumns(text, first);
                    int cumulative = IndentLevelSum(levels, depth, 0x7);
                    int delta = Math.Max(0, Math.Min(7, indent - cumulative));
                    int flag = (endsWithColon && !isItem) ? 8 : 0;
                    levels |= (delta | flag) << (4 * depth);
                    depth++;
                    buffer.InsertPairedToken(codeEnd, PairIndentBlock, true, ExclusiveBlockRegion);
                }
            }

            buffer.Flush(-1, -2);
            if (sink.IsLastLine)
            {
                for (; depth > 0; depth--)
                {
                    sink.AddPairedToken(TypeNormal, len, len, PairIndentBlock, false, null);
                    levels &= ~(0xF << (4 * (depth - 1)));
                }
            }
            endState = depth | (levels << YamlLevelShift);
            return true;
        }

        // ==================================================================
        // UCI (OpenWrt /etc/config/*)
        // ==================================================================

        // Line state: 0x01 - inside a "config" block, which ends at the first blank line.
        private const int StUciInBlock = 0x01;

        private static bool TokenizeUci(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int first = SkipBlanks(text, 0);
            bool inBlock = (startState & StUciInBlock) != 0;
            string word = null;
            int wordEnd = first;
            if (first < len && text[first] != '#')
            {
                while (wordEnd < len && !IsBlank(text[wordEnd]))
                {
                    wordEnd++;
                }
                word = text.Substring(first, wordEnd - first);
            }
            bool isConfig = word == "config";
            bool isStatement = isConfig || word == "option" || word == "list" || word == "package";

            // A blank line, the next "config" or "package" ends the block that is open: it stays visible below "config x...".
            if (inBlock && (first == len || isConfig || word == "package"))
            {
                sink.AddPairedToken(TypeNormal, 0, 0, PairConfigBlock, false, null);
                inBlock = false;
            }

            if (isStatement)
            {
                int last = 0;
                Normal(ref last, last, first, sink);
                sink.AddToken(TypeKeyword, first, wordEnd);
                last = wordEnd;

                // Arguments: "config <type> [name]", "option <name> <value>", "list <name> <value>".
                int i = SkipBlanks(text, wordEnd);
                int argument = 0;
                while (i < len)
                {
                    char c = text[i];
                    if (c == '#')
                    {
                        break;
                    }
                    int s = i;
                    int e;
                    if (c == '"' || c == '\'')
                    {
                        int close = FindQuoteEnd(text, i, c);
                        e = (close < 0) ? len : close;
                    }
                    else
                    {
                        e = i;
                        while (e < len && !IsBlank(text[e]))
                        {
                            e++;
                        }
                    }
                    string value = text.Substring(s, e - s);
                    int type;
                    if (argument == 0 && word != "package")
                    {
                        type = isConfig ? TypeSection : TypeVariable;
                    }
                    else if (c == '"' || c == '\'')
                    {
                        type = TypeString;
                    }
                    else
                    {
                        long number;
                        type = long.TryParse(value, out number) ? TypeNumber : TypeString;
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(type, s, e);
                    last = e;
                    argument++;
                    i = SkipBlanks(text, e);
                }

                if (isConfig)
                {
                    AddEndOfLineOpener(text, (i < len) ? i : len, PairConfigBlock, sink, ExclusiveBlockRegion);
                    inBlock = true;
                }
                if (i < len)
                {
                    sink.AddToken(TypeComment, i, len);
                }
            }
            else if (first < len && text[first] == '#')
            {
                sink.AddToken(TypeComment, first, len);
            }

            // The end of the document ends the block too.
            if (inBlock && sink.IsLastLine)
            {
                sink.AddPairedToken(TypeNormal, len, len, PairConfigBlock, false, null);
                inBlock = false;
            }
            endState = inBlock ? StUciInBlock : 0;
            return true;
        }
    }
}
