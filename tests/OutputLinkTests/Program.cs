using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SmarTTY.Controls;
using SmarTTY.Terminal;

// Regression harness for the links in the output of build commands in a Smart Terminal tab: which names become links, where a
// click opens them (path, line, column), and which directory the relative names of make, ninja and cmake builds are
// relative to. Run with: dotnet run --project tests/OutputLinkTests
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

    private sealed class Host : IOutputLinkHost
    {
        public readonly List<string> Opened = new List<string>();

        public bool IsLinkExtension(string extension)
        {
            // The default list of the terminal, shortened to what the checks use.
            return new[] { ".c", ".h", ".cpp", ".hpp", ".cc", ".py", ".sh", ".rs", ".go", ".txt", ".mk", ".cxx", ".s" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        public void RequestOpenFile(string path, int? line, int? column)
        {
            Opened.Add(path + "|" + (line.HasValue ? line.Value.ToString() : "-") + "|" + (column.HasValue ? column.Value.ToString() : "-"));
        }
    }

    // The output of a command as the terminal receives it: pieces of text in order. Returns what each link covers, and clicks
    // on all of them.
    private sealed class Output
    {
        public readonly List<string> Covered = new List<string>();

        public readonly Host Host = new Host();

        public Output(string workingDirectory, string command, params string[] pieces)
        {
            OutputLinkScanner scanner = new OutputLinkScanner(Host, workingDirectory, command);
            StringBuilder text = new StringBuilder();
            List<TerminalEmulatorBase.LinkCell> cells = new List<TerminalEmulatorBase.LinkCell>();
            foreach (string piece in pieces)
            {
                text.Append(piece);
                cells.AddRange(scanner.Scan(piece));
            }
            scanner.Finish();
            List<TerminalEmulatorBase.ILinkTarget> seen = new List<TerminalEmulatorBase.ILinkTarget>();
            foreach (TerminalEmulatorBase.LinkCell cell in cells)
            {
                if (cell.Target != null && cell.Target.IsActive && !seen.Contains(cell.Target))
                {
                    seen.Add(cell.Target);
                }
            }
            foreach (TerminalEmulatorBase.ILinkTarget target in seen)
            {
                StringBuilder covered = new StringBuilder();
                for (int i = 0; i < cells.Count; i++)
                {
                    if (ReferenceEquals(cells[i].Target, target))
                    {
                        covered.Append(text[i]);
                    }
                }
                Covered.Add(covered.ToString());
                target.Activate();
            }
        }

        public string Click(int index) => Host.Opened[index];
    }

    private static void CompilerMessageChecks()
    {
        Output gcc = new Output("/home/u/proj", "gcc -Wall main.c", "main.c:12:5: error: expected ';' before 'return'\n");
        Equal(gcc.Covered.Count, 1, "gcc: one link in the message");
        Equal(gcc.Click(0), "/home/u/proj/main.c|11|4", "gcc: the link opens main.c at line 12, column 5 (counted from 0)");

        Output sub = new Output("/home/u/proj", "make", "src/util.h:7:1: warning: unused variable\n");
        Equal(sub.Click(0), "/home/u/proj/src/util.h|6|0", "a relative path in a folder, a warning");

        Output absolute = new Output("/home/u/proj", "gcc x.c", "/usr/include/stdio.h:33:10: fatal error: stddef.h: No such file or directory\n");
        Equal(absolute.Click(0), "/usr/include/stdio.h|32|9", "an absolute path is taken as it is");

        Output lineOnly = new Output("/w", "cc", "foo.c:20: undefined reference to `bar'\n");
        Equal(lineOnly.Click(0), "/w/foo.c|19|-", "a line without a column");

        Output included = new Output("/w", "make", "In file included from main.c:3,\n", "                 from app.c:9:\n");
        Equal(included.Click(0), "/w/main.c|2|-", "'In file included from main.c:3,'");
        Equal(included.Click(1), "/w/app.c|8|-", "the 'from app.c:9:' continuation line");

        Output clang = new Output("/w", "clang a.cpp", "a.cpp:4:12: error: use of undeclared identifier 'x'\n");
        Equal(clang.Click(0), "/w/a.cpp|3|11", "clang prints the same form");

        Output rust = new Output("/w", "cargo build", "  --> src/main.rs:5:9\n");
        Equal(rust.Click(0), "/w/src/main.rs|4|8", "rustc: '--> src/main.rs:5:9'");

        Output go = new Output("/w", "go build", "./main.go:12:2: undefined: foo\n");
        Equal(go.Click(0), "/w/main.go|11|1", "go: './main.go:12:2:' (the './' is dropped)");

        Output dotDot = new Output("/w/build", "ninja", "../src/a.c:3:1: error: x\n");
        Equal(dotDot.Click(0), "/w/build/../src/a.c|2|0", "'..' is left to the server (a symbolic link may change what it means)");

        Output text = new Output("/w", "ls", "readme.txt notes.md\n");
        Equal(text.Covered.Count, 1, "ls: a .txt file is a link, an extension that is not in the list is not");
        Equal(text.Click(0), "/w/readme.txt|-|-", "a name without a position opens at the top");

        // The output ends right after the position (the tracker finishes the scan): the link stays.
        Output atEnd = new Output("/w", "grep -n x a.c", "a.c:7");
        Equal(atEnd.Covered.Count, 1, "a link at the very end of the output stays a link");
        Equal(atEnd.Click(0), "/w/a.c|6|-", "...and opens at its line");

        Output plain = new Output("/w", "echo", "nothing to see here: 12\n");
        Equal(plain.Covered.Count, 0, "ordinary words are not links");
    }

    private static void ColoredOutputChecks()
    {
        // gcc colours the parts of the message separately; the terminal gets the text between the escape sequences in pieces.
        Output colored = new Output("/w", "gcc a.c", "\u0001", "a.c:", "10:5:", " ", "error: ", "expected ';'", "\n");
        Equal(colored.Click(0), "/w/a.c|9|4", "colored gcc: the name and the position come in pieces");

        Output split = new Output("/w", "gcc", "src/ma", "in.c:", "3", ":1: error\n");
        Equal(split.Click(0), "/w/src/main.c|2|0", "a name split between two pieces of output");
    }

    private static void NameWithoutExtensionChecks()
    {
        Output make = new Output("/w", "make", "Makefile:12: *** missing separator.  Stop.\n");
        Equal(make.Click(0), "/w/Makefile|11|-", "Makefile:12: opens the Makefile");

        Output recursive = new Output("/w", "make", "make[1]: *** [Makefile:30: all] Error 2\n");
        Equal(recursive.Covered.Count, 1, "the Makefile name inside make's [..] message is a link");
        Equal(recursive.Click(0), "/w/Makefile|29|-", "[Makefile:30: all] opens line 30");

        Output docker = new Output("/w", "ls", "Dockerfile  Kconfig  configure\n");
        Equal(docker.Covered.Count, 2, "ls: Dockerfile and Kconfig are links, 'configure' is not");

        Output other = new Output("/w", "echo", "Notmakefile:12:\n");
        Equal(other.Covered.Count, 0, "a name that only contains 'makefile' is not a link");
    }

    private static void DirectoryChecks()
    {
        // make -C: the names are relative to the directory make announces.
        Output makeC = new Output("/home/u/proj", "make -C sub",
            "make: Entering directory '/home/u/proj/sub'\n", "main.c:5:3: error: x\n", "make: Leaving directory '/home/u/proj/sub'\n");
        Equal(makeC.Covered.Count, 1, "make -C: the directory in the message is not a link, the file is");
        Equal(makeC.Click(0), "/home/u/proj/sub/main.c|4|2", "make -C: the name is relative to the announced directory");

        // Recursive make and the old quoting (`dir').
        Output nested = new Output("/w", "make",
            "make[1]: Entering directory `/w/lib'\n", "a.c:1:1: error\n",
            "make[2]: Entering directory `/w/lib/deep'\n", "b.c:2:2: error\n",
            "make[2]: Leaving directory `/w/lib/deep'\n", "c.c:3:3: error\n",
            "make[1]: Leaving directory `/w/lib'\n", "d.c:4:4: error\n");
        Equal(nested.Click(0), "/w/lib/a.c|0|0", "recursive make: the first level");
        Equal(nested.Click(1), "/w/lib/deep/b.c|1|1", "recursive make: the second level");
        Equal(nested.Click(2), "/w/lib/c.c|2|2", "recursive make: back on the first level");
        Equal(nested.Click(3), "/w/d.c|3|3", "recursive make: back where the command was typed");

        // ninja announces a directory relative to where it was started.
        Output ninja = new Output("/w", "ninja -C build", "ninja: Entering directory `build'\n", "../src/x.c:9:1: error\n");
        Equal(ninja.Click(0), "/w/build/../src/x.c|8|0", "ninja -C build: relative to the build directory");

        Output ninjaAfterCd = new Output("/w", "cd app && ninja -C out", "ninja: Entering directory `out'\n", "m.c:1:1: error\n");
        Equal(ninjaAfterCd.Click(0), "/w/app/out/m.c|0|0", "cd app && ninja -C out: the announced directory is relative to app");

        // The command line alone, without any message.
        Output cmake = new Output("/w", "cmake --build out --target app", "../src/y.c:2:1: error\n");
        Equal(cmake.Click(0), "/w/out/../src/y.c|1|0", "cmake --build out: relative to out");

        Output cd = new Output("/w", "cd firmware && make V=1", "led.c:8:2: error\n");
        Equal(cd.Click(0), "/w/firmware/led.c|7|1", "cd dir && make: relative to dir");

        // A message that is cut by the end of the output does nothing.
        Output partial = new Output("/w", "make", "make: Entering directory '/w/sub", "\n", "z.c:1:1: error\n");
        Check(partial.Host.Opened.Contains("/w/z.c|0|0") && !partial.Host.Opened.Contains("/w/sub/z.c|0|0"), "a message that never closes is ignored");

        // A "Leaving" without an "Entering" does not break anything.
        Output stray = new Output("/w", "make", "make: Leaving directory '/w/sub'\n", "q.c:1:1: error\n");
        Equal(stray.Covered.Count, 1, "a stray Leaving directory: its directory is not a link");
        Equal(stray.Click(0), "/w/q.c|0|0", "a stray Leaving directory is ignored");
    }

    private static void CommandHintChecks()
    {
        Equal(BuildDirectories.FromCommand("make -C sub", "/w"), "/w/sub", "make -C sub");
        Equal(BuildDirectories.FromCommand("make -Csub all", "/w"), "/w/sub", "make -Csub");
        Equal(BuildDirectories.FromCommand("make --directory=/abs/dir", "/w"), "/abs/dir", "make --directory=/abs/dir");
        Equal(BuildDirectories.FromCommand("make --directory ../other", "/w/a"), "/w/a/../other", "make --directory ../other");
        Equal(BuildDirectories.FromCommand("gmake -j8 -C 'my dir' all", "/w"), "/w/my dir", "a quoted directory with a space");
        Equal(BuildDirectories.FromCommand("ninja -C build", "/w"), "/w/build", "ninja -C build");
        Equal(BuildDirectories.FromCommand("cmake --build build", "/w"), "/w/build", "cmake --build build");
        Equal(BuildDirectories.FromCommand("cmake --build . --parallel", "/w"), "/w", "cmake --build . stays where it is");
        Equal(BuildDirectories.FromCommand("cd src && make", "/w"), "/w/src", "cd src && make");
        Equal(BuildDirectories.FromCommand("cd /opt/app; make", "/w"), "/opt/app", "cd /opt/app; make");
        Equal(BuildDirectories.FromCommand("cd a && cd b && make", "/w"), "/w/a/b", "two cd in a row");
        Equal(BuildDirectories.FromCommand("sudo make -C sub install", "/w"), "/w/sub", "sudo in front");
        Equal(BuildDirectories.FromCommand("CC=gcc make -C sub", "/w"), "/w/sub", "a variable assignment in front");
        Equal(BuildDirectories.FromCommand("make -C sub 2>&1 | tee log", "/w"), "/w/sub", "redirection and a pipe after it");

        Equal(BuildDirectories.FromCommand("make", "/w"), (string)null, "make alone says nothing");
        Equal(BuildDirectories.FromCommand("git -C sub status", "/w"), (string)null, "git -C is not a build directory");
        Equal(BuildDirectories.FromCommand("tar -C /tmp -xf a.tar", "/w"), (string)null, "tar -C is not a build directory");
        Equal(BuildDirectories.FromCommand("grep -C 3 foo a.c", "/w"), (string)null, "grep -C is not a build directory");
        Equal(BuildDirectories.FromCommand("make -C $DIR", "/w"), (string)null, "a variable cannot be followed");
        Equal(BuildDirectories.FromCommand("cd ~/proj && make", "/w"), (string)null, "~ cannot be followed");
        Equal(BuildDirectories.FromCommand("cd - && make", "/w"), (string)null, "cd - cannot be followed");
        Equal(BuildDirectories.FromCommand("", "/w"), (string)null, "no command");
        Equal(BuildDirectories.FromCommand(null, "/w"), (string)null, "null command");
        Equal(BuildDirectories.FromCommand("make -C sub", ""), (string)null, "no working directory");

        Equal(BuildDirectories.Resolve("/w", "./a.c"), "/w/a.c", "'./' in front is dropped");
        Equal(BuildDirectories.Resolve("/w", "x/./a.c"), "/w/x/a.c", "'/./' inside is dropped");
        Equal(BuildDirectories.Resolve("/w", "../a.c"), "/w/../a.c", "'..' is kept");
        Equal(BuildDirectories.Resolve("/w", "/a/b.c"), "/a/b.c", "an absolute path stays");
        Equal(BuildDirectories.Resolve("/w", "."), "/w", ". is the directory");
    }

    private static void MessageChecks()
    {
        bool entering;
        string directory;
        Check(BuildDirectories.TryParseMessage("make[2]: Entering directory '/a b/c'", out entering, out directory) && entering && directory == "/a b/c", "Entering directory with a space");
        Check(BuildDirectories.TryParseMessage("make: Leaving directory `/x'", out entering, out directory) && !entering && directory == "/x", "Leaving directory in the old quoting");
        Check(BuildDirectories.TryParseMessage("ninja: Entering directory `build'", out entering, out directory) && entering && directory == "build", "ninja: a relative directory");
        Check(!BuildDirectories.TryParseMessage("make: Nothing to be done for 'all'.", out entering, out directory), "another make message");
        Check(!BuildDirectories.TryParseMessage("", out entering, out directory), "empty text");
        Check(!BuildDirectories.TryParseMessage(null, out entering, out directory), "null text");
    }

    private static void Main()
    {
        CompilerMessageChecks();
        ColoredOutputChecks();
        NameWithoutExtensionChecks();
        DirectoryChecks();
        CommandHintChecks();
        MessageChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " link checks passed.");
    }
}
