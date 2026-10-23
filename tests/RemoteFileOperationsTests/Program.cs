using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SmarTTY.IO;

// Regression harness for the file list operations (IO/RemoteFileOperations.cs): name rules, copy names and the
// shell commands, which are run by /bin/sh on a temporary directory (busybox sh as well when it is installed, the
// shell of OpenWRT). Names with spaces, quotes and $ check the quoting.
// Run with: dotnet run --project tests/RemoteFileOperationsTests
internal static class Program
{
    private static int _passed;

    private static string _shell = "/bin/sh";

    // For BusyBox: a directory with links to its applets, put first in PATH, so cp, mv, rm and mkdir are BusyBox's.
    private static string _appletPath;

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

    private static string RunOutput(string command)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo(_shell.Split(' ')[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in _shell.Split(' ')[1..])
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        using Process process = Process.Start(startInfo);
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static int Run(string command, out string errors)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo(_shell.Split(' ')[0])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (string argument in _shell.Split(' ')[1..])
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (_appletPath != null)
        {
            startInfo.Environment["PATH"] = _appletPath + ":" + Environment.GetEnvironmentVariable("PATH");
        }
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        using Process process = Process.Start(startInfo);
        errors = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void NameChecks()
    {
        Check(RemoteFileOperations.ValidateName("report.txt") == null, "a plain name is valid");
        Check(RemoteFileOperations.ValidateName("my file $1 'x'") == null, "spaces, quotes and $ are valid");
        Check(RemoteFileOperations.ValidateName("") != null, "an empty name is rejected");
        Check(RemoteFileOperations.ValidateName("   ") != null, "a blank name is rejected");
        Check(RemoteFileOperations.ValidateName("a/b") != null, "a slash is rejected");
        Check(RemoteFileOperations.ValidateName("..") != null, ".. is rejected");
        Check(RemoteFileOperations.ValidateName(".") != null, ". is rejected");

        HashSet<string> existing = new HashSet<string> { "report.txt", "report (copy).txt", "dir", ".bashrc" };
        Equal(RemoteFileOperations.MakeCopyName("report.txt", false, existing), "report (copy 2).txt", "the next free copy name keeps the extension");
        Equal(RemoteFileOperations.MakeCopyName("dir", true, existing), "dir (copy)", "a folder gets the suffix at the end");
        Equal(RemoteFileOperations.MakeCopyName("archive.d", true, existing), "archive.d (copy)", "a folder with a dot is not split");
        Equal(RemoteFileOperations.MakeCopyName(".bashrc", false, existing), ".bashrc (copy)", "a dot file has no extension");
        Equal(RemoteFileOperations.MakeCopyName("Makefile", false, existing), "Makefile (copy)", "a name without an extension");

        Check(RemoteFileOperations.IsSameOrInside("/a/b", "/a/b"), "a folder is inside itself");
        Check(RemoteFileOperations.IsSameOrInside("/a/b/c", "/a/b/"), "a subfolder is inside");
        Check(!RemoteFileOperations.IsSameOrInside("/a/bc", "/a/b"), "a sibling with the same prefix is not inside");
        Check(!RemoteFileOperations.IsSameOrInside("/a/B", "/a/b"), "paths are case-sensitive");
        Check(RemoteFileOperations.IsSameOrInside("/x", "/"), "everything is inside /");
        Equal(RemoteFileOperations.Join("/", "etc"), "/etc", "join to the root");
        Equal(RemoteFileOperations.Join("/home/u/", "f"), "/home/u/f", "join with a trailing slash");

        Check(RemoteFileOperations.IsExtractableArchive("backup.tar.gz") && RemoteFileOperations.IsExtractableArchive("backup.tgz") && RemoteFileOperations.IsExtractableArchive("backup.tar"), ".tar.gz, .tgz and .tar can be extracted");
        Check(RemoteFileOperations.IsExtractableArchive("BACKUP.TAR.GZ") && RemoteFileOperations.IsExtractableArchive("a b.Tgz"), "the extension is not case-sensitive");
        Check(!RemoteFileOperations.IsExtractableArchive("backup.gz") && !RemoteFileOperations.IsExtractableArchive("backup.zip") && !RemoteFileOperations.IsExtractableArchive("tar") && !RemoteFileOperations.IsExtractableArchive("backup.tar.bz2") && !RemoteFileOperations.IsExtractableArchive(null), "other files are not offered");
        Equal(RemoteFileOperations.ExtractCommand("/a b/it's.tar.gz", "/a b"), "tar -xzf '/a b/it'\\''s.tar.gz' -C '/a b'", "the extract command quotes both paths and asks for gzip");
        Equal(RemoteFileOperations.ExtractCommand("/a/plain.tar", "/a"), "tar -xf '/a/plain.tar' -C '/a'", "a plain tar is not decompressed");
    }

    private static void ScriptNameChecks()
    {
        Equal(RemoteScript.MakeRemotePath("backup.sh", "ab12cd34"), "/tmp/smartty-ab12cd34-backup.sh", "a script goes to /tmp with a token in its name");
        Equal(RemoteScript.MakeRemotePath("my script $(rm -rf x).sh", "t"), "/tmp/smartty-t-my_script___rm_-rf_x_.sh", "unusual characters of the name are replaced");
        Equal(RemoteScript.MakeRemotePath("-rf", "t"), "/tmp/smartty-t-rf", "a leading dash is dropped");
        Equal(RemoteScript.MakeRemotePath("", "t"), "/tmp/smartty-t-script.sh", "an empty name gets a default");
        Check(RemoteScript.MakeRemotePath(new string('a', 300), "t").Length < 80, "a long name is cut");

        Check(RemoteScript.Validate(new byte[0]) != null, "an empty file is not a script");
        Check(RemoteScript.Validate(new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0, 1 }) != null, "a binary file is not a script");
        Check(RemoteScript.Validate(new byte[RemoteScript.MaxLength + 1]) != null, "a huge file is not a script");
        Check(RemoteScript.Validate(System.Text.Encoding.UTF8.GetBytes("echo hi\n")) == null, "a text file is a script");

        string windows = "\uFEFF#!/bin/sh\r\necho one\r\nif true; then\r\n  echo \"a\rb\"\r\nfi\r\n";
        byte[] normalized = RemoteScript.Normalize(System.Text.Encoding.UTF8.GetBytes(windows));
        Equal(System.Text.Encoding.UTF8.GetString(normalized), "#!/bin/sh\necho one\nif true; then\n  echo \"a\rb\"\nfi\n", "CR LF becomes LF, a lone CR stays, the byte order mark is dropped");
        Check(RemoteScript.HasShebang(normalized) && !RemoteScript.HasShebang(System.Text.Encoding.UTF8.GetBytes("echo hi")), "a shebang is recognized after the byte order mark is dropped");

        Equal(RemoteScript.BuildCommand("/tmp/s 1.sh", true), "('/tmp/s 1.sh'; rc=$?; rm -f '/tmp/s 1.sh'; exit $rc)", "a script with a shebang is run as a program, in a subshell");
        Equal(RemoteScript.BuildCommand("/tmp/s.sh", false), "(sh '/tmp/s.sh'; rc=$?; rm -f '/tmp/s.sh'; exit $rc)", "a script without one is given to sh");

        Equal(RemoteScript.BuildRunInPlaceCommand("/a b/x.sh", new[] { RemoteScript.ExecutableMarker }), "(cd '/a b' && './x.sh')", "an executable script is run as it is, from its folder");
        Equal(RemoteScript.BuildRunInPlaceCommand("/a/x.sh", new[] { "#!/bin/bash" }), "(cd '/a' && '/bin/bash' './x.sh')", "a script that is not executable is given to the interpreter of its shebang");
        Equal(RemoteScript.BuildRunInPlaceCommand("/x.sh", new[] { "#!/usr/bin/env  bash -e " }), "(cd '/' && '/usr/bin/env' 'bash' '-e' './x.sh')", "the words of a shebang line are kept apart");
        Equal(RemoteScript.BuildRunInPlaceCommand("/a/x.sh", new[] { "echo hi" }), "(cd '/a' && sh './x.sh')", "a script without a shebang is given to sh");
        Equal(RemoteScript.BuildRunInPlaceCommand("/a/x.sh", null), "(cd '/a' && sh './x.sh')", "without an answer from the server sh is used");
        Equal(RemoteScript.BuildRunInPlaceCommand("/a/-x's.sh", new[] { "#!/bin/sh; rm -rf ~" }), "(cd '/a' && '/bin/sh;' 'rm' '-rf' '~' './-x'\\''s.sh')", "text of the shebang line is quoted, it is never shell syntax");
    }

