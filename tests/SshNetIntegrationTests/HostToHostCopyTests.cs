using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SmarTTY.IO;
using SmarTTY.Sync;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Drives <see cref="DirectoryCopier.CopyBetweenHosts"/> against a real OpenSSH server: the source
/// <c>tar</c> stream of one session is piped straight into the extracting <c>tar</c> of another. The test
/// server plays both hosts (two sessions to the same machine, different directories), which is enough to
/// exercise the two-channel plumbing, the streaming and the error reporting.
/// </summary>
internal static class HostToHostCopyTests
{
    private static TestEnvironment _environment;

    public static void Run(TestEnvironment environment)
    {
        _environment = environment;
        Test.Section("Host-to-host directory copy");
        CopiesADirectoryBetweenTwoSessions();
        CopiesHiddenFilesAndOddNames();
        ReportsAFailedSource();
        LocalNameChecks();
    }

    // "tar -cf - *" left out the files that start with a dot, failed in an empty folder, and read a file named like an option of tar
    // ("--checkpoint-action=exec=...") as an option, which runs on the server.
    private static void CopiesHiddenFilesAndOddNames()
    {
        string source = _environment.RemoteFile("smartty-h2h-hidden-source");
        string target = _environment.RemoteFile("smartty-h2h-hidden-target");
        string marker = _environment.RemoteFile("smartty-h2h-injected");
        using SshSession sourceSession = Connect();
        using SshSession targetSession = Connect();
        string[] cleanup = { source, target, marker };
        string cleanupCommand = "rm -rf " + string.Join(" ", cleanup.Select(RemotePathUtils.ShellQuote));
        sourceSession.RunCommandChecked(cleanupCommand, requireSuccess: false);
        sourceSession.RunCommandChecked("mkdir -p " + RemotePathUtils.ShellQuote(source + "/.config"), requireSuccess: true);
        sourceSession.RunCommandChecked("printf 'secret' > " + RemotePathUtils.ShellQuote(source + "/.config/.token"), requireSuccess: true);
        sourceSession.RunCommandChecked("printf 'visible' > " + RemotePathUtils.ShellQuote(source + "/visible.txt"), requireSuccess: true);
        sourceSession.RunCommandChecked("printf 'touch " + marker + "' > " + RemotePathUtils.ShellQuote(source + "/x.sh"), requireSuccess: true);
        sourceSession.RunCommandChecked("cd " + RemotePathUtils.ShellQuote(source) + " && printf 'a' > ./--checkpoint=1 && printf 'b' > './--checkpoint-action=exec=sh x.sh' && printf 'c' > ./-v", requireSuccess: true);
        DirectoryCopier.CopyBetweenHosts(sourceSession, source, targetSession, target, null);
        Test.Equal("secret", ReadRemoteFile(targetSession, target + "/.config/.token"), "a hidden folder with a hidden file arrives");
        Test.Equal("visible", ReadRemoteFile(targetSession, target + "/visible.txt"), "an ordinary file arrives next to them");
        Test.Equal("c", ReadRemoteFile(targetSession, target + "/-v"), "a file named like an option arrives as a file");
        Test.Equal("b", ReadRemoteFile(targetSession, target + "/--checkpoint-action=exec=sh x.sh"), "...so does the one that would be an action of tar");
        Test.Check(targetSession.RunCommandChecked("test -e " + RemotePathUtils.ShellQuote(marker), requireSuccess: true) == null, "nothing was run on the server because of a file name");

        // An empty folder copies without an error.
        string empty = _environment.RemoteFile("smartty-h2h-empty-source");
        sourceSession.RunCommandChecked("rm -rf " + RemotePathUtils.ShellQuote(empty) + " && mkdir -p " + RemotePathUtils.ShellQuote(empty), requireSuccess: true);
        DirectoryCopier.CopyBetweenHosts(sourceSession, empty, targetSession, target + "/from-empty", null);
        Test.Check(targetSession.RunCommandChecked("test -d " + RemotePathUtils.ShellQuote(target + "/from-empty"), requireSuccess: true) != null, "an empty folder is copied without a failure");
        sourceSession.RunCommandChecked(cleanupCommand + " " + RemotePathUtils.ShellQuote(empty), requireSuccess: false);
    }

