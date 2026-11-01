using System;
using System.Collections.Generic;
using SmarTTY.CodeEditing;

namespace SmarTTY.Completion;

/// <summary>
/// Builds the parameter hint of the editor: the parameter list of the function whose call the caret is
/// inside. The definitions are read from the document itself, so no external database is needed.
///
/// The class has no UI dependency, so the test harness can check it without a display. It is
/// deliberately conservative: it only recognizes the declaration forms of the languages it knows and
/// returns nothing when it is unsure, so a hint never shows a wrong signature.
/// </summary>
public static class EditorSignatureSource
{
    /// <summary>The function definitions found in a document, in the order they appear.</summary>
    public static List<ParameterSignature> ParseDefinitions(SyntaxHighlighting.Language language, string documentText)
    {
        return ParseDefinitionsNormalized(language, Normalize(documentText));
    }

    /// <summary>
    /// The signature of the call the caret is inside, together with the index of the parameter being
    /// typed. Returns null when the caret is not inside a call. A function defined in the document wins;
    /// otherwise the standard API symbols of <paramref name="database"/> are consulted.
    /// </summary>
    public static ParameterSignature FindSignature(SyntaxHighlighting.Language language, string documentText, int line, int column, out int activeParameter)
    {
        return FindSignature(language, documentText, line, column, null, out activeParameter);
    }

    /// <summary>The overload that falls back to the standard API symbols of <paramref name="database"/>.</summary>
    public static ParameterSignature FindSignature(SyntaxHighlighting.Language language, string documentText, int line, int column, ApiSymbolDatabase database, out int activeParameter)
    {
        activeParameter = 0;
        if (documentText == null || line < 0 || column < 0)
        {
            return null;
        }
        string text = Normalize(documentText);
        if (!FindCall(language, text, line, column, out string name, out activeParameter))
        {
            return null;
        }
        foreach (ParameterSignature signature in ParseDefinitionsNormalized(language, text))
        {
            if (signature.Name == name)
            {
                if (activeParameter >= signature.ParameterCount)
                {
                    activeParameter = Math.Max(0, signature.ParameterCount - 1);
                }
                return signature;
            }
        }
        if (database != null)
        {
            ParameterSignature apiSignature = database.FindSignature(language, name);
            if (apiSignature != null)
            {
                if (activeParameter >= apiSignature.ParameterCount)
                {
                    activeParameter = Math.Max(0, apiSignature.ParameterCount - 1);
                }
                return apiSignature;
            }
        }
        return null;
    }

    /// <summary>
    /// The name of the function whose call the caret is inside and the number of commas already typed
    /// at the call's own level (the parameter being edited). False when the caret is not inside a call.
    /// </summary>
    public static bool FindCall(SyntaxHighlighting.Language language, string documentText, int line, int column, out string name, out int activeParameter)
    {
        name = null;
        activeParameter = 0;
        if (documentText == null || line < 0 || column < 0)
        {
            return false;
        }
        string text = Normalize(documentText);
        int offset = OffsetOf(text, line, column);
        if (offset < 0)
        {
            return false;
        }
        return FindCallAt(language, text, offset, out name, out activeParameter);
    }

    private static List<ParameterSignature> ParseDefinitionsNormalized(SyntaxHighlighting.Language language, string text)
    {
        List<ParameterSignature> result = new List<ParameterSignature>();
        int offset = 0;
        foreach (string line in text.Split('\n'))
        {
            if (TryParseDefinitionHead(language, line, out string name, out int openParen, out bool skipFirstName))
            {
                string inner = ExtractParenGroup(text, offset + openParen);
                if (inner != null)
                {
                    if (skipFirstName)
                    {
                        inner = RemoveFirstWord(inner);
                    }
                    result.Add(new ParameterSignature(name, ParameterSignature.SplitParameters(inner)));
                }
            }
            offset += line.Length + 1;
        }
        return result;
    }

