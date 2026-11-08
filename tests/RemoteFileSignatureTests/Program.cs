using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SmarTTY.IO;

// The signature with which the editor notices that somebody else changed the file on the server (IO/RemoteFileSignature.cs). The
// command is run by the shell of this machine (and by BusyBox where it is installed) on real files: the point is that a change in the
// same second that keeps the size no longer goes unnoticed.
// Run with: dotnet run --project tests/RemoteFileSignatureTests
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

    private static int Run(string shell, string[] shellArguments, string path, string command, out List<string> lines)
    {
        ProcessStartInfo info = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in shellArguments)
        {
            info.ArgumentList.Add(argument);
        }
        info.ArgumentList.Add(command);
        if (path != null)
        {
            info.Environment["PATH"] = path;
        }
        using Process process = Process.Start(info);
        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        lines = output.Split('\n').Where(line => line.Length > 0).ToList();
        return process.ExitCode;
    }

    private static string Sign(string shell, string[] shellArguments, string path, string file)
    {
        int exit = Run(shell, shellArguments, path, RemoteFileSignature.BuildCommand(file), out List<string> lines);
        return exit == 0 ? RemoteFileSignature.Parse(lines) : null;
    }

    private static void Touch(string file, string epoch)
    {
        ProcessStartInfo info = new ProcessStartInfo("touch")
        {
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("-d");
        info.ArgumentList.Add("@" + epoch);
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(file);
        using Process process = Process.Start(info);
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new Exception("touch failed: " + error);
        }
    }

    private static void CheckParser()
    {
        Check(RemoteFileSignature.Parse(null) == null && RemoteFileSignature.Parse(new string[0]) == null && RemoteFileSignature.Parse(new[] { "  ", "" }) == null, "parse: no output is no signature");
        Check(RemoteFileSignature.Parse(new[] { "2023-11-14 22:13:20.100000000 +0000 5\r" }) == "2023-11-14 22:13:20.100000000 +0000 5", "parse: the line is trimmed");
        Check(RemoteFileSignature.Parse(new[] { "abc  /tmp/f" }) == "abc  /tmp/f", "parse: a checksum line is a signature as well");
    }

    private static void CheckOn(string label, string shell, string[] shellArguments, string path, string folder)
    {
        string file = Path.Combine(folder, label + " it's a file.txt");
        File.WriteAllText(file, "aaaa\n");
        Touch(file, "1700000000.100000000");
        string first = Sign(shell, shellArguments, path, file);
        Check(first != null && first.StartsWith("2023-11-14 22:13:20.1") && first.EndsWith(" 5"), label + ": the signature of a file is its time to the fraction of a second and its size (" + first + ")");
        Check(first == Sign(shell, shellArguments, path, file), label + ": the signature of an unchanged file is the same every time");

        // Rewritten in place, the same size, the same second (what a script or an editor that saves in place does).
        using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Write))
        {
            stream.Write(new byte[] { (byte)'b', (byte)'b', (byte)'b', (byte)'b', (byte)'\n' }, 0, 5);
        }
        Touch(file, "1700000000.900000000");
        string second = Sign(shell, shellArguments, path, file);
        Check(second != null && second != first, label + ": a change in the same second that keeps the size changes the signature");
        Run(shell, shellArguments, path, "stat -c '%Y %s' '" + file + "'", out List<string> oldAfter);
        Touch(file, "1700000000.100000000");
        Run(shell, shellArguments, path, "stat -c '%Y %s' '" + file + "'", out List<string> oldBefore);
        Check(oldAfter.SequenceEqual(oldBefore), label + ": (the signature of seconds and size, used before, was the same for both)");

        Check(Sign(shell, shellArguments, path, Path.Combine(folder, "no such file")) == null, label + ": a file that is not there has no signature (the command fails)");
    }

    private static void CheckOnShell()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            Console.WriteLine("SKIP: the command is only run where /bin/sh exists");
            return;
        }
        string folder = Path.Combine(Path.GetTempPath(), "smartty-sig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            CheckOn("sh", "/bin/sh", new[] { "-c" }, null, folder);

            // Where stat has no format (or does not exist) the checksum is the signature.
            string stubs = Path.Combine(folder, "stubs");
            Directory.CreateDirectory(stubs);
            File.WriteAllText(Path.Combine(stubs, "stat"), "#!/bin/sh\nexit 1\n");
            Run("chmod", new[] { "+x" }, null, Path.Combine(stubs, "stat"), out _);
            string md5 = Run("which", new string[0], null, "md5sum", out List<string> found) == 0 ? found[0] : null;
            if (md5 != null)
            {
                File.CreateSymbolicLink(Path.Combine(stubs, "md5sum"), md5);
                string target = Path.Combine(folder, "checksum.txt");
                File.WriteAllText(target, "hello\n");
                string signature = Sign("/bin/sh", new[] { "-c" }, stubs, target);
                Check(signature != null && signature.StartsWith("b1946ac92492d2347c6235b4d2611184"), "sh without a usable stat: the signature is the md5 checksum (" + signature + ")");
                File.WriteAllText(target, "hellp\n");
                Check(Sign("/bin/sh", new[] { "-c" }, stubs, target) != signature, "sh without a usable stat: another content, another checksum");
            }

            string busybox = new[] { "/usr/bin/busybox", "/bin/busybox" }.FirstOrDefault(File.Exists);
            if (busybox == null)
            {
                Console.WriteLine("SKIP: BusyBox is not installed");
                return;
            }
            string applets = Path.Combine(folder, "applets");
            Directory.CreateDirectory(applets);
            foreach (string applet in new[] { "stat", "md5sum" })
            {
                File.CreateSymbolicLink(Path.Combine(applets, applet), busybox);
            }
            CheckOn("busybox", busybox, new[] { "sh", "-c" }, applets, folder);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static int Main()
    {
        CheckParser();
        CheckOnShell();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " checks passed.");
        return 0;
    }
}
