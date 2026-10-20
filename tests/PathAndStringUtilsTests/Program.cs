using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SmarTTY.IO;
using SmarTTY.Utilities;

// Regression harness for the remote path helpers and the size formatting/parsing helpers.
// Run with: dotnet run --project tests/PathAndStringUtilsTests
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

    private static void Equal(string expected, string actual, string name)
    {
        Check(expected == actual, name + " (expected " + (expected ?? "null") + ", got " + (actual ?? "null") + ")");
    }

    // The real shell must see exactly the name that was quoted: create files and directories with awkward names
    // through the quoted commands that the sync engines produce, and look at what appeared.
    private static void CheckShellRoundTrip()
    {
        string[] names =
        {
            "Foo$Bar", "$HOME", "${PATH}", "`echo hacked`", "$(echo hacked)", "with space", "it's", "say \"hi\"",
            "back\\slash", "-leading-dash", "semi;colon && amp", "new\nline", "Привет мир", "star*", "tilde~",
        };
        string root = Path.Combine(Path.GetTempPath(), "smartty-quote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (!RunShell("true", root))
            {
                Console.WriteLine("SKIP: no 'sh', the shell round trip was not run");
                return;
            }
            foreach (string name in names)
            {
                string q = RemotePathUtils.ShellQuote(name);
                // the same shapes as the generated sync scripts: mkdir, cd, touch, ln -s, rm, rmdir
                // (the scripts "cd" into absolute paths, so the directory is given with a ./ here)
                bool ok = RunShell("mkdir -- " + q + " && cd " + RemotePathUtils.ShellQuote("./" + name) + " && touch -- " + q + " && ln -s -- " + q + " " + RemotePathUtils.ShellQuote("link-to-" + name), root);
                Check(ok && Directory.Exists(Path.Combine(root, name)) && File.Exists(Path.Combine(root, name, name)), "shell: created and entered \"" + name.Replace("\n", "\\n") + "\" literally");
            }
            Check(Directory.GetDirectories(root).Length == names.Length, "shell: exactly one directory per name was created (" + Directory.GetDirectories(root).Length + ")");
            foreach (string name in names)
            {
                string q = RemotePathUtils.ShellQuote(name);
                bool ok = RunShell("rm -f -- " + RemotePathUtils.ShellQuote(name + "/link-to-" + name) + " && rm -f -- " + RemotePathUtils.ShellQuote(name + "/" + name) + " && rmdir -- " + q, root);
                Check(ok && !Directory.Exists(Path.Combine(root, name)), "shell: removed with rm and rmdir: \"" + name.Replace("\n", "\\n") + "\"");
            }
            Check(!Directory.EnumerateFileSystemEntries(root).Any(), "shell: nothing is left, nothing outside was touched");
            // "rm -f" cannot remove a directory, rmdir can: this is why the empty directory action uses rmdir
            Directory.CreateDirectory(Path.Combine(root, "emptydir"));
            Check(!RunShell("rm -f -- 'emptydir'", root) && Directory.Exists(Path.Combine(root, "emptydir")), "shell: rm -f leaves a directory in place and fails");
            Check(RunShell("rmdir -- 'emptydir'", root) && !Directory.Exists(Path.Combine(root, "emptydir")), "shell: rmdir removes the empty directory");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Text put between double quotes must come out of the shell unchanged: this is how a typed command is written to
    // the history file and handed to "shell -c".
    private static void CheckDoubleQuoteEscaping()
    {
        string root = Path.GetTempPath();
        string[] samples =
        {
            "plain", "with $HOME", "back`tick`", "$(echo hacked)", "say \"hi\"", "a\\b", "trailing backslash\\", "\\\"", "\\$x \\`y\\`", "'single'", "mixed \\ \" $ ` end",
        };
        foreach (string sample in samples)
        {
            string output = ShellOutput("printf '%s' \"" + RemotePathUtils.EscapeForDoubleQuotes(sample) + "\"", root);
            if (output == null)
            {
                Console.WriteLine("SKIP: no 'sh', the double quote escaping was not run");
                return;
            }
            Check(output == sample, "double quotes: [" + sample + "] comes out unchanged (got [" + output + "])");
        }
        Equal("", RemotePathUtils.EscapeForDoubleQuotes(null), "double quotes: null");
    }

    private static string ShellOutput(string command, string workDir)
    {
        try
        {
            ProcessStartInfo info = new ProcessStartInfo("sh")
            {
                WorkingDirectory = workDir,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
            using Process process = Process.Start(info);
            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool RunShell(string command, string workDir)
    {
        try
        {
            ProcessStartInfo info = new ProcessStartInfo("sh")
            {
                WorkingDirectory = workDir,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
            info.Environment["LC_ALL"] = "C.UTF-8";
            using Process process = Process.Start(info);
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static int Main()
    {
        // --- file names and extensions ---
        Equal("txt", RemotePathUtils.GetExtension("/a/b.txt").TrimStart('.'), "extension: ordinary file");
        Equal("", RemotePathUtils.GetExtension("/a/b"), "extension: none");
        Equal("", RemotePathUtils.GetExtension("/a/dir.d/"), "extension: a directory path with a trailing slash does not throw");
        Equal("", RemotePathUtils.GetExtension("dir/"), "extension: relative directory path");
        Equal("/a", RemotePathUtils.GetDirectoryName("/a/b"), "directory name");
        Equal("/", RemotePathUtils.GetDirectoryName("/b"), "directory name of a root level entry");

        // --- normalization of unix paths ---
        Equal("/a/c", RemotePathUtils.NormalizeUnix("/a/b/../c"), "normalize: ..");
        Equal("/a/b", RemotePathUtils.NormalizeUnix("/a/./b"), "normalize: .");
        Equal("/", RemotePathUtils.NormalizeUnix("/.."), "normalize: the parent of / is /");
        Equal("/etc", RemotePathUtils.NormalizeUnix("/../../etc"), "normalize: going above the root stays at the root");
        Equal("/b", RemotePathUtils.NormalizeUnix("/a/../../b"), "normalize: above the root in the middle");
        Equal("../b", RemotePathUtils.NormalizeUnix("a/../../b"), "normalize: a relative path keeps the leading ..");
        Equal("..", RemotePathUtils.NormalizeUnix("../a/.."), "normalize: relative, back to the parent");

        // --- mixed separators ---
        Equal("b", RemotePathUtils.NormalizeMixed("a/../b"), "mixed: a relative path stays relative");
        Equal("../b", RemotePathUtils.NormalizeMixed("a/../../b"), "mixed: leading .. of a relative path is kept");
        Equal("../../x", RemotePathUtils.NormalizeMixed("../../x"), "mixed: .. is not collapsed with another ..");
        Equal("/x/b", RemotePathUtils.NormalizeMixed("/x/a/../b"), "mixed: an absolute path stays absolute");
        Equal("a/c", RemotePathUtils.NormalizeMixed("a/b/../c"), "mixed: .. in the middle");
        Equal("C:\\a\\c", RemotePathUtils.NormalizeMixed("C:\\a\\b\\..\\c"), "mixed: windows path");

        // --- relative paths ---
        Equal(".", RemotePathUtils.GetRelativePath("/a/b", "/a/b"), "relative: the same path");
        Equal(".", RemotePathUtils.GetRelativePath("/a/b", "/a/b/"), "relative: the same path with a trailing slash is not an empty string");
        Equal(".", RemotePathUtils.GetRelativePath("/a/b", "/a/./b"), "relative: the same path written differently");
        Equal("c", RemotePathUtils.GetRelativePath("/a/b", "/a/b/c"), "relative: a child");
        Equal("..", RemotePathUtils.GetRelativePath("/a/b", "/a"), "relative: the parent");
        Equal("../d", RemotePathUtils.GetRelativePath("/a/b", "/a/d"), "relative: a sibling");
        Equal("../../x/y", RemotePathUtils.GetRelativePath("/a/b", "/x/y", allowAbsoluteFallback: false), "relative: nothing in common without the absolute fallback");
        Equal("/x/y", RemotePathUtils.GetRelativePath("/a/b", "/x/y"), "relative: absolute fallback");

        // --- combine ---
        Equal("/a/b", RemotePathUtils.Combine("/a/", "b"), "combine: trailing slash");
        Equal("/b", RemotePathUtils.Combine("/a", "/b"), "combine: an absolute second path wins");
        Equal("/a/.hidden", RemotePathUtils.Combine("/a", ".hidden"), "combine: a hidden name is relative");

        // --- quoting for the remote shell ---
        Equal("'plain'", RemotePathUtils.ShellQuote("plain"), "quote: plain word");
        Equal("'it'\\''s'", RemotePathUtils.ShellQuote("it's"), "quote: a single quote is closed, escaped and reopened");
        Equal("''", RemotePathUtils.ShellQuote(""), "quote: empty");
        Equal("''", RemotePathUtils.ShellQuote(null), "quote: null");
        CheckShellRoundTrip();
        CheckDoubleQuoteEscaping();

        // --- sizes ---
        Equal("512", StringUtils.FormatSize(512), "size: small");
        Equal("20K", StringUtils.FormatSize(20 * 1024), "size: kilobytes");
        Equal("50M", StringUtils.FormatSize(50L * 1024 * 1024), "size: megabytes");
        Equal("20G", StringUtils.FormatSize(20L * 1024 * 1024 * 1024), "size: gigabytes");
        Equal("5000G", StringUtils.FormatSize(5000L * 1024 * 1024 * 1024), "size: 5000 GB");
        Equal("20000G", StringUtils.FormatSize(20000L * 1024 * 1024 * 1024), "size: above 10 TB stays in gigabytes, not kilobytes");
        Equal("20GB", StringUtils.FormatSize(20L * 1024 * 1024 * 1024, appendUnit: true), "size: with the unit");

        Check(StringUtils.ParseNumber("12") == 12UL, "number: decimal");
        Check(StringUtils.ParseNumber("  12") == 12UL, "number: leading blanks");
        Check(StringUtils.ParseNumber("12 bytes") == 12UL, "number: text after the number");
        Check(StringUtils.ParseNumber("0x1F") == 31UL, "number: hex");
        Check(StringUtils.ParseNumber("abc") == null, "number: not a number");
        Check(StringUtils.ParseNumber(null) == null, "number: null");
        Check(StringUtils.ParseSize("2K") == 2048UL, "parse size: kilobytes");

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed.");
        return 0;
    }
}
