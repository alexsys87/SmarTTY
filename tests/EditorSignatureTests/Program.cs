using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.CodeEditing;
using SmarTTY.Completion;
using L = SmarTTY.CodeEditing.SyntaxHighlighting.Language;

// Regression harness for the editor's parameter hint:
//  * the function definitions parsed from the document (per language),
//  * the signature of the call the caret is inside and the parameter being typed.
// Run with: dotnet run --project tests/EditorSignatureTests
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

    private static List<string> Definitions(L language, string text)
    {
        return EditorSignatureSource.ParseDefinitions(language, text).Select(s => s.ToString()).ToList();
    }

    private static string Signature(L language, string text, int line, int column)
    {
        ParameterSignature signature =
            EditorSignatureSource.FindSignature(language, text, line, column, out int active);
        return signature == null ? null : signature + "@" + active;
    }

    private static void DefinitionChecks()
    {
        // Python.
        Equal(string.Join("|", Definitions(L.Python, "def add(a, b):\n    return a + b\n")), "add(a, b)", "python: def is parsed");
        Equal(string.Join("|", Definitions(L.Python, "def f():\n    pass\n")), "f()", "python: no parameters");
        Check(!Definitions(L.Python, "# def f(a):\n").Any(), "python: a comment is not a definition");
        Equal(string.Join("|", Definitions(L.Python, "def f(a, b=(1, 2)):\n    pass\n")), "f(a, b=(1, 2))", "python: a default with a comma stays one parameter");

        // Lua.
        Equal(string.Join("|", Definitions(L.Lua, "local function greet(name)\nend\n")), "greet(name)", "lua: local function");
        Equal(string.Join("|", Definitions(L.Lua, "function M.add(a, b)\nend\n")), "add(a, b)", "lua: method name keeps the last identifier");
        Check(!Definitions(L.Lua, "-- function f(a)\n").Any(), "lua: a comment is not a definition");

        // AWK has its own profile and parses its "function" declaration form.
        Equal(string.Join("|", Definitions(L.Awk, "function add(a, b) {\n    return a + b\n}\n")), "add(a, b)", "awk: function");
        Equal(string.Join("|", Definitions(L.Awk, "func trim(s) {\n    return s\n}\n")), "trim(s)", "awk: func");
        Check(!Definitions(L.Awk, "# function hidden(a)\n").Any(), "awk: a comment is not a definition");

        // Ruby, Perl and CMake have their own profiles; Dockerfile has no functions.
        Equal(string.Join("|", Definitions(L.Ruby, "def render(item, style)\nend\n")), "render(item, style)", "ruby: def");
        Equal(string.Join("|", Definitions(L.Ruby, "def self.render(item)\nend\n")), "render(item)", "ruby: a singleton method keeps the callable name");
        Equal(string.Join("|", Definitions(L.Perl, "sub trim($)\n}\n")), "trim($)", "perl: sub");
        Equal(string.Join("|", Definitions(L.CMake, "function(my_func arg1 arg2)\nendfunction()\n")), "my_func(arg1 arg2)", "cmake: function");
        Equal(string.Join("|", Definitions(L.CMake, "macro(my_macro)\nendmacro()\n")), "my_macro()", "cmake: macro");
        Check(!Definitions(L.Dockerfile, "FROM alpine\n").Any(), "dockerfile: no definitions");

        // C-like.
        Equal(string.Join("|", Definitions(L.CLike, "int add(int a, int b) {\n    return a + b;\n}\n")), "add(int a, int b)", "c-like: a function definition");
        Equal(string.Join("|", Definitions(L.CLike, "void foo(void);\n")), "foo()", "c-like: void foo(void) has no parameters");
        Equal(string.Join("|", Definitions(L.CLike, "static std::string name(int x);\n")), "name(int x)", "c-like: a return type with '::' is accepted");
        Check(!Definitions(L.CLike, "x = foo(1, 2);\n").Any(), "c-like: an assignment is not a definition");
        Check(!Definitions(L.CLike, "return foo(1);\n").Any(), "c-like: a call after return is not a definition");
        Check(!Definitions(L.CLike, "if (foo(a)) {\n}\n").Any(), "c-like: a control statement is not a definition");

        // The split C-like profiles parse their own function declaration forms.
        Equal(string.Join("|", Definitions(L.CSharp, "public static int Add(int a, int b) {\n    return a + b;\n}\n")),
            "Add(int a, int b)", "c#: a method definition");
        Equal(string.Join("|", Definitions(L.Java, "public int add(int a, int b) {\n    return a + b;\n}\n")),
            "add(int a, int b)", "java: a method definition");
        Equal(string.Join("|", Definitions(L.Kotlin, "fun add(a: Int, b: Int): Int {\n    return a + b\n}\n")),
            "add(a: Int, b: Int)", "kotlin: fun");
        Equal(string.Join("|", Definitions(L.Scala, "def add(a: Int, b: Int): Int = {\n    a + b\n}\n")),
            "add(a: Int, b: Int)", "scala: def");
        Equal(string.Join("|", Definitions(L.Groovy, "def add(int a, int b) {\n    a + b\n}\n")),
            "add(int a, int b)", "groovy: def");
        Equal(string.Join("|", Definitions(L.Go, "func add(a int, b int) int {\n    return a + b\n}\n")),
            "add(a int, b int)", "go: func");
        Equal(string.Join("|", Definitions(L.Rust, "fn add(a: i32, b: i32) -> i32 {\n    a + b\n}\n")),
            "add(a: i32, b: i32)", "rust: fn");
        Equal(string.Join("|", Definitions(L.Swift, "func add(_ a: Int, _ b: Int) -> Int {\n    return a + b\n}\n")),
            "add(_ a: Int, _ b: Int)", "swift: func");
        Equal(string.Join("|", Definitions(L.Dart, "int add(int a, int b) {\n    return a + b;\n}\n")),
            "add(int a, int b)", "dart: a function definition");
        Equal(string.Join("|", Definitions(L.JavaScript, "function add(a, b) {\n    return a + b;\n}\n")),
            "add(a, b)", "javascript: function");
        Equal(string.Join("|", Definitions(L.TypeScript, "function add(a: number, b: number): number {\n    return a + b;\n}\n")),
            "add(a: number, b: number)", "typescript: function");
        Equal(string.Join("|", Definitions(L.Php, "function add($a, $b) {\n    return $a + $b;\n}\n")),
            "add($a, $b)", "php: function");
        Check(!Definitions(L.Css, ".card {\n    color: red;\n}\n").Any(), "css: no function definitions");
        Check(!Definitions(L.Protobuf, "rpc Get(User) returns (User);\n").Any(), "protobuf: rpc is not a function definition");
        Check(!Definitions(L.DeviceTree, "/ { compatible = \"board\"; };\n").Any(), "device tree: no function definitions");
        Check(!Definitions(L.Tcl, "proc add {a b} {\n    expr {$a + $b}\n}\n").Any(), "tcl: a proc is not parsed as a function definition");
        Check(!Definitions(L.Tcl, "# proc hidden {a} {\n}\n").Any(), "tcl: a '#' comment is not a definition");
        Check(!Definitions(L.Assembly, "add:\n    mov eax, 1\n    ret\n").Any(), "assembly: a label is not a function definition");
        Check(!Definitions(L.Assembly, "; add(a, b)\n").Any(), "assembly: a ';' comment is not a definition");
        Check(!Definitions(L.PowerShell, "function Get-Thing {\n    param($Name)\n}\n").Any(), "powershell: a function is not parsed as a parenthesized definition");
        Check(!Definitions(L.PowerShell, "# function hidden(a)\n").Any(), "powershell: a '#' comment is not a definition");
        Check(!Definitions(L.PowerShell, "<# function hidden(a) #>\n").Any(), "powershell: a block comment is not a definition");
        Check(!Definitions(L.Php, "# function hidden(a) {\n").Any(), "php: a '#' comment is not a definition");

        // Languages without a recognized declaration form.
        Check(!Definitions(L.Json, "{ \"a\": 1 }\n").Any(), "json: no definitions");
        Check(!Definitions(L.Plain, "def f(a)\n").Any(), "plain text: no definitions");
    }

    private static void Main2Checks()
    {
        // C: the brace on a line of its own, and a parameter list over several lines.
        Equal(string.Join("|", Definitions(L.CLike, "int main(int argc, char **argv)\n{\n    return 0;\n}\n")), "main(int argc, char **argv)", "c-like: the brace on the next line");
        Equal(string.Join("|", Definitions(L.CLike, "int add(int a,\n        int b)\n{\n    return a + b;\n}\n")), "add(int a, int b)", "c-like: the parameters over two lines");
        Equal(string.Join("|", Definitions(L.CLike, "int add(int a, /* first */ int b) // tail\n{\n}\n")), "add(int a, int b)", "c-like: comments between the parameters are not parameters");
        Equal(string.Join("|", Definitions(L.Cpp, "int size() const {\n    return 1;\n}\n")), "size()", "c++: a const method");
        Equal(string.Join("|", Definitions(L.Java, "void read(int a) throws IOException {\n}\n")), "read(int a)", "java: a throws clause");
        Check(!Definitions(L.CLike, "x = foo(1,\n    2);\n").Any(), "c-like: a call over two lines is not a definition");
        Check(!Definitions(L.CLike, "foo(1)\n    ;\n").Any(), "c-like: a call without a return type is not a definition");

        // Arrays and nullable types as return types.
        Equal(string.Join("|", Definitions(L.Java, "public byte[] read(int n) {\n}\n")), "read(int n)", "java: an array return type");
        Equal(string.Join("|", Definitions(L.CSharp, "public string? Find(string key) {\n}\n")), "Find(string key)", "c#: a nullable return type");

        // Type arguments keep their commas.
        Equal(string.Join("|", Definitions(L.Java, "void put(Map<String, Integer> m, int b) {\n}\n")), "put(Map<String, Integer> m, int b)", "java: a comma inside type arguments");
        Equal(string.Join("|", Definitions(L.Rust, "fn longest<'a>(x: &'a str, y: &'a str) -> &'a str {\n}\n")), "longest(x: &'a str, y: &'a str)", "rust: generics and lifetimes");
        Equal(string.Join("|", Definitions(L.Rust, "fn pick(x: &'a str) -> &'a str {\n}\n")), "pick(x: &'a str)", "rust: a lone lifetime is not a string");
        Equal(string.Join("|", Definitions(L.TypeScript, "function wrap<T>(value: T, n: number): T {\n}\n")), "wrap(value: T, n: number)", "typescript: type parameters after the name");
        Equal(string.Join("|", Definitions(L.Kotlin, "fun <T> first(items: List<T>, n: Int): T {\n}\n")), "first(items: List<T>, n: Int)", "kotlin: type parameters before the name");
        Equal(string.Join("|", Definitions(L.Go, "func (s *Server) Handle(path string, code int) error {\n}\n")), "Handle(path string, code int)", "go: a method with a receiver");

        // Python: a comment with an apostrophe between the parameters.
        Equal(string.Join("|", Definitions(L.Python, "def f(a,  # don't\n      b):\n    pass\n\ndef g(x):\n    pass\n")), "f(a, b)|g(x)", "python: an apostrophe in a comment does not open a string");

        // A method call does not pass the receiver.
        string cls = "class A:\n    def m(self, a, b):\n        pass\n\n";
        Equal(Signature(L.Python, cls + "obj.m(1, ", 4, 9), "m(a, b)@1", "python: obj.m( does not count self");
        Equal(Signature(L.Python, cls + "m(1, ", 4, 5), "m(self, a, b)@1", "python: a plain call keeps self");
        string rustImpl = "impl A {\n    fn m(&self, a: i32, b: i32) {\n    }\n}\n";
        Equal(Signature(L.Rust, rustImpl + "a.m(1, ", 4, 7), "m(a: i32, b: i32)@1", "rust: a.m( does not count &self");

        // Type arguments in a call.
        string java = "void f(int a, int b) {\n}\n";
        Equal(Signature(L.Java, java + "f(new HashMap<String, Integer>(), ", 2, 35), "f(int a, int b)@1", "java: the comma inside type arguments of an argument is not counted");
        Equal(Signature(L.Rust, "fn parse(s: &str, n: i32) {}\nparse::<i32>(\"1\", ", 1, 18), "parse(s: &str, n: i32)@1", "rust: a call with a turbofish");

        // A caret column past the end of the line does not read outside the text.
        Equal(Signature(L.Python, "f(", 0, 50), null, "a column past the end of the text is not an error");
    }

    private static void SignatureChecks()
    {
        Main2Checks();
        // The caret is right after the opening parenthesis.
        string py = "def add(a, b):\n    return a + b\n\nadd(\n";
        Equal(Signature(L.Python, py, 3, 4), "add(a, b)@0", "python: the call shows its signature, first parameter");

        // After one comma the second parameter is active.
        Equal(Signature(L.Python, py + "1, ", 4, 3), "add(a, b)@1", "python: the second parameter is active after a comma");

        // A nested call does not add a parameter to the outer one.
        string nested = "def outer(a, b):\n    pass\n\ndef inner(x):\n    pass\n\nouter(inner(1), \n";
        Equal(Signature(L.Python, nested, 6, 15), "outer(a, b)@1", "python: a comma inside a nested call is not counted");

        // A comma inside a string is not a separator.
        Equal(Signature(L.Python, py + "\"a, b\", ", 4, 8), "add(a, b)@1", "python: a comma inside a string does not count");

        // A comma inside a nested pair of brackets is not a separator.
        Equal(Signature(L.Python, py + "[1, 2], ", 4, 8), "add(a, b)@1", "python: a comma inside [] does not count");

        // The caret outside any call has no signature.
        Equal(Signature(L.Python, py, 0, 3), null, "python: outside a call there is no signature");

        // A call of a function the document does not define has no signature.
        Equal(Signature(L.Python, "print(\n", 0, 6), null, "python: a call of an unknown function has no signature");

        // C-like.
        string c = "int add(int a, int b) {\n    return a + b;\n}\n\nint main() {\n    add(1, \n}\n";
        Equal(Signature(L.CLike, c, 5, 11), "add(int a, int b)@1", "c-like: the call shows its signature, second parameter");

        // The same call hint works for the split profiles that use a C-style call.
        string java = "public int add(int a, int b) {\n    return a + b;\n}\n\nadd(1, \n";
        Equal(Signature(L.Java, java, 4, 7), "add(int a, int b)@1", "java: the call shows its signature, second parameter");
        string rust = "fn add(a: i32, b: i32) -> i32 {\n    a + b\n}\n\nadd(1, \n";
        Equal(Signature(L.Rust, rust, 4, 7), "add(a: i32, b: i32)@1", "rust: the call shows its signature, second parameter");

        // The active parameter never runs past the last one.
        Equal(Signature(L.Python, py + "1, 2, 3, ", 4, 9), "add(a, b)@1", "python: the active parameter is clamped to the last one");

        // The caret on the closing parenthesis still belongs to the call.
        Equal(Signature(L.Python, py + "1, 2)", 4, 4), "add(a, b)@1", "python: the caret just before ')' is still inside the call");

        // AWK function definitions feed the hint of a call in an action block.
        string awk = "function add(a, b) {\n    return a + b\n}\n\nBEGIN { add(1, \n}\n";
        Equal(Signature(L.Awk, awk, 4, 15), "add(a, b)@1", "awk: the call shows its signature");

        // A CRLF document (the editor's usual line ending) is handled.
        string crlf = "def add(a, b):\r\n    return a + b\r\n\r\nadd(\r\n";
        Equal(Signature(L.Python, crlf, 3, 4), "add(a, b)@0", "python: a CRLF document is handled");
    }

    private static void Main()
    {
        DefinitionChecks();
        SignatureChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " signature checks passed.");
    }
}
