using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SmarTTY.CodeEditing;
using SmarTTY.Completion;
using L = SmarTTY.CodeEditing.SyntaxHighlighting.Language;

// Regression harness for the API symbol database (ApiSymbols.xml) and the way it feeds the editor:
//  * the XML is well formed and every entry is usable,
//  * the lookup by language and name, including a "module.name" call,
//  * the completion list offers the symbols with their summary and signature,
//  * the parameter hint falls back to an API symbol when the document does not define the function.
// Run with: dotnet run --project tests/ApiSymbolTests
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

    private static string Root()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "SmarTTY.csproj")))
        {
            root = Path.GetDirectoryName(root);
        }
        return root;
    }

    private static ApiSymbolDatabase LoadDatabase()
    {
        string root = Root();
        Check(root != null, "the repository root is found");
        string path = Path.Combine(root, "Completion", "ApiSymbols.xml");
        Check(File.Exists(path), "the default symbol set (Completion/ApiSymbols.xml) exists");
        using (FileStream file = File.OpenRead(path))
        {
            return ApiSymbolDatabase.Read(file);
        }
    }

    private static void DatabaseChecks(ApiSymbolDatabase database)
    {
        Check(database.Symbols.Length > 100, "the default set has a useful number of symbols (" + database.Symbols.Length + ")");

        // Every entry has a language and a name, and every signature can be parsed without a name or a list.
        foreach (ApiSymbolDatabase.Symbol symbol in database.Symbols)
        {
            Check(!string.IsNullOrEmpty(symbol.Language), "a symbol has a language");
            Check(!string.IsNullOrEmpty(symbol.Name), "the symbol '" + symbol.Name + "' has a name");
            ParameterSignature parsed = symbol.Parsed;
            Check(parsed != null, "the signature of '" + symbol.Name + "' parses");
        }

        // The set covers the languages the editor highlights with a code tokenizer.
        foreach (L language in new[] { L.Python, L.CLike, L.Bash, L.Lua, L.Sql, L.Script })
        {
            Check(database.ForLanguage(language).Count > 0, "the set has symbols for " + language);
        }
        // Plain text and diff have no API set; asking for one is empty, not a crash.
        Equal(database.ForLanguage(L.Plain).Count, 0, "plain text has no API symbols");
        Equal(database.ForLanguage(L.Diff).Count, 0, "diff has no API symbols");

        // Lookup by name and language.
        Check(database.Find(L.Python, "print") != null, "python 'print' is found");
        Check(database.Find(L.CLike, "print") == null, "python's 'print' is not offered for C-like");
        Equal(database.Find(L.Python, "print").Summary, "Write objects to the text stream", "the summary is read");

        // A "module.name" call is resolved to its last segment.
        ParameterSignature sqrt = database.FindSignature(L.Lua, "math.sqrt");
        Check(sqrt != null, "a 'math.sqrt' call resolves to the 'sqrt' symbol");
        Equal(sqrt.ToString(), "math.sqrt(x)", "the resolved signature keeps the qualified name");
        Check(database.FindSignature(L.Python, "sqrt") == null, "python has no bare 'sqrt' (it is math.sqrt)");
        Check(database.FindSignature(L.Lua, "math.sqrt") != null, "lua has 'math.sqrt'");

        // A name that is not in the set resolves to nothing.
        Check(database.FindSignature(L.Python, "definitelyNotAFunction") == null, "an unknown name has no signature");
    }

    private static List<ISuggestion> Build(L language, string text, string prefix, ApiSymbolDatabase database)
    {
        return EditorCompletionSource.Build(language, text, prefix, database);
    }

    private static void CompletionChecks(ApiSymbolDatabase database)
    {
        // A standard function is offered with its summary and signature.
        List<ISuggestion> items = Build(L.Python, "", "pri", database);
        ISuggestion print = items.FirstOrDefault(s => s.DisplayName == "print");
        Check(print != null, "the completion list offers the API symbol 'print'");
        Equal(print.DetailText, "Write objects to the text stream", "the summary is the detail text");
        Check(print.HintText.StartsWith("print("), "the signature is the hint text");
        Check(print.IsBold, "an API symbol is drawn in bold");

        // Keywords still come first, then API symbols, then document words.
        List<ISuggestion> all = Build(L.Python, "myVariable = 1\n", "", database);
        int keyword = all.FindIndex(s => s.DisplayName == "def");
        int api = all.FindIndex(s => s.DisplayName == "print");
        int word = all.FindIndex(s => s.DisplayName == "myVariable");
        Check(keyword >= 0 && api >= 0 && word >= 0, "keywords, API symbols and document words are all offered");
        Check(keyword < api, "keywords come before the API symbols");
        Check(api < word, "the API symbols come before the document words");

        // The same word is offered once, and a document word does not shadow the API symbol.
        Equal(all.Count(s => s.DisplayName == "print"), 1, "a symbol appears only once");
        List<ISuggestion> shadowed = Build(L.Python, "print = 1\n", "pri", database);
        ISuggestion onlyPrint = shadowed.First(s => s.DisplayName == "print");
        Check(onlyPrint.IsBold, "a document word does not replace the API symbol of the same name");

        // A "module.name" entry is offered under its plain name.
        List<ISuggestion> lua = Build(L.Lua, "", "sqr", database);
        Check(lua.Any(s => s.DisplayName == "sqrt"), "the 'math.sqrt' entry is offered as 'sqrt'");

        // Without a database the list is what it always was: keywords and document words only.
        List<ISuggestion> plain = Build(L.Python, "", "pri", null);
        Check(!plain.Any(s => s.DisplayName == "print"), "without a database no API symbol is offered");
    }

    private static void HintChecks(ApiSymbolDatabase database)
    {
        // The caret inside "print(" shows the builtin signature even though the document does not define it.
        ParameterSignature signature = EditorSignatureSource.FindSignature(
            L.Python, "print(\n", 0, 6, database, out int active);
        Check(signature != null, "the parameter hint falls back to the API symbol");
        Equal(signature.Name, "print", "the hint shows the API symbol name");
        Check(signature.ParameterCount > 1, "the builtin signature has its parameters");
        Equal(active, 0, "the first parameter is active");

        // A definition in the document still wins over the database.
        string document = "def print(a):\n    pass\n\nprint(\n";
        ParameterSignature local = EditorSignatureSource.FindSignature(
            L.Python, document, 3, 6, database, out int localActive);
        Equal(local.ToString(), "print(a)", "a function defined in the document wins over the API symbol");

        // The hint reads only the identifier before "(", so a "math.sqrt" entry is reachable from a bare "sqrt(" call.
        ParameterSignature lua = EditorSignatureSource.FindSignature(
            L.Lua, "sqrt(\n", 0, 5, database, out int luaActive);
        Check(lua != null, "a bare 'sqrt(' call finds the 'math.sqrt' symbol");

        // Without a database the unknown call shows nothing.
        Check(EditorSignatureSource.FindSignature(L.Python, "print(\n", 0, 6, null, out _) == null,
            "without a database an unknown call shows no hint");
    }

    private static int Main()
    {
        try
        {
            ApiSymbolDatabase database = LoadDatabase();
            DatabaseChecks(database);
            CompletionChecks(database);
            HintChecks(database);
            Console.WriteLine();
            Console.WriteLine("All " + _passed + " API symbol checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            Console.WriteLine(exception.StackTrace);
            return 1;
        }
    }
}
