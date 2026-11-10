using System;
using System.Collections.Generic;

namespace SmarTTY.Completion;

/// <summary>
/// A function signature shown by the editor's parameter hint: the name and the text of every parameter.
/// Shared by the definitions read from the document and by the API symbol database.
/// </summary>
public sealed class ParameterSignature : ISignatureInfo
{
    public readonly string Name;

    private readonly string[] _parameters;

    public ParameterSignature(string name, string[] parameters)
    {
        Name = name ?? "";
        _parameters = parameters ?? new string[0];
    }

    public int ParameterCount => _parameters.Length;

    public string Separator => ", ";

    public bool IsVertical => false;

    public ISignatureParameter GetParameter(int index)
    {
        return new Parameter(_parameters[index]);
    }

    /// <summary>The full signature, e.g. "foo(int x, int y)".</summary>
    public override string ToString()
    {
        return Name + "(" + string.Join(Separator, _parameters) + ")";
    }

    /// <summary>
    /// Parses "name(a, b)" into a signature. A missing name or parameter list is allowed, so a partially
    /// written symbol never fails the load. "void" alone (as in "foo(void)") means no parameters.
    /// </summary>
    public static ParameterSignature Parse(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return new ParameterSignature("", new string[0]);
        }
        int open = signature.IndexOf('(');
        if (open < 0)
        {
            return new ParameterSignature(signature.Trim(), new string[0]);
        }
        int close = signature.LastIndexOf(')');
        if (close < open)
        {
            close = signature.Length;
        }
        string name = signature.Substring(0, open).Trim();
        return new ParameterSignature(name, SplitParameters(signature.Substring(open + 1, close - open - 1)));
    }

    /// <summary>
    /// Splits the text inside the parentheses on its own-level commas. Commas inside nested brackets or
    /// strings stay in their parameter.
    /// </summary>
    public static string[] SplitParameters(string inner)
    {
        return SplitParameters(inner, angleBrackets: false, apostropheQuotes: true);
    }

    /// <summary>
    /// <paramref name="angleBrackets"/>: a "&lt;" right after an identifier opens a type argument list ("Map&lt;String, int&gt; m"), so its commas
    /// stay in their parameter. <paramref name="apostropheQuotes"/> is false where an apostrophe is not a quote (Rust lifetimes: "&amp;'a str").
    /// </summary>
    public static string[] SplitParameters(string inner, bool angleBrackets, bool apostropheQuotes)
    {
        List<string> parts = new List<string>();
        if (inner == null)
        {
            return parts.ToArray();
        }
        int depth = 0;
        int angle = 0;
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '"' || (c == '\'' && apostropheQuotes))
            {
                i = SkipString(inner, i) - 1;
                continue;
            }
            if (angleBrackets)
            {
                if (c == '<' && i > 0 && (char.IsLetterOrDigit(inner[i - 1]) || inner[i - 1] == '_'))
                {
                    angle++;
                    continue;
                }
                if (c == '>' && angle > 0 && inner[i - 1] != '-' && inner[i - 1] != '=')
                {
                    angle--;
                    continue;
                }
            }
            if (c == '(' || c == '[' || c == '{')
            {
                depth++;
            }
            else if (c == ')' || c == ']' || c == '}')
            {
                if (depth > 0)
                {
                    depth--;
                }
            }
            else if (c == ',' && depth == 0 && angle == 0)
            {
                AddParameter(parts, inner.Substring(start, i - start));
                start = i + 1;
            }
        }
        AddParameter(parts, inner.Substring(start));
        // "void foo(void)" declares no parameters.
        if (parts.Count == 1 && parts[0] == "void")
        {
            parts.Clear();
        }
        return parts.ToArray();
    }

    private static void AddParameter(List<string> parts, string text)
    {
        // A parameter that was written over several lines is one line in the hint.
        string trimmed = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s*[\r\n]+\s*", " ");
        if (trimmed.Length > 0)
        {
            parts.Add(trimmed);
        }
    }

    private static int SkipString(string text, int i)
    {
        char quote = text[i];
        i++;
        while (i < text.Length)
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
        return text.Length;
    }

    private sealed class Parameter : ISignatureParameter
    {
        public Parameter(string text)
        {
            Text = text;
        }

        public string Text { get; }
    }
}
