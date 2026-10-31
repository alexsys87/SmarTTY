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
        foreach (L language in new[] { L.Python, L.CLike, L.Cpp, L.Bash, L.Lua, L.Sql, L.Script })
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
        Check(python.Count > 1300, "the Python set is broad (" + python.Count + " symbols)");
        Equal(python.Count, python.Select(s => s.Name).Distinct().Count(), "Python names are unique");
        Equal(python.Count, python.Count(s => !string.IsNullOrEmpty(s.Summary) && !string.IsNullOrEmpty(s.Description)),
            "every Python symbol has a summary and a description");
        foreach (string name in new[]
                 {
                     "object", "BaseException", "Exception", "ExceptionGroup", "Warning", "UnicodeDecodeError",
                     "math.sqrt", "math.pi", "os.getcwd", "os.path.join", "sys.argv", "sys.version_info",
                     "re.compile", "re.IGNORECASE", "json.loads", "json.JSONDecodeError",
                     "pathlib.Path", "pathlib.Path.read_text", "datetime.datetime", "datetime.timedelta",
                     "collections.Counter", "collections.abc.Mapping", "itertools.product", "functools.lru_cache",
                     "str.split", "list.append", "dict.get", "set.add", "file.read",
                     "subprocess.run", "argparse.ArgumentParser", "logging.getLogger", "typing.Optional",
                     "asyncio.run", "random.choice", "time.monotonic", "shutil.copytree", "glob.glob",
                     "tempfile.TemporaryDirectory", "socket.create_connection", "sqlite3.connect",
                     "csv.DictReader", "urllib.parse.urlparse", "http.client.HTTPSConnection",
                     "unittest.mock.patch", "dataclasses.dataclass", "enum.Enum", "threading.Thread",
                     "hashlib.sha256", "base64.b64encode", "pickle.dumps", "copy.deepcopy", "pprint.pformat",
                     "traceback.format_exc", "contextlib.contextmanager", "statistics.mean", "decimal.Decimal",
                     "uuid.uuid4", "platform.system", "ssl.SSLContext"
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
        Check(database.Find(L.Python, "str.split").Description.Contains("Python str method"),
            "the description of 'str.split' names a str method");
        Check(database.Find(L.Python, "subprocess.run").Description.Contains("Python subprocess module"),
            "the description of 'subprocess.run' names the subprocess module");
        Check(database.Find(L.Python, "hashlib.HASH.hexdigest").Description.Contains("Python hash object"),
            "the description of 'hashlib.HASH.hexdigest' names a hash object");

        // The Lua set covers the base library, the standard modules and the metamethods.
        List<ApiSymbolDatabase.Symbol> luaSymbols = database.ForLanguage(L.Lua).ToList();
        Check(luaSymbols.Count >= 180, "the Lua set is broad (" + luaSymbols.Count + " symbols)");
        Equal(luaSymbols.Count, luaSymbols.Select(s => s.Name).Distinct().Count(), "Lua names are unique");
        Equal(luaSymbols.Count, luaSymbols.Count(s => !string.IsNullOrEmpty(s.Summary) && !string.IsNullOrEmpty(s.Description)),
            "every Lua symbol has a summary and a description");
        foreach (string name in new[]
                 {
                     "dofile", "load", "loadfile", "rawequal", "rawlen", "warn", "_G", "_VERSION", "arg",
                     "coroutine.create", "coroutine.resume", "coroutine.close",
                     "string.gmatch", "string.pack", "string.unpack",
                     "table.move", "table.pack", "table.unpack",
                     "math.pi", "math.huge", "math.tointeger", "math.type",
                     "io.popen", "io.tmpfile", "io.stdin",
                     "os.clock", "os.execute", "os.tmpname",
                     "package.searchpath", "package.searchers",
                     "utf8.codepoint", "utf8.charpattern",
                     "debug.traceback", "debug.upvaluejoin",
                     "__index", "__newindex", "__close"
                 })
        {
            Check(database.Find(L.Lua, name) != null, "the Lua set includes '" + name + "'");
        }
        Check(database.Find(L.Lua, "coroutine.create").Description.Contains("Lua coroutine library"),
            "the description of 'coroutine.create' names the coroutine library");
        Check(database.Find(L.Lua, "string.gmatch").Description.Contains("Lua string library"),
            "the description of 'string.gmatch' names the string library");
        Check(database.Find(L.Lua, "__index").Description.Contains("Lua metamethod"),
            "the description of '__index' names a Lua metamethod");

        ParameterSignature luaGsub = database.FindSignature(L.Lua, "string.gsub");
        Check(luaGsub != null, "a 'string.gsub' call resolves to the Lua symbol");
        Equal(luaGsub.ToString(), "string.gsub(text, pattern, replacement)",
            "the resolved Lua signature keeps the qualified name");

        // The SQL set covers functions and data types; statement keywords come from the syntax highlighting.
        List<ApiSymbolDatabase.Symbol> sql = database.ForLanguage(L.Sql).ToList();
        Check(sql.Count >= 140, "the SQL set is broad (" + sql.Count + " symbols)");
        Equal(sql.Count, sql.Select(s => s.Name).Distinct().Count(), "SQL names are unique");
        Equal(sql.Count, sql.Count(s => !string.IsNullOrEmpty(s.Summary) && !string.IsNullOrEmpty(s.Description)),
            "every SQL symbol has a summary and a description");
        foreach (string name in new[]
                 {
                     "CONCAT", "LEFT", "RIGHT", "LPAD", "DATE_TRUNC", "EXTRACT", "ROW_NUMBER", "RANK",
                     "LAG", "LEAD", "STRING_AGG", "JSON_EXTRACT", "JSON_OBJECT", "JSONB",
                     "INT", "VARCHAR", "TIMESTAMP", "BOOLEAN"
                 })
        {
            Check(database.Find(L.Sql, name) != null, "the SQL set includes '" + name + "'");
        }
        Check(database.Find(L.Sql, "ROW_NUMBER").Description.Contains("SQL window function"),
            "the description of 'ROW_NUMBER' names a SQL window function");
        Check(database.Find(L.Sql, "CONCAT").Description.Contains("SQL string function"),
            "the description of 'CONCAT' names a SQL string function");
        Check(database.Find(L.Sql, "INT").Description.Contains("SQL data type"),
            "the description of 'INT' names a SQL data type");

        ParameterSignature sqlConcat = database.FindSignature(L.Sql, "CONCAT");
        Check(sqlConcat != null, "a 'CONCAT' call resolves to the SQL function");
        Equal(sqlConcat.ToString(), "CONCAT(value, ...)", "the SQL signature keeps its parameters");

        // A "module.name" call is resolved to its last segment.
        ParameterSignature pythonSqrt = database.FindSignature(L.Python, "math.sqrt");
        Check(pythonSqrt != null, "a 'math.sqrt' call resolves to the Python symbol");
        Equal(pythonSqrt.ToString(), "math.sqrt(x)", "the resolved Python signature keeps the qualified name");
        Check(database.FindSignature(L.Python, "sqrt") != null,
            "a bare 'sqrt' call also finds the Python math function");
        ParameterSignature pythonSplit = database.FindSignature(L.Python, "str.split");
        Check(pythonSplit != null, "a 'str.split' call resolves to the Python method");
        Equal(pythonSplit.ToString(), "str.split(sep=None, maxsplit=-1)", "the resolved method signature keeps the qualified name");
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

        // C++ is a separate profile: it inherits the C library and adds the STL, while C files stay C-only.
        List<ApiSymbolDatabase.Symbol> cpp = database.ForLanguage(L.Cpp).ToList();
        Check(cpp.Count > 2400, "the C++ set is broad (" + cpp.Count + " symbols)");
        Equal(cpp.Count, cpp.Select(s => s.Name).Distinct().Count(), "C++ names are unique");
        Equal(cpp.Count, cpp.Count(s => !string.IsNullOrEmpty(s.Summary) && !string.IsNullOrEmpty(s.Description)),
            "every C++ symbol has a summary and a description");
        foreach (string name in new[]
                 {
                     "std::vector", "std::vector::push_back", "std::array", "std::deque", "std::list",
                     "std::map", "std::unordered_map", "std::set", "std::stack", "std::queue",
                     "std::string", "std::string_view", "std::sort", "std::find", "std::accumulate",
                     "std::unique_ptr", "std::shared_ptr", "std::make_unique", "std::make_shared",
                     "std::optional", "std::variant", "std::any", "std::tuple",
                     "std::chrono::duration", "std::chrono::system_clock", "std::chrono::milliseconds",
                     "std::filesystem::path", "std::filesystem::exists", "std::filesystem::directory_iterator",
                     "std::thread", "std::mutex", "std::lock_guard", "std::condition_variable",
                     "std::future", "std::async", "std::atomic",
                     "std::regex", "std::regex_match", "std::smatch",
                     "std::exception", "std::runtime_error", "std::error_code", "std::system_error",
                     "std::is_same", "std::enable_if", "std::numeric_limits"
                 })
        {
            Check(database.Find(L.Cpp, name) != null, "the C++ set includes '" + name + "'");
        }
        Check(database.Find(L.Cpp, "printf") != null, "the C++ profile inherits the C library ('printf')");
        Check(database.Find(L.Cpp, "malloc") != null, "the C++ profile inherits the C library ('malloc')");
        Check(database.Find(L.Cpp, "std::vector").Description.Contains("C++ sequence container"),
            "the description of 'std::vector' names a C++ sequence container");
        Check(database.Find(L.Cpp, "std::sort").Description.Contains("C++ algorithm"),
            "the description of 'std::sort' names a C++ algorithm");
        Check(database.Find(L.Cpp, "std::filesystem::exists").Description.Contains("C++ filesystem"),
            "the description of 'std::filesystem::exists' names the C++ filesystem");
        Check(database.Find(L.CLike, "std::vector") == null, "C files do not see 'std::vector'");
        Check(database.Find(L.CLike, "std::sort") == null, "C files do not see 'std::sort'");
        Check(database.Find(L.CLike, "std::string") == null, "C files do not see 'std::string'");

        ParameterSignature cppPushBack = database.FindSignature(L.Cpp, "std::vector::push_back");
        Check(cppPushBack != null, "a 'std::vector::push_back' call resolves to the C++ method");
        Equal(cppPushBack.ToString(), "std::vector::push_back(const T& value)",
            "the resolved C++ signature keeps the qualified name");

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

        // The Lua standard library entries are offered under their plain names too.
        List<ISuggestion> luaPatterns = Build(L.Lua, "", "gmat", database);
        ISuggestion gmatch = luaPatterns.FirstOrDefault(s => s.DisplayName == "gmatch");
        Check(gmatch != null, "the completion list offers 'gmatch' from 'string.gmatch'");
        Equal(gmatch.DetailText, "Iterate over matches", "the Lua library summary is the detail text");
        Check(gmatch.HintText.StartsWith("string.gmatch("), "the qualified Lua signature is the hint text");
        Check(Build(L.Lua, "", "isyi", database).Any(s => s.DisplayName == "isyieldable"),
            "the completion list offers 'coroutine.isyieldable' as 'isyieldable'");
        Check(Build(L.Lua, "", "codepo", database).Any(s => s.DisplayName == "codepoint"),
            "the completion list offers 'utf8.codepoint' as 'codepoint'");

        // SQL functions and data types feed the completion list with their summaries.
        List<ISuggestion> sqlWindow = Build(L.Sql, "", "ROW_N", database);
        ISuggestion rowNumber = sqlWindow.FirstOrDefault(s => s.DisplayName == "ROW_NUMBER");
        Check(rowNumber != null, "the completion list offers the SQL function 'ROW_NUMBER'");
        Equal(rowNumber.DetailText, "Row number in a window", "the SQL summary is the detail text");
        Check(rowNumber.HintText.StartsWith("ROW_NUMBER("), "the SQL signature is the hint text");
        ISuggestion varchar = Build(L.Sql, "", "VARC", database).FirstOrDefault(s => s.DisplayName == "VARCHAR");
        Check(varchar != null, "the completion list offers the SQL type 'VARCHAR'");
        Equal(varchar.DetailText, "VARCHAR data type", "the SQL type summary is the detail text");

        // The C++ profile offers the STL under the last segment and keeps the inherited C library.
        List<ISuggestion> cppVector = Build(L.Cpp, "", "vec", database);
        ISuggestion vector = cppVector.FirstOrDefault(s => s.DisplayName == "vector");
        Check(vector != null, "the completion list offers 'std::vector' as 'vector'");
        Equal(vector.DetailText, "Dynamic array", "the C++ container summary is the detail text");
        Check(vector.HintText.StartsWith("std::vector("), "the qualified C++ signature is the hint text");

        List<ISuggestion> cppSort = Build(L.Cpp, "", "sor", database);
        ISuggestion sort = cppSort.FirstOrDefault(s => s.DisplayName == "sort");
        Check(sort != null, "the completion list offers 'std::sort' as 'sort'");
        Equal(sort.DetailText, "Sort a range", "the C++ algorithm summary is the detail text");
        Check(sort.HintText.StartsWith("std::sort("), "the qualified C++ algorithm signature is the hint text");

        ISuggestion cppPushBack = Build(L.Cpp, "", "push_b", database).FirstOrDefault(s => s.DisplayName == "push_back");
        Check(cppPushBack != null, "the completion list offers 'std::vector::push_back' as 'push_back'");
        Equal(cppPushBack.DetailText, "Add an element at the end", "the C++ method summary is the detail text");
        Check(cppPushBack.HintText.StartsWith("std::vector::push_back("),
            "the qualified C++ method signature is the hint text");

        Check(Build(L.Cpp, "", "print", database).Any(s => s.DisplayName == "printf"),
            "the C++ completion list offers the inherited C 'printf'");
        Check(!Build(L.CLike, "", "vec", database).Any(s => s.DisplayName == "vector"),
            "the C completion list does not offer the STL");
        Check(!Build(L.CLike, "", "sor", database).Any(s => s.DisplayName == "sort"),
            "the C completion list does not offer the C++ algorithms");

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

        // Built-in type methods and the phase-2 modules feed the completion list under their short names.
        List<ISuggestion> pythonDict = Build(L.Python, "", "get", database);
        ISuggestion dictGet = pythonDict.FirstOrDefault(s => s.DisplayName == "get");
        Check(dictGet != null, "the completion list offers 'dict.get' as 'get'");
        Equal(dictGet.DetailText, "Read a value", "the dict method summary is the detail text");
        Check(dictGet.HintText.StartsWith("dict.get("), "the qualified dict method signature is the hint text");
        ISuggestion append = Build(L.Python, "", "append", database).FirstOrDefault(s => s.DisplayName == "append");
        Check(append != null, "the completion list offers 'list.append' as 'append'");
        Equal(append.DetailText, "Append an item", "the built-in method wins over the deque method");
        Check(append.HintText.StartsWith("list.append("), "the list method signature is the hint text");
        ISuggestion read = Build(L.Python, "", "read", database).FirstOrDefault(s => s.DisplayName == "read");
        Check(read != null, "the completion list offers 'file.read' as 'read'");
        Equal(read.DetailText, "Read the file", "the file method wins over the os function");
        Check(read.HintText.StartsWith("file.read("), "the file method signature is the hint text");
        ISuggestion connect = Build(L.Python, "", "create_conn", database).FirstOrDefault(s => s.DisplayName == "create_connection");
        Check(connect != null, "the completion list offers 'socket.create_connection' as 'create_connection'");
        Equal(connect.DetailText, "Connect to a host", "the socket summary is the detail text");
        Check(connect.HintText.StartsWith("socket.create_connection("), "the socket signature is the hint text");

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

        ParameterSignature luaGsubHint = EditorSignatureSource.FindSignature(
            L.Lua, "gsub(\n", 0, 5, database, out int luaGsubActive);
        Check(luaGsubHint != null, "a bare 'gsub(' call finds the 'string.gsub' symbol");
        Check(luaGsubHint.Name == "string.gsub", "the Lua string hint keeps the qualified name");
        Equal(luaGsubHint.ParameterCount, 3, "the Lua string hint has its parameters");

        ParameterSignature sql = EditorSignatureSource.FindSignature(
            L.Sql, "CONCAT(\n", 0, 7, database, out int sqlActive);
        Check(sql != null, "a bare 'CONCAT(' call finds the SQL function");
        Equal(sql.Name, "CONCAT", "the SQL hint keeps the function name");
        Equal(sql.ParameterCount, 2, "the SQL hint has its parameters");

        ParameterSignature python = EditorSignatureSource.FindSignature(
            L.Python, "sqrt(\n", 0, 5, database, out int pythonActive);
        Check(python != null, "a bare 'sqrt(' call finds the Python 'math.sqrt' symbol");
        Check(python.Name == "math.sqrt", "the Python hint keeps the qualified name");

        ParameterSignature getHint = EditorSignatureSource.FindSignature(
            L.Python, "get(\n", 0, 4, database, out int getActive);
        Check(getHint != null, "a bare 'get(' call finds the 'dict.get' method");
        Equal(getHint.Name, "dict.get", "the dict method hint keeps the qualified name");
        Equal(getHint.ParameterCount, 2, "the dict method hint has its parameters");

        ParameterSignature appendHint = EditorSignatureSource.FindSignature(
            L.Python, "append(\n", 0, 7, database, out int appendActive);
        Check(appendHint != null, "a bare 'append(' call finds the 'list.append' method");
        Equal(appendHint.Name, "list.append", "the list method hint keeps the qualified name");
        Equal(appendHint.ParameterCount, 1, "the list method hint has its parameter");

        ParameterSignature readHint = EditorSignatureSource.FindSignature(
            L.Python, "read(\n", 0, 5, database, out int readActive);
        Check(readHint != null, "a bare 'read(' call finds the 'file.read' method");
        Equal(readHint.Name, "file.read", "the file method hint keeps the qualified name");
        Equal(readHint.ParameterCount, 1, "the file method hint has its parameter");

        ParameterSignature runHint = EditorSignatureSource.FindSignature(
            L.Python, "subprocess.run(\n", 0, 15, database, out int runActive);
        Check(runHint != null, "a 'subprocess.run(' call finds the 'subprocess.run' function");
        Equal(runHint.Name, "subprocess.run", "the subprocess hint keeps the qualified name");
        Check(runHint.ParameterCount > 5, "the subprocess hint has its parameters");

        ParameterSignature cppSort = EditorSignatureSource.FindSignature(
            L.Cpp, "sort(\n", 0, 5, database, out int cppSortActive);
        Check(cppSort != null, "a bare 'sort(' call finds the C++ 'std::sort' algorithm");
        Equal(cppSort.Name, "std::sort", "the C++ hint keeps the qualified algorithm name");
        Equal(cppSort.ParameterCount, 3, "the C++ algorithm hint has its parameters");
        Equal(cppSortActive, 0, "the first C++ parameter is active");

        ParameterSignature cppPush = EditorSignatureSource.FindSignature(
            L.Cpp, "push_back(\n", 0, 10, database, out int cppPushActive);
        Check(cppPush != null, "a bare 'push_back(' call finds 'std::vector::push_back'");
        Equal(cppPush.Name, "std::vector::push_back", "the C++ method hint keeps the qualified name");
        Equal(cppPush.ParameterCount, 1, "the C++ method hint has its parameter");

        ParameterSignature cppPrintf = EditorSignatureSource.FindSignature(
            L.Cpp, "printf(\n", 0, 7, database, out int cppPrintfActive);
        Check(cppPrintf != null, "a 'printf(' call in C++ finds the inherited C function");
        Equal(cppPrintf.Name, "printf", "the inherited C hint keeps its name");

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
