using SmarTTY.CodeEditing;
using System;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing
{
    /// <summary>
    /// Подсветка синтаксиса для встроенного редактора SmarTTY.
    ///
    /// Класс полностью автономен: он не требует правок в TokenCache / CodeEditorPP.
    /// Он поставляет:
    ///   * константы типов токенов (индекс в массиве CodeEditorPP.TokenBrushes);
    ///   * распознавание языка файла по имени/расширению;
    ///   * токенизаторы (делегаты TokenCache.LineTokenizer) для Python, Bash,
    ///     конфигов (INI/YAML) и универсальных текстовых файлов.
    ///
    /// Важно: токенизаторы создают ТОЛЬКО цветовые токены (ITokenSink.AddToken) и
    /// никогда не создают парные/складные токены, поэтому существующая логика
    /// сворачивания кода, парных скобок и выделения непомеченных токенов в
    /// TokenCache остаётся полностью нетронутой. Многострочное состояние строк
    /// (тройные кавычки Python и т.п.) выражается целочисленным битовым флагом
    /// startState/endState и поддерживается самим TokenCache.
    /// </summary>
    public static class SyntaxHighlighting
    {
        // ------------------------------------------------------------------
        // Типы токенов — индексы в массиве TokenBrushes. Индекс 0 обязан быть
        // "обычным" цветом текста (фолбэк при пустых/нулевых TokenBrushes).
        // ------------------------------------------------------------------
        public const int TypeNormal = 0;        // обычный текст
        public const int TypeKeyword = 1;       // ключевые слова
        public const int TypeString = 2;        // строки
        public const int TypeComment = 3;       // комментарии
        public const int TypeNumber = 4;        // числа
        public const int TypeVariable = 5;      // переменные (bash $var, ключи config)
        public const int TypeSection = 6;       // заголовки секций [..], ---, #!
        public const int TypePreprocessor = 7;  // shebang, директивы
        public const int TypeCount = 8;         // размер палитры

        /// <summary>Распознаваемые языки.</summary>
        public enum Language
        {
            Plain,   // без подсветки (поведение редактора не меняется)
            Python,
            Bash,
            Config,  // INI / .conf / .properties / YAML и т.п.
            Generic  // универсальные кодовые файлы
        }

        /// <summary>
        /// Распознаёт язык по имени файла (пути). Регистр расширений не важен.
        /// </summary>
        public static Language DetectLanguage(string remotePath)
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

            // Известные имена файлов без расширения (распространены в Linux).
            switch (name)
            {
                case ".bashrc":
                case ".bash_profile":
                case ".bash_login":
                case ".profile":
                case ".zshrc":
                case ".kshrc":
                case ".bash_aliases":
                case ".bash_functions":
                    return Language.Bash;

                case ".gitconfig":
                case ".vimrc":
                case ".inputrc":
                case ".gitignore":
                case ".hgignore":
                case "dockerfile":
                case "makefile":
                case "gnumakefile":
                    return Language.Generic;

                case ".ssh":
                    return Language.Config;

                case "fstab":
                case "resolv.conf":
                case "crontab":
                case "hosts":
                case "passwd":
                case "group":
                case "ssh_config":
                case "sshd_config":
                case "nginx.conf":
                case "httpd.conf":
                case "php.ini":
                    return Language.Config;

                case ".profile.d":
                    break;
            }

            if (name.StartsWith(".bash", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".zsh", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".ksh", StringComparison.OrdinalIgnoreCase))
            {
                return Language.Bash;
            }

            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1)
            {
                return Language.Plain;
            }

            switch (name.Substring(dot + 1).ToLowerInvariant())
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
                    return Language.Config;

                case "c":
                case "h":
                case "cpp":
                case "hpp":
                case "hh":
                case "cc":
                case "cxx":
                case "cs":
                case "java":
                case "go":
                case "rs":
                case "rb":
                case "js":
                case "jsx":
                case "ts":
                case "tsx":
                case "php":
                case "pl":
                case "lua":
                case "vim":
                case "sql":
                case "html":
                case "htm":
                case "xml":
                case "css":
                case "scss":
                case "md":
                case "markdown":
                case "json":
                case "toml":
                case "txt":
                case "log":
                case "csv":
                case "diff":
                case "patch":
                case "pem":
                case "pub":
                case "key":
                case "crt":
                    return Language.Generic;

                default:
                    return Language.Plain;
            }
        }

        /// <summary>Возвращает токенизатор для языка; для Plain — null (без подсветки).</summary>
        public static TokenCache.LineTokenizer GetTokenizer(Language language)
        {
            switch (language)
            {
                case Language.Python: return TokenizePython;
                case Language.Bash: return TokenizeBash;
                case Language.Config: return TokenizeConfig;
                case Language.Generic: return TokenizeGeneric;
                default: return null;
            }
        }

        // ==================================================================
        // Вспомогательные методы
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

        /// <summary>Отмечает участок обычным цветом и обновляет «хвост» last.</summary>
        private static void Normal(ref int last, int start, int end, ITokenSink sink)
        {
            if (start < end)
            {
                sink.AddToken(TypeNormal, start, end);
            }
            last = end;
        }

        /// <summary>Ищет конец строковой кавычки (с учётом экранирования \).
        /// Возвращает индекс ПОСЛЕ закрывающей кавычки либо -1, если не закрыта.</summary>
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

        // ==================================================================
        // Python
        // ==================================================================

        // Биты состояния между строками:
        //   0x01 — внутри тройных двойных кавычек """
        //   0x02 — внутри тройных одинарных кавычек '''
        private const int StPyTripleDouble = 0x01;
        private const int StPyTripleSingle = 0x02;

        private static readonly HashSet<string> PyKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "and", "as", "assert", "async", "await", "break", "class", "continue",
            "def", "del", "elif", "else", "except", "False", "finally", "for", "from",
            "global", "if", "import", "in", "is", "lambda", "None", "nonlocal", "not",
            "or", "pass", "raise", "return", "True", "try", "while", "with", "yield"
        };

        private static bool TokenizePython(int startState, string text, ITokenSink sink, out int endState)
        {
            int len = text.Length;
            bool inTripleDouble = (startState & StPyTripleDouble) != 0;
            bool inTripleSingle = (startState & StPyTripleSingle) != 0;
            int i = 0;
            int last = 0;

            // Продолжение тройной строки с предыдущей строки.
            if (inTripleDouble || inTripleSingle)
            {
                string quote = inTripleDouble ? "\"\"\"" : "'''";
                int close = text.IndexOf(quote, StringComparison.Ordinal);
                if (close < 0)
                {
                    if (len > 0)
                    {
                        sink.AddToken(TypeString, 0, len);
                    }
                    endState = startStateTerms; // остаёмся внутри тройной кавычки
                    return true;
                }
                sink.AddToken(TypeString, 0, close + 3);
                i = close + 3;
                last = i;
                inTripleDouble = false;
                inTripleSingle = false;
            }

            while (i < len)
            {
                char c = text[i];

                // Комментарий до конца строки.
                if (c == '#')
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    endState = 0;
                    return true;
                }

                // Кавычки.
                if (c == '"' || c == '\'')
                {
                    bool triple = i + 2 < len && text[i + 1] == c && text[i + 2] == c;
                    if (triple)
                    {
                        string quote = (c == '"') ? "\"\"\"" : "'''";
                        int close = text.IndexOf(quote, i + 3, StringComparison.Ordinal);
                        if (close < 0)
                        {
                            Normal(ref last, last, i, sink);
                            sink.AddToken(TypeString, i, len);
                            endState = (c == '"') ? StPyTripleDouble : StPyTripleSingle;
                            return true;
                        }
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeString, i, close + 3);
                        i = close + 3;
                        last = i;
                        continue;
                    }
                    int end = FindQuoteEnd(text, i, c);
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeString, i, (end < 0) ? len : end);
                    i = (end < 0) ? len : end;
                    last = i;
                    continue;
                }

                // Числа (включая 0x.., 0o.., 0b.., 1_000, 1e-5, 1.5j).
                if (IsDigit(c) || (c == '.' && i + 1 < len && IsDigit(text[i + 1])))
                {
                    int s = i;
                    while (i < len)
                    {
                        char n = text[i];
                        if (IsDigit(n) || n == '_' || n == '.' || n == 'x' || n == 'X' ||
                            n == 'o' || n == 'O' || n == 'b' || n == 'B' || n == 'e' || n == 'E' ||
                            n == '+' || n == '-' || n == 'j' || n == 'J' ||
                            (n >= 'a' && n <= 'f') || (n >= 'A' && n <= 'F'))
                        {
                            // '+'/'-' отбрасываем, если они не внутри показателя степени.
                            if ((n == '+' || n == '-') &&
                                !(i > s && (text[i - 1] == 'e' || text[i - 1] == 'E')))
                            {
                                break;
                            }
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

                // Слова — ключевые слова и префиксы строк (f, r, b, rf, fr).
                if (IsIdStart(c))
                {
                    int s = i;
                    while (i < len && IsIdChar(text[i]))
                    {
                        i++;
                    }
                    string word = text.Substring(s, i - s);
                    if (PyKeywords.Contains(word))
                    {
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeKeyword, s, i);
                        last = i;
                        continue;
                    }
                    // Обычный идентификатор / префикс строки — обычный цвет.
                    Normal(ref last, last, s, sink);
                    last = i;
                    continue;
                }

                i++;
            }

            if (last < len)
            {
                sink.AddToken(TypeNormal, last, len);
            }
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
            "readonly", "declare", "return", "break", "continue", "source", ".", "shift",
            "unset", "alias", "echo", "printf", "read", "trap", "exec", "exit", "set",
            "unset", "let"
        };

        private static bool TokenizeBash(int startState, string text, ITokenSink sink, out int endState)
        {
            int len = text.Length;
            bool inDouble = (startState & StBashDouble) != 0;
            bool inSingle = (startState & StBashSingle) != 0;
            int i = 0;
            int last = 0;

            // Продолжение кавычки с предыдущей строки.
            if (inDouble || inSingle)
            {
                char quote = inDouble ? '"' : '\'';
                int end = FindQuoteEnd(text, -1, quote);
                // FindQuoteEnd ожидает start < len; для незакрытой строки обработаем вручную.
                end = -1;
                if (quote == '"')
                {
                    int closeIdx = 0;
                    while (closeIdx < len)
                    {
                        if (text[closeIdx] == '"')
                        {
                            end = closeIdx + 1;
                            break;
                        }
                        closeIdx++;
                    }
                }
                if (end < 0)
                {
                    if (len > 0)
                    {
                        sink.AddToken(TypeString, 0, len);
                    }
                    endState = startState; // кавычка всё ещё не закрыта
                    return true;
                }
                sink.AddToken(TypeString, 0, end);
                i = end;
                last = i;
                inDouble = false;
                inSingle = false;
            }

            while (i < len)
            {
                char c = text[i];

                // Комментарий: '#' в начале слова (в начале строки или после пробела).
                if (c == '#' && (i == 0 || text[i - 1] == ' ' || text[i - 1] == '\t'))
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    endState = 0;
                    return true;
                }

                // Одинарные кавычки (без экранирования).
                if (c == '\'')
                {
                    int end = text.IndexOf('\'', i + 1);
                    Normal(ref last, last, i, sink);
                    int e = (end < 0) ? len : (end + 1);
                    sink.AddToken(TypeString, i, e);
                    i = e;
                    last = i;
                    continue;
                }

                // Двойные кавычки (с экранированием и продолжением строки).
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

                // Переменные $var, ${var}, $1, $*, $$, $@.
                if (c == '$')
                {
                    Normal(ref last, last, i, sink);
                    int s = i + 1;
                    if (s < len && text[s] == '{')
                    {
                        int close = text.IndexOf('}', s + 1);
                        int e = (close < 0) ? len : (close + 1);
                        sink.AddToken(TypeVariable, i, e);
                        i = e;
                        last = i;
                        continue;
                    }
                    if (s < len && (IsIdStart(text[s]) || IsDigit(text[s])))
                    {
                        int e = s;
                        string specials = "@*#?$!-0123456789";
                        if (specials.IndexOf(text[s]) >= 0 && !IsIdStart(text[s]))
                        {
                            e = s + 1;
                        }
                        else
                        {
                            while (e < len && IsIdChar(text[e]))
                            {
                                e++;
                            }
                        }
                        sink.AddToken(TypeVariable, i, e);
                        i = e;
                        last = i;
                        continue;
                    }
                    // Уединённый '$' — обычный цвет.
                    sink.AddToken(TypeNormal, i, i + 1);
                    i++;
                    last = i;
                    continue;
                }

                // Числа.
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

                // Слова — ключевые слова и присваивания (var=value).
                if (IsIdStart(c))
                {
                    int s = i;
                    while (i < len && IsIdChar(text[i]))
                    {
                        i++;
                    }
                    string word = text.Substring(s, i - s);
                    Normal(ref last, last, s, sink);
                    // Присваивание: слово = ...
                    int probe = i;
                    while (probe < len && (text[probe] == ' ' || text[probe] == '\t'))
                    {
                        probe++;
                    }
                    if (probe < len && text[probe] == '=')
                    {
                        sink.AddToken(TypeVariable, s, i);
                        last = i;
                        continue;
                    }
                    if (BashKeywords.Contains(word))
                    {
                        sink.AddToken(TypeKeyword, s, i);
                    }
                    else
                    {
                        sink.AddToken(TypeNormal, s, i);
                    }
                    last = i;
                    continue;
                }

                i++;
            }

            if (last < len)
            {
                sink.AddToken(TypeNormal, last, len);
            }
            endState = 0;
            return true;
        }

        // ==================================================================
        // Config (INI / .conf / .properties / YAML)
        // ==================================================================

        private static bool TokenizeConfig(int startState, string text, ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int i = 0;
            int last = 0;

            while (i < len)
            {
                char c = text[i];

                // Комментарии '#' и ';'.
                if (c == '#' || c == ';')
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    endState = 0;
                    return true;
                }

                // Заголовки секций [section] и YAML-документные маркеры '---'.
                if (c == '[')
                {
                    int close = text.IndexOf(']', i + 1);
                    if (close >= 0)
                    {
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeSection, i, close + 1);
                        i = close + 1;
                        last = i;
                        continue;
                    }
                }
                if (c == '-' && i + 1 < len && text[i + 1] == '-')
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeSection, i, len);
                    endState = 0;
                    return true;
                }

                // Строки в кавычках.
                if (c == '"' || c == '\'')
                {
                    int end = FindQuoteEnd(text, i, c);
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeString, i, (end < 0) ? len : end);
                    i = (end < 0) ? len : end;
                    last = i;
                    continue;
                }

                // Ключи "name:" / "name = value" (в т.ч. с пробелами между словами — 
                // покрасим ключ, только если сразу после идёт '=' и ':' на той же паре слов).
                if (IsIdStart(c) || c == '.')
                {
                    int s = i;
                    while (i < len && (IsIdChar(text[i]) || text[i] == '.' || text[i] == '-'))
                    {
                        i++;
                    }
                    // Найденное слово — ключ, если после пробелов идёт '=' или равно ":" без значений.
                    int probe = i;
                    while (probe < len && (text[probe] == ' ' || text[probe] == '\t'))
                    {
                        probe++;
                    }
                    bool isKey = probe < len && (text[probe] == '=' || text[probe] == ':');
                    if (isKey)
                    {
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeVariable, s, i);
                        last = i;
                        continue;
                    }
                    // Обычное слово.
                    Normal(ref last, last, s, sink);
                    last = i;
                    continue;
                }

                i++;
            }

            if (last < len)
            {
                sink.AddToken(TypeNormal, last, len);
            }
            endState = 0;
            return true;
        }

        // ==================================================================
        // Generic (универсальные кодовые и текстовые файлы)
        // ==================================================================

        private static readonly HashSet<string> GenericKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "else", "elif", "for", "while", "do", "done", "return", "break",
            "continue", "def", "class", "function", "var", "let", "const", "new",
            "true", "false", "null", "import", "from", "export", "public", "private",
            "static", "void", "int", "string", "bool", "switch", "case", "default",
            "try", "catch", "finally", "throw", "struct", "enum", "namespace"
        };

        private static bool TokenizeGeneric(int startState, string text, ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int i = 0;
            int last = 0;

            while (i < len)
            {
                char c = text[i];

                // Комментарии: '#', '//', '/* ... */'.
                if (c == '#' || (c == '/' && i + 1 < len && text[i + 1] == '/'))
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    endState = 0;
                    return true;
                }
                if (c == '/' && i + 1 < len && text[i + 1] == '*')
                {
                    int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeComment, i, len);
                        endState = 0;
                        return true;
                    }
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, close + 2);
                    i = close + 2;
                    last = i;
                    continue;
                }

                // Строки.
                if (c == '"' || c == '\'')
                {
                    int end = FindQuoteEnd(text, i, c);
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeString, i, (end < 0) ? len : end);
                    i = (end < 0) ? len : end;
                    last = i;
                    continue;
                }

                // Числа.
                if (IsDigit(c))
                {
                    int s = i;
                    while (i < len && (IsDigit(text[i]) || text[i] == '.' || text[i] == 'x' ||
                                       text[i] == 'X' || text[i] == 'e' || text[i] == 'E'))
                    {
                        i++;
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(TypeNumber, s, i);
                    last = i;
                    continue;
                }

                // Слова — ключевые слова.
                if (IsIdStart(c))
                {
                    int s = i;
                    while (i < len && IsIdChar(text[i]))
                    {
                        i++;
                    }
                    string word = text.Substring(s, i - s);
                    Normal(ref last, last, s, sink);
                    if (GenericKeywords.Contains(word))
                    {
                        sink.AddToken(TypeKeyword, s, i);
                    }
                    else
                    {
                        sink.AddToken(TypeNormal, s, i);
                    }
                    last = i;
                    continue;
                }

                i++;
            }

            if (last < len)
            {
                sink.AddToken(TypeNormal, last, len);
            }
            endState = 0;
            return true;
        }
    }
}
