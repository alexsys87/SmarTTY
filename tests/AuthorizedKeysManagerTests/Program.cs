using System;
using System.IO;
using System.Linq;
using System.Text;
using SmarTTY;
using SmarTTY.Ssh;

// Checks of the public key installation against a stand-in session: the append script must be waited for, its failure
// must be reported, and the key must be read back, so that a profile is never switched to a key that is not on the server.
// Run with: dotnet run --project tests/AuthorizedKeysManagerTests
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

    private static Exception Catch(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }
        return null;
    }

    private static byte[] Blob(int seed)
    {
        byte[] blob = new byte[4 + 11 + 32];
        blob[3] = 11;
        Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(blob, 4);
        for (int i = 0; i < 32; i++)
        {
            blob[15 + i] = (byte)(seed * 31 + i);
        }
        return blob;
    }

    private static string KeyLine(byte[] blob)
    {
        return "ssh-ed25519 " + Convert.ToBase64String(blob) + " me@host";
    }

    private static int Main()
    {
        byte[] key = Blob(1);
        byte[] other = Blob(2);
        string line = KeyLine(key);
        const string home = "/root/.ssh/authorized_keys";
        const string dropbear = "/etc/dropbear/authorized_keys";

        // A normal installation stores the key and verifies it.
        SshSession session = new SshSession();
        AuthorizedKeysManager.AppendAuthorizedKey(session, line);
        Check(session.Files[home].Trim() == line, "install: the key is stored in authorized_keys");
        Check(AuthorizedKeysManager.IsKeyAuthorized(session, key), "install: the key is reported as authorized");

        // The other keys of the file stay, and a key already there is not added twice.
        session = new SshSession();
        session.Files[home] = KeyLine(other);
        AuthorizedKeysManager.AppendAuthorizedKey(session, line);
        Check(session.Files[home] == KeyLine(other) + "\n" + line + "\n", "install: another key stays, the new one follows");
        session.AppendedPaths.Clear();
        AuthorizedKeysManager.AppendAuthorizedKey(session, line);
        Check(session.AppendedPaths.Count == 0, "install: a key that is already there is not appended again");

        // Dropbear root: both files get the key.
        session = new SshSession { IsDropbearRoot = true };
        AuthorizedKeysManager.AppendAuthorizedKey(session, line);
        Check(session.Files[home].Contains(line) && session.Files[dropbear].Contains(line), "dropbear: the key is in the home file and in /etc/dropbear");

        // Full flash (the overlay of OpenWrt): the error is reported, with the reason from the server.
        session = new SshSession { AppendOutcome = AppendOutcome.DiskFull };
        Exception error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, line));
        Check(error != null && error.Message.Contains("No space left on device"), "full disk: installation fails with the message of the server");
        Check(!AuthorizedKeysManager.IsKeyAuthorized(session, key), "full disk: the key is not reported as authorized");

        // The server closes the channel without an exit status and without storing the key: only the read-back sees it.
        session = new SshSession { AppendOutcome = AppendOutcome.ClosedSilently };
        error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, line));
        Check(error is RemoteOperationException && error.Message.Contains("not found"), "closed channel: the missing key is found by the read-back");

        // No exit status reported, but the key is there: the read-back accepts it.
        session = new SshSession { AppendOutcome = AppendOutcome.StoredWithoutStatus };
        AuthorizedKeysManager.AppendAuthorizedKey(session, line);
        Check(AuthorizedKeysManager.IsKeyAuthorized(session, key), "no exit status: a stored key is accepted after the read-back");

        // The connection goes away during the script: a connection error, not a configuration error.
        session = new SshSession { AppendOutcome = AppendOutcome.ConnectionLost };
        error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, line));
        Check(error is IOException && session.IsConnectionClosed, "lost connection: reported as a lost connection");

        // The script never ends: the wait is bounded and the error says so.
        session = new SshSession { AppendOutcome = AppendOutcome.Hangs };
        error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, line));
        Check(error is RemoteOperationException && error.Message.Contains("Timed out"), "hanging script: timeout is reported");

        // A lost connection is not "no home directory" / "no .ssh directory" (the old messages).
        session = new SshSession { IsConnectionClosed = true };
        error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, line));
        Check(error is IOException && error.Message.Contains("lost"), "install: lost connection before pwd is reported as such");
        error = Catch(() => AuthorizedKeysManager.RemoveAuthorizedKey(session, key));
        Check(error is IOException && error.Message.Contains("lost"), "remove: lost connection before pwd is reported as such");
        error = Catch(() => AuthorizedKeysManager.IsKeyAuthorized(session, key));
        Check(error is IOException, "check: a lost connection is thrown, not answered with 'not authorized'");
        session = new SshSession { FailMkdir = true };
        error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, line));
        Check(error != null && error.Message.Contains(".ssh directory"), "install: a refused .ssh directory is reported");

        // A missing file is simply "not authorized", with a working connection.
        session = new SshSession();
        Check(!AuthorizedKeysManager.IsKeyAuthorized(session, key), "check: no authorized_keys file means not authorized");

        // chmod may fail (no modes on the file system): the key is there, so the installation stands.
        session = new SshSession { FailChmod = true };
        AuthorizedKeysManager.AppendAuthorizedKey(session, line);
        Check(session.Files[home].Contains(line), "install: a failing chmod does not undo a stored key");

        // Not a key at all: nothing is written.
        session = new SshSession();
        error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, "not a key"));
        Check(error is ArgumentException && session.Files.Count == 0, "install: a line that is not a key is refused");

        // Out of space in the middle of the line: the user is asked, and the incomplete line goes only if they agree.
        const string shortLine = "ssh-ed25519 AAAA other@host";
        string frag = line.Substring(0, 25);
        foreach (string[] shell in new[] { new[] { "sh" }, new[] { "/usr/bin/busybox", "sh" } })
        {
            if (shell[0] != "sh" && !File.Exists(shell[0]))
            {
                Console.WriteLine("SKIP: busybox is not installed");
                continue;
            }
            string label = string.Join(" ", shell);
            session = new SshSession { AppendOutcome = AppendOutcome.DiskFullPartial, Shell = shell };
            session.Files[home] = shortLine + "\n";
            string question = null;
            error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, line, message => { question = message; return true; }));
            Check(question != null && question.Contains(home) && question.Contains(frag.Substring(0, 20)), "partial line (" + label + "): the user is asked, with the file and the line");
            Check(session.Files[home] == shortLine + "\n", "partial line (" + label + "): removed after the user agreed, the other key is intact");
            Check(error.Message.Contains("No space left on device") && error.Message.Contains("was removed"), "partial line (" + label + "): the error keeps the cause and says what was removed");
        }

        session = new SshSession { AppendOutcome = AppendOutcome.DiskFullPartial };
        session.Files[home] = shortLine + "\n";
        error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, line, message => false));
        Check(session.Files[home] == shortLine + "\n" + frag && !error.Message.Contains("removed"), "partial line: nothing is removed when the user says no");

        session = new SshSession { AppendOutcome = AppendOutcome.DiskFullPartial };
        session.Files[home] = shortLine + "\n";
        error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, line));
        Check(session.Files[home] == shortLine + "\n" + frag, "partial line: without a question handler nothing is removed");

        session = new SshSession { AppendOutcome = AppendOutcome.DiskFull };
        session.Files[home] = shortLine + "\n";
        bool asked = false;
        error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, line, message => { asked = true; return true; }));
        Check(!asked && error != null, "no partial line: the user is not asked");

        session = new SshSession { AppendOutcome = AppendOutcome.ConnectionLost };
        asked = false;
        error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, line, message => { asked = true; return true; }));
        Check(!asked && error is IOException, "lost connection: the user is not asked, the loss is reported");

        // The file changed after the question: it is left alone and the message says so.
        session = new SshSession { AppendOutcome = AppendOutcome.DiskFullPartial };
        session.Files[home] = shortLine + "\n";
        error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, line, message => { session.Files[home] += "\n"; return true; }));
        Check(session.Files[home] == shortLine + "\n" + frag + "\n" && error.Message.Contains("could not be removed"), "partial line: a file that changed meanwhile is not touched");

        // Dropbear root: the home file got the key, the space ran out in the Dropbear file: only that line is offered.
        session = new SshSession { IsDropbearRoot = true, AppendOutcome = AppendOutcome.DiskFullPartial, OutcomeOnlyFor = "/etc/dropbear" };
        session.Files[dropbear] = shortLine + "\n";
        int questions = 0;
        error = Catch(() => AuthorizedKeysManager.AppendAuthorizedKey(session, line, message => { questions++; return true; }));
        Check(questions == 1 && session.Files[dropbear] == shortLine + "\n" && session.Files[home].Contains(line), "dropbear: only the incomplete line of /etc/dropbear is removed, the home file keeps the key");

        // Removal of the key. No scp program (OpenWrt): the file is read with cat and written back by a remote script.
        string third = KeyLine(Blob(3));
        string both = KeyLine(other) + "\n" + line + "\n" + third + "\n";
        session = new SshSession();
        session.Files[home] = both;
        AuthorizedKeysManager.RemoveAuthorizedKey(session, key);
        Check(session.Files[home] == KeyLine(other) + "\n" + third + "\n", "remove without scp: only the lines of the key go, the others stay");
        Check(session.ReplacedPaths.SequenceEqual(new[] { home }) && session.ScpUploads.Count == 0, "remove without scp: the file is replaced by the remote script, not by scp");
        Check(!session.Files.Keys.Any(path => path.EndsWith(".smartty")), "remove without scp: no temporary file is left");
        Check(!AuthorizedKeysManager.IsKeyAuthorized(session, key), "remove without scp: the key is no longer authorized");

        session = new SshSession();
        session.Files[home] = line + "\n";
        AuthorizedKeysManager.RemoveAuthorizedKey(session, key);
        Check(session.Files[home] == "", "remove without scp: the only key leaves an empty file");

        session = new SshSession();
        session.Files[home] = KeyLine(other) + "\n";
        AuthorizedKeysManager.RemoveAuthorizedKey(session, key);
        Check(session.Files[home] == KeyLine(other) + "\n" && session.ReplacedPaths.Count == 0, "remove without scp: a file without the key is not rewritten");

        session = new SshSession();
        AuthorizedKeysManager.RemoveAuthorizedKey(session, key);
        Check(session.Files.Count == 0, "remove: no .ssh directory, nothing to do");
        session = new SshSession();
        session.Files["/root/.ssh/known_hosts"] = "host key\n";
        AuthorizedKeysManager.RemoveAuthorizedKey(session, key);
        Check(session.Files.Count == 1 && session.ReplacedPaths.Count == 0, "remove: no authorized_keys file, nothing to do");

        // With scp the file still goes through scp.
        session = new SshSession { NoScp = false };
        session.Files[home] = both;
        AuthorizedKeysManager.RemoveAuthorizedKey(session, key);
        Check(session.Files[home] == KeyLine(other) + "\n" + third + "\n" && session.ScpUploads.SequenceEqual(new[] { home + ".smartty" }) && session.ReplacedPaths.Count == 0, "remove with scp: the file goes through scp");

        // Dropbear root without scp: the file of root is the one the server reads.
        session = new SshSession { IsDropbearRoot = true };
        session.Files[home] = both;
        session.Files[dropbear] = both;
        AuthorizedKeysManager.RemoveAuthorizedKey(session, key);
        Check(!session.Files[home].Contains(line) && !session.Files[dropbear].Contains(line) && session.Files[dropbear].Contains(third), "dropbear remove without scp: the key goes from both files, the others stay");

        // A failure in the Dropbear file is reported (the key is still accepted there), after the home file was cleaned too.
        session = new SshSession { IsDropbearRoot = true, AppendOutcome = AppendOutcome.DiskFull, OutcomeOnlyFor = "/etc/dropbear" };
        session.Files[home] = both;
        session.Files[dropbear] = both;
        error = Catch(() => AuthorizedKeysManager.RemoveAuthorizedKey(session, key));
        Check(error != null && error.Message.Contains(dropbear) && error.Message.Contains("No space left on device"), "dropbear remove: a failure in /etc/dropbear is reported with its reason");
        Check(session.Files[dropbear] == both && !session.Files[home].Contains(line), "dropbear remove: the failed file is intact, the home file is cleaned");

        // Full flash: the original file stays as it was and the error says why.
        session = new SshSession { AppendOutcome = AppendOutcome.DiskFull };
        session.Files[home] = both;
        error = Catch(() => AuthorizedKeysManager.RemoveAuthorizedKey(session, key));
        Check(error != null && error.Message.Contains("No space left on device") && session.Files[home] == both, "remove: full disk is reported and the file is intact");

        // A file that cannot be read is an error, not "nothing to remove".
        session = new SshSession();
        session.Files[home] = both;
        session.Unreadable.Add(home);
        error = Catch(() => AuthorizedKeysManager.RemoveAuthorizedKey(session, key));
        Check(error != null && error.Message.Contains("Cannot read") && session.Files[home] == both, "remove: an unreadable file is reported");

        // The channel closes without an exit status: only the read-back tells.
        session = new SshSession { AppendOutcome = AppendOutcome.ClosedSilently };
        session.Files[home] = both;
        error = Catch(() => AuthorizedKeysManager.RemoveAuthorizedKey(session, key));
        Check(error is RemoteOperationException && error.Message.Contains("still"), "remove: a key that is still there after a silent close is an error");
        session = new SshSession { AppendOutcome = AppendOutcome.StoredWithoutStatus };
        session.Files[home] = both;
        AuthorizedKeysManager.RemoveAuthorizedKey(session, key);
        Check(!session.Files[home].Contains(line), "remove: a removal without exit status is accepted after the read-back");

        session = new SshSession { AppendOutcome = AppendOutcome.ConnectionLost };
        session.Files[home] = both;
        error = Catch(() => AuthorizedKeysManager.RemoveAuthorizedKey(session, key));
        Check(error is IOException && session.IsConnectionClosed && session.Files[home] == both, "remove: a lost connection is reported as such");

        session = new SshSession { AppendOutcome = AppendOutcome.Hangs };
        session.Files[home] = both;
        error = Catch(() => AuthorizedKeysManager.RemoveAuthorizedKey(session, key));
        Check(error is RemoteOperationException && error.Message.Contains("Timed out") && session.Files[home] == both, "remove: a hanging script is reported");

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