    private static void LocalNameChecks()
    {
        // Names from the listing of the server in the mode without tar: legal on Linux, a way out of the folder here.
        foreach (string bad in new[] { "..\\..\\x.dll", "C:\\Users\\x", "a\\b", "a/b", "..", ".", "name:stream", "" })
        {
            Test.Check(!DirectoryCopier.IsSafeLocalName(bad), "a local name is refused: '" + bad + "'");
        }
        foreach (string good in new[] { "readme.txt", ".hidden", "it's a file (1).txt", "Привет.txt", "a..b" })
        {
            Test.Check(DirectoryCopier.IsSafeLocalName(good), "a local name is fine: '" + good + "'");
        }
        using System.IO.MemoryStream script = new System.IO.MemoryStream(System.Text.Encoding.ASCII.GetBytes("#!/bin/sh\necho hi\n"));
        using System.IO.MemoryStream elf = new System.IO.MemoryStream(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1 });
        using System.IO.MemoryStream text = new System.IO.MemoryStream(System.Text.Encoding.ASCII.GetBytes("just text"));
        Test.Check(DirectoryCopier.LooksExecutable(script, "run") && script.Position == 0, "a file that starts with #! is executable (and read from the start again)");
        Test.Check(DirectoryCopier.LooksExecutable(elf, "tool"), "an ELF program is executable");
        Test.Check(!DirectoryCopier.LooksExecutable(text, "notes.txt"), "a text file is not");
        Test.Check(DirectoryCopier.LooksExecutable(text, "backup.SH"), "a script by its extension is executable, whatever the case");
        string create = DirectoryCopier.TarCommands.GetCreateCommand("/srv/a b", false);
        Test.Check(create.EndsWith(" -cf - ."), "the whole folder is archived, not the names in it (" + create + ")");
        Test.Check(DirectoryCopier.TarCommands.GetExtractCommand("/x", false).EndsWith("tar -xof -") && DirectoryCopier.TarCommands.GetExtractCommand("/x", true).EndsWith("tar -xmof -"),
            "the files are unpacked as the user's own (-o)");
        Test.Check(DirectoryCopier.TarCommands.GetCreateCommand("/x", false, excludeItems: new[] { "build", "*.log" }).Contains("--exclude='./build'"), "a plain exclusion also matches the ./ name");
    }

    private static SshSession Connect()
    {
        SshSession session = new SshSession(_environment.Parameters());
        if (!session.Authenticate(_environment.User, _environment.Password))
        {
            session.Dispose();
            throw new InvalidOperationException("Could not authenticate against the test server.");
        }
        return session;
    }

    private static void CopiesADirectoryBetweenTwoSessions()
    {
        string source = _environment.RemoteFile("smartty-h2h-source");
        string target = _environment.RemoteFile("smartty-h2h-target");
        using SshSession sourceSession = Connect();
        using SshSession targetSession = Connect();

        sourceSession.RunCommandChecked("rm -rf " + RemotePathUtils.ShellQuote(source) + " " + RemotePathUtils.ShellQuote(target), requireSuccess: false);
        sourceSession.RunCommandChecked("mkdir -p " + RemotePathUtils.ShellQuote(source + "/sub"), requireSuccess: true);
        sourceSession.RunCommandChecked("printf 'hello' > " + RemotePathUtils.ShellQuote(source + "/a.txt"), requireSuccess: true);
        sourceSession.RunCommandChecked("printf 'world' > " + RemotePathUtils.ShellQuote(source + "/sub/b.txt"), requireSuccess: true);

        List<string> log = new List<string>();
        long transferred = DirectoryCopier.CopyBetweenHosts(sourceSession, source, targetSession, target, (object sender, string line) => log.Add(line));

        Test.Check(transferred > 0L, "the copy reports the number of bytes streamed between the sessions");
        Test.Equal("hello", ReadRemoteFile(targetSession, target + "/a.txt"), "the file at the top of the source directory arrives");
        Test.Equal("world", ReadRemoteFile(targetSession, target + "/sub/b.txt"), "a file in a subdirectory arrives");

        // The target directory is created when it is missing.
        string nested = _environment.RemoteFile("smartty-h2h-deep/one/two");
        targetSession.RunCommandChecked("rm -rf " + RemotePathUtils.ShellQuote(_environment.RemoteFile("smartty-h2h-deep")), requireSuccess: false);
        DirectoryCopier.CopyBetweenHosts(sourceSession, source, targetSession, nested, null);
        Test.Equal("hello", ReadRemoteFile(targetSession, nested + "/a.txt"), "a missing target directory is created first");

        sourceSession.RunCommandChecked("rm -rf " + RemotePathUtils.ShellQuote(source) + " " + RemotePathUtils.ShellQuote(target) + " " + RemotePathUtils.ShellQuote(_environment.RemoteFile("smartty-h2h-deep")), requireSuccess: false);
    }

    private static void ReportsAFailedSource()
    {
        using SshSession sourceSession = Connect();
        using SshSession targetSession = Connect();
        string missing = _environment.RemoteFile("smartty-h2h-missing-" + Guid.NewGuid().ToString("N"));
        Test.Throws<Exception>(
            () => DirectoryCopier.CopyBetweenHosts(sourceSession, missing, targetSession, _environment.RemoteFile("smartty-h2h-target"), null),
            "a missing source directory fails the copy instead of reporting success");
    }

    private static string ReadRemoteFile(SshSession session, string path)
    {
        using ScpDownloadStream download = session.OpenDownload(path);
        byte[] buffer = new byte[download.Length];
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = download.Read(buffer, read, buffer.Length - read);
            if (chunk <= 0)
            {
                break;
            }
            read += chunk;
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }
}
