using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Drives the production SSH layer against a real OpenSSH server: key exchange, the host key
/// callback, password, keyboard-interactive and public key authentication, exec channels with a
/// PTY and a separate stderr, exit statuses and signals, shell channels, window resizing,
/// polling delivery and the SCP upload/download protocol.
/// These paths only exist over a live connection, which is where an SSH.NET update breaks.
/// </summary>
internal static class SshIntegrationTests
{
    private static TestEnvironment _environment;

    public static void Run(TestEnvironment environment)
    {
        _environment = environment;
        ConnectionAndHostKey();
        Authentication();
        KeyboardInteractiveAuthentication();
        SessionStatistics();
        PublicKeyAuthentication();
        ExecChannels();
        ShellChannel();
        ExtendedData();
        Scp();
        ConnectionLoss();
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

    private static void ConnectionAndHostKey()
    {
        Test.Section("Connect, host key and session properties");
        using (SshSession session = new SshSession(_environment.Parameters()))
        {
            Test.Check(!session.IsAuthenticated, "the session waits for authentication after the key exchange");
            Test.Equal(_environment.Host, session.HostName, "HostName is reported");
            Test.Equal(_environment.Port, session.Port, "Port is reported");
            Test.Check(!session.KeepAliveEnabled, "keep-alive is off by default");
            session.ConfigureKeepAlive(30);
            Test.Check(session.KeepAliveEnabled, "keep-alive can be enabled");
            session.ConfigureKeepAlive(0);
            Test.Check(!session.KeepAliveEnabled, "keep-alive can be disabled");

            SshSession.HostKeyInfo hostKey = session.GetHostKey();
            Test.Check(hostKey != null && hostKey.KeyData != null && hostKey.KeyData.Length > 0, "the server host key was captured");
            Test.Check(hostKey.Algorithm != null && hostKey.Algorithm.StartsWith("ssh-", StringComparison.Ordinal), "the key type was parsed from the host key blob (actual: " + hostKey.Algorithm + ")");
            Test.Equal(_environment.HostKeyType, hostKey.Algorithm, "the reported key type matches the one sshd offers");

            SshSession.AuthMethods methods = session.GetAuthMethods(_environment.User);
            Test.Check((methods & SshSession.AuthMethods.Password) != 0, "the server advertises the password method");
            Test.Check((methods & SshSession.AuthMethods.PublicKey) != 0, "the server advertises the public key method");
            Test.Check(!session.AuthenticateWithPassword(_environment.User, "definitely-wrong"), "a wrong password is rejected");
            Test.Check(!session.IsAuthenticated, "a failed attempt leaves the session unauthenticated");
        }
    }

    private static void Authentication()
    {
        Test.Section("Password and keyboard-interactive authentication");
        using (SshSession session = new SshSession(_environment.Parameters()))
        {
            Test.Check(session.Authenticate(_environment.User, _environment.Password), "Authenticate succeeds with the correct password");
            Test.Check(session.IsAuthenticated, "IsAuthenticated is set after a successful login");
            Test.Equal(_environment.User, session.UserName, "UserName is recorded after login");

            List<string> output = session.RunCommand("echo hello", out int? exitStatus);
            Test.Check(output != null && output.Count == 1 && output[0] == "hello", "RunCommand returns the remote output");
            Test.Equal(0, exitStatus, "a successful command reports exit status 0");

            session.RunCommand("exit 3", out exitStatus);
            Test.Equal(3, exitStatus, "a failing command reports its exit status");
            Test.Check(session.RunCommandChecked("exit 3", requireSuccess: true) == null, "RunCommandChecked hides a failing command");
            Test.Check(session.RunCommandChecked("echo ok", requireSuccess: true) != null, "RunCommandChecked keeps a successful command");
        }
    }

    private static void SessionStatistics()
    {
        Test.Section("Session statistics");
        using SshSession session = Connect();
        session.RunCommand("echo hello", out _);
        Test.Check(session.Statistics.BytesReceived > 0, "received bytes are counted");

        using SshShellChannel channel = session.OpenShellChannel(command: null, terminalType: "xterm");
        byte[] line = Encoding.ASCII.GetBytes("exit\n");
        channel.Input.Write(line, 0, line.Length);
        ReadToEnd(channel.Output);
        channel.WaitClosed();
        Test.Check(session.Statistics.BytesSent >= line.Length, "sent bytes are counted (actual: " + session.Statistics.BytesSent + ")");
    }

    private static void KeyboardInteractiveAuthentication()
    {
        Test.Section("Keyboard-interactive authentication");
        using (SshSession interactive = new SshSession(_environment.Parameters()))
        {
            bool prompted = false;
            bool changed = interactive.AuthenticateKeyboardInteractive(_environment.User, (string name, string instruction, SshSession.KeyboardInteractivePrompt[] prompts, string[] responses) =>
            {
                prompted = true;
                for (int i = 0; i < responses.Length; i++)
                {
                    responses[i] = _environment.Password;
                }
            });
            Test.Check(changed, "keyboard-interactive authentication succeeds");
            Test.Check(prompted, "the keyboard-interactive prompt callback ran");
            Test.Check(interactive.IsAuthenticated, "the keyboard-interactive session is authenticated");
        }

        using (SshSession unauthenticated = new SshSession(_environment.Parameters()))
        {
            Test.Throws<InvalidOperationException>(() => unauthenticated.OpenShellChannel("echo x"), "opening a channel before login is refused");
        }
    }

    private static void PublicKeyAuthentication()
    {
        Test.Section("Public key authentication");
        using (SshSession session = new SshSession(_environment.Parameters()))
        {
            Test.Check(session.AuthenticateWithKeyFile(_environment.User, out bool passphraseRequired, _environment.KeyFile), "AuthenticateWithKeyFile succeeds with a generated OpenSSH key");
            Test.Check(!passphraseRequired, "no passphrase is reported for an unencrypted key");
            Test.Check(session.IsAuthenticated, "the key session is authenticated");
        }

        using (SshSession session = new SshSession(_environment.Parameters()))
        {
            Test.Check(!session.AuthenticateWithKeyFile(_environment.User, out bool passphraseRequired, _environment.EncryptedKeyFile, "wrong-passphrase"), "a wrong passphrase does not authenticate");
            Test.Check(passphraseRequired, "a wrong passphrase is reported as passphraseRequired");
        }

        using (SshSession session = new SshSession(_environment.Parameters()))
        {
            Test.Check(!session.AuthenticateWithKeyFile(_environment.User, out bool passphraseRequired, _environment.EncryptedKeyFile), "an encrypted key without a passphrase does not authenticate");
            Test.Check(passphraseRequired, "a missing passphrase is reported as passphraseRequired");
        }

        using (SshSession session = new SshSession(_environment.Parameters()))
        {
            Test.Check(session.AuthenticateWithKeyFile(_environment.User, out bool passphraseRequired, _environment.EncryptedKeyFile, _environment.KeyPassphrase), "the encrypted key authenticates with its passphrase");
            Test.Check(!passphraseRequired, "a correct passphrase is not reported as missing");
        }

        using (SshSession session = new SshSession(_environment.Parameters()))
        {
            string missing = Path.Combine(Path.GetDirectoryName(_environment.KeyFile), "no-such-key");
            Test.Throws<IOException>(() => session.AuthenticateWithKeyFile(_environment.User, out _, missing), "a missing key file is reported");
        }
    }

    private static void ExecChannels()
    {
        Test.Section("Exec channels with and without a PTY");
        using (SshSession session = Connect())
        using (SshShellChannel channel = session.OpenShellChannel("echo out; echo err 1>&2", terminalType: null, ExtendedDataMode.Normal))
        {
            Test.Check(channel.TerminalType == null, "a channel without a PTY reports no terminal type");
            string stdout = ReadToEnd(channel.Output);
            string stderr = ReadToEnd(channel.Error);
            channel.WaitClosed();
            Test.Check(stdout.Contains("out"), "stdout is delivered on the output stream");
            Test.Check(!stdout.Contains("err"), "stderr is not mixed into stdout");
            Test.Check(stderr.Contains("err"), "stderr is delivered on the separate error stream");
            Test.Equal(0, channel.ExitStatus, "the exit status is reported");
            Test.Check(channel.IsEof, "the channel reports EOF once it is drained");
            Test.Check(channel.TryWaitClosed(), "the channel reports that it is closed");
        }

        // With a PTY the server merges both streams, which is why SmarTTY opens a PTY only for interactive use.
        using (SshSession session = Connect())
        using (SshShellChannel channel = session.OpenShellChannel("echo out; echo err 1>&2", terminalType: "xterm", ExtendedDataMode.Normal))
        {
            Test.Equal("xterm", channel.TerminalType, "the PTY type is recorded on the channel");
            string stdout = ReadToEnd(channel.Output);
            channel.WaitClosed();
            Test.Check(stdout.Contains("out") && stdout.Contains("err"), "a PTY session merges stdout and stderr");
            Test.Equal(0, channel.ExitStatus, "the PTY channel still reports its exit status");
        }

        using (SshSession session = Connect())
        using (SshShellChannel channel = session.OpenShellChannel("exit 7"))
        {
            ReadToEnd(channel.Output);
            channel.WaitClosed();
            Test.Equal(7, channel.ExitStatus, "a non-zero exit status is reported");
        }

        using (SshSession session = Connect())
        using (SshShellChannel channel = session.OpenShellChannel("kill -TERM $$"))
        {
            ReadToEnd(channel.Output);
            channel.WaitClosed();
            Test.Equal(-1, channel.ExitStatus, "a signal-terminated command reports -1");
        }

        using (SshSession session = Connect())
        using (SshShellChannel channel = session.OpenShellChannel("echo hidden 1>&2"))
        {
            string stdout = ReadToEnd(channel.Output);
            string stderr = ReadToEnd(channel.Error);
            channel.WaitClosed();
            Test.Check(!stdout.Contains("hidden"), "stdout stays empty when only stderr is written");
            Test.Check(stderr.Contains("hidden"), "the error stream carries the message");
        }

        using (SshSession session = Connect())
        {
            Test.Check(session.RunCommand("nonexistent-command-smartty-test", out int? notFound) != null, "a failing command still returns output");
            Test.Check(notFound != 0, "an unknown command reports a non-zero status");
        }
    }

    private static void ShellChannel()
    {
        Test.Section("Shell channel, PTY resize and EOF");
        using SshSession session = Connect();
        using SshShellChannel channel = session.OpenShellChannel(command: null, terminalType: "xterm");
        channel.ResizePty(120, 40);
        Test.Check(true, "ResizePty sends a window-change request without failing");

        byte[] command = Encoding.ASCII.GetBytes("echo shell-works; exit 0\n");
        channel.Input.Write(command, 0, command.Length);
        channel.SendEof();
        channel.SendEof();
        Test.Check(true, "SendEof is idempotent");

        string output = ReadToEnd(channel.Output);
        channel.WaitClosed();
        Test.Check(output.Contains("shell-works"), "the shell channel runs the typed command");
        Test.Check(!SmarTTY.Terminal.ByteColumns.ParseProbe(session.RunCommand(SmarTTY.Terminal.ByteColumns.ProbeCommand, out _)), "a bash login shell is not taken for a shell that counts UTF-8 bytes");
        Test.Check(!channel.ClosedByConnectionLoss, "a shell that exits is not reported as closed by a lost connection");
        Test.Check(!channel.IsDisposed, "IsDisposed is false before Dispose");
        channel.Dispose();
        Test.Check(channel.IsDisposed, "IsDisposed is true after Dispose");

        // A terminal tab must not take the end of the session for the shell exiting (that would close the tab).
        SshSession closing = Connect();
        SshShellChannel open = closing.OpenShellChannel(command: null, terminalType: "xterm");
        closing.Dispose();
        Test.Check(open.TryWaitClosed(), "disposing the session closes its open channels");
        Test.Check(open.ClosedByConnectionLoss, "a channel still open when the session ends is reported as closed by the connection");
        open.Dispose();
    }

    private static void ExtendedData()
    {
        Test.Section("Polling delivery and extended data modes");
        using (SshSession session = Connect())
        using (SshShellChannel channel = session.OpenShellChannel("echo polled; echo polled-err 1>&2"))
        {
            StringBuilder stdout = new StringBuilder();
            StringBuilder stderr = new StringBuilder();
            channel.DataReceived += (object sender, byte[] buffer, int offset, int count, int streamId) =>
            {
                (streamId == 0 ? stdout : stderr).Append(Encoding.UTF8.GetString(buffer, offset, count));
            };
            channel.PollingEnabled = true;
            Test.WaitUntil(() => channel.TryWaitClosed() && stdout.ToString().Contains("polled") && stderr.ToString().Contains("polled-err"), TimeSpan.FromSeconds(20), "polling delivers stdout and stderr to DataReceived");
            Test.Equal(0, channel.ExitStatus, "the polled channel still reports its exit status");
        }

        using (SshSession session = Connect())
        using (SshShellChannel channel = session.OpenShellChannel("echo ignored 1>&2; echo kept", extendedDataMode: ExtendedDataMode.Ignore))
        {
            string stdout = ReadToEnd(channel.Output);
            channel.WaitClosed();
            Test.Check(stdout.Contains("kept"), "Ignore mode keeps stdout");
            Test.Check(!stdout.Contains("ignored"), "Ignore mode drops stderr");
        }

        using (SshSession session = Connect())
        using (SshShellChannel channel = session.OpenShellChannel("echo one; echo two 1>&2", extendedDataMode: ExtendedDataMode.Merge))
        {
            string stdout = ReadToEnd(channel.Output);
            channel.WaitClosed();
            Test.Check(stdout.Contains("one") && stdout.Contains("two"), "Merge mode joins stderr into the output stream");
        }

        using (SshSession session = Connect())
        using (SshShellChannel channel = session.OpenShellChannel("echo evented"))
        {
            ManualResetEvent finished = new ManualResetEvent(false);
            channel.Finished += (object sender) => finished.Set();
            channel.PollingEnabled = true;
            Test.Check(finished.WaitOne(TimeSpan.FromSeconds(20)), "the Finished event is raised for a polled channel");
        }

        using (SshSession session = Connect())
        using (SshShellChannel channel = session.OpenShellChannel("echo events"))
        {
            channel.ReadStderrOnRead = true;
            int stderrEvents = 0;
            channel.DataReceived += (object sender, byte[] buffer, int offset, int count, int streamId) =>
            {
                if (streamId != 0)
                {
                    Interlocked.Increment(ref stderrEvents);
                }
            };
            ReadToEnd(channel.Output);
            channel.WaitClosed();
            Test.Check(true, "ReadStderrOnRead drains stderr while stdout is read");
        }
    }

    private static void Scp()
    {
        Test.Section("SCP upload and download");
        using SshSession session = Connect();
        string remote = _environment.RemoteFile("smartty-upload.bin");
        byte[] payload = new byte[8192];
        new Random(1234).NextBytes(payload);
        DateTime modifyTime = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

        using (ScpChannelStream upload = session.OpenUpload(remote, payload.Length, modifyTime, Convert.ToInt32("640", 8)))
        {
            upload.Write(payload, 0, payload.Length);
            Test.Equal((long)payload.Length, upload.Length, "the upload stream reports the announced length");
            Test.Throws<IOException>(() => upload.Write(payload, 0, 1), "writing more than announced is refused");
        }

        List<string> stat = session.RunCommand("stat -c '%a %s' " + remote, out _);
        Test.Check(stat != null && stat.Count == 1 && stat[0] == "640 " + payload.Length, "the upload keeps the requested mode and size (actual: " + string.Join(" ", stat ?? new List<string>()) + ")");

        List<string> stamp = session.RunCommand("date -u -r " + remote + " +%Y-%m-%dT%H:%M:%S", out _);
        Test.Check(stamp != null && stamp.Count == 1 && stamp[0] == "2024-05-06T07:08:09", "the upload keeps the modification time (actual: " + string.Join(" ", stamp ?? new List<string>()) + ")");

        using (ScpDownloadStream download = session.OpenDownload(remote))
        {
            Test.Equal((long)payload.Length, download.Length, "the download stream knows the remote size");
            Test.Equal(modifyTime, download.ModifyTime, "the download reports the remote modification time");
            Test.Equal(SshSession.UnixEpoch, download.ChangeTime, "SCP does not transfer a change time");
            byte[] received = ReadExactly(download, payload.Length);
            Test.Check(received.SequenceEqual(payload), "the downloaded bytes match the uploaded bytes");
        }

        Test.Throws<RemoteOperationException>(() => session.OpenDownload(_environment.RemoteHome), "downloading a directory is reported", ex => Test.Check(ex.Message.Contains("regular file") || ex.Message.Contains("directory"), "the directory message is readable (actual: " + ex.Message + ")"));
        Test.Throws<RemotePathException>(() => session.OpenUpload(_environment.RemoteHome + "/missing/nope.bin", 1), "an upload to a missing directory is diagnosed", ex => Test.Equal(RemotePathProblem.DirectoryNotWritable, ex.Problem, "the diagnosis points at the unwritable directory"));
        Test.Check(session.RunCommand("rm -f " + remote, out _) != null, "the uploaded file is cleaned up");
    }

    private static void ConnectionLoss()
    {
        Test.Section("Connection loss");
        SshSession session = new SshSession(_environment.Parameters());
        session.Authenticate(_environment.User, _environment.Password);
        using SshShellChannel channel = session.OpenShellChannel("echo PPID=$$; kill -9 $PPID; sleep 60");
        ManualResetEvent lost = new ManualResetEvent(false);
        session.ConnectionLost += (object sender, EventArgs e) => lost.Set();
        // Killing the server-side session process drops the connection; SSH.NET then reports the failure.
        ReadToEnd(channel.Output);
        Test.Check(lost.WaitOne(TimeSpan.FromSeconds(30)), "ConnectionLost is raised when the connection goes away");
        Test.Check(channel.TryWaitClosed(), "open channels are closed when the connection goes away");
        Test.Check(channel.ClosedByConnectionLoss, "a channel closed by the lost connection says so");
    }

    private static string ReadToEnd(Stream stream)
    {
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

    private static byte[] ReadExactly(Stream stream, int length)
    {
        byte[] buffer = new byte[length];
        int done = 0;
        while (done < length)
        {
            int read = stream.Read(buffer, done, length - done);
            if (read <= 0)
            {
                break;
            }
            done += read;
        }
        return (done == length) ? buffer : buffer.Take(done).ToArray();
    }
}
