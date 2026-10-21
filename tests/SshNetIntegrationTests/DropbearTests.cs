using System;
using System.Collections.Generic;
using System.Threading;
using SmarTTY.IO;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Checks against Dropbear with a BusyBox ash login shell, the SSH server and shell of OpenWRT, provisioned by
/// setup-dropbear.sh: PTY commands started together with helper commands, the way a Smart Terminal command starts
/// together with the history write. Dropbear drops the request of a PTY channel when a helper without a PTY exits
/// while it is handled (see Ssh/PtySetupGate.cs); the race is too timing dependent to be caught reliably here, so the
/// gate itself is checked by PtySetupGateTests and this is a compatibility check of the whole path on Dropbear.
/// </summary>
internal static class DropbearTests
{
    public static bool Enabled()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMARTTY_DROPBEAR_TEST_PORT")))
        {
            return true;
        }
        if (Environment.GetEnvironmentVariable("SMARTTY_DROPBEAR_TEST_REQUIRED") == "1")
        {
            throw new InvalidOperationException("SMARTTY_DROPBEAR_TEST_REQUIRED=1 but no Dropbear server was provisioned. Run tests/SshNetIntegrationTests/setup-dropbear.sh first.");
        }
        return false;
    }

    public static void Run()
    {
        string host = Value("SMARTTY_DROPBEAR_TEST_HOST", "127.0.0.1");
        int port = int.Parse(Value("SMARTTY_DROPBEAR_TEST_PORT", "2223"));
        string user = Value("SMARTTY_DROPBEAR_TEST_USER", "smartty-ash");
        string password = Value("SMARTTY_DROPBEAR_TEST_PASSWORD", "smartty-ash-password");
        Console.WriteLine();
        Console.WriteLine("Using the Dropbear server at " + host + ":" + port + " as " + user);

        Test.Section("Dropbear: helper commands do not overlap with setting up a PTY channel");
        SshConnectionParameters parameters = new SshConnectionParameters
        {
            Host = host,
            DisplayHostName = host,
            Port = port,
            UserName = user,
            // A dropped request is only noticed through the session timeout; a short one keeps the check fast.
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        using SshSession session = new SshSession(parameters);
        Test.Check(session.Authenticate(user, password), "password authentication against Dropbear");
        // The executable of the shell that runs the command (the command substitution keeps it alive meanwhile).
        string shellExecutable = session.RunCommandChecked("echo $(readlink /proc/$$/exe)", requireSuccess: true)?.Find((string line) => line.Trim() != "")?.Trim() ?? "";
        Test.Check(shellExecutable.EndsWith("/busybox"), "the login shell is BusyBox ash (" + shellExecutable + ")");
        // The helper exits at slightly different moments, so that some of its exits would fall into the PTY setup.
        const int iterations = 40;
        int failures = 0;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            string helper = ((iteration % 4 == 0) ? "" : "sleep 0.00" + (iteration % 10) + "; ") + "printf x >> smartty-dropbear-race";
            Thread helperThread = new Thread(() => session.RunCommand(helper, out _));
            helperThread.Start();
            try
            {
                using SshShellChannel channel = session.OpenShellChannel("echo pty-command-ran", "xterm", ExtendedDataMode.Merge);
                string output = new System.IO.StreamReader(channel.Output).ReadToEnd();
                channel.WaitClosed();
                if (!output.Contains("pty-command-ran"))
                {
                    failures++;
                    Console.WriteLine("  iteration " + iteration + ": the command did not run: '" + output.Trim() + "'");
                }
            }
            catch (Exception exception)
            {
                failures++;
                Console.WriteLine("  iteration " + iteration + ": " + exception.Message);
            }
            helperThread.Join();
        }
        Test.Equal(0, failures, "a PTY command started together with a helper command always starts (" + iterations + " tries)");
        Test.Equal(iterations.ToString(), session.RunCommandChecked("wc -c < smartty-dropbear-race", requireSuccess: true)?.Find((string line) => line.Trim() != "")?.Trim(), "every helper command ran");
        session.RunCommand("rm -f smartty-dropbear-race", out _);
        FileOperations(session);
    }

    // The commands of the file list's Copy/Cut/Paste/Rename/Delete/New, run by BusyBox ash and its applets.
    private static void FileOperations(SshSession session)
    {
        Test.Section("Dropbear: file list operations in BusyBox ash");
        string home = session.RunCommandChecked("pwd", requireSuccess: true)?.Find((string line) => line.StartsWith("/"));
        string root = RemoteFileOperations.Join(home, "smartty file ops");
        session.RunCommand("rm -rf " + RemotePathUtils.ShellQuote(root), out _);
        Test.Equal(0, Run(session, RemoteFileOperations.CreateDirectoryCommand(root)), "create a folder");
        string file = RemoteFileOperations.Join(root, "it's a $file.txt");
        Test.Equal(0, Run(session, RemoteFileOperations.CreateFileCommand(file)), "create an empty file");
        Test.Check(Run(session, RemoteFileOperations.CreateFileCommand(file)) != 0, "creating an existing file fails");
        string folder = RemoteFileOperations.Join(root, "sub dir");
        Test.Equal(0, Run(session, RemoteFileOperations.CreateDirectoryCommand(folder)), "create a subfolder");
        Test.Equal(0, Run(session, RemoteFileOperations.CopyCommand(file, RemoteFileOperations.Join(folder, "copy.txt"), replace: false)), "copy a file into the subfolder");
        string folderCopy = RemoteFileOperations.Join(root, RemoteFileOperations.MakeCopyName("sub dir", true, new HashSet<string> { "sub dir" }));
        Test.Equal(0, Run(session, RemoteFileOperations.CopyCommand(folder, folderCopy, replace: false)), "copy a folder next to itself");
        Test.Equal(0, Run(session, "test -f " + RemotePathUtils.ShellQuote(RemoteFileOperations.Join(folderCopy, "copy.txt"))), "the folder copy has its file");
        Test.Check(Run(session, RemoteFileOperations.CopyCommand(folder, folderCopy, replace: false)) != 0, "copying onto an existing folder fails instead of nesting it");
        Test.Equal(0, Run(session, RemoteFileOperations.MoveCommand(file, RemoteFileOperations.Join(folderCopy, "moved.txt"), replace: false)), "move a file");
        Test.Check(Run(session, "test -e " + RemotePathUtils.ShellQuote(file)) != 0, "the moved file left its folder");
        Test.Equal(0, Run(session, RemoteFileOperations.DeleteCommand(new[] { folder, folderCopy })), "delete two folders");
        Test.Equal(0, Run(session, "test -z \"$(ls -A " + RemotePathUtils.ShellQuote(root) + ")\""), "the folder is empty afterwards");
        session.RunCommand("rm -rf " + RemotePathUtils.ShellQuote(root), out _);
    }

    private static int Run(SshSession session, string command)
    {
        session.RunCommand(command, out int? exitCode);
        return exitCode ?? -1;
    }

    private static string Value(string name, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
}
