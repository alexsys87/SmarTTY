using System;
using System.Collections.Generic;
using SmarTTY.CodeEditing;

namespace SmarTTY.Completion;

/// <summary>
/// Builds the word list the editor's completion popup offers: the keywords of the language (the same
/// sets the tokenizers highlight with) plus the identifiers that already appear in the document.
///
/// The class is free of any UI dependency, so the test harness can check it without a display.
/// </summary>
public static class EditorCompletionSource
{
    // The popup is meant to help while typing, not to dump a whole file: keep the list short.
    private const int MaxItems = 500;

    private const int MaxWordLength = 64;

    private sealed class Word : ISuggestion
    {
        public readonly string Text;

        public readonly bool IsKeyword;

        /// <summary>0 for a keyword, 1 for an API symbol, 2 for a plain document word.</summary>
        public readonly int Rank;

        // The standard API symbol behind the word, or null for a keyword or a plain document word.
        public readonly ApiSymbolDatabase.Symbol Symbol;

        public Word(string text, int rank, ApiSymbolDatabase.Symbol symbol = null)
        {
            Text = text;
            Rank = rank;
            IsKeyword = rank == 0;
            Symbol = symbol;
        }

        // No icons in the editor popup (the list has no image list).
        public int ImageIndex => -1;

        public string DisplayName => Text;

        public string DetailText => Symbol != null ? (Symbol.Summary ?? "function") : (IsKeyword ? "keyword" : "word");

        // An empty hint keeps the little "hint" window of the popup closed; an API symbol shows its signature there.
        public string HintText => Symbol != null ? (Symbol.Signature ?? "") : "";

        public bool IsBold => Symbol != null;

        public string HintDescription => Symbol?.Description;
    }

    /// <summary>A character that can be part of an identifier in any of the supported languages.</summary>
    public static bool IsIdentifierChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_';
    }

    /// <summary>An identifier: a letter or '_' followed by letters, digits and '_'.</summary>
    public static bool IsIdentifier(string word)
    {
        if (string.IsNullOrEmpty(word) || (!char.IsLetter(word[0]) && word[0] != '_'))
        {
            return false;
        }
        for (int i = 1; i < word.Length; i++)
        {
            if (!IsIdentifierChar(word[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The completion items for a language and a document: keywords first (alphabetically), then the
    /// standard API symbols of the language, then the document's identifiers. Only words that start with
    /// <paramref name="prefix"/> are returned; an empty prefix returns everything (the Ctrl+Space case).
    /// <paramref name="database"/> may be null, in which case only keywords and document words are offered.
    /// </summary>
    public static List<ISuggestion> Build(SyntaxHighlighting.Language language, string documentText, string prefix)
    {
        return Build(language, documentText, prefix, null);
    }

    /// <summary>The overload that also offers the standard API symbols of <paramref name="database"/>.</summary>
    public static List<ISuggestion> Build(SyntaxHighlighting.Language language, string documentText, string prefix, ApiSymbolDatabase database)
    {
        List<Word> words = new List<Word>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string keyword in SyntaxHighlighting.GetKeywords(language))
        {
            Add(words, seen, keyword, 0, null, prefix);
        }
        if (database != null)
        {
            foreach (ApiSymbolDatabase.Symbol symbol in database.ForLanguage(language))
            {
                // The list is keyed by the plain name; a "math.sqrt" entry is offered as "sqrt".
                Add(words, seen, ShortName(symbol.Name), 1, symbol, prefix);
            }
        }
        if (documentText != null)
        {
            foreach (string word in EnumerateWords(documentText))
            {
                Add(words, seen, word, 2, null, prefix);
            }
        }

        words.Sort(CompareWords);

        int count = Math.Min(words.Count, MaxItems);
        List<ISuggestion> result = new List<ISuggestion>(count);
        for (int i = 0; i < count; i++)
        {
            result.Add(words[i]);
        }
        return result;
    }

    private static string ShortName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }
        int dot = name.LastIndexOf('.');
        return (dot >= 0) ? name.Substring(dot + 1) : name;
    }

    private static void Add(List<Word> words, HashSet<string> seen, string word, int rank, ApiSymbolDatabase.Symbol symbol, string prefix)
    {
        if (word == null || word.Length == 0 || word.Length > MaxWordLength)
        {
            return;
        }
        if (!IsIdentifier(word) || !MatchesPrefix(word, prefix))
        {
            return;
        }
        // A document word may match a name already offered as a keyword or API symbol; the better one stays.
        if (!seen.Add(word))
        {
            return;
        }
        words.Add(new Word(word, rank, symbol));
    }

    private static bool MatchesPrefix(string word, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return true;
        }
        return word.Length >= prefix.Length
            && string.Compare(word, 0, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static IEnumerable<string> EnumerateWords(string text)
    {
        int start = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (IsIdentifierChar(text[i]))
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (start >= 0)
            {
                yield return text.Substring(start, i - start);
                start = -1;
            }
        }
        if (start >= 0)
        {
            yield return text.Substring(start);
        }
    }

    private static int CompareWords(Word a, Word b)
    {
        if (a.Rank != b.Rank)
        {
            return a.Rank - b.Rank;
        }
        return string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);
    }
}