    // The two steps of "Run" in the file list: ask the server about the file, then build the command from the answer.
    private static string InPlaceCommand(string path)
    {
        string probe = RunOutput(RemoteScript.ProbeCommand(path));
        return RemoteScript.BuildRunInPlaceCommand(path, probe.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string[] RunLines(string command)
    {
        return RunOutput(command).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static Process StartHelper(string script)
    {
        return Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", script }, UseShellExecute = false });
    }

    // The process list of the Process manager (read from the local /proc by this shell) and the commands that stop a process.
    private static void ProcessChecks()
    {
        Process busy = StartHelper("while :; do :; done");
        Process sleeper = StartHelper("exec sleep 60");
        Process stubborn = StartHelper("trap '' 15; while :; do sleep 1; done");
        try
        {
            ProcessSnapshot first = RemoteProcessList.Parse(RunLines(RemoteProcessList.ListCommand), null);
            System.Threading.Thread.Sleep(1500);
            ProcessSnapshot second = RemoteProcessList.Parse(RunLines(RemoteProcessList.ListCommand), first);
            string shell = _shell + ": ";
            Check(second.Processes.Count >= 4, shell + "the list has the processes (" + second.Processes.Count + ")");
            Check(second.TotalTicks > first.TotalTicks && second.CpuCount >= 1, shell + "the processor time of all cores grows and the cores are counted (" + second.CpuCount + ")");
            Check(second.UptimeSeconds > 0 && second.MemTotalKb > 0 && second.MemFreeKb.HasValue && second.LoadAverage != null && second.LoadAverage.Split(' ').Length == 3, shell + "uptime, memory and load are read");

            RemoteProcess self = second.Processes.Find(p => p.Pid == Environment.ProcessId);
            Check(self != null && self.Command.Length > 0 && self.MemoryKb > 0 && self.State.Length == 1 && self.ParentPid > 0, shell + "this test's own process is listed with command, memory, state and parent");
            Check(first.Processes.TrueForAll(p => p.CpuPercent == null), shell + "the first list has no processor shares yet");

            RemoteProcess busyRow = second.Processes.Find(p => p.Pid == busy.Id);
            Check(busyRow != null && busyRow.CpuPercent > 40 && busyRow.CpuPercent < 150, shell + "a process in a loop uses about one core (" + busyRow?.CpuPercent + " %)");
            RemoteProcess sleeperRow = second.Processes.Find(p => p.Pid == sleeper.Id);
            Check(sleeperRow != null && sleeperRow.CpuPercent < 5 && sleeperRow.Command.Contains("sleep 60") && sleeperRow.Name == "sleep" && sleeperRow.ParentPid == Environment.ProcessId, shell + "a sleeping process uses no processor, its name, command line and parent are right");
            Check(busyRow.User == sleeperRow.User && busyRow.User.Length > 0, shell + "the user is the same for both");
            Check(second.Processes.TrueForAll(p => p.Command.Length > 0 && p.Name.Length > 0 && p.State.Length > 0), shell + "every process has a name, a command and a state");

            Equal(RemoteProcessList.SignalCommand(42, force: false), "kill -15 42", "ending a process is kill -15");
            Equal(RemoteProcessList.SignalCommand(42, force: true), "kill -9 42", "killing a process is kill -9");
            Equal(Run(RemoteProcessList.SignalCommand(sleeper.Id, force: false), out _), 0, shell + "a process is asked to end");
            Check(sleeper.WaitForExit(3000), shell + "it ends");
            System.Threading.Thread.Sleep(500);
            Equal(Run(RemoteProcessList.SignalCommand(stubborn.Id, force: false), out _), 0, shell + "a process that ignores the request is signalled");
            System.Threading.Thread.Sleep(700);
            Check(!stubborn.HasExited, shell + "it ignores the request to end");
            Equal(Run(RemoteProcessList.SignalCommand(stubborn.Id, force: true), out _), 0, shell + "it is killed");
            Check(stubborn.WaitForExit(3000), shell + "and it is gone");
            Check(Run(RemoteProcessList.SignalCommand(sleeper.Id, force: false), out string errors) != 0 && errors.Length > 0, shell + "signalling a process that is gone fails with a message");
        }
        finally
        {
            foreach (Process helper in new[] { busy, sleeper, stubborn })
            {
                try
                {
                    if (!helper.HasExited)
                    {
                        helper.Kill();
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        // Lines of the shell that are not part of the list are skipped, a command line may hold the separator.
        ProcessSnapshot parsed = RemoteProcessList.Parse(new[]
        {
            "sh: can't open something", "T|1000|4", "U|3600.5", "L|0.10 0.05 0.01", "M|2048000|100000|1500000", "N|0|root", "N|0|toor", "N|1000|alice",
            "P|1|0|S|10|1200|0|init|/sbin/init splash", "P|20|1|R|50|0|1000|worker|worker --opt a|b  ", "garbage", "P|x|1|S|0|0|0|bad|bad"
        }, null);
        Equal(parsed.Processes.Count, 2, "only well-formed process lines are taken");
        Check(parsed.Processes[1].User == "alice" && parsed.Processes[1].Command == "worker --opt a|b" && parsed.Processes[1].MemoryKb == 0 && parsed.Processes[0].User == "root", "users are named, the first name of a number wins, a command line may hold a |");
        Check(parsed.MemTotalKb == 2048000 && parsed.MemFreeKb == 1500000 && parsed.CpuCount == 4 && parsed.UptimeSeconds == 3600.5, "the numbers around the list are read (available memory is preferred to free)");
        ProcessSnapshot later = RemoteProcessList.Parse(new[] { "T|1400|4", "P|1|0|S|10|1200|0|init|init", "P|20|1|R|150|0|1000|worker|worker", "P|30|1|S|5|0|0|new|new" }, parsed);
        Check(later.Processes[0].CpuPercent == 0 && later.Processes[1].CpuPercent == 100 && later.Processes[2].CpuPercent == null, "the share is the process time of the interval in the time of all cores, 4 cores x 100 ticks of 400 is 100 %, a new process has none yet");
        Equal(RemoteProcessList.FormatKb(512), "512 kB", "small sizes are in kilobytes");
        Equal(RemoteProcessList.FormatKb(100 * 1024), "100 MB", "larger sizes are in megabytes");
        Equal(RemoteProcessList.FormatKb(3L * 1024 * 1024 * 10), "30 GB", "the largest in gigabytes");
    }

    private static void RunInPlaceChecks(string root)
    {
        Check(RemoteScript.IsScriptFileName("run.sh") && RemoteScript.IsScriptFileName("RUN.SH") && RemoteScript.IsScriptFileName("a.bash") && RemoteScript.IsScriptFileName("a.ash") && RemoteScript.IsScriptFileName("a b.ksh") && RemoteScript.IsScriptFileName("x.zsh") && RemoteScript.IsScriptFileName("x.dash"), "shell script extensions are recognized");
        Check(!RemoteScript.IsScriptFileName("sh") && !RemoteScript.IsScriptFileName(".sh") && !RemoteScript.IsScriptFileName("notes.txt") && !RemoteScript.IsScriptFileName("a.sh.bak") && !RemoteScript.IsScriptFileName("") && !RemoteScript.IsScriptFileName(null), "other names are not offered");

        string folder = Path.Combine(root, "it's a folder");
        Directory.CreateDirectory(folder);
        // The scripts print the folder they run in and what they were given as $0, and exit with 5.
        string body = "echo \"dir=$(pwd | sed 's|.*/||') name=$0\"\necho from-stderr >&2\nexit 5\n";
        (string Name, string Content, bool Executable, string Expect)[] cases =
        {
            ("exec shebang.sh", "#!/bin/sh\n" + body, true, "dir=it's a folder name=./exec shebang.sh"),
            ("plain shebang.sh", "#!/bin/sh\n" + body, false, "dir=it's a folder name=./plain shebang.sh"),
            ("env shebang.sh", "#!/usr/bin/env sh\n" + body, false, "dir=it's a folder name=./env shebang.sh"),
            ("crlf shebang.sh", "#!/bin/sh\r\n" + body, false, "dir=it's a folder name=./crlf shebang.sh"),
            ("no shebang.sh", body, false, "dir=it's a folder name=./no shebang.sh"),
            ("-dash.sh", body, false, "dir=it's a folder name=./-dash.sh"),
        };
        foreach (var testCase in cases)
        {
            string path = Path.Combine(folder, testCase.Name);
            File.WriteAllText(path, testCase.Content);
            Equal(Run("chmod " + (testCase.Executable ? "700" : "600") + " " + RemotePathUtils.ShellQuote(path), out _), 0, "set the mode of " + testCase.Name);
            string inPlace = InPlaceCommand(path);
            string text = RunOutput(inPlace);
            Check(text.Contains(testCase.Expect) && text.Contains("from-stderr"), _shell + ": " + testCase.Name + " runs from its own folder (got: " + text.Trim().Replace("\n", " | ") + ")");
            Equal(Run(inPlace, out _), 5, testCase.Name + ": the exit status of the script is kept");
            Check(RunOutput(inPlace + "; echo still-here").Contains("still-here"), testCase.Name + ": the shell that typed the command goes on afterwards");
            Check(File.Exists(path) && File.ReadAllText(path) == testCase.Content, testCase.Name + ": the script is left unchanged");
        }
        Check(Run(InPlaceCommand(Path.Combine(folder, "missing.sh")), out string errors) != 0 && errors.Length > 0, "a script that does not exist fails with a message");
    }

    private static void ScriptRunChecks(string root)
    {
        string work = Path.Combine(root, "work dir");
        Directory.CreateDirectory(work);
        foreach (bool shebang in new[] { true, false })
        {
            string path = Path.Combine(root, "it's a $script" + (shebang ? "-shebang" : "-plain") + ".sh");
            byte[] script = RemoteScript.Normalize(System.Text.Encoding.UTF8.GetBytes((shebang ? "#!/bin/sh\r\n" : "") + "echo \"in $(pwd | sed 's|.*/||')\"\r\necho to-stderr >&2\r\nexit 7\r\n"));
            File.WriteAllBytes(path, script);
            if (shebang)
            {
                Equal(Run("chmod 700 " + RemotePathUtils.ShellQuote(path), out _), 0, "make the script executable");
            }
            // The command is typed into a terminal that is in the directory the script is to run in.
            string typed = RemoteScript.BuildCommand(path, shebang);
            int exit = Run("cd " + RemotePathUtils.ShellQuote(work) + " && " + typed, out string errors);
            Equal(exit, 7, _shell + ": the exit status of the script is the exit status of the command (" + (shebang ? "shebang" : "no shebang") + ")");
            Check(errors.Contains("to-stderr"), "the script's error output comes through");
            Check(!File.Exists(path), "the script is removed after it has run");
        }
        string output = RunOutput(RemoteScript.BuildCommand(Path.Combine(root, "missing.sh"), true) + "; echo still-here");
        Check(output.Contains("still-here") && !File.Exists(Path.Combine(root, "missing.sh")), "a missing script fails, the shell that typed the command goes on, nothing is left behind");
    }

    private static void CommandChecks(string root)
    {
        string name = "it's a $file";
        string source = Path.Combine(root, name);
        File.WriteAllText(source, "data");
        string folder = Path.Combine(root, "my dir");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "inner.txt"), "inner");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);

        Equal(Run(RemoteFileOperations.CopyCommand(source, Path.Combine(target, name), replace: false), out _), 0, _shell + ": copy a file");
        Equal(File.ReadAllText(Path.Combine(target, name)), "data", "the copy has the content");
        Check(File.Exists(source), "copying keeps the source");

        int exit = Run(RemoteFileOperations.CopyCommand(source, Path.Combine(target, name), replace: false), out string errors);
        Check(exit != 0 && errors.Contains("already exists"), _shell + ": copying onto an existing file fails with a message");
        File.WriteAllText(source, "new data");
        Equal(Run(RemoteFileOperations.CopyCommand(source, Path.Combine(target, name), replace: true), out _), 0, "replace an existing file");
        Equal(File.ReadAllText(Path.Combine(target, name)), "new data", "the replaced file has the new content");

        Equal(Run(RemoteFileOperations.CopyCommand(folder, Path.Combine(target, "my dir"), replace: false), out _), 0, "copy a folder recursively");
        Check(File.Exists(Path.Combine(target, "my dir", "sub", "inner.txt")), "the folder copy has the nested file");
        exit = Run(RemoteFileOperations.CopyCommand(folder, Path.Combine(target, "my dir"), replace: false), out _);
        Check(exit != 0 && !Directory.Exists(Path.Combine(target, "my dir", "my dir")), "copying a folder onto an existing one does not nest it inside");
        File.WriteAllText(Path.Combine(target, "my dir", "stale.txt"), "stale");
        Equal(Run(RemoteFileOperations.CopyCommand(folder, Path.Combine(target, "my dir"), replace: true), out _), 0, "replace an existing folder");
        Check(!File.Exists(Path.Combine(target, "my dir", "stale.txt")), "the replaced folder has no stale files");

        string moved = Path.Combine(target, "moved $x");
        Equal(Run(RemoteFileOperations.MoveCommand(source, moved, replace: false), out _), 0, "move a file");
        Check(!File.Exists(source) && File.Exists(moved), "the moved file is gone from the source");
        File.WriteAllText(source, "again");
        exit = Run(RemoteFileOperations.MoveCommand(source, moved, replace: false), out errors);
        Check(exit != 0 && File.Exists(source) && File.ReadAllText(moved) == "new data", "moving onto an existing file does not overwrite it");
        Equal(Run(RemoteFileOperations.MoveCommand(source, Path.Combine(root, "renamed"), replace: false), out _), 0, "rename a file");
        Check(File.Exists(Path.Combine(root, "renamed")), "the renamed file exists");

        string newFolder = Path.Combine(root, "new 'folder'");
        Equal(Run(RemoteFileOperations.CreateDirectoryCommand(newFolder), out _), 0, "create a folder");
        Check(Directory.Exists(newFolder), "the folder exists");
        Check(Run(RemoteFileOperations.CreateDirectoryCommand(newFolder), out _) != 0, "creating an existing folder fails");
        string newFile = Path.Combine(root, "empty $file.txt");
        Equal(Run(RemoteFileOperations.CreateFileCommand(newFile), out _), 0, "create an empty file");
        Check(File.Exists(newFile) && new FileInfo(newFile).Length == 0, "the new file is empty");
        File.WriteAllText(newFile, "keep");
        Check(Run(RemoteFileOperations.CreateFileCommand(newFile), out _) != 0 && File.ReadAllText(newFile) == "keep", "creating an existing file fails and keeps its content");

        // Extract here: archives made with the host tar (names with spaces, quotes and $), unpacked by this shell's tar.
        string packed = Path.Combine(root, "packed dir");
        Directory.CreateDirectory(Path.Combine(packed, "docs"));
        File.WriteAllText(Path.Combine(packed, "it's $one.txt"), "one");
        File.WriteAllText(Path.Combine(packed, "docs", "two.txt"), "two");
        string archiveDirectory = Path.Combine(root, "it's archives");
        Directory.CreateDirectory(archiveDirectory);
        string gz = Path.Combine(archiveDirectory, "pack $1.tar.gz");
        string plain = Path.Combine(archiveDirectory, "pack.tar");
        Equal(Run("tar -czf " + RemotePathUtils.ShellQuote(gz) + " -C " + RemotePathUtils.ShellQuote(root) + " 'packed dir'", out _), 0, "make a .tar.gz");
        Equal(Run("tar -cf " + RemotePathUtils.ShellQuote(plain) + " -C " + RemotePathUtils.ShellQuote(root) + " 'packed dir'", out _), 0, "make a .tar");
        Equal(Run(RemoteFileOperations.ExtractCommand(gz, archiveDirectory), out errors), 0, _shell + ": extract a .tar.gz" + errors);
        Equal(File.ReadAllText(Path.Combine(archiveDirectory, "packed dir", "it's $one.txt")), "one", "the extracted file is next to the archive");
        Check(File.Exists(Path.Combine(archiveDirectory, "packed dir", "docs", "two.txt")) && File.Exists(gz), "the nested file is extracted and the archive is kept");
        Directory.Delete(Path.Combine(archiveDirectory, "packed dir"), recursive: true);
        Equal(Run(RemoteFileOperations.ExtractCommand(plain, archiveDirectory), out errors), 0, _shell + ": extract a .tar" + errors);
        Check(File.Exists(Path.Combine(archiveDirectory, "packed dir", "docs", "two.txt")), "the plain tar is extracted");
        File.WriteAllText(Path.Combine(archiveDirectory, "broken.tar.gz"), "this is not an archive");
        Check(Run(RemoteFileOperations.ExtractCommand(Path.Combine(archiveDirectory, "broken.tar.gz"), archiveDirectory), out errors) != 0 && errors.Length > 0, "a file that is not an archive fails with a message");

        Equal(Run(RemoteFileOperations.DeleteCommand(new[] { newFile, Path.Combine(target, "my dir") }), out _), 0, "delete a file and a folder");
        Check(!File.Exists(newFile) && !Directory.Exists(Path.Combine(target, "my dir")), "both are gone");
    }

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "smartty-fileops-" + Guid.NewGuid().ToString("N"));
        try
        {
            NameChecks();
            ScriptNameChecks();
            List<string> shells = new List<string> { "/bin/sh" };
            foreach (string busybox in new[] { "/bin/busybox", "/usr/bin/busybox" })
            {
                if (File.Exists(busybox))
                {
                    shells.Add(busybox + " sh");
                    break;
                }
            }
            foreach (string shell in shells)
            {
                _shell = shell;
                _appletPath = null;
                if (shell.Contains("busybox"))
                {
                    _appletPath = Path.Combine(root, "applets");
                    Directory.CreateDirectory(_appletPath);
                    foreach (string applet in new[] { "cp", "mv", "rm", "mkdir", "tar", "sh", "tr" })
                    {
                        File.CreateSymbolicLink(Path.Combine(_appletPath, applet), shell.Split(' ')[0]);
                    }
                }
                string shellRoot = Path.Combine(root, Path.GetFileName(shell.Replace(" ", "-")));
                Directory.CreateDirectory(shellRoot);
                CommandChecks(shellRoot);
                ScriptRunChecks(shellRoot);
                RunInPlaceChecks(shellRoot);
                if (Directory.Exists("/proc/self"))
                {
                    ProcessChecks();
                }
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
        Console.WriteLine($"All {_passed} checks passed.");
        return 0;
    }
}
