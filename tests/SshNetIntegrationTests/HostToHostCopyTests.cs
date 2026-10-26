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
        ReportsAFailedSource();
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
