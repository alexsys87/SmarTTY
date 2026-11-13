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

        // A file with mixed endings: the join keeps the ending of the second line, and the undo puts every ending back where it was.
        const string mixed = "ab\r\ncd\nef\r\n";
        EditableTextDocument mix = Document(mixed);
        TextDocument.TextPosition mixStart = new TextDocument.TextPosition(1, 0);
        TextDocument.TextPosition mixEnd = new TextDocument.TextPosition(1, 1);
        string mixRemoved = mix.GetRangeText(mixStart, mixEnd, mix.GetEffectiveLineTerminator);
        Equal(mixRemoved, "b\r\nc", "the removed text keeps the ending of the line break it contains");
        DeleteRange(mix, mixStart, mixEnd);
        Equal(mix.GetText(), "ad\nef\r\n", "the joined line ends like the second line did");
        mix.InsertText(mixStart, mixRemoved);
        Equal(mix.GetText(), mixed, "re-inserting the removed text restores every ending of a mixed file");

        // The tabs of a line are counted again when it is edited (the width of the longest line depends on it).
        EditableTextDocument tabs = Document("a");
        Equal(tabs.GetMaxLineWidth(8), 1, "a line without tabs is as wide as it is long");
        tabs.ReplaceInLine(0, 1, 0, "\t\t");
        Equal(tabs.GetMaxLineWidth(8), 17, "tabs typed into a line widen it");
        tabs.ReplaceInLine(0, 1, 2, "");
        Equal(tabs.GetMaxLineWidth(8), 1, "and the width goes back when they are deleted");

        EditableTextDocument crlfUndo = Document("a\r\nb\r\nc");
        string crlfRemoved = crlfUndo.GetRangeText(new TextDocument.TextPosition(0, 0), new TextDocument.TextPosition(0, 2), crlfUndo.GetEffectiveLineTerminator);
        Equal(crlfRemoved, "a\r\nb\r\n", "CRLF terminators are kept as well");
    }

    private static void LanguageDetectionChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/Main.PY"), L.Python, "extensions are case insensitive");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/Dockerfile"), L.Dockerfile, "Dockerfile (capitalized) is recognized");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/Makefile"), L.Makefile, "Makefile (capitalized) is recognized");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/.bashrc"), L.Bash, ".bashrc is bash");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/ssh/sshd_config"), L.SshConfig, "sshd_config is an ssh config file");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/systemd/system/app.service"), L.Systemd, "systemd units are systemd files");
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

        (string Path, L Language)[] cLikeExtensions =
        {
            ("/src/Program.cs", L.CSharp), ("/src/Main.java", L.Java), ("/src/Main.kt", L.Kotlin),
            ("/src/build.kts", L.Kotlin), ("/src/Main.scala", L.Scala), ("/src/main.sc", L.Scala),
            ("/src/build.groovy", L.Groovy), ("/src/build.gradle", L.Groovy), ("/src/main.go", L.Go),
            ("/src/main.rs", L.Rust), ("/src/Main.swift", L.Swift), ("/src/main.dart", L.Dart),
            ("/src/app.js", L.JavaScript), ("/src/app.mjs", L.JavaScript), ("/src/app.cjs", L.JavaScript),
            ("/src/App.jsx", L.JavaScript), ("/src/app.ts", L.TypeScript), ("/src/App.tsx", L.TypeScript),
            ("/src/app.mts", L.TypeScript), ("/src/app.cts", L.TypeScript), ("/src/index.php", L.Php),
            ("/src/page.phtml", L.Php), ("/src/site.css", L.Css), ("/src/site.scss", L.Css),
            ("/src/site.sass", L.Css), ("/src/site.less", L.Css), ("/src/message.proto", L.Protobuf),
            ("/src/board.dts", L.DeviceTree), ("/src/board.dtsi", L.DeviceTree)
        };
        foreach ((string path, L language) in cLikeExtensions)
        {
            Equal(SyntaxHighlighting.DetectLanguage(path), language,
                path + " is " + SyntaxHighlighting.GetDisplayName(language));
        }

        (L Language, string Name)[] cLikeNames =
        {
            (L.CSharp, "C#"), (L.Java, "Java"), (L.Kotlin, "Kotlin"), (L.Scala, "Scala"),
            (L.Groovy, "Groovy"), (L.Go, "Go"), (L.Rust, "Rust"), (L.Swift, "Swift"), (L.Dart, "Dart"),
            (L.JavaScript, "JavaScript"), (L.TypeScript, "TypeScript"), (L.Php, "PHP"),
            (L.Css, "CSS, SCSS, Less"), (L.Protobuf, "Protocol Buffers"), (L.DeviceTree, "Device Tree")
        };
        foreach ((L language, string name) in cLikeNames)
        {
            Equal(SyntaxHighlighting.GetDisplayName(language), name, name + " has its own display name");
            Check(SyntaxHighlighting.SelectableLanguages.Contains(language), name + " is in the syntax menu");
        }

        (L Language, string Keyword)[] cLikeKeywords =
        {
            (L.CSharp, "namespace"), (L.Java, "synchronized"), (L.Kotlin, "fun"), (L.Scala, "implicit"),
            (L.Groovy, "def"), (L.Go, "defer"), (L.Rust, "impl"), (L.Swift, "guard"), (L.Dart, "mixin"),
            (L.JavaScript, "function"), (L.TypeScript, "interface"), (L.Php, "echo"),
            (L.Css, "display"), (L.Protobuf, "message"), (L.DeviceTree, "compatible")
        };
        foreach ((L language, string keyword) in cLikeKeywords)
        {
            Check(SyntaxHighlighting.GetKeywords(language).Contains(keyword),
                SyntaxHighlighting.GetDisplayName(language) + " keywords contain '" + keyword + "'");
        }
        Check(SyntaxHighlighting.GetKeywords(L.CSharp).Contains("region"), "C# keywords contain the 'region' directive");
        Check(!SyntaxHighlighting.GetKeywords(L.CSharp).Contains("include"), "C# keywords do not contain the C 'include' directive");
        Check(SyntaxHighlighting.GetKeywords(L.DeviceTree).Contains("dts-v1"), "Device Tree keywords contain '/dts-v1/'");
        Check(SyntaxHighlighting.GetKeywords(L.DeviceTree).Contains("delete-node"), "Device Tree keywords contain '/delete-node/'");
        Check(SyntaxHighlighting.GetKeywords(L.DeviceTree).Contains("gpio-controller"), "Device Tree keywords contain 'gpio-controller'");
        Check(SyntaxHighlighting.GetKeywords(L.DeviceTree).Contains("pinctrl-names"), "Device Tree keywords contain 'pinctrl-names'");
        Check(SyntaxHighlighting.GetKeywords(L.DeviceTree).Contains("address-cells"), "Device Tree keywords contain '#address-cells'");
        Check(!SyntaxHighlighting.GetKeywords(L.CLike).Contains("interface"), "C keywords do not include the C# 'interface'");
        Check(!SyntaxHighlighting.GetKeywords(L.CLike).Contains("fun"), "C keywords do not include the Kotlin 'fun'");
        Check(!SyntaxHighlighting.GetKeywords(L.CLike).Contains("impl"), "C keywords do not include the Rust 'impl'");
        Check(!SyntaxHighlighting.GetKeywords(L.CLike).Contains("message"), "C keywords do not include the Protobuf 'message'");
        foreach (L language in new[]
                 {
                     L.Java, L.Kotlin, L.Scala, L.Groovy, L.Go, L.Rust, L.Swift, L.Dart, L.JavaScript,
                     L.TypeScript, L.Php, L.Css, L.Protobuf, L.DeviceTree
                 })
        {
            Check(!SyntaxHighlighting.GetKeywords(language).Contains("sizeof"),
                SyntaxHighlighting.GetDisplayName(language) + " does not see the C 'sizeof'");
        }

        Equal(SyntaxHighlighting.GetDisplayName(L.Cpp), "C++", "C++ has its own display name");
        Check(SyntaxHighlighting.SelectableLanguages.Contains(L.Cpp), "C++ is in the syntax menu");
        Check(SyntaxHighlighting.GetKeywords(L.Cpp).Contains("constexpr"), "C++ keywords contain 'constexpr'");
        Check(SyntaxHighlighting.GetKeywords(L.Cpp).Contains("nullptr"), "C++ keywords contain 'nullptr'");
        Check(!SyntaxHighlighting.GetKeywords(L.Cpp).Contains("interface"), "C++ keywords do not include the C# 'interface'");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/Dockerfile.dev"), L.Dockerfile, "Dockerfile.dev is a Dockerfile");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/app.rb"), L.Ruby, ".rb is Ruby");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/Gemfile"), L.Ruby, "Gemfile is Ruby");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/script.pl"), L.Perl, ".pl is Perl");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/Module.pm"), L.Perl, ".pm is Perl");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/CMakeLists.txt"), L.CMake, "CMakeLists.txt is CMake");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/tools.cmake"), L.CMake, ".cmake is CMake");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/job.r"), L.Script, "R stays in the generic script profile");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/run.awk"), L.Awk, ".awk is AWK");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/default.nix"), L.Nix, ".nix is Nix");
        Equal(SyntaxHighlighting.GetDisplayName(L.Awk), "AWK", "AWK has its own display name");
        Check(SyntaxHighlighting.SelectableLanguages.Contains(L.Awk), "AWK is in the syntax menu");
        Check(SyntaxHighlighting.GetKeywords(L.Awk).Contains("BEGIN"), "AWK keywords contain 'BEGIN'");
        Check(SyntaxHighlighting.GetKeywords(L.Awk).Contains("print"), "AWK keywords contain 'print'");
        Check(SyntaxHighlighting.GetKeywords(L.Awk).Contains("getline"), "AWK keywords contain 'getline'");
        Check(!SyntaxHighlighting.GetKeywords(L.Awk).Contains("sizeof"), "AWK keywords do not include the C 'sizeof'");
        Check(!SyntaxHighlighting.GetKeywords(L.Script).Contains("BEGIN"), "the generic Script profile no longer carries the AWK 'BEGIN'");
        Check(!SyntaxHighlighting.GetKeywords(L.Script).Contains("getline"), "the generic Script profile no longer carries the AWK 'getline'");
        Equal(SyntaxHighlighting.GetDisplayName(L.Script), "Script (R)", "the generic Script profile now covers only R");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/deploy.tcl"), L.Tcl, ".tcl is Tcl");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/panel.tk"), L.Tcl, ".tk is Tcl");
        Equal(SyntaxHighlighting.GetDisplayName(L.Tcl), "Tcl", "Tcl has its own display name");
        Check(SyntaxHighlighting.SelectableLanguages.Contains(L.Tcl), "Tcl is in the syntax menu");
        Check(SyntaxHighlighting.GetKeywords(L.Tcl).Contains("proc"), "Tcl keywords contain 'proc'");
        Check(SyntaxHighlighting.GetKeywords(L.Tcl).Contains("namespace"), "Tcl keywords contain 'namespace'");
        Check(SyntaxHighlighting.GetKeywords(L.Tcl).Contains("puts"), "Tcl keywords contain 'puts'");
        Check(!SyntaxHighlighting.GetKeywords(L.Tcl).Contains("sizeof"), "Tcl keywords do not include the C 'sizeof'");
        Check(!SyntaxHighlighting.GetKeywords(L.Script).Contains("proc"), "the generic Script profile no longer carries the Tcl 'proc'");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/start.asm"), L.Assembly, ".asm is Assembly");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/crt0.s"), L.Assembly, ".s is Assembly");
        Equal(SyntaxHighlighting.DetectLanguage("/src/boot.S"), L.Assembly, ".S is Assembly too");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/mbr.nasm"), L.Assembly, ".nasm is Assembly");
        Equal(SyntaxHighlighting.GetDisplayName(L.Assembly), "Assembly", "Assembly has its own display name");
        Check(SyntaxHighlighting.SelectableLanguages.Contains(L.Assembly), "Assembly is in the syntax menu");
        Check(SyntaxHighlighting.GetKeywords(L.Assembly).Contains("mov"), "Assembly keywords contain 'mov'");
        Check(SyntaxHighlighting.GetKeywords(L.Assembly).Contains("section"), "Assembly keywords contain 'section'");
        Check(SyntaxHighlighting.GetKeywords(L.Assembly).Contains("rax"), "Assembly keywords contain the register 'rax'");
        Check(!SyntaxHighlighting.GetKeywords(L.Assembly).Contains("sizeof"), "Assembly keywords do not include the C 'sizeof'");
        Check(!SyntaxHighlighting.GetKeywords(L.Script).Contains("mov"), "the generic Script profile does not carry Assembly instructions");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/deploy.ps1"), L.PowerShell, ".ps1 is PowerShell");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/module.psm1"), L.PowerShell, ".psm1 is PowerShell");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/data.psd1"), L.PowerShell, ".psd1 is PowerShell");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/format.ps1xml"), L.PowerShell, ".ps1xml is PowerShell");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/session.pssc"), L.PowerShell, ".pssc is PowerShell");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/profile.psrc"), L.PowerShell, ".psrc is PowerShell");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/DEPLOY.PS1"), L.PowerShell, ".PS1 is PowerShell too");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/bin/tool", "#!/usr/bin/env pwsh"), L.PowerShell, "a pwsh shebang is PowerShell");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/bin/tool", "#!/usr/bin/env LANG=C python3"), L.Python, "variable assignments after env are skipped");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/bin/tool", "#!/usr/bin/env -S PYTHONUNBUFFERED=1 python3 -u"), L.Python, "also after env -S");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/bin/tool", "#!/usr/bin/powershell -NoProfile"), L.PowerShell, "a powershell shebang is PowerShell");
        Equal(SyntaxHighlighting.GetDisplayName(L.PowerShell), "PowerShell", "PowerShell has its own display name");
        Check(SyntaxHighlighting.SelectableLanguages.Contains(L.PowerShell), "PowerShell is in the syntax menu");
        Check(SyntaxHighlighting.GetKeywords(L.PowerShell).Contains("function"), "PowerShell keywords contain 'function'");
        Check(SyntaxHighlighting.GetKeywords(L.PowerShell).Contains("param"), "PowerShell keywords contain 'param'");
        Check(SyntaxHighlighting.GetKeywords(L.PowerShell).Contains("foreach"), "PowerShell keywords contain 'foreach'");
        Check(!SyntaxHighlighting.GetKeywords(L.PowerShell).Contains("sizeof"), "PowerShell keywords do not include the C 'sizeof'");
        Check(!SyntaxHighlighting.GetKeywords(L.Script).Contains("param"), "the generic Script profile does not carry PowerShell keywords");
        Equal(SyntaxHighlighting.DetectLanguage("/src/cpu.v"), L.Verilog, ".v is Verilog");
        Equal(SyntaxHighlighting.DetectLanguage("/src/cpu.vh"), L.Verilog, ".vh is a Verilog header");
        Equal(SyntaxHighlighting.DetectLanguage("/src/cpu.sv"), L.Verilog, ".sv is SystemVerilog");
        Equal(SyntaxHighlighting.DetectLanguage("/src/cpu.svh"), L.Verilog, ".svh is a SystemVerilog header");
        Equal(SyntaxHighlighting.DetectLanguage("/src/CPU.SV"), L.Verilog, ".SV is Verilog too");
        Equal(SyntaxHighlighting.DetectLanguage("/src/cpu.vhd"), L.Vhdl, ".vhd is VHDL");
        Equal(SyntaxHighlighting.DetectLanguage("/src/cpu.vhdl"), L.Vhdl, ".vhdl is VHDL");
        Equal(SyntaxHighlighting.DetectLanguage("/src/CPU.VHDL"), L.Vhdl, ".VHDL is VHDL too");
        Equal(SyntaxHighlighting.GetDisplayName(L.Verilog), "Verilog", "Verilog has its own display name");
        Equal(SyntaxHighlighting.GetDisplayName(L.Vhdl), "VHDL", "VHDL has its own display name");
        Check(SyntaxHighlighting.SelectableLanguages.Contains(L.Verilog), "Verilog is in the syntax menu");
        Check(SyntaxHighlighting.SelectableLanguages.Contains(L.Vhdl), "VHDL is in the syntax menu");
        Check(SyntaxHighlighting.GetKeywords(L.Verilog).Contains("module"), "Verilog keywords contain 'module'");
        Check(SyntaxHighlighting.GetKeywords(L.Verilog).Contains("always_ff"), "Verilog keywords contain 'always_ff'");
        Check(SyntaxHighlighting.GetKeywords(L.Verilog).Contains("timescale"), "Verilog keywords contain the `timescale directive");
        Check(SyntaxHighlighting.GetKeywords(L.Verilog).Contains("include"), "Verilog keywords contain the `include directive");
        Check(!SyntaxHighlighting.GetKeywords(L.Verilog).Contains("MODULE"), "Verilog keywords are case sensitive");
        Check(!SyntaxHighlighting.GetKeywords(L.Verilog).Contains("entity"), "Verilog keywords do not include the VHDL 'entity'");
        Check(SyntaxHighlighting.GetKeywords(L.Vhdl).Contains("entity"), "VHDL keywords contain 'entity'");
        Check(SyntaxHighlighting.GetKeywords(L.Vhdl).Contains("downto"), "VHDL keywords contain 'downto'");
        Check(SyntaxHighlighting.GetKeywords(L.Vhdl).Contains("process"), "VHDL keywords contain 'process'");
        Check(((HashSet<string>)SyntaxHighlighting.GetKeywords(L.Vhdl)).Contains("ENTITY"),
            "VHDL keywords are matched without regard to case");
        Check(!SyntaxHighlighting.GetKeywords(L.Vhdl).Contains("module"), "VHDL keywords do not include the Verilog 'module'");
        Check(!SyntaxHighlighting.GetKeywords(L.Vhdl).Contains("sizeof"), "VHDL keywords do not include the C 'sizeof'");
        Equal(SyntaxHighlighting.GetDisplayName(L.Ruby), "Ruby", "Ruby has its own display name");
        Equal(SyntaxHighlighting.GetDisplayName(L.Perl), "Perl", "Perl has its own display name");
        Equal(SyntaxHighlighting.GetDisplayName(L.CMake), "CMake", "CMake has its own display name");
        Equal(SyntaxHighlighting.GetDisplayName(L.Dockerfile), "Dockerfile", "Dockerfile has its own display name");
        Check(SyntaxHighlighting.SelectableLanguages.Contains(L.Ruby), "Ruby is in the syntax menu");
        Check(SyntaxHighlighting.SelectableLanguages.Contains(L.Perl), "Perl is in the syntax menu");
        Check(SyntaxHighlighting.SelectableLanguages.Contains(L.CMake), "CMake is in the syntax menu");
        Check(SyntaxHighlighting.SelectableLanguages.Contains(L.Dockerfile), "Dockerfile is in the syntax menu");
        Check(SyntaxHighlighting.GetKeywords(L.Ruby).Contains("def"), "Ruby keywords contain 'def'");
        Check(!SyntaxHighlighting.GetKeywords(L.Ruby).Contains("FROM"), "Ruby keywords do not contain the Dockerfile 'FROM'");
        Check(SyntaxHighlighting.GetKeywords(L.Perl).Contains("sub"), "Perl keywords contain 'sub'");
        Check(!SyntaxHighlighting.GetKeywords(L.Perl).Contains("def"), "Perl keywords do not contain the Ruby 'def'");
        Check(SyntaxHighlighting.GetKeywords(L.CMake).Contains("add_executable"), "CMake keywords contain 'add_executable'");
        Check(!SyntaxHighlighting.GetKeywords(L.CMake).Contains("FROM"), "CMake keywords do not contain the Dockerfile 'FROM'");
        Check(SyntaxHighlighting.GetKeywords(L.Dockerfile).Contains("FROM"), "Dockerfile keywords contain 'FROM'");
        Check(!SyntaxHighlighting.GetKeywords(L.Dockerfile).Contains("add_executable"), "Dockerfile keywords do not contain the CMake commands");
        Equal(SyntaxHighlighting.DetectLanguage("/src/Makefile.am"), L.Makefile, "Makefile.am is a makefile");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/bin/tool", "#!/bin/busybox sh"), L.Bash, "#!/bin/busybox sh is shell");
        (string Line, L Language)[] cLikeShebangs =
        {
            ("#!/usr/bin/env node", L.JavaScript), ("#!/usr/bin/env ts-node", L.TypeScript),
            ("#!/usr/bin/env php", L.Php), ("#!/usr/bin/env kotlin", L.Kotlin),
            ("#!/usr/bin/env scala", L.Scala), ("#!/usr/bin/env amm", L.Scala),
            ("#!/usr/bin/env groovy", L.Groovy), ("#!/usr/bin/env swift", L.Swift),
            ("#!/usr/bin/env dart", L.Dart), ("#!/usr/bin/env dotnet-script", L.CSharp),
            ("#!/usr/bin/env rust-script", L.Rust)
        };
        foreach ((string line, L language) in cLikeShebangs)
        {
            Equal(SyntaxHighlighting.DetectLanguage("/usr/bin/tool", line), language,
                line + " is " + SyntaxHighlighting.GetDisplayName(language));
        }
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/syslog.log"), L.Log, "logs get the colours of their timestamps and levels");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/notes.txt"), L.Plain, "text files are not highlighted");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/README.md"), L.Markdown, "Markdown is highlighted");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/fix.patch"), L.Diff, "patches are diffs");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/index.html"), L.Markup, "HTML is markup");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/backup", "#!/bin/bash"), L.Bash, "#!/bin/bash makes a script without an extension bash");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/env python3"), L.Python, "#!/usr/bin/env python3 is python");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/env -S perl -w"), L.Perl, "env options are skipped and Perl is recognized");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/env ruby"), L.Ruby, "#!/usr/bin/env ruby is Ruby");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/cmake -P"), L.CMake, "a CMake script is CMake");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/tclsh"), L.Tcl, "tclsh is Tcl");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/env wish8.6"), L.Tcl, "wish is Tcl");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/expect -f"), L.Tcl, "an Expect script is Tcl");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/awk -f"), L.Awk, "awk is AWK");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/env gawk"), L.Awk, "gawk is AWK");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/mawk -f"), L.Awk, "mawk is AWK");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/nawk -f"), L.Awk, "nawk is AWK");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/bin/busybox awk"), L.Awk, "busybox awk is AWK");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/Rscript"), L.Script, "Rscript stays in the generic script profile");
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

        Highlighted builtins = new Highlighted(L.Bash,
            "cd /tmp && pwd\n" +
            "type -a grep\n" +
            "shopt -s nullglob\n" +
            "mapfile -t lines < input\n" +
            "wait $!\n");
        Equal(Join(builtins.Tokens(0, SyntaxHighlighting.TypeKeyword)), "cd|pwd", "Bash: cd and pwd are builtins");
        Equal(Join(builtins.Tokens(1, SyntaxHighlighting.TypeKeyword)), "type", "Bash: type is a builtin");
        Equal(Join(builtins.Tokens(2, SyntaxHighlighting.TypeKeyword)), "shopt", "Bash: shopt is a builtin");
        Equal(Join(builtins.Tokens(3, SyntaxHighlighting.TypeKeyword)), "mapfile", "Bash: mapfile is a builtin");
        Equal(Join(builtins.Tokens(4, SyntaxHighlighting.TypeKeyword)), "wait", "Bash: wait is a builtin");
        Equal(Join(builtins.Tokens(4, SyntaxHighlighting.TypeVariable)), "$!", "Bash: the pid of the last background command");

        Highlighted ansi = new Highlighted(L.Bash, "printf '%s' $'it\\'s'\necho $\"hi\"\n");
        Equal(Join(ansi.Tokens(0, SyntaxHighlighting.TypeString)), "'%s'|$'it\\'s'", "Bash: ANSI-C quoting with an escaped quote");
        Equal(Join(ansi.Tokens(1, SyntaxHighlighting.TypeString)), "$\"hi\"", "Bash: a localized string");
        Equal(Join(ansi.Tokens(1, SyntaxHighlighting.TypeVariable)), "", "Bash: the dollar of a localized string is not a variable");

        Highlighted ansiMulti = new Highlighted(L.Bash, "x=$'one\\\ntwo'\n");
        Equal(Join(ansiMulti.Tokens(0, SyntaxHighlighting.TypeString)), "$'one\\", "Bash: an ANSI-C string may continue");
        Equal(Join(ansiMulti.Tokens(1, SyntaxHighlighting.TypeString)), "two'", "Bash: the second line of an ANSI-C string");
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
        Highlighted c = new Highlighted(L.CLike, "#include <stdio.h>\nint main() { /* a\n b */ return 0; }\nconst char *u = \"http://x\"; // c\nint n = `z`; char q = 'y';");
        Equal(Join(c.Tokens(0, SyntaxHighlighting.TypePreprocessor)), "#include", "preprocessor directive");
        Equal(Join(c.Tokens(1, SyntaxHighlighting.TypeComment)), "/* a", "block comment starts");
        Equal(Join(c.Tokens(2, SyntaxHighlighting.TypeComment)), " b */", "block comment continues on the next line");
        Equal(Join(c.Tokens(2, SyntaxHighlighting.TypeKeyword)), "return", "code after the block comment");
        Equal(Join(c.Tokens(3, SyntaxHighlighting.TypeString)), "\"http://x\"", "'//' inside a string is not a comment");
        Equal(Join(c.Tokens(3, SyntaxHighlighting.TypeComment)), "// c", "line comment");
        Equal(Join(c.Tokens(4, SyntaxHighlighting.TypeString)), "'y'", "C has no template strings; a backtick is not a string");
        Equal(Join(c.Tokens(4, SyntaxHighlighting.TypePreprocessor)), "", "a backtick is not a preprocessor directive");

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
        Highlighted js = new Highlighted(L.JavaScript, "const a = `x ${y}\nsecond` + 'z';");
        Equal(Join(js.Tokens(0, SyntaxHighlighting.TypeString)), "`x ${y}", "a JavaScript template string starts");
        Equal(Join(js.Tokens(1, SyntaxHighlighting.TypeString)), "second`|'z'", "...goes on, and the code after it is highlighted");
        Highlighted csharp = new Highlighted(L.CSharp, "string t = \"\"\"\nbody \"quote\"\n\"\"\";\nstring v = @\"a\"\"b\nc\";");
        Equal(Join(csharp.Tokens(0, SyntaxHighlighting.TypeString)), "\"\"\"", "a C# raw string starts");
        Equal(Join(csharp.Tokens(1, SyntaxHighlighting.TypeString)), "body \"quote\"", "the body of a C# raw string is text");
        Equal(Join(csharp.Tokens(2, SyntaxHighlighting.TypeString)), "\"\"\"", "the closing \"\"\"");
        Equal(Join(csharp.Tokens(3, SyntaxHighlighting.TypeString)), "@\"a\"\"b", "a verbatim string with a doubled quote");
        Equal(Join(csharp.Tokens(4, SyntaxHighlighting.TypeString)), "c\"", "...goes on to its closing quote");
        Highlighted csharpDirective = new Highlighted(L.CSharp, "#region Helpers\nclass C { }");
        Equal(Join(csharpDirective.Tokens(0, SyntaxHighlighting.TypePreprocessor)), "#region", "a C# preprocessor directive");
        Highlighted java = new Highlighted(L.Java, "String t = \"\"\"\nbody\n\"\"\";");
        Equal(Join(java.Tokens(1, SyntaxHighlighting.TypeString)), "body", "a Java text block is a string");
        foreach (L tripleQuote in new[] { L.Kotlin, L.Scala, L.Groovy, L.Swift, L.Dart })
        {
            Highlighted multi = new Highlighted(tripleQuote, "x = \"\"\"\nbody\n\"\"\";");
            Equal(Join(multi.Tokens(1, SyntaxHighlighting.TypeString)), "body",
                SyntaxHighlighting.GetDisplayName(tripleQuote) + " supports triple-quoted strings");
        }
        Highlighted rust = new Highlighted(L.Rust, "fn f<'a>(x: &'a str) -> &'static str { 'outer: loop { break 'outer; } }\nlet c = 'x'; let r = r#\"a \"b\" c\"#;");
        Equal(Join(rust.Tokens(0, SyntaxHighlighting.TypeString)), "", "Rust lifetimes and labels are not strings");
        Equal(Join(rust.Tokens(1, SyntaxHighlighting.TypeString)), "'x'|r#\"a \"b\" c\"#", "a Rust character literal and a raw string");
        Highlighted go = new Highlighted(L.Go, "func f() { s := `raw\nstring` } // note");
        Equal(Join(go.Tokens(0, SyntaxHighlighting.TypeString)), "`raw", "a Go raw string starts");
        Equal(Join(go.Tokens(1, SyntaxHighlighting.TypeString)), "string`", "...goes on to its closing backtick");
        Equal(Join(go.Tokens(1, SyntaxHighlighting.TypeComment)), "// note", "a Go line comment");
        Highlighted ts = new Highlighted(L.TypeScript, "interface I { value: number; } // note");
        Equal(Join(ts.Tokens(0, SyntaxHighlighting.TypeKeyword)), "interface|number", "TypeScript keywords are highlighted");
        Equal(Join(ts.Tokens(0, SyntaxHighlighting.TypeComment)), "// note", "a TypeScript line comment");
        Highlighted php = new Highlighted(L.Php, "<?php # note\n// note2\n/* block */\nfunction f() { echo 1; }");
        Equal(Join(php.Tokens(0, SyntaxHighlighting.TypeComment)), "# note", "a PHP '#' comment");
        Equal(Join(php.Tokens(1, SyntaxHighlighting.TypeComment)), "// note2", "a PHP line comment");
        Equal(Join(php.Tokens(2, SyntaxHighlighting.TypeComment)), "/* block */", "a PHP block comment");
        Equal(Join(php.Tokens(3, SyntaxHighlighting.TypeKeyword)), "function|echo", "PHP keywords are highlighted");
        Highlighted css = new Highlighted(L.Css, "/* note */\n#fff { font-size: 12px; } // not a comment");
        Equal(Join(css.Tokens(0, SyntaxHighlighting.TypeComment)), "/* note */", "a CSS block comment");
        Equal(Join(css.Tokens(1, SyntaxHighlighting.TypeComment)), "", "CSS has no '//' line comments");
        Equal(Join(css.Tokens(1, SyntaxHighlighting.TypePreprocessor)), "", "CSS '#fff' is not a preprocessor directive");
        Equal(Join(css.Tokens(1, SyntaxHighlighting.TypeKeyword)), "font-size", "a hyphenated CSS property is highlighted");
        Highlighted proto = new Highlighted(L.Protobuf, "message User { // note\n  string name = 1;\n}");
        Equal(Join(proto.Tokens(0, SyntaxHighlighting.TypeKeyword)), "message", "a Protobuf message keyword");
        Equal(Join(proto.Tokens(0, SyntaxHighlighting.TypeComment)), "// note", "a Protobuf line comment");
        Highlighted dt = new Highlighted(L.DeviceTree,
            "/dts-v1/;\n#include <dt-bindings/gpio/gpio.h>\n/include/ \"board.dtsi\"\n/delete-node/ &old;\n" +
            "/ {\n    myboard: board@10000000 {\n        #address-cells = <1>;\n" +
            "        interrupt-parent = <&gic>;\n        pinctrl-names = \"default\";\n" +
            "        compatible = \"vendor,board\";\n        /delete-property/ status;\n" +
            "        ref = &{/soc};\n    };\n}; // note");
        Equal(Join(dt.Tokens(0, SyntaxHighlighting.TypeKeyword)), "/dts-v1/", "a Device Tree version directive");
        Equal(Join(dt.Tokens(1, SyntaxHighlighting.TypePreprocessor)), "#include", "a C preprocessor include");
        Equal(Join(dt.Tokens(2, SyntaxHighlighting.TypeKeyword)), "/include/", "a Device Tree include directive");
        Equal(Join(dt.Tokens(2, SyntaxHighlighting.TypeString)), "\"board.dtsi\"", "a Device Tree include path");
        Equal(Join(dt.Tokens(3, SyntaxHighlighting.TypeKeyword)), "/delete-node/|&old", "a delete directive and a reference");
        Equal(Join(dt.Tokens(5, SyntaxHighlighting.TypeKeyword)), "myboard|board", "a label and a node name");
        Equal(Join(dt.Tokens(5, SyntaxHighlighting.TypeNumber)), "10000000", "a node unit address");
        Equal(Join(dt.Tokens(6, SyntaxHighlighting.TypeKeyword)), "#address-cells", "a cell-count property");
        Equal(Join(dt.Tokens(7, SyntaxHighlighting.TypeKeyword)), "interrupt-parent|&gic", "a property and a reference");
        Equal(Join(dt.Tokens(8, SyntaxHighlighting.TypeKeyword)), "pinctrl-names", "another hyphenated property");
        Equal(Join(dt.Tokens(9, SyntaxHighlighting.TypeKeyword)), "compatible", "a standard property");
        Equal(Join(dt.Tokens(10, SyntaxHighlighting.TypeKeyword)), "/delete-property/|status", "a delete-property directive");
        Equal(Join(dt.Tokens(11, SyntaxHighlighting.TypeKeyword)), "&{/soc}", "a path reference");
        Equal(Join(dt.Tokens(13, SyntaxHighlighting.TypeComment)), "// note", "a Device Tree line comment");
        Highlighted tcl = new Highlighted(L.Tcl,
            "# note\nproc greet {name} {\n    puts \"Hello, $name\"\n" +
            "    if {$name eq \"\"} { return }\n}\nset x 'literal'");
        Equal(Join(tcl.Tokens(0, SyntaxHighlighting.TypeComment)), "# note", "a Tcl line comment");
        Equal(Join(tcl.Tokens(1, SyntaxHighlighting.TypeKeyword)), "proc", "a Tcl procedure keyword");
        Equal(Join(tcl.Tokens(2, SyntaxHighlighting.TypeKeyword)), "puts", "a Tcl built-in command");
        Equal(Join(tcl.Tokens(2, SyntaxHighlighting.TypeString)), "\"Hello, $name\"", "a Tcl quoted string");
        Equal(Join(tcl.Tokens(3, SyntaxHighlighting.TypeKeyword)), "if|eq|return", "Tcl control words and operators");
        Equal(Join(tcl.Tokens(3, SyntaxHighlighting.TypeString)), "\"\"", "an empty Tcl string");
        Equal(Join(tcl.Tokens(5, SyntaxHighlighting.TypeString)), "", "Tcl single quotes are not strings");
        Highlighted awk = new Highlighted(L.Awk,
            "# note\nBEGIN { FS = \",\" }\nfunction add(a, b) {\n    return a + b\n}\n" +
            "{ print $1 } # note2\nEND { printf \"%d\\n\", NR }\nset x 'literal'");
        Equal(Join(awk.Tokens(0, SyntaxHighlighting.TypeComment)), "# note", "an AWK line comment");
        Equal(Join(awk.Tokens(1, SyntaxHighlighting.TypeKeyword)), "BEGIN|FS", "AWK pattern and variable keywords");
        Equal(Join(awk.Tokens(1, SyntaxHighlighting.TypeString)), "\",\"", "an AWK quoted string");
        Equal(Join(awk.Tokens(2, SyntaxHighlighting.TypeKeyword)), "function", "an AWK function keyword");
        Equal(Join(awk.Tokens(3, SyntaxHighlighting.TypeKeyword)), "return", "an AWK return keyword");
        Equal(Join(awk.Tokens(5, SyntaxHighlighting.TypeKeyword)), "print", "an AWK print keyword");
        Equal(Join(awk.Tokens(5, SyntaxHighlighting.TypeComment)), "# note2", "an AWK comment after code");
        Equal(Join(awk.Tokens(6, SyntaxHighlighting.TypeKeyword)), "END|printf|NR", "AWK END and printf keywords");
        Equal(Join(awk.Tokens(6, SyntaxHighlighting.TypeString)), "\"%d\\n\"", "an AWK printf format string");
        Equal(Join(awk.Tokens(7, SyntaxHighlighting.TypeString)), "", "AWK single quotes are not strings");
        TokenCache.Token awkBrace = awk.At(1, 6);
        Check(awkBrace != null && awkBrace.Partner != null, "AWK braces are paired");
        Check(awkBrace.Style != null, "AWK braces are foldable");
        Highlighted assembly = new Highlighted(L.Assembly,
            "; nasm comment\n# gas comment\n@ arm comment\n.text\n.globl _start\n_start:\n" +
            "    mov %eax, %ebx ; set ebx\n    ldr r0, [r1, #4] @ load\n%define SIZE 16\n" +
            ".loop:\n1:\n    db \"hi\" /* note */");
        Equal(Join(assembly.Tokens(0, SyntaxHighlighting.TypeComment)), "; nasm comment", "an Assembly ';' comment");
        Equal(Join(assembly.Tokens(1, SyntaxHighlighting.TypeComment)), "# gas comment", "an Assembly '#' comment at the start of a line");
        Equal(Join(assembly.Tokens(2, SyntaxHighlighting.TypeComment)), "@ arm comment", "an Assembly '@' comment");
        Equal(Join(assembly.Tokens(3, SyntaxHighlighting.TypePreprocessor)), ".text", "an Assembly dot directive");
        Equal(Join(assembly.Tokens(4, SyntaxHighlighting.TypePreprocessor)), ".globl", "another Assembly dot directive");
        Equal(Join(assembly.Tokens(5, SyntaxHighlighting.TypeSection)), "_start:", "an Assembly label");
        Equal(Join(assembly.Tokens(6, SyntaxHighlighting.TypeKeyword)), "mov", "an Assembly instruction");
        Equal(Join(assembly.Tokens(6, SyntaxHighlighting.TypeVariable)), "%eax|%ebx", "AT&T registers");
        Equal(Join(assembly.Tokens(6, SyntaxHighlighting.TypeComment)), "; set ebx", "an Assembly comment after code");
        Equal(Join(assembly.Tokens(7, SyntaxHighlighting.TypeKeyword)), "ldr", "an ARM instruction");
        Equal(Join(assembly.Tokens(7, SyntaxHighlighting.TypeVariable)), "r0|r1", "ARM registers");
        Equal(Join(assembly.Tokens(7, SyntaxHighlighting.TypeNumber)), "4", "an ARM immediate is not a '#' comment");
        Equal(Join(assembly.Tokens(7, SyntaxHighlighting.TypeComment)), "@ load", "an '@' comment after code");
        Equal(Join(assembly.Tokens(8, SyntaxHighlighting.TypePreprocessor)), "%define", "a NASM preprocessor directive");
        Equal(Join(assembly.Tokens(8, SyntaxHighlighting.TypeNumber)), "16", "a NASM constant value");
        Equal(Join(assembly.Tokens(9, SyntaxHighlighting.TypeSection)), ".loop:", "a local Assembly label");
        Equal(Join(assembly.Tokens(10, SyntaxHighlighting.TypeSection)), "1:", "a numeric local label");
        Equal(Join(assembly.Tokens(11, SyntaxHighlighting.TypeKeyword)), "db", "a data directive");
        Equal(Join(assembly.Tokens(11, SyntaxHighlighting.TypeString)), "\"hi\"", "an Assembly string");
        Equal(Join(assembly.Tokens(11, SyntaxHighlighting.TypeComment)), "/* note */", "an Assembly block comment");
        Highlighted powershell = new Highlighted(L.PowerShell,
            "# comment\n" +
            "Write-Host \"hi\" # note\n" +
            "$name = 'value'\n" +
            "Get-ChildItem -Path . | Where-Object { $_ -eq 1 }\n" +
            "<# block\n" +
            "   still #>\n" +
            "$text = @\"\n" +
            "hello $Name\n" +
            "\"@\n" +
            "@args\n" +
            "function Get-Thing {\n" +
            "    param($Name)\n" +
            "}\n" +
            "$s = \"a # b\"");
        Equal(Join(powershell.Tokens(0, SyntaxHighlighting.TypeComment)), "# comment", "a PowerShell '#' comment");
        Equal(Join(powershell.Tokens(1, SyntaxHighlighting.TypeKeyword)), "Write-Host", "a PowerShell cmdlet");
        Equal(Join(powershell.Tokens(1, SyntaxHighlighting.TypeString)), "\"hi\"", "a PowerShell string");
        Equal(Join(powershell.Tokens(1, SyntaxHighlighting.TypeComment)), "# note", "a PowerShell comment after code");
        Equal(Join(powershell.Tokens(2, SyntaxHighlighting.TypeVariable)), "$name", "a PowerShell variable");
        Equal(Join(powershell.Tokens(2, SyntaxHighlighting.TypeString)), "'value'", "a PowerShell single-quoted string");
        Equal(Join(powershell.Tokens(3, SyntaxHighlighting.TypeKeyword)), "Get-ChildItem|Where-Object|-eq",
            "PowerShell commands and a comparison operator");
        Equal(Join(powershell.Tokens(3, SyntaxHighlighting.TypeVariable)), "$_", "the PowerShell current-item variable");
        Equal(Join(powershell.Tokens(3, SyntaxHighlighting.TypeNumber)), "1", "a PowerShell number");
        Equal(Join(powershell.Tokens(4, SyntaxHighlighting.TypeComment)), "<# block", "a PowerShell block comment");
        Equal(Join(powershell.Tokens(5, SyntaxHighlighting.TypeComment)), "   still #>", "the rest of a PowerShell block comment");
        Equal(Join(powershell.Tokens(6, SyntaxHighlighting.TypeVariable)), "$text", "a variable before a here-string");
        Equal(Join(powershell.Tokens(6, SyntaxHighlighting.TypeString)), "@\"", "the start of a double-quoted here-string");
        Equal(Join(powershell.Tokens(7, SyntaxHighlighting.TypeString)), "hello $Name", "a here-string body");
        Equal(Join(powershell.Tokens(8, SyntaxHighlighting.TypeString)), "\"@", "the end of a double-quoted here-string");
        Equal(Join(powershell.Tokens(9, SyntaxHighlighting.TypeVariable)), "@args", "a splatted variable");
        Equal(Join(powershell.Tokens(10, SyntaxHighlighting.TypeKeyword)), "function|Get-Thing", "a PowerShell function");
        Equal(Join(powershell.Tokens(11, SyntaxHighlighting.TypeKeyword)), "param", "a PowerShell param block");
        Equal(Join(powershell.Tokens(11, SyntaxHighlighting.TypeVariable)), "$Name", "a PowerShell parameter variable");
        Equal(Join(powershell.Tokens(13, SyntaxHighlighting.TypeComment)), "", "a '#' inside a string is not a comment");
        Equal(Join(powershell.Tokens(13, SyntaxHighlighting.TypeString)), "\"a # b\"", "a PowerShell string with '#'");
        Highlighted html2 = new Highlighted(L.Markup, "<p>He said \"hi</p>\n<a href=\"u\"\n   title=\"t\">x</a>\n<!DOCTYPE html>");
        Equal(Join(html2.Tokens(0, SyntaxHighlighting.TypeString)), "", "a quote in the text between tags is not a string");
        Equal(Join(html2.Tokens(1, SyntaxHighlighting.TypeString)), "\"u\"", "an attribute value is");
        Equal(Join(html2.Tokens(2, SyntaxHighlighting.TypeString)), "\"t\"", "also on the next line of the same tag");
        Equal(Join(html2.Tokens(2, SyntaxHighlighting.TypeKeyword)), "</a", "and the tag after it");
        Equal(Join(html2.Tokens(3, SyntaxHighlighting.TypeKeyword)), "<!DOCTYPE", "the doctype");

        Highlighted docker = new Highlighted(L.Dockerfile,
            "FROM alpine # base\nRUN echo http://example.com/a#b\nfrom scratch\nRUN echo FROM");
        Equal(Join(docker.Tokens(0, SyntaxHighlighting.TypeKeyword)), "FROM", "Dockerfile instruction");
        Equal(Join(docker.Tokens(0, SyntaxHighlighting.TypeComment)), "# base", "'#' comment after whitespace");
        Equal(Join(docker.Tokens(1, SyntaxHighlighting.TypeComment)), "", "'#' inside a word is not a comment");
        Equal(Join(docker.Tokens(2, SyntaxHighlighting.TypeKeyword)), "from", "Dockerfile instructions are case insensitive");
        Equal(Join(docker.Tokens(3, SyntaxHighlighting.TypeKeyword)), "RUN", "only the first word of a line is an instruction");

        Highlighted dockerVars = new Highlighted(L.Dockerfile,
            "ARG BASE=alpine\n" +
            "FROM ${BASE} AS build\n" +
            "ENV PATH=$PATH:/app\n");
        Equal(Join(dockerVars.Tokens(1, SyntaxHighlighting.TypeVariable)), "${BASE}", "Dockerfile: a braced variable in FROM");
        Equal(Join(dockerVars.Tokens(1, SyntaxHighlighting.TypeKeyword)), "FROM", "Dockerfile: the instruction stays a keyword");
        Equal(Join(dockerVars.Tokens(2, SyntaxHighlighting.TypeVariable)), "$PATH", "Dockerfile: a plain variable in ENV");

        Highlighted dockerHeredoc = new Highlighted(L.Dockerfile,
            "RUN <<EOF\n" +
            "echo $HOME # not a comment\n" +
            "EOF\n" +
            "RUN echo 'done'\n");
        Equal(Join(dockerHeredoc.Tokens(1, SyntaxHighlighting.TypeString)), "echo $HOME # not a comment", "Dockerfile: a heredoc body is text");
        Equal(Join(dockerHeredoc.Tokens(1, SyntaxHighlighting.TypeComment)), "", "Dockerfile: '#' in a heredoc body is not a comment");
        Equal(Join(dockerHeredoc.Tokens(2, SyntaxHighlighting.TypeKeyword)), "EOF", "Dockerfile: the heredoc terminator");
        Equal(Join(dockerHeredoc.Tokens(3, SyntaxHighlighting.TypeString)), "'done'", "Dockerfile: code after a heredoc");

        Highlighted dockerStrip = new Highlighted(L.Dockerfile, "RUN <<-END\n\t$x\n\tEND\nRUN true\n");
        Equal(Join(dockerStrip.Tokens(1, SyntaxHighlighting.TypeString)), "\t$x", "Dockerfile: <<- heredoc body");
        Equal(Join(dockerStrip.Tokens(2, SyntaxHighlighting.TypeKeyword)), "\tEND", "Dockerfile: <<- accepts an indented terminator");
        Equal(Join(dockerStrip.Tokens(3, SyntaxHighlighting.TypeKeyword)), "RUN", "Dockerfile: the line after the terminator");

        Highlighted ruby = new Highlighted(L.Ruby, "class Widget#note\ndef render(item)\n  puts item\nend\nend");
        Equal(Join(ruby.Tokens(0, SyntaxHighlighting.TypeKeyword)), "class", "Ruby class keyword");
        Equal(Join(ruby.Tokens(0, SyntaxHighlighting.TypeComment)), "#note", "Ruby '#' starts a comment after code");
        Equal(Join(ruby.Tokens(1, SyntaxHighlighting.TypeKeyword)), "def", "Ruby def keyword");
        Equal(Join(ruby.Tokens(3, SyntaxHighlighting.TypeKeyword)), "end", "Ruby end keyword");

        Highlighted perl = new Highlighted(L.Perl, "my $x = 1;# note\nsub trim($) {\n  return $x;\n}");
        Equal(Join(perl.Tokens(0, SyntaxHighlighting.TypeKeyword)), "my", "Perl my keyword");
        Equal(Join(perl.Tokens(0, SyntaxHighlighting.TypeComment)), "# note", "Perl '#' starts a comment after code");
        Equal(Join(perl.Tokens(1, SyntaxHighlighting.TypeKeyword)), "sub", "Perl sub keyword");

        Highlighted cmake = new Highlighted(L.CMake,
            "cmake_minimum_required(VERSION 3.20)\nADD_EXECUTABLE(app main.cpp) # note\nif(APPLE)\n  set(CMAKE_CXX_STANDARD 20)\nendif()");
        Equal(Join(cmake.Tokens(0, SyntaxHighlighting.TypeKeyword)), "cmake_minimum_required", "CMake command");
        Equal(Join(cmake.Tokens(1, SyntaxHighlighting.TypeKeyword)), "ADD_EXECUTABLE", "CMake commands are case insensitive");
        Equal(Join(cmake.Tokens(1, SyntaxHighlighting.TypeComment)), "# note", "CMake comment after code");
        Equal(Join(cmake.Tokens(2, SyntaxHighlighting.TypeKeyword)), "if", "CMake if command");
        Equal(Join(cmake.Tokens(3, SyntaxHighlighting.TypeKeyword)), "set", "CMake set command");
        Equal(Join(cmake.Tokens(4, SyntaxHighlighting.TypeKeyword)), "endif", "CMake endif command");

        Highlighted diff = new Highlighted(L.Diff, "--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n+new\n same");
        Equal(diff.TypeAt(3, 0), SyntaxHighlighting.TypeString, "removed line");
        Equal(diff.TypeAt(4, 0), SyntaxHighlighting.TypeComment, "added line");
        Equal(diff.TypeAt(2, 0), SyntaxHighlighting.TypeSection, "hunk header");
        Equal(diff.TypeAt(0, 0), SyntaxHighlighting.TypeKeyword, "file header");
    }

    private static void HdlChecks()
    {
        Highlighted verilog = new Highlighted(L.Verilog,
            "`timescale 1ns / 1ps\n" +
            "`define WIDTH 8\n" +
            "/* block\n" +
            "   comment */\n" +
            "module counter #(parameter WIDTH = 8) (\n" +
            "    input wire clk,\n" +
            "    output reg [WIDTH-1:0] q\n" +
            ");\n" +
            "always @(posedge clk) begin\n" +
            "    if (rst) q <= 0; // note\n" +
            "    else q <= q + 1'b1;\n" +
            "end\n" +
            "initial $display(\"q=%0d\", q); $finish;\n" +
            "endmodule");
        Equal(Join(verilog.Tokens(0, SyntaxHighlighting.TypePreprocessor)), "`timescale",
            "a Verilog `timescale directive");
        Equal(Join(verilog.Tokens(0, SyntaxHighlighting.TypeNumber)), "1ns|1ps", "Verilog time values");
        Equal(Join(verilog.Tokens(1, SyntaxHighlighting.TypePreprocessor)), "`define",
            "a Verilog `define directive");
        Equal(Join(verilog.Tokens(2, SyntaxHighlighting.TypeComment)), "/* block", "a Verilog block comment starts");
        Equal(Join(verilog.Tokens(3, SyntaxHighlighting.TypeComment)), "   comment */",
            "...and continues on the next line");
        Equal(Join(verilog.Tokens(4, SyntaxHighlighting.TypeKeyword)), "module|parameter", "Verilog module keywords");
        Equal(Join(verilog.Tokens(5, SyntaxHighlighting.TypeKeyword)), "input|wire", "Verilog port keywords");
        Equal(Join(verilog.Tokens(6, SyntaxHighlighting.TypeKeyword)), "output|reg", "more Verilog port keywords");
        Equal(Join(verilog.Tokens(8, SyntaxHighlighting.TypeKeyword)), "always|posedge|begin",
            "Verilog always and begin keywords");
        Equal(Join(verilog.Tokens(9, SyntaxHighlighting.TypeKeyword)), "if", "a Verilog if keyword");
        Equal(Join(verilog.Tokens(9, SyntaxHighlighting.TypeComment)), "// note", "a Verilog line comment");
        Equal(Join(verilog.Tokens(10, SyntaxHighlighting.TypeKeyword)), "else", "a Verilog else keyword");
        Equal(Join(verilog.Tokens(10, SyntaxHighlighting.TypeString)), "",
            "the apostrophe of a based literal is not a string");
        Equal(Join(verilog.Tokens(12, SyntaxHighlighting.TypeKeyword)), "initial|$display|$finish",
            "the $display and $finish system tasks are keywords");
        Equal(Join(verilog.Tokens(12, SyntaxHighlighting.TypeString)), "\"q=%0d\"", "a Verilog format string");
        Equal(Join(verilog.Tokens(13, SyntaxHighlighting.TypeKeyword)), "endmodule", "the endmodule keyword");

        Highlighted vhdl = new Highlighted(L.Vhdl,
            "-- note\n" +
            "library ieee;\n" +
            "use ieee.std_logic_1164.all;\n" +
            "entity counter is\n" +
            "    Port (clk : in  STD_LOGIC;\n" +
            "          q   : out STD_LOGIC_VECTOR(7 downto 0));\n" +
            "end entity;\n" +
            "architecture rtl of counter is\n" +
            "    signal count : unsigned(7 downto 0) := (others => '0');\n" +
            "begin\n" +
            "    process (clk) begin\n" +
            "        if rising_edge(clk) then\n" +
            "            count <= count + 1;\n" +
            "        end if;\n" +
            "    end process;\n" +
            "end architecture;\n" +
            "ARCHITECTURE RTL OF Counter IS\n" +
            "            if clk'event and clk = '1' then");
        Equal(Join(vhdl.Tokens(0, SyntaxHighlighting.TypeComment)), "-- note", "a VHDL line comment");
        Equal(Join(vhdl.Tokens(1, SyntaxHighlighting.TypeKeyword)), "library", "a VHDL library keyword");
        Equal(Join(vhdl.Tokens(2, SyntaxHighlighting.TypeKeyword)), "use|all", "VHDL use and all keywords");
        Equal(Join(vhdl.Tokens(3, SyntaxHighlighting.TypeKeyword)), "entity|is", "VHDL entity declaration");
        Equal(Join(vhdl.Tokens(4, SyntaxHighlighting.TypeKeyword)), "Port|in",
            "VHDL keywords are case insensitive ('Port')");
        Equal(Join(vhdl.Tokens(5, SyntaxHighlighting.TypeKeyword)), "out|downto", "a VHDL port direction and range");
        Equal(Join(vhdl.Tokens(6, SyntaxHighlighting.TypeKeyword)), "end|entity", "the end of a VHDL entity");
        Equal(Join(vhdl.Tokens(7, SyntaxHighlighting.TypeKeyword)), "architecture|of|is", "a VHDL architecture");
        Equal(Join(vhdl.Tokens(8, SyntaxHighlighting.TypeKeyword)), "signal|downto|others",
            "VHDL signal, range and others keywords");
        Equal(Join(vhdl.Tokens(8, SyntaxHighlighting.TypeString)), "",
            "VHDL character literals and attributes are not strings");
        Equal(Join(vhdl.Tokens(10, SyntaxHighlighting.TypeKeyword)), "process|begin", "a VHDL process");
        Equal(Join(vhdl.Tokens(11, SyntaxHighlighting.TypeKeyword)), "if|then", "a VHDL if statement");
        Equal(Join(vhdl.Tokens(13, SyntaxHighlighting.TypeKeyword)), "end|if", "the end of a VHDL if");
        Equal(Join(vhdl.Tokens(14, SyntaxHighlighting.TypeKeyword)), "end|process", "the end of a VHDL process");
        Equal(Join(vhdl.Tokens(15, SyntaxHighlighting.TypeKeyword)), "end|architecture", "the end of a VHDL architecture");
        Equal(Join(vhdl.Tokens(16, SyntaxHighlighting.TypeKeyword)), "ARCHITECTURE|OF|IS",
            "uppercase VHDL keywords are highlighted too");
        Equal(Join(vhdl.Tokens(17, SyntaxHighlighting.TypeKeyword)), "if|and|then",
            "the apostrophe of clk'event does not swallow the rest of the line");
        Equal(Join(vhdl.Tokens(17, SyntaxHighlighting.TypeString)), "", "clk'event and '1' are not strings");
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
        L[] selfNamed =
        {
            L.Lua, L.Json, L.Makefile, L.Python, L.Sql, L.Diff, L.Plain, L.Ruby, L.Perl, L.CMake,
            L.Dockerfile, L.Java, L.Kotlin, L.Scala, L.Groovy, L.Go, L.Rust, L.Verilog, L.Swift, L.Dart,
            L.JavaScript, L.TypeScript, L.Protobuf, L.Tcl, L.Assembly, L.PowerShell, L.Postfix, L.Netplan, L.Prometheus, L.Alertmanager, L.MongoDb, L.Elasticsearch, L.Meson, L.Markdown, L.Nix, L.Fish
        };
        foreach (L language in Enum.GetValues(typeof(L)))
        {
            Check(Array.IndexOf(SyntaxHighlighting.SelectableLanguages, language) >= 0, language + " is offered in the syntax menu");
            Check(SyntaxHighlighting.GetDisplayName(language) != language.ToString() || Array.IndexOf(selfNamed, language) >= 0, language + " has a display name");
        }
    }

    private static void AdminFormatDetectionChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/ssh/ssh_config"), L.SshConfig, "ssh_config");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/ssh/SSHD_CONFIG"), L.SshConfig, "the name is matched without regard to case");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/.ssh/config"), L.SshConfig, "~/.ssh/config is the client configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/config"), L.Config, "a file called config elsewhere is a plain config");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/ssh/sshd_config.d/10-hardening.conf"), L.SshConfig, "a drop-in of sshd_config.d");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/ssh/ssh_config.d/50-x.conf"), L.SshConfig, "a drop-in of ssh_config.d");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/nginx/nginx.conf"), L.Nginx, "nginx.conf");
        Equal(SyntaxHighlighting.DetectLanguage("/opt/web/nginx.conf"), L.Nginx, "nginx.conf anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/nginx/conf.d/site.conf"), L.Nginx, "conf.d of nginx");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/nginx/sites-available/default"), L.Nginx, "sites-available has no extension");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/nginx/snippets/ssl.conf"), L.Nginx, "snippets of nginx");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/nginx/mime.types"), L.Plain, "mime.types is not a configuration of directives");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/nginx/readme"), L.Plain, "a folder called nginx somewhere else says nothing");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/etc/nginx/nginx.conf"), L.Nginx, "the BSD place of nginx.conf");
        foreach (string unit in new[] { "a.service", "a.socket", "a.timer", "a.mount", "a.automount", "a.swap", "a.target", "a.path", "a.slice", "a.scope" })
        {
            Equal(SyntaxHighlighting.DetectLanguage("/etc/systemd/system/" + unit), L.Systemd, unit + " is a systemd unit");
        }
        Equal(SyntaxHighlighting.DetectLanguage("/etc/systemd/system/app.service.d/override.conf"), L.Systemd, "a drop-in of a unit");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/systemd/journald.conf"), L.Systemd, "journald.conf is systemd's");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/foo.conf"), L.Config, "an ordinary .conf stays a config");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/systemd/network/10-eth.network"), L.SystemdNetworkd, "networkd files are not units");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/crontab"), L.Crontab, "/etc/crontab is a crontab");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/.env"), L.Config, ".env stays a config");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/pyproject.toml"), L.Toml, "pyproject.toml is TOML");

        Equal(SyntaxHighlighting.DetectLanguage("/var/log/syslog"), L.Log, "syslog");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/messages"), L.Log, "messages");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/auth.log"), L.Log, "auth.log");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/nginx/access.log"), L.Log, "a log in a folder of /var/log");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/syslog.1"), L.Log, "a rotated log");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/app.log.2"), L.Log, "a rotated .log");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/app.log"), L.Log, ".log anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/app.log.3"), L.Log, "a rotated .log anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/wtmp"), L.Plain, "wtmp is binary");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/README.md"), L.Markdown, "a Markdown file in /var/log is Markdown");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/notes.txt"), L.Plain, "prose stays plain");
        Equal(SyntaxHighlighting.DetectLanguage("/tmp/x.1"), L.Plain, "a file called x.1 elsewhere says nothing");

        // The status bar and the syntax menu offer them, with names of their own.
        foreach (L language in new[] { L.Systemd, L.Nginx, L.SshConfig, L.Crontab, L.Fstab, L.Sudoers, L.MySql, L.Redis, L.HAProxy, L.ApacheHttpd, L.SystemdNetworkd, L.Kubernetes, L.DockerCompose, L.Ansible, L.PostgreSql, L.Log })
        {
            Check(Array.IndexOf(SyntaxHighlighting.SelectableLanguages, language) >= 0, language + " is in the syntax menu");
            Check(SyntaxHighlighting.GetDisplayName(language) != language.ToString(), language + " has a name for people");
            Check(SyntaxHighlighting.GetTokenizer(language) != null, language + " has a tokenizer");
            Check(ReferenceEquals(SyntaxHighlighting.GetTokenizer(language), SyntaxHighlighting.GetTokenizer(language)), language + ": the tokenizer is the same object every time");
        }
        Check(CommentStyle.ForLanguage(L.Systemd) != null && CommentStyle.ForLanguage(L.Nginx) != null && CommentStyle.ForLanguage(L.SshConfig) != null, "# comments in systemd, nginx and ssh files");
        Check(CommentStyle.ForLanguage(L.Crontab) != null, "# comments in crontab files");
        Check(CommentStyle.ForLanguage(L.Fstab) != null, "# comments in fstab");
        Check(CommentStyle.ForLanguage(L.Sudoers) != null, "# comments in sudoers");
        Check(CommentStyle.ForLanguage(L.MySql) != null, "# comments in MySQL files");
        Check(CommentStyle.ForLanguage(L.Redis) != null, "# comments in Redis files");
        Check(CommentStyle.ForLanguage(L.HAProxy) != null, "# comments in HAProxy files");
        Check(CommentStyle.ForLanguage(L.ApacheHttpd) != null, "# comments in Apache files");
        Check(CommentStyle.ForLanguage(L.SystemdNetworkd) != null, "# comments in systemd-networkd files");
        Check(CommentStyle.ForLanguage(L.Kubernetes) != null, "# comments in Kubernetes YAML");
        Check(CommentStyle.ForLanguage(L.DockerCompose) != null, "# comments in Docker Compose files");
        Check(CommentStyle.ForLanguage(L.Ansible) != null, "# comments in Ansible files");
        Check(CommentStyle.ForLanguage(L.PostgreSql) != null, "# comments in PostgreSQL files");
        Check(CommentStyle.ForLanguage(L.Log) == null, "a log has no comments");
    }

    private static void AdminFormatTokenChecks()
    {
        // systemd: the directives are keywords, an unknown key is a plain key, sections and comments as in Config.
        Highlighted unit = new Highlighted(L.Systemd, "[Service]\nExecStart=/usr/bin/app --verbose\nRestart=on-failure\nExecStartt=/typo\n# note\n; also a note\n  User = www-data\nEnvironment=\"A=1\" B=2\n[Install]\nWantedBy=multi-user.target");
        Equal(Join(unit.Tokens(0, SyntaxHighlighting.TypeSection)), "[Service]", "systemd: the section");
        Equal(Join(unit.Tokens(1, SyntaxHighlighting.TypeKeyword)), "ExecStart", "systemd: ExecStart is a keyword");
        Equal(Join(unit.Tokens(2, SyntaxHighlighting.TypeKeyword)), "Restart", "systemd: Restart is a keyword");
        Equal(Join(unit.Tokens(3, SyntaxHighlighting.TypeKeyword)), "", "systemd: a misspelt directive is not a keyword");
        Equal(Join(unit.Tokens(3, SyntaxHighlighting.TypeVariable)), "ExecStartt", "systemd: ...it is a plain key");
        Equal(Join(unit.Tokens(4, SyntaxHighlighting.TypeComment)), "# note", "systemd: # comment");
        Equal(Join(unit.Tokens(5, SyntaxHighlighting.TypeComment)), "; also a note", "systemd: ; comment");
        Equal(Join(unit.Tokens(6, SyntaxHighlighting.TypeKeyword)), "User", "systemd: an indented directive with spaces around =");
        Equal(Join(unit.Tokens(7, SyntaxHighlighting.TypeString)), "\"A=1\"", "systemd: a quoted value");
        Equal(Join(unit.Tokens(9, SyntaxHighlighting.TypeKeyword)), "WantedBy", "systemd: WantedBy");
        Check(!SyntaxHighlighting.GetKeywords(L.Systemd).Contains("execstart"), "systemd: directives are case sensitive");

        // nginx
        Highlighted site = new Highlighted(L.Nginx, "server {\n    listen 80;\n    server_name example.org;\n    location /api/ {\n        proxy_pass http://127.0.0.1:8080;\n        proxy_set_header Host $host;\n        my_unknown_directive on;\n    }\n}\n# comment");
        Equal(Join(site.Tokens(0, SyntaxHighlighting.TypeKeyword)), "server", "nginx: server is a directive");
        Equal(Join(site.Tokens(1, SyntaxHighlighting.TypeKeyword)), "listen", "nginx: listen");
        Equal(Join(site.Tokens(1, SyntaxHighlighting.TypeNumber)), "80", "nginx: the port is a number");
        Equal(Join(site.Tokens(3, SyntaxHighlighting.TypeKeyword)), "location", "nginx: location");
        Equal(Join(site.Tokens(4, SyntaxHighlighting.TypeKeyword)), "proxy_pass", "nginx: proxy_pass");
        Equal(Join(site.Tokens(5, SyntaxHighlighting.TypeKeyword)), "proxy_set_header", "nginx: proxy_set_header");
        Equal(Join(site.Tokens(5, SyntaxHighlighting.TypeVariable)), "$host", "nginx: the variable $host");
        Equal(Join(site.Tokens(6, SyntaxHighlighting.TypeKeyword)), "on", "nginx: an unknown directive is not a keyword (only 'on' is, as a constant)");
        Equal(Join(site.Tokens(6, SyntaxHighlighting.TypeVariable)), "my_unknown_directive", "nginx: ...it is a plain key");
        Equal(Join(site.Tokens(9, SyntaxHighlighting.TypeComment)), "# comment", "nginx: comment");

        Highlighted njs = new Highlighted(L.Nginx,
            "js_import http.js;\njs_content hello;\njs_set $foo foo;\njs_var $bar value;\njs_periodic main interval=1s;\njs_preload_object config.json;");
        Equal(Join(njs.Tokens(0, SyntaxHighlighting.TypeKeyword)), "js_import", "nginx: js_import");
        Equal(Join(njs.Tokens(1, SyntaxHighlighting.TypeKeyword)), "js_content", "nginx: js_content");
        Equal(Join(njs.Tokens(2, SyntaxHighlighting.TypeKeyword)), "js_set", "nginx: js_set");
        Equal(Join(njs.Tokens(3, SyntaxHighlighting.TypeKeyword)), "js_var", "nginx: js_var");
        Equal(Join(njs.Tokens(4, SyntaxHighlighting.TypeKeyword)), "js_periodic", "nginx: js_periodic");
        Equal(Join(njs.Tokens(5, SyntaxHighlighting.TypeKeyword)), "js_preload_object", "nginx: js_preload_object");
        Check(SyntaxHighlighting.GetKeywords(L.Nginx).Contains("js_import"), "nginx keywords contain 'js_import'");
        Check(SyntaxHighlighting.GetKeywords(L.Nginx).Contains("js_periodic"), "nginx keywords contain 'js_periodic'");

        // ssh: the options are case insensitive
        Highlighted ssh = new Highlighted(L.SshConfig, "Host *\n    permitrootlogin no\n    PasswordAuthentication no\nPermitRootLogn yes\n# a comment");
        Equal(Join(ssh.Tokens(0, SyntaxHighlighting.TypeKeyword)), "Host", "ssh: Host");
        Equal(Join(ssh.Tokens(1, SyntaxHighlighting.TypeKeyword)), "permitrootlogin|no", "ssh: an option in lower case; no is a constant");
        Equal(Join(ssh.Tokens(2, SyntaxHighlighting.TypeKeyword)), "PasswordAuthentication|no", "ssh: PasswordAuthentication");
        Equal(Join(ssh.Tokens(3, SyntaxHighlighting.TypeKeyword)), "yes", "ssh: a misspelt option is not a keyword (yes is a constant)");
        Equal(Join(ssh.Tokens(3, SyntaxHighlighting.TypeVariable)), "PermitRootLogn", "ssh: ...it is a plain key");
        Check(SyntaxHighlighting.GetKeywords(L.SshConfig).Contains("PermitRootLogin"), "ssh: completion offers PermitRootLogin");

        // logs
        Highlighted log = new Highlighted(L.Log, string.Join("\n", new[]
        {
            "2026-10-31 07:00:01,123 ERROR db: connection refused",
            "2026-10-31T07:00:02.5Z WARN slow request",
            "2026-10-31T07:00:03+03:00 INFO started",
            "Oct 31 07:00:04 host sshd[12]: DEBUG details",
            "Oct  1 07:00:05 host app: ERROR_CODE=5 retry",
            "127.0.0.1 - - [31/Oct/2026:07:00:06 +0000] \"GET / HTTP/1.1\" 200 12",
            "2026/10/31 07:00:07 [error] 12#12: *1 open() failed",
            "ts=1 level=warn msg=slow",
            "an ordinary sentence with error and Warning in it",
            "07:00:09 FATAL out of memory"
        }));
        Equal(Join(log.Tokens(0, SyntaxHighlighting.TypeSection)), "2026-10-31 07:00:01,123", "log: a timestamp with a comma fraction");
        Equal(Join(log.Tokens(0, SyntaxHighlighting.TypeString)), "ERROR", "log: ERROR");
        Equal(Join(log.Tokens(1, SyntaxHighlighting.TypeSection)), "2026-10-31T07:00:02.5Z", "log: ISO time with Z");
        Equal(Join(log.Tokens(1, SyntaxHighlighting.TypeNumber)), "WARN", "log: WARN");
        Equal(Join(log.Tokens(2, SyntaxHighlighting.TypeSection)), "2026-10-31T07:00:03+03:00", "log: ISO time with an offset");
        Equal(Join(log.Tokens(2, SyntaxHighlighting.TypeKeyword)), "INFO", "log: INFO");
        Equal(Join(log.Tokens(3, SyntaxHighlighting.TypeSection)), "Oct 31 07:00:04", "log: the syslog time");
        Equal(Join(log.Tokens(3, SyntaxHighlighting.TypeComment)), "DEBUG", "log: DEBUG");
        Equal(Join(log.Tokens(4, SyntaxHighlighting.TypeSection)), "Oct  1 07:00:05", "log: the syslog time with a padded day");
        Equal(Join(log.Tokens(4, SyntaxHighlighting.TypeString)), "", "log: ERROR_CODE is not a level");
        Equal(Join(log.Tokens(5, SyntaxHighlighting.TypeSection)), "[31/Oct/2026:07:00:06 +0000]", "log: the time of an access log");
        Equal(Join(log.Tokens(6, SyntaxHighlighting.TypeSection)), "2026/10/31 07:00:07", "log: the time of nginx's error log (slashes)");
        Equal(Join(log.Tokens(6, SyntaxHighlighting.TypeString)), "[error]", "log: [error]");
        Equal(Join(log.Tokens(7, SyntaxHighlighting.TypeNumber)), "warn", "log: level=warn");
        Equal(Join(log.Tokens(8, SyntaxHighlighting.TypeString)) + Join(log.Tokens(8, SyntaxHighlighting.TypeNumber)), "", "log: the words in a sentence are left alone");
        Equal(Join(log.Tokens(9, SyntaxHighlighting.TypeSection)), "07:00:09", "log: a time of the day alone");
        Equal(Join(log.Tokens(9, SyntaxHighlighting.TypeString)), "FATAL", "log: FATAL");
    }

    private static void CrontabChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/cron.d/app"), L.Crontab, "a file of /etc/cron.d");
        Equal(SyntaxHighlighting.DetectLanguage("/var/spool/cron/crontabs/root"), L.Crontab, "a user crontab");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/crontab"), L.Crontab, "a file named crontab");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/cron.daily/backup"), L.Plain, "a script of /etc/cron.daily is not a crontab");

        Highlighted cron = new Highlighted(L.Crontab,
            "# m h dom mon dow command\nSHELL=/bin/bash\nMAILTO = root # where mail goes\n0 5 * * * /usr/bin/backup --quiet # nightly\n@reboot /usr/local/bin/start.sh\n*/15 9-17 * * mon-fri /usr/bin/report \"daily\"\n0 5 * * * /bin/echo a#b");
        Equal(Join(cron.Tokens(0, SyntaxHighlighting.TypeComment)), "# m h dom mon dow command", "crontab: a comment");
        Equal(Join(cron.Tokens(1, SyntaxHighlighting.TypeVariable)), "SHELL", "crontab: an environment assignment");
        Equal(Join(cron.Tokens(2, SyntaxHighlighting.TypeVariable)), "MAILTO", "crontab: an environment assignment with spaces");
        Equal(Join(cron.Tokens(2, SyntaxHighlighting.TypeComment)), "# where mail goes", "crontab: a comment after an assignment");
        Equal(Join(cron.Tokens(3, SyntaxHighlighting.TypeNumber)), "0|5", "crontab: the minutes and hours");
        Equal(Join(cron.Tokens(3, SyntaxHighlighting.TypeKeyword)), "*|*|*", "crontab: the * fields");
        Equal(Join(cron.Tokens(3, SyntaxHighlighting.TypeComment)), "# nightly", "crontab: a comment after the command");
        Equal(Join(cron.Tokens(4, SyntaxHighlighting.TypeKeyword)), "@reboot", "crontab: @reboot");
        Equal(Join(cron.Tokens(5, SyntaxHighlighting.TypeKeyword)), "*/15|*|*|mon-fri", "crontab: steps, ranges and names");
        Equal(Join(cron.Tokens(5, SyntaxHighlighting.TypeNumber)), "9-17", "crontab: a range of hours");
        Equal(Join(cron.Tokens(5, SyntaxHighlighting.TypeString)), "\"daily\"", "crontab: a quoted argument");
        Equal(Join(cron.Tokens(6, SyntaxHighlighting.TypeComment)), "", "crontab: # without a space is not a comment");
        Check(SyntaxHighlighting.GetKeywords(L.Crontab).Contains("jan"), "crontab keywords contain 'jan'");
        Check(SyntaxHighlighting.GetKeywords(L.Crontab).Contains("@reboot"), "crontab keywords contain '@reboot'");

        Highlighted environment = new Highlighted(L.Crontab,
            "RANDOM_DELAY=10\nMLS_LEVEL=SystemHigh\nCONTENT_TYPE=text/plain\nMAILFROM=cron@example.com");
        Equal(Join(environment.Tokens(0, SyntaxHighlighting.TypeVariable)), "RANDOM_DELAY", "crontab: RANDOM_DELAY");
        Equal(Join(environment.Tokens(1, SyntaxHighlighting.TypeVariable)), "MLS_LEVEL", "crontab: MLS_LEVEL");
        Equal(Join(environment.Tokens(2, SyntaxHighlighting.TypeVariable)), "CONTENT_TYPE", "crontab: CONTENT_TYPE");
        Equal(Join(environment.Tokens(3, SyntaxHighlighting.TypeVariable)), "MAILFROM", "crontab: MAILFROM");
        Check(SyntaxHighlighting.GetKeywords(L.Crontab).Contains("RANDOM_DELAY"), "crontab keywords contain 'RANDOM_DELAY'");
        Check(SyntaxHighlighting.GetKeywords(L.Crontab).Contains("MLS_LEVEL"), "crontab keywords contain 'MLS_LEVEL'");
        Check(SyntaxHighlighting.GetKeywords(L.Crontab).Contains("CONTENT_TYPE"), "crontab keywords contain 'CONTENT_TYPE'");
        Check(SyntaxHighlighting.GetKeywords(L.Crontab).Contains("MAILFROM"), "crontab keywords contain 'MAILFROM'");
        Equal(Join(CommentStyle.ForLanguage(L.Crontab).Toggle(new[] { "0 5 * * * /usr/bin/backup" })), "# 0 5 * * * /usr/bin/backup", "crontab comment");
    }

    private static void FstabChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/fstab"), L.Fstab, "/etc/fstab");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/fstab.d/home.conf"), L.Fstab, "a drop-in of /etc/fstab.d");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/fstab"), L.Fstab, "a file named fstab");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/fstab.conf"), L.Config, "a .conf named after fstab stays a config");

        Highlighted fstab = new Highlighted(L.Fstab,
            "# /etc/fstab\nUUID=1234-ABCD / ext4 defaults,noatime 0 1\n/dev/sdb1 /home xfs rw,noexec,nosuid 0 2\n//server/share /mnt/share cifs credentials=/root/.smb,uid=1000 0 0\ntmpfs /tmp tmpfs nosuid,nodev,size=256M 0 0\nserver:/export /mnt/nfs nfs4 soft,timeo=100 0 0\n/dev/sdc1 /mnt/bad myfs defaults,foo=bar 0 0\n/dev/sdd1 /mnt/data ext4 defaults 0 0 # backup");
        Equal(Join(fstab.Tokens(0, SyntaxHighlighting.TypeComment)), "# /etc/fstab", "fstab: a comment");
        Equal(Join(fstab.Tokens(1, SyntaxHighlighting.TypeString)), "UUID=1234-ABCD|/", "fstab: the device and the mount point");
        Equal(Join(fstab.Tokens(1, SyntaxHighlighting.TypeKeyword)), "ext4|defaults|noatime", "fstab: the filesystem and the options");
        Equal(Join(fstab.Tokens(1, SyntaxHighlighting.TypeNumber)), "0|1", "fstab: dump and pass");
        Equal(Join(fstab.Tokens(2, SyntaxHighlighting.TypeKeyword)), "xfs|rw|noexec|nosuid", "fstab: more options");
        Equal(Join(fstab.Tokens(3, SyntaxHighlighting.TypeKeyword)), "cifs|credentials=/root/.smb|uid=1000", "fstab: a network share");
        Equal(Join(fstab.Tokens(4, SyntaxHighlighting.TypeKeyword)), "tmpfs|nosuid|nodev|size=256M", "fstab: tmpfs");
        Equal(Join(fstab.Tokens(5, SyntaxHighlighting.TypeKeyword)), "nfs4|soft|timeo=100", "fstab: NFS options");
        Equal(Join(fstab.Tokens(6, SyntaxHighlighting.TypeVariable)), "myfs|foo=bar", "fstab: an unknown filesystem and option");
        Equal(Join(fstab.Tokens(7, SyntaxHighlighting.TypeComment)), "# backup", "fstab: a trailing comment");
        Check(SyntaxHighlighting.GetKeywords(L.Fstab).Contains("ext4"), "fstab keywords contain 'ext4'");
        Check(SyntaxHighlighting.GetKeywords(L.Fstab).Contains("noatime"), "fstab keywords contain 'noatime'");

        Highlighted systemd = new Highlighted(L.Fstab,
            "/dev/sdb2 /mnt/var ext4 defaults,x-systemd.growfs,x-systemd.makefs,x-systemd.mount-timeout=30,comment=backup 0 2");
        Equal(Join(systemd.Tokens(0, SyntaxHighlighting.TypeKeyword)),
            "ext4|defaults|x-systemd.growfs|x-systemd.makefs|x-systemd.mount-timeout=30|comment=backup",
            "fstab: systemd options and a comment option");
        Check(SyntaxHighlighting.GetKeywords(L.Fstab).Contains("x-systemd.growfs"), "fstab keywords contain 'x-systemd.growfs'");
        Check(SyntaxHighlighting.GetKeywords(L.Fstab).Contains("x-systemd.makefs"), "fstab keywords contain 'x-systemd.makefs'");
        Check(SyntaxHighlighting.GetKeywords(L.Fstab).Contains("x-systemd.mount-timeout"), "fstab keywords contain 'x-systemd.mount-timeout'");
        Check(SyntaxHighlighting.GetKeywords(L.Fstab).Contains("comment"), "fstab keywords contain 'comment'");
        Equal(Join(CommentStyle.ForLanguage(L.Fstab).Toggle(new[] { "UUID=1 / ext4 defaults 0 1" })), "# UUID=1 / ext4 defaults 0 1", "fstab comment");
    }

    private static void SudoersChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/sudoers"), L.Sudoers, "/etc/sudoers");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/sudoers.d/90-admins"), L.Sudoers, "a drop-in of sudoers.d");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/sudoers"), L.Sudoers, "a file named sudoers");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/sudoers.conf"), L.Config, "a .conf named after sudoers stays a config");

        Highlighted sudoers = new Highlighted(L.Sudoers,
            "Defaults env_reset,secure_path=\"/usr/local/sbin:/usr/bin:/bin\"\nDefaults:root !requiretty\nUser_Alias ADMINS = alice, bob\nCmnd_Alias SERVICES = /bin/systemctl restart nginx, /bin/systemctl status nginx\nroot ALL=(ALL:ALL) ALL\n%sudo ALL=(ALL:ALL) NOPASSWD: /usr/bin/systemctl restart app\ndeploy ALL=(root) PASSWD: /usr/bin/apt update\n#includedir /etc/sudoers.d\n@includedir /etc/sudoers.d\n# a comment");
        Equal(Join(sudoers.Tokens(0, SyntaxHighlighting.TypeKeyword)), "Defaults|env_reset|secure_path", "sudoers: Defaults and its flags");
        Equal(Join(sudoers.Tokens(0, SyntaxHighlighting.TypeString)), "\"/usr/local/sbin:/usr/bin:/bin\"", "sudoers: a quoted path list");
        Equal(Join(sudoers.Tokens(1, SyntaxHighlighting.TypeKeyword)), "Defaults|requiretty", "sudoers: a Defaults line for root");
        Equal(Join(sudoers.Tokens(2, SyntaxHighlighting.TypeKeyword)), "User_Alias", "sudoers: User_Alias");
        Equal(Join(sudoers.Tokens(3, SyntaxHighlighting.TypeKeyword)), "Cmnd_Alias", "sudoers: Cmnd_Alias");
        Equal(Join(sudoers.Tokens(4, SyntaxHighlighting.TypeVariable)), "root", "sudoers: the user of a rule");
        Equal(Join(sudoers.Tokens(4, SyntaxHighlighting.TypeKeyword)), "ALL|ALL|ALL|ALL", "sudoers: the ALL keywords");
        Equal(Join(sudoers.Tokens(5, SyntaxHighlighting.TypeKeyword)), "ALL|ALL|ALL|NOPASSWD", "sudoers: a NOPASSWD rule");
        Equal(Join(sudoers.Tokens(6, SyntaxHighlighting.TypeVariable)), "deploy", "sudoers: the user of a tagged rule");
        Equal(Join(sudoers.Tokens(6, SyntaxHighlighting.TypeKeyword)), "ALL|PASSWD", "sudoers: PASSWD");
        Equal(Join(sudoers.Tokens(7, SyntaxHighlighting.TypeKeyword)), "#includedir", "sudoers: #includedir is a directive");
        Equal(Join(sudoers.Tokens(7, SyntaxHighlighting.TypeString)), "/etc/sudoers.d", "sudoers: the include path");
        Equal(Join(sudoers.Tokens(8, SyntaxHighlighting.TypeKeyword)), "@includedir", "sudoers: @includedir is a directive");
        Equal(Join(sudoers.Tokens(9, SyntaxHighlighting.TypeComment)), "# a comment", "sudoers: a comment");
        Check(SyntaxHighlighting.GetKeywords(L.Sudoers).Contains("NOPASSWD"), "sudoers keywords contain 'NOPASSWD'");
        Check(SyntaxHighlighting.GetKeywords(L.Sudoers).Contains("secure_path"), "sudoers keywords contain 'secure_path'");
        Equal(Join(CommentStyle.ForLanguage(L.Sudoers).Toggle(new[] { "root ALL=(ALL:ALL) ALL" })), "# root ALL=(ALL:ALL) ALL", "sudoers comment");
    }

    private static void MySqlChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/mysql/my.cnf"), L.MySql, "my.cnf");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/mysql/mariadb.conf.d/50-server.cnf"), L.MySql, "a drop-in of MariaDB");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/mysql/conf.d/foo.cnf"), L.MySql, "a drop-in of MySQL");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/my.cnf"), L.MySql, "the old /etc/my.cnf");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/.my.cnf"), L.MySql, "a user's .my.cnf");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/my.ini"), L.MySql, "my.ini");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/database.conf"), L.Config, "an ordinary .conf stays a config");

        Highlighted mysql = new Highlighted(L.MySql,
            "[client]\nport = 3306\nsocket = /run/mysqld/mysqld.sock\n[mysqld]\nuser = mysql\nbind-address = 127.0.0.1\ndatadir = /var/lib/mysql\ninnodb_buffer_pool_size = 1G\nmax_connections = 200\nlog_error = /var/log/mysql/error.log # note\n!includedir /etc/mysql/conf.d/\n!include /etc/mysql/extra.cnf\n# comment\n; semicolon comment");
        Equal(Join(mysql.Tokens(0, SyntaxHighlighting.TypeSection)), "[client]", "MySQL: the client group");
        Equal(Join(mysql.Tokens(1, SyntaxHighlighting.TypeKeyword)), "port", "MySQL: port");
        Equal(Join(mysql.Tokens(1, SyntaxHighlighting.TypeNumber)), "3306", "MySQL: the port number");
        Equal(Join(mysql.Tokens(2, SyntaxHighlighting.TypeKeyword)), "socket", "MySQL: socket");
        Equal(Join(mysql.Tokens(3, SyntaxHighlighting.TypeSection)), "[mysqld]", "MySQL: the mysqld group");
        Equal(Join(mysql.Tokens(4, SyntaxHighlighting.TypeKeyword)), "user|mysql", "MySQL: user and its value");
        Equal(Join(mysql.Tokens(5, SyntaxHighlighting.TypeKeyword)), "bind-address", "MySQL: a hyphenated option");
        Equal(Join(mysql.Tokens(7, SyntaxHighlighting.TypeKeyword)), "innodb_buffer_pool_size", "MySQL: an InnoDB option");
        Equal(Join(mysql.Tokens(8, SyntaxHighlighting.TypeKeyword)), "max_connections", "MySQL: max_connections");
        Equal(Join(mysql.Tokens(9, SyntaxHighlighting.TypeComment)), "# note", "MySQL: a trailing comment");
        Equal(Join(mysql.Tokens(10, SyntaxHighlighting.TypeKeyword)), "!includedir", "MySQL: !includedir is a directive");
        Equal(Join(mysql.Tokens(10, SyntaxHighlighting.TypeString)), "/etc/mysql/conf.d/", "MySQL: the directory of !includedir");
        Equal(Join(mysql.Tokens(11, SyntaxHighlighting.TypeKeyword)), "!include", "MySQL: !include is a directive");
        Equal(Join(mysql.Tokens(11, SyntaxHighlighting.TypeString)), "/etc/mysql/extra.cnf", "MySQL: the file of !include");
        Equal(Join(mysql.Tokens(12, SyntaxHighlighting.TypeComment)), "# comment", "MySQL: a # comment");
        Equal(Join(mysql.Tokens(13, SyntaxHighlighting.TypeComment)), "; semicolon comment", "MySQL: a ; comment");
        Check(SyntaxHighlighting.GetKeywords(L.MySql).Contains("innodb_buffer_pool_size"), "MySQL keywords contain 'innodb_buffer_pool_size'");
        Check(SyntaxHighlighting.GetKeywords(L.MySql).Contains("skip-name-resolve"), "MySQL keywords contain 'skip-name-resolve'");
        Equal(Join(CommentStyle.ForLanguage(L.MySql).Toggle(new[] { "max_connections = 200" })), "# max_connections = 200", "MySQL comment");
    }

    private static void RedisChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/redis/redis.conf"), L.Redis, "redis.conf");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/redis/6379.conf"), L.Redis, "a Redis instance file");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/redis.conf"), L.Redis, "redis.conf anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/sentinel.conf"), L.Redis, "sentinel.conf anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/app.conf"), L.Config, "an ordinary .conf stays a config");

        Highlighted redis = new Highlighted(L.Redis,
            "# Redis\nbind 127.0.0.1 ::1\nport 6379\ndaemonize yes\nloglevel notice\nsave 900 1\nsave 300 10\ndir /var/lib/redis\nmaxmemory 256mb\nmaxmemory-policy allkeys-lru\nappendonly yes\nappendfsync everysec\ninclude /etc/redis/local.conf\nuser default on nopass ~* &* +@all");
        Equal(Join(redis.Tokens(0, SyntaxHighlighting.TypeComment)), "# Redis", "Redis: a comment");
        Equal(Join(redis.Tokens(1, SyntaxHighlighting.TypeKeyword)), "bind", "Redis: bind");
        Equal(Join(redis.Tokens(1, SyntaxHighlighting.TypeNumber)), "127.0.0.1|1", "Redis: the addresses");
        Equal(Join(redis.Tokens(2, SyntaxHighlighting.TypeKeyword)), "port", "Redis: port");
        Equal(Join(redis.Tokens(3, SyntaxHighlighting.TypeKeyword)), "daemonize|yes", "Redis: daemonize");
        Equal(Join(redis.Tokens(4, SyntaxHighlighting.TypeKeyword)), "loglevel|notice", "Redis: the log level");
        Equal(Join(redis.Tokens(5, SyntaxHighlighting.TypeKeyword)), "save", "Redis: save");
        Equal(Join(redis.Tokens(5, SyntaxHighlighting.TypeNumber)), "900|1", "Redis: the save interval");
        Equal(Join(redis.Tokens(8, SyntaxHighlighting.TypeKeyword)), "maxmemory", "Redis: maxmemory");
        Equal(Join(redis.Tokens(9, SyntaxHighlighting.TypeKeyword)), "maxmemory-policy|allkeys-lru", "Redis: the eviction policy");
        Equal(Join(redis.Tokens(11, SyntaxHighlighting.TypeKeyword)), "appendfsync|everysec", "Redis: appendfsync");
        Equal(Join(redis.Tokens(12, SyntaxHighlighting.TypeKeyword)), "include", "Redis: include");
        Equal(Join(redis.Tokens(13, SyntaxHighlighting.TypeKeyword)), "user|on|nopass|all", "Redis: an ACL user");
        Check(SyntaxHighlighting.GetKeywords(L.Redis).Contains("maxmemory-policy"), "Redis keywords contain 'maxmemory-policy'");
        Check(SyntaxHighlighting.GetKeywords(L.Redis).Contains("appendfsync"), "Redis keywords contain 'appendfsync'");
        Equal(Join(CommentStyle.ForLanguage(L.Redis).Toggle(new[] { "port 6379" })), "# port 6379", "Redis comment");
    }

    private static void HAProxyChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/haproxy/haproxy.cfg"), L.HAProxy, "haproxy.cfg");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/haproxy/conf.d/10-global.cfg"), L.HAProxy, "a drop-in of /etc/haproxy");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/haproxy.cfg"), L.HAProxy, "haproxy.cfg anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/app.cfg"), L.Config, "an ordinary .cfg stays a config");

        Highlighted haproxy = new Highlighted(L.HAProxy,
            "# HAProxy\nglobal\n    log /dev/log local0\n    maxconn 4096\n    daemon\ndefaults\n    mode http\n    timeout connect 5s\n    timeout client 30s\n    timeout server 30s\n    retries 3\nfrontend http-in\n    bind *:80\n    default_backend servers\nbackend servers\n    balance roundrobin\n    server web1 10.0.0.1:8080 check\nlisten stats\n    bind *:8404\n    stats enable\n    stats uri /stats");
        Equal(Join(haproxy.Tokens(0, SyntaxHighlighting.TypeComment)), "# HAProxy", "HAProxy: a comment");
        Equal(Join(haproxy.Tokens(1, SyntaxHighlighting.TypeKeyword)), "global", "HAProxy: the global section");
        Equal(Join(haproxy.Tokens(2, SyntaxHighlighting.TypeKeyword)), "log|local0", "HAProxy: log and its facility");
        Equal(Join(haproxy.Tokens(3, SyntaxHighlighting.TypeKeyword)), "maxconn", "HAProxy: maxconn");
        Equal(Join(haproxy.Tokens(3, SyntaxHighlighting.TypeNumber)), "4096", "HAProxy: the connection limit");
        Equal(Join(haproxy.Tokens(4, SyntaxHighlighting.TypeKeyword)), "daemon", "HAProxy: a directive that stands alone");
        Equal(Join(haproxy.Tokens(5, SyntaxHighlighting.TypeKeyword)), "defaults", "HAProxy: the defaults section");
        Equal(Join(haproxy.Tokens(6, SyntaxHighlighting.TypeKeyword)), "mode|http", "HAProxy: the mode");
        Equal(Join(haproxy.Tokens(7, SyntaxHighlighting.TypeKeyword)), "timeout|connect", "HAProxy: a timeout");
        Equal(Join(haproxy.Tokens(11, SyntaxHighlighting.TypeKeyword)), "frontend", "HAProxy: frontend");
        Equal(Join(haproxy.Tokens(12, SyntaxHighlighting.TypeKeyword)), "bind", "HAProxy: bind");
        Equal(Join(haproxy.Tokens(14, SyntaxHighlighting.TypeKeyword)), "backend", "HAProxy: backend");
        Equal(Join(haproxy.Tokens(15, SyntaxHighlighting.TypeKeyword)), "balance|roundrobin", "HAProxy: the balance algorithm");
        Equal(Join(haproxy.Tokens(16, SyntaxHighlighting.TypeKeyword)), "server|check", "HAProxy: a server with its check");
        Equal(Join(haproxy.Tokens(16, SyntaxHighlighting.TypeNumber)), "10.0.0.1|8080", "HAProxy: the server address");
        Equal(Join(haproxy.Tokens(17, SyntaxHighlighting.TypeKeyword)), "listen", "HAProxy: listen");
        Equal(Join(haproxy.Tokens(19, SyntaxHighlighting.TypeKeyword)), "stats|enable", "HAProxy: the stats page");
        Equal(Join(haproxy.Tokens(20, SyntaxHighlighting.TypeKeyword)), "stats|uri", "HAProxy: the stats uri");
        Check(SyntaxHighlighting.GetKeywords(L.HAProxy).Contains("default_backend"), "HAProxy keywords contain 'default_backend'");
        Check(SyntaxHighlighting.GetKeywords(L.HAProxy).Contains("roundrobin"), "HAProxy keywords contain 'roundrobin'");
        Equal(Join(CommentStyle.ForLanguage(L.HAProxy).Toggle(new[] { "    balance roundrobin" })), "    # balance roundrobin", "HAProxy comment");
    }

    private static void ApacheChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/httpd/conf/httpd.conf"), L.ApacheHttpd, "httpd.conf");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/apache2/apache2.conf"), L.ApacheHttpd, "apache2.conf");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/apache2/sites-available/000-default.conf"), L.ApacheHttpd, "a site of apache2");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/apache2/ports.conf"), L.ApacheHttpd, "ports.conf of apache2");
        Equal(SyntaxHighlighting.DetectLanguage("/var/www/html/.htaccess"), L.ApacheHttpd, ".htaccess");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/.htaccess"), L.ApacheHttpd, ".htaccess anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/site.conf"), L.Config, "an ordinary .conf stays a config");

        Highlighted apache = new Highlighted(L.ApacheHttpd,
            "# Apache\nServerRoot \"/etc/httpd\"\nListen 80\nLoadModule mpm_event_module modules/mod_mpm_event.so\nUser apache\nGroup apache\nServerName example.com:80\nDocumentRoot \"/var/www/html\"\nErrorLog \"logs/error_log\"\nLogLevel warn\n<Directory \"/var/www/html\">\n    Options Indexes FollowSymLinks\n    AllowOverride All\n    Require all granted\n</Directory>\n<VirtualHost *:80>\n    ServerName example.com\n    ProxyPass /api/ http://127.0.0.1:8080/\n    RewriteEngine On\n    RewriteRule ^/old /new [R=301,L]\n</VirtualHost>\nIncludeOptional conf.d/*.conf");
        Equal(Join(apache.Tokens(0, SyntaxHighlighting.TypeComment)), "# Apache", "Apache: a comment");
        Equal(Join(apache.Tokens(1, SyntaxHighlighting.TypeKeyword)), "ServerRoot", "Apache: ServerRoot");
        Equal(Join(apache.Tokens(1, SyntaxHighlighting.TypeString)), "\"/etc/httpd\"", "Apache: a quoted path");
        Equal(Join(apache.Tokens(2, SyntaxHighlighting.TypeKeyword)), "Listen", "Apache: Listen");
        Equal(Join(apache.Tokens(2, SyntaxHighlighting.TypeNumber)), "80", "Apache: the port");
        Equal(Join(apache.Tokens(3, SyntaxHighlighting.TypeKeyword)), "LoadModule", "Apache: LoadModule");
        Equal(Join(apache.Tokens(6, SyntaxHighlighting.TypeKeyword)), "ServerName", "Apache: ServerName");
        Equal(Join(apache.Tokens(7, SyntaxHighlighting.TypeKeyword)), "DocumentRoot", "Apache: DocumentRoot");
        Equal(Join(apache.Tokens(9, SyntaxHighlighting.TypeKeyword)), "LogLevel|warn", "Apache: the log level");
        Equal(Join(apache.Tokens(10, SyntaxHighlighting.TypeKeyword)), "Directory", "Apache: a Directory section");
        Equal(Join(apache.Tokens(10, SyntaxHighlighting.TypeString)), "\"/var/www/html\"", "Apache: the directory path");
        Equal(Join(apache.Tokens(11, SyntaxHighlighting.TypeKeyword)), "Options|Indexes|FollowSymLinks", "Apache: the options");
        Equal(Join(apache.Tokens(12, SyntaxHighlighting.TypeKeyword)), "AllowOverride|All", "Apache: AllowOverride");
        Equal(Join(apache.Tokens(13, SyntaxHighlighting.TypeKeyword)), "Require|all|granted", "Apache: Require");
        Equal(Join(apache.Tokens(14, SyntaxHighlighting.TypeKeyword)), "Directory", "Apache: the closing section");
        Equal(Join(apache.Tokens(15, SyntaxHighlighting.TypeKeyword)), "VirtualHost", "Apache: a VirtualHost section");
        Equal(Join(apache.Tokens(17, SyntaxHighlighting.TypeKeyword)), "ProxyPass", "Apache: ProxyPass");
        Equal(Join(apache.Tokens(18, SyntaxHighlighting.TypeKeyword)), "RewriteEngine|On", "Apache: RewriteEngine");
        Equal(Join(apache.Tokens(19, SyntaxHighlighting.TypeKeyword)), "RewriteRule", "Apache: RewriteRule");
        Equal(Join(apache.Tokens(21, SyntaxHighlighting.TypeKeyword)), "IncludeOptional", "Apache: IncludeOptional");
        Highlighted define = new Highlighted(L.ApacheHttpd,
            "Define BACKEND http://127.0.0.1:8080\nUnDefine OLD\n<If \"-R '10.0.0.0/8'\">\n    Require all granted\n</If>");
        Equal(Join(define.Tokens(0, SyntaxHighlighting.TypeKeyword)), "Define", "Apache: Define");
        Equal(Join(define.Tokens(1, SyntaxHighlighting.TypeKeyword)), "UnDefine", "Apache: UnDefine");
        Equal(Join(define.Tokens(2, SyntaxHighlighting.TypeKeyword)), "If", "Apache: an If section");
        Equal(Join(define.Tokens(4, SyntaxHighlighting.TypeKeyword)), "If", "Apache: the closing If section");
        Check(SyntaxHighlighting.GetKeywords(L.ApacheHttpd).Contains("Define"), "Apache keywords contain 'Define'");
        Check(SyntaxHighlighting.GetKeywords(L.ApacheHttpd).Contains("UnDefine"), "Apache keywords contain 'UnDefine'");
        Check(SyntaxHighlighting.GetKeywords(L.ApacheHttpd).Contains("If"), "Apache keywords contain 'If'");
        Check(SyntaxHighlighting.GetKeywords(L.ApacheHttpd).Contains("DocumentRoot"), "Apache keywords contain 'DocumentRoot'");
        Check(SyntaxHighlighting.GetKeywords(L.ApacheHttpd).Contains("FollowSymLinks"), "Apache keywords contain 'FollowSymLinks'");
        Equal(Join(CommentStyle.ForLanguage(L.ApacheHttpd).Toggle(new[] { "Listen 80" })), "# Listen 80", "Apache comment");
    }

    private static void SystemdNetworkdChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/systemd/network/10-eth.network"), L.SystemdNetworkd, "a .network of networkd");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/systemd/network/10-eth.netdev"), L.SystemdNetworkd, "a .netdev of networkd");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/systemd/network/10-eth.link"), L.SystemdNetworkd, "a .link of networkd");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/lib/systemd/network/99-default.link"), L.SystemdNetworkd, "the defaults of the distribution");
        Equal(SyntaxHighlighting.DetectLanguage("/run/systemd/network/10-dhcp.network"), L.SystemdNetworkd, "a generated file");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/wg0.netdev"), L.SystemdNetworkd, "a .netdev anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/10-eth.link"), L.SystemdNetworkd, "a .link anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/systemd/system/app.service"), L.Systemd, "units stay systemd");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/foo.conf"), L.Config, "an ordinary .conf stays a config");

        Highlighted network = new Highlighted(L.SystemdNetworkd,
            "# Network\n[Match]\nName=eth0\n\n[Network]\nDHCP=ipv4\nAddress=192.168.1.10/24\nGateway=192.168.1.1\nDNS=1.1.1.1 8.8.8.8\nLinkLocalAddressing=ipv6\nIPv6PrivacyExtensions=yes\n\n[DHCPv4]\nUseDNS=no\nRouteMetric=100\n\n[Address]\nAddress=192.168.1.20/24\nPreferredLifetime=forever");
        Equal(Join(network.Tokens(0, SyntaxHighlighting.TypeComment)), "# Network", "networkd: a comment");
        Equal(Join(network.Tokens(1, SyntaxHighlighting.TypeSection)), "[Match]", "networkd: the Match section");
        Equal(Join(network.Tokens(2, SyntaxHighlighting.TypeKeyword)), "Name", "networkd: Name");
        Equal(Join(network.Tokens(4, SyntaxHighlighting.TypeSection)), "[Network]", "networkd: the Network section");
        Equal(Join(network.Tokens(5, SyntaxHighlighting.TypeKeyword)), "DHCP|ipv4", "networkd: DHCP");
        Equal(Join(network.Tokens(6, SyntaxHighlighting.TypeKeyword)), "Address", "networkd: Address");
        Equal(Join(network.Tokens(6, SyntaxHighlighting.TypeNumber)), "192.168.1.10|24", "networkd: an address and its prefix");
        Equal(Join(network.Tokens(7, SyntaxHighlighting.TypeNumber)), "192.168.1.1", "networkd: a gateway");
        Equal(Join(network.Tokens(8, SyntaxHighlighting.TypeKeyword)), "DNS", "networkd: DNS");
        Equal(Join(network.Tokens(8, SyntaxHighlighting.TypeNumber)), "1.1.1.1|8.8.8.8", "networkd: the name servers");
        Equal(Join(network.Tokens(9, SyntaxHighlighting.TypeKeyword)), "LinkLocalAddressing|ipv6", "networkd: LinkLocalAddressing");
        Equal(Join(network.Tokens(10, SyntaxHighlighting.TypeKeyword)), "IPv6PrivacyExtensions|yes", "networkd: IPv6PrivacyExtensions");
        Equal(Join(network.Tokens(12, SyntaxHighlighting.TypeSection)), "[DHCPv4]", "networkd: the DHCPv4 section");
        Equal(Join(network.Tokens(13, SyntaxHighlighting.TypeKeyword)), "UseDNS|no", "networkd: UseDNS");
        Equal(Join(network.Tokens(14, SyntaxHighlighting.TypeKeyword)), "RouteMetric", "networkd: RouteMetric");
        Equal(Join(network.Tokens(14, SyntaxHighlighting.TypeNumber)), "100", "networkd: the metric");
        Equal(Join(network.Tokens(16, SyntaxHighlighting.TypeSection)), "[Address]", "networkd: the Address section");
        Equal(Join(network.Tokens(18, SyntaxHighlighting.TypeKeyword)), "PreferredLifetime|forever", "networkd: PreferredLifetime");
        Check(SyntaxHighlighting.GetKeywords(L.SystemdNetworkd).Contains("DHCP"), "networkd keywords contain 'DHCP'");
        Check(SyntaxHighlighting.GetKeywords(L.SystemdNetworkd).Contains("PreferredLifetime"), "networkd keywords contain 'PreferredLifetime'");

        Highlighted wireguard = new Highlighted(L.SystemdNetworkd,
            "[WireGuard]\nPrivateKey = abc=\nListenPort = 51820\n[WireGuardPeer]\nPublicKey = def=\nPresharedKey = ghi=\nAllowedIPs = 10.0.0.0/24\nEndpoint = 192.168.1.1:51820\nPersistentKeepalive = 25");
        Equal(Join(wireguard.Tokens(0, SyntaxHighlighting.TypeSection)), "[WireGuard]", "networkd: the WireGuard section");
        Equal(Join(wireguard.Tokens(1, SyntaxHighlighting.TypeKeyword)), "PrivateKey", "networkd: PrivateKey");
        Equal(Join(wireguard.Tokens(2, SyntaxHighlighting.TypeKeyword)), "ListenPort", "networkd: ListenPort");
        Equal(Join(wireguard.Tokens(3, SyntaxHighlighting.TypeSection)), "[WireGuardPeer]", "networkd: the WireGuardPeer section");
        Equal(Join(wireguard.Tokens(4, SyntaxHighlighting.TypeKeyword)), "PublicKey", "networkd: PublicKey");
        Equal(Join(wireguard.Tokens(5, SyntaxHighlighting.TypeKeyword)), "PresharedKey", "networkd: PresharedKey");
        Equal(Join(wireguard.Tokens(6, SyntaxHighlighting.TypeKeyword)), "AllowedIPs", "networkd: AllowedIPs");
        Equal(Join(wireguard.Tokens(7, SyntaxHighlighting.TypeKeyword)), "Endpoint", "networkd: Endpoint");
        Equal(Join(wireguard.Tokens(8, SyntaxHighlighting.TypeKeyword)), "PersistentKeepalive", "networkd: PersistentKeepalive");
        Check(SyntaxHighlighting.GetKeywords(L.SystemdNetworkd).Contains("PrivateKey"), "networkd keywords contain 'PrivateKey'");
        Check(SyntaxHighlighting.GetKeywords(L.SystemdNetworkd).Contains("AllowedIPs"), "networkd keywords contain 'AllowedIPs'");
        Equal(Join(CommentStyle.ForLanguage(L.SystemdNetworkd).Toggle(new[] { "DHCP=ipv4" })), "# DHCP=ipv4", "networkd comment");
    }

    private static void IfupdownChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/network/interfaces"), L.Ifupdown, "/etc/network/interfaces");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/network/interfaces.d/eth0"), L.Ifupdown, "a drop-in of interfaces.d");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/network/interfaces.d/br0"), L.Ifupdown, "another drop-in");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/network/if-up.d/up"), L.Plain, "if-up.d is a script, not the configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/network/if-pre-up.d/x"), L.Plain, "if-pre-up.d is a script too");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/interfaces"), L.Plain, "interfaces elsewhere is not ifupdown");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/network/routes"), L.Plain, "another file of /etc/network is not ifupdown");

        Highlighted net = new Highlighted(L.Ifupdown,
            "# Debian networking\nauto lo eth0\niface lo inet loopback\niface eth0 inet static\n    address 192.168.1.10\n    netmask 255.255.255.0\n    gateway 192.168.1.1\n    dns-nameservers 8.8.8.8 1.1.1.1\n    pre-up ip link set eth0 up\n    post-up iptables -A INPUT -j ACCEPT\n    my-option value\niface eth0 inet6 dhcp\n    accept_ra 2\nsource /etc/network/interfaces.d/*\nallow-hotplug eth1");
        Equal(Join(net.Tokens(0, SyntaxHighlighting.TypeComment)), "# Debian networking", "ifupdown: a comment");
        Equal(Join(net.Tokens(1, SyntaxHighlighting.TypeKeyword)), "auto", "ifupdown: auto");
        Equal(Join(net.Tokens(2, SyntaxHighlighting.TypeKeyword)), "iface|inet|loopback", "ifupdown: an interface and its family and method");
        Equal(Join(net.Tokens(3, SyntaxHighlighting.TypeKeyword)), "iface|inet|static", "ifupdown: a static interface");
        Equal(Join(net.Tokens(4, SyntaxHighlighting.TypeKeyword)), "address", "ifupdown: address");
        Equal(Join(net.Tokens(4, SyntaxHighlighting.TypeNumber)), "192.168.1.10", "ifupdown: an address");
        Equal(Join(net.Tokens(5, SyntaxHighlighting.TypeKeyword)), "netmask", "ifupdown: netmask");
        Equal(Join(net.Tokens(5, SyntaxHighlighting.TypeNumber)), "255.255.255.0", "ifupdown: a netmask");
        Equal(Join(net.Tokens(7, SyntaxHighlighting.TypeKeyword)), "dns-nameservers", "ifupdown: dns-nameservers");
        Equal(Join(net.Tokens(7, SyntaxHighlighting.TypeNumber)), "8.8.8.8|1.1.1.1", "ifupdown: the name servers");
        Equal(Join(net.Tokens(8, SyntaxHighlighting.TypeKeyword)), "pre-up", "ifupdown: pre-up");
        Equal(Join(net.Tokens(9, SyntaxHighlighting.TypeKeyword)), "post-up", "ifupdown: post-up");
        Equal(Join(net.Tokens(10, SyntaxHighlighting.TypeKeyword)), "", "ifupdown: an unknown option is not a keyword");
        Equal(Join(net.Tokens(10, SyntaxHighlighting.TypeVariable)), "my-option", "ifupdown: ...it is a plain key");
        Equal(Join(net.Tokens(11, SyntaxHighlighting.TypeKeyword)), "iface|inet6|dhcp", "ifupdown: an IPv6 interface with DHCP");
        Equal(Join(net.Tokens(12, SyntaxHighlighting.TypeKeyword)), "accept_ra", "ifupdown: accept_ra");
        Equal(Join(net.Tokens(13, SyntaxHighlighting.TypeKeyword)), "source", "ifupdown: source");
        Equal(Join(net.Tokens(14, SyntaxHighlighting.TypeKeyword)), "allow-hotplug", "ifupdown: allow-hotplug");
        Check(SyntaxHighlighting.GetKeywords(L.Ifupdown).Contains("iface"), "ifupdown keywords contain 'iface'");
        Check(SyntaxHighlighting.GetKeywords(L.Ifupdown).Contains("bridge_ports"), "ifupdown keywords contain 'bridge_ports'");
        Check(SyntaxHighlighting.GetKeywords(L.Ifupdown).Contains("dhcp"), "ifupdown keywords contain 'dhcp'");
        Equal(Join(CommentStyle.ForLanguage(L.Ifupdown).Toggle(new[] { "iface eth0 inet static" })), "# iface eth0 inet static", "ifupdown comment");
    }

    private static void DnsmasqChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/dnsmasq.conf"), L.Dnsmasq, "the main dnsmasq configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/dnsmasq.d/lan.conf"), L.Dnsmasq, "a drop-in of dnsmasq.d");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/dnsmasq.d/01-lan"), L.Dnsmasq, "a drop-in without an extension");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/NetworkManager/dnsmasq.d/nm.conf"), L.Dnsmasq, "the dnsmasq drop-ins of NetworkManager");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/dnsmasq.conf"), L.Dnsmasq, "a dnsmasq.conf outside /etc");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/dnsmasq.d/README"), L.Plain, "the README of dnsmasq.d is prose");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/dnsmasq.d/notes.txt"), L.Plain, "another text file of dnsmasq.d");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/dnsmasq.d-available/lan.conf"), L.Config, "dnsmasq.d-available is not read by dnsmasq");

        Highlighted net = new Highlighted(L.Dnsmasq,
            "# dnsmasq configuration\ndomain-needed\nbogus-priv\nserver=8.8.8.8\nserver=/example.com/1.1.1.1\n" +
            "interface=eth0\nbind-interfaces\ncache-size=1000\n" +
            "dhcp-range=192.168.1.50,192.168.1.150,12h\n" +
            "dhcp-range=192.168.2.50,192.168.2.150,static\n" +
            "dhcp-option=6,192.168.1.1\n" +
            "dhcp-host=aa:bb:cc:dd:ee:ff,192.168.1.20,printer\n" +
            "log-queries\nlog-facility=/var/log/dnsmasq.log\n" +
            "conf-dir=/etc/dnsmasq.d/,*.conf\nunknown-option=1\n" +
            "cache-size=2000 # entries");
        Equal(Join(net.Tokens(0, SyntaxHighlighting.TypeComment)), "# dnsmasq configuration", "dnsmasq: a comment");
        Equal(Join(net.Tokens(1, SyntaxHighlighting.TypeKeyword)), "domain-needed", "dnsmasq: a flag without a value");
        Equal(Join(net.Tokens(2, SyntaxHighlighting.TypeKeyword)), "bogus-priv", "dnsmasq: another flag without a value");
        Equal(Join(net.Tokens(3, SyntaxHighlighting.TypeKeyword)), "server", "dnsmasq: server");
        Equal(Join(net.Tokens(3, SyntaxHighlighting.TypeNumber)), "8.8.8.8", "dnsmasq: an upstream address");
        Equal(Join(net.Tokens(4, SyntaxHighlighting.TypeKeyword)), "server", "dnsmasq: a server for a domain");
        Equal(Join(net.Tokens(4, SyntaxHighlighting.TypeNumber)), "1.1.1.1", "dnsmasq: the address after the domain");
        Equal(Join(net.Tokens(6, SyntaxHighlighting.TypeKeyword)), "bind-interfaces", "dnsmasq: bind-interfaces");
        Equal(Join(net.Tokens(7, SyntaxHighlighting.TypeKeyword)), "cache-size", "dnsmasq: cache-size");
        Equal(Join(net.Tokens(7, SyntaxHighlighting.TypeNumber)), "1000", "dnsmasq: the cache size");
        Equal(Join(net.Tokens(8, SyntaxHighlighting.TypeKeyword)), "dhcp-range", "dnsmasq: dhcp-range");
        Equal(Join(net.Tokens(8, SyntaxHighlighting.TypeNumber)), "192.168.1.50|192.168.1.150", "dnsmasq: the range");
        Equal(Join(net.Tokens(9, SyntaxHighlighting.TypeKeyword)), "dhcp-range|static", "dnsmasq: a static range");
        Equal(Join(net.Tokens(10, SyntaxHighlighting.TypeKeyword)), "dhcp-option", "dnsmasq: dhcp-option");
        Equal(Join(net.Tokens(10, SyntaxHighlighting.TypeNumber)), "6|192.168.1.1", "dnsmasq: the option and its value");
        Equal(Join(net.Tokens(11, SyntaxHighlighting.TypeKeyword)), "dhcp-host", "dnsmasq: dhcp-host");
        Equal(Join(net.Tokens(11, SyntaxHighlighting.TypeNumber)), "192.168.1.20", "dnsmasq: the reserved address");
        Equal(Join(net.Tokens(12, SyntaxHighlighting.TypeKeyword)), "log-queries", "dnsmasq: log-queries");
        Equal(Join(net.Tokens(13, SyntaxHighlighting.TypeKeyword)), "log-facility", "dnsmasq: log-facility");
        Equal(Join(net.Tokens(14, SyntaxHighlighting.TypeKeyword)), "conf-dir", "dnsmasq: conf-dir");
        Equal(Join(net.Tokens(15, SyntaxHighlighting.TypeKeyword)), "", "dnsmasq: an unknown option is not a keyword");
        Equal(Join(net.Tokens(15, SyntaxHighlighting.TypeVariable)), "unknown-option", "dnsmasq: ...it is a plain key");
        Equal(Join(net.Tokens(16, SyntaxHighlighting.TypeNumber)), "2000", "dnsmasq: a value before a comment");
        Equal(Join(net.Tokens(16, SyntaxHighlighting.TypeComment)), "# entries", "dnsmasq: a trailing comment");
        Check(SyntaxHighlighting.GetKeywords(L.Dnsmasq).Contains("dhcp-range"), "dnsmasq keywords contain 'dhcp-range'");
        Check(SyntaxHighlighting.GetKeywords(L.Dnsmasq).Contains("dnssec"), "dnsmasq keywords contain 'dnssec'");
        Check(SyntaxHighlighting.GetKeywords(L.Dnsmasq).Contains("ra-names"), "dnsmasq keywords contain 'ra-names'");
        Equal(Join(CommentStyle.ForLanguage(L.Dnsmasq).Toggle(new[] { "server=8.8.8.8" })), "# server=8.8.8.8", "dnsmasq comment");
    }

    private static void HostapdChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/hostapd/hostapd.conf"), L.Hostapd, "the main hostapd configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/hostapd/wlan1.conf"), L.Hostapd, "a per-interface file of /etc/hostapd");
        Equal(SyntaxHighlighting.DetectLanguage("/var/run/hostapd-phy0.conf"), L.Hostapd, "the runtime file of OpenWrt");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/hostapd.conf"), L.Hostapd, "a hostapd.conf outside /etc");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/hostapd-phy0.conf"), L.Hostapd, "a runtime file outside /var/run");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/hostapd/hostapd.eap_user"), L.Plain, "the EAP user file is not the configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/hostapd/hostapd.conf.bak"), L.Plain, "a backup is not the configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/wpa_supplicant/wpa_supplicant.conf"), L.Config, "wpa_supplicant is not hostapd");

        Highlighted net = new Highlighted(L.Hostapd,
            "# hostapd configuration\ninterface=wlan0\ndriver=nl80211\n" +
            "ctrl_interface=/var/run/hostapd\nssid=My Network\nhw_mode=g\nchannel=6\n" +
            "ieee80211n=1\nwmm_enabled=1\nmacaddr_acl=0\nauth_algs=1\nwpa=2\n" +
            "wpa_key_mgmt=WPA-PSK SAE\nwpa_pairwise=CCMP\nrsn_pairwise=CCMP\n" +
            "wpa_passphrase=secret123\ncountry_code=US\nbeacon_int=100\ndtim_period=2\n" +
            "unknown-option=1");
        Equal(Join(net.Tokens(0, SyntaxHighlighting.TypeComment)), "# hostapd configuration", "hostapd: a comment");
        Equal(Join(net.Tokens(1, SyntaxHighlighting.TypeKeyword)), "interface", "hostapd: interface");
        Equal(Join(net.Tokens(2, SyntaxHighlighting.TypeKeyword)), "driver", "hostapd: driver");
        Equal(Join(net.Tokens(3, SyntaxHighlighting.TypeKeyword)), "ctrl_interface", "hostapd: ctrl_interface");
        Equal(Join(net.Tokens(4, SyntaxHighlighting.TypeKeyword)), "ssid", "hostapd: ssid");
        Equal(Join(net.Tokens(5, SyntaxHighlighting.TypeKeyword)), "hw_mode|g", "hostapd: hw_mode and its letter");
        Equal(Join(net.Tokens(6, SyntaxHighlighting.TypeKeyword)), "channel", "hostapd: channel");
        Equal(Join(net.Tokens(6, SyntaxHighlighting.TypeNumber)), "6", "hostapd: the channel number");
        Equal(Join(net.Tokens(7, SyntaxHighlighting.TypeKeyword)), "ieee80211n", "hostapd: ieee80211n");
        Equal(Join(net.Tokens(7, SyntaxHighlighting.TypeNumber)), "1", "hostapd: 802.11n is on");
        Equal(Join(net.Tokens(9, SyntaxHighlighting.TypeKeyword)), "macaddr_acl", "hostapd: macaddr_acl");
        Equal(Join(net.Tokens(11, SyntaxHighlighting.TypeKeyword)), "wpa", "hostapd: wpa");
        Equal(Join(net.Tokens(11, SyntaxHighlighting.TypeNumber)), "2", "hostapd: WPA2");
        Equal(Join(net.Tokens(12, SyntaxHighlighting.TypeKeyword)), "wpa_key_mgmt|WPA-PSK|SAE", "hostapd: the key management suites");
        Equal(Join(net.Tokens(13, SyntaxHighlighting.TypeKeyword)), "wpa_pairwise|CCMP", "hostapd: the pairwise cipher");
        Equal(Join(net.Tokens(14, SyntaxHighlighting.TypeKeyword)), "rsn_pairwise|CCMP", "hostapd: the RSN cipher");
        Equal(Join(net.Tokens(15, SyntaxHighlighting.TypeKeyword)), "wpa_passphrase", "hostapd: wpa_passphrase");
        Equal(Join(net.Tokens(16, SyntaxHighlighting.TypeKeyword)), "country_code", "hostapd: country_code");
        Equal(Join(net.Tokens(17, SyntaxHighlighting.TypeNumber)), "100", "hostapd: the beacon interval");
        Equal(Join(net.Tokens(18, SyntaxHighlighting.TypeNumber)), "2", "hostapd: the DTIM period");
        Equal(Join(net.Tokens(19, SyntaxHighlighting.TypeKeyword)), "", "hostapd: an unknown option is not a keyword");
        Equal(Join(net.Tokens(19, SyntaxHighlighting.TypeVariable)), "unknown-option", "hostapd: ...it is a plain key");
        Check(SyntaxHighlighting.GetKeywords(L.Hostapd).Contains("wpa_key_mgmt"), "hostapd keywords contain 'wpa_key_mgmt'");
        Check(SyntaxHighlighting.GetKeywords(L.Hostapd).Contains("ieee80211ax"), "hostapd keywords contain 'ieee80211ax'");
        Check(SyntaxHighlighting.GetKeywords(L.Hostapd).Contains("SAE"), "hostapd keywords contain 'SAE'");
        Equal(Join(CommentStyle.ForLanguage(L.Hostapd).Toggle(new[] { "ssid=My Network" })), "# ssid=My Network", "hostapd comment");
    }

    private static void WireguardChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/wireguard/wg0.conf"), L.Wireguard, "a tunnel of /etc/wireguard");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/wireguard/home.conf"), L.Wireguard, "a tunnel with a custom name");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/wg0.conf"), L.Wireguard, "a wg0.conf outside /etc");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/wg12.conf"), L.Wireguard, "a numbered interface");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/wg-home.conf"), L.Wireguard, "an interface with a dashed name");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/wg.conf"), L.Wireguard, "the bare wg.conf");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/wget.conf"), L.Config, "wget.conf is not WireGuard");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/wireguard/privatekey"), L.Plain, "a key file is not the configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/wireguard/wg0.conf.bak"), L.Plain, "a backup is not the configuration");

        Highlighted net = new Highlighted(L.Wireguard,
            "# WireGuard tunnel\n[Interface]\nAddress = 10.0.0.2/24\nListenPort = 51820\n" +
            "PrivateKey = abc=\nDNS = 1.1.1.1\nMTU = 1420\nTable = auto\n" +
            "PostUp = iptables -A FORWARD -i wg0 -j ACCEPT\nSaveConfig = true\n\n" +
            "[Peer]\nPublicKey = def=\nPresharedKey = ghi=\n" +
            "AllowedIPs = 10.0.0.0/24\nEndpoint = vpn.example.com:51820\n" +
            "PersistentKeepalive = 25\nUnknownKey = 1\nendpoint = vpn2.example.com:51821");
        Equal(Join(net.Tokens(0, SyntaxHighlighting.TypeComment)), "# WireGuard tunnel", "WireGuard: a comment");
        Equal(Join(net.Tokens(1, SyntaxHighlighting.TypeSection)), "[Interface]", "WireGuard: the Interface section");
        Equal(Join(net.Tokens(2, SyntaxHighlighting.TypeKeyword)), "Address", "WireGuard: Address");
        Equal(Join(net.Tokens(2, SyntaxHighlighting.TypeNumber)), "10.0.0.2|24", "WireGuard: the tunnel address");
        Equal(Join(net.Tokens(3, SyntaxHighlighting.TypeKeyword)), "ListenPort", "WireGuard: ListenPort");
        Equal(Join(net.Tokens(3, SyntaxHighlighting.TypeNumber)), "51820", "WireGuard: the listen port");
        Equal(Join(net.Tokens(4, SyntaxHighlighting.TypeKeyword)), "PrivateKey", "WireGuard: PrivateKey");
        Equal(Join(net.Tokens(5, SyntaxHighlighting.TypeNumber)), "1.1.1.1", "WireGuard: the DNS server");
        Equal(Join(net.Tokens(7, SyntaxHighlighting.TypeKeyword)), "Table|auto", "WireGuard: the routing table");
        Equal(Join(net.Tokens(8, SyntaxHighlighting.TypeKeyword)), "PostUp", "WireGuard: PostUp");
        Equal(Join(net.Tokens(9, SyntaxHighlighting.TypeKeyword)), "SaveConfig|true", "WireGuard: SaveConfig and its value");
        Equal(Join(net.Tokens(11, SyntaxHighlighting.TypeSection)), "[Peer]", "WireGuard: the Peer section");
        Equal(Join(net.Tokens(12, SyntaxHighlighting.TypeKeyword)), "PublicKey", "WireGuard: PublicKey");
        Equal(Join(net.Tokens(13, SyntaxHighlighting.TypeKeyword)), "PresharedKey", "WireGuard: PresharedKey");
        Equal(Join(net.Tokens(14, SyntaxHighlighting.TypeKeyword)), "AllowedIPs", "WireGuard: AllowedIPs");
        Equal(Join(net.Tokens(15, SyntaxHighlighting.TypeKeyword)), "Endpoint", "WireGuard: Endpoint");
        Equal(Join(net.Tokens(15, SyntaxHighlighting.TypeNumber)), "51820", "WireGuard: the endpoint port");
        Equal(Join(net.Tokens(16, SyntaxHighlighting.TypeKeyword)), "PersistentKeepalive", "WireGuard: PersistentKeepalive");
        Equal(Join(net.Tokens(16, SyntaxHighlighting.TypeNumber)), "25", "WireGuard: the keepalive interval");
        Equal(Join(net.Tokens(17, SyntaxHighlighting.TypeKeyword)), "", "WireGuard: an unknown key is not a keyword");
        Equal(Join(net.Tokens(17, SyntaxHighlighting.TypeVariable)), "UnknownKey", "WireGuard: ...it is a plain key");
        Equal(Join(net.Tokens(18, SyntaxHighlighting.TypeKeyword)), "endpoint", "WireGuard: the keys match in any case");
        Check(SyntaxHighlighting.GetKeywords(L.Wireguard).Contains("AllowedIPs"), "WireGuard keywords contain 'AllowedIPs'");
        Check(SyntaxHighlighting.GetKeywords(L.Wireguard).Contains("PersistentKeepalive"), "WireGuard keywords contain 'PersistentKeepalive'");
        Check(SyntaxHighlighting.GetKeywords(L.Wireguard).Contains("auto"), "WireGuard keywords contain 'auto'");
        Equal(Join(CommentStyle.ForLanguage(L.Wireguard).Toggle(new[] { "ListenPort = 51820" })), "# ListenPort = 51820", "WireGuard comment");
    }

    private static void UdevChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/udev/rules.d/99-local.rules"), L.Udev, "a local udev rule");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/lib/udev/rules.d/60-persistent-storage.rules"), L.Udev, "a rule of the distribution");
        Equal(SyntaxHighlighting.DetectLanguage("/lib/udev/rules.d/50-udev-default.rules"), L.Udev, "a rule of the merged /lib");
        Equal(SyntaxHighlighting.DetectLanguage("/run/udev/rules.d/10-dynamic.rules"), L.Udev, "a generated rule");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/udev/udev.conf"), L.Udev, "the main udev.conf");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/udev.conf"), L.Udev, "a udev.conf outside /etc");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/share/polkit-1/rules.d/50-default.rules"), L.Config, "a polkit rule is not udev");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/99-test.rules"), L.Config, "a .rules file outside udev");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/udev/rules.d/README"), L.Plain, "the README of rules.d is prose");

        Highlighted net = new Highlighted(L.Udev,
            "# udev rules\nACTION==\"add\", SUBSYSTEM==\"net\", KERNEL==\"eth*\", NAME=\"lan0\"\n" +
            "SUBSYSTEM==\"block\", ENV{ID_FS_TYPE}==\"ext4\", SYMLINK+=\"disk/by-label/data\"\n" +
            "ATTR{address}==\"aa:bb:cc:dd:ee:ff\", ATTR{mtu}==\"1500\", TAG+=\"systemd\"\n" +
            "KERNEL==\"sd*\", PROGRAM=\"/bin/sh -c 'echo %k'\", RESULT==\"sda\", RUN+=\"/usr/bin/logger\"\n" +
            "MODE=\"0660\", OWNER=\"root\", GROUP=\"disk\"\n" +
            "OPTIONS+=db_persist\nKERNEL==\"ttyUSB*\", OPTIONS+=\"last_rule\"\n" +
            "UNKNOWN_KEY==\"x\"");
        Equal(Join(net.Tokens(0, SyntaxHighlighting.TypeComment)), "# udev rules", "udev: a comment");
        Equal(Join(net.Tokens(1, SyntaxHighlighting.TypeKeyword)), "ACTION|SUBSYSTEM|KERNEL|NAME", "udev: the first rule");
        Equal(Join(net.Tokens(1, SyntaxHighlighting.TypeString)), "\"add\"|\"net\"|\"eth*\"|\"lan0\"", "udev: the values of the first rule");
        Equal(Join(net.Tokens(2, SyntaxHighlighting.TypeKeyword)), "SUBSYSTEM|ENV|SYMLINK", "udev: a filesystem rule");
        Equal(Join(net.Tokens(3, SyntaxHighlighting.TypeKeyword)), "ATTR|ATTR|TAG", "udev: the attributes and a tag");
        Equal(Join(net.Tokens(4, SyntaxHighlighting.TypeKeyword)), "KERNEL|PROGRAM|RESULT|RUN", "udev: a program and its result");
        Equal(Join(net.Tokens(5, SyntaxHighlighting.TypeKeyword)), "MODE|OWNER|GROUP", "udev: owner and mode");
        Equal(Join(net.Tokens(6, SyntaxHighlighting.TypeKeyword)), "OPTIONS|db_persist", "udev: an unquoted option");
        Equal(Join(net.Tokens(7, SyntaxHighlighting.TypeKeyword)), "KERNEL|OPTIONS", "udev: last_rule");
        Equal(Join(net.Tokens(8, SyntaxHighlighting.TypeKeyword)), "", "udev: an unknown key is not a keyword");
        Equal(Join(net.Tokens(8, SyntaxHighlighting.TypeVariable)), "UNKNOWN_KEY", "udev: ...it is a plain key");
        Check(SyntaxHighlighting.GetKeywords(L.Udev).Contains("SUBSYSTEM"), "udev keywords contain 'SUBSYSTEM'");
        Check(SyntaxHighlighting.GetKeywords(L.Udev).Contains("RUN"), "udev keywords contain 'RUN'");
        Check(SyntaxHighlighting.GetKeywords(L.Udev).Contains("db_persist"), "udev keywords contain 'db_persist'");
        Equal(Join(CommentStyle.ForLanguage(L.Udev).Toggle(new[] { "ACTION==\"add\"" })), "# ACTION==\"add\"", "udev comment");
    }

    private static void NftablesChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/nftables.conf"), L.Nftables, "the main nftables ruleset");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/sysconfig/nftables.conf"), L.Nftables, "the Fedora ruleset");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/nftables.d/10-filter.conf"), L.Nftables, "a drop-in of nftables.d");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/nftables.d/10-filter.nft"), L.Nftables, "an .nft drop-in");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/firewall.nft"), L.Nftables, "an .nft file anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/nftables.conf"), L.Nftables, "a nftables.conf outside /etc");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/rules.conf"), L.Config, "an ordinary .conf is not nftables");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/nftables.d/README"), L.Plain, "the README of nftables.d is prose");

        Highlighted net = new Highlighted(L.Nftables,
            "#!/usr/sbin/nft -f\n# firewall\nflush ruleset\n\n" +
            "table inet filter {\n    chain input {\n" +
            "        type filter hook input priority filter; policy drop;\n" +
            "        ct state established,related accept\n" +
            "        iifname \"lo\" accept\n" +
            "        tcp dport { ssh, http, https } accept\n" +
            "        ip saddr 192.168.1.0/24 accept\n" +
            "        counter drop\n" +
            "        log prefix \"nft: \" level info\n    }\n" +
            "    chain forward {\n" +
            "        type filter hook forward priority 0; policy drop;\n" +
            "        my-command foo\n    }\n}\n" +
            "table ip nat {\n    chain postrouting {\n" +
            "        type nat hook postrouting priority srcnat; policy accept;\n" +
            "        masquerade\n    }\n}");
        Equal(Join(net.Tokens(0, SyntaxHighlighting.TypeComment)), "#!/usr/sbin/nft -f", "nftables: the shebang is a comment");
        Equal(Join(net.Tokens(1, SyntaxHighlighting.TypeComment)), "# firewall", "nftables: a comment");
        Equal(Join(net.Tokens(2, SyntaxHighlighting.TypeKeyword)), "flush|ruleset", "nftables: flush ruleset");
        Equal(Join(net.Tokens(4, SyntaxHighlighting.TypeKeyword)), "table|inet|filter", "nftables: a table");
        Equal(Join(net.Tokens(5, SyntaxHighlighting.TypeKeyword)), "chain|input", "nftables: a chain");
        Equal(Join(net.Tokens(6, SyntaxHighlighting.TypeKeyword)), "type|filter|hook|input|priority|filter|policy|drop", "nftables: the chain declaration");
        Equal(Join(net.Tokens(7, SyntaxHighlighting.TypeKeyword)), "ct|state|established|related|accept", "nftables: connection tracking");
        Equal(Join(net.Tokens(8, SyntaxHighlighting.TypeKeyword)), "iifname|accept", "nftables: an input interface");
        Equal(Join(net.Tokens(8, SyntaxHighlighting.TypeString)), "\"lo\"", "nftables: the interface name");
        Equal(Join(net.Tokens(9, SyntaxHighlighting.TypeKeyword)), "tcp|dport|accept", "nftables: a port set");
        Equal(Join(net.Tokens(10, SyntaxHighlighting.TypeKeyword)), "ip|saddr|accept", "nftables: a source address");
        Equal(Join(net.Tokens(10, SyntaxHighlighting.TypeNumber)), "192.168.1.0|24", "nftables: the network and its prefix");
        Equal(Join(net.Tokens(11, SyntaxHighlighting.TypeKeyword)), "counter|drop", "nftables: a counter and drop");
        Equal(Join(net.Tokens(12, SyntaxHighlighting.TypeKeyword)), "log|prefix|level|info", "nftables: logging");
        Equal(Join(net.Tokens(14, SyntaxHighlighting.TypeKeyword)), "chain|forward", "nftables: the forward chain");
        Equal(Join(net.Tokens(15, SyntaxHighlighting.TypeKeyword)), "type|filter|hook|forward|priority|policy|drop", "nftables: the forward policy");
        Equal(Join(net.Tokens(15, SyntaxHighlighting.TypeNumber)), "0", "nftables: the priority number");
        Equal(Join(net.Tokens(16, SyntaxHighlighting.TypeKeyword)), "", "nftables: an unknown command is not a keyword");
        Equal(Join(net.Tokens(16, SyntaxHighlighting.TypeVariable)), "my-command", "nftables: ...it is a plain key");
        Equal(Join(net.Tokens(19, SyntaxHighlighting.TypeKeyword)), "table|ip|nat", "nftables: the NAT table");
        Equal(Join(net.Tokens(21, SyntaxHighlighting.TypeKeyword)), "type|nat|hook|postrouting|priority|srcnat|policy|accept", "nftables: the postrouting declaration");
        Equal(Join(net.Tokens(22, SyntaxHighlighting.TypeKeyword)), "masquerade", "nftables: masquerade");
        Check(SyntaxHighlighting.GetKeywords(L.Nftables).Contains("masquerade"), "nftables keywords contain 'masquerade'");
        Check(SyntaxHighlighting.GetKeywords(L.Nftables).Contains("established"), "nftables keywords contain 'established'");
        Check(SyntaxHighlighting.GetKeywords(L.Nftables).Contains("dport"), "nftables keywords contain 'dport'");
        Equal(Join(CommentStyle.ForLanguage(L.Nftables).Toggle(new[] { "flush ruleset" })), "# flush ruleset", "nftables comment");
    }

    private static void ModprobeChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/modprobe.d/blacklist.conf"), L.Modprobe, "a local modprobe.d file");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/lib/modprobe.d/systemd.conf"), L.Modprobe, "a file of the distribution");
        Equal(SyntaxHighlighting.DetectLanguage("/lib/modprobe.d/aliases.conf"), L.Modprobe, "a file of the merged /lib");
        Equal(SyntaxHighlighting.DetectLanguage("/run/modprobe.d/local.conf"), L.Modprobe, "a generated file");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/modprobe.conf"), L.Modprobe, "the legacy modprobe.conf");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/modprobe.conf"), L.Modprobe, "a modprobe.conf outside /etc");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/blacklist.conf"), L.Config, "an ordinary .conf is not modprobe.d");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/modules-load.d/local.conf"), L.Config, "modules-load.d is not modprobe.d");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/modprobe.d/README"), L.Plain, "the README of modprobe.d is prose");

        Highlighted net = new Highlighted(L.Modprobe,
            "# module options\nblacklist nouveau\noptions nouveau modeset=0\n" +
            "alias net-pf-10 off\n" +
            "install snd_hda_intel /sbin/modprobe --ignore-install snd_hda_intel $CMDLINE_OPTS\n" +
            "remove snd_hda_intel /sbin/modprobe -r snd_hda_intel\n" +
            "softdep nf_conntrack pre: nf_defrag_ipv4 post: nf_conntrack_netlink\n" +
            "include /etc/modprobe.d/extra.conf\nunknown-directive foo");
        Equal(Join(net.Tokens(0, SyntaxHighlighting.TypeComment)), "# module options", "modprobe: a comment");
        Equal(Join(net.Tokens(1, SyntaxHighlighting.TypeKeyword)), "blacklist", "modprobe: blacklist");
        Equal(Join(net.Tokens(2, SyntaxHighlighting.TypeKeyword)), "options", "modprobe: options");
        Equal(Join(net.Tokens(2, SyntaxHighlighting.TypeNumber)), "0", "modprobe: an option value");
        Equal(Join(net.Tokens(3, SyntaxHighlighting.TypeKeyword)), "alias|off", "modprobe: an alias turned off");
        Equal(Join(net.Tokens(4, SyntaxHighlighting.TypeKeyword)), "install", "modprobe: install");
        Equal(Join(net.Tokens(4, SyntaxHighlighting.TypeVariable)), "$CMDLINE_OPTS", "modprobe: the install command variables");
        Equal(Join(net.Tokens(5, SyntaxHighlighting.TypeKeyword)), "remove", "modprobe: remove");
        Equal(Join(net.Tokens(6, SyntaxHighlighting.TypeKeyword)), "softdep|pre|post", "modprobe: a soft dependency");
        Equal(Join(net.Tokens(7, SyntaxHighlighting.TypeKeyword)), "include", "modprobe: include");
        Equal(Join(net.Tokens(8, SyntaxHighlighting.TypeKeyword)), "", "modprobe: an unknown directive is not a keyword");
        Equal(Join(net.Tokens(8, SyntaxHighlighting.TypeVariable)), "unknown-directive", "modprobe: ...it is a plain key");
        Check(SyntaxHighlighting.GetKeywords(L.Modprobe).Contains("softdep"), "modprobe keywords contain 'softdep'");
        Check(SyntaxHighlighting.GetKeywords(L.Modprobe).Contains("blacklist"), "modprobe keywords contain 'blacklist'");
        Check(SyntaxHighlighting.GetKeywords(L.Modprobe).Contains("pre"), "modprobe keywords contain 'pre'");
        Equal(Join(CommentStyle.ForLanguage(L.Modprobe).Toggle(new[] { "blacklist nouveau" })), "# blacklist nouveau", "modprobe comment");
    }

    private static void LogrotateChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/logrotate.conf"), L.Logrotate, "the main logrotate configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/logrotate.d/nginx"), L.Logrotate, "a drop-in without an extension");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/logrotate.d/app.conf"), L.Logrotate, "a drop-in with an extension");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/etc/logrotate.d/app"), L.Logrotate, "a drop-in of a custom prefix");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/logrotate.conf"), L.Logrotate, "a logrotate.conf outside /etc");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/logrotate.d/README"), L.Plain, "the README of logrotate.d is prose");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/logrotate.d/README.md"), L.Markdown, "a Markdown README of logrotate.d is Markdown");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/logrotate.d/notes.ini"), L.Config, "an ini file of logrotate.d is not logrotate");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/logrotate.d-available/app"), L.Plain, "logrotate.d-available is not read by logrotate");

        Highlighted lr = new Highlighted(L.Logrotate,
            "# logrotate configuration\nweekly\nrotate 4\ncreate 0640 root adm\ninclude /etc/logrotate.d\n\n" +
            "/var/log/syslog {\n    daily\n    missingok\n    rotate 7\n    compress\n    delaycompress\n" +
            "    notifempty\n    create 0640 root adm\n    sharedscripts\n    postrotate\n" +
            "        /usr/lib/rsyslog/rsyslog-rotate\n    endscript\n}\n" +
            "/var/log/nginx/*.log {\n    weekly\n    rotate 52\n    maxsize 100M\n    su www-data adm\n" +
            "    olddir /var/log/nginx/old\n    dateext\n    dateformat -%Y%m%d\n    compress\n" +
            "    unknown-directive foo\n}\n");
        Equal(Join(lr.Tokens(0, SyntaxHighlighting.TypeComment)), "# logrotate configuration", "logrotate: a comment");
        Equal(Join(lr.Tokens(1, SyntaxHighlighting.TypeKeyword)), "weekly", "logrotate: a standalone directive");
        Equal(Join(lr.Tokens(2, SyntaxHighlighting.TypeKeyword)), "rotate", "logrotate: rotate");
        Equal(Join(lr.Tokens(2, SyntaxHighlighting.TypeNumber)), "4", "logrotate: the rotation count");
        Equal(Join(lr.Tokens(3, SyntaxHighlighting.TypeKeyword)), "create", "logrotate: create");
        Equal(Join(lr.Tokens(3, SyntaxHighlighting.TypeNumber)), "0640", "logrotate: the mode of the new file");
        Equal(Join(lr.Tokens(4, SyntaxHighlighting.TypeKeyword)), "include", "logrotate: include");
        Equal(Join(lr.Tokens(7, SyntaxHighlighting.TypeKeyword)), "daily", "logrotate: daily inside a block");
        Equal(Join(lr.Tokens(8, SyntaxHighlighting.TypeKeyword)), "missingok", "logrotate: missingok");
        Equal(Join(lr.Tokens(10, SyntaxHighlighting.TypeKeyword)), "compress", "logrotate: compress");
        Equal(Join(lr.Tokens(11, SyntaxHighlighting.TypeKeyword)), "delaycompress", "logrotate: delaycompress");
        Equal(Join(lr.Tokens(12, SyntaxHighlighting.TypeKeyword)), "notifempty", "logrotate: notifempty");
        Equal(Join(lr.Tokens(14, SyntaxHighlighting.TypeKeyword)), "sharedscripts", "logrotate: sharedscripts");
        Equal(Join(lr.Tokens(15, SyntaxHighlighting.TypeKeyword)), "postrotate", "logrotate: postrotate");
        Equal(Join(lr.Tokens(17, SyntaxHighlighting.TypeKeyword)), "endscript", "logrotate: endscript");
        Equal(Join(lr.Tokens(20, SyntaxHighlighting.TypeKeyword)), "weekly", "logrotate: weekly in the second block");
        Equal(Join(lr.Tokens(21, SyntaxHighlighting.TypeKeyword)), "rotate", "logrotate: rotate in the second block");
        Equal(Join(lr.Tokens(21, SyntaxHighlighting.TypeNumber)), "52", "logrotate: its rotation count");
        Equal(Join(lr.Tokens(22, SyntaxHighlighting.TypeKeyword)), "maxsize", "logrotate: maxsize");
        Equal(Join(lr.Tokens(23, SyntaxHighlighting.TypeKeyword)), "su", "logrotate: su");
        Equal(Join(lr.Tokens(24, SyntaxHighlighting.TypeKeyword)), "olddir", "logrotate: olddir");
        Equal(Join(lr.Tokens(25, SyntaxHighlighting.TypeKeyword)), "dateext", "logrotate: dateext");
        Equal(Join(lr.Tokens(26, SyntaxHighlighting.TypeKeyword)), "dateformat", "logrotate: dateformat");
        Equal(Join(lr.Tokens(28, SyntaxHighlighting.TypeKeyword)), "", "logrotate: an unknown directive is not a keyword");
        Equal(Join(lr.Tokens(28, SyntaxHighlighting.TypeVariable)), "unknown-directive", "logrotate: ...it is a plain key");
        Equal(Folds(lr), "6:18,19:29", "logrotate: the rule blocks fold on their braces");
        Equal(lr.At(6, 16).Partner?.LineNumber, 18, "the { of the first rule pairs with its }");
        Check(SyntaxHighlighting.GetKeywords(L.Logrotate).Contains("postrotate"), "logrotate keywords contain 'postrotate'");
        Check(SyntaxHighlighting.GetKeywords(L.Logrotate).Contains("dateext"), "logrotate keywords contain 'dateext'");
        Check(SyntaxHighlighting.GetKeywords(L.Logrotate).Contains("missingok"), "logrotate keywords contain 'missingok'");
        Equal(Join(CommentStyle.ForLanguage(L.Logrotate).Toggle(new[] { "rotate 4" })), "# rotate 4", "logrotate comment");
    }

    private static void SysctlChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/sysctl.conf"), L.Sysctl, "the main sysctl configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/sysctl.d/99-sysctl.conf"), L.Sysctl, "a drop-in of sysctl.d");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/lib/sysctl.d/50-default.conf"), L.Sysctl, "a drop-in of the distribution");
        Equal(SyntaxHighlighting.DetectLanguage("/run/sysctl.d/90-local.conf"), L.Sysctl, "a generated drop-in");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/sysctl.conf"), L.Sysctl, "a sysctl.conf outside /etc");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/sysctl.d/README"), L.Plain, "the README of sysctl.d is prose");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/sysctl.d/notes.ini"), L.Config, "an ini file of sysctl.d is not sysctl");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/sysctl.d-available/99-local.conf"), L.Config, "sysctl.d-available is not read by sysctl");

        Highlighted sc = new Highlighted(L.Sysctl,
            "# kernel settings\nnet.ipv4.ip_forward = 1\nnet.ipv4.conf.all.rp_filter = 1\nvm.swappiness=10\n" +
            "kernel.sysrq = 1\nfs.file-max = 2097152\nnet.core.somaxconn = 4096\n" +
            "net.ipv4.tcp_congestion_control = bbr\nnet.ipv4.ip_local_port_range = 1024 65535\n" +
            "unknown.tunable = 1\nnet.ipv4.ip_forward = 1 # routed\n");
        Equal(Join(sc.Tokens(0, SyntaxHighlighting.TypeComment)), "# kernel settings", "sysctl: a comment");
        Equal(Join(sc.Tokens(1, SyntaxHighlighting.TypeKeyword)), "net.ipv4.ip_forward", "sysctl: ip_forward");
        Equal(Join(sc.Tokens(1, SyntaxHighlighting.TypeNumber)), "1", "sysctl: its value");
        Equal(Join(sc.Tokens(2, SyntaxHighlighting.TypeKeyword)), "net.ipv4.conf.all.rp_filter", "sysctl: rp_filter");
        Equal(Join(sc.Tokens(3, SyntaxHighlighting.TypeKeyword)), "vm.swappiness", "sysctl: a key written without spaces");
        Equal(Join(sc.Tokens(3, SyntaxHighlighting.TypeNumber)), "10", "sysctl: the swappiness value");
        Equal(Join(sc.Tokens(4, SyntaxHighlighting.TypeKeyword)), "kernel.sysrq", "sysctl: sysrq");
        Equal(Join(sc.Tokens(5, SyntaxHighlighting.TypeKeyword)), "fs.file-max", "sysctl: file-max");
        Equal(Join(sc.Tokens(6, SyntaxHighlighting.TypeKeyword)), "net.core.somaxconn", "sysctl: somaxconn");
        Equal(Join(sc.Tokens(7, SyntaxHighlighting.TypeKeyword)), "net.ipv4.tcp_congestion_control|bbr", "sysctl: the congestion control algorithm");
        Equal(Join(sc.Tokens(8, SyntaxHighlighting.TypeKeyword)), "net.ipv4.ip_local_port_range", "sysctl: the port range");
        Equal(Join(sc.Tokens(8, SyntaxHighlighting.TypeNumber)), "1024|65535", "sysctl: the two ports");
        Equal(Join(sc.Tokens(9, SyntaxHighlighting.TypeKeyword)), "", "sysctl: an unknown tunable is not a keyword");
        Equal(Join(sc.Tokens(9, SyntaxHighlighting.TypeVariable)), "unknown.tunable", "sysctl: ...it is a plain key");
        Equal(Join(sc.Tokens(10, SyntaxHighlighting.TypeComment)), "# routed", "sysctl: a trailing comment");
        Check(SyntaxHighlighting.GetKeywords(L.Sysctl).Contains("net.ipv4.ip_forward"), "sysctl keywords contain 'net.ipv4.ip_forward'");
        Check(SyntaxHighlighting.GetKeywords(L.Sysctl).Contains("vm.swappiness"), "sysctl keywords contain 'vm.swappiness'");
        Check(SyntaxHighlighting.GetKeywords(L.Sysctl).Contains("bbr"), "sysctl keywords contain 'bbr'");
        Equal(Join(CommentStyle.ForLanguage(L.Sysctl).Toggle(new[] { "vm.swappiness = 10" })), "# vm.swappiness = 10", "sysctl comment");
    }

    private static void PamChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/pam.conf"), L.Pam, "the single pam.conf");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/pam.d/sshd"), L.Pam, "a service of pam.d");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/pam.d/common-auth"), L.Pam, "a common PAM stack");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/lib/pam.d/system-auth"), L.Pam, "a vendor stack of pam.d");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/pam.conf"), L.Pam, "a pam.conf outside /etc");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/pam.d/README"), L.Plain, "the README of pam.d is prose");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/pam.d/notes.txt"), L.Plain, "another text file of pam.d");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/pam.d-available/sshd"), L.Plain, "pam.d-available is not read by PAM");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/pam.d/sshd.conf"), L.Config, "a .conf file of pam.d is not a service");

        Highlighted pam = new Highlighted(L.Pam,
            "#%PAM-1.0\nauth required pam_env.so\n" +
            "auth [success=1 default=ignore] pam_unix.so nullok try_first_pass\n" +
            "account required pam_unix.so\npassword requisite pam_pwquality.so retry=3\n" +
            "session optional pam_systemd.so\nsession include system-auth\n@include common-auth\n" +
            "-auth optional pam_echo.so\nauth required unknown_module.so\n");
        Equal(Join(pam.Tokens(0, SyntaxHighlighting.TypeComment)), "#%PAM-1.0", "PAM: the version comment");
        Equal(Join(pam.Tokens(1, SyntaxHighlighting.TypeKeyword)), "auth|required|pam_env.so", "PAM: a simple rule");
        Equal(Join(pam.Tokens(2, SyntaxHighlighting.TypeKeyword)), "auth|success|default|ignore|pam_unix.so|nullok|try_first_pass", "PAM: a bracketed control and the module arguments");
        Equal(Join(pam.Tokens(2, SyntaxHighlighting.TypeNumber)), "1", "PAM: the success count");
        Equal(Join(pam.Tokens(3, SyntaxHighlighting.TypeKeyword)), "account|required|pam_unix.so", "PAM: the account stack");
        Equal(Join(pam.Tokens(4, SyntaxHighlighting.TypeKeyword)), "password|requisite|pam_pwquality.so|retry", "PAM: the password stack");
        Equal(Join(pam.Tokens(4, SyntaxHighlighting.TypeNumber)), "3", "PAM: the retry count");
        Equal(Join(pam.Tokens(5, SyntaxHighlighting.TypeKeyword)), "session|optional|pam_systemd.so", "PAM: the session stack");
        Equal(Join(pam.Tokens(6, SyntaxHighlighting.TypeKeyword)), "session|include", "PAM: an include control");
        Equal(Join(pam.Tokens(7, SyntaxHighlighting.TypeKeyword)), "include", "PAM: the @include of Debian");
        Equal(Join(pam.Tokens(8, SyntaxHighlighting.TypeKeyword)), "auth|optional|pam_echo.so", "PAM: a type written with a dash");
        Equal(Join(pam.Tokens(9, SyntaxHighlighting.TypeKeyword)), "auth|required", "PAM: an unknown module is not a keyword");
        Equal(Join(pam.Tokens(9, SyntaxHighlighting.TypeVariable)), "", "PAM: ...it stays an ordinary word");
        Equal(Folds(pam), "", "PAM rules do not fold");
        Check(SyntaxHighlighting.GetKeywords(L.Pam).Contains("pam_unix.so"), "PAM keywords contain 'pam_unix.so'");
        Check(SyntaxHighlighting.GetKeywords(L.Pam).Contains("requisite"), "PAM keywords contain 'requisite'");
        Check(SyntaxHighlighting.GetKeywords(L.Pam).Contains("try_first_pass"), "PAM keywords contain 'try_first_pass'");
        Equal(Join(CommentStyle.ForLanguage(L.Pam).Toggle(new[] { "auth required pam_env.so" })), "# auth required pam_env.so", "PAM comment");
    }

    private static void SambaChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/samba/smb.conf"), L.Samba, "the Samba configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/smb.conf"), L.Samba, "a smb.conf outside /etc");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/samba/lmhosts"), L.Plain, "lmhosts is not smb.conf");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/samba/smbusers"), L.Plain, "smbusers is not smb.conf");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/samba/smb.conf.example"), L.Plain, "the example is not the configuration");

        Highlighted smb = new Highlighted(L.Samba,
            "# Samba configuration\n[global]\n   workgroup = WORKGROUP\n   server string = Samba Server\n" +
            "   security = user\n   map to guest = bad user\n   log level = 1\n   passdb backend = tdbsam\n" +
            "   server min protocol = SMB2\n   hosts allow = 192.168.1. 127.0.0.1\n   load printers = yes\n\n" +
            "[share]\n   comment = Shared folder\n   path = /srv/share\n   browseable = yes\n   read only = no\n" +
            "   guest ok = yes\n   valid users = alice, bob\n   create mask = 0664\n   directory mask = 0775\n" +
            "   force group = users\n   vfs objects = recycle\n   unknown option = 1\n");
        Equal(Join(smb.Tokens(0, SyntaxHighlighting.TypeComment)), "# Samba configuration", "Samba: a comment");
        Equal(Join(smb.Tokens(1, SyntaxHighlighting.TypeSection)), "[global]", "Samba: the global section");
        Equal(Join(smb.Tokens(2, SyntaxHighlighting.TypeKeyword)), "workgroup", "Samba: workgroup");
        Equal(Join(smb.Tokens(3, SyntaxHighlighting.TypeKeyword)), "server string", "Samba: a two-word directive");
        Equal(Join(smb.Tokens(4, SyntaxHighlighting.TypeKeyword)), "security|user", "Samba: the security model");
        Equal(Join(smb.Tokens(5, SyntaxHighlighting.TypeKeyword)), "map to guest|bad|user", "Samba: a three-word directive and its values");
        Equal(Join(smb.Tokens(6, SyntaxHighlighting.TypeKeyword)), "log level", "Samba: log level");
        Equal(Join(smb.Tokens(6, SyntaxHighlighting.TypeNumber)), "1", "Samba: the log level number");
        Equal(Join(smb.Tokens(7, SyntaxHighlighting.TypeKeyword)), "passdb backend|tdbsam", "Samba: the password backend");
        Equal(Join(smb.Tokens(8, SyntaxHighlighting.TypeKeyword)), "server min protocol|SMB2", "Samba: the minimum protocol");
        Equal(Join(smb.Tokens(9, SyntaxHighlighting.TypeKeyword)), "hosts allow", "Samba: hosts allow");
        Equal(Join(smb.Tokens(9, SyntaxHighlighting.TypeNumber)), "192.168.1.|127.0.0.1", "Samba: the allowed networks");
        Equal(Join(smb.Tokens(10, SyntaxHighlighting.TypeKeyword)), "load printers|yes", "Samba: load printers");
        Equal(Join(smb.Tokens(12, SyntaxHighlighting.TypeSection)), "[share]", "Samba: the share section");
        Equal(Join(smb.Tokens(13, SyntaxHighlighting.TypeKeyword)), "comment", "Samba: comment");
        Equal(Join(smb.Tokens(14, SyntaxHighlighting.TypeKeyword)), "path", "Samba: path");
        Equal(Join(smb.Tokens(15, SyntaxHighlighting.TypeKeyword)), "browseable|yes", "Samba: browseable");
        Equal(Join(smb.Tokens(16, SyntaxHighlighting.TypeKeyword)), "read only|no", "Samba: read only");
        Equal(Join(smb.Tokens(17, SyntaxHighlighting.TypeKeyword)), "guest ok|yes", "Samba: guest ok");
        Equal(Join(smb.Tokens(18, SyntaxHighlighting.TypeKeyword)), "valid users", "Samba: valid users");
        Equal(Join(smb.Tokens(19, SyntaxHighlighting.TypeKeyword)), "create mask", "Samba: the create mask");
        Equal(Join(smb.Tokens(19, SyntaxHighlighting.TypeNumber)), "0664", "Samba: the file mode");
        Equal(Join(smb.Tokens(20, SyntaxHighlighting.TypeKeyword)), "directory mask", "Samba: the directory mask");
        Equal(Join(smb.Tokens(20, SyntaxHighlighting.TypeNumber)), "0775", "Samba: the directory mode");
        Equal(Join(smb.Tokens(21, SyntaxHighlighting.TypeKeyword)), "force group", "Samba: force group");
        Equal(Join(smb.Tokens(22, SyntaxHighlighting.TypeKeyword)), "vfs objects|recycle", "Samba: the VFS objects");
        Equal(Join(smb.Tokens(23, SyntaxHighlighting.TypeKeyword)), "", "Samba: an unknown directive is not a keyword");
        Equal(Join(smb.Tokens(23, SyntaxHighlighting.TypeVariable)), "unknown", "Samba: ...it is a plain key");
        Equal(Folds(smb), "", "Samba parameters do not fold");
        Check(SyntaxHighlighting.GetKeywords(L.Samba).Contains("map to guest"), "Samba keywords contain the phrase 'map to guest'");
        Check(SyntaxHighlighting.GetKeywords(L.Samba).Contains("workgroup"), "Samba keywords contain 'workgroup'");
        Check(SyntaxHighlighting.GetKeywords(L.Samba).Contains("tdbsam"), "Samba keywords contain 'tdbsam'");
        Equal(Join(CommentStyle.ForLanguage(L.Samba).Toggle(new[] { "workgroup = WORKGROUP" })), "# workgroup = WORKGROUP", "Samba comment");
    }

    private static void PostfixChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/postfix/main.cf"), L.Postfix, "the main Postfix configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/postfix/master.cf"), L.Postfix, "the service map of Postfix");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/postfix/dynamicmaps.cf"), L.Postfix, "a drop-in of /etc/postfix");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/main.cf"), L.Postfix, "a main.cf outside /etc");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/master.cf"), L.Postfix, "a master.cf outside /etc");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/postfix/header_checks"), L.Plain, "a lookup table of Postfix is not a .cf file");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/postfix/sasl/smtpd.conf"), L.Config, "the Cyrus SASL configuration is not Postfix");

        Highlighted main = new Highlighted(L.Postfix,
            "# Postfix main configuration\nmyhostname = mail.example.com\nmydomain = example.com\n" +
            "myorigin = $mydomain\ninet_interfaces = all\ninet_protocols = ipv4\n" +
            "mydestination = $myhostname, localhost.$mydomain, localhost\n" +
            "mynetworks = 127.0.0.0/8, 192.168.1.0/24\nrelayhost = [smtp.example.com]:587\n" +
            "smtpd_banner = $myhostname ESMTP $mail_name\nsmtpd_tls_security_level = may\n" +
            "smtpd_tls_cert_file = /etc/ssl/certs/mail.pem\nsmtpd_sasl_auth_enable = yes\n" +
            "smtpd_relay_restrictions = permit_mynetworks, permit_sasl_authenticated, reject_unauth_destination\n" +
            "message_size_limit = 10240000\nmailbox_size_limit = 0\nhome_mailbox = Maildir/\n" +
            "compatibility_level = 3.6\nunknown_parameter = 1\n");
        Equal(Join(main.Tokens(0, SyntaxHighlighting.TypeComment)), "# Postfix main configuration", "Postfix: a comment");
        Equal(Join(main.Tokens(1, SyntaxHighlighting.TypeKeyword)), "myhostname", "Postfix: myhostname");
        Equal(Join(main.Tokens(3, SyntaxHighlighting.TypeKeyword)), "myorigin", "Postfix: myorigin");
        Equal(Join(main.Tokens(3, SyntaxHighlighting.TypeVariable)), "$mydomain", "Postfix: a variable value");
        Equal(Join(main.Tokens(4, SyntaxHighlighting.TypeKeyword)), "inet_interfaces|all", "Postfix: the interfaces");
        Equal(Join(main.Tokens(5, SyntaxHighlighting.TypeKeyword)), "inet_protocols|ipv4", "Postfix: the protocols");
        Equal(Join(main.Tokens(6, SyntaxHighlighting.TypeKeyword)), "mydestination", "Postfix: mydestination");
        Equal(Join(main.Tokens(6, SyntaxHighlighting.TypeVariable)), "$myhostname|$mydomain", "Postfix: the destination variables");
        Equal(Join(main.Tokens(7, SyntaxHighlighting.TypeKeyword)), "mynetworks", "Postfix: mynetworks");
        Equal(Join(main.Tokens(7, SyntaxHighlighting.TypeNumber)), "127.0.0.0|8|192.168.1.0|24", "Postfix: the trusted networks");
        Equal(Join(main.Tokens(8, SyntaxHighlighting.TypeKeyword)), "relayhost", "Postfix: the relay host");
        Equal(Join(main.Tokens(8, SyntaxHighlighting.TypeNumber)), "587", "Postfix: the relay port");
        Equal(Join(main.Tokens(10, SyntaxHighlighting.TypeKeyword)), "smtpd_tls_security_level|may", "Postfix: the TLS level");
        Equal(Join(main.Tokens(12, SyntaxHighlighting.TypeKeyword)), "smtpd_sasl_auth_enable|yes", "Postfix: SASL authentication");
        Equal(Join(main.Tokens(13, SyntaxHighlighting.TypeKeyword)), "smtpd_relay_restrictions|permit_mynetworks|permit_sasl_authenticated|reject_unauth_destination", "Postfix: the relay restrictions");
        Equal(Join(main.Tokens(14, SyntaxHighlighting.TypeNumber)), "10240000", "Postfix: the message size limit");
        Equal(Join(main.Tokens(17, SyntaxHighlighting.TypeNumber)), "3.6", "Postfix: the compatibility level");
        Equal(Join(main.Tokens(18, SyntaxHighlighting.TypeKeyword)), "", "Postfix: an unknown parameter is not a keyword");
        Equal(Join(main.Tokens(18, SyntaxHighlighting.TypeVariable)), "unknown_parameter", "Postfix: ...it is a plain key");

        Highlighted master = new Highlighted(L.Postfix,
            "# Postfix master configuration\n" +
            "smtp      inet  n       -       y       -       -       smtpd\n" +
            "smtps     inet  n       -       y       -       -       smtpd -o smtpd_tls_wrappermode=yes\n" +
            "submission inet n       -       y       -       -       smtpd -o smtpd_tls_security_level=may\n" +
            "pickup    unix  n       -       y       60      1       pickup\n" +
            "cleanup   unix  n       -       y       -       0       cleanup\n" +
            "qmgr      unix  n       -       n       300     1       qmgr\n");
        Equal(Join(master.Tokens(1, SyntaxHighlighting.TypeKeyword)), "smtp|inet|n|y|smtpd", "Postfix: an smtp service");
        Equal(Join(master.Tokens(2, SyntaxHighlighting.TypeKeyword)), "smtps|inet|n|y|smtpd|yes", "Postfix: smtps with an override");
        Equal(Join(master.Tokens(3, SyntaxHighlighting.TypeKeyword)), "submission|inet|n|y|smtpd|may", "Postfix: the submission service");
        Equal(Join(master.Tokens(4, SyntaxHighlighting.TypeKeyword)), "pickup|unix|n|y|pickup", "Postfix: the pickup service");
        Equal(Join(master.Tokens(4, SyntaxHighlighting.TypeNumber)), "60|1", "Postfix: the wakeup and process limits");
        Equal(Join(master.Tokens(6, SyntaxHighlighting.TypeKeyword)), "qmgr|unix|n|n|qmgr", "Postfix: the qmgr service");
        Equal(Join(master.Tokens(6, SyntaxHighlighting.TypeNumber)), "300|1", "Postfix: the qmgr limits");
        Check(SyntaxHighlighting.GetKeywords(L.Postfix).Contains("myhostname"), "Postfix keywords contain 'myhostname'");
        Check(SyntaxHighlighting.GetKeywords(L.Postfix).Contains("smtpd_relay_restrictions"), "Postfix keywords contain 'smtpd_relay_restrictions'");
        Check(SyntaxHighlighting.GetKeywords(L.Postfix).Contains("permit_mynetworks"), "Postfix keywords contain 'permit_mynetworks'");
        Equal(Join(CommentStyle.ForLanguage(L.Postfix).Toggle(new[] { "myhostname = mail.example.com" })), "# myhostname = mail.example.com", "Postfix comment");
    }

    private static void OpenVpnChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/openvpn/server.conf"), L.OpenVpn, "a server profile of /etc/openvpn");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/openvpn/client/home.conf"), L.OpenVpn, "a client profile of /etc/openvpn");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/openvpn/server/site.conf"), L.OpenVpn, "a profile of the server directory");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/mullvad.ovpn"), L.OpenVpn, "an .ovpn profile anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/openvpn.conf"), L.OpenVpn, "openvpn.conf anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/openvpn/README"), L.Plain, "the README of /etc/openvpn is prose");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/openvpn/update-resolv-conf"), L.Plain, "a helper script is not a profile");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/server.conf"), L.Config, "a server.conf elsewhere is a plain config");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/work.ovpn.backup"), L.Plain, "a backup of a profile is not OpenVPN");

        Highlighted vpn = new Highlighted(L.OpenVpn,
            "# OpenVPN server profile\nport 1194\nproto udp\ndev tun\nca /etc/openvpn/ca.crt\n" +
            "cert /etc/openvpn/server.crt\nkey /etc/openvpn/server.key   # keep secret\n" +
            "dh none\ntls-crypt /etc/openvpn/ta.key\nserver 10.8.0.0 255.255.255.0\n" +
            "push \"redirect-gateway def1 bypass-dhcp\"\npush \"dhcp-option DNS 10.8.0.1\"\n" +
            "keepalive 10 120\ncipher AES-256-GCM\ndata-ciphers AES-256-GCM:AES-128-GCM\n" +
            "auth SHA256\nuser nobody\npersist-key\npersist-tun\nstatus /var/log/openvpn-status.log\n" +
            "verb 3\nclient\nremote vpn.example.com 1194\nremote-cert-tls server\n" +
            "resolv-retry infinite\nnobind\ntls-version-min 1.2\nverify-client-cert require\n" +
            "unknown-option 1\n; a semicolon comment\n");
        Equal(Join(vpn.Tokens(0, SyntaxHighlighting.TypeComment)), "# OpenVPN server profile", "OpenVPN: a comment");
        Equal(Join(vpn.Tokens(1, SyntaxHighlighting.TypeKeyword)), "port", "OpenVPN: port");
        Equal(Join(vpn.Tokens(1, SyntaxHighlighting.TypeNumber)), "1194", "OpenVPN: the port number");
        Equal(Join(vpn.Tokens(2, SyntaxHighlighting.TypeKeyword)), "proto|udp", "OpenVPN: the transport");
        Equal(Join(vpn.Tokens(3, SyntaxHighlighting.TypeKeyword)), "dev|tun", "OpenVPN: the device");
        Equal(Join(vpn.Tokens(4, SyntaxHighlighting.TypeKeyword)), "ca", "OpenVPN: the CA file");
        Equal(Join(vpn.Tokens(6, SyntaxHighlighting.TypeKeyword)), "key", "OpenVPN: the key file");
        Equal(Join(vpn.Tokens(6, SyntaxHighlighting.TypeComment)), "# keep secret", "OpenVPN: a trailing comment");
        Equal(Join(vpn.Tokens(7, SyntaxHighlighting.TypeKeyword)), "dh|none", "OpenVPN: no Diffie-Hellman parameters");
        Equal(Join(vpn.Tokens(8, SyntaxHighlighting.TypeKeyword)), "tls-crypt", "OpenVPN: tls-crypt");
        Equal(Join(vpn.Tokens(9, SyntaxHighlighting.TypeKeyword)), "server", "OpenVPN: the server directive");
        Equal(Join(vpn.Tokens(9, SyntaxHighlighting.TypeNumber)), "10.8.0.0|255.255.255.0", "OpenVPN: the VPN network");
        Equal(Join(vpn.Tokens(10, SyntaxHighlighting.TypeKeyword)), "push", "OpenVPN: a push");
        Equal(Join(vpn.Tokens(10, SyntaxHighlighting.TypeString)), "\"redirect-gateway def1 bypass-dhcp\"", "OpenVPN: the pushed route");
        Equal(Join(vpn.Tokens(12, SyntaxHighlighting.TypeKeyword)), "keepalive", "OpenVPN: keepalive");
        Equal(Join(vpn.Tokens(12, SyntaxHighlighting.TypeNumber)), "10|120", "OpenVPN: the keepalive timers");
        Equal(Join(vpn.Tokens(13, SyntaxHighlighting.TypeKeyword)), "cipher|AES-256-GCM", "OpenVPN: the cipher");
        Equal(Join(vpn.Tokens(14, SyntaxHighlighting.TypeKeyword)), "data-ciphers|AES-256-GCM|AES-128-GCM", "OpenVPN: the cipher list");
        Equal(Join(vpn.Tokens(15, SyntaxHighlighting.TypeKeyword)), "auth|SHA256", "OpenVPN: the digest");
        Equal(Join(vpn.Tokens(17, SyntaxHighlighting.TypeKeyword)), "persist-key", "OpenVPN: a directive without a value");
        Equal(Join(vpn.Tokens(18, SyntaxHighlighting.TypeKeyword)), "persist-tun", "OpenVPN: another directive without a value");
        Equal(Join(vpn.Tokens(20, SyntaxHighlighting.TypeKeyword)), "verb", "OpenVPN: verb");
        Equal(Join(vpn.Tokens(20, SyntaxHighlighting.TypeNumber)), "3", "OpenVPN: the verbosity");
        Equal(Join(vpn.Tokens(21, SyntaxHighlighting.TypeKeyword)), "client", "OpenVPN: the client mode");
        Equal(Join(vpn.Tokens(22, SyntaxHighlighting.TypeKeyword)), "remote", "OpenVPN: a remote");
        Equal(Join(vpn.Tokens(22, SyntaxHighlighting.TypeNumber)), "1194", "OpenVPN: the remote port");
        Equal(Join(vpn.Tokens(23, SyntaxHighlighting.TypeKeyword)), "remote-cert-tls|server", "OpenVPN: the certificate role");
        Equal(Join(vpn.Tokens(24, SyntaxHighlighting.TypeKeyword)), "resolv-retry|infinite", "OpenVPN: the resolver retries");
        Equal(Join(vpn.Tokens(26, SyntaxHighlighting.TypeKeyword)), "tls-version-min", "OpenVPN: the minimum TLS version");
        Equal(Join(vpn.Tokens(27, SyntaxHighlighting.TypeKeyword)), "verify-client-cert|require", "OpenVPN: the client certificate check");
        Equal(Join(vpn.Tokens(28, SyntaxHighlighting.TypeKeyword)), "", "OpenVPN: an unknown option is not a keyword");
        Equal(Join(vpn.Tokens(28, SyntaxHighlighting.TypeVariable)), "unknown-option", "OpenVPN: ...it is a plain key");
        Equal(Join(vpn.Tokens(29, SyntaxHighlighting.TypeComment)), "; a semicolon comment", "OpenVPN: a semicolon comment");
        Check(SyntaxHighlighting.GetKeywords(L.OpenVpn).Contains("tls-crypt"), "OpenVPN keywords contain 'tls-crypt'");
        Check(SyntaxHighlighting.GetKeywords(L.OpenVpn).Contains("persist-key"), "OpenVPN keywords contain 'persist-key'");
        Check(SyntaxHighlighting.GetKeywords(L.OpenVpn).Contains("AES-256-GCM"), "OpenVPN keywords contain 'AES-256-GCM'");
        Equal(Join(CommentStyle.ForLanguage(L.OpenVpn).Toggle(new[] { "port 1194" })), "# port 1194", "OpenVPN comment");
    }

    private static void NetplanChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/netplan/01-netcfg.yaml"), L.Netplan, "a Netplan configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/netplan/50-cloud-init.yml"), L.Netplan, "a Netplan file with .yml");
        Equal(SyntaxHighlighting.DetectLanguage("/run/netplan/10-netplan.yaml"), L.Netplan, "a generated Netplan file");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/netplan.yaml"), L.Netplan, "netplan.yaml by its name");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/netplan/README"), L.Plain, "the README of /etc/netplan is prose");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/netplan/notes.txt"), L.Plain, "a text file is not Netplan");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/netplan/backup.yaml.bak"), L.Plain, "a backup is not Netplan");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/network.yaml"), L.Yaml, "an ordinary network.yaml stays YAML");

        Highlighted net = new Highlighted(L.Netplan,
            "# Netplan configuration\nnetwork:\n  version: 2\n  renderer: networkd\n  ethernets:\n    enp3s0:\n" +
            "      dhcp4: true\n      addresses:\n        - 192.168.1.10/24\n      gateway4: 192.168.1.1\n" +
            "      nameservers:\n        addresses: [9.9.9.9, 149.112.112.112]\n        search: [example.com]\n" +
            "  wifis:\n    wl0:\n      dhcp4: no\n      access-points:\n        \"home\":\n" +
            "          password: \"secret\"\n      routes:\n        - to: default\n          via: 192.168.1.1\n" +
            "          metric: 100\n      unknown-setting: 1\n");
        Equal(Join(net.Tokens(0, SyntaxHighlighting.TypeComment)), "# Netplan configuration", "Netplan: a comment");
        Equal(Join(net.Tokens(1, SyntaxHighlighting.TypeKeyword)), "network", "Netplan: the network block");
        Equal(Join(net.Tokens(2, SyntaxHighlighting.TypeKeyword)), "version", "Netplan: the version");
        Equal(Join(net.Tokens(2, SyntaxHighlighting.TypeNumber)), "2", "Netplan: the version number");
        Equal(Join(net.Tokens(3, SyntaxHighlighting.TypeKeyword)), "renderer|networkd", "Netplan: the renderer");
        Equal(Join(net.Tokens(4, SyntaxHighlighting.TypeKeyword)), "ethernets", "Netplan: the ethernet devices");
        Equal(Join(net.Tokens(5, SyntaxHighlighting.TypeVariable)), "enp3s0", "Netplan: an interface name stays an ordinary key");
        Equal(Join(net.Tokens(6, SyntaxHighlighting.TypeKeyword)), "dhcp4|true", "Netplan: DHCP");
        Equal(Join(net.Tokens(7, SyntaxHighlighting.TypeKeyword)), "addresses", "Netplan: the addresses");
        Equal(Join(net.Tokens(8, SyntaxHighlighting.TypeNumber)), "192.168.1.10|24", "Netplan: an address with its prefix");
        Equal(Join(net.Tokens(9, SyntaxHighlighting.TypeKeyword)), "gateway4", "Netplan: the gateway");
        Equal(Join(net.Tokens(9, SyntaxHighlighting.TypeNumber)), "192.168.1.1", "Netplan: the gateway address");
        Equal(Join(net.Tokens(10, SyntaxHighlighting.TypeKeyword)), "nameservers", "Netplan: the name servers");
        Equal(Join(net.Tokens(11, SyntaxHighlighting.TypeKeyword)), "addresses", "Netplan: the DNS addresses");
        Equal(Join(net.Tokens(11, SyntaxHighlighting.TypeNumber)), "9.9.9.9|149.112.112.112", "Netplan: the DNS servers");
        Equal(Join(net.Tokens(12, SyntaxHighlighting.TypeKeyword)), "search", "Netplan: the search domains");
        Equal(Join(net.Tokens(13, SyntaxHighlighting.TypeKeyword)), "wifis", "Netplan: the Wi-Fi devices");
        Equal(Join(net.Tokens(15, SyntaxHighlighting.TypeKeyword)), "dhcp4|no", "Netplan: DHCP off");
        Equal(Join(net.Tokens(16, SyntaxHighlighting.TypeKeyword)), "access-points", "Netplan: the access points");
        Equal(Join(net.Tokens(17, SyntaxHighlighting.TypeString)), "\"home\"", "Netplan: the SSID");
        Equal(Join(net.Tokens(18, SyntaxHighlighting.TypeKeyword)), "password", "Netplan: the password");
        Equal(Join(net.Tokens(18, SyntaxHighlighting.TypeString)), "\"secret\"", "Netplan: the quoted password");
        Equal(Join(net.Tokens(19, SyntaxHighlighting.TypeKeyword)), "routes", "Netplan: the routes");
        Equal(Join(net.Tokens(20, SyntaxHighlighting.TypeKeyword)), "to|default", "Netplan: the default route");
        Equal(Join(net.Tokens(21, SyntaxHighlighting.TypeKeyword)), "via", "Netplan: the route gateway");
        Equal(Join(net.Tokens(22, SyntaxHighlighting.TypeKeyword)), "metric", "Netplan: the route metric");
        Equal(Join(net.Tokens(22, SyntaxHighlighting.TypeNumber)), "100", "Netplan: the metric value");
        Equal(Join(net.Tokens(23, SyntaxHighlighting.TypeKeyword)), "", "Netplan: an unknown setting is not a keyword");
        Equal(Join(net.Tokens(23, SyntaxHighlighting.TypeVariable)), "unknown-setting", "Netplan: ...it is a plain key");
        Check(Folds(net).Length > 0, "Netplan blocks fold");
        Check(SyntaxHighlighting.GetKeywords(L.Netplan).Contains("renderer"), "Netplan keywords contain 'renderer'");
        Check(SyntaxHighlighting.GetKeywords(L.Netplan).Contains("access-points"), "Netplan keywords contain 'access-points'");
        Check(SyntaxHighlighting.GetKeywords(L.Netplan).Contains("networkd"), "Netplan keywords contain 'networkd'");
        Equal(Join(CommentStyle.ForLanguage(L.Netplan).Toggle(new[] { "  dhcp4: true" })), "  # dhcp4: true", "Netplan comment");
    }

    private static void CloudInitChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/cloud/cloud.cfg"), L.CloudInit, "the cloud-init configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/cloud/cloud.cfg.d/99-fixes.cfg"), L.CloudInit, "a drop-in of cloud.cfg.d");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/lib/cloud/cloud.cfg.d/05-logging.cfg"), L.CloudInit, "a vendor drop-in");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/cloud/cloud-config"), L.CloudInit, "a cloud-config seed");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/cloud.cfg"), L.CloudInit, "cloud.cfg anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/cloud/user-data"), L.CloudInit, "a user-data file under /cloud/");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/user-data"), L.Plain, "a user-data outside /cloud/ without a marker is plain");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/user-data", "#cloud-config"), L.CloudInit, "the #cloud-config marker");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/notes.yaml", "#cloud-config"), L.CloudInit, "the marker wins over the YAML name");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/notes.yaml", "# just a comment"), L.Yaml, "another comment leaves YAML");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/cloud/README"), L.Plain, "the README of /etc/cloud is prose");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/cloud.cfg.bak"), L.Plain, "a backup of cloud.cfg is not cloud-init");

        Highlighted init = new Highlighted(L.CloudInit,
            "#cloud-config\nversion: 1\nhostname: web-01\nfqdn: web-01.example.com\nmanage_etc_hosts: true\n" +
            "users:\n  - name: deploy\n    gecos: Deploy user\n    groups: [sudo, docker]\n    shell: /bin/bash\n" +
            "    sudo: [\"ALL=(ALL) NOPASSWD:ALL\"]\n    ssh_authorized_keys:\n      - ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample\n" +
            "ssh_pwauth: false\ndisable_root: true\nchpasswd:\n  expire: false\n  list: |\n    root:linux\n" +
            "package_update: true\npackages:\n  - nginx\n  - curl\nwrite_files:\n  - path: /etc/motd\n" +
            "    content: |\n      Welcome to the server\n    owner: root:root\n    permissions: '0644'\n" +
            "runcmd:\n  - [systemctl, enable, --now, nginx]\n  - echo done > /tmp/done\n" +
            "power_state:\n  mode: reboot\n  timeout: 30\n  condition: test -f /var/run/reboot-required\n" +
            "unknown_module: 1\n");
        Equal(Join(init.Tokens(0, SyntaxHighlighting.TypeComment)), "#cloud-config", "cloud-init: the marker");
        Equal(Join(init.Tokens(1, SyntaxHighlighting.TypeKeyword)), "version", "cloud-init: the version");
        Equal(Join(init.Tokens(1, SyntaxHighlighting.TypeNumber)), "1", "cloud-init: the version number");
        Equal(Join(init.Tokens(2, SyntaxHighlighting.TypeKeyword)), "hostname", "cloud-init: the hostname");
        Equal(Join(init.Tokens(3, SyntaxHighlighting.TypeKeyword)), "fqdn", "cloud-init: the FQDN");
        Equal(Join(init.Tokens(4, SyntaxHighlighting.TypeKeyword)), "manage_etc_hosts|true", "cloud-init: /etc/hosts");
        Equal(Join(init.Tokens(5, SyntaxHighlighting.TypeKeyword)), "users", "cloud-init: the users");
        Equal(Join(init.Tokens(6, SyntaxHighlighting.TypeKeyword)), "name", "cloud-init: the name of a user");
        Equal(Join(init.Tokens(7, SyntaxHighlighting.TypeKeyword)), "gecos", "cloud-init: gecos");
        Equal(Join(init.Tokens(8, SyntaxHighlighting.TypeKeyword)), "groups", "cloud-init: the groups of a user");
        Equal(Join(init.Tokens(9, SyntaxHighlighting.TypeKeyword)), "shell", "cloud-init: the shell of a user");
        Equal(Join(init.Tokens(11, SyntaxHighlighting.TypeKeyword)), "ssh_authorized_keys", "cloud-init: the authorized keys");
        Equal(Join(init.Tokens(13, SyntaxHighlighting.TypeKeyword)), "ssh_pwauth|false", "cloud-init: password authentication");
        Equal(Join(init.Tokens(14, SyntaxHighlighting.TypeKeyword)), "disable_root|true", "cloud-init: the root account");
        Equal(Join(init.Tokens(15, SyntaxHighlighting.TypeKeyword)), "chpasswd", "cloud-init: the passwords");
        Equal(Join(init.Tokens(16, SyntaxHighlighting.TypeKeyword)), "expire|false", "cloud-init: the password expiry");
        Equal(Join(init.Tokens(17, SyntaxHighlighting.TypeKeyword)), "list", "cloud-init: the password list");
        Equal(Join(init.Tokens(18, SyntaxHighlighting.TypeString)), "root:linux", "cloud-init: the text of the block scalar");
        Equal(Join(init.Tokens(19, SyntaxHighlighting.TypeKeyword)), "package_update|true", "cloud-init: the package update");
        Equal(Join(init.Tokens(20, SyntaxHighlighting.TypeKeyword)), "packages", "cloud-init: the package list");
        Equal(Join(init.Tokens(23, SyntaxHighlighting.TypeKeyword)), "write_files", "cloud-init: the files to write");
        Equal(Join(init.Tokens(24, SyntaxHighlighting.TypeKeyword)), "path", "cloud-init: the path of a file");
        Equal(Join(init.Tokens(25, SyntaxHighlighting.TypeKeyword)), "content", "cloud-init: the content of a file");
        Equal(Join(init.Tokens(26, SyntaxHighlighting.TypeString)), "Welcome to the server", "cloud-init: the content block");
        Equal(Join(init.Tokens(27, SyntaxHighlighting.TypeKeyword)), "owner", "cloud-init: the owner of a file");
        Equal(Join(init.Tokens(28, SyntaxHighlighting.TypeKeyword)), "permissions", "cloud-init: the permissions of a file");
        Equal(Join(init.Tokens(28, SyntaxHighlighting.TypeString)), "'0644'", "cloud-init: the mode of a file");
        Equal(Join(init.Tokens(29, SyntaxHighlighting.TypeKeyword)), "runcmd", "cloud-init: the commands");
        Equal(Join(init.Tokens(32, SyntaxHighlighting.TypeKeyword)), "power_state", "cloud-init: the power state");
        Equal(Join(init.Tokens(33, SyntaxHighlighting.TypeKeyword)), "mode|reboot", "cloud-init: the reboot mode");
        Equal(Join(init.Tokens(34, SyntaxHighlighting.TypeKeyword)), "timeout", "cloud-init: the power timeout");
        Equal(Join(init.Tokens(34, SyntaxHighlighting.TypeNumber)), "30", "cloud-init: the timeout value");
        Equal(Join(init.Tokens(35, SyntaxHighlighting.TypeKeyword)), "condition", "cloud-init: the power condition");
        Equal(Join(init.Tokens(36, SyntaxHighlighting.TypeKeyword)), "", "cloud-init: an unknown module is not a keyword");
        Equal(Join(init.Tokens(36, SyntaxHighlighting.TypeVariable)), "unknown_module", "cloud-init: ...it is a plain key");
        Check(Folds(init).Length > 0, "cloud-init blocks fold");
        Check(SyntaxHighlighting.GetKeywords(L.CloudInit).Contains("write_files"), "cloud-init keywords contain 'write_files'");
        Check(SyntaxHighlighting.GetKeywords(L.CloudInit).Contains("runcmd"), "cloud-init keywords contain 'runcmd'");
        Check(SyntaxHighlighting.GetKeywords(L.CloudInit).Contains("reboot"), "cloud-init keywords contain 'reboot'");
        Equal(Join(CommentStyle.ForLanguage(L.CloudInit).Toggle(new[] { "hostname: web-01" })), "# hostname: web-01", "cloud-init comment");
    }

    private static void PrometheusChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/prometheus/prometheus.yml"), L.Prometheus, "the main Prometheus configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/prometheus/rules/node.rules.yml"), L.Prometheus, "a rule file of /etc/prometheus");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/monitoring/prometheus.yml"), L.Prometheus, "prometheus.yml anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/monitoring/prometheus.yaml"), L.Prometheus, "prometheus.yaml anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/monitoring/prometheus.yml.bak"), L.Plain, "a backup of the configuration is not Prometheus");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/prometheus/README"), L.Plain, "the README of /etc/prometheus is prose");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/rules.yml"), L.Yaml, "a rules.yml outside the Prometheus tree stays YAML");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/app.yml"), L.Yaml, "an ordinary .yml stays YAML");

        Highlighted prom = new Highlighted(L.Prometheus,
            "# Prometheus configuration\nglobal:\n  scrape_interval: 15s\n  evaluation_interval: 15s\n" +
            "  external_labels:\n    monitor: prod\nrule_files:\n  - /etc/prometheus/rules/*.yml\n" +
            "scrape_configs:\n  - job_name: prometheus\n    static_configs:\n" +
            "      - targets: ['localhost:9090']\n  - job_name: node\n    scrape_interval: 30s\n" +
            "    metrics_path: /metrics\n    scheme: https\n    basic_auth:\n      username: monitor\n" +
            "      password: secret\n    static_configs:\n      - targets: ['node1:9100', 'node2:9100']\n" +
            "        labels:\n          env: prod\n    relabel_configs:\n      - source_labels: [__address__]\n" +
            "        target_label: instance\n        regex: '(.*):9100'\n        replacement: '$1'\n" +
            "alerting:\n  alertmanagers:\n    - static_configs:\n        - targets: ['localhost:9093']\n" +
            "remote_write:\n  - url: https://prometheus.example.com/api/v1/write\n    queue_config:\n" +
            "      max_samples_per_send: 10000\n    tls_config:\n      insecure_skip_verify: false\n" +
            "    authorization:\n      type: Bearer\n      credentials: token\nunknown_field: 1\n");
        Equal(Join(prom.Tokens(0, SyntaxHighlighting.TypeComment)), "# Prometheus configuration", "Prometheus: a comment");
        Equal(Join(prom.Tokens(1, SyntaxHighlighting.TypeKeyword)), "global", "Prometheus: the global block");
        Equal(Join(prom.Tokens(2, SyntaxHighlighting.TypeKeyword)), "scrape_interval", "Prometheus: the scrape interval");
        Equal(Join(prom.Tokens(3, SyntaxHighlighting.TypeKeyword)), "evaluation_interval", "Prometheus: the evaluation interval");
        Equal(Join(prom.Tokens(4, SyntaxHighlighting.TypeKeyword)), "external_labels", "Prometheus: the external labels");
        Equal(Join(prom.Tokens(5, SyntaxHighlighting.TypeVariable)), "monitor", "Prometheus: a label name stays an ordinary key");
        Equal(Join(prom.Tokens(6, SyntaxHighlighting.TypeKeyword)), "rule_files", "Prometheus: the rule files");
        Equal(Join(prom.Tokens(8, SyntaxHighlighting.TypeKeyword)), "scrape_configs", "Prometheus: the scrape configurations");
        Equal(Join(prom.Tokens(9, SyntaxHighlighting.TypeKeyword)), "job_name", "Prometheus: the name of a job");
        Equal(Join(prom.Tokens(10, SyntaxHighlighting.TypeKeyword)), "static_configs", "Prometheus: static targets");
        Equal(Join(prom.Tokens(11, SyntaxHighlighting.TypeKeyword)), "targets", "Prometheus: the targets");
        Equal(Join(prom.Tokens(12, SyntaxHighlighting.TypeKeyword)), "job_name", "Prometheus: the node job");
        Equal(Join(prom.Tokens(15, SyntaxHighlighting.TypeKeyword)), "scheme|https", "Prometheus: the scheme");
        Equal(Join(prom.Tokens(16, SyntaxHighlighting.TypeKeyword)), "basic_auth", "Prometheus: basic authentication");
        Equal(Join(prom.Tokens(17, SyntaxHighlighting.TypeKeyword)), "username", "Prometheus: the user name");
        Equal(Join(prom.Tokens(18, SyntaxHighlighting.TypeKeyword)), "password", "Prometheus: the password");
        Equal(Join(prom.Tokens(20, SyntaxHighlighting.TypeKeyword)), "targets", "Prometheus: the node targets");
        Equal(Join(prom.Tokens(21, SyntaxHighlighting.TypeKeyword)), "labels", "Prometheus: the labels of a target");
        Equal(Join(prom.Tokens(22, SyntaxHighlighting.TypeVariable)), "env", "Prometheus: a label of a target stays an ordinary key");
        Equal(Join(prom.Tokens(23, SyntaxHighlighting.TypeKeyword)), "relabel_configs", "Prometheus: the relabeling rules");
        Equal(Join(prom.Tokens(24, SyntaxHighlighting.TypeKeyword)), "source_labels", "Prometheus: source_labels");
        Equal(Join(prom.Tokens(25, SyntaxHighlighting.TypeKeyword)), "target_label", "Prometheus: target_label");
        Equal(Join(prom.Tokens(26, SyntaxHighlighting.TypeKeyword)), "regex", "Prometheus: the regular expression");
        Equal(Join(prom.Tokens(27, SyntaxHighlighting.TypeKeyword)), "replacement", "Prometheus: the replacement");
        Equal(Join(prom.Tokens(28, SyntaxHighlighting.TypeKeyword)), "alerting", "Prometheus: the alerting block");
        Equal(Join(prom.Tokens(29, SyntaxHighlighting.TypeKeyword)), "alertmanagers", "Prometheus: the alert managers");
        Equal(Join(prom.Tokens(32, SyntaxHighlighting.TypeKeyword)), "remote_write", "Prometheus: the remote write");
        Equal(Join(prom.Tokens(33, SyntaxHighlighting.TypeKeyword)), "url|https", "Prometheus: the remote write URL");
        Equal(Join(prom.Tokens(34, SyntaxHighlighting.TypeKeyword)), "queue_config", "Prometheus: the queue configuration");
        Equal(Join(prom.Tokens(35, SyntaxHighlighting.TypeKeyword)), "max_samples_per_send", "Prometheus: the batch size");
        Equal(Join(prom.Tokens(35, SyntaxHighlighting.TypeNumber)), "10000", "Prometheus: the batch size value");
        Equal(Join(prom.Tokens(36, SyntaxHighlighting.TypeKeyword)), "tls_config", "Prometheus: the TLS configuration");
        Equal(Join(prom.Tokens(37, SyntaxHighlighting.TypeKeyword)), "insecure_skip_verify|false", "Prometheus: the certificate check");
        Equal(Join(prom.Tokens(38, SyntaxHighlighting.TypeKeyword)), "authorization", "Prometheus: the authorization");
        Equal(Join(prom.Tokens(39, SyntaxHighlighting.TypeKeyword)), "type|Bearer", "Prometheus: the authorization type");
        Equal(Join(prom.Tokens(41, SyntaxHighlighting.TypeKeyword)), "", "Prometheus: an unknown field is not a keyword");
        Equal(Join(prom.Tokens(41, SyntaxHighlighting.TypeVariable)), "unknown_field", "Prometheus: ...it is a plain key");
        Check(Folds(prom).Length > 0, "Prometheus blocks fold");

        Highlighted rules = new Highlighted(L.Prometheus,
            "groups:\n  - name: node\n    rules:\n      - alert: InstanceDown\n        expr: up == 0\n" +
            "        for: 5m\n        labels:\n          severity: critical\n        annotations:\n" +
            "          summary: \"Instance is down\"\n          runbook_url: https://example.com/runbook\n");
        Equal(Join(rules.Tokens(0, SyntaxHighlighting.TypeKeyword)), "groups", "Prometheus rules: groups");
        Equal(Join(rules.Tokens(1, SyntaxHighlighting.TypeKeyword)), "name", "Prometheus rules: the name of a group");
        Equal(Join(rules.Tokens(2, SyntaxHighlighting.TypeKeyword)), "rules", "Prometheus rules: the rules");
        Equal(Join(rules.Tokens(3, SyntaxHighlighting.TypeKeyword)), "alert", "Prometheus rules: an alert");
        Equal(Join(rules.Tokens(4, SyntaxHighlighting.TypeKeyword)), "expr", "Prometheus rules: the expression");
        Equal(Join(rules.Tokens(5, SyntaxHighlighting.TypeKeyword)), "for", "Prometheus rules: the duration");
        Equal(Join(rules.Tokens(6, SyntaxHighlighting.TypeKeyword)), "labels", "Prometheus rules: the labels");
        Equal(Join(rules.Tokens(8, SyntaxHighlighting.TypeKeyword)), "annotations", "Prometheus rules: the annotations");
        Equal(Join(rules.Tokens(9, SyntaxHighlighting.TypeKeyword)), "summary", "Prometheus rules: the summary");
        Equal(Join(rules.Tokens(9, SyntaxHighlighting.TypeString)), "\"Instance is down\"", "Prometheus rules: the text of the annotation");
        Equal(Join(rules.Tokens(10, SyntaxHighlighting.TypeKeyword)), "runbook_url|https", "Prometheus rules: the runbook");
        Check(SyntaxHighlighting.GetKeywords(L.Prometheus).Contains("scrape_configs"), "Prometheus keywords contain 'scrape_configs'");
        Check(SyntaxHighlighting.GetKeywords(L.Prometheus).Contains("relabel_configs"), "Prometheus keywords contain 'relabel_configs'");
        Check(SyntaxHighlighting.GetKeywords(L.Prometheus).Contains("https"), "Prometheus keywords contain 'https'");
        Equal(Join(CommentStyle.ForLanguage(L.Prometheus).Toggle(new[] { "  scrape_interval: 15s" })), "  # scrape_interval: 15s", "Prometheus comment");
    }

    private static void AlertmanagerChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/alertmanager/alertmanager.yml"), L.Alertmanager, "the Alertmanager configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/alertmanager/alertmanager.yaml"), L.Alertmanager, "the .yaml spelling");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/monitoring/alertmanager.yml"), L.Alertmanager, "alertmanager.yml anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/monitoring/alertmanager.yaml"), L.Alertmanager, "alertmanager.yaml anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/alertmanager/templates/default.tmpl"), L.Plain, "a template is not the configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/alertmanager/README"), L.Plain, "the README of /etc/alertmanager is prose");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/alertmanager/alertmanager.yml.bak"), L.Plain, "a backup is not Alertmanager");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/monitoring/prometheus.yml"), L.Prometheus, "Prometheus keeps its own name");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/app.yml"), L.Yaml, "an ordinary .yml stays YAML");

        Highlighted am = new Highlighted(L.Alertmanager,
            "# Alertmanager configuration\nglobal:\n  resolve_timeout: 5m\n  smtp_smarthost: smtp.example.com:587\n" +
            "  smtp_from: alerts@example.com\n  smtp_auth_username: alerts\n  smtp_auth_password: secret\n" +
            "  smtp_require_tls: true\nroute:\n  receiver: default\n  group_by: [alertname, instance]\n" +
            "  group_wait: 30s\n  group_interval: 5m\n  repeat_interval: 4h\n  matchers:\n    - severity=\"critical\"\n" +
            "  routes:\n    - receiver: pager\n      matchers:\n        - team=\"ops\"\n      continue: false\n" +
            "receivers:\n  - name: default\n    email_configs:\n      - to: oncall@example.com\n" +
            "        send_resolved: true\n        headers:\n          Subject: \"{{ .Status }}\"\n" +
            "  - name: pager\n    slack_configs:\n      - api_url: https://hooks.slack.com/services/T000/B000\n" +
            "        channel: \"#alerts\"\n        send_resolved: true\n        title: \"{{ .CommonLabels.alertname }}\"\n" +
            "inhibit_rules:\n  - source_matchers:\n      - severity=\"critical\"\n    target_matchers:\n" +
            "      - severity=\"warning\"\n    equal: [alertname, instance]\n" +
            "mute_time_intervals:\n  - name: weekends\n    time_intervals:\n" +
            "      - weekdays: [saturday, sunday]\n        start_time: \"00:00\"\n        end_time: \"24:00\"\n" +
            "unknown_field: 1\n");
        Equal(Join(am.Tokens(0, SyntaxHighlighting.TypeComment)), "# Alertmanager configuration", "Alertmanager: a comment");
        Equal(Join(am.Tokens(1, SyntaxHighlighting.TypeKeyword)), "global", "Alertmanager: the global block");
        Equal(Join(am.Tokens(2, SyntaxHighlighting.TypeKeyword)), "resolve_timeout", "Alertmanager: the resolve timeout");
        Equal(Join(am.Tokens(3, SyntaxHighlighting.TypeKeyword)), "smtp_smarthost", "Alertmanager: the SMTP host");
        Equal(Join(am.Tokens(5, SyntaxHighlighting.TypeKeyword)), "smtp_auth_username", "Alertmanager: the SMTP user");
        Equal(Join(am.Tokens(7, SyntaxHighlighting.TypeKeyword)), "smtp_require_tls|true", "Alertmanager: TLS for SMTP");
        Equal(Join(am.Tokens(8, SyntaxHighlighting.TypeKeyword)), "route", "Alertmanager: the route");
        Equal(Join(am.Tokens(9, SyntaxHighlighting.TypeKeyword)), "receiver", "Alertmanager: the receiver of a route");
        Equal(Join(am.Tokens(10, SyntaxHighlighting.TypeKeyword)), "group_by", "Alertmanager: the grouping");
        Equal(Join(am.Tokens(13, SyntaxHighlighting.TypeKeyword)), "repeat_interval", "Alertmanager: the repeat interval");
        Equal(Join(am.Tokens(14, SyntaxHighlighting.TypeKeyword)), "matchers", "Alertmanager: the matchers");
        Equal(Join(am.Tokens(16, SyntaxHighlighting.TypeKeyword)), "routes", "Alertmanager: the child routes");
        Equal(Join(am.Tokens(17, SyntaxHighlighting.TypeKeyword)), "receiver", "Alertmanager: a child receiver");
        Equal(Join(am.Tokens(20, SyntaxHighlighting.TypeKeyword)), "continue|false", "Alertmanager: the continue flag");
        Equal(Join(am.Tokens(21, SyntaxHighlighting.TypeKeyword)), "receivers", "Alertmanager: the receivers");
        Equal(Join(am.Tokens(22, SyntaxHighlighting.TypeKeyword)), "name", "Alertmanager: the name of a receiver");
        Equal(Join(am.Tokens(23, SyntaxHighlighting.TypeKeyword)), "email_configs", "Alertmanager: the e-mail settings");
        Equal(Join(am.Tokens(24, SyntaxHighlighting.TypeKeyword)), "to", "Alertmanager: the recipient");
        Equal(Join(am.Tokens(25, SyntaxHighlighting.TypeKeyword)), "send_resolved|true", "Alertmanager: the resolved notices");
        Equal(Join(am.Tokens(26, SyntaxHighlighting.TypeKeyword)), "headers", "Alertmanager: the headers");
        Equal(Join(am.Tokens(27, SyntaxHighlighting.TypeVariable)), "Subject", "Alertmanager: a header name stays an ordinary key");
        Equal(Join(am.Tokens(29, SyntaxHighlighting.TypeKeyword)), "slack_configs", "Alertmanager: the Slack settings");
        Equal(Join(am.Tokens(30, SyntaxHighlighting.TypeKeyword)), "api_url|https", "Alertmanager: the Slack webhook");
        Equal(Join(am.Tokens(31, SyntaxHighlighting.TypeKeyword)), "channel", "Alertmanager: the Slack channel");
        Equal(Join(am.Tokens(31, SyntaxHighlighting.TypeString)), "\"#alerts\"", "Alertmanager: the channel name");
        Equal(Join(am.Tokens(33, SyntaxHighlighting.TypeKeyword)), "title", "Alertmanager: the title of a message");
        Equal(Join(am.Tokens(34, SyntaxHighlighting.TypeKeyword)), "inhibit_rules", "Alertmanager: the inhibition rules");
        Equal(Join(am.Tokens(35, SyntaxHighlighting.TypeKeyword)), "source_matchers", "Alertmanager: the source matchers");
        Equal(Join(am.Tokens(37, SyntaxHighlighting.TypeKeyword)), "target_matchers", "Alertmanager: the target matchers");
        Equal(Join(am.Tokens(39, SyntaxHighlighting.TypeKeyword)), "equal", "Alertmanager: the equal labels");
        Equal(Join(am.Tokens(40, SyntaxHighlighting.TypeKeyword)), "mute_time_intervals", "Alertmanager: the mute intervals");
        Equal(Join(am.Tokens(42, SyntaxHighlighting.TypeKeyword)), "time_intervals", "Alertmanager: the time intervals");
        Equal(Join(am.Tokens(43, SyntaxHighlighting.TypeKeyword)), "weekdays|saturday|sunday", "Alertmanager: the weekdays");
        Equal(Join(am.Tokens(44, SyntaxHighlighting.TypeKeyword)), "start_time", "Alertmanager: the start of an interval");
        Equal(Join(am.Tokens(44, SyntaxHighlighting.TypeString)), "\"00:00\"", "Alertmanager: the start time");
        Equal(Join(am.Tokens(45, SyntaxHighlighting.TypeKeyword)), "end_time", "Alertmanager: the end of an interval");
        Equal(Join(am.Tokens(46, SyntaxHighlighting.TypeKeyword)), "", "Alertmanager: an unknown field is not a keyword");
        Equal(Join(am.Tokens(46, SyntaxHighlighting.TypeVariable)), "unknown_field", "Alertmanager: ...it is a plain key");
        Check(Folds(am).Length > 0, "Alertmanager blocks fold");
        Check(SyntaxHighlighting.GetKeywords(L.Alertmanager).Contains("receivers"), "Alertmanager keywords contain 'receivers'");
        Check(SyntaxHighlighting.GetKeywords(L.Alertmanager).Contains("slack_configs"), "Alertmanager keywords contain 'slack_configs'");
        Check(SyntaxHighlighting.GetKeywords(L.Alertmanager).Contains("saturday"), "Alertmanager keywords contain 'saturday'");
        Equal(Join(CommentStyle.ForLanguage(L.Alertmanager).Toggle(new[] { "  resolve_timeout: 5m" })), "  # resolve_timeout: 5m", "Alertmanager comment");
    }

    private static void MongoDbChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/mongod.conf"), L.MongoDb, "the mongod configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/mongos.conf"), L.MongoDb, "the mongos configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/mongodb.conf"), L.MongoDb, "the older MongoDB configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/mongod.conf"), L.MongoDb, "mongod.conf anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/mongodb/mongod.conf"), L.MongoDb, "a configuration under /etc/mongodb");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/mongodb/conf/mongos.conf"), L.MongoDb, "a nested mongos configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/mongod.conf.bak"), L.Plain, "a backup of mongod.conf is not MongoDB");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/mongodb/README"), L.Plain, "the README of /etc/mongodb is prose");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/app.yml"), L.Yaml, "an ordinary .yml stays YAML");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/server.conf"), L.Config, "an ordinary .conf stays Config");

        Highlighted mongo = new Highlighted(L.MongoDb,
            "# MongoDB configuration\nsystemLog:\n  destination: file\n  path: /var/log/mongodb/mongod.log\n" +
            "  logAppend: true\n  logRotate: reopen\nstorage:\n  dbPath: /var/lib/mongodb\n  journal:\n" +
            "    enabled: true\n  engine: wiredTiger\n  wiredTiger:\n    engineConfig:\n      cacheSizeGB: 2\n" +
            "    collectionConfig:\n      blockCompressor: snappy\n    indexConfig:\n      prefixCompression: true\n" +
            "  directoryPerDB: true\nnet:\n  port: 27017\n  bindIp: 127.0.0.1,10.0.0.5\n  ipv6: false\n" +
            "  maxIncomingConnections: 65536\n  tls:\n    mode: requireTLS\n" +
            "    certificateKeyFile: /etc/ssl/mongodb.pem\n    allowConnectionsWithoutCertificates: false\n" +
            "processManagement:\n  fork: true\n  pidFilePath: /var/run/mongodb/mongod.pid\nsecurity:\n" +
            "  authorization: enabled\n  keyFile: /etc/mongodb/key\n  javascriptEnabled: false\n" +
            "operationProfiling:\n  mode: slowOp\n  slowOpThresholdMs: 100\nreplication:\n  replSetName: rs0\n" +
            "  oplogSizeMB: 2048\nsharding:\n  clusterRole: shardsvr\nauditLog:\n  destination: file\n" +
            "  format: JSON\n  path: /var/log/mongodb/audit.json\nsetParameter:\n" +
            "  enableLocalhostAuthBypass: false\n  authenticationMechanisms: SCRAM-SHA-256\nunknown_option: 1\n");
        Equal(Join(mongo.Tokens(0, SyntaxHighlighting.TypeComment)), "# MongoDB configuration", "MongoDB: a comment");
        Equal(Join(mongo.Tokens(1, SyntaxHighlighting.TypeKeyword)), "systemLog", "MongoDB: the systemLog block");
        Equal(Join(mongo.Tokens(2, SyntaxHighlighting.TypeKeyword)), "destination|file", "MongoDB: the log destination");
        Equal(Join(mongo.Tokens(3, SyntaxHighlighting.TypeKeyword)), "path", "MongoDB: the log path");
        Equal(Join(mongo.Tokens(4, SyntaxHighlighting.TypeKeyword)), "logAppend|true", "MongoDB: append to the log");
        Equal(Join(mongo.Tokens(5, SyntaxHighlighting.TypeKeyword)), "logRotate|reopen", "MongoDB: the log rotation");
        Equal(Join(mongo.Tokens(6, SyntaxHighlighting.TypeKeyword)), "storage", "MongoDB: the storage block");
        Equal(Join(mongo.Tokens(7, SyntaxHighlighting.TypeKeyword)), "dbPath", "MongoDB: the data directory");
        Equal(Join(mongo.Tokens(9, SyntaxHighlighting.TypeKeyword)), "enabled|true", "MongoDB: the journal");
        Equal(Join(mongo.Tokens(10, SyntaxHighlighting.TypeKeyword)), "engine|wiredTiger", "MongoDB: the storage engine");
        Equal(Join(mongo.Tokens(11, SyntaxHighlighting.TypeKeyword)), "wiredTiger", "MongoDB: the WiredTiger block");
        Equal(Join(mongo.Tokens(12, SyntaxHighlighting.TypeKeyword)), "engineConfig", "MongoDB: the engine configuration");
        Equal(Join(mongo.Tokens(13, SyntaxHighlighting.TypeKeyword)), "cacheSizeGB", "MongoDB: the cache size");
        Equal(Join(mongo.Tokens(13, SyntaxHighlighting.TypeNumber)), "2", "MongoDB: the cache size value");
        Equal(Join(mongo.Tokens(15, SyntaxHighlighting.TypeKeyword)), "blockCompressor|snappy", "MongoDB: the compressor");
        Equal(Join(mongo.Tokens(17, SyntaxHighlighting.TypeKeyword)), "prefixCompression|true", "MongoDB: the index prefix compression");
        Equal(Join(mongo.Tokens(19, SyntaxHighlighting.TypeKeyword)), "net", "MongoDB: the network block");
        Equal(Join(mongo.Tokens(20, SyntaxHighlighting.TypeKeyword)), "port", "MongoDB: the port");
        Equal(Join(mongo.Tokens(20, SyntaxHighlighting.TypeNumber)), "27017", "MongoDB: the port number");
        Equal(Join(mongo.Tokens(21, SyntaxHighlighting.TypeKeyword)), "bindIp", "MongoDB: the bind addresses");
        Equal(Join(mongo.Tokens(22, SyntaxHighlighting.TypeKeyword)), "ipv6|false", "MongoDB: the IPv6 flag");
        Equal(Join(mongo.Tokens(23, SyntaxHighlighting.TypeKeyword)), "maxIncomingConnections", "MongoDB: the connection limit");
        Equal(Join(mongo.Tokens(23, SyntaxHighlighting.TypeNumber)), "65536", "MongoDB: the connection limit value");
        Equal(Join(mongo.Tokens(25, SyntaxHighlighting.TypeKeyword)), "mode|requireTLS", "MongoDB: the TLS mode");
        Equal(Join(mongo.Tokens(26, SyntaxHighlighting.TypeKeyword)), "certificateKeyFile", "MongoDB: the certificate");
        Equal(Join(mongo.Tokens(27, SyntaxHighlighting.TypeKeyword)), "allowConnectionsWithoutCertificates|false", "MongoDB: the client certificate check");
        Equal(Join(mongo.Tokens(28, SyntaxHighlighting.TypeKeyword)), "processManagement", "MongoDB: the process block");
        Equal(Join(mongo.Tokens(29, SyntaxHighlighting.TypeKeyword)), "fork|true", "MongoDB: the fork flag");
        Equal(Join(mongo.Tokens(31, SyntaxHighlighting.TypeKeyword)), "security", "MongoDB: the security block");
        Equal(Join(mongo.Tokens(32, SyntaxHighlighting.TypeKeyword)), "authorization|enabled", "MongoDB: the authorization");
        Equal(Join(mongo.Tokens(33, SyntaxHighlighting.TypeKeyword)), "keyFile", "MongoDB: the key file");
        Equal(Join(mongo.Tokens(34, SyntaxHighlighting.TypeKeyword)), "javascriptEnabled|false", "MongoDB: the JavaScript engine");
        Equal(Join(mongo.Tokens(35, SyntaxHighlighting.TypeKeyword)), "operationProfiling", "MongoDB: the profiler block");
        Equal(Join(mongo.Tokens(36, SyntaxHighlighting.TypeKeyword)), "mode|slowOp", "MongoDB: the profiling mode");
        Equal(Join(mongo.Tokens(37, SyntaxHighlighting.TypeKeyword)), "slowOpThresholdMs", "MongoDB: the slow operation threshold");
        Equal(Join(mongo.Tokens(37, SyntaxHighlighting.TypeNumber)), "100", "MongoDB: the threshold value");
        Equal(Join(mongo.Tokens(38, SyntaxHighlighting.TypeKeyword)), "replication", "MongoDB: the replication block");
        Equal(Join(mongo.Tokens(39, SyntaxHighlighting.TypeKeyword)), "replSetName", "MongoDB: the replica set name");
        Equal(Join(mongo.Tokens(40, SyntaxHighlighting.TypeKeyword)), "oplogSizeMB", "MongoDB: the oplog size");
        Equal(Join(mongo.Tokens(40, SyntaxHighlighting.TypeNumber)), "2048", "MongoDB: the oplog size value");
        Equal(Join(mongo.Tokens(41, SyntaxHighlighting.TypeKeyword)), "sharding", "MongoDB: the sharding block");
        Equal(Join(mongo.Tokens(42, SyntaxHighlighting.TypeKeyword)), "clusterRole|shardsvr", "MongoDB: the node role");
        Equal(Join(mongo.Tokens(43, SyntaxHighlighting.TypeKeyword)), "auditLog", "MongoDB: the audit log");
        Equal(Join(mongo.Tokens(45, SyntaxHighlighting.TypeKeyword)), "format|JSON", "MongoDB: the audit format");
        Equal(Join(mongo.Tokens(47, SyntaxHighlighting.TypeKeyword)), "setParameter", "MongoDB: the server parameters");
        Equal(Join(mongo.Tokens(48, SyntaxHighlighting.TypeKeyword)), "enableLocalhostAuthBypass|false", "MongoDB: the localhost bypass");
        Equal(Join(mongo.Tokens(49, SyntaxHighlighting.TypeKeyword)), "authenticationMechanisms|SCRAM-SHA-256", "MongoDB: the authentication mechanism");
        Equal(Join(mongo.Tokens(50, SyntaxHighlighting.TypeKeyword)), "", "MongoDB: an unknown option is not a keyword");
        Equal(Join(mongo.Tokens(50, SyntaxHighlighting.TypeVariable)), "unknown_option", "MongoDB: ...it is a plain key");
        Check(Folds(mongo).Length > 0, "MongoDB blocks fold");
        Check(SyntaxHighlighting.GetKeywords(L.MongoDb).Contains("wiredTiger"), "MongoDB keywords contain 'wiredTiger'");
        Check(SyntaxHighlighting.GetKeywords(L.MongoDb).Contains("requireTLS"), "MongoDB keywords contain 'requireTLS'");
        Check(SyntaxHighlighting.GetKeywords(L.MongoDb).Contains("shardsvr"), "MongoDB keywords contain 'shardsvr'");
        Equal(Join(CommentStyle.ForLanguage(L.MongoDb).Toggle(new[] { "  dbPath: /var/lib/mongodb" })), "  # dbPath: /var/lib/mongodb", "MongoDB comment");
    }

    private static void ElasticsearchChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/elasticsearch/elasticsearch.yml"), L.Elasticsearch, "the Elasticsearch configuration");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/elasticsearch/elasticsearch.yaml"), L.Elasticsearch, "the .yaml spelling");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/elasticsearch.yml"), L.Elasticsearch, "elasticsearch.yml anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/elasticsearch.yml.bak"), L.Plain, "a backup of the configuration is not Elasticsearch");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/elasticsearch/README"), L.Plain, "the README of /etc/elasticsearch is prose");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/elasticsearch/jvm.options"), L.Plain, "the JVM options are not Elasticsearch YAML");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/app.yml"), L.Yaml, "an ordinary .yml stays YAML");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/mongod.conf"), L.MongoDb, "MongoDB keeps its own name");

        Highlighted es = new Highlighted(L.Elasticsearch,
            "# Elasticsearch configuration\ncluster.name: logs\n" +
            "cluster.initial_master_nodes: [\"node-1\", \"node-2\"]\nnode.name: node-1\n" +
            "node.roles: [master, data_hot, ingest]\npath.data: /var/lib/elasticsearch\n" +
            "path.logs: /var/log/elasticsearch\nnetwork.host: 0.0.0.0\nhttp.port: 9200\n" +
            "xpack.security.enabled: true\nxpack.security.transport.ssl.enabled: true\n" +
            "xpack.security.transport.ssl.verification_mode: certificate\n" +
            "xpack.security.transport.ssl.keystore.path: certs/elastic-certificates.p12\n" +
            "xpack.security.http.ssl.enabled: true\nxpack.security.http.ssl.keystore.path: certs/http.p12\n" +
            "discovery.seed_hosts: [\"es-1:9300\", \"es-2:9300\"]\ndiscovery.type: single-node\n" +
            "bootstrap.memory_lock: true\naction.destructive_requires_name: true\n" +
            "indices.query.bool.max_clause_count: 4096\nthread_pool.write.queue_size: 1000\n" +
            "xpack.ml.enabled: false\nxpack.security.authc.realms:\n  native:\n    order: 0\nunknown.setting: 1\n");
        Equal(Join(es.Tokens(0, SyntaxHighlighting.TypeComment)), "# Elasticsearch configuration", "Elasticsearch: a comment");
        Equal(Join(es.Tokens(1, SyntaxHighlighting.TypeKeyword)), "cluster.name", "Elasticsearch: the cluster name");
        Equal(Join(es.Tokens(2, SyntaxHighlighting.TypeKeyword)), "cluster.initial_master_nodes", "Elasticsearch: the initial masters");
        Equal(Join(es.Tokens(2, SyntaxHighlighting.TypeString)), "\"node-1\"|\"node-2\"", "Elasticsearch: the master names");
        Equal(Join(es.Tokens(3, SyntaxHighlighting.TypeKeyword)), "node.name", "Elasticsearch: the node name");
        Equal(Join(es.Tokens(4, SyntaxHighlighting.TypeKeyword)), "node.roles|master|data_hot|ingest", "Elasticsearch: the node roles");
        Equal(Join(es.Tokens(5, SyntaxHighlighting.TypeKeyword)), "path.data", "Elasticsearch: the data path");
        Equal(Join(es.Tokens(6, SyntaxHighlighting.TypeKeyword)), "path.logs", "Elasticsearch: the log path");
        Equal(Join(es.Tokens(7, SyntaxHighlighting.TypeKeyword)), "network.host", "Elasticsearch: the bind address");
        Equal(Join(es.Tokens(8, SyntaxHighlighting.TypeKeyword)), "http.port", "Elasticsearch: the HTTP port");
        Equal(Join(es.Tokens(8, SyntaxHighlighting.TypeNumber)), "9200", "Elasticsearch: the port number");
        Equal(Join(es.Tokens(9, SyntaxHighlighting.TypeKeyword)), "xpack.security.enabled|true", "Elasticsearch: the security switch");
        Equal(Join(es.Tokens(10, SyntaxHighlighting.TypeKeyword)), "xpack.security.transport.ssl.enabled|true", "Elasticsearch: TLS on the transport");
        Equal(Join(es.Tokens(11, SyntaxHighlighting.TypeKeyword)), "xpack.security.transport.ssl.verification_mode|certificate", "Elasticsearch: the TLS verification");
        Equal(Join(es.Tokens(12, SyntaxHighlighting.TypeKeyword)), "xpack.security.transport.ssl.keystore.path", "Elasticsearch: the transport keystore");
        Equal(Join(es.Tokens(13, SyntaxHighlighting.TypeKeyword)), "xpack.security.http.ssl.enabled|true", "Elasticsearch: TLS on the HTTP layer");
        Equal(Join(es.Tokens(14, SyntaxHighlighting.TypeKeyword)), "xpack.security.http.ssl.keystore.path", "Elasticsearch: the HTTP keystore");
        Equal(Join(es.Tokens(15, SyntaxHighlighting.TypeKeyword)), "discovery.seed_hosts", "Elasticsearch: the seed hosts");
        Equal(Join(es.Tokens(15, SyntaxHighlighting.TypeString)), "\"es-1:9300\"|\"es-2:9300\"", "Elasticsearch: the seed host names");
        Equal(Join(es.Tokens(16, SyntaxHighlighting.TypeKeyword)), "discovery.type|single-node", "Elasticsearch: the single-node discovery");
        Equal(Join(es.Tokens(17, SyntaxHighlighting.TypeKeyword)), "bootstrap.memory_lock|true", "Elasticsearch: the memory lock");
        Equal(Join(es.Tokens(18, SyntaxHighlighting.TypeKeyword)), "action.destructive_requires_name|true", "Elasticsearch: the destructive actions");
        Equal(Join(es.Tokens(19, SyntaxHighlighting.TypeKeyword)), "indices.query.bool.max_clause_count", "Elasticsearch: the clause limit");
        Equal(Join(es.Tokens(19, SyntaxHighlighting.TypeNumber)), "4096", "Elasticsearch: the clause limit value");
        Equal(Join(es.Tokens(20, SyntaxHighlighting.TypeKeyword)), "thread_pool.write.queue_size", "Elasticsearch: the write queue");
        Equal(Join(es.Tokens(20, SyntaxHighlighting.TypeNumber)), "1000", "Elasticsearch: the queue size");
        Equal(Join(es.Tokens(21, SyntaxHighlighting.TypeKeyword)), "xpack.ml.enabled|false", "Elasticsearch: machine learning off");
        Equal(Join(es.Tokens(22, SyntaxHighlighting.TypeKeyword)), "xpack.security.authc.realms", "Elasticsearch: the authentication realms");
        Equal(Join(es.Tokens(24, SyntaxHighlighting.TypeKeyword)), "order", "Elasticsearch: the realm order");
        Equal(Join(es.Tokens(25, SyntaxHighlighting.TypeKeyword)), "", "Elasticsearch: an unknown setting is not a keyword");
        Equal(Join(es.Tokens(25, SyntaxHighlighting.TypeVariable)), "unknown.setting", "Elasticsearch: ...it is a plain key");
        Check(Folds(es).Length > 0, "Elasticsearch blocks fold");
        Check(SyntaxHighlighting.GetKeywords(L.Elasticsearch).Contains("discovery.seed_hosts"), "Elasticsearch keywords contain 'discovery.seed_hosts'");
        Check(SyntaxHighlighting.GetKeywords(L.Elasticsearch).Contains("single-node"), "Elasticsearch keywords contain 'single-node'");
        Check(SyntaxHighlighting.GetKeywords(L.Elasticsearch).Contains("data_hot"), "Elasticsearch keywords contain 'data_hot'");
        Equal(Join(CommentStyle.ForLanguage(L.Elasticsearch).Toggle(new[] { "network.host: 0.0.0.0" })), "# network.host: 0.0.0.0", "Elasticsearch comment");
    }

    private static void MesonChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/src/meson.build"), L.Meson, "meson.build");
        Equal(SyntaxHighlighting.DetectLanguage("/src/subdir/meson.build"), L.Meson, "a nested meson.build");
        Equal(SyntaxHighlighting.DetectLanguage("/src/meson.options"), L.Meson, "meson.options");
        Equal(SyntaxHighlighting.DetectLanguage("/src/meson_options.txt"), L.Meson, "the older meson_options.txt");
        Equal(SyntaxHighlighting.DetectLanguage("/src/Meson.build"), L.Meson, "the name is case-insensitive");
        Equal(SyntaxHighlighting.DetectLanguage("/src/meson.build.bak"), L.Plain, "a backup of meson.build is not Meson");
        Equal(SyntaxHighlighting.DetectLanguage("/src/build.ninja"), L.Plain, "the Ninja file is not Meson");
        Equal(SyntaxHighlighting.DetectLanguage("/src/CMakeLists.txt"), L.CMake, "CMake keeps its own name");
        Equal(SyntaxHighlighting.DetectLanguage("/src/Makefile"), L.Makefile, "Makefile keeps its own name");

        Highlighted meson = new Highlighted(L.Meson,
            "# Meson build file\nproject('demo', ['c', 'cpp'],\n  version: '1.2.0',\n" +
            "  default_options: ['warning_level=3', 'cpp_std=c++17'])\n\n" +
            "cc = meson.get_compiler('c')\ninc = include_directories('include')\n\n" +
            "sources = files('main.c', 'util.c')\nlib = static_library('util', sources,\n" +
            "  include_directories: inc,\n  install: false)\n\n" +
            "if host_machine.system() == 'linux'\n  add_project_arguments('-D_GNU_SOURCE', language: 'c')\n" +
            "elif host_machine.system() == 'darwin'\n  add_project_arguments('-D_DARWIN_C_SOURCE', language: 'c')\n" +
            "else\n  warning('unknown system')\nendif\n\n" +
            "foreach name : ['a', 'b']\n  message('@0@'.format(name))\nendforeach\n\n" +
            "executable('demo', sources,\n  link_with: lib,\n  dependencies: [dependency('threads')],\n" +
            "  install: true)\n\nsummary('build', true)\noption('jobs', type: 'integer', value: 4)\n");
        Equal(Join(meson.Tokens(0, SyntaxHighlighting.TypeComment)), "# Meson build file", "Meson: a comment");
        Equal(Join(meson.Tokens(1, SyntaxHighlighting.TypeKeyword)), "project", "Meson: project()");
        Equal(Join(meson.Tokens(1, SyntaxHighlighting.TypeString)), "'demo'|'c'|'cpp'", "Meson: the project languages");
        Equal(Join(meson.Tokens(5, SyntaxHighlighting.TypeVariable)), "meson", "Meson: the meson object");
        Equal(Join(meson.Tokens(5, SyntaxHighlighting.TypeKeyword)), "get_compiler", "Meson: get_compiler()");
        Equal(Join(meson.Tokens(6, SyntaxHighlighting.TypeKeyword)), "include_directories", "Meson: include_directories()");
        Equal(Join(meson.Tokens(8, SyntaxHighlighting.TypeKeyword)), "files", "Meson: files()");
        Equal(Join(meson.Tokens(9, SyntaxHighlighting.TypeKeyword)), "static_library", "Meson: static_library()");
        Equal(Join(meson.Tokens(10, SyntaxHighlighting.TypeKeyword)), "include_directories", "Meson: a keyword argument");
        Equal(Join(meson.Tokens(11, SyntaxHighlighting.TypeKeyword)), "false", "Meson: false");
        Equal(Join(meson.Tokens(13, SyntaxHighlighting.TypeKeyword)), "if|system", "Meson: if and the system method");
        Equal(Join(meson.Tokens(13, SyntaxHighlighting.TypeVariable)), "host_machine", "Meson: the host machine");
        Equal(Join(meson.Tokens(14, SyntaxHighlighting.TypeKeyword)), "add_project_arguments", "Meson: add_project_arguments()");
        Equal(Join(meson.Tokens(15, SyntaxHighlighting.TypeKeyword)), "elif|system", "Meson: elif");
        Equal(Join(meson.Tokens(17, SyntaxHighlighting.TypeKeyword)), "else", "Meson: else");
        Equal(Join(meson.Tokens(18, SyntaxHighlighting.TypeKeyword)), "warning", "Meson: warning()");
        Equal(Join(meson.Tokens(19, SyntaxHighlighting.TypeKeyword)), "endif", "Meson: endif");
        Equal(Join(meson.Tokens(21, SyntaxHighlighting.TypeKeyword)), "foreach", "Meson: foreach");
        Equal(Join(meson.Tokens(22, SyntaxHighlighting.TypeKeyword)), "message|format", "Meson: message() and format()");
        Equal(Join(meson.Tokens(23, SyntaxHighlighting.TypeKeyword)), "endforeach", "Meson: endforeach");
        Equal(Join(meson.Tokens(25, SyntaxHighlighting.TypeKeyword)), "executable", "Meson: executable()");
        Equal(Join(meson.Tokens(25, SyntaxHighlighting.TypeString)), "'demo'", "Meson: the target name");
        Equal(Join(meson.Tokens(27, SyntaxHighlighting.TypeKeyword)), "dependency", "Meson: dependency()");
        Equal(Join(meson.Tokens(28, SyntaxHighlighting.TypeKeyword)), "true", "Meson: true");
        Equal(Join(meson.Tokens(30, SyntaxHighlighting.TypeKeyword)), "summary|true", "Meson: summary()");
        Equal(Join(meson.Tokens(31, SyntaxHighlighting.TypeKeyword)), "option", "Meson: option()");
        Equal(Join(meson.Tokens(31, SyntaxHighlighting.TypeNumber)), "4", "Meson: the option value");
        Check(SyntaxHighlighting.GetKeywords(L.Meson).Contains("executable"), "Meson keywords contain 'executable'");
        Check(SyntaxHighlighting.GetKeywords(L.Meson).Contains("endforeach"), "Meson keywords contain 'endforeach'");
        Check(SyntaxHighlighting.GetKeywords(L.Meson).Contains("meson"), "Meson keywords contain 'meson'");
        Equal(Join(CommentStyle.ForLanguage(L.Meson).Toggle(new[] { "project('demo')" })), "# project('demo')", "Meson comment");
    }

    private static void HclChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/infra/main.tf"), L.Hcl, ".tf is HCL");
        Equal(SyntaxHighlighting.DetectLanguage("/infra/terraform.tfvars"), L.Hcl, ".tfvars is HCL");
        Equal(SyntaxHighlighting.DetectLanguage("/infra/packer.pkr.hcl"), L.Hcl, ".hcl is HCL");
        Equal(SyntaxHighlighting.DetectLanguage("/jobs/web.nomad"), L.Hcl, ".nomad is HCL");
        Equal(SyntaxHighlighting.DetectLanguage("/infra/MAIN.TF"), L.Hcl, "the extension is matched without regard to case");
        Equal(SyntaxHighlighting.DetectLanguage("/infra/main.tf.bak"), L.Plain, "a backup of a .tf file is not HCL");
        Equal(SyntaxHighlighting.DetectLanguage("/infra/main.tf.json"), L.Json, ".tf.json stays JSON");

        Highlighted hcl = new Highlighted(L.Hcl,
            "# Terraform configuration\n" +
            "// A line comment\n" +
            "/* A block comment\n" +
            "   that spans lines */\n" +
            "terraform {\n" +
            "  required_version = \">= 1.5.0\"\n" +
            "}\n" +
            "\n" +
            "variable \"region\" {\n" +
            "  type    = string\n" +
            "  default = \"eu-west-1\"\n" +
            "}\n" +
            "\n" +
            "resource \"aws_instance\" \"web\" {\n" +
            "  ami           = \"ami-12345678\"\n" +
            "  instance_type = \"t3.micro\"\n" +
            "  count         = 2\n" +
            "\n" +
            "  tags = {\n" +
            "    Name = \"web-${count.index}\"\n" +
            "  }\n" +
            "\n" +
            "  user_data = <<-EOF\n" +
            "    #!/bin/bash\n" +
            "    echo \"hello\"\n" +
            "  EOF\n" +
            "}\n" +
            "\n" +
            "output \"ids\" {\n" +
            "  value = [for i in aws_instance.web : i.id]\n" +
            "}\n" +
            "\n" +
            "locals {\n" +
            "  names = [for name in var.names : upper(name)]\n" +
            "}\n" +
            "\n" +
            "resource \"aws_s3_bucket\" \"logs\" {\n" +
            "  bucket = \"${var.prefix}-logs\"\n" +
            "  tags   = merge(local.common, { Name = \"logs\" })\n" +
            "}\n");
        Equal(Join(hcl.Tokens(0, SyntaxHighlighting.TypeComment)), "# Terraform configuration", "HCL: a # comment");
        Equal(Join(hcl.Tokens(1, SyntaxHighlighting.TypeComment)), "// A line comment", "HCL: a // comment");
        Equal(Join(hcl.Tokens(2, SyntaxHighlighting.TypeComment)), "/* A block comment", "HCL: a block comment");
        Equal(Join(hcl.Tokens(3, SyntaxHighlighting.TypeComment)), "   that spans lines */", "HCL: the second line of a block comment");
        Equal(Join(hcl.Tokens(4, SyntaxHighlighting.TypeKeyword)), "terraform", "HCL: the terraform block");
        Equal(Join(hcl.Tokens(5, SyntaxHighlighting.TypeString)), "\">= 1.5.0\"", "HCL: a version string");
        Equal(Join(hcl.Tokens(8, SyntaxHighlighting.TypeKeyword)), "variable", "HCL: the variable block");
        Equal(Join(hcl.Tokens(8, SyntaxHighlighting.TypeString)), "\"region\"", "HCL: the variable name");
        Equal(Join(hcl.Tokens(9, SyntaxHighlighting.TypeKeyword)), "type|string", "HCL: the type constraint");
        Equal(Join(hcl.Tokens(10, SyntaxHighlighting.TypeKeyword)), "default", "HCL: the default value");
        Equal(Join(hcl.Tokens(10, SyntaxHighlighting.TypeString)), "\"eu-west-1\"", "HCL: the default string");
        Equal(Join(hcl.Tokens(13, SyntaxHighlighting.TypeKeyword)), "resource", "HCL: the resource block");
        Equal(Join(hcl.Tokens(13, SyntaxHighlighting.TypeString)), "\"aws_instance\"|\"web\"", "HCL: the resource labels");
        Equal(Join(hcl.Tokens(16, SyntaxHighlighting.TypeKeyword)), "count", "HCL: count");
        Equal(Join(hcl.Tokens(16, SyntaxHighlighting.TypeNumber)), "2", "HCL: the count value");
        Equal(Join(hcl.Tokens(19, SyntaxHighlighting.TypeString)), "\"web-${count.index}\"", "HCL: interpolation stays inside the string");
        Equal(Join(hcl.Tokens(23, SyntaxHighlighting.TypeString)), "    #!/bin/bash", "HCL: a heredoc line with a # is not a comment");
        Equal(Join(hcl.Tokens(24, SyntaxHighlighting.TypeString)), "    echo \"hello\"", "HCL: heredoc quotes stay text");
        Equal(Join(hcl.Tokens(25, SyntaxHighlighting.TypeKeyword)), "  EOF", "HCL: an indented heredoc terminator");
        Check(Folds(hcl).Length > 0, "HCL blocks fold");
        Equal(Join(hcl.Tokens(29, SyntaxHighlighting.TypeKeyword)), "for|in", "HCL: a for expression");
        Equal(Join(hcl.Tokens(33, SyntaxHighlighting.TypeKeyword)), "for|in|upper", "HCL: functions in a for expression");
        Equal(Join(hcl.Tokens(33, SyntaxHighlighting.TypeVariable)), "var", "HCL: the var object");
        Equal(Join(hcl.Tokens(38, SyntaxHighlighting.TypeKeyword)), "merge", "HCL: merge()");
        Equal(Join(hcl.Tokens(38, SyntaxHighlighting.TypeVariable)), "local", "HCL: the local object");
        Equal(Join(hcl.Tokens(38, SyntaxHighlighting.TypeString)), "\"logs\"", "HCL: a string inside braces");

        Highlighted heredoc = new Highlighted(L.Hcl, "x = <<EOF\nnot a # comment\nEOF\n");
        Equal(Join(heredoc.Tokens(1, SyntaxHighlighting.TypeString)), "not a # comment", "HCL: a heredoc body ignores comments");
        Equal(Join(heredoc.Tokens(2, SyntaxHighlighting.TypeKeyword)), "EOF", "HCL: a heredoc terminator");

        Highlighted heredocComment = new Highlighted(L.Hcl, "x = <<EOF # note\nbody\nEOF\n");
        Equal(Join(heredocComment.Tokens(0, SyntaxHighlighting.TypeComment)), "# note", "HCL: a comment after a heredoc opener");
        Equal(Join(heredocComment.Tokens(1, SyntaxHighlighting.TypeString)), "body", "HCL: the body after a commented opener");

        Check(SyntaxHighlighting.GetKeywords(L.Hcl).Contains("resource"), "HCL keywords contain 'resource'");
        Check(SyntaxHighlighting.GetKeywords(L.Hcl).Contains("jsonencode"), "HCL keywords contain 'jsonencode'");
        Check(SyntaxHighlighting.GetKeywords(L.Hcl).Contains("for_each"), "HCL keywords contain 'for_each'");
        Check(SyntaxHighlighting.GetKeywords(L.Hcl).Contains("var"), "HCL keywords contain 'var'");
        Equal(Join(CommentStyle.ForLanguage(L.Hcl).Toggle(new[] { "resource \"aws_instance\" \"web\" {}" })), "# resource \"aws_instance\" \"web\" {}", "HCL comment");
    }

    private static void MarkdownChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/README.md"), L.Markdown, ".md is Markdown");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/notes.markdown"), L.Markdown, ".markdown is Markdown");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/notes.mdown"), L.Markdown, ".mdown is Markdown");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/notes.mkd"), L.Markdown, ".mkd is Markdown");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/notes.mkdn"), L.Markdown, ".mkdn is Markdown");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/notes.mdwn"), L.Markdown, ".mdwn is Markdown");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/README.MD"), L.Markdown, "the extension is matched without regard to case");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/README.md.bak"), L.Plain, "a backup of a Markdown file is prose");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/notes.txt"), L.Plain, "text files stay plain");

        Highlighted md = new Highlighted(L.Markdown,
            "# Title\n" +
            "## Section\n" +
            "\n" +
            "A paragraph with **bold**, *italic*, `code` and ~~gone~~.\n" +
            "\n" +
            "> A quote\n" +
            "> - [x] done\n" +
            "\n" +
            "- item\n" +
            "- [ ] task\n" +
            "1. first\n" +
            "2) second\n" +
            "\n" +
            "```c\n" +
            "int main(void) { return 0; }\n" +
            "```\n" +
            "\n" +
            "[link](https://example.com) ![alt](image.png) <https://example.org>\n" +
            "\n" +
            "| Name | Value |\n" +
            "| --- | ---: |\n" +
            "| a | 1 |\n" +
            "\n" +
            "<!-- a comment\n" +
            "that spans lines -->\n" +
            "\n" +
            "Setext heading\n" +
            "===\n");
        Equal(Join(md.Tokens(0, SyntaxHighlighting.TypeSection)), "# Title", "Markdown: a level-one heading");
        Equal(Join(md.Tokens(1, SyntaxHighlighting.TypeSection)), "## Section", "Markdown: a level-two heading");
        Equal(Join(md.Tokens(3, SyntaxHighlighting.TypeKeyword)), "**bold**", "Markdown: bold");
        Equal(Join(md.Tokens(3, SyntaxHighlighting.TypeVariable)), "*italic*", "Markdown: italic");
        Equal(Join(md.Tokens(3, SyntaxHighlighting.TypeString)), "`code`", "Markdown: inline code");
        Equal(Join(md.Tokens(3, SyntaxHighlighting.TypeComment)), "~~gone~~", "Markdown: strikethrough");
        Equal(Join(md.Tokens(5, SyntaxHighlighting.TypeComment)), "> ", "Markdown: a quote marker");
        Equal(Join(md.Tokens(6, SyntaxHighlighting.TypeComment)), "> ", "Markdown: a quote marker with a list");
        Equal(Join(md.Tokens(6, SyntaxHighlighting.TypeKeyword)), "-", "Markdown: a task list bullet");
        Equal(Join(md.Tokens(6, SyntaxHighlighting.TypePreprocessor)), "[x]", "Markdown: a checked task");
        Equal(Join(md.Tokens(8, SyntaxHighlighting.TypeKeyword)), "-", "Markdown: an unordered list bullet");
        Equal(Join(md.Tokens(9, SyntaxHighlighting.TypePreprocessor)), "[ ]", "Markdown: an unchecked task");
        Equal(Join(md.Tokens(10, SyntaxHighlighting.TypeNumber)), "1", "Markdown: an ordered list number");
        Equal(Join(md.Tokens(10, SyntaxHighlighting.TypeKeyword)), ".", "Markdown: an ordered list dot");
        Equal(Join(md.Tokens(11, SyntaxHighlighting.TypeNumber)), "2", "Markdown: another ordered list number");
        Equal(Join(md.Tokens(11, SyntaxHighlighting.TypeKeyword)), ")", "Markdown: an ordered list parenthesis");
        Equal(Join(md.Tokens(13, SyntaxHighlighting.TypePreprocessor)), "```", "Markdown: a fence");
        Equal(Join(md.Tokens(13, SyntaxHighlighting.TypeString)), "c", "Markdown: the fence language");
        Equal(Join(md.Tokens(14, SyntaxHighlighting.TypeString)), "int main(void) { return 0; }", "Markdown: a code line");
        Equal(Join(md.Tokens(15, SyntaxHighlighting.TypePreprocessor)), "```", "Markdown: the closing fence");
        Equal(Join(md.Tokens(17, SyntaxHighlighting.TypeKeyword)), "[|]|![|]", "Markdown: link brackets");
        Equal(Join(md.Tokens(17, SyntaxHighlighting.TypeString)), "(https://example.com)|(image.png)|<https://example.org>", "Markdown: link targets and autolinks");
        Equal(Join(md.Tokens(19, SyntaxHighlighting.TypeKeyword)), "|||||", "Markdown: three table pipes");
        Equal(Join(md.Tokens(20, SyntaxHighlighting.TypeSection)), "| --- | ---: |", "Markdown: a table separator");
        Equal(Join(md.Tokens(23, SyntaxHighlighting.TypeComment)), "<!-- a comment", "Markdown: an HTML comment");
        Equal(Join(md.Tokens(24, SyntaxHighlighting.TypeComment)), "that spans lines -->", "Markdown: the second line of an HTML comment");
        Equal(Join(md.Tokens(27, SyntaxHighlighting.TypeSection)), "===", "Markdown: a Setext underline");

        Check(Folds(md).Length == 0, "Markdown does not fold");
        Equal(Join(CommentStyle.ForLanguage(L.Markdown).Toggle(new[] { "text" })), "<!-- text -->", "Markdown comment");
    }

    private static void StarlarkChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/repo/BUILD"), L.Starlark, "BUILD is Starlark");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/BUILD.bazel"), L.Starlark, "BUILD.bazel is Starlark");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/WORKSPACE"), L.Starlark, "WORKSPACE is Starlark");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/WORKSPACE.bazel"), L.Starlark, "WORKSPACE.bazel is Starlark");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/WORKSPACE.bzlmod"), L.Starlark, "WORKSPACE.bzlmod is Starlark");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/MODULE.bazel"), L.Starlark, "MODULE.bazel is Starlark");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/REPO.bazel"), L.Starlark, "REPO.bazel is Starlark");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/tools/build_defs.bzl"), L.Starlark, ".bzl is Starlark");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/tools/defs.star"), L.Starlark, ".star is Starlark");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/TARGETS"), L.Starlark, "TARGETS is Starlark");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/BUCK"), L.Starlark, "BUCK is Starlark");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/BUILD.BAK"), L.Plain, "a backup of BUILD is not Starlark");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/.bazelrc"), L.Plain, ".bazelrc is not Starlark");

        Highlighted starlark = new Highlighted(L.Starlark,
            "# A Bazel build file\n" +
            "load(\"@rules_cc//cc:defs.bzl\", \"cc_library\")\n" +
            "package(default_visibility = [\"//visibility:public\"])\n" +
            "\n" +
            "cc_library(\n" +
            "    name = \"hello\",\n" +
            "    srcs = glob([\"*.cc\", \"*.h\"]),\n" +
            "    deps = select({\"//conditions:default\": []}),\n" +
            ")\n" +
            "\n" +
            "def _impl(ctx):\n" +
            "    \"\"\"Make the outputs.\"\"\"\n" +
            "    if ctx.attr.enabled:\n" +
            "        return [DefaultInfo(files = depset([ctx.outputs.out]))]\n" +
            "    else:\n" +
            "        fail(\"disabled\")\n" +
            "\n" +
            "count = 3\n" +
            "result = [x * 2 for x in range(count) if x > 0]\n");
        Equal(Join(starlark.Tokens(0, SyntaxHighlighting.TypeComment)), "# A Bazel build file", "Starlark: a comment");
        Equal(Join(starlark.Tokens(1, SyntaxHighlighting.TypeKeyword)), "load", "Starlark: load");
        Equal(Join(starlark.Tokens(1, SyntaxHighlighting.TypeString)), "\"@rules_cc//cc:defs.bzl\"|\"cc_library\"", "Starlark: load strings");
        Equal(Join(starlark.Tokens(2, SyntaxHighlighting.TypeKeyword)), "package", "Starlark: package()");
        Equal(Join(starlark.Tokens(4, SyntaxHighlighting.TypeKeyword)), "cc_library", "Starlark: a rule call");
        Equal(Join(starlark.Tokens(6, SyntaxHighlighting.TypeKeyword)), "glob", "Starlark: glob()");
        Equal(Join(starlark.Tokens(6, SyntaxHighlighting.TypeString)), "\"*.cc\"|\"*.h\"", "Starlark: glob patterns");
        Equal(Join(starlark.Tokens(7, SyntaxHighlighting.TypeKeyword)), "select", "Starlark: select()");
        Equal(Join(starlark.Tokens(7, SyntaxHighlighting.TypeString)), "\"//conditions:default\"", "Starlark: a select key");
        Equal(Join(starlark.Tokens(10, SyntaxHighlighting.TypeKeyword)), "def", "Starlark: def");
        Equal(Join(starlark.Tokens(11, SyntaxHighlighting.TypeString)), "\"\"\"Make the outputs.\"\"\"", "Starlark: a docstring");
        Equal(Join(starlark.Tokens(12, SyntaxHighlighting.TypeKeyword)), "if", "Starlark: if");
        Equal(Join(starlark.Tokens(13, SyntaxHighlighting.TypeKeyword)), "return|depset", "Starlark: return and depset()");
        Equal(Join(starlark.Tokens(14, SyntaxHighlighting.TypeKeyword)), "else", "Starlark: else");
        Equal(Join(starlark.Tokens(15, SyntaxHighlighting.TypeKeyword)), "fail", "Starlark: fail()");
        Equal(Join(starlark.Tokens(15, SyntaxHighlighting.TypeString)), "\"disabled\"", "Starlark: the failure message");
        Equal(Join(starlark.Tokens(17, SyntaxHighlighting.TypeNumber)), "3", "Starlark: a number");
        Equal(Join(starlark.Tokens(18, SyntaxHighlighting.TypeKeyword)), "for|in|range|if", "Starlark: a comprehension");
        Equal(Join(starlark.Tokens(18, SyntaxHighlighting.TypeNumber)), "2|0", "Starlark: the numbers of a comprehension");
        Check(Folds(starlark).Length > 0, "Starlark blocks fold");

        Highlighted doc = new Highlighted(L.Starlark, "def f():\n    \"\"\"Line one\n    line two\"\"\"\n    return 1\n");
        Equal(Join(doc.Tokens(1, SyntaxHighlighting.TypeString)), "\"\"\"Line one", "Starlark: a multi-line string");
        Equal(Join(doc.Tokens(2, SyntaxHighlighting.TypeString)), "    line two\"\"\"", "Starlark: the second line of a string");
        Equal(Join(doc.Tokens(3, SyntaxHighlighting.TypeNumber)), "1", "Starlark: code after a string");

        Check(SyntaxHighlighting.GetKeywords(L.Starlark).Contains("load"), "Starlark keywords contain 'load'");
        Check(SyntaxHighlighting.GetKeywords(L.Starlark).Contains("cc_library"), "Starlark keywords contain 'cc_library'");
        Check(SyntaxHighlighting.GetKeywords(L.Starlark).Contains("depset"), "Starlark keywords contain 'depset'");
        Equal(Join(CommentStyle.ForLanguage(L.Starlark).Toggle(new[] { "fail(\"no\")" })), "# fail(\"no\")", "Starlark comment");
    }

    private static void NixChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/nixos/configuration.nix"), L.Nix, ".nix is Nix");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/flake.nix"), L.Nix, "flake.nix is Nix");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/shell.nix"), L.Nix, "shell.nix is Nix");
        Equal(SyntaxHighlighting.DetectLanguage("/repo/default.nix.bak"), L.Plain, "a backup of a .nix file is not Nix");

        Highlighted nix = new Highlighted(L.Nix,
            "# A NixOS module\n" +
            "{ config, pkgs, lib, ... }:\n" +
            "let\n" +
            "  inherit (pkgs) hello;\n" +
            "  name = \"world\";\n" +
            "  message = \"Hello, ${name}!\";\n" +
            "in\n" +
            "{\n" +
            "  environment.systemPackages = [ pkgs.hello pkgs.git ];\n" +
            "  services.nginx.enable = true;\n" +
            "  boot.kernelPackages = pkgs.linuxPackages_latest;\n" +
            "  text = ''\n" +
            "    # not a comment\n" +
            "    echo \"hello\"\n" +
            "    ''${literal}\n" +
            "  '';\n" +
            "  path = ./default.nix;\n" +
            "  store = /nix/store/abc-source;\n" +
            "  source = <nixpkgs>;\n" +
            "  version = 1.5;\n" +
            "  mapped = builtins.map (x: x.name) [ ];\n" +
            "  ratio = a / b;\n" +
            "  /* a block comment\n" +
            "     still a comment */\n" +
            "}\n");
        Equal(Join(nix.Tokens(0, SyntaxHighlighting.TypeComment)), "# A NixOS module", "Nix: a # comment");
        Equal(Join(nix.Tokens(2, SyntaxHighlighting.TypeKeyword)), "let", "Nix: let");
        Equal(Join(nix.Tokens(3, SyntaxHighlighting.TypeKeyword)), "inherit", "Nix: inherit");
        Equal(Join(nix.Tokens(4, SyntaxHighlighting.TypeString)), "\"world\"", "Nix: a string");
        Equal(Join(nix.Tokens(5, SyntaxHighlighting.TypeString)), "\"Hello, ${name}!\"", "Nix: interpolation stays inside the string");
        Equal(Join(nix.Tokens(6, SyntaxHighlighting.TypeKeyword)), "in", "Nix: in");
        Equal(Join(nix.Tokens(9, SyntaxHighlighting.TypeKeyword)), "true", "Nix: true");
        Equal(Join(nix.Tokens(11, SyntaxHighlighting.TypeString)), "''", "Nix: the start of an indented string");
        Equal(Join(nix.Tokens(12, SyntaxHighlighting.TypeString)), "    # not a comment", "Nix: a # inside an indented string is text");
        Equal(Join(nix.Tokens(13, SyntaxHighlighting.TypeString)), "    echo \"hello\"", "Nix: quotes inside an indented string are text");
        Equal(Join(nix.Tokens(14, SyntaxHighlighting.TypeString)), "    ''${literal}", "Nix: an escaped interpolation stays in the string");
        Equal(Join(nix.Tokens(15, SyntaxHighlighting.TypeString)), "  ''", "Nix: the end of an indented string");
        Equal(Join(nix.Tokens(16, SyntaxHighlighting.TypeString)), "./default.nix", "Nix: a relative path");
        Equal(Join(nix.Tokens(17, SyntaxHighlighting.TypeString)), "/nix/store/abc-source", "Nix: an absolute path");
        Equal(Join(nix.Tokens(18, SyntaxHighlighting.TypeString)), "<nixpkgs>", "Nix: a search path");
        Equal(Join(nix.Tokens(19, SyntaxHighlighting.TypeNumber)), "1.5", "Nix: a number");
        Equal(Join(nix.Tokens(20, SyntaxHighlighting.TypeVariable)), "builtins", "Nix: the builtins object");
        Equal(Join(nix.Tokens(20, SyntaxHighlighting.TypeKeyword)), "map", "Nix: map()");
        Equal(Join(nix.Tokens(21, SyntaxHighlighting.TypeString)), "", "Nix: division is not a path");
        Equal(Join(nix.Tokens(22, SyntaxHighlighting.TypeComment)), "/* a block comment", "Nix: a block comment");
        Equal(Join(nix.Tokens(23, SyntaxHighlighting.TypeComment)), "     still a comment */", "Nix: the second line of a block comment");
        Check(Folds(nix).Length > 0, "Nix attrsets fold");

        Highlighted multi = new Highlighted(L.Nix, "x = \"line one\nline two\";\n");
        Equal(Join(multi.Tokens(1, SyntaxHighlighting.TypeString)), "line two\"", "Nix: a multi-line string");
        Equal(Join(multi.Tokens(1, SyntaxHighlighting.TypeNormal)), "", "Nix: the string is one token");

        Check(SyntaxHighlighting.GetKeywords(L.Nix).Contains("inherit"), "Nix keywords contain 'inherit'");
        Check(SyntaxHighlighting.GetKeywords(L.Nix).Contains("derivation"), "Nix keywords contain 'derivation'");
        Check(SyntaxHighlighting.GetKeywords(L.Nix).Contains("builtins"), "Nix keywords contain 'builtins'");
        Equal(Join(CommentStyle.ForLanguage(L.Nix).Toggle(new[] { "pkgs.hello" })), "# pkgs.hello", "Nix comment");
    }

    private static void FishChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/.config/fish/config.fish"), L.Fish, ".fish is Fish");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/scripts/deploy.fish"), L.Fish, "a .fish script");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/fish"), L.Fish, "#!/usr/bin/fish is Fish");
        Equal(SyntaxHighlighting.DetectLanguage("/usr/local/bin/tool", "#!/usr/bin/env fish"), L.Fish, "env fish is Fish");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/config.fish.bak"), L.Plain, "a backup of a .fish file is plain");

        Highlighted fish = new Highlighted(L.Fish,
            "# Fish configuration\n" +
            "set -gx EDITOR vim\n" +
            "set -l now (date +%s)\n" +
            "function greet --description \"Say hello\"\n" +
            "    echo \"Hello, $USER\"\n" +
            "    for file in $files\n" +
            "        if test -f $file\n" +
            "            echo $file\n" +
            "        else if test -d $file\n" +
            "            echo \"dir: $file\"\n" +
            "        end\n" +
            "    end\n" +
            "end\n" +
            "\n" +
            "switch $argv[1]\n" +
            "    case start\n" +
            "        echo start\n" +
            "    case stop\n" +
            "        echo stop\n" +
            "end\n");
        Equal(Join(fish.Tokens(0, SyntaxHighlighting.TypeComment)), "# Fish configuration", "Fish: a comment");
        Equal(Join(fish.Tokens(1, SyntaxHighlighting.TypeKeyword)), "set", "Fish: set");
        Equal(Join(fish.Tokens(2, SyntaxHighlighting.TypeKeyword)), "set|date", "Fish: a command substitution");
        Equal(Join(fish.Tokens(3, SyntaxHighlighting.TypeKeyword)), "function", "Fish: function");
        Equal(Join(fish.Tokens(3, SyntaxHighlighting.TypeString)), "\"Say hello\"", "Fish: a quoted description");
        Equal(Join(fish.Tokens(4, SyntaxHighlighting.TypeString)), "\"Hello, $USER\"", "Fish: a double-quoted string");
        Equal(Join(fish.Tokens(5, SyntaxHighlighting.TypeKeyword)), "for|in", "Fish: for ... in");
        Equal(Join(fish.Tokens(5, SyntaxHighlighting.TypeVariable)), "$files", "Fish: a variable");
        Equal(Join(fish.Tokens(6, SyntaxHighlighting.TypeKeyword)), "if|test", "Fish: if test");
        Equal(Join(fish.Tokens(6, SyntaxHighlighting.TypeVariable)), "$file", "Fish: the tested variable");
        Equal(Join(fish.Tokens(8, SyntaxHighlighting.TypeKeyword)), "else|if|test", "Fish: else if");
        Equal(Join(fish.Tokens(10, SyntaxHighlighting.TypeKeyword)), "end", "Fish: end");
        Equal(Join(fish.Tokens(14, SyntaxHighlighting.TypeKeyword)), "switch", "Fish: switch");
        Equal(Join(fish.Tokens(14, SyntaxHighlighting.TypeVariable)), "$argv", "Fish: argv");
        Equal(Join(fish.Tokens(14, SyntaxHighlighting.TypeNumber)), "1", "Fish: a slice index");
        Equal(Join(fish.Tokens(15, SyntaxHighlighting.TypeKeyword)), "case", "Fish: case");
        Equal(Join(fish.Tokens(16, SyntaxHighlighting.TypeKeyword)), "echo", "Fish: echo");
        Check(Folds(fish).Length > 0, "Fish blocks fold");

        Check(SyntaxHighlighting.GetKeywords(L.Fish).Contains("function"), "Fish keywords contain 'function'");
        Check(SyntaxHighlighting.GetKeywords(L.Fish).Contains("test"), "Fish keywords contain 'test'");
        Check(SyntaxHighlighting.GetKeywords(L.Fish).Contains("string"), "Fish keywords contain 'string'");
        Equal(Join(CommentStyle.ForLanguage(L.Fish).Toggle(new[] { "echo hi" })), "# echo hi", "Fish comment");
    }

    private static void BatchChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/build.bat"), L.Batch, ".bat is Batch");
        Equal(SyntaxHighlighting.DetectLanguage("C:\\tools\\deploy.cmd"), L.Batch, ".cmd is Batch");
        Equal(SyntaxHighlighting.DetectLanguage("C:\\tools\\deploy.BAT"), L.Batch, ".BAT is Batch");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/build.bat.bak"), L.Plain, "a backup of a .bat file is plain");

        Highlighted batch = new Highlighted(L.Batch,
            "@echo off\n" +
            "REM Build the project\n" +
            ":: old-style comment\n" +
            "set \"APP=MyApp\"\n" +
            "set COUNT=3\n" +
            "if not defined DEBUG (\n" +
            "    echo Building %APP% %1\n" +
            ")\n" +
            "goto :build\n" +
            ":build\n" +
            "for %%i in (*.c) do echo %%~ni\n" +
            "echo !COUNT! %~dp0\n");
        Equal(Join(batch.Tokens(0, SyntaxHighlighting.TypeKeyword)), "echo|off", "Batch: @echo off");
        Equal(Join(batch.Tokens(1, SyntaxHighlighting.TypeComment)), "REM Build the project", "Batch: a REM comment");
        Equal(Join(batch.Tokens(2, SyntaxHighlighting.TypeComment)), ":: old-style comment", "Batch: a :: comment");
        Equal(Join(batch.Tokens(3, SyntaxHighlighting.TypeKeyword)), "set", "Batch: set");
        Equal(Join(batch.Tokens(3, SyntaxHighlighting.TypeString)), "\"APP=MyApp\"", "Batch: a quoted assignment");
        Equal(Join(batch.Tokens(4, SyntaxHighlighting.TypeNumber)), "3", "Batch: a number");
        Equal(Join(batch.Tokens(5, SyntaxHighlighting.TypeKeyword)), "if|not|defined", "Batch: if not defined");
        Equal(Join(batch.Tokens(6, SyntaxHighlighting.TypeKeyword)), "echo", "Batch: echo");
        Equal(Join(batch.Tokens(6, SyntaxHighlighting.TypeVariable)), "%APP%|%1", "Batch: %NAME% and %1");
        Equal(Join(batch.Tokens(8, SyntaxHighlighting.TypeKeyword)), "goto", "Batch: goto");
        Equal(Join(batch.Tokens(8, SyntaxHighlighting.TypeSection)), ":build", "Batch: a goto target");
        Equal(Join(batch.Tokens(9, SyntaxHighlighting.TypeSection)), ":build", "Batch: a label");
        Equal(Join(batch.Tokens(10, SyntaxHighlighting.TypeKeyword)), "for|in|do|echo", "Batch: for ... in ... do");
        Equal(Join(batch.Tokens(10, SyntaxHighlighting.TypeVariable)), "%%i|%%~ni", "Batch: loop variables");
        Equal(Join(batch.Tokens(11, SyntaxHighlighting.TypeVariable)), "!COUNT!|%~dp0", "Batch: delayed expansion and the script directory");
        Check(Folds(batch).Length == 0, "Batch does not fold");

        Highlighted words = new Highlighted(L.Batch, "echo REM hello\n");
        Equal(Join(words.Tokens(0, SyntaxHighlighting.TypeKeyword)), "echo", "Batch: REM after echo is an argument");
        Equal(Join(words.Tokens(0, SyntaxHighlighting.TypeComment)), "", "Batch: REM after echo is not a comment");

        Check(SyntaxHighlighting.GetKeywords(L.Batch).Contains("echo"), "Batch keywords contain 'echo'");
        Check(SyntaxHighlighting.GetKeywords(L.Batch).Contains("setlocal"), "Batch keywords contain 'setlocal'");
        Highlighted upper = new Highlighted(L.Batch, "ECHO OFF\n");
        Equal(Join(upper.Tokens(0, SyntaxHighlighting.TypeKeyword)), "ECHO|OFF", "Batch keywords are case-insensitive");
        Equal(Join(CommentStyle.ForLanguage(L.Batch).Toggle(new[] { "echo hi" })), "REM echo hi", "Batch comment");
    }

    private static void KubernetesChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/k8s/deployment.yaml"), L.Kubernetes, "a manifest under /k8s/");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/kubernetes/service.yml"), L.Kubernetes, "a manifest under /kubernetes/");
        Equal(SyntaxHighlighting.DetectLanguage("/opt/app/manifests/configmap.yaml"), L.Kubernetes, "a manifest under /manifests/");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/charts/web/templates/deployment.yaml"), L.Kubernetes, "a Helm template");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/charts/web/Chart.yaml"), L.Kubernetes, "the metadata of a Helm chart");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/charts/web/values.yaml"), L.Kubernetes, "the values of a Helm chart");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/kustomization.yaml"), L.Kubernetes, "a kustomization");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/deployment.yaml"), L.Kubernetes, "a deployment by its name");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/notes.yaml"), L.Yaml, "an ordinary .yaml stays YAML");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/site.yml"), L.Yaml, "an ordinary .yml stays YAML");

        Highlighted manifest = new Highlighted(L.Kubernetes,
            "# web\napiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: web\n  labels:\n    app: web\nspec:\n  replicas: 3\n  selector:\n    matchLabels:\n      app: web\n  template:\n    metadata:\n      labels:\n        app: web\n    spec:\n      containers:\n      - name: web\n        image: nginx:1.25\n        ports:\n        - containerPort: 80\n        env:\n        - name: MODE\n          value: \"prod\"\n{{- include \"web.labels\" . | nindent 4 }}");
        Equal(Join(manifest.Tokens(0, SyntaxHighlighting.TypeComment)), "# web", "Kubernetes: a comment");
        Equal(Join(manifest.Tokens(1, SyntaxHighlighting.TypeKeyword)), "apiVersion|v1", "Kubernetes: apiVersion and the version");
        Equal(Join(manifest.Tokens(2, SyntaxHighlighting.TypeKeyword)), "kind|Deployment", "Kubernetes: the kind");
        Equal(Join(manifest.Tokens(3, SyntaxHighlighting.TypeKeyword)), "metadata", "Kubernetes: metadata");
        Equal(Join(manifest.Tokens(4, SyntaxHighlighting.TypeKeyword)), "name", "Kubernetes: a name");
        Equal(Join(manifest.Tokens(6, SyntaxHighlighting.TypeVariable)), "app", "Kubernetes: an unknown key stays an ordinary key");
        Equal(Join(manifest.Tokens(7, SyntaxHighlighting.TypeKeyword)), "spec", "Kubernetes: spec");
        Equal(Join(manifest.Tokens(8, SyntaxHighlighting.TypeKeyword)), "replicas", "Kubernetes: replicas");
        Equal(Join(manifest.Tokens(8, SyntaxHighlighting.TypeNumber)), "3", "Kubernetes: the number of replicas");
        Equal(Join(manifest.Tokens(10, SyntaxHighlighting.TypeKeyword)), "matchLabels", "Kubernetes: matchLabels");
        Equal(Join(manifest.Tokens(17, SyntaxHighlighting.TypeKeyword)), "containers", "Kubernetes: containers");
        Equal(Join(manifest.Tokens(18, SyntaxHighlighting.TypeKeyword)), "name", "Kubernetes: a container name");
        Equal(Join(manifest.Tokens(19, SyntaxHighlighting.TypeKeyword)), "image", "Kubernetes: image");
        Equal(Join(manifest.Tokens(19, SyntaxHighlighting.TypeNumber)), "1.25", "Kubernetes: the tag of the image");
        Equal(Join(manifest.Tokens(21, SyntaxHighlighting.TypeKeyword)), "containerPort", "Kubernetes: containerPort");
        Equal(Join(manifest.Tokens(21, SyntaxHighlighting.TypeNumber)), "80", "Kubernetes: the port");
        Equal(Join(manifest.Tokens(24, SyntaxHighlighting.TypeKeyword)), "value", "Kubernetes: value");
        Equal(Join(manifest.Tokens(24, SyntaxHighlighting.TypeString)), "\"prod\"", "Kubernetes: a quoted value");
        Equal(Join(manifest.Tokens(25, SyntaxHighlighting.TypeKeyword)), "include|nindent", "Kubernetes: the Helm template functions");
        Equal(Join(manifest.Tokens(25, SyntaxHighlighting.TypeString)), "\"web.labels\"", "Kubernetes: the name of the template");
        Check(SyntaxHighlighting.GetKeywords(L.Kubernetes).Contains("apiVersion"), "Kubernetes keywords contain 'apiVersion'");
        Check(SyntaxHighlighting.GetKeywords(L.Kubernetes).Contains("containers"), "Kubernetes keywords contain 'containers'");
        Check(SyntaxHighlighting.GetKeywords(L.Kubernetes).Contains("Deployment"), "Kubernetes keywords contain 'Deployment'");
        Equal(Join(CommentStyle.ForLanguage(L.Kubernetes).Toggle(new[] { "kind: Deployment" })), "# kind: Deployment", "Kubernetes comment");
    }

    private static void DockerComposeChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/srv/docker-compose.yml"), L.DockerCompose, "docker-compose.yml");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/docker-compose.yaml"), L.DockerCompose, "docker-compose.yaml");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/compose.yml"), L.DockerCompose, "compose.yml");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/compose.yaml"), L.DockerCompose, "compose.yaml");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/docker-compose.override.yml"), L.DockerCompose, "an override file");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/compose.prod.yaml"), L.DockerCompose, "a per-environment file");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app.yml"), L.Yaml, "an ordinary .yml stays YAML");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/charts/web/values.yaml"), L.Kubernetes, "Helm values stay Kubernetes");

        Highlighted compose = new Highlighted(L.DockerCompose,
            "# compose\nversion: \"3.9\"\nservices:\n  web:\n    image: nginx:1.25\n    build:\n      context: .\n      dockerfile: Dockerfile\n    ports:\n      - \"80:80\"\n    environment:\n      MY_SETTING: prod\n    depends_on:\n      - db\n    restart: unless-stopped\n    healthcheck:\n      test: [\"CMD\", \"curl\", \"-f\", \"http://localhost\"]\n      interval: 30s\n      retries: 3\n    deploy:\n      replicas: 2\n      resources:\n        limits:\n          cpus: \"0.5\"\n          memory: 256M\n  db:\n    image: postgres:16\n    volumes:\n      - data:/var/lib/postgresql/data\nvolumes:\n  data: {}\nnetworks:\n  default:\n    driver: bridge");
        Equal(Join(compose.Tokens(0, SyntaxHighlighting.TypeComment)), "# compose", "Compose: a comment");
        Equal(Join(compose.Tokens(1, SyntaxHighlighting.TypeKeyword)), "version", "Compose: the version");
        Equal(Join(compose.Tokens(1, SyntaxHighlighting.TypeString)), "\"3.9\"", "Compose: the version number");
        Equal(Join(compose.Tokens(2, SyntaxHighlighting.TypeKeyword)), "services", "Compose: services");
        Equal(Join(compose.Tokens(3, SyntaxHighlighting.TypeVariable)), "web", "Compose: a service name stays an ordinary key");
        Equal(Join(compose.Tokens(4, SyntaxHighlighting.TypeKeyword)), "image", "Compose: image");
        Equal(Join(compose.Tokens(4, SyntaxHighlighting.TypeNumber)), "1.25", "Compose: the tag of the image");
        Equal(Join(compose.Tokens(5, SyntaxHighlighting.TypeKeyword)), "build", "Compose: build");
        Equal(Join(compose.Tokens(9, SyntaxHighlighting.TypeString)), "\"80:80\"", "Compose: a published port");
        Equal(Join(compose.Tokens(10, SyntaxHighlighting.TypeKeyword)), "environment", "Compose: environment");
        Equal(Join(compose.Tokens(11, SyntaxHighlighting.TypeVariable)), "MY_SETTING", "Compose: an environment variable stays an ordinary key");
        Equal(Join(compose.Tokens(12, SyntaxHighlighting.TypeKeyword)), "depends_on", "Compose: depends_on");
        Equal(Join(compose.Tokens(14, SyntaxHighlighting.TypeKeyword)), "restart|unless-stopped", "Compose: the restart policy");
        Equal(Join(compose.Tokens(15, SyntaxHighlighting.TypeKeyword)), "healthcheck", "Compose: healthcheck");
        Equal(Join(compose.Tokens(16, SyntaxHighlighting.TypeKeyword)), "test", "Compose: the health test");
        Equal(Join(compose.Tokens(17, SyntaxHighlighting.TypeKeyword)), "interval", "Compose: the health interval");
        Equal(Join(compose.Tokens(18, SyntaxHighlighting.TypeKeyword)), "retries", "Compose: the health retries");
        Equal(Join(compose.Tokens(18, SyntaxHighlighting.TypeNumber)), "3", "Compose: the number of retries");
        Equal(Join(compose.Tokens(19, SyntaxHighlighting.TypeKeyword)), "deploy", "Compose: deploy");
        Equal(Join(compose.Tokens(20, SyntaxHighlighting.TypeKeyword)), "replicas", "Compose: replicas");
        Equal(Join(compose.Tokens(20, SyntaxHighlighting.TypeNumber)), "2", "Compose: the number of replicas");
        Equal(Join(compose.Tokens(23, SyntaxHighlighting.TypeKeyword)), "cpus", "Compose: a cpu limit");
        Equal(Join(compose.Tokens(23, SyntaxHighlighting.TypeString)), "\"0.5\"", "Compose: the cpu value");
        Equal(Join(compose.Tokens(27, SyntaxHighlighting.TypeKeyword)), "volumes", "Compose: the volumes of a service");
        Equal(Join(compose.Tokens(28, SyntaxHighlighting.TypeVariable)), "data", "Compose: a named volume");
        Equal(Join(compose.Tokens(33, SyntaxHighlighting.TypeKeyword)), "driver|bridge", "Compose: the network driver");
        Check(SyntaxHighlighting.GetKeywords(L.DockerCompose).Contains("services"), "Compose keywords contain 'services'");
        Check(SyntaxHighlighting.GetKeywords(L.DockerCompose).Contains("depends_on"), "Compose keywords contain 'depends_on'");
        Check(SyntaxHighlighting.GetKeywords(L.DockerCompose).Contains("unless-stopped"), "Compose keywords contain 'unless-stopped'");
        Equal(Join(CommentStyle.ForLanguage(L.DockerCompose).Toggle(new[] { "    image: nginx" })), "    # image: nginx", "Compose comment");
    }

    private static void AnsibleChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/srv/ansible/site.yml"), L.Ansible, "a playbook under /ansible/");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/playbooks/deploy.yaml"), L.Ansible, "a playbook under /playbooks/");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/roles/nginx/tasks/main.yml"), L.Ansible, "a task file of a role");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/inventory/group_vars/all.yml"), L.Ansible, "the variables of a group");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/ansible/deploy-playbook.yml"), L.Ansible, "a name with 'playbook'");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/handlers/main.yaml"), L.Ansible, "a handler file");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/site.yml"), L.Yaml, "an ordinary site.yml stays YAML");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/tasks.yaml"), L.Yaml, "an ordinary tasks.yaml stays YAML");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/k8s/deployment.yaml"), L.Kubernetes, "Kubernetes wins under /k8s/");

        Highlighted playbook = new Highlighted(L.Ansible,
            "# deploy\n- name: Configure web\n  hosts: web\n  become: yes\n  vars:\n    http_port: 80\n  tasks:\n    - name: Install nginx\n      apt:\n        name: nginx\n        state: present\n      notify: restart nginx\n    - name: Start the service\n      service:\n        name: nginx\n        state: started\n        enabled: yes\n    - name: Run the command\n      command: /bin/echo hello\n      when: ansible_os_family == \"Debian\"\n      register: result\n      changed_when: false\n  handlers:\n    - name: restart nginx\n      service:\n        name: nginx\n        state: restarted");
        Equal(Join(playbook.Tokens(0, SyntaxHighlighting.TypeComment)), "# deploy", "Ansible: a comment");
        Equal(Join(playbook.Tokens(1, SyntaxHighlighting.TypeKeyword)), "name", "Ansible: the name of a play");
        Equal(Join(playbook.Tokens(2, SyntaxHighlighting.TypeKeyword)), "hosts", "Ansible: hosts");
        Equal(Join(playbook.Tokens(3, SyntaxHighlighting.TypeKeyword)), "become|yes", "Ansible: become");
        Equal(Join(playbook.Tokens(4, SyntaxHighlighting.TypeKeyword)), "vars", "Ansible: vars");
        Equal(Join(playbook.Tokens(5, SyntaxHighlighting.TypeVariable)), "http_port", "Ansible: a variable stays an ordinary key");
        Equal(Join(playbook.Tokens(5, SyntaxHighlighting.TypeNumber)), "80", "Ansible: the port of the variable");
        Equal(Join(playbook.Tokens(6, SyntaxHighlighting.TypeKeyword)), "tasks", "Ansible: tasks");
        Equal(Join(playbook.Tokens(8, SyntaxHighlighting.TypeKeyword)), "apt", "Ansible: the apt module");
        Equal(Join(playbook.Tokens(10, SyntaxHighlighting.TypeKeyword)), "state|present", "Ansible: state and present");
        Equal(Join(playbook.Tokens(11, SyntaxHighlighting.TypeKeyword)), "notify", "Ansible: notify");
        Equal(Join(playbook.Tokens(13, SyntaxHighlighting.TypeKeyword)), "service", "Ansible: the service module");
        Equal(Join(playbook.Tokens(15, SyntaxHighlighting.TypeKeyword)), "state|started", "Ansible: state and started");
        Equal(Join(playbook.Tokens(16, SyntaxHighlighting.TypeKeyword)), "enabled|yes", "Ansible: enabled");
        Equal(Join(playbook.Tokens(18, SyntaxHighlighting.TypeKeyword)), "command", "Ansible: the command module");
        Equal(Join(playbook.Tokens(19, SyntaxHighlighting.TypeKeyword)), "when", "Ansible: when");
        Equal(Join(playbook.Tokens(20, SyntaxHighlighting.TypeKeyword)), "register", "Ansible: register");
        Equal(Join(playbook.Tokens(21, SyntaxHighlighting.TypeKeyword)), "changed_when|false", "Ansible: changed_when");
        Equal(Join(playbook.Tokens(22, SyntaxHighlighting.TypeKeyword)), "handlers", "Ansible: handlers");
        Equal(Join(playbook.Tokens(23, SyntaxHighlighting.TypeKeyword)), "name", "Ansible: the name of a handler");
        Equal(Join(playbook.Tokens(26, SyntaxHighlighting.TypeKeyword)), "state|restarted", "Ansible: a handler that restarts");
        Check(SyntaxHighlighting.GetKeywords(L.Ansible).Contains("hosts"), "Ansible keywords contain 'hosts'");
        Check(SyntaxHighlighting.GetKeywords(L.Ansible).Contains("apt"), "Ansible keywords contain 'apt'");
        Check(SyntaxHighlighting.GetKeywords(L.Ansible).Contains("become"), "Ansible keywords contain 'become'");
        Equal(Join(CommentStyle.ForLanguage(L.Ansible).Toggle(new[] { "  become: yes" })), "  # become: yes", "Ansible comment");
    }

    private static void PostgreSqlChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/etc/postgresql/16/main/postgresql.conf"), L.PostgreSql, "postgresql.conf");
        Equal(SyntaxHighlighting.DetectLanguage("/var/lib/pgsql/data/pg_hba.conf"), L.PostgreSql, "pg_hba.conf");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/postgresql/16/main/pg_ident.conf"), L.PostgreSql, "pg_ident.conf");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/pg_hba.conf"), L.PostgreSql, "pg_hba.conf anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/database.conf"), L.Config, "an ordinary .conf stays a config");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/schema.sql"), L.Sql, "SQL stays SQL");

        Highlighted postgres = new Highlighted(L.PostgreSql,
            "# PostgreSQL\nlisten_addresses = '*'\nmax_connections = 100\nshared_buffers = '128MB'\nwal_level = replica\nhba_file = '/etc/postgresql/pg_hba.conf' # path\nlocal all postgres peer\nhost all all 127.0.0.1/32 scram-sha-256\nhostssl appdb app 0.0.0.0/0 md5\ninclude 'extra.conf'");
        Equal(Join(postgres.Tokens(0, SyntaxHighlighting.TypeComment)), "# PostgreSQL", "PostgreSQL: a comment");
        Equal(Join(postgres.Tokens(1, SyntaxHighlighting.TypeKeyword)), "listen_addresses", "PostgreSQL: listen_addresses");
        Equal(Join(postgres.Tokens(1, SyntaxHighlighting.TypeString)), "'*'", "PostgreSQL: a quoted address");
        Equal(Join(postgres.Tokens(2, SyntaxHighlighting.TypeKeyword)), "max_connections", "PostgreSQL: max_connections");
        Equal(Join(postgres.Tokens(2, SyntaxHighlighting.TypeNumber)), "100", "PostgreSQL: a number");
        Equal(Join(postgres.Tokens(3, SyntaxHighlighting.TypeKeyword)), "shared_buffers", "PostgreSQL: shared_buffers");
        Equal(Join(postgres.Tokens(3, SyntaxHighlighting.TypeString)), "'128MB'", "PostgreSQL: a quoted size");
        Equal(Join(postgres.Tokens(5, SyntaxHighlighting.TypeString)), "'/etc/postgresql/pg_hba.conf'", "PostgreSQL: a quoted path");
        Equal(Join(postgres.Tokens(5, SyntaxHighlighting.TypeComment)), "# path", "PostgreSQL: a trailing comment");
        Equal(Join(postgres.Tokens(6, SyntaxHighlighting.TypeKeyword)), "local|all|peer", "PostgreSQL: a local hba line");
        Equal(Join(postgres.Tokens(7, SyntaxHighlighting.TypeKeyword)), "host|all|all|scram-sha-256", "PostgreSQL: a host hba line");
        Equal(Join(postgres.Tokens(7, SyntaxHighlighting.TypeNumber)), "127.0.0.1|32", "PostgreSQL: the address and the mask");
        Equal(Join(postgres.Tokens(8, SyntaxHighlighting.TypeKeyword)), "hostssl|md5", "PostgreSQL: hostssl with md5");
        Equal(Join(postgres.Tokens(9, SyntaxHighlighting.TypeKeyword)), "include", "PostgreSQL: include");
        Equal(Join(postgres.Tokens(9, SyntaxHighlighting.TypeString)), "'extra.conf'", "PostgreSQL: the included file");
        Check(SyntaxHighlighting.GetKeywords(L.PostgreSql).Contains("listen_addresses"), "PostgreSQL keywords contain 'listen_addresses'");
        Check(SyntaxHighlighting.GetKeywords(L.PostgreSql).Contains("scram-sha-256"), "PostgreSQL keywords contain 'scram-sha-256'");
        Equal(Join(CommentStyle.ForLanguage(L.PostgreSql).Toggle(new[] { "max_connections = 100" })), "# max_connections = 100", "PostgreSQL comment");
    }

    private static void TomlChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/Cargo.toml"), L.Toml, ".toml is TOML");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/pyproject.toml"), L.Toml, "pyproject.toml is TOML");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/containers/storage.conf"), L.Toml, "the containers configuration is TOML");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/Pipfile"), L.Toml, "Pipfile is TOML");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/Cargo.lock"), L.Toml, "Cargo.lock is TOML");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/poetry.lock"), L.Toml, "poetry.lock is TOML");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/notes.txt"), L.Plain, "prose stays plain");
        Check(Array.IndexOf(SyntaxHighlighting.SelectableLanguages, L.Toml) >= 0, "TOML is in the syntax menu");
        Check(SyntaxHighlighting.GetTokenizer(L.Toml) != null, "TOML has a tokenizer");

        Highlighted toml = new Highlighted(L.Toml,
            "# Cargo manifest\n[package]\nname = \"app\"\nversion = \"0.1.0\"\nedition = 2021\n[dependencies]\nserde = { version = \"1\", features = [\"derive\"] }\n[profile.release]\nlto = true\n[workspace]\nmembers = [\n  \"crates/a\", # first\n  \"crates/b\",\n]\n[[bin]]\nname = \"tool\"\npath = 'src/main.rs'\ncreated = 1979-05-27T07:32:00Z\nhex = 0xDEAD_BEEF\nratio = 1.5e-3\nnotes = \"\"\"\nline one\nline two\n\"\"\"\nphysical.color = \"orange\"\nsite.\"google.com\" = false");
        Equal(Join(toml.Tokens(0, SyntaxHighlighting.TypeComment)), "# Cargo manifest", "TOML: a comment");
        Equal(Join(toml.Tokens(1, SyntaxHighlighting.TypeSection)), "[package]", "TOML: a table");
        Equal(Join(toml.Tokens(2, SyntaxHighlighting.TypeVariable)), "name", "TOML: a bare key");
        Equal(Join(toml.Tokens(2, SyntaxHighlighting.TypeString)), "\"app\"", "TOML: a basic string");
        Equal(Join(toml.Tokens(4, SyntaxHighlighting.TypeNumber)), "2021", "TOML: an integer");
        Equal(Join(toml.Tokens(6, SyntaxHighlighting.TypeVariable)), "serde|version|features", "TOML: an inline table and its keys");
        Equal(Join(toml.Tokens(6, SyntaxHighlighting.TypeString)), "\"1\"|\"derive\"", "TOML: strings in an inline table");
        Equal(Join(toml.Tokens(8, SyntaxHighlighting.TypeKeyword)), "true", "TOML: true");
        Equal(Join(toml.Tokens(10, SyntaxHighlighting.TypeVariable)), "members", "TOML: the key of an array");
        Equal(Join(toml.Tokens(11, SyntaxHighlighting.TypeString)), "\"crates/a\"", "TOML: a string in a multi-line array");
        Equal(Join(toml.Tokens(11, SyntaxHighlighting.TypeComment)), "# first", "TOML: a comment inside an array");
        Equal(Join(toml.Tokens(14, SyntaxHighlighting.TypeSection)), "[[bin]]", "TOML: an array of tables");
        Equal(Join(toml.Tokens(16, SyntaxHighlighting.TypeString)), "'src/main.rs'", "TOML: a literal string");
        Equal(Join(toml.Tokens(17, SyntaxHighlighting.TypeNumber)), "1979-05-27T07:32:00Z", "TOML: an offset date-time");
        Equal(Join(toml.Tokens(18, SyntaxHighlighting.TypeNumber)), "0xDEAD_BEEF", "TOML: a hexadecimal integer");
        Equal(Join(toml.Tokens(19, SyntaxHighlighting.TypeNumber)), "1.5e-3", "TOML: a float");
        Equal(Join(toml.Tokens(20, SyntaxHighlighting.TypeVariable)), "notes", "TOML: the key of a multi-line string");
        Equal(Join(toml.Tokens(20, SyntaxHighlighting.TypeString)), "\"\"\"", "TOML: a multi-line string opens");
        Equal(toml.TypeAt(21, 0), SyntaxHighlighting.TypeString, "TOML: ...its text continues");
        Equal(toml.TypeAt(22, 0), SyntaxHighlighting.TypeString, "TOML: ...on every line");
        Equal(Join(toml.Tokens(23, SyntaxHighlighting.TypeString)), "\"\"\"", "TOML: ...and closes");
        Equal(Join(toml.Tokens(24, SyntaxHighlighting.TypeVariable)), "physical|color", "TOML: a dotted key");
        Equal(Join(toml.Tokens(25, SyntaxHighlighting.TypeVariable)), "site|\"google.com\"", "TOML: a quoted key of a dotted key");
        Equal(Join(toml.Tokens(25, SyntaxHighlighting.TypeKeyword)), "false", "TOML: false");

        // A literal multi-line string is raw: backslashes and quotes inside are text.
        Highlighted raw = new Highlighted(L.Toml, "pattern = '''\nC:\\new\\text\n'''\nafter = 1");
        Equal(raw.TypeAt(1, 0), SyntaxHighlighting.TypeString, "TOML: a literal multi-line string continues");
        Equal(Join(raw.Tokens(2, SyntaxHighlighting.TypeString)), "'''", "TOML: ...and closes");
        Equal(Join(raw.Tokens(3, SyntaxHighlighting.TypeVariable)), "after", "TOML: code after a multi-line string");
        Equal(Join(raw.Tokens(3, SyntaxHighlighting.TypeNumber)), "1", "TOML: the value after a multi-line string");

        // A "#" inside a string is text; outside it starts a comment.
        Highlighted hash = new Highlighted(L.Toml, "url = \"http://x/#frag\" # note\nx = 1#tight");
        Equal(Join(hash.Tokens(0, SyntaxHighlighting.TypeString)), "\"http://x/#frag\"", "TOML: # inside a string");
        Equal(Join(hash.Tokens(0, SyntaxHighlighting.TypeComment)), "# note", "TOML: a trailing comment");
        Equal(Join(hash.Tokens(1, SyntaxHighlighting.TypeComment)), "#tight", "TOML: a comment need not be preceded by a space");

        // The [ ] of an array fold; the [table] after the array is a table again.
        Highlighted folding = new Highlighted(L.Toml, "arr = [\n  1,\n  2,\n]\n[after]\nk = 1");
        Equal(Folds(folding), "0:3", "TOML: a multi-line array folds");
        Equal(Join(folding.Tokens(4, SyntaxHighlighting.TypeSection)), "[after]", "TOML: a table after an array");
        Equal(Join(folding.Tokens(5, SyntaxHighlighting.TypeVariable)), "k", "TOML: a key after an array");

        Check(SyntaxHighlighting.GetKeywords(L.Toml).Contains("true"), "TOML keywords contain 'true'");
        Check(!SyntaxHighlighting.GetKeywords(L.Toml).Contains("select"), "TOML keywords do not include the SQL 'select'");
        Equal(Join(CommentStyle.ForLanguage(L.Toml).Toggle(new[] { "name = \"app\"" })), "# name = \"app\"", "TOML comment");
    }

    private static void KconfigChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/build/.config"), L.Kconfig, "the .config of a kernel or OpenWrt build");
        Equal(SyntaxHighlighting.DetectLanguage("/src/linux/Kconfig"), L.Kconfig, "Kconfig");
        Equal(SyntaxHighlighting.DetectLanguage("/src/linux/init/Kconfig.debug"), L.Kconfig, "Kconfig.debug");
        Equal(SyntaxHighlighting.DetectLanguage("/src/linux/arch/x86/configs/x86_64_defconfig"), L.Kconfig, "a defconfig");
        Equal(SyntaxHighlighting.DetectLanguage("/src/linux/config.in"), L.Kconfig, "config.in");
        Equal(SyntaxHighlighting.DetectLanguage("/boot/config-6.1.0"), L.Kconfig, "the config of the running kernel");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/config"), L.Config, "a file called config elsewhere stays a config");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/foo.config"), L.Config, "a .config extension elsewhere stays a config");

        Highlighted kconfig = new Highlighted(L.Kconfig,
            "config NET\n\tbool \"Networking\"\n\tdefault y\n\tdepends on NET\nmenu \"Drivers\"\nendmenu\nhelp\n\tSay Y here.\n# CONFIG_X is not set\nsource $(ARCH)/Kconfig\nrsource ../Kconfig.local\nosource $(KCONFIG_DIR)/Kconfig\norsource arch/Kconfig");
        Equal(Join(kconfig.Tokens(0, SyntaxHighlighting.TypeKeyword)), "config", "Kconfig: config");
        Equal(Join(kconfig.Tokens(1, SyntaxHighlighting.TypeKeyword)), "bool", "Kconfig: a type");
        Equal(Join(kconfig.Tokens(2, SyntaxHighlighting.TypeKeyword)), "default|y", "Kconfig: default and its value");
        Equal(Join(kconfig.Tokens(3, SyntaxHighlighting.TypeKeyword)), "depends|on", "Kconfig: depends on");
        Equal(Join(kconfig.Tokens(4, SyntaxHighlighting.TypeKeyword)), "menu", "Kconfig: menu");
        Equal(Join(kconfig.Tokens(5, SyntaxHighlighting.TypeKeyword)), "endmenu", "Kconfig: a directive that stands alone");
        Equal(Join(kconfig.Tokens(6, SyntaxHighlighting.TypeKeyword)), "help", "Kconfig: help");
        Equal(Join(kconfig.Tokens(7, SyntaxHighlighting.TypeVariable)), "", "Kconfig: the help text is prose");
        Equal(Join(kconfig.Tokens(8, SyntaxHighlighting.TypeComment)), "# CONFIG_X is not set", "Kconfig: comment");
        Equal(Join(kconfig.Tokens(9, SyntaxHighlighting.TypeKeyword)), "source", "Kconfig: source");
        Equal(Join(kconfig.Tokens(9, SyntaxHighlighting.TypeVariable)), "$(ARCH)", "Kconfig: a $(NAME) reference");
        Equal(Join(kconfig.Tokens(10, SyntaxHighlighting.TypeKeyword)), "rsource", "Kconfig: rsource");
        Equal(Join(kconfig.Tokens(11, SyntaxHighlighting.TypeKeyword)), "osource", "Kconfig: osource");
        Equal(Join(kconfig.Tokens(11, SyntaxHighlighting.TypeVariable)), "$(KCONFIG_DIR)", "Kconfig: a $(NAME) reference in osource");
        Equal(Join(kconfig.Tokens(12, SyntaxHighlighting.TypeKeyword)), "orsource", "Kconfig: orsource");
        Check(SyntaxHighlighting.GetKeywords(L.Kconfig).Contains("menuconfig"), "Kconfig keywords contain 'menuconfig'");
        Check(SyntaxHighlighting.GetKeywords(L.Kconfig).Contains("depends"), "Kconfig keywords contain 'depends'");
        Check(SyntaxHighlighting.GetKeywords(L.Kconfig).Contains("rsource"), "Kconfig keywords contain 'rsource'");
        Check(SyntaxHighlighting.GetKeywords(L.Kconfig).Contains("osource"), "Kconfig keywords contain 'osource'");
        Equal(Join(CommentStyle.ForLanguage(L.Kconfig).Toggle(new[] { "config NET" })), "# config NET", "Kconfig comment");
    }

    private static void UBootChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/boot/uEnv.txt"), L.UBoot, "a U-Boot environment text file");
        Equal(SyntaxHighlighting.DetectLanguage("/boot/uboot.env"), L.UBoot, "a saved U-Boot environment");
        Equal(SyntaxHighlighting.DetectLanguage("/boot/boot.cmd"), L.UBoot, "a U-Boot boot script");
        Equal(SyntaxHighlighting.DetectLanguage("/boot/boot.scr"), L.UBoot, "a compiled U-Boot boot script");
        Equal(SyntaxHighlighting.DetectLanguage("/firmware/u-boot-env.txt"), L.UBoot, "a named U-Boot environment");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/.env"), L.Config, ".env stays a config");

        Highlighted uboot = new Highlighted(L.UBoot,
            "setenv bootargs console=${console} root=/dev/mmcblk0p2\nif test ${bootcount} -gt 3; then\n\trun bootcmd\nfi\n# comment\nmy_unknown_command arg");
        Equal(Join(uboot.Tokens(0, SyntaxHighlighting.TypeKeyword)), "setenv|bootargs", "U-Boot: commands and common variables");
        Equal(Join(uboot.Tokens(0, SyntaxHighlighting.TypeVariable)), "${console}", "U-Boot: a braced variable");
        Equal(Join(uboot.Tokens(1, SyntaxHighlighting.TypeKeyword)), "if|test|then", "U-Boot: the shell words");
        Equal(Join(uboot.Tokens(1, SyntaxHighlighting.TypeVariable)), "${bootcount}", "U-Boot: a variable in a test");
        Equal(Join(uboot.Tokens(1, SyntaxHighlighting.TypeNumber)), "3", "U-Boot: a number");
        Equal(Join(uboot.Tokens(2, SyntaxHighlighting.TypeKeyword)), "run|bootcmd", "U-Boot: run and a boot variable");
        Equal(Join(uboot.Tokens(4, SyntaxHighlighting.TypeComment)), "# comment", "U-Boot: a comment");
        Equal(Join(uboot.Tokens(5, SyntaxHighlighting.TypeKeyword)), "", "U-Boot: an unknown command is not a keyword");
        Check(SyntaxHighlighting.GetKeywords(L.UBoot).Contains("bootm"), "U-Boot keywords contain 'bootm'");
        Check(SyntaxHighlighting.GetKeywords(L.UBoot).Contains("setenv"), "U-Boot keywords contain 'setenv'");

        Highlighted ubootCommands = new Highlighted(L.UBoot,
            "bootefi 0x80000\nbootmenu 3\nbootflow scan\nwget 192.168.1.10/uImage\nnvme scan\npci enum\nping 192.168.1.1\nsntp 192.168.1.1\ncat 0x80000\nbdinfo");
        Equal(Join(ubootCommands.Tokens(0, SyntaxHighlighting.TypeKeyword)), "bootefi", "U-Boot: bootefi");
        Equal(Join(ubootCommands.Tokens(1, SyntaxHighlighting.TypeKeyword)), "bootmenu", "U-Boot: bootmenu");
        Equal(Join(ubootCommands.Tokens(2, SyntaxHighlighting.TypeKeyword)), "bootflow", "U-Boot: bootflow");
        Equal(Join(ubootCommands.Tokens(3, SyntaxHighlighting.TypeKeyword)), "wget", "U-Boot: wget");
        Equal(Join(ubootCommands.Tokens(4, SyntaxHighlighting.TypeKeyword)), "nvme", "U-Boot: nvme");
        Equal(Join(ubootCommands.Tokens(5, SyntaxHighlighting.TypeKeyword)), "pci", "U-Boot: pci");
        Equal(Join(ubootCommands.Tokens(6, SyntaxHighlighting.TypeKeyword)), "ping", "U-Boot: ping");
        Equal(Join(ubootCommands.Tokens(7, SyntaxHighlighting.TypeKeyword)), "sntp", "U-Boot: sntp");
        Equal(Join(ubootCommands.Tokens(8, SyntaxHighlighting.TypeKeyword)), "cat", "U-Boot: cat");
        Equal(Join(ubootCommands.Tokens(9, SyntaxHighlighting.TypeKeyword)), "bdinfo", "U-Boot: bdinfo");
        Check(SyntaxHighlighting.GetKeywords(L.UBoot).Contains("bootefi"), "U-Boot keywords contain 'bootefi'");
        Check(SyntaxHighlighting.GetKeywords(L.UBoot).Contains("bootflow"), "U-Boot keywords contain 'bootflow'");
        Check(SyntaxHighlighting.GetKeywords(L.UBoot).Contains("bootmenu"), "U-Boot keywords contain 'bootmenu'");
        Check(SyntaxHighlighting.GetKeywords(L.UBoot).Contains("wget"), "U-Boot keywords contain 'wget'");
        Check(SyntaxHighlighting.GetKeywords(L.UBoot).Contains("nvme"), "U-Boot keywords contain 'nvme'");
        Check(SyntaxHighlighting.GetKeywords(L.UBoot).Contains("pci"), "U-Boot keywords contain 'pci'");
        Check(SyntaxHighlighting.GetKeywords(L.UBoot).Contains("ping"), "U-Boot keywords contain 'ping'");
        Check(SyntaxHighlighting.GetKeywords(L.UBoot).Contains("sntp"), "U-Boot keywords contain 'sntp'");
        Check(SyntaxHighlighting.GetKeywords(L.UBoot).Contains("cat"), "U-Boot keywords contain 'cat'");
        Check(SyntaxHighlighting.GetKeywords(L.UBoot).Contains("bdinfo"), "U-Boot keywords contain 'bdinfo'");
        Equal(Join(CommentStyle.ForLanguage(L.UBoot).Toggle(new[] { "setenv x 1" })), "# setenv x 1", "U-Boot comment");
    }

    private static void YoctoChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/src/poky/meta/recipes-core/busybox/busybox_1.36.bb"), L.Yocto, "a BitBake recipe");
        Equal(SyntaxHighlighting.DetectLanguage("/src/meta-foo/recipes-app/app/app_1.0.bbappend"), L.Yocto, "a .bbappend");
        Equal(SyntaxHighlighting.DetectLanguage("/src/poky/meta/classes/base.bbclass"), L.Yocto, "a .bbclass");
        Equal(SyntaxHighlighting.DetectLanguage("/src/meta-foo/recipes-app/app/files/defconfig.inc"), L.Yocto, "an .inc of a layer");
        Equal(SyntaxHighlighting.DetectLanguage("/src/meta-foo/conf/layer.conf"), L.Yocto, "a configuration of a layer");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/php.inc"), L.Plain, ".inc outside a layer stays plain");

        Highlighted recipe = new Highlighted(L.Yocto,
            "SUMMARY = \"A test\"\nDEPENDS ?= \"zlib\"\nSRC_URI += \"file://defconfig\"\ninherit autotools systemd\ndo_install() {\n    install -d ${D}${bindir}\n}\nSRCREV = \"${AUTOREV}\"\n# comment");
        Equal(Join(recipe.Tokens(0, SyntaxHighlighting.TypeKeyword)), "SUMMARY", "Yocto: SUMMARY");
        Equal(Join(recipe.Tokens(1, SyntaxHighlighting.TypeKeyword)), "DEPENDS", "Yocto: a conditional assignment");
        Equal(Join(recipe.Tokens(2, SyntaxHighlighting.TypeKeyword)), "SRC_URI", "Yocto: an appended variable");
        Equal(Join(recipe.Tokens(3, SyntaxHighlighting.TypeKeyword)), "inherit", "Yocto: inherit");
        Equal(Join(recipe.Tokens(4, SyntaxHighlighting.TypeKeyword)), "do_install", "Yocto: a task definition");
        Equal(Join(recipe.Tokens(5, SyntaxHighlighting.TypeVariable)), "install|${D}|${bindir}", "Yocto: the task body and its variables");
        Equal(Join(recipe.Tokens(7, SyntaxHighlighting.TypeKeyword)), "SRCREV", "Yocto: SRCREV");
        Equal(Join(recipe.Tokens(7, SyntaxHighlighting.TypeString)), "\"${AUTOREV}\"", "Yocto: a quoted value");
        Equal(Join(recipe.Tokens(8, SyntaxHighlighting.TypeComment)), "# comment", "Yocto: a comment");
        Check(SyntaxHighlighting.GetKeywords(L.Yocto).Contains("SRC_URI"), "Yocto keywords contain 'SRC_URI'");
        Check(SyntaxHighlighting.GetKeywords(L.Yocto).Contains("do_install"), "Yocto keywords contain 'do_install'");

        Highlighted modern = new Highlighted(L.Yocto,
            "addpylib ${LAYERDIR}/lib\naddfragments conf/fragments OE_FRAGMENTS OE_FRAGMENTS_METADATA_VARS OE_FRAGMENTS_BUILTIN\nBB_NUMBER_THREADS = \"8\"\nBB_CONF_FRAGMENT_SUMMARY = \"1\"\nOE_FRAGMENTS = \"core/base\"");
        Equal(Join(modern.Tokens(0, SyntaxHighlighting.TypeKeyword)), "addpylib", "Yocto: addpylib");
        Equal(Join(modern.Tokens(0, SyntaxHighlighting.TypeVariable)), "${LAYERDIR}", "Yocto: a variable in addpylib");
        Equal(Join(modern.Tokens(1, SyntaxHighlighting.TypeKeyword)), "addfragments", "Yocto: addfragments");
        Equal(Join(modern.Tokens(2, SyntaxHighlighting.TypeKeyword)), "BB_NUMBER_THREADS", "Yocto: BB_NUMBER_THREADS");
        Equal(Join(modern.Tokens(3, SyntaxHighlighting.TypeKeyword)), "BB_CONF_FRAGMENT_SUMMARY", "Yocto: BB_CONF_FRAGMENT_SUMMARY");
        Equal(Join(modern.Tokens(4, SyntaxHighlighting.TypeKeyword)), "OE_FRAGMENTS", "Yocto: OE_FRAGMENTS");
        Check(SyntaxHighlighting.GetKeywords(L.Yocto).Contains("addpylib"), "Yocto keywords contain 'addpylib'");
        Check(SyntaxHighlighting.GetKeywords(L.Yocto).Contains("addfragments"), "Yocto keywords contain 'addfragments'");
        Check(SyntaxHighlighting.GetKeywords(L.Yocto).Contains("BB_NUMBER_THREADS"), "Yocto keywords contain 'BB_NUMBER_THREADS'");
        Check(SyntaxHighlighting.GetKeywords(L.Yocto).Contains("BB_CONF_FRAGMENT_SUMMARY"), "Yocto keywords contain 'BB_CONF_FRAGMENT_SUMMARY'");
        Check(SyntaxHighlighting.GetKeywords(L.Yocto).Contains("OE_FRAGMENTS"), "Yocto keywords contain 'OE_FRAGMENTS'");
        Equal(Join(CommentStyle.ForLanguage(L.Yocto).Toggle(new[] { "SUMMARY = \"x\"" })), "# SUMMARY = \"x\"", "Yocto comment");
    }

    private static void LinkerScriptChecks()
    {
        Equal(SyntaxHighlighting.DetectLanguage("/src/boot/linker.ld"), L.LinkerScript, "a linker script");
        Equal(SyntaxHighlighting.DetectLanguage("/src/u-boot/u-boot.lds"), L.LinkerScript, "a .lds script");
        Equal(SyntaxHighlighting.DetectLanguage("/src/linux/arch/riscv/kernel/vmlinux.lds.S"), L.LinkerScript, "a preprocessed .lds.S script");
        Equal(SyntaxHighlighting.DetectLanguage("/src/board/board.ld.in"), L.LinkerScript, "a .ld.in template");
        Equal(SyntaxHighlighting.DetectLanguage("/src/main.s"), L.Assembly, ".s stays assembly");

        Highlighted linker = new Highlighted(L.LinkerScript,
            "OUTPUT_FORMAT(\"elf64-littleriscv\")\nENTRY(_start)\nSECTIONS\n{\n  . = 0x80000000;\n  .text : { *(.text*) }\n  /* comment */\n  .data : AT(ADDR(.text) + SIZEOF(.text)) { *(.data*) }\n}");
        Equal(Join(linker.Tokens(0, SyntaxHighlighting.TypeKeyword)), "OUTPUT_FORMAT", "linker: OUTPUT_FORMAT");
        Equal(Join(linker.Tokens(0, SyntaxHighlighting.TypeString)), "\"elf64-littleriscv\"", "linker: a quoted format");
        Equal(Join(linker.Tokens(1, SyntaxHighlighting.TypeKeyword)), "ENTRY", "linker: ENTRY");
        Equal(Join(linker.Tokens(2, SyntaxHighlighting.TypeKeyword)), "SECTIONS", "linker: SECTIONS");
        Equal(Join(linker.Tokens(4, SyntaxHighlighting.TypeNumber)), "0x80000000", "linker: an address");
        Equal(Join(linker.Tokens(6, SyntaxHighlighting.TypeComment)), "/* comment */", "linker: a block comment");
        Equal(Join(linker.Tokens(7, SyntaxHighlighting.TypeKeyword)), "AT|ADDR|SIZEOF", "linker: commands in an expression");
        Check(SyntaxHighlighting.GetKeywords(L.LinkerScript).Contains("SECTIONS"), "linker keywords contain 'SECTIONS'");
        Check(SyntaxHighlighting.GetKeywords(L.LinkerScript).Contains("SIZEOF"), "linker keywords contain 'SIZEOF'");
        Check(SyntaxHighlighting.GetKeywords(L.LinkerScript).Contains("VERSION"), "linker keywords contain 'VERSION'");
        Check(SyntaxHighlighting.GetKeywords(L.LinkerScript).Contains("NOCROSSREFS"), "linker keywords contain 'NOCROSSREFS'");
        Check(SyntaxHighlighting.GetKeywords(L.LinkerScript).Contains("NOCROSSREFS_TO"), "linker keywords contain 'NOCROSSREFS_TO'");

        Highlighted modern = new Highlighted(L.LinkerScript,
            "VERSION { global: foo; local: *; }\nNOCROSSREFS(.text .data)\nNOCROSSREFS_TO(.text .rodata)");
        Equal(Join(modern.Tokens(0, SyntaxHighlighting.TypeKeyword)), "VERSION", "linker: VERSION");
        Equal(Join(modern.Tokens(1, SyntaxHighlighting.TypeKeyword)), "NOCROSSREFS", "linker: NOCROSSREFS");
        Equal(Join(modern.Tokens(2, SyntaxHighlighting.TypeKeyword)), "NOCROSSREFS_TO", "linker: NOCROSSREFS_TO");
        Equal(Join(CommentStyle.ForLanguage(L.LinkerScript).Toggle(new[] { "ENTRY(_start)" })), "/* ENTRY(_start) */", "linker comment");
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

        // nginx: the blocks fold, the braces of comments, strings and regular expressions do not count.
        Highlighted nginx = new Highlighted(L.Nginx,
            "http {\n    upstream app {\n        server 10.0.0.1:8080;\n    }\n    server {\n        listen 80;\n        location ~ ^/a{2}/ {\n            return 200 \"ok {\";\n        }\n        # comment {\n        location / { return 404; }\n    }\n}");
        Equal(Folds(nginx), "0:12,1:3,4:11,6:8", "nginx http, upstream, server and location blocks fold");
        Equal(nginx.At(0, 5).Partner?.LineNumber, 12, "the { of http is paired with the last }");
        Highlighted nginxAllman = new Highlighted(L.Nginx, "server\n{\n    listen 80;\n}\n");
        Equal(Folds(nginxAllman), "1:3", "a { alone on its line folds");
        Highlighted nginxLua = new Highlighted(L.Nginx, "location /x {\n    content_by_lua_block {\n        local t = {}\n        ngx.say(\"}\")\n    }\n}");
        Equal(Folds(nginxLua), "0:5,1:4", "braces of a Lua block inside nginx pair up, and {} on one line does not fold");
        Equal(Join(nginx.Tokens(0, SyntaxHighlighting.TypeKeyword)), "http", "the directive before the { is still a keyword");
        Equal(nginx.TypeAt(9, 10), SyntaxHighlighting.TypeComment, "a { in a comment is a comment");
        Equal(Folds(new Highlighted(L.Systemd, "[Service]\nExecStart=/bin/echo {\n}")), "", "systemd units do not fold on braces");
        Equal(Folds(new Highlighted(L.SshConfig, "Host x {\n}")), "", "ssh configuration does not fold on braces");

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

        // GNU make functions inside "$(...)" and "${...}", including nested calls.
        Highlighted makeFunctions = new Highlighted(L.Makefile,
            "SRC := $(wildcard src/*.c)\n" +
            "OBJ := $(patsubst %.c,%.o,$(SRC))\n" +
            "$(info building $(OBJ))\n" +
            "all: $(OBJ)\n" +
            "\t$(foreach f,$(OBJ),$(call cc,$(f)))\n" +
            "Q = $(shell)\n");
        Equal(Join(makeFunctions.Tokens(0, SyntaxHighlighting.TypeKeyword)), "wildcard", "Makefile: wildcard is a function");
        Equal(Join(makeFunctions.Tokens(1, SyntaxHighlighting.TypeKeyword)), "patsubst", "Makefile: patsubst is a function");
        Equal(Join(makeFunctions.Tokens(2, SyntaxHighlighting.TypeKeyword)), "info", "Makefile: info is a function");
        Equal(Join(makeFunctions.Tokens(4, SyntaxHighlighting.TypeKeyword)), "foreach|call", "Makefile: nested function calls are recognized");
        Equal(Join(makeFunctions.Tokens(1, SyntaxHighlighting.TypeVariable)), "OBJ|$(|$(SRC)|)", "Makefile: the brackets and the nested reference stay variables");
        Equal(Join(makeFunctions.Tokens(5, SyntaxHighlighting.TypeKeyword)), "", "Makefile: a variable named like a function is not a call");
        Equal(Join(makeFunctions.Tokens(5, SyntaxHighlighting.TypeVariable)), "Q|$(shell)", "Makefile: a reference without arguments is a variable");

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
        Equal(SyntaxHighlighting.DetectLanguage("/srv/docker-compose.yml"), L.DockerCompose, "docker-compose.yml is Compose");

        // The text of a block scalar is not YAML.
        Highlighted scalar = new Highlighted(L.Yaml, "run: |\n  echo \"don't # stop\"\n  x: y\n\n  more\nnext: 'v'\n");
        Equal(Join(scalar.Tokens(1, SyntaxHighlighting.TypeString)), "echo \"don't # stop\"", "a line of a block scalar is text");
        Equal(Join(scalar.Tokens(1, SyntaxHighlighting.TypeComment)), "", "'#' in a block scalar is not a comment");
        Equal(Join(scalar.Tokens(2, SyntaxHighlighting.TypeVariable)), "", "'x: y' in a block scalar is not a key");
        Equal(Join(scalar.Tokens(4, SyntaxHighlighting.TypeString)), "more", "a blank line does not end the block scalar");
        Equal(Join(scalar.Tokens(5, SyntaxHighlighting.TypeVariable)), "next", "a line that is not indented deeper ends it");
        Equal(Join(scalar.Tokens(5, SyntaxHighlighting.TypeString)), "'v'", "and is YAML again");

        // YAML anchors: definitions, aliases, the merge key and foldable anchor headers.
        Highlighted yamlAnchors = new Highlighted(L.Yaml,
            "defaults: &defaults\n" +
            "  adapter: postgres\n" +
            "  host: localhost\n" +
            "development:\n" +
            "  <<: *defaults\n" +
            "  database: dev\n" +
            "list:\n" +
            "- &first\n" +
            "  name: one\n" +
            "- *first\n");
        Equal(Join(yamlAnchors.Tokens(0, SyntaxHighlighting.TypeVariable)), "defaults|&defaults", "YAML: an anchor after a key");
        Equal(Join(yamlAnchors.Tokens(4, SyntaxHighlighting.TypeKeyword)), "<<", "YAML: the merge key");
        Equal(Join(yamlAnchors.Tokens(4, SyntaxHighlighting.TypeVariable)), "*defaults", "YAML: an alias as a value");
        Equal(Join(yamlAnchors.Tokens(7, SyntaxHighlighting.TypeVariable)), "&first", "YAML: an anchor of a list item");
        Equal(Join(yamlAnchors.Tokens(9, SyntaxHighlighting.TypeVariable)), "*first", "YAML: an alias of a list item");
        Equal(Folds(yamlAnchors), "0:3,3:6,6:10,7:9", "YAML: anchor headers fold the nodes they name");

        Highlighted yamlAnchorValue = new Highlighted(L.Yaml, "value: &name text\nother: [one, *name]\nnext: 1\ncommand: echo *branch\n");
        Equal(Join(yamlAnchorValue.Tokens(0, SyntaxHighlighting.TypeVariable)), "value|&name", "YAML: an anchor before an inline value");
        Equal(Join(yamlAnchorValue.Tokens(1, SyntaxHighlighting.TypeVariable)), "other|*name", "YAML: an alias in a flow sequence");
        Equal(Join(yamlAnchorValue.Tokens(3, SyntaxHighlighting.TypeVariable)), "command", "YAML: a '*' inside a plain scalar is not an alias");
        Equal(Folds(yamlAnchorValue), "", "YAML: an anchor with an inline value does not open a block");

        Highlighted yamlAnchorList = new Highlighted(L.Yaml, "defaults: &defaults\n- a\n- b\nnext: 1\n");
        Equal(Folds(yamlAnchorList), "0:3", "YAML: a list at the indentation of an anchor key belongs to it");
        Highlighted yamlRootAnchor = new Highlighted(L.Yaml, "&root\n  a: 1\nb: 2\n");
        Equal(Folds(yamlRootAnchor), "0:2", "YAML: an anchor alone on its line opens a block");
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

    // The problems a check finds before a save are shown as an inline message under each offending line. The editor
    // and the message provider are Windows-only, so the wiring is checked by reading the sources, as the checks above.
    private static void InlineMessageChecks()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            Console.WriteLine("SKIP: repository root not found, the inline message wiring is not checked");
            return;
        }
        string provider = File.ReadAllText(Path.Combine(root, "CodeEditing", "InlineMessageProvider.cs"));
        Check(provider.Contains("EnsureDrawingObjects") && provider.Contains("_fontHeight != rowHeight"),
            "the message provider builds its font and icons once, not on every paint");
        Check(provider.Contains("public Color BackColor") && provider.Contains("public Color ErrorColor") && provider.Contains("public Color WarningColor"),
            "the message colours are settings of the provider instead of the old fixed ones");
        Check(!provider.Contains("Brushes.LightGray") && !provider.Contains("Brushes.Red") && !provider.Contains("Brushes.DarkGoldenrod"),
            "the provider no longer paints with the fixed light-theme brushes");
        string editor = File.ReadAllText(Path.Combine(root, "Controls", "CodeEditorPP.cs"));
        Check(editor.Contains("public void ClearMessages()") && editor.Contains("_inlineMessageProvider.Clear()"),
            "ClearMessages drops the messages and lays the editor out again");
        Check(editor.Contains("public void AddMessages(IEnumerable<InlineMessageProvider.MessageRequest> messages)"),
            "a whole list of problems can be added in one rebuild");
        Check(editor.Contains("protected override void OnWordWrapChanged()"),
            "switching the word wrapping on or off drops the messages");
        string remote = File.ReadAllText(Path.Combine(root, "Controls", "RemoteFileEditor.cs"));
        Check(remote.Contains("bool showInText = !WordWrap && !IsHexMode;"),
            "messages are shown in the text view only, not in the wrapped or the hex view");
        Check(remote.Contains("AddMessages(messages)") && remote.Contains("const int MaxInline = 50"),
            "the problems of a failed check become inline messages, capped like ParseIssues");
        Check(remote.Contains("they are shown in the text") && !remote.Contains("const int MaxShown"),
            "the dialog is short and no longer lists the problems it points at");
        Check(remote.Contains("RefreshMessageColors()"),
            "the messages take the colours of the current theme");
        int clears = 0;
        foreach (string snippet in new[] { "// The problems the previous check named", "// The messages name lines of the text", "// The messages are rows of the text view" })
        {
            if (remote.Contains(snippet))
            {
                clears++;
            }
        }
        Check(clears == 3, "the messages are cleared before a save, on a reload and on a hex view switch");
        string settings = File.ReadAllText(Path.Combine(root, "VisualTTY", "GlobalSettings.cs"));
        Check(settings.Contains("public DeviceTerminalSettings.ColorWrapper ErrorColor { get; set; }")
            && settings.Contains("public DeviceTerminalSettings.ColorWrapper WarningColor { get; set; }"),
            "the editor colours carry an error and a warning colour for the messages");
        Check(settings.Contains("colors.ErrorColor = defaults.ErrorColor") && settings.Contains("colors.WarningColor = defaults.WarningColor"),
            "older settings files get the default message colours");
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
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.Awk).Toggle(new[] { "print $1" })), "# print $1", "AWK comment");
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.Tcl).Toggle(new[] { "set x 1" })), "# set x 1", "Tcl comment");
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.Assembly).Toggle(new[] { "mov eax, 1" })), "; mov eax, 1", "Assembly comment");
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.PowerShell).Toggle(new[] { "Get-ChildItem" })), "# Get-ChildItem", "PowerShell comment");
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.Ruby).Toggle(new[] { "puts 1" })), "# puts 1", "Ruby comment");
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.Perl).Toggle(new[] { "print 1" })), "# print 1", "Perl comment");
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.CMake).Toggle(new[] { "set(X 1)" })), "# set(X 1)", "CMake comment");
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.Dockerfile).Toggle(new[] { "FROM alpine" })), "# FROM alpine", "Dockerfile comment");
        foreach (L language in new[]
                 {
                     L.CSharp, L.Java, L.Kotlin, L.Scala, L.Groovy, L.Go, L.Rust, L.Verilog, L.Swift, L.Dart,
                     L.JavaScript, L.TypeScript, L.Php, L.Protobuf, L.DeviceTree
                 })
        {
            Equal(Join(CommentStyle.ForLanguage(language).Toggle(new[] { "x" })), "// x",
                SyntaxHighlighting.GetDisplayName(language) + " uses '//' comments");
        }
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.Css).Toggle(new[] { "a" })), "/* a */", "CSS uses block comments");
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.Sql).Toggle(new[] { "select 1" })), "-- select 1", "SQL comment");
        Equal(Join(CommentStyle.ForLanguage(SyntaxHighlighting.Language.Vhdl).Toggle(new[] { "signal x : bit" })),
            "-- signal x : bit", "VHDL comment");
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
            AdminFormatDetectionChecks();
        AdminFormatTokenChecks();
        CrontabChecks();
        FstabChecks();
        SudoersChecks();
        MySqlChecks();
        RedisChecks();
        HAProxyChecks();
        ApacheChecks();
        SystemdNetworkdChecks();
        IfupdownChecks();
        DnsmasqChecks();
        HostapdChecks();
        WireguardChecks();
        UdevChecks();
        NftablesChecks();
        ModprobeChecks();
        LogrotateChecks();
        SysctlChecks();
        PamChecks();
        SambaChecks();
        PostfixChecks();
        OpenVpnChecks();
        NetplanChecks();
        CloudInitChecks();
        PrometheusChecks();
        AlertmanagerChecks();
        MongoDbChecks();
        ElasticsearchChecks();
        MesonChecks();
        HclChecks();
        MarkdownChecks();
        StarlarkChecks();
        NixChecks();
        FishChecks();
        BatchChecks();
        KubernetesChecks();
        DockerComposeChecks();
        AnsibleChecks();
        PostgreSqlChecks();
        TomlChecks();
        KconfigChecks();
        UBootChecks();
        YoctoChecks();
        LinkerScriptChecks();
        FoldChecks();
            PythonChecks();
            CppChecks();
            GenericChecks();
            HdlChecks();
            TokenOrderChecks();
            PairEditingChecks();
            CommentChecks();
            WrapChecks();
            ShowTabsChecks();
            ShowAllCharactersChecks();
            InlineMessageChecks();
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
