using System;
using System.Collections.Generic;
using System.IO;
using SmarTTY.CodeEditing;

// Checks of CodeEditing/RemoteFileServer.cs, the server side of the remote file editor (what the file is on the server: whether a path
// exists, whether it can be written, how it is read, and the version that tells whether somebody else changed it), with a fake connection.
// The same code is run against real OpenSSH and Dropbear servers by tests/RemoteFileEditorIntegrationTests.
// Run with: dotnet run --project tests/RemoteFileServerTests
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
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            Check(true, name);
            return;
        }
        Check(false, name + " (nothing was thrown)");
    }

    // The connection: answers of commands by their text, a connection that can be lost, and a log of the calls.
    private sealed class FakeHost : IRemoteFileHost
    {
        public bool IsWindows { get; set; }

        public readonly Dictionary<string, List<string>> Answers = new Dictionary<string, List<string>>();

        public bool ConnectionLost;

        public readonly List<string> Calls = new List<string>();

        public bool SftpExistsAnswer;

        public bool? SftpCanWriteAnswer;

        public string SftpSignatureAnswer;

        public Exception SftpSignatureFailure;

        public List<string> RunCommandChecked(string command)
        {
            Calls.Add("run " + command);
            return Answers.TryGetValue(command, out List<string> output) ? output : null;
        }

        public void ThrowIfConnectionLost()
        {
            Calls.Add("check connection");
            if (ConnectionLost)
            {
                throw new IOException("The connection to the server was lost.");
            }
        }

        public Stream OpenScpDownload(string path, out long length)
        {
            Calls.Add("scp " + path);
            length = 7;
            return new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7 });
        }

        public Stream OpenSftpRead(string path, out long length)
        {
            Calls.Add("sftp read " + path);
            length = 3;
            return new MemoryStream(new byte[] { 1, 2, 3 });
        }

        public bool SftpExists(string path)
        {
            Calls.Add("sftp exists " + path);
            return SftpExistsAnswer;
        }

        public bool? SftpCanWrite(string path)
        {
            Calls.Add("sftp can write " + path);
            return SftpCanWriteAnswer;
        }

        public string SftpSignature(string path)
        {
            Calls.Add("sftp signature " + path);
            if (SftpSignatureFailure != null)
            {
                throw SftpSignatureFailure;
            }
            return SftpSignatureAnswer;
        }
    }

    private static string Joined(FakeHost host)
    {
        return string.Join("|", host.Calls);
    }

    private static void ExistsOnUnix()
    {
        FakeHost host = new FakeHost();
        RemoteFileServer server = new RemoteFileServer(host, "/etc/a b.conf");
        host.Answers["test -e '/etc/a b.conf'"] = new List<string>();
        Check(server.Exists("/etc/a b.conf"), "a path the test finds exists");
        Equal(Joined(host), "run test -e '/etc/a b.conf'", "the test runs with the path quoted, and nothing else is asked");

        host.Calls.Clear();
        Check(!server.Exists("/missing"), "a path the test does not find does not exist");
        Equal(Joined(host), "run test -e '/missing'|check connection", "the connection is checked before the answer no");

        host.Calls.Clear();
        host.ConnectionLost = true;
        Throws<IOException>(() => server.Exists("/missing"), "a lost connection is not 'does not exist'");

        host.Calls.Clear();
        host.ConnectionLost = false;
        host.Answers["test -e 'it'\\''s'"] = new List<string>();
        Check(server.Exists("it's"), "a path with a quote is quoted for the shell");
    }

    private static void ExistsOnWindows()
    {
        FakeHost host = new FakeHost { IsWindows = true, SftpExistsAnswer = true };
        RemoteFileServer server = new RemoteFileServer(host, "C:/x");
        Check(server.Exists("C:/dir"), "a Windows host answers over SFTP");
        Equal(Joined(host), "sftp exists C:/dir", "no command is run on a Windows host");
        host.SftpExistsAnswer = false;
        Check(!server.Exists("C:/dir"), "and the answer no is final: the connection is not checked");
        Check(!host.Calls.Contains("check connection"), "no connection check on Windows");
    }

    private static void Writable()
    {
        FakeHost host = new FakeHost();
        RemoteFileServer server = new RemoteFileServer(host, "/x");
        host.Answers["test -w '/etc'"] = new List<string>();
        Check(server.IsWritable("/etc"), "a path the test finds writable is writable");
        Equal(Joined(host), "run test -w '/etc'", "only the test is run");
        host.Calls.Clear();
        Check(!server.IsWritable("/root"), "a path the test refuses is not writable");
        Equal(Joined(host), "run test -w '/root'|check connection", "the connection is checked before the answer no");
        host.ConnectionLost = true;
        Throws<IOException>(() => server.IsWritable("/root"), "a lost connection is not 'not writable'");

        FakeHost windows = new FakeHost { IsWindows = true };
        RemoteFileServer windowsServer = new RemoteFileServer(windows, "C:/x");
        windows.SftpCanWriteAnswer = true;
        Check(windowsServer.IsWritable("C:/x"), "Windows: writable");
        windows.SftpCanWriteAnswer = false;
        Check(!windowsServer.IsWritable("C:/x"), "Windows: not writable");
        windows.SftpCanWriteAnswer = null;
        Check(windowsServer.IsWritable("C:/x"), "Windows: a probe with no answer counts as writable");
        Check(!windows.Calls.Exists(c => c.StartsWith("run ")), "Windows: no command is run");
    }

    private static void Reading()
    {
        FakeHost unix = new FakeHost();
        using (Stream stream = new RemoteFileServer(unix, "/etc/passwd").OpenForReading(out long length))
        {
            Equal(length, 7L, "Unix: the length comes from SCP");
            Equal(stream.Length, 7L, "Unix: the stream is the SCP download");
        }
        Equal(Joined(unix), "scp /etc/passwd", "Unix: the file of the editor is downloaded over SCP");

        FakeHost windows = new FakeHost { IsWindows = true };
        using (Stream stream = new RemoteFileServer(windows, "C:/a.txt").OpenForReading(out long length))
        {
            Equal(length, 3L, "Windows: the length comes from SFTP");
            Equal(stream.Length, 3L, "Windows: the stream is the SFTP read");
        }
        Equal(Joined(windows), "sftp read C:/a.txt", "Windows: the file of the editor is read over SFTP");
    }

    private static void Signature()
    {
        FakeHost host = new FakeHost();
        RemoteFileServer server = new RemoteFileServer(host, "/etc/a b");
        string command = "stat -c '%y %s' '/etc/a b' 2>/dev/null || md5sum '/etc/a b'";
        host.Answers[command] = new List<string> { "2026-01-02 03:04:05.123456789 +0000 42" };
        Equal(server.FetchSignature(), "2026-01-02 03:04:05.123456789 +0000 42", "Unix: the signature is the output of the command");
        Equal(Joined(host), "run " + command, "Unix: the command is the one of RemoteFileSignature");

        host.Answers[command] = new List<string> { "  ", "x", "" };
        Equal(server.FetchSignature(), "x", "Unix: the output lines are joined and trimmed");

        host.Answers.Clear();
        Equal(server.FetchSignature(), null, "Unix: a failed command gives no signature");

        host.Answers[command] = new List<string>();
        Equal(server.FetchSignature(), null, "Unix: no output gives no signature");

        FakeHost windows = new FakeHost { IsWindows = true, SftpSignatureAnswer = "1700000000 12" };
        RemoteFileServer windowsServer = new RemoteFileServer(windows, "C:/a.txt");
        Equal(windowsServer.FetchSignature(), "1700000000 12", "Windows: the signature comes over SFTP");
        Equal(Joined(windows), "sftp signature C:/a.txt", "Windows: for the path of the editor");
        windows.SftpSignatureFailure = new IOException("gone");
        Equal(windowsServer.FetchSignature(), null, "Windows: a failure is no signature, not an exception");

        FakeHost lost = new FakeHost();
        lost.Answers[command] = null;
        lost.ConnectionLost = true;
        Equal(new RemoteFileServer(lost, "/etc/a b").FetchSignature(), null, "a lost connection is no signature");
    }

    private static void Changes()
    {
        FakeHost host = new FakeHost();
        RemoteFileServer server = new RemoteFileServer(host, "/f");
        string command = RemoteFileSignatureCommand("/f");

        // The signature of the server is unknown: nothing can be said, so it is not a conflict.
        Check(!server.HasChanged("a", isNewFile: false), "no signature of the server: no conflict");
        Check(!server.HasChanged(null, isNewFile: true), "no signature of the server and a new file: no conflict");

        host.Answers[command] = new List<string> { "b" };
        Check(server.HasChanged("a", isNewFile: false), "another signature: changed");
        Check(!server.HasChanged("b", isNewFile: false), "the same signature: not changed");
        Check(!server.HasChanged(null, isNewFile: false), "no signature of the opened file: nothing can be said");
        Check(server.HasChanged(null, isNewFile: true), "a file now there that was new: changed");
        Check(server.HasChanged("b", isNewFile: true), "a new file has no signature to compare: changed");
    }

    private static string RemoteFileSignatureCommand(string path)
    {
        return "stat -c '%y %s' '" + path + "' 2>/dev/null || md5sum '" + path + "'";
    }

    private static void Descriptions()
    {
        RemoteFileServer server = new RemoteFileServer(new FakeHost(), "/etc/x.conf");
        Equal(server.ConflictDescription(isNewFile: false), "/etc/x.conf has been changed on the server since it was opened in the editor.", "the text for a changed file");
        Equal(server.ConflictDescription(isNewFile: true), "/etc/x.conf has been created on the server since you started this new file.", "the text for a new file");
    }

    private static int Main()
    {
        try
        {
            ExistsOnUnix();
            ExistsOnWindows();
            Writable();
            Reading();
            Signature();
            Changes();
            Descriptions();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " remote file server checks passed.");
        return 0;
    }
}
