using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.CodeEditing;
using SmarTTY.Completion;
using L = SmarTTY.CodeEditing.SyntaxHighlighting.Language;

// Regression harness for the editor autocompletion word list:
//  * the per-language keyword sets exposed by SyntaxHighlighting.GetKeywords,
//  * the completion source (keywords + the identifiers of the document, prefix filter, ordering).
// Run with: dotnet run --project tests/EditorCompletionTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private static List<string> Names(L language, string text, string prefix)
    {
        return EditorCompletionSource.Build(language, text, prefix).Select(s => s.DisplayName).ToList();
    }

    private static void KeywordChecks()
    {
        // Every highlighted language offers its words; plain text and diff have none.
        foreach (L language in SyntaxHighlighting.SelectableLanguages)
        {
            // Plain and Diff have no words of their own; Markup (HTML/XML) has no fixed keyword set either
            // (its tags are free-form), so it relies on the identifiers found in the document.
            bool expected = language != L.Plain && language != L.Diff && language != L.Markup;
            bool hasWords = SyntaxHighlighting.GetKeywords(language).Count > 0;
            Equal(hasWords, expected, "GetKeywords(" + language + ") " + (expected ? "is not empty" : "is empty"));
        }

        Check(SyntaxHighlighting.GetKeywords(L.CLike).Contains("if"), "C-like keywords contain 'if'");
        Check(SyntaxHighlighting.GetKeywords(L.CLike).Contains("include"), "C-like keywords contain the preprocessor 'include'");
        Check(SyntaxHighlighting.GetKeywords(L.Python).Contains("def"), "Python keywords contain 'def'");
        Check(SyntaxHighlighting.GetKeywords(L.Python).Contains("match"), "Python block keywords are merged in ('match')");
        Check(SyntaxHighlighting.GetKeywords(L.Lua).Contains("elseif"), "Lua keywords contain 'elseif'");
        Check(SyntaxHighlighting.GetKeywords(L.Bash).Contains("fi"), "Bash keywords contain 'fi'");
        Check(SyntaxHighlighting.GetKeywords(L.Bash).Contains("coproc"), "Bash keywords contain 'coproc'");
        Check(SyntaxHighlighting.GetKeywords(L.Makefile).Contains("ifeq"), "Makefile keywords contain 'ifeq'");
        Check(SyntaxHighlighting.GetKeywords(L.Json).Contains("true"), "JSON literals contain 'true'");
        Check(SyntaxHighlighting.GetKeywords(L.Uci).Contains("config"), "UCI keywords contain 'config'");
        Check(SyntaxHighlighting.GetKeywords(L.Yaml).Contains("true"), "YAML literals contain 'true'");
        Check(SyntaxHighlighting.GetKeywords(L.Sql).Contains("select"), "SQL keywords contain 'select'");
        Check(SyntaxHighlighting.GetKeywords(L.Markup).Count == 0, "Markup has no keyword list of its own");
    }

    private static void SourceChecks()
    {
        // Keywords and document words are both offered.
        List<string> all = Names(L.CLike, "int counter; counter = 1;", "");
        Check(all.Contains("if"), "the C-like keyword 'if' is offered");
        Check(all.Contains("counter"), "the document identifier 'counter' is offered");
        Equal(all.Count(x => x == "counter"), 1, "a word appears only once");
        Check(all.IndexOf("if") < all.IndexOf("counter"), "keywords come before the document words");

        // The prefix filter is case insensitive.
        List<string> prefixed = Names(L.Python, "def doWork():\n    done = 1\n", "do");
        Check(prefixed.Contains("doWork"), "the prefix filter keeps 'doWork'");
        Check(prefixed.Contains("done"), "the prefix filter keeps 'done'");
        Check(!prefixed.Contains("def"), "'def' does not match the prefix 'do'");

        // An empty prefix returns everything, a missing document is fine.
        Check(Names(L.Lua, null, "").Count > 0, "keywords are offered without a document");

        // Plain text has no keywords, but the words of the document itself are still offered.
        Equal(Names(L.Plain, "", "").Count, 0, "an empty plain document has no completions");
        Check(Names(L.Plain, "hello world", "").Contains("hello"), "plain text offers the words of the document");

        // Numbers, keywords of other languages and over-long words are dropped.
        List<string> words = Names(L.CLike, "value1 _tmp 123abc", "");
        Check(words.Contains("value1"), "an identifier with a digit is kept");
        Check(words.Contains("_tmp"), "an identifier starting with '_' is kept");
        Check(!words.Contains("123abc"), "a token that starts with a digit is dropped");
        Check(!words.Contains("select"), "a keyword of another language is not offered");

        Check(EditorCompletionSource.IsIdentifier("foo_bar2"), "foo_bar2 is an identifier");
        Check(!EditorCompletionSource.IsIdentifier("2foo"), "2foo is not an identifier");
        Check(!EditorCompletionSource.IsIdentifier(""), "an empty string is not an identifier");
    }

    private static void SuggestionFilterChecks()
    {
        // The rule the completion popup filters its list with. A name shorter than the prefix cannot start with
        // it; the old catalog test asked for a substring past the end of a short key and threw ArgumentOutOfRange.
        Check(SuggestionFilter.MatchesPrefix("done", null), "a null prefix matches everything");
        Check(SuggestionFilter.MatchesPrefix("done", ""), "an empty prefix matches everything");
        Check(SuggestionFilter.MatchesPrefix("done", "do"), "'done' matches the prefix 'do'");
        Check(!SuggestionFilter.MatchesPrefix("def", "do"), "'def' does not match the prefix 'do'");
        Check(!SuggestionFilter.MatchesPrefix("d", "do"), "a name shorter than the prefix does not match");
        Check(!SuggestionFilter.MatchesPrefix("do", "done"), "a name shorter than a longer prefix does not match");
        Check(SuggestionFilter.MatchesPrefix("do", "do"), "a name equal to the prefix matches");
        Check(SuggestionFilter.MatchesPrefix("Done", "Done"), "the prefix is matched case-sensitively");
        Check(!SuggestionFilter.MatchesPrefix("done", "Done"), "a different case does not match");
        Check(!SuggestionFilter.MatchesPrefix(null, "do"), "a null name matches nothing");
        Check(SuggestionFilter.MatchesPrefix("x", null), "a null prefix matches even a short name");
    }

    private static void Main()
    {
        KeywordChecks();
        SourceChecks();
        SuggestionFilterChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " completion checks passed.");
    }
}
