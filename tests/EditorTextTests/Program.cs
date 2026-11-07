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
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/default.nix"), L.Script, "Nix stays in the generic script profile");
        Equal(SyntaxHighlighting.GetDisplayName(L.Awk), "AWK", "AWK has its own display name");
        Check(SyntaxHighlighting.SelectableLanguages.Contains(L.Awk), "AWK is in the syntax menu");
        Check(SyntaxHighlighting.GetKeywords(L.Awk).Contains("BEGIN"), "AWK keywords contain 'BEGIN'");
        Check(SyntaxHighlighting.GetKeywords(L.Awk).Contains("print"), "AWK keywords contain 'print'");
        Check(SyntaxHighlighting.GetKeywords(L.Awk).Contains("getline"), "AWK keywords contain 'getline'");
        Check(!SyntaxHighlighting.GetKeywords(L.Awk).Contains("sizeof"), "AWK keywords do not include the C 'sizeof'");
        Check(!SyntaxHighlighting.GetKeywords(L.Script).Contains("BEGIN"), "the generic Script profile no longer carries the AWK 'BEGIN'");
        Check(!SyntaxHighlighting.GetKeywords(L.Script).Contains("getline"), "the generic Script profile no longer carries the AWK 'getline'");
        Equal(SyntaxHighlighting.GetDisplayName(L.Script), "Script (R, Nix)", "the generic Script profile now covers only R and Nix");
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
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/README.md"), L.Plain, "markdown is not highlighted");
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
            L.JavaScript, L.TypeScript, L.Protobuf, L.Tcl, L.Assembly, L.PowerShell
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
        Equal(SyntaxHighlighting.DetectLanguage("/etc/systemd/network/10-eth.network"), L.Config, "networkd files are not units");
        Equal(SyntaxHighlighting.DetectLanguage("/etc/crontab"), L.Crontab, "/etc/crontab is a crontab");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/.env"), L.Config, ".env stays a config");
        Equal(SyntaxHighlighting.DetectLanguage("/srv/app/pyproject.toml"), L.Config, "toml stays a config");

        Equal(SyntaxHighlighting.DetectLanguage("/var/log/syslog"), L.Log, "syslog");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/messages"), L.Log, "messages");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/auth.log"), L.Log, "auth.log");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/nginx/access.log"), L.Log, "a log in a folder of /var/log");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/syslog.1"), L.Log, "a rotated log");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/app.log.2"), L.Log, "a rotated .log");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/app.log"), L.Log, ".log anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/app.log.3"), L.Log, "a rotated .log anywhere");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/wtmp"), L.Plain, "wtmp is binary");
        Equal(SyntaxHighlighting.DetectLanguage("/var/log/README.md"), L.Plain, "a markdown file in /var/log is prose");
        Equal(SyntaxHighlighting.DetectLanguage("/home/u/notes.txt"), L.Plain, "prose stays plain");
        Equal(SyntaxHighlighting.DetectLanguage("/tmp/x.1"), L.Plain, "a file called x.1 elsewhere says nothing");

        // The status bar and the syntax menu offer them, with names of their own.
        foreach (L language in new[] { L.Systemd, L.Nginx, L.SshConfig, L.Crontab, L.Fstab, L.Sudoers, L.PostgreSql, L.Log })
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
            "config NET\n\tbool \"Networking\"\n\tdefault y\n\tdepends on NET\nmenu \"Drivers\"\nendmenu\nhelp\n\tSay Y here.\n# CONFIG_X is not set\nsource $(ARCH)/Kconfig");
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
        Check(SyntaxHighlighting.GetKeywords(L.Kconfig).Contains("menuconfig"), "Kconfig keywords contain 'menuconfig'");
        Check(SyntaxHighlighting.GetKeywords(L.Kconfig).Contains("depends"), "Kconfig keywords contain 'depends'");
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
        PostgreSqlChecks();
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
