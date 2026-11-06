using System;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

// The formats of the Linux kernel, OpenWrt and embedded builds that are not programs with a configuration file:
// Kconfig and the .config it produces. They use the colours of Config with the words of the language known, so a
// misspelt directive stands out and the editor offers the real ones.
public static partial class SyntaxHighlighting
{
    // ---------------------------------------------------------------- Kconfig

    // Line state: the indented text of a "help" block, which is prose and not keys and values.
    private const int StKconfigHelp = 0x01;

    private static readonly HashSet<string> KconfigKeywords = Words(StringComparer.Ordinal,
        // structure
        @"config menuconfig menu endmenu choice endchoice comment mainmenu source if endif
          depends select imply default prompt help range visible option optional modules transition
          bool tristate string int hex def_bool def_tristate def_hex def_string def_int");

    private static readonly HashSet<string> KconfigValues = Words(StringComparer.Ordinal, @"y m n");

    private static readonly TokenCache.LineTokenizer KconfigTokenizer = TokenizeKconfig;

    /// <summary>
    /// Kconfig: the colours of Config with the Kconfig words as keywords. A directive that stands alone ("endmenu",
    /// "help", "choice") is a keyword too, which the Config tokenizer cannot tell without a value after it. The text
    /// of a help block is left plain.
    /// </summary>
    private static bool TokenizeKconfig(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        int first = SkipBlanks(text, 0);
        if ((startState & StKconfigHelp) != 0 && (first == text.Length || first > 0))
        {
            endState = StKconfigHelp;
            return true;
        }

        if (first < text.Length && text[first] != '#' && text[first] != ';')
        {
            if (StartsWithAt(text, first, "---help---") && SkipBlanks(text, first + 10) == text.Length)
            {
                sink.AddToken(TypeKeyword, first, first + 10);
                endState = StKconfigHelp;
                return true;
            }
            int end = first;
            while (end < text.Length && IsIdChar(text[end]))
            {
                end++;
            }
            if (end > first && SkipBlanks(text, end) == text.Length &&
                KconfigKeywords.Contains(text.Substring(first, end - first)))
            {
                sink.AddToken(TypeKeyword, first, end);
                endState = (text.Substring(first, end - first) == "help") ? StKconfigHelp : 0;
                return true;
            }
        }
        return TokenizeConfigCore(KconfigKeywords, startState, text, sink, out endState, dollarParentheses: true, valueKeywords: KconfigValues);
    }
}
