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
        int offset = OffsetOf(text, line, column);
        if (offset < 0 || !FindCallAt(language, text, offset, out string name, out activeParameter, out bool qualified))
        {
            return null;
        }
        foreach (ParameterSignature definition in ParseDefinitionsNormalized(language, text))
        {
            if (definition.Name == name)
            {
                ParameterSignature signature = DropReceiver(language, definition, qualified);
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
        return FindCallAt(language, text, offset, out name, out activeParameter, out _);
    }

    /// <summary>
    /// "obj.method(" calls a method with the object as its first parameter, which the definition of Python ("self", "cls") and Rust
    /// ("self", "&amp;self", "&amp;mut self") names but the call does not: without it the hint would mark the wrong parameter.
    /// </summary>
    private static ParameterSignature DropReceiver(SyntaxHighlighting.Language language, ParameterSignature signature, bool qualified)
    {
        if (!qualified || signature.ParameterCount == 0 ||
            (language != SyntaxHighlighting.Language.Python && language != SyntaxHighlighting.Language.Rust))
        {
            return signature;
        }
        string first = signature.GetParameter(0).Text;
        int colon = first.IndexOf(':');
        string bare = (colon >= 0 ? first.Substring(0, colon) : first).Trim();
        bool receiver = language == SyntaxHighlighting.Language.Python
            ? (bare == "self" || bare == "cls")
            : (bare == "self" || bare == "&self" || bare == "&mut self" || bare == "mut self" || bare == "& self" || bare == "& mut self");
        if (!receiver)
        {
            return signature;
        }
        string[] rest = new string[signature.ParameterCount - 1];
        for (int i = 1; i < signature.ParameterCount; i++)
        {
            rest[i - 1] = signature.GetParameter(i).Text;
        }
        return new ParameterSignature(signature.Name, rest);
    }

    /// <summary>
    /// Languages with type arguments in angle brackets ("Map&lt;String, int&gt;"), where a comma inside "&lt;&gt;" does not separate parameters.
    /// </summary>
    private static bool UsesAngleBrackets(SyntaxHighlighting.Language language)
    {
        switch (language)
        {
            case SyntaxHighlighting.Language.Cpp:
            case SyntaxHighlighting.Language.CSharp:
            case SyntaxHighlighting.Language.Java:
            case SyntaxHighlighting.Language.Kotlin:
            case SyntaxHighlighting.Language.Scala:
            case SyntaxHighlighting.Language.Groovy:
            case SyntaxHighlighting.Language.Rust:
            case SyntaxHighlighting.Language.Swift:
            case SyntaxHighlighting.Language.Dart:
            case SyntaxHighlighting.Language.TypeScript:
                return true;
            default:
                return false;
        }
    }

    private static List<ParameterSignature> ParseDefinitionsNormalized(SyntaxHighlighting.Language language, string text)
    {
        List<ParameterSignature> result = new List<ParameterSignature>();
        int offset = 0;
        foreach (string line in text.Split('\n'))
        {
            if (TryParseDefinitionHead(language, line, text, offset, out string name, out int openParen, out bool skipFirstName))
            {
                string inner = ExtractParenGroup(language, text, offset + openParen);
                if (inner != null)
                {
                    // A parameter list that goes over several lines may have comments between the parameters.
                    inner = StripComments(language, inner);
                    if (skipFirstName)
                    {
                        inner = RemoveFirstWord(inner);
                    }
                    result.Add(new ParameterSignature(name, ParameterSignature.SplitParameters(inner, UsesAngleBrackets(language), language != SyntaxHighlighting.Language.Rust)));
                }
            }
            offset += line.Length + 1;
        }
        return result;
    }

    private static bool FindCallAt(SyntaxHighlighting.Language language, string text, int offset, out string name, out int activeParameter, out bool qualified)
    {
        name = null;
        activeParameter = 0;
        qualified = false;
        offset = Math.Min(offset, text.Length);
        List<int> stack = new List<int>();
        int i = 0;
        while (i < offset)
        {
            char c = text[i];
            if (IsStringQuote(language, text, i))
            {
                i = SkipString(text, i, offset);
                continue;
            }
            if (TrySkipComment(language, text, ref i, offset))
            {
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
        if (end >= 0 && text[end] == '>' && UsesAngleBrackets(language))
        {
            // A call with type arguments ("convert<int>(", "parse::<T>(" in Rust): the name is in front of them.
            int open = FindTypeArgumentsStart(text, end);
            if (open >= 0)
            {
                end = open - 1;
                if (end >= 1 && text[end] == ':' && text[end - 1] == ':')
                {
                    end -= 2;
                }
                while (end >= 0 && char.IsWhiteSpace(text[end]))
                {
                    end--;
                }
            }
        }
        int nameEnd = end;
        if (language == SyntaxHighlighting.Language.Ruby && end >= 0 && (text[end] == '?' || text[end] == '!'))
        {
            // Ruby predicates and bang methods: "empty?(" and "sort!(".
            end--;
        }
        int start = end;
        while (start >= 0 && EditorCompletionSource.IsIdentifierChar(text[start]))
        {
            start--;
        }
        start++;
        if (start > end || nameEnd < 0)
        {
            return false;
        }
        name = text.Substring(start, nameEnd - start + 1);
        qualified = start > 0 && text[start - 1] == '.';
        activeParameter = CountParameters(language, text, top + 1, offset, depth);
        return true;
    }

    /// <summary>The text without its comments (each is replaced by a space); strings are left as they are.</summary>
    private static string StripComments(SyntaxHighlighting.Language language, string text)
    {
        System.Text.StringBuilder result = null;
        int copied = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (IsStringQuote(language, text, i))
            {
                i = SkipString(text, i, text.Length);
                continue;
            }
            int start = i;
            if (TrySkipComment(language, text, ref i, text.Length))
            {
                result = result ?? new System.Text.StringBuilder();
                result.Append(text, copied, start - copied).Append(' ');
                copied = i;
                continue;
            }
            i++;
        }
        if (result == null)
        {
            return text;
        }
        result.Append(text, copied, text.Length - copied);
        return result.ToString();
    }

    /// <summary>The "&lt;" that matches the "&gt;" at <paramref name="close"/> (on the same line), or -1.</summary>
    private static int FindTypeArgumentsStart(string text, int close)
    {
        int depth = 0;
        for (int i = close; i >= 0 && close - i < 200; i--)
        {
            char c = text[i];
            if (c == '\n' || c == ';' || c == '{' || c == '}' || c == '(' || c == ')')
            {
                return -1;
            }
            if (c == '>')
            {
                depth++;
            }
            else if (c == '<')
            {
                depth--;
                if (depth == 0)
                {
                    return i > 0 && (EditorCompletionSource.IsIdentifierChar(text[i - 1]) || text[i - 1] == ':' || char.IsWhiteSpace(text[i - 1])) ? i : -1;
                }
            }
        }
        return -1;
    }

    /// <summary>
    /// Steps over a comment that starts at <paramref name="i"/> (up to <paramref name="limit"/>). A string that has an apostrophe in a comment
    /// ("don't") must not open a string of its own.
    /// </summary>
    private static bool TrySkipComment(SyntaxHighlighting.Language language, string text, ref int i, int limit)
    {
        if (IsBlockCommentStart(language, text, i))
        {
            int commentEnd = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            i = (commentEnd < 0 || commentEnd + 2 > limit) ? limit : commentEnd + 2;
            return true;
        }
        if (IsLineCommentStart(language, text, i))
        {
            while (i < limit && text[i] != '\n')
            {
                i++;
            }
            return true;
        }
        return false;
    }

    private static int CountParameters(SyntaxHighlighting.Language language, string text, int from, int to, int depth)
    {
        int count = 0;
        int current = depth;
        int angle = 0;
        bool angles = UsesAngleBrackets(language);
        int i = from;
        while (i < to)
        {
            char c = text[i];
            if (IsStringQuote(language, text, i))
            {
                i = SkipString(text, i, to);
                continue;
            }
            if (TrySkipComment(language, text, ref i, to))
            {
                continue;
            }
            if (angles && c == '<' && i > 0 && EditorCompletionSource.IsIdentifierChar(text[i - 1]))
            {
                // "new Dictionary<string, int>()": the comma between the type arguments does not start another argument.
                angle++;
            }
            else if (angles && c == '>' && angle > 0 && text[i - 1] != '-' && text[i - 1] != '=')
            {
                angle--;
            }
            else if (c == '(' || c == '[' || c == '{')
            {
                current++;
            }
            else if (c == ')' || c == ']' || c == '}')
            {
                current--;
            }
            else if (c == ',' && current == depth && angle == 0)
            {
                count++;
            }
            i++;
        }
        return count;
    }

    // line: the text of one line; fullText and lineStart: the whole document and where the line begins in it (the parameter list and the
    // brace of a definition can be on the next lines).
    private static bool TryParseDefinitionHead(SyntaxHighlighting.Language language, string line, string fullText, int lineStart, out string name, out int openParen, out bool skipFirstName)
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
                return TryBareDefinition(language, text, i, fullText, lineStart, out name, out openParen);
            case SyntaxHighlighting.Language.Ruby:
                return TryKeywordDefinition(text, i, out name, out openParen, "def");
            case SyntaxHighlighting.Language.Perl:
                return TryKeywordDefinition(text, i, out name, out openParen, "sub");
            case SyntaxHighlighting.Language.CMake:
                skipFirstName = TryCMakeDefinition(text, i, out name, out openParen);
                return skipFirstName;
            case SyntaxHighlighting.Language.Awk:
                return TryKeywordDefinition(text, i, out name, out openParen, "function", "func");
            case SyntaxHighlighting.Language.Script:
                return TryKeywordDefinition(text, i, out name, out openParen, "function", "fn")
                    || TryBareDefinition(language, text, i, fullText, lineStart, out name, out openParen);
            case SyntaxHighlighting.Language.CLike:
            case SyntaxHighlighting.Language.Cpp:
            case SyntaxHighlighting.Language.CSharp:
            case SyntaxHighlighting.Language.Java:
            case SyntaxHighlighting.Language.Kotlin:
            case SyntaxHighlighting.Language.Scala:
            case SyntaxHighlighting.Language.Groovy:
            case SyntaxHighlighting.Language.Go:
            case SyntaxHighlighting.Language.Rust:
            case SyntaxHighlighting.Language.Swift:
            case SyntaxHighlighting.Language.Dart:
            case SyntaxHighlighting.Language.JavaScript:
            case SyntaxHighlighting.Language.TypeScript:
            case SyntaxHighlighting.Language.Php:
            case SyntaxHighlighting.Language.Css:
            case SyntaxHighlighting.Language.Protobuf:
                return TryKeywordDefinition(text, i, out name, out openParen, "function", "func", "fn", "fun", "def")
                    || TryCStyleDefinition(language, text, i, fullText, lineStart, out name, out openParen);
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
            // "fun <T> name(" (Kotlin) puts the type parameters in front of the name, and "func (r *T) name(" (Go) the receiver.
            if (p < text.Length && (text[p] == '<' || (text[p] == '(' && keyword == "func")))
            {
                int skipped = SkipBalanced(text, p);
                if (skipped < 0)
                {
                    continue;
                }
                p = skipped;
                while (p < text.Length && char.IsWhiteSpace(text[p]))
                {
                    p++;
                }
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
            // Type parameters after the name: "fn name<T>(" (Rust), "function name<T>(" (TypeScript), "def name[T](" (Scala).
            if (q < text.Length && (text[q] == '<' || text[q] == '['))
            {
                int skipped = SkipBalanced(text, q);
                if (skipped >= 0)
                {
                    q = skipped;
                    while (q < text.Length && char.IsWhiteSpace(text[q]))
                    {
                        q++;
                    }
                }
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
    /// The index after the bracket group that opens at <paramref name="open"/> ("(...)", "&lt;...&gt;", "[...]"; "-&gt;" does not close one),
    /// or -1 when it does not close in the text.
    /// </summary>
    private static int SkipBalanced(string text, int open)
    {
        char opening = text[open];
        char closing = opening == '(' ? ')' : (opening == '<' ? '>' : ']');
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == opening)
            {
                depth++;
            }
            else if (c == closing && !(c == '>' && i > 0 && (text[i - 1] == '-' || text[i - 1] == '=')))
            {
                depth--;
                if (depth == 0)
                {
                    return i + 1;
                }
            }
        }
        return -1;
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
    private static bool TryBareDefinition(SyntaxHighlighting.Language language, string text, int start, string fullText, int lineStart, out string name, out int openParen)
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
        int match = FindMatchingParen(language, fullText, lineStart + q);
        if (match < 0)
        {
            return false;
        }
        int after = match + 1;
        while (after < fullText.Length)
        {
            if (char.IsWhiteSpace(fullText[after]))
            {
                after++;
            }
            else if (!TrySkipComment(language, fullText, ref after, fullText.Length))
            {
                break;
            }
        }
        if (after >= fullText.Length || fullText[after] != '{')
        {
            return false;
        }
        name = text.Substring(nameStart, p - nameStart);
        openParen = q;
        return true;
    }

    private static bool TryCStyleDefinition(SyntaxHighlighting.Language language, string text, int start, string fullText, int lineStart, out string name, out int openParen)
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
        // The parameters can go on over several lines, and the brace is often on a line of its own.
        int match = FindMatchingParen(language, fullText, lineStart + paren);
        if (match < 0)
        {
            return false;
        }
        int after = match + 1;
        // Qualifiers between the parameters and the body: "const", "noexcept", "override", "throws IOException", "-> int".
        while (after < fullText.Length)
        {
            if (TrySkipComment(language, fullText, ref after, fullText.Length))
            {
                continue;
            }
            char tail = fullText[after];
            if (!(char.IsWhiteSpace(tail) || EditorCompletionSource.IsIdentifierChar(tail) || ",.:<>&*-".IndexOf(tail) >= 0))
            {
                break;
            }
            after++;
        }
        if (after >= fullText.Length || (fullText[after] != '{' && fullText[after] != ';'))
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
            // "[]" and "?" for the return types "byte[]" (Java, C#) and "string?" (C#, Kotlin, Dart).
            if (!(EditorCompletionSource.IsIdentifierChar(c) || char.IsWhiteSpace(c) || "*&:<>,~[]?".IndexOf(c) >= 0))
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

    private static string ExtractParenGroup(SyntaxHighlighting.Language language, string text, int openOffset)
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
            if (IsStringQuote(language, text, i))
            {
                i = SkipString(text, i, text.Length);
                continue;
            }
            if (TrySkipComment(language, text, ref i, text.Length))
            {
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

    private static int FindMatchingParen(SyntaxHighlighting.Language language, string text, int openOffset)
    {
        if (openOffset < 0 || openOffset >= text.Length || text[openOffset] != '(')
        {
            return -1;
        }
        int depth = 0;
        for (int i = openOffset; i < text.Length; i++)
        {
            char c = text[i];
            if (IsStringQuote(language, text, i))
            {
                i = SkipString(text, i, text.Length) - 1;
                continue;
            }
            if (TrySkipComment(language, text, ref i, text.Length))
            {
                // The loop adds one: the comment ends at i, and the character there (a line break) is not a bracket.
                i--;
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
            if (IsStringQuote(language, line, i))
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

    /// <summary>
    /// True for a character that opens a string. Verilog uses the apostrophe in based literals (4'b1010)
    /// and VHDL in attributes (clk'event) and character literals, so there it is an ordinary character.
    /// </summary>
    private static bool IsStringQuote(SyntaxHighlighting.Language language, string text, int i)
    {
        char c = text[i];
        if (c == '"')
        {
            return true;
        }
        if (c != '\'' || language == SyntaxHighlighting.Language.Verilog || language == SyntaxHighlighting.Language.Vhdl)
        {
            return false;
        }
        if (language == SyntaxHighlighting.Language.Rust)
        {
            // A lifetime ("&'a str") is no string: only 'x' and '\n' are character literals.
            return (i + 2 < text.Length && text[i + 2] == '\'') || (i + 1 < text.Length && text[i + 1] == '\\');
        }
        return true;
    }

    private static bool IsLineCommentStart(SyntaxHighlighting.Language language, string text, int i)
    {
        char c = text[i];
        char next = (i + 1 < text.Length) ? text[i + 1] : '\0';
        switch (language)
        {
            case SyntaxHighlighting.Language.CLike:
            case SyntaxHighlighting.Language.Cpp:
            case SyntaxHighlighting.Language.CSharp:
            case SyntaxHighlighting.Language.Java:
            case SyntaxHighlighting.Language.Kotlin:
            case SyntaxHighlighting.Language.Scala:
            case SyntaxHighlighting.Language.Groovy:
            case SyntaxHighlighting.Language.Go:
            case SyntaxHighlighting.Language.Rust:
            case SyntaxHighlighting.Language.Verilog:
            case SyntaxHighlighting.Language.Swift:
            case SyntaxHighlighting.Language.Dart:
            case SyntaxHighlighting.Language.JavaScript:
            case SyntaxHighlighting.Language.TypeScript:
            case SyntaxHighlighting.Language.Protobuf:
            case SyntaxHighlighting.Language.DeviceTree:
                return c == '/' && next == '/';
            case SyntaxHighlighting.Language.Php:
                return (c == '/' && next == '/') || c == '#';
            case SyntaxHighlighting.Language.Sql:
            case SyntaxHighlighting.Language.Lua:
            case SyntaxHighlighting.Language.Vhdl:
                return c == '-' && next == '-';
            case SyntaxHighlighting.Language.Python:
            case SyntaxHighlighting.Language.Bash:
            case SyntaxHighlighting.Language.Script:
            case SyntaxHighlighting.Language.Awk:
            case SyntaxHighlighting.Language.Tcl:
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
        switch (language)
        {
            case SyntaxHighlighting.Language.CLike:
            case SyntaxHighlighting.Language.Cpp:
            case SyntaxHighlighting.Language.CSharp:
            case SyntaxHighlighting.Language.Java:
            case SyntaxHighlighting.Language.Kotlin:
            case SyntaxHighlighting.Language.Scala:
            case SyntaxHighlighting.Language.Groovy:
            case SyntaxHighlighting.Language.Go:
            case SyntaxHighlighting.Language.Rust:
            case SyntaxHighlighting.Language.Verilog:
            case SyntaxHighlighting.Language.Vhdl:
            case SyntaxHighlighting.Language.Swift:
            case SyntaxHighlighting.Language.Dart:
            case SyntaxHighlighting.Language.JavaScript:
            case SyntaxHighlighting.Language.TypeScript:
            case SyntaxHighlighting.Language.Php:
            case SyntaxHighlighting.Language.Css:
            case SyntaxHighlighting.Language.Protobuf:
            case SyntaxHighlighting.Language.DeviceTree:
            case SyntaxHighlighting.Language.Sql:
                return text[i + 1] == '*';
            default:
                return false;
        }
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
        // A column past the end of the line (a stale caret) stays on the line: the scan reads the text up to this offset.
        int lineEnd = text.IndexOf('\n', offset);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }
        return Math.Min(offset + column, lineEnd);
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