    private static bool FindCallAt(SyntaxHighlighting.Language language, string text, int offset, out string name, out int activeParameter)
    {
        name = null;
        activeParameter = 0;
        List<int> stack = new List<int>();
        int i = 0;
        while (i < offset)
        {
            char c = text[i];
            if (c == '\'' || c == '"')
            {
                i = SkipString(text, i, offset);
                continue;
            }
            if (IsBlockCommentStart(language, text, i))
            {
                int commentEnd = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = (commentEnd < 0 || commentEnd + 2 > offset) ? offset : commentEnd + 2;
                continue;
            }
            if (IsLineCommentStart(language, text, i))
            {
                while (i < offset && text[i] != '\n')
                {
                    i++;
                }
                continue;
            }
            if (c == '(')
            {
                stack.Add(i);
            }
            else if (c == ')' && stack.Count > 0)
            {
                stack.RemoveAt(stack.Count - 1);
            }
            i++;
        }
        if (stack.Count == 0)
        {
            return false;
        }
        int top = stack[stack.Count - 1];
        int depth = stack.Count;
        int end = top - 1;
        while (end >= 0 && char.IsWhiteSpace(text[end]))
        {
            end--;
        }
        int start = end;
        while (start >= 0 && EditorCompletionSource.IsIdentifierChar(text[start]))
        {
            start--;
        }
        start++;
        if (start > end)
        {
            return false;
        }
        name = text.Substring(start, end - start + 1);
        activeParameter = CountParameters(language, text, top + 1, offset, depth);
        return true;
    }

    private static int CountParameters(SyntaxHighlighting.Language language, string text, int from, int to, int depth)
    {
        int count = 0;
        int current = depth;
        int i = from;
        while (i < to)
        {
            char c = text[i];
            if (c == '\'' || c == '"')
            {
                i = SkipString(text, i, to);
                continue;
            }
            if (IsBlockCommentStart(language, text, i))
            {
                int commentEnd = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = (commentEnd < 0 || commentEnd + 2 > to) ? to : commentEnd + 2;
                continue;
            }
            if (IsLineCommentStart(language, text, i))
            {
                while (i < to && text[i] != '\n')
                {
                    i++;
                }
                continue;
            }
            if (c == '(' || c == '[' || c == '{')
            {
                current++;
            }
            else if (c == ')' || c == ']' || c == '}')
            {
                current--;
            }
            else if (c == ',' && current == depth)
            {
                count++;
            }
            i++;
        }
        return count;
    }

    private static bool TryParseDefinitionHead(SyntaxHighlighting.Language language, string line, out string name, out int openParen, out bool skipFirstName)
    {
        name = null;
        openParen = -1;
        skipFirstName = false;
        string text = StripLineComment(language, line);
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        if (i >= text.Length)
        {
            return false;
        }
        switch (language)
        {
            case SyntaxHighlighting.Language.Python:
                return TryKeywordDefinition(text, i, out name, out openParen, "def");
            case SyntaxHighlighting.Language.Lua:
                if (StartsWithWord(text, i, "local"))
                {
                    int after = SkipWord(text, i + "local".Length);
                    while (after < text.Length && char.IsWhiteSpace(text[after]))
                    {
                        after++;
                    }
                    return TryKeywordDefinition(text, after, out name, out openParen, "function");
                }
                return TryKeywordDefinition(text, i, out name, out openParen, "function");
            case SyntaxHighlighting.Language.Bash:
                return TryBareDefinition(text, i, out name, out openParen);
            case SyntaxHighlighting.Language.Ruby:
                return TryKeywordDefinition(text, i, out name, out openParen, "def");
            case SyntaxHighlighting.Language.Perl:
                return TryKeywordDefinition(text, i, out name, out openParen, "sub");
            case SyntaxHighlighting.Language.CMake:
                skipFirstName = TryCMakeDefinition(text, i, out name, out openParen);
                return skipFirstName;
            case SyntaxHighlighting.Language.Script:
                return TryKeywordDefinition(text, i, out name, out openParen, "function", "fn")
                    || TryBareDefinition(text, i, out name, out openParen);
            case SyntaxHighlighting.Language.CLike:
            case SyntaxHighlighting.Language.Cpp:
                return TryKeywordDefinition(text, i, out name, out openParen, "function", "func", "fn")
                    || TryCStyleDefinition(text, i, out name, out openParen);
            default:
                return false;
        }
    }

