using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SmarTTY.IO;

// Regression harness for the listing that the file-by-file directory download uses (it replaced "ls -1ALF", whose
// marks cannot be told from the last character of a name). The parser is checked on canned output; on a host with
// /bin/sh the generated command is also run against a folder with awkward names.
// Run with: dotnet run --project tests/RemoteDirectoryListingTests
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

    private static string Joined(IEnumerable<string> names)
    {
        return string.Join("|", names.OrderBy(name => name, StringComparer.Ordinal));
    }

    private static int Run(string fileName, string[] arguments, string workingDirectory, out List<string> lines)
    {
        ProcessStartInfo info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(info);
        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        lines = output.Split('\n').ToList();
        if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return process.ExitCode;
    }

    private static void CheckParser()
    {
        RemoteDirectoryListing listing = RemoteDirectoryListing.Parse(new[]
        {
            "f plain.txt", "f a=", "f b>", "f c|", "f d@", "f star*", "f ends with space ", "d dir", "d dir=", "d star*", "!", "", "noise", "f ", "d "
        });
        Check(Joined(listing.Files) == "a=|b>|c||d@|ends with space |plain.txt|star*", "parser: a name keeps every character, whatever it ends with");
        Check(Joined(listing.Directories) == "dir|dir=|star*", "parser: folders are told by their letter, not by a trailing slash");
        Check(listing.NamesWithLineBreak == 1, "parser: the mark of a name with a line break is counted");
        Check(RemoteDirectoryListing.Parse(new string[0]).Files.Count == 0, "parser: an empty folder gives nothing");
    }

    private static void CheckCommand()
    {
        string command = RemoteDirectoryListing.BuildCommand("/srv/it's here");
        Check(command.StartsWith("/bin/sh -c '"), "command: runs in /bin/sh whatever the login shell is");
        Check(command.IndexOf('\n') == -1, "command: one line (tcsh reads a line feed inside quotes as the end of the command)");
    }

    private static void CheckOnShell()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            Console.WriteLine("SKIP: the generated command is only run where /bin/sh exists");
            return;
        }
        string root = Path.Combine(Path.GetTempPath(), "smartty-ls-" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(root, "it's a dir");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string name in new[] { "plain.txt", "eq=", "gt>", "pipe|", "at@", "star*", "sp ace", "-dash", ".hidden", "..dots", "back\\slash", "dq\"uote", "q'uote", "\u0442\u0435\u0441\u0442.txt" })
            {
                File.WriteAllText(Path.Combine(dir, name), "x");
            }
            File.WriteAllText(Path.Combine(dir, "tool*"), "x");
            File.SetUnixFileMode(Path.Combine(dir, "tool*"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(dir, "exe"), "x");
            File.SetUnixFileMode(Path.Combine(dir, "exe"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(dir, "a\nb"), "x");
            Directory.CreateDirectory(Path.Combine(dir, "dir"));
            Directory.CreateDirectory(Path.Combine(dir, "dir="));
            Directory.CreateDirectory(Path.Combine(dir, "dir*"));
            File.CreateSymbolicLink(Path.Combine(dir, "link-to-file"), "plain.txt");
            File.CreateSymbolicLink(Path.Combine(dir, "link-to-dir"), "dir");
            File.CreateSymbolicLink(Path.Combine(dir, "broken"), "no-such-thing");
            bool fifo = Run("mkfifo", new[] { Path.Combine(dir, "fifo") }, root, out _) == 0;

            int exitCode = Run("/bin/sh", new[] { "-c", RemoteDirectoryListing.BuildCommand(dir) }, root, out List<string> lines);
            RemoteDirectoryListing listing = RemoteDirectoryListing.Parse(lines);
            Check(exitCode == 0, "shell: the command succeeds");
            string expectedFiles = Joined(new[]
            {
                "plain.txt", "eq=", "gt>", "pipe|", "at@", "star*", "sp ace", "-dash", ".hidden", "..dots", "back\\slash", "dq\"uote", "q'uote",
                "\u0442\u0435\u0441\u0442.txt", "tool*", "exe", "link-to-file"
            });
            Check(Joined(listing.Files) == expectedFiles, "shell: files with =, >, |, @ and * at the end of the name are all listed under their own names");
            Check(Joined(listing.Directories) == "dir|dir*|dir=|link-to-dir", "shell: folders, and a link to a folder, are listed");
            Check(listing.NamesWithLineBreak == 1, "shell: a name with a line break is counted, not split into two names");
            Check(!listing.Files.Contains("broken") && (!fifo || !listing.Files.Contains("fifo")), "shell: a broken link and a pipe are not offered for download");
            Check(!listing.Files.Contains("tool") && !listing.Files.Contains("star"), "shell: a name is never cut at its last character");

            int missing = Run("/bin/sh", new[] { "-c", RemoteDirectoryListing.BuildCommand(Path.Combine(root, "nothing here")) }, root, out _);
            Check(missing != 0, "shell: a folder that cannot be entered fails the command");

            string emptyDir = Path.Combine(root, "empty");
            Directory.CreateDirectory(emptyDir);
            exitCode = Run("/bin/sh", new[] { "-c", RemoteDirectoryListing.BuildCommand(emptyDir) }, root, out lines);
            Check(exitCode == 0 && RemoteDirectoryListing.Parse(lines).Files.Count == 0 && RemoteDirectoryListing.Parse(lines).Directories.Count == 0, "shell: an empty folder succeeds and lists nothing");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static int Main()
    {
        CheckParser();
        CheckCommand();
        CheckOnShell();
        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed.");
        return 0;
    }
}
