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

        // The Python set covers the builtins, the exceptions, the base classes and the common modules.
        List<ApiSymbolDatabase.Symbol> python = database.ForLanguage(L.Python).ToList();
        Check(python.Count > 550, "the Python set is broad (" + python.Count + " symbols)");
        Equal(python.Count, python.Select(s => s.Name).Distinct().Count(), "Python names are unique");
        Equal(python.Count, python.Count(s => !string.IsNullOrEmpty(s.Summary) && !string.IsNullOrEmpty(s.Description)),
            "every Python symbol has a summary and a description");
        foreach (string name in new[]
                 {
                     "object", "BaseException", "Exception", "ExceptionGroup", "Warning", "UnicodeDecodeError",
                     "math.sqrt", "math.pi", "os.getcwd", "os.path.join", "sys.argv", "sys.version_info",
                     "re.compile", "re.IGNORECASE", "json.loads", "json.JSONDecodeError",
                     "pathlib.Path", "pathlib.Path.read_text", "datetime.datetime", "datetime.timedelta",
                     "collections.Counter", "collections.abc.Mapping", "itertools.product", "functools.lru_cache"
                 })
        {
            Check(database.Find(L.Python, name) != null, "the Python set includes '" + name + "'");
        }
        Check(database.Find(L.Python, "BaseException").Description.Contains("Python exception"),
            "the description of 'BaseException' names a Python exception");
        Check(database.Find(L.Python, "object").Description.Contains("Python base class"),
            "the description of 'object' names a Python base class");
        Check(database.Find(L.Python, "math.sqrt").Description.Contains("Python math module"),
            "the description of 'math.sqrt' names the math module");

        // A "module.name" call is resolved to its last segment.
        ParameterSignature pythonSqrt = database.FindSignature(L.Python, "math.sqrt");
        Check(pythonSqrt != null, "a 'math.sqrt' call resolves to the Python symbol");
        Equal(pythonSqrt.ToString(), "math.sqrt(x)", "the resolved Python signature keeps the qualified name");
        Check(database.FindSignature(L.Python, "sqrt") != null,
            "a bare 'sqrt' call also finds the Python math function");
        ParameterSignature luaSqrt = database.FindSignature(L.Lua, "math.sqrt");
        Check(luaSqrt != null, "a 'math.sqrt' call resolves to the 'sqrt' symbol");
        Equal(luaSqrt.ToString(), "math.sqrt(x)", "the resolved signature keeps the qualified name");
        Check(database.FindSignature(L.Lua, "math.sqrt") != null, "lua has 'math.sqrt'");

        // A name that is not in the set resolves to nothing.
        Check(database.FindSignature(L.Python, "definitelyNotAFunction") == null, "an unknown name has no signature");

        // Every C-like entry has a summary for the completion list and a longer description for the hint.
        List<ApiSymbolDatabase.Symbol> cLike = database.ForLanguage(L.CLike).ToList();
        Check(cLike.Count > 900, "the C library set is broad (" + cLike.Count + " symbols)");
        Equal(cLike.Count, cLike.Count(s => !string.IsNullOrEmpty(s.Summary) && !string.IsNullOrEmpty(s.Description)),
            "every C-like symbol has a summary and a description");

        // The descriptions carry the full prototype and the header that provides the symbol.
        ApiSymbolDatabase.Symbol printf = database.Find(L.CLike, "printf");
        Check(printf.Description.Contains("Standard prototype: int printf(const char *format, ...);"),
            "the description of 'printf' has the full prototype");
        Check(printf.Description.Contains("<stdio.h>"), "the description of 'printf' names its header");

        // The headers added for real-world C code are represented in the shared C-like set.
        foreach (string name in new[]
                 {
                     "va_list", "va_start", "int8_t", "SIZE_MAX", "errno", "EINVAL", "CHAR_BIT", "FLT_MAX",
                     "setlocale", "LC_ALL", "jmp_buf", "setjmp", "signal", "sigaction", "SIGINT", "wint_t",
                     "wcslen", "iswalpha", "off_t"
                 })
        {
            Check(database.Find(L.CLike, name) != null, "the C set includes '" + name + "'");
        }

        // The Annex K sorting signatures use rsize_t, not size_t, for their bounds.
        Check(database.Find(L.CLike, "qsort_s").Signature.Contains("rsize_t count"),
            "the 'qsort_s' signature uses rsize_t bounds");
        Check(database.Find(L.CLike, "bsearch_s").Signature.Contains("rsize_t count"),
            "the 'bsearch_s' signature uses rsize_t bounds");

        // The Bash set covers the shell builtins and the documented shell variables.
        List<ApiSymbolDatabase.Symbol> bash = database.ForLanguage(L.Bash).ToList();
        Check(bash.Count > 150, "the Bash set is complete (" + bash.Count + " symbols)");
        Equal(bash.Count, bash.Select(s => s.Name).Distinct().Count(), "Bash names are unique");
        Equal(bash.Count, bash.Count(s => !string.IsNullOrEmpty(s.Summary) && !string.IsNullOrEmpty(s.Description)),
            "every Bash symbol has a summary and a description");
        foreach (string name in new[]
                 {
                     "shopt", "mapfile", "compgen", "complete", "wait",
                     "BASH_VERSION", "BASH_SOURCE", "PATH", "IFS", "PIPESTATUS"
                 })
        {
            Check(database.Find(L.Bash, name) != null, "the Bash set includes '" + name + "'");
        }
        Check(database.Find(L.Bash, "shopt").Description.Contains("Bash builtin"),
            "the description of 'shopt' names a Bash builtin");
        Check(database.Find(L.Bash, "BASH_VERSION").Description.Contains("Bash variable"),
            "the description of 'BASH_VERSION' names a Bash variable");
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

        // The Python modules are offered under their plain names too.
        List<ISuggestion> pythonMath = Build(L.Python, "", "sqr", database);
        ISuggestion pythonSqrt = pythonMath.FirstOrDefault(s => s.DisplayName == "sqrt");
        Check(pythonSqrt != null, "the completion list offers 'sqrt' from 'math.sqrt'");
        Equal(pythonSqrt.DetailText, "Square root", "the Python module summary is the detail text");
        Check(pythonSqrt.HintText.StartsWith("math.sqrt("), "the qualified Python signature is the hint text");
        Check(Build(L.Python, "", "loads", database).Any(s => s.DisplayName == "loads"),
            "the completion list offers 'json.loads' as 'loads'");
        Check(Build(L.Python, "", "read_te", database).Any(s => s.DisplayName == "read_text"),
            "the completion list offers 'Path.read_text' as 'read_text'");

        // The Bash builtins and variables feed the completion list.
        List<ISuggestion> bash = Build(L.Bash, "", "sho", database);
        ISuggestion shopt = bash.FirstOrDefault(s => s.DisplayName == "shopt");
        Check(shopt != null, "the completion list offers the Bash builtin 'shopt'");
        Equal(shopt.DetailText, "Set shell options", "the Bash summary is the detail text");
        Check(shopt.HintText.StartsWith("shopt("), "the Bash signature is the hint text");

        // A shell variable is offered after the '$' (the popup filters on the identifier alone).
        List<ISuggestion> variables = Build(L.Bash, "echo $PA\n", "PA", database);
        ISuggestion path = variables.FirstOrDefault(s => s.DisplayName == "PATH");
        Check(path != null && path.IsBold, "the completion list offers the Bash variable 'PATH'");

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
        Check(lua.Name == "math.sqrt", "the Lua hint keeps the qualified name");

        ParameterSignature python = EditorSignatureSource.FindSignature(
            L.Python, "sqrt(\n", 0, 5, database, out int pythonActive);
        Check(python != null, "a bare 'sqrt(' call finds the Python 'math.sqrt' symbol");
        Check(python.Name == "math.sqrt", "the Python hint keeps the qualified name");

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