    private static bool TryKeywordDefinition(string text, int start, out string name, out int openParen, params string[] keywords)
    {
        name = null;
        openParen = -1;
        foreach (string keyword in keywords)
        {
            if (!StartsWithWord(text, start, keyword))
            {
                continue;
            }
            int p = SkipWord(text, start + keyword.Length);
            while (p < text.Length && char.IsWhiteSpace(text[p]))
            {
                p++;
            }
            int nameStart = p;
            // A dotted name ("function M.add") keeps its last segment, which is the callable one.
            while (p < text.Length && (EditorCompletionSource.IsIdentifierChar(text[p]) || text[p] == '.'))
            {
                p++;
            }
            if (p == nameStart)
            {
                continue;
            }
            string rawName = text.Substring(nameStart, p - nameStart);
            int dot = rawName.LastIndexOf('.');
            string shortName = (dot >= 0) ? rawName.Substring(dot + 1) : rawName;
            if (shortName.Length == 0)
            {
                continue;
            }
            int q = p;
            while (q < text.Length && char.IsWhiteSpace(text[q]))
            {
                q++;
            }
            if (q < text.Length && text[q] == '(')
            {
                name = shortName;
                openParen = q;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A CMake definition: "function(name arg1 arg2)" or "macro(name ...)". The first argument inside the
    /// parentheses is the name of the function or macro, not a parameter.
    /// </summary>
    private static bool TryCMakeDefinition(string text, int start, out string name, out int openParen)
    {
        name = null;
        openParen = -1;
        foreach (string keyword in new[] { "function", "macro" })
        {
            if (!StartsWithWord(text, start, keyword))
            {
                continue;
            }
            int p = start + keyword.Length;
            while (p < text.Length && char.IsWhiteSpace(text[p]))
            {
                p++;
            }
            if (p >= text.Length || text[p] != '(')
            {
                continue;
            }
            int q = p + 1;
            while (q < text.Length && char.IsWhiteSpace(text[q]))
            {
                q++;
            }
            int nameStart = q;
            while (q < text.Length && EditorCompletionSource.IsIdentifierChar(text[q]))
            {
                q++;
            }
            if (q == nameStart)
            {
                continue;
            }
            name = text.Substring(nameStart, q - nameStart);
            openParen = p;
            return true;
        }
        return false;
    }

    /// <summary>Removes the first whitespace-separated word, used for the name of a CMake definition.</summary>
    private static string RemoveFirstWord(string text)
    {
        int i = 0;
        while (i < text.Length && !char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        return text.Substring(i);
    }

    /// <summary>A shell-style definition: "name(args) { ... }" (the brace makes it a definition, not a call).</summary>
    private static bool TryBareDefinition(string text, int start, out string name, out int openParen)
    {
        name = null;
        openParen = -1;
        int p = start;
        int nameStart = p;
        while (p < text.Length && EditorCompletionSource.IsIdentifierChar(text[p]))
        {
            p++;
        }
        if (p == nameStart)
        {
            return false;
        }
        int q = p;
        while (q < text.Length && char.IsWhiteSpace(text[q]))
        {
            q++;
        }
        if (q >= text.Length || text[q] != '(')
        {
            return false;
        }
        int match = FindMatchingParen(text, q);
        if (match < 0)
        {
            return false;
        }
        int after = match + 1;
        while (after < text.Length && char.IsWhiteSpace(text[after]))
        {
            after++;
        }
        if (after >= text.Length || text[after] != '{')
        {
            return false;
        }
        name = text.Substring(nameStart, p - nameStart);
        openParen = q;
        return true;
    }

    private static bool TryCStyleDefinition(string text, int start, out string name, out int openParen)
    {
        name = null;
        openParen = -1;
        int paren = text.IndexOf('(', start);
        if (paren < 0)
        {
            return false;
        }
        int end = paren - 1;
        while (end >= start && char.IsWhiteSpace(text[end]))
        {
            end--;
        }
        int nameStart = end;
        while (nameStart >= start && EditorCompletionSource.IsIdentifierChar(text[nameStart]))
        {
            nameStart--;
        }
        nameStart++;
        if (nameStart > end)
        {
            return false;
        }
        // The text before the name is the return type; a call at statement start has none, and an
        // assignment ("x = foo(") or a control statement ("return foo(") is not a definition.
        if (!IsTypePrefix(text.Substring(start, nameStart - start).Trim()))
        {
            return false;
        }
        int match = FindMatchingParen(text, paren);
        if (match < 0)
        {
            return false;
        }
        int after = match + 1;
        while (after < text.Length && char.IsWhiteSpace(text[after]))
        {
            after++;
        }
        if (after >= text.Length || (text[after] != '{' && text[after] != ';'))
        {
            return false;
        }
        name = text.Substring(nameStart, end - nameStart + 1);
        openParen = paren;
        return true;
    }

    private static bool IsTypePrefix(string prefix)
    {
        if (prefix.Length == 0)
        {
            return false;
        }
        foreach (char c in prefix)
        {
            if (!(EditorCompletionSource.IsIdentifierChar(c) || char.IsWhiteSpace(c) || "*&:<>,~".IndexOf(c) >= 0))
            {
                return false;
            }
        }
        return !IsStatementKeyword(FirstWord(prefix));
    }

    private static bool IsStatementKeyword(string word)
    {
        switch (word)
        {
            case "return":
            case "if":
            case "else":
            case "for":
            case "foreach":
            case "while":
            case "do":
            case "switch":
            case "case":
            case "default":
            case "catch":
            case "throw":
            case "new":
            case "delete":
            case "sizeof":
            case "typeof":
            case "using":
            case "lock":
            case "when":
            case "assert":
            case "and":
            case "or":
            case "not":
            case "in":
            case "is":
            case "yield":
            case "await":
            case "goto":
                return true;
            default:
                return false;
        }
    }

    private static string ExtractParenGroup(string text, int openOffset)
    {
        if (openOffset < 0 || openOffset >= text.Length || text[openOffset] != '(')
        {
            return null;
        }
        int depth = 0;
        int i = openOffset;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\'' || c == '"')
            {
                i = SkipString(text, i, text.Length);
                continue;
            }
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(openOffset + 1, i - openOffset - 1);
                }
            }
            i++;
        }
        return null;
    }

