using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using L = SmarTTY.CodeEditing.SyntaxHighlighting.Language;

// Regression harness for the remote file editor's text model and syntax highlighting:
//  * line endings of new lines and of undone deletions,
//  * language detection,
//  * tokenizers (comments, strings, keys, variables, multi-line state),
//  * bracket pairing through TokenCache, including after edits.
// Run with: dotnet run --project tests/EditorTextTests
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
        Check(equal, name + (equal ? "" : $" (expected '{Escape(expected)}', got '{Escape(actual)}')"));
    }

    private static string Escape(object value)
    {
        return (value?.ToString() ?? "null").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static EditableTextDocument Document(string text)
    {
        EditableTextDocument document = new EditableTextDocument { DefaultLineEnding = TextDocument.LineEnding.Lf };
        document.SetText(text);
        return document;
    }

    // Mirrors EditableTextBrowser.DeleteRange.
    private static void DeleteRange(EditableTextDocument document, TextDocument.TextPosition start, TextDocument.TextPosition end)
    {
        if (start.Line == end.Line)
        {
            document.ReplaceInLine(start.Line, start.Column, end.Column - start.Column, "");
            return;
        }
        document.ReplaceInLine(start.Line, start.Column, -1, "");
        document.ReplaceInLine(end.Line, 0, end.Column, "");
        int removedLineCount = end.Line - start.Line - 1;
        if (removedLineCount != 0)
        {
            document.RemoveLines(start.Line + 1, removedLineCount);
        }
        document.JoinWithNextLine(start.Line);
    }

    private static void LineEndingChecks()
    {
        EditableTextDocument empty = Document("");
        Equal(empty.GetEffectiveLineTerminator(0), "\n", "the only line of an empty file ends like the document default");
        string enter = empty.GetLineTerminator(0, "\n");
        if (enter.Length == 0)
        {
            enter = TextDocument.GetLineEndingText(empty.DefaultLineEnding);
        }
        empty.InsertText(new TextDocument.TextPosition(0, 0), enter, inheritLineEnding: true);
        Equal(empty.GetText(), "\n", "Enter in an empty file inserts a line break");

        EditableTextDocument single = Document("abc");
        Equal(single.GetEffectiveLineTerminator(0), "\n", "a single line without a newline uses the default ending");

        EditableTextDocument crlf = Document("a\r\nb\r\n");
        Equal(crlf.GetEffectiveLineTerminator(0), "\r\n", "a CRLF line keeps its ending");

        // Undo of a multi-line deletion must restore LF endings in an LF file.
        const string original = "one\ntwo\nthree\nfour\n";
        EditableTextDocument lf = Document(original);
        TextDocument.TextPosition start = new TextDocument.TextPosition(1, 0);
        TextDocument.TextPosition end = new TextDocument.TextPosition(2, 2);
        string removed = lf.GetRangeText(start, end, lf.GetEffectiveLineTerminator);
        Equal(removed, "ne\ntwo\nth", "the removed text keeps the LF terminators");
        DeleteRange(lf, start, end);
        Equal(lf.GetText(), "oree\nfour\n", "the range is deleted");
        lf.InsertText(start, removed);
        Equal(lf.GetText(), original, "re-inserting the removed text restores the LF file exactly");

        EditableTextDocument crlfUndo = Document("a\r\nb\r\nc");
        string crlfRemoved = crlfUndo.GetRangeText(new TextDocument.TextPosition(0, 0), new TextDocument.TextPosition(0, 2), crlfUndo.GetEffectiveLineTerminator);
        Equal(crlfRemoved, "a\r\nb\r\n", "CRLF terminators are kept as well");
    }

    private static void LanguageDetectionChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/Main.PY"), L.Python, "extensions are case insensitive");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/Dockerfile"), L.Script, "Dockerfile (capitalized) is recognized");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/Makefile"), L.Makefile, "Makefile (capitalized) is recognized");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/.bashrc"), L.Bash, ".bashrc is bash");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/ssh/sshd_config"), L.Config, "sshd_config is a config file");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/systemd/system/app.service"), L.Config, "systemd units are config files");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.c"), L.CLike, "C sources are C-like");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.cpp"), L.Cpp, "C++ sources are C++");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.cxx"), L.Cpp, ".cxx is C++");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.cc"), L.Cpp, ".cc is C++");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.c++"), L.Cpp, ".c++ is C++");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.hpp"), L.Cpp, "C++ headers are C++");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.hxx"), L.Cpp, ".hxx is C++");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.hh"), L.Cpp, ".hh is C++");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.h++"), L.Cpp, ".h++ is C++");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.ipp"), L.Cpp, ".ipp is C++");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.tpp"), L.Cpp, ".tpp is C++");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.inl"), L.Cpp, ".inl is C++");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.ixx"), L.Cpp, ".ixx is a C++ module interface");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.cppm"), L.Cpp, ".cppm is a C++ module");
        Equal(SyntaxHighlighting.DetectLanguage("/src/sketch.ino"), L.Cpp, ".ino is a C++ sketch");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.h"), L.CLike, "C headers stay C-like");
        Equal(SyntaxHighlighting.GetDisplayName(L.Cpp), "C++", "C++ has its own display name");
        Check(SyntaxHighlighting.SelectableLanguages.Contains(L.Cpp), "C++ is in the syntax menu");
        Check(SyntaxHighlighting.GetKeywords(L.Cpp).Contains("constexpr"), "C++ keywords contain 'constexpr'");
        Check(SyntaxHighlighting.GetKeywords(L.Cpp).Contains("nullptr"), "C++ keywords contain 'nullptr'");
        Check(!SyntaxHighlighting.GetKeywords(L.Cpp).Contains("interface"), "C++ keywords do not include the C# 'interface'");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/Dockerfile.dev"), L.Script, "Dockerfile.dev is a Dockerfile");
        Equal(SyntaxHighlighting.DetectLanguage("/src/Makefile.am"), L.Makefile, "Makefile.am is a makefile");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/bin/tool", "#!/bin/busybox sh"), L.Bash, "#!/bin/busybox sh is shell");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/bin/tool", "#!/usr/bin/env node"), L.CLike, "#!/usr/bin/env node is JavaScript");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/syslog.log"), L.Plain, "logs are not highlighted");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/notes.txt"), L.Plain, "text files are not highlighted");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/README.md"), L.Plain, "markdown is not highlighted");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/fix.patch"), L.Diff, "patches are diffs");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/index.html"), L.Markup, "HTML is markup");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/backup", "#!/bin/bash"), L.Bash, "#!/bin/bash makes a script without an extension bash");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/env python3"), L.Python, "#!/usr/bin/env python3 is python");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/env -S perl -w"), L.Script, "env options are skipped");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "hello"), L.Plain, "no shebang: plain");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/x.sh", "#!/usr/bin/python"), L.Bash, "the extension wins over the shebang");
    }

    private sealed class Highlighted
    {
        public readonly TokenCache Cache;
        public readonly EditableTextDocument Doc;
        public readonly TokenCache.LineTokenizer Tokenizer;

        public Highlighted(L language, string text)
        {
            Doc = Document(text);
            Tokenizer = SyntaxHighlighting.GetTokenizer(language);
            Cache = new TokenCache(Doc, Tokenizer);
        }

        /// <summary>Returns the text of every token of the given type on a line.</summary>
        public List<string> Tokens(int line, int type)
        {
            string text = Doc.GetLineText(line);
            return Cache.GetLineTokens(line).Where(t => t.Type == type)
                .Select(t => text.Substring(t.Start, t.End - t.Start)).ToList();
        }

        /// <summary>Returns the token that starts exactly at the column.</summary>
        public TokenCache.Token At(int line, int column)
        {
            return Cache.GetLineTokens(line).FirstOrDefault(t => t.Start == column);
        }

        public int TypeAt(int line, int column)
        {
            foreach (TokenCache.Token token in Cache.GetLineTokens(line))
            {
                if (token.Start <= column && column < token.End)
                {
                    return token.Type;
                }
            }
            return SyntaxHighlighting.TypeNormal;
        }
    }

    private static string Join(List<string> items)
    {
        return string.Join("|", items);
    }

    private static void ConfigChecks()
    {
        Highlighted unit = new Highlighted(L.Config, "[Service]\nExecStart=/usr/bin/app --verbose -- run\n---\ncolor=#fff\nkey = value # note\n; ini comment\nPort 22\nlisten 192.168.0.1:8080;\nenabled: yes\n- name: web");
        Equal(Join(unit.Tokens(0, SyntaxHighlighting.TypeSection)), "[Service]", "[section] header");
        Equal(Join(unit.Tokens(1, SyntaxHighlighting.TypeSection)), "", "'--' inside a value is not a section marker");
        Equal(Join(unit.Tokens(1, SyntaxHighlighting.TypeVariable)), "ExecStart", "key before '='");
        Equal(Join(unit.Tokens(2, SyntaxHighlighting.TypeSection)), "---", "YAML document marker");
        Equal(Join(unit.Tokens(3, SyntaxHighlighting.TypeComment)), "", "'#fff' glued to the value is not a comment");
        Equal(Join(unit.Tokens(4, SyntaxHighlighting.TypeComment)), "# note", "trailing '# note' is a comment");
        Equal(Join(unit.Tokens(5, SyntaxHighlighting.TypeComment)), "; ini comment", "';' comment at the start of a line");
        Equal(Join(unit.Tokens(6, SyntaxHighlighting.TypeVariable)), "Port", "sshd_config style keyword");
        Equal(Join(unit.Tokens(6, SyntaxHighlighting.TypeNumber)), "22", "number value");
        Equal(Join(unit.Tokens(7, SyntaxHighlighting.TypeNumber)), "192.168.0.1|8080", "IP address and port are numbers");
        Equal(Join(unit.Tokens(8, SyntaxHighlighting.TypeKeyword)), "yes", "yes/no constants");
        Equal(Join(unit.Tokens(9, SyntaxHighlighting.TypeVariable)), "name", "YAML list item key");
    }

    private static void BashChecks()
    {
        Highlighted sh = new Highlighted(L.Bash, "#!/bin/bash\necho $? $# ${HOME} $1\ngit config --set x\nFOO=bar; export PATH+=:/x\nls a#b ;# done\nX='multi\nline' done\necho \"a \\\" b\" # c\nif [ -f /usr/bin/test ]; then");
        Equal(Join(sh.Tokens(0, SyntaxHighlighting.TypePreprocessor)), "#!/bin/bash", "shebang");
        Equal(Join(sh.Tokens(1, SyntaxHighlighting.TypeVariable)), "$?|$#|${HOME}|$1", "special and braced variables");
        Equal(Join(sh.Tokens(1, SyntaxHighlighting.TypeComment)), "", "'$#' does not start a comment");
        Equal(Join(sh.Tokens(2, SyntaxHighlighting.TypeKeyword)), "", "'--set' is an argument, not the set builtin");
        Equal(Join(sh.Tokens(3, SyntaxHighlighting.TypeVariable)), "FOO|PATH", "assignments (= and +=)");
        Equal(Join(sh.Tokens(4, SyntaxHighlighting.TypeComment)), "# done", "'a#b' is a word, ';#' starts a comment");
        Equal(Join(sh.Tokens(5, SyntaxHighlighting.TypeString)), "'multi", "an unclosed single quote...");
        Equal(Join(sh.Tokens(6, SyntaxHighlighting.TypeString)), "line'", "...continues on the next line");
        Equal(Join(sh.Tokens(6, SyntaxHighlighting.TypeKeyword)), "done", "and the line goes on after the quote");
        Equal(Join(sh.Tokens(7, SyntaxHighlighting.TypeString)), "\"a \\\" b\"", "escaped quote inside a string");
        Equal(Join(sh.Tokens(7, SyntaxHighlighting.TypeComment)), "# c", "comment after the string");
        Equal(Join(sh.Tokens(8, SyntaxHighlighting.TypeKeyword)), "if|then", "keywords; 'test' inside a path is not one");

        // A here document is text up to its terminator: an apostrophe inside must not open a string.
        Highlighted heredoc = new Highlighted(L.Bash, "cat <<EOF\ndon't stop\nEOF\necho 'a'");
        Equal(Join(heredoc.Tokens(1, SyntaxHighlighting.TypeString)), "don't stop", "the body of a here document is text");
        Equal(Join(heredoc.Tokens(2, SyntaxHighlighting.TypeKeyword)), "EOF", "the terminator ends it");
        Equal(Join(heredoc.Tokens(3, SyntaxHighlighting.TypeString)), "'a'", "and the code after it is highlighted again");
        Highlighted stripped = new Highlighted(L.Bash, "cat <<-'END'\n\t$x 'q\n\tEND\nx='b'");
        Equal(Join(stripped.Tokens(1, SyntaxHighlighting.TypeVariable)), "", "a quoted here document has no variables");
        Equal(Join(stripped.Tokens(2, SyntaxHighlighting.TypeKeyword)), "\tEND", "<<- accepts a terminator indented with tabs");
        Equal(Join(stripped.Tokens(3, SyntaxHighlighting.TypeString)), "'b'", "text after a <<- here document");
        Highlighted shift = new Highlighted(L.Bash, "echo $((1 << n))\nx='a'");
        Equal(Join(shift.Tokens(1, SyntaxHighlighting.TypeString)), "'a'", "a shift in arithmetic is not a here document");
        Highlighted hereString = new Highlighted(L.Bash, "cat <<< word\nx='a'");
        Equal(Join(hereString.Tokens(1, SyntaxHighlighting.TypeString)), "'a'", "a here string is not a here document");
    }

    private static void PythonChecks()
    {
        Highlighted py = new Highlighted(L.Python, "@app.route('/')\ndef f(x):\n    s = \"\"\"doc\n    more \\\"\"\" still\"\"\" + 1\n    return {'a': [1, 2]}  # done");
        Equal(Join(py.Tokens(0, SyntaxHighlighting.TypePreprocessor)), "@app.route", "decorator");
        Equal(Join(py.Tokens(1, SyntaxHighlighting.TypeKeyword)), "def", "keyword");
        Equal(Join(py.Tokens(2, SyntaxHighlighting.TypeString)), "\"\"\"doc", "triple-quoted string starts");
        Equal(Join(py.Tokens(3, SyntaxHighlighting.TypeString)), "    more \\\"\"\" still\"\"\"", "an escaped quote does not close the triple-quoted string");
        Equal(Join(py.Tokens(3, SyntaxHighlighting.TypeNumber)), "1", "code after the string");
        Equal(Join(py.Tokens(4, SyntaxHighlighting.TypeComment)), "# done", "comment");

        TokenCache.Token open = py.At(1, 5);
        Check(open != null && open.Kind == TokenCache.Token.TokenKind.Paired && open.Partner != null && open.Partner.Start == 7, "'(' is paired with ')'");
        TokenCache.Token brace = py.At(4, 11);
        Check(brace != null && brace.Partner != null && py.Doc.GetLineText(4)[brace.Partner.Start] == '}', "'{' is paired with '}'");
    }

    private static void CppChecks()
    {
        Highlighted cpp = new Highlighted(L.Cpp,
            "#include <vector>\nstd::vector<int> v; // note\nconstexpr int n = 3;\nauto p = std::make_unique<int>(1);\nclass Widget {\npublic:\n  virtual ~Widget() = default;\n};");
        Equal(Join(cpp.Tokens(0, SyntaxHighlighting.TypePreprocessor)), "#include", "C++ preprocessor directive");
        Equal(Join(cpp.Tokens(1, SyntaxHighlighting.TypeKeyword)), "std|int", "C++ namespace and type keywords");
        Equal(Join(cpp.Tokens(1, SyntaxHighlighting.TypeComment)), "// note", "C++ line comment");
        Equal(Join(cpp.Tokens(2, SyntaxHighlighting.TypeKeyword)), "constexpr|int", "C++ constexpr keyword");
        Equal(Join(cpp.Tokens(3, SyntaxHighlighting.TypeKeyword)), "auto|std|int", "C++ auto keyword");
        Equal(Join(cpp.Tokens(4, SyntaxHighlighting.TypeKeyword)), "class", "C++ class keyword");
        Equal(Join(cpp.Tokens(5, SyntaxHighlighting.TypeKeyword)), "public", "C++ access specifier");
        Equal(Join(cpp.Tokens(6, SyntaxHighlighting.TypeKeyword)), "virtual|default", "C++ virtual and default keywords");
        TokenCache.Token brace = cpp.At(4, 13);
        Check(brace != null && brace.Kind == TokenCache.Token.TokenKind.Paired && brace.Partner != null &&
              brace.Partner.LineNumber == 7, "C++ braces are paired across lines");
        Check(brace.Style != null, "C++ braces are foldable");
    }

    private static void GenericChecks()
    {
        Highlighted c = new Highlighted(L.CLike, "#include <stdio.h>\nint main() { /* a\n b */ return 0; }\nconst char *u = \"http://x\"; // c\n#fff {");
        Equal(Join(c.Tokens(0, SyntaxHighlighting.TypePreprocessor)), "#include", "preprocessor directive");
        Equal(Join(c.Tokens(1, SyntaxHighlighting.TypeComment)), "/* a", "block comment starts");
        Equal(Join(c.Tokens(2, SyntaxHighlighting.TypeComment)), " b */", "block comment continues on the next line");
        Equal(Join(c.Tokens(2, SyntaxHighlighting.TypeKeyword)), "return", "code after the block comment");
        Equal(Join(c.Tokens(3, SyntaxHighlighting.TypeString)), "\"http://x\"", "'//' inside a string is not a comment");
        Equal(Join(c.Tokens(3, SyntaxHighlighting.TypeComment)), "// c", "line comment");
        Equal(Join(c.Tokens(4, SyntaxHighlighting.TypePreprocessor)), "", "a CSS '#id' is not a directive");

        TokenCache.Token brace = c.At(1, 11);
        Check(brace != null && brace.Partner != null && brace.Partner.LineNumber == 2, "'{' is paired with '}' on the next line");
        Check(brace.Style != null && brace.Style.OpeningOnNextLine && brace.Style.TrailingBlankLines == 0, "C-like '{' can be folded, also from the line above, without hiding a following blank line");
        Check(c.At(1, 8).Style == null, "'(' is not foldable");
        Highlighted pyBraces = new Highlighted(L.Python, "x = {\n}");
        Check(pyBraces.At(0, 4).Style == null, "Python braces are paired but not folded");

        Highlighted html = new Highlighted(L.Markup, "<p class=\"x\">Don't <!-- note\n--> stop</p>");
        Equal(Join(html.Tokens(0, SyntaxHighlighting.TypeKeyword)), "<p", "tag name");
        Equal(Join(html.Tokens(0, SyntaxHighlighting.TypeString)), "\"x\"", "attribute value; the apostrophe in text is not a string");
        Equal(Join(html.Tokens(1, SyntaxHighlighting.TypeComment)), "-->", "multi-line markup comment");
        Equal(Join(html.Tokens(1, SyntaxHighlighting.TypeKeyword)), "</p", "closing tag");

        // Strings that go on over lines, and quotes that are not strings.
        Highlighted multi = new Highlighted(L.CLike, "const a = `x ${y}\nsecond` + 'z';\nlet t = \"\"\"\nbody \"quote\"\n\"\"\";\nvar v = @\"a\"\"b\nc\";\nx = ['a b', 'c d'];");
        Equal(Join(multi.Tokens(0, SyntaxHighlighting.TypeString)), "`x ${y}", "a template string starts");
        Equal(Join(multi.Tokens(1, SyntaxHighlighting.TypeString)), "second`|'z'", "...goes on, and the code after it is highlighted");
        Equal(Join(multi.Tokens(3, SyntaxHighlighting.TypeString)), "body \"quote\"", "the body of a \"\"\" string is text");
        Equal(Join(multi.Tokens(4, SyntaxHighlighting.TypeString)), "\"\"\"", "the closing \"\"\"");
        Equal(Join(multi.Tokens(5, SyntaxHighlighting.TypeString)), "@\"a\"\"b", "a verbatim string with a doubled quote");
        Equal(Join(multi.Tokens(6, SyntaxHighlighting.TypeString)), "c\"", "...goes on to its closing quote");
        Equal(Join(multi.Tokens(7, SyntaxHighlighting.TypeString)), "'a b'|'c d'", "strings in an array are not lifetimes");
        Highlighted rust = new Highlighted(L.CLike, "fn f<'a>(x: &'a str) -> &'static str { 'outer: loop { break 'outer; } }\nlet c = 'x'; let r = r#\"a \"b\" c\"#;");
        Equal(Join(rust.Tokens(0, SyntaxHighlighting.TypeString)), "", "lifetimes and labels are not strings");
        Equal(Join(rust.Tokens(1, SyntaxHighlighting.TypeString)), "'x'|r#\"a \"b\" c\"#", "a character literal and a raw string");
        Highlighted html2 = new Highlighted(L.Markup, "<p>He said \"hi</p>\n<a href=\"u\"\n   title=\"t\">x</a>\n<!DOCTYPE html>");
        Equal(Join(html2.Tokens(0, SyntaxHighlighting.TypeString)), "", "a quote in the text between tags is not a string");
        Equal(Join(html2.Tokens(1, SyntaxHighlighting.TypeString)), "\"u\"", "an attribute value is");
        Equal(Join(html2.Tokens(2, SyntaxHighlighting.TypeString)), "\"t\"", "also on the next line of the same tag");
        Equal(Join(html2.Tokens(2, SyntaxHighlighting.TypeKeyword)), "</a", "and the tag after it");
        Equal(Join(html2.Tokens(3, SyntaxHighlighting.TypeKeyword)), "<!DOCTYPE", "the doctype");

        Highlighted make = new Highlighted(L.Script, "FROM alpine # base\nURL=http://example.com/a#b");
        Equal(Join(make.Tokens(0, SyntaxHighlighting.TypeKeyword)), "FROM", "Dockerfile instruction");
        Equal(Join(make.Tokens(0, SyntaxHighlighting.TypeComment)), "# base", "'#' comment after whitespace");
        Equal(Join(make.Tokens(1, SyntaxHighlighting.TypeComment)), "", "'#' inside a word is not a comment");

        Highlighted diff = new Highlighted(L.Diff, "--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n+new\n same");
        Equal(diff.TypeAt(3, 0), SyntaxHighlighting.TypeString, "removed line");
        Equal(diff.TypeAt(4, 0), SyntaxHighlighting.TypeComment, "added line");
        Equal(diff.TypeAt(2, 0), SyntaxHighlighting.TypeSection, "hunk header");
        Equal(diff.TypeAt(0, 0), SyntaxHighlighting.TypeKeyword, "file header");
    }

    private static void TokenOrderChecks()
    {
        // Every tokenizer must emit sorted, non-overlapping tokens inside the line, whatever the input.
        string[] samples =
        {
            "", " ", "\t#", "'", "\"", "\\", "$", "${", "$(", "#!", "@", "@x", "/*", "*/", "<!--", "<", "</", "<?", "--", "---",
            "[", "]", "[a", "a = [1, {2: (3)}]", "x = \"unterminated", "y = 'a\\'b' # c", "0x1F + 1e-5 - .5j",
            "echo \"$HOME\" 'x' # y", "key: \"v\" # c", "#include <a.h> // x /* y", "}{)(][", "function f() end", "then do end", "[==[", "--[[", "config a", "ifeq (a,b)", "$(", "a\\", "\"\\", "'''", "\"\"\"x\"\"\""
        };
        foreach (L language in Enum.GetValues(typeof(L)))
        {
            TokenCache.LineTokenizer tokenizer = SyntaxHighlighting.GetTokenizer(language);
            if (tokenizer == null)
            {
                continue;
            }
            foreach (int state in new[] { 0, 1, 2, 4, 0x10, 0x20, 0x110, 0x220 })
            {
                foreach (string sample in samples)
                foreach (bool lastLine in new[] { false, true })
                {
                    RecordingSink sink = new RecordingSink { IsLastLine = lastLine };
                    tokenizer(state, sample, sink, out int _);
                    int end = 0;
                    foreach (Tuple<int, int> token in sink.Tokens)
                    {
                        if (token.Item1 < end || token.Item2 < token.Item1 || token.Item2 > sample.Length)
                        {
                            throw new Exception($"FAIL: {language} state {state} produced a bad token [{token.Item1}, {token.Item2}) for '{sample}'");
                        }
                        end = token.Item2;
                    }
                }
            }
        }
        Check(true, "all tokenizers emit ordered tokens within the line for edge-case input");
    }

    private sealed class RecordingSink : TokenCache.ITokenSink
    {
        public readonly List<Tuple<int, int>> Tokens = new List<Tuple<int, int>>();

        public int CurrentLine => 0;

        public bool IsLastLine { get; set; }

        public void AddToken(int type, int start, int end)
        {
            Tokens.Add(Tuple.Create(start, end));
        }

        public void AddPairedToken(int type, int start, int end, int pairId, bool isOpening, TokenCache.RegionStyle regionStyle)
        {
            Tokens.Add(Tuple.Create(start, end));
        }
    }

    /// <summary>The foldable regions of a document: "line:partnerLine" for every opening token with a fold style.</summary>
    private static string Folds(Highlighted h)
    {
        List<string> folds = new List<string>();
        for (int line = 0; line < h.Doc.LineCount; line++)
        {
            foreach (TokenCache.Token token in h.Cache.GetLineTokens(line))
            {
                if (token.Kind == TokenCache.Token.TokenKind.Paired && token.IsOpening && token.Style != null &&
                    token.Partner != null && token.Partner.LineNumber != line)
                {
                    int partnerLine = token.Partner.LineNumber;
                    if (token.Style.EndsBeforeClosingLine)
                    {
                        // Mirrors FoldingManager: the closing line and the blank lines before it stay visible.
                        int hidden = partnerLine - line - 1;
                        for (int blank = partnerLine - 1; blank > line && hidden > 0 && h.Doc.GetLineText(blank).Trim().Length == 0; blank--)
                        {
                            hidden--;
                        }
                        if (hidden <= 0)
                        {
                            continue;
                        }
                    }
                    folds.Add(line + ":" + partnerLine);
                }
            }
        }
        return string.Join(",", folds);
    }

    private static void OpenWrtDetectionChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/config/network"), L.Uci, "/etc/config files are UCI");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/config/firewall", "config defaults"), L.Uci, "the path wins over the content");
        Equal(SyntaxHighlighting.DetectLanguage("/tmp/network.uci"), L.Uci, ".uci is UCI");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/init.d/dropbear"), L.Bash, "init scripts are shell scripts");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/hotplug.d/iface/20-ntp"), L.Bash, "hotplug handlers are shell scripts");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/uci-defaults/90-setup"), L.Bash, "uci-defaults are shell scripts");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/init.d/boot", "#!/bin/sh /etc/rc.common"), L.Bash, "#!/bin/sh /etc/rc.common is shell");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/profile"), L.Bash, "/etc/profile is shell");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/lib/lua/luci/model/cbi/x.lua"), L.Lua, ".lua is Lua");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/bin/tool", "#!/usr/bin/lua"), L.Lua, "#!/usr/bin/lua is Lua");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/bin/tool", "#!/usr/bin/env luajit"), L.Lua, "env luajit is Lua");
        Equal(SyntaxHighlighting.DetectLanguage("/build/package/foo/Makefile"), L.Makefile, "Makefile");
        Equal(SyntaxHighlighting.DetectLanguage("/build/rules.mk"), L.Makefile, ".mk is a Makefile");
        Equal(SyntaxHighlighting.DetectLanguage("/build/tool", "#!/usr/bin/make -f"), L.Makefile, "#!/usr/bin/make is a Makefile");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/board.json"), L.Json, ".json is JSON");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/tsconfig.jsonc"), L.Json, ".jsonc is JSON");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/config.d/x"), L.Plain, "only /etc/config/ is UCI");
        foreach (L language in Enum.GetValues(typeof(L)))
        {
            Check(Array.IndexOf(SyntaxHighlighting.SelectableLanguages, language) >= 0, language + " is offered in the syntax menu");
            Check(SyntaxHighlighting.GetDisplayName(language) != language.ToString() || language == L.Lua || language == L.Json || language == L.Makefile || language == L.Python || language == L.Sql || language == L.Diff || language == L.Plain, language + " has a display name");
        }
    }

    private static void FoldChecks()
    {
        // Bash: then...fi, do...done, case...esac, function bodies.
        Highlighted sh = new Highlighted(L.Bash,
            "#!/bin/sh\nstart() {\n  if [ -f /x ]; then\n    for i in 1 2; do\n      echo $i\n    done\n  elif [ -d /y ]; then\n    case $a in\n      a) echo a ;;\n      *) echo b ;;\n    esac\n  else\n    echo no\n  fi\n}");
        Equal(Folds(sh), "1:14,2:13,3:5,7:10", "bash functions, if, for and case blocks fold (elif does not open a block)");
        TokenCache.Token cond = sh.Cache.GetLineTokens(2).First(t => t.Kind == TokenCache.Token.TokenKind.Paired && t.IsOpening && t.Style != null);
        Equal(sh.Doc.GetLineText(2).Substring(cond.Start, cond.End - cond.Start), "then", "an if block folds at its then");
        Highlighted shLine = new Highlighted(L.Bash, "if [ -z \"$x\" ]; then echo a; fi\nfind . -exec ls {} \\;\necho a{1,2}b\nx=${y:-z}");
        Equal(Folds(shLine), "", "one-line blocks, {} and ${} do not fold");
        Equal(shLine.At(0, 3).Partner?.Start, 13, "the [ ] of a test are paired");
        Highlighted shCase = new Highlighted(L.Bash, "f() {\n  case $1 in\n    a) x ;;\n    b) y ;;\n  esac\n}");
        Equal(Folds(shCase), "0:5,1:4", "case patterns ')' do not break the surrounding blocks");

        // Lua.
        Highlighted lua = new Highlighted(L.Lua,
            "local M = {}\nfunction M.run(a)\n  if a then\n    return 1\n  elseif a == 2 then\n    return 2\n  end\n  for k, v in pairs(M) do\n    print(k)\n  end\n  repeat\n    a = a - 1\n  until a < 0\nend\nlocal t = {\n  x = 1,\n}\nlocal f = function(x) return x end\nlocal g = function(x)\n  return x -- c\nend");
        Equal(Folds(lua), "1:13,2:6,7:9,10:12,14:16,18:20", "Lua function/if/for/repeat/table blocks fold; elseif and one-line function do not");
        Highlighted luaStr = new Highlighted(L.Lua, "local s = [==[\nfunction end\n]==]\n--[[ end\nthen ]] local x = 1 -- end");
        Equal(Folds(luaStr), "", "keywords inside long strings and comments do not open blocks");
        Equal(luaStr.TypeAt(1, 0), SyntaxHighlighting.TypeString, "long string continues on the next line");
        Equal(luaStr.TypeAt(4, 0), SyntaxHighlighting.TypeComment, "long comment continues on the next line");
        Equal(luaStr.TypeAt(4, 8), SyntaxHighlighting.TypeKeyword, "code after a long comment is highlighted");
        Equal(Join(new Highlighted(L.Lua, "x = 0x1F + 1e-3 .. 'a' -- c").Tokens(0, SyntaxHighlighting.TypeNumber)), "0x1F|1e-3", "Lua numbers");

        // JSON.
        Highlighted json = new Highlighted(L.Json, "{\n  \"a\": [\n    1, 2.5e3, true, null\n  ],\n  \"b\": {\"c\": \"x\"} // note\n}");
        Equal(Folds(json), "0:5,1:3", "JSON objects and arrays fold");
        Equal(Join(json.Tokens(1, SyntaxHighlighting.TypeVariable)), "\"a\"", "JSON keys");
        Equal(Join(json.Tokens(4, SyntaxHighlighting.TypeString)), "\"x\"", "JSON string values");
        Equal(Join(json.Tokens(2, SyntaxHighlighting.TypeNumber)), "1|2.5e3", "JSON numbers");
        Equal(Join(json.Tokens(2, SyntaxHighlighting.TypeKeyword)), "true|null", "JSON constants");
        Equal(Join(json.Tokens(4, SyntaxHighlighting.TypeComment)), "// note", "JSON comments are tolerated");

        // Makefile.
        Highlighted mk = new Highlighted(L.Makefile,
            "include $(TOPDIR)/rules.mk\nPKG_NAME:=foo # name\nifeq ($(CONFIG_X),y)\n  EXTRA=1\nelse\n  EXTRA=0\nendif\ndefine Package/foo/install\n\t$(INSTALL_BIN) $(PKG_BUILD_DIR)/foo $(1)/bin/\nendef\nall: build\n\t@echo done $@ # c");
        Equal(Folds(mk), "2:6,7:9", "Makefile ifeq and define blocks fold");
        Equal(Join(mk.Tokens(0, SyntaxHighlighting.TypeKeyword)), "include", "include is a directive");
        Equal(Join(mk.Tokens(1, SyntaxHighlighting.TypeVariable)), "PKG_NAME", "variable assignment");
        Equal(Join(mk.Tokens(1, SyntaxHighlighting.TypeComment)), "# name", "trailing comment");
        Equal(Join(mk.Tokens(8, SyntaxHighlighting.TypeVariable)), "$(INSTALL_BIN)|$(PKG_BUILD_DIR)|$(1)", "make references in recipes");
        Equal(Join(mk.Tokens(10, SyntaxHighlighting.TypeSection)), "all", "rule target");
        Equal(Join(mk.Tokens(11, SyntaxHighlighting.TypeVariable)), "$@", "automatic variable");

        // Makefile rules end at the first blank line or the next line that is not a recipe.
        Highlighted rules = new Highlighted(L.Makefile, "all: build\n\t@echo a\n\t@echo b\nbuild:\n\t@true\n\ndefine X\nfoo: bar\n\tbaz\nendef\nlast:\n\tone\n\ttwo\n");
        Equal(Folds(rules), "0:3,3:5,6:9,10:13", "Makefile rules fold up to the next rule, a blank line or the end of the document");
        Highlighted rulesNoNewline = new Highlighted(L.Makefile, "a:\n\tone\n\ttwo");
        Equal(Folds(rulesNoNewline), "0:2", "a rule at the end of a file without a trailing newline keeps its last line visible");

        // UCI.
        Highlighted uci = new Highlighted(L.Uci,
            "package network\n\nconfig interface 'lan'\n\toption proto 'static'\n\toption mtu 1500\n\tlist dns '8.8.8.8' # google\n\nconfig device\n\toption name 'br-lan'\n\nconfig interface 'wan'\n\toption proto 'dhcp'");
        Equal(Folds(uci), "2:6,7:9", "UCI config blocks fold up to the blank line");
        Equal(Join(uci.Tokens(2, SyntaxHighlighting.TypeKeyword)), "config", "config keyword");
        Equal(Join(uci.Tokens(2, SyntaxHighlighting.TypeSection)), "interface", "config type");
        Equal(Join(uci.Tokens(2, SyntaxHighlighting.TypeString)), "'lan'", "config name");
        Equal(Join(uci.Tokens(3, SyntaxHighlighting.TypeVariable)), "proto", "option name");
        Equal(Join(uci.Tokens(4, SyntaxHighlighting.TypeNumber)), "1500", "numeric option value");
        Equal(Join(uci.Tokens(5, SyntaxHighlighting.TypeComment)), "# google", "comment after a value");
        Highlighted uciTight = new Highlighted(L.Uci, "config a\n\toption x '1'\n\toption y '2'\nconfig b\n\toption z '3'\n\toption w '4'\n");
        Equal(Folds(uciTight), "0:3,3:6", "UCI blocks without blank lines fold up to the next config and to the end of the document");
        Highlighted uciOpen = new Highlighted(L.Uci, "config a\n\toption x '1'\n\toption y '2'");
        Equal(Folds(uciOpen), "0:2", "the last line of the document stays visible");
        uciOpen.Doc.InsertText(new TextDocument.TextPosition(uciOpen.Doc.GetLineText(2).Length, 2), "\n", inheritLineEnding: true);
        uciOpen.Cache.OnLinesChanged(2, 0, 1, uciOpen.Tokenizer);
        Equal(Folds(uciOpen), "0:3", "a new last line ends the block again after an edit");

        // Python: blocks by indentation.
        Highlighted py = new Highlighted(L.Python,
            "import os\n\nclass A:\n    x = 1\n\n    def f(self, a,\n          b):\n        if a:\n            return {\n  'k': 1,\n  }\n        else:\n            pass  # c\n\n    # about g\n    def g(self):\n        s = \"\"\"\n  doc:\n\"\"\"\n        return s\n\n\ndef h():\n    return 1\n");
        Equal(Folds(py), "2:22,6:14,7:11,11:14,15:22,22:24", "Python blocks fold by indentation (multi-line signatures, strings and brackets do not end them)");
        Highlighted pyOne = new Highlighted(L.Python, "if x: y = 1\nz = {\n 'a':\n 1}\ndef f():\n  pass");
        Equal(Folds(pyOne), "", "one-line statements and dictionaries do not fold");

        // Editing keeps the blocks: Enter inside a body, then a body line that loses its indentation.
        Highlighted pyEdit = new Highlighted(L.Python, "def f():\n    a = 1\n    b = 2\n\nx = 3\n");
        Equal(Folds(pyEdit), "0:4", "a function body folds up to the next statement");
        pyEdit.Doc.InsertText(new TextDocument.TextPosition(pyEdit.Doc.GetLineText(1).Length, 1), "\n    c = 3", inheritLineEnding: true);
        pyEdit.Cache.OnLinesChanged(1, 0, 1, pyEdit.Tokenizer);
        Equal(Folds(pyEdit), "0:5", "Enter in the body of a function extends the block");
        pyEdit.Doc.ReplaceInLine(3, 0, 4, "");
        pyEdit.Cache.RetokenizeLine(3, pyEdit.Tokenizer);
        Equal(Folds(pyEdit), "0:3", "a line without indentation ends the block");

        // YAML: blocks by indentation.
        Highlighted yaml = new Highlighted(L.Yaml,
            "---\nname: web   # note\nitems:\n- a: 1\n  b: 2\n- a: 3\nservices:\n  app:\n    image: x\n    env: |\n      A=1\n      B=2\n    ports:\n      - 80\nlast: 1\n");
        Equal(Folds(yaml), "2:6,3:5,6:14,7:14,9:12,12:14", "YAML mappings, lists at the indentation of their key and block scalars fold");
        Equal(Join(yaml.Tokens(1, SyntaxHighlighting.TypeVariable)), "name", "YAML keys keep the config colors");
        Equal(Join(yaml.Tokens(1, SyntaxHighlighting.TypeComment)), "# note", "YAML comments keep the config colors");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/docker-compose.yml"), L.Yaml, ".yml is YAML");

        // The text of a block scalar is not YAML.
        Highlighted scalar = new Highlighted(L.Yaml, "run: |\n  echo \"don't # stop\"\n  x: y\n\n  more\nnext: 'v'\n");
        Equal(Join(scalar.Tokens(1, SyntaxHighlighting.TypeString)), "echo \"don't # stop\"", "a line of a block scalar is text");
        Equal(Join(scalar.Tokens(1, SyntaxHighlighting.TypeComment)), "", "'#' in a block scalar is not a comment");
        Equal(Join(scalar.Tokens(2, SyntaxHighlighting.TypeVariable)), "", "'x: y' in a block scalar is not a key");
        Equal(Join(scalar.Tokens(4, SyntaxHighlighting.TypeString)), "more", "a blank line does not end the block scalar");
        Equal(Join(scalar.Tokens(5, SyntaxHighlighting.TypeVariable)), "next", "a line that is not indented deeper ends it");
        Equal(Join(scalar.Tokens(5, SyntaxHighlighting.TypeString)), "'v'", "and is YAML again");
    }

    private static void PairEditingChecks()
    {
        // Brackets stay paired while lines are edited, inserted and removed.
        Highlighted c = new Highlighted(L.CLike, "void f()\n{\n    if (x) {\n        g();\n    }\n}");
        TokenCache.Token outer = c.At(1, 0);
        Check(outer.Partner != null && outer.Partner.LineNumber == 5, "the outer braces are paired");

        // Insert a line with an extra block inside the function.
        c.Doc.InsertText(new TextDocument.TextPosition(c.Doc.GetLineText(3).Length, 3), "\n        while (y) { h(); }", inheritLineEnding: true);
        c.Cache.OnLinesChanged(3, 0, 1, c.Tokenizer);
        outer = c.At(1, 0);
        Check(outer.Partner != null && outer.Partner.LineNumber == 6, "pairs follow an inserted line");
        TokenCache.Token loop = c.At(4, 18);
        Check(loop != null && loop.Partner != null && loop.Partner.LineNumber == 4, "the new block is paired on its line");

        // Break the inner block by deleting its closing brace, then restore it.
        c.Doc.ReplaceInLine(5, 4, 1, "");
        c.Cache.RetokenizeLine(5, c.Tokenizer);
        TokenCache.Token inner = c.At(2, 11);
        Check(inner != null && inner.Kind == TokenCache.Token.TokenKind.Paired, "the inner '{' is still a bracket token");
        c.Doc.ReplaceInLine(5, 4, 0, "}");
        c.Cache.RetokenizeLine(5, c.Tokenizer);
        inner = c.At(2, 11);
        Check(inner.Partner != null && inner.Partner.LineNumber == 5, "the inner braces are paired again after the edit is undone");

        // Remove the inserted line again.
        DeleteRange(c.Doc, new TextDocument.TextPosition(c.Doc.GetLineText(3).Length, 3), new TextDocument.TextPosition(c.Doc.GetLineText(4).Length, 4));
        c.Cache.OnLinesChanged(3, 1, 0, c.Tokenizer);
        outer = c.At(1, 0);
        Check(outer.Partner != null && outer.Partner.LineNumber == 5, "pairs follow a removed line");

        // A multi-line comment opened above hides the brackets below, closing it brings them back.
        c.Doc.ReplaceInLine(2, 0, 0, "/*");
        c.Cache.RetokenizeLine(2, c.Tokenizer);
        Check(c.Cache.GetLineTokens(3).All(t => t.Type == SyntaxHighlighting.TypeComment), "lines below an unclosed /* are comments");
        c.Doc.ReplaceInLine(2, 0, 2, "");
        c.Cache.RetokenizeLine(2, c.Tokenizer);
        outer = c.At(1, 0);
        Check(c.Cache.GetLineTokens(3).Any(t => t.Kind == TokenCache.Token.TokenKind.Paired) && outer.Partner != null && outer.Partner.LineNumber == 5, "removing the /* restores code and pairs");
    }

    private static void ReplaceChecks()
    {
        int count = 0;
        Equal(TextDocument.ReplaceAll("foo bar foo", "foo", "baz", StringComparison.Ordinal, ref count), "baz bar baz", "every occurrence is replaced");
        Equal(count, 2, "the replacements are counted");
        count = 0;
        Equal(TextDocument.ReplaceAll("Foo FOO foo", "foo", "x", StringComparison.OrdinalIgnoreCase, ref count), "x x x", "case-insensitive replace");
        Equal(count, 3, "case-insensitive count");
        count = 0;
        Equal(TextDocument.ReplaceAll("aaaa", "aa", "a", StringComparison.Ordinal, ref count), "aa", "matches do not overlap and replaced text is not searched again");
        count = 0;
        Equal(TextDocument.ReplaceAll("ab", "b", "bb", StringComparison.Ordinal, ref count), "abb", "a replacement containing the searched text does not loop");
        count = 0;
        Equal(TextDocument.ReplaceAll("abc", "", "x", StringComparison.Ordinal, ref count), "abc", "an empty search text replaces nothing");
        Equal(count, 0, "nothing counted for an empty search text");
    }

    // The "show a line under each tab character" setting of the editor is wired end to end. The ReadonlyTextBrowser
    // that paints the tabs and the settings window are Windows-only, so they are checked by reading the sources, the
    // same way the theme harness checks the settings window XAML.
    private static void ShowTabsChecks()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            Console.WriteLine("SKIP: repository root not found, the ShowTabs wiring is not checked");
            return;
        }
        string browser = File.ReadAllText(Path.Combine(root, "Controls", "ReadonlyTextBrowser.cs"));
        Check(browser.Contains("public bool ShowTabs") && browser.Contains("_showTabs = value"),
            "the terminal/editor paints a line under tabs through a ShowTabs property");
        string settings = File.ReadAllText(Path.Combine(root, "VisualTTY", "GlobalSettings.cs"));
        Check(settings.Contains("public bool ShowTabs;"), "the editor settings carry the ShowTabs value");
        string wrapper = File.ReadAllText(Path.Combine(root, "VisualTTY", "EditorSettingsWrapper.cs"));
        Check(wrapper.Contains("public bool ShowTabs"), "the settings wrapper exposes ShowTabs to the window");
        string controller = File.ReadAllText(Path.Combine(root, "GUI", "Backend", "MainWindowController.cs"));
        Check(controller.Contains("edit.ShowTabs = Settings.Editor.ShowTabs"), "opening the editor applies the ShowTabs setting");
        string xaml = File.ReadAllText(Path.Combine(root, "GUI", "GlobalSettingsWindow.xaml"));
        Check(xaml.Contains("EditorWrapper.ShowTabs"), "Editor Settings offers a checkbox for ShowTabs");
    }

    // "Show all characters" is a property of the editor, a toggle button on the WPF tool bar and a checkable item in
    // the WinForms context menu, backed by a setting. The controls are Windows-only, so the wiring is checked by
    // reading the sources, as the ShowTabs checks above do.
    private static void ShowAllCharactersChecks()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            Console.WriteLine("SKIP: repository root not found, the Show All Characters wiring is not checked");
            return;
        }
        string browser = File.ReadAllText(Path.Combine(root, "Controls", "ReadonlyTextBrowser.cs"));
        Check(browser.Contains("public bool ShowAllCharacters") && browser.Contains("_showAllCharacters = value"),
            "the editor draws the invisible-character marks through a ShowAllCharacters property");
        Check(browser.Contains("PaintInvisibleCharacters") && browser.Contains("\"¶\"") && browser.Contains("0x2400 + ch"),
            "the overlay marks spaces, tabs, line ends and control characters");
        string commands = File.ReadAllText(Path.Combine(root, "Controls", "EditorCommandState.cs"));
        Check(commands.Contains("ShowAllCharacters") && commands.Contains("public bool IsChecked"),
            "the tool bar and the menu have a ShowAllCharacters command with a checked state");
        Check(commands.Contains("new EditorCommandDescription(\"Show All Characters\""),
            "the command catalog names Show All Characters");
        string toolbar = File.ReadAllText(Path.Combine(root, "GUI", "Backend", "RemoteFileEditorToolBar.cs"));
        Check(toolbar.Contains("new ToggleButton") && toolbar.Contains("ToolBar.ToggleButtonStyleKey") && toolbar.Contains("state.IsChecked"),
            "the WPF tool bar shows Show All Characters as a toggle button");
        string panel = File.ReadAllText(Path.Combine(root, "Controls", "RemoteFileEditorPanel.cs"));
        Check(panel.Contains("_editor.ShowAllCharacters = !_editor.ShowAllCharacters")
            && panel.Contains("ShowAllCharacters = _editor.ShowAllCharacters")
            && panel.Contains("pair.Value.Checked = CommandState.IsChecked"),
            "the panel toggles the marks, captures the state and checks the menu item");
        string settings = File.ReadAllText(Path.Combine(root, "VisualTTY", "GlobalSettings.cs"));
        Check(settings.Contains("public bool ShowAllCharacters;"), "the editor settings carry the ShowAllCharacters value");
        string wrapper = File.ReadAllText(Path.Combine(root, "VisualTTY", "EditorSettingsWrapper.cs"));
        Check(wrapper.Contains("public bool ShowAllCharacters") && wrapper.Contains("_Settings.Editor.ShowAllCharacters = value"),
            "the settings wrapper exposes ShowAllCharacters to the window");
        string controller = File.ReadAllText(Path.Combine(root, "GUI", "Backend", "MainWindowController.cs"));
        Check(controller.Contains("edit.ShowAllCharacters = Settings.Editor.ShowAllCharacters"),
            "opening the editor applies the ShowAllCharacters setting");
        Check(controller.Contains("Settings.Editor.ShowAllCharacters = editor.ShowAllCharacters"),
            "the tool bar toggle saves the ShowAllCharacters setting");
        string xaml = File.ReadAllText(Path.Combine(root, "GUI", "GlobalSettingsWindow.xaml"));
        Check(xaml.Contains("EditorWrapper.ShowAllCharacters"), "Editor Settings offers a checkbox for ShowAllCharacters");
        Check(File.Exists(Path.Combine(root, "icons", "editor", "show-all-characters.png")),
            "the Show All Characters tool bar icon exists");
    }

    private static string FindRepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "SmarTTY.csproj")))
        {
            root = Path.GetDirectoryName(root);
        }
        return root;
    }

    private static string Join(string[] lines)
    {
        return string.Join("|", lines);
    }

    private static void CommentChecks()
    {
        CommentStyle hash = CommentStyle.ForLanguage(SyntaxHighlighting.Language.Bash);
        Equal(Join(hash.Toggle(new[] { "echo a", "echo b" })), "# echo a|# echo b", "lines are commented out");
        Equal(Join(hash.Toggle(new[] { "# echo a", "# echo b" })), "echo a|echo b", "commented lines are uncommented");
        Equal(Join(hash.Toggle(new[] { "# echo a", "echo b" })), "# # echo a|# echo b", "a mixed block is commented as a whole");
        Equal(Join(hash.Toggle(new[] { "    a", "  b", "" })), "  #   a|  # b|", "the mark goes to the smallest indent, blank lines stay");
        Equal(Join(hash.Toggle(new[] { "  # a", "", "  # b" })), "  a||  b", "blank lines do not stop the uncomment");
        Equal(Join(hash.Toggle(new[] { "#a" })), "a", "no space after the mark");
        Equal(Join(hash.Toggle(new[] { "" })), "# ", "a blank line alone is commented");
        CommentStyle markup = CommentStyle.ForLanguage(SyntaxHighlighting.Language.Markup);
        Equal(Join(markup.Toggle(new[] { "<a/>" })), "<!-- <a/> -->", "markup uses a block comment per line");
        Equal(Join(markup.Toggle(new[] { "<!-- <a/> -->" })), "<a/>", "markup comment is removed");
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.CLike).Toggle(new[] { "int a;" })), "// int a;", "C-like comment");
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.Cpp).Toggle(new[] { "int a;" })), "// int a;", "C++ comment");
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.Sql).Toggle(new[] { "select 1" })), "-- select 1", "SQL comment");
        Check(CommentStyle.ForLanguage(SyntaxHighlighting.Language.Plain) == null, "plain text has no comments");
        Check(CommentStyle.ForLanguage(SyntaxHighlighting.Language.Diff) == null, "a diff has no comments");
    }

    private static void WrapChecks()
    {
        Check(LineWrapper.ComputeBreaks("short", 10, 4) == null, "a short line takes one row");
        Equal(string.Join(",", LineWrapper.ComputeBreaks("aaaa bbbb cccc", 10, 4)), "10", "the row breaks after the last space that fits");
        Equal(string.Join(",", LineWrapper.ComputeBreaks("aaaaaaaaaaaaaaaaaaaaaaaaa", 10, 4)), "10,20", "a long word breaks where the row ends");
        Equal(string.Join(",", LineWrapper.ComputeBreaks("ab\tcd", 4, 4)), "4", "a tab counts to the next tab stop");
        Equal(string.Join(",", LineWrapper.ComputeBreaks("aa bb cc dd ee", 6, 4)), "6,12", "every row is filled up to the width");
    }

    private static int Main()
    {
        try
        {
            LineEndingChecks();
            ReplaceChecks();
            LanguageDetectionChecks();
            ConfigChecks();
            BashChecks();
            OpenWrtDetectionChecks();
            FoldChecks();
            PythonChecks();
            CppChecks();
            GenericChecks();
            TokenOrderChecks();
            PairEditingChecks();
            CommentChecks();
            WrapChecks();
            ShowTabsChecks();
            ShowAllCharactersChecks();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            Console.WriteLine(exception.StackTrace);
            return 1;
        }
        Console.WriteLine($"All {_passed} checks passed.");
        return 0;
    }
}