    private static int FindMatchingParen(string text, int openOffset)
    {
        if (openOffset < 0 || openOffset >= text.Length || text[openOffset] != '(')
        {
            return -1;
        }
        int depth = 0;
        for (int i = openOffset; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'' || c == '"')
            {
                i = SkipString(text, i, text.Length) - 1;
                continue;
            }
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    private static string StripLineComment(SyntaxHighlighting.Language language, string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\'' || line[i] == '"')
            {
                i = SkipString(line, i, line.Length) - 1;
                continue;
            }
            if (IsLineCommentStart(language, line, i))
            {
                return line.Substring(0, i);
            }
        }
        return line;
    }

    private static bool IsLineCommentStart(SyntaxHighlighting.Language language, string text, int i)
    {
        char c = text[i];
        char next = (i + 1 < text.Length) ? text[i + 1] : '\0';
        switch (language)
        {
            case SyntaxHighlighting.Language.CLike:
            case SyntaxHighlighting.Language.Cpp:
                return c == '/' && next == '/';
            case SyntaxHighlighting.Language.Sql:
            case SyntaxHighlighting.Language.Lua:
                return c == '-' && next == '-';
            case SyntaxHighlighting.Language.Python:
            case SyntaxHighlighting.Language.Bash:
            case SyntaxHighlighting.Language.Script:
            case SyntaxHighlighting.Language.Ruby:
            case SyntaxHighlighting.Language.Perl:
            case SyntaxHighlighting.Language.CMake:
            case SyntaxHighlighting.Language.Dockerfile:
            case SyntaxHighlighting.Language.Config:
            case SyntaxHighlighting.Language.Systemd:
            case SyntaxHighlighting.Language.Nginx:
            case SyntaxHighlighting.Language.SshConfig:
            case SyntaxHighlighting.Language.Makefile:
            case SyntaxHighlighting.Language.Uci:
            case SyntaxHighlighting.Language.Yaml:
                return c == '#';
            default:
                return false;
        }
    }

    private static bool IsBlockCommentStart(SyntaxHighlighting.Language language, string text, int i)
    {
        if (i + 1 >= text.Length || text[i] != '/')
        {
            return false;
        }
        if (language != SyntaxHighlighting.Language.CLike && language != SyntaxHighlighting.Language.Cpp &&
            language != SyntaxHighlighting.Language.Sql)
        {
            return false;
        }
        return text[i + 1] == '*';
    }

    private static int SkipString(string text, int i, int limit)
    {
        char quote = text[i];
        i++;
        while (i < limit)
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
        return limit;
    }

    private static bool StartsWithWord(string text, int index, string word)
    {
        if (index + word.Length > text.Length)
        {
            return false;
        }
        if (string.CompareOrdinal(text, index, word, 0, word.Length) != 0)
        {
            return false;
        }
        int after = index + word.Length;
        return after >= text.Length || !EditorCompletionSource.IsIdentifierChar(text[after]);
    }

    private static int SkipWord(string text, int index)
    {
        int i = index;
        while (i < text.Length && EditorCompletionSource.IsIdentifierChar(text[i]))
        {
            i++;
        }
        return i;
    }

    private static string FirstWord(string text)
    {
        int i = 0;
        while (i < text.Length && !EditorCompletionSource.IsIdentifierChar(text[i]))
        {
            i++;
        }
        int start = i;
        while (i < text.Length && EditorCompletionSource.IsIdentifierChar(text[i]))
        {
            i++;
        }
        return text.Substring(start, i - start);
    }

    private static int OffsetOf(string text, int line, int column)
    {
        int offset = 0;
        int current = 0;
        while (current < line && offset < text.Length)
        {
            if (text[offset] == '\n')
            {
                current++;
            }
            offset++;
        }
        if (current < line)
        {
            return -1;
        }
        return offset + column;
    }

    private static string Normalize(string text)
    {
        if (text == null || text.IndexOf('\r') < 0)
        {
            return text ?? "";
        }
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}
