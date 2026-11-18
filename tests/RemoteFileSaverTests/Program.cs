using System;
using System.Collections.Generic;
using System.IO;
using SmarTTY.CodeEditing;
using SmarTTY.IO;
using SmarTTY.Utilities;

// Checks of CodeEditing/RemoteFileSaver.cs, what the remote file editor does on the server to save (the upload of the temporary copy, the check of
// the file, the replacement with its failures, the replacement through sudo, the clean-up, the reload command of a service), with a fake connection.
// The same code is run in the editor against real servers by tests/RemoteFileEditorIntegrationTests.
// Run with: dotnet run --project tests/RemoteFileSaverTests
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

    private static T Throws<T>(Action action, string name) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            Check(true, name);
            return exception;
        }
        Check(false, name + " (nothing was thrown)");
        return null;
    }

    private sealed class FakeHost : IRemoteFileWriteHost
    {
        public bool IsWindows { get; set; }

        public readonly List<string> Calls = new List<string>();

        // The answers of the commands by their start: the output, and the exit status.
        public readonly Dictionary<string, KeyValuePair<List<string>, int?>> Commands = new Dictionary<string, KeyValuePair<List<string>, int?>>();

        public bool ConnectionLost;

        public SudoRunResult Sudo = new SudoRunResult { ExitStatus = 0, HasExitStatus = true };

        public string SftpSignatureAnswer = "sftp-sig";

        public Exception FailOnRun;

        public byte[] Uploaded;

        public int UploadedMode;

        public List<string> RunCommandChecked(string command)
        {
            Calls.Add("checked " + command);
            if (FailOnRun != null)
            {
                throw FailOnRun;
            }
            foreach (KeyValuePair<string, KeyValuePair<List<string>, int?>> answer in Commands)
            {
                if (command.StartsWith(answer.Key))
                {
                    return answer.Value.Value == 0 ? answer.Value.Key : null;
                }
            }
            return null;
        }

        public List<string> RunCommand(string command, out int? exitStatus, TimeSpan? timeout)
        {
            Calls.Add("run " + command + (timeout.HasValue ? " (timeout " + (int)timeout.Value.TotalSeconds + "s)" : ""));
            if (FailOnRun != null)
            {
                exitStatus = null;
                throw FailOnRun;
            }
            foreach (KeyValuePair<string, KeyValuePair<List<string>, int?>> answer in Commands)
            {
                if (command.StartsWith(answer.Key))
                {
                    exitStatus = answer.Value.Value;
                    return answer.Value.Key;
                }
            }
            exitStatus = null;
            return null;
        }

        public void ThrowIfConnectionLost()
        {
            Calls.Add("check connection");
            if (ConnectionLost)
            {
                throw new IOException("The connection to the server was lost.");
            }
        }

        public Stream OpenScpDownload(string path, out long length) { throw new NotSupportedException(); }

        public Stream OpenSftpRead(string path, out long length) { throw new NotSupportedException(); }

        public bool SftpExists(string path) { return false; }

        public bool? SftpCanWrite(string path) { return true; }

        public string SftpSignature(string path)
        {
            Calls.Add("sftp signature " + path);
            return SftpSignatureAnswer;
        }

        public void UploadScp(string path, byte[] bytes, int mode)
        {
            Calls.Add("upload " + path);
            Uploaded = bytes;
            UploadedMode = mode;
        }

        public void SftpSave(string path, byte[] bytes, bool backup)
        {
            Calls.Add("sftp save " + path + " " + bytes.Length + (backup ? " with backup" : ""));
        }

        public SudoRunResult RunSudo(string sudoCommand)
        {
            Calls.Add("sudo " + sudoCommand);
            return Sudo;
        }
    }

    private const string Target = "/etc/app/a.conf";

    private const string Temporary = "/tmp/x.txt";

    private static string SignatureCommand = RemoteFileSignature.BuildCommand(Target);

    private static RemoteSaveJob Job(bool backup = false, ConfigValidator validator = null)
    {
        return new RemoteSaveJob
        {
            Bytes = new byte[] { 1, 2, 3 },
            TemporaryPath = Temporary,
            ReplaceCommand = RemoteSaveCommand.BuildScript(Target, Temporary, 3, backup),
            IncludesBackup = backup,
            Validator = validator
        };
    }

    private static RemoteFileSaver Saver(FakeHost host, bool sudoOnSave = false)
    {
        host.Commands[SignatureCommand] = new KeyValuePair<List<string>, int?>(new List<string> { "new-sig" }, 0);
        return new RemoteFileSaver(host, new RemoteFileServer(host, Target), Target, () => sudoOnSave);
    }

    private static string Replace(RemoteSaveJob job)
    {
        return "run " + PosixShell.Wrap(job.ReplaceCommand);
    }

    private static void PlainSave()
    {
        FakeHost host = new FakeHost();
        RemoteFileSaver saver = Saver(host);
        RemoteSaveJob job = Job();
        host.Commands[PosixShell.Wrap(job.ReplaceCommand)] = new KeyValuePair<List<string>, int?>(new List<string>(), 0);
        Check(saver.UploadAndReplace(job), "a save that the server accepts is done");
        Equal(string.Join("|", host.Calls), "upload " + Temporary + "|" + Replace(job) + "|checked " + SignatureCommand, "the order: upload, replace, signature");
        Equal(host.UploadedMode, 0x180, "the temporary copy is private (0600)");
        Equal(host.Uploaded, job.Bytes, "the bytes of the job are uploaded");
        Equal(job.NewSignature, "new-sig", "the new version of the file is read");
        Check(job.Validation == null && !job.KeepTemporaryFiles, "nothing else is set");

        // The user said at opening that the file is written with sudo: no plain attempt.
        host = new FakeHost();
        saver = Saver(host, sudoOnSave: true);
        job = Job();
        Check(!saver.UploadAndReplace(job), "sudo on save: the plain write is not tried, sudo is asked for");
        Equal(string.Join("|", host.Calls), "upload " + Temporary, "only the upload ran");
    }

    private static void Validation()
    {
        ConfigValidator validator = new ConfigValidator("sh -n").Add("sh", "-n {FILE}");
        FakeHost host = new FakeHost();
        RemoteFileSaver saver = Saver(host);
        RemoteSaveJob job = Job(validator: validator);
        string checkCommand = PosixShell.Wrap(validator.BuildCommand(RemotePathUtils.ShellQuote(Temporary)));
        host.Commands[checkCommand] = new KeyValuePair<List<string>, int?>(new List<string> { Temporary + ": 3: Syntax error: \"(\" unexpected" }, 2);
        Check(!saver.UploadAndReplace(job), "a check that fails: the file is not replaced");
        Check(job.Validation != null && job.Validation.Outcome == ValidationOutcome.Failed, "the failure is kept for the editor");
        Equal(job.Validation.Issues.Count, 1, "one problem");
        Equal(job.Validation.Issues[0].Line, 3, "on line 3");
        Equal(string.Join("|", host.Calls), "upload " + Temporary + "|run " + checkCommand + " (timeout 20s)", "the check runs on the uploaded copy, with a timeout of 20 s, and nothing replaces the file");

        host = new FakeHost();
        saver = Saver(host);
        job = Job(validator: validator);
        host.Commands[checkCommand] = new KeyValuePair<List<string>, int?>(new List<string>(), 0);
        host.Commands[PosixShell.Wrap(job.ReplaceCommand)] = new KeyValuePair<List<string>, int?>(new List<string>(), 0);
        Check(saver.UploadAndReplace(job), "a check that passes: the file is replaced");
        Check(job.Validation == null, "no failure is kept");

        host = new FakeHost();
        saver = Saver(host);
        job = Job(validator: validator);
        host.Commands[checkCommand] = new KeyValuePair<List<string>, int?>(new List<string>(), 127);
        host.Commands[PosixShell.Wrap(job.ReplaceCommand)] = new KeyValuePair<List<string>, int?>(new List<string>(), 0);
        Check(saver.UploadAndReplace(job), "a check that cannot run (no such program) does not stop the save");
    }

    private static void FailedReplacement()
    {
        // The write was refused: no description for a plain failure, the connection is fine: sudo is offered.
        FakeHost host = new FakeHost();
        RemoteFileSaver saver = Saver(host);
        RemoteSaveJob job = Job();
        host.Commands[PosixShell.Wrap(job.ReplaceCommand)] = new KeyValuePair<List<string>, int?>(new List<string> { "cp: cannot create regular file: Permission denied" }, 1);
        Check(!saver.UploadAndReplace(job), "a refused write: false, so that sudo can be offered");
        Check(host.Calls.Contains("check connection") && !job.KeepTemporaryFiles, "the connection was checked first, the files are not kept");

        // No space: an error that says so, no sudo.
        host = new FakeHost();
        saver = Saver(host);
        job = Job();
        host.Commands[PosixShell.Wrap(job.ReplaceCommand)] = new KeyValuePair<List<string>, int?>(new List<string>(), RemoteSaveCommand.ExitNoSpace);
        IOException noSpace = Throws<IOException>(() => saver.UploadAndReplace(job), "no space: an error, not a question about sudo");
        Equal(noSpace.Message, RemoteSaveCommand.Describe(RemoteSaveCommand.ExitNoSpace, new List<string>(), Target, Temporary), "the message is the one of RemoteSaveCommand");
        Check(!job.KeepTemporaryFiles, "the files are not kept when the old file is intact");

        // The old content could not be put back: the files are kept.
        host = new FakeHost();
        saver = Saver(host);
        job = Job();
        host.Commands[PosixShell.Wrap(job.ReplaceCommand)] = new KeyValuePair<List<string>, int?>(new List<string>(), RemoteSaveCommand.ExitNotRestored);
        Throws<IOException>(() => saver.UploadAndReplace(job), "not restored: an error");
        Check(job.KeepTemporaryFiles, "the uploaded text and the restore copy are kept");

        // No answer and the connection is gone.
        host = new FakeHost { ConnectionLost = true };
        saver = Saver(host);
        job = Job();
        IOException lost = Throws<IOException>(() => saver.UploadAndReplace(job), "a lost connection during the replacement: an error");
        Check(lost.Message.StartsWith("The connection to the server was lost while " + Target + " was being written."), "which says so, and that the text is still in the editor");

        // No answer, the connection is there: sudo can be offered.
        host = new FakeHost();
        saver = Saver(host);
        job = Job();
        Check(!saver.UploadAndReplace(job), "no answer and a live connection: false");
    }

    private static void WindowsHost()
    {
        FakeHost host = new FakeHost { IsWindows = true };
        RemoteFileSaver saver = Saver(host);
        RemoteSaveJob job = Job(backup: true);
        Check(saver.UploadAndReplace(job), "Windows: written over SFTP");
        Equal(string.Join("|", host.Calls), "sftp save " + Target + " 3 with backup|sftp signature " + Target, "Windows: one write, then the signature; no upload, no command");
        Equal(job.NewSignature, "sftp-sig", "Windows: the new version of the file");
        host.Calls.Clear();
        saver.RemoveTemporaryFile(job);
        Equal(host.Calls.Count, 0, "Windows: nothing to remove");
    }

    private static void Sudo()
    {
        FakeHost host = new FakeHost();
        RemoteFileSaver saver = Saver(host);
        RemoteSaveJob job = Job();
        saver.ReplaceWithSudo(job);
        Equal(string.Join("|", host.Calls), "sudo sudo sh -c " + RemotePathUtils.ShellQuote(job.ReplaceCommand), "the replace script runs through sudo sh -c, quoted");

        host = new FakeHost { Sudo = new SudoRunResult { ExitStatus = RemoteSaveCommand.ExitNotRestored, HasExitStatus = true } };
        saver = Saver(host);
        job = Job();
        Exception failure = Throws<Exception>(() => saver.ReplaceWithSudo(job), "sudo: a failure of the script is an error");
        Equal(failure.Message, RemoteSaveCommand.Describe(RemoteSaveCommand.ExitNotRestored, null, Target, Temporary), "the message is the one of RemoteSaveCommand, with no details");
        Check(job.KeepTemporaryFiles, "and the files are kept when the old content was not restored");

        host = new FakeHost { Sudo = new SudoRunResult { ExitStatus = -1 } };
        saver = Saver(host);
        Equal(Throws<Exception>(() => saver.RunSudoCommand("x"), "no exit status and no signal: an error").Message, "Sudo did not report an exit status", "which says so");

        host = new FakeHost { Sudo = new SudoRunResult { ExitStatus = -1, HasExitSignal = true } };
        saver = Saver(host);
        Equal(Throws<Exception>(() => saver.RunSudoCommand("x"), "a signal is a failure").Message, "Sudo exited with code -1", "with the usual message");

        host = new FakeHost { Sudo = new SudoRunResult { ExitStatus = 5, HasExitStatus = true } };
        saver = Saver(host);
        Equal(Throws<Exception>(() => saver.RunSudoCommand("x"), "a status other than 0 is a failure").Message, "Sudo exited with code 5", "the usual message");
        Equal(Throws<Exception>(() => saver.RunSudoCommand("x", status => "failed " + status), "a message of the caller").Message, "failed 5", "is used");

        host = new FakeHost();
        saver = Saver(host);
        saver.RunSudoCommand("echo 'it'");
        Equal(host.Calls[0], "sudo sudo sh -c 'echo '\\''it'\\'''", "a quote in the command is escaped for the shell");
    }

    private static void CleanUp()
    {
        FakeHost host = new FakeHost();
        RemoteFileSaver saver = Saver(host);
        RemoteSaveJob job = Job();
        saver.RemoveTemporaryFile(job);
        Equal(string.Join("|", host.Calls), "checked rm -f '" + Temporary + "' '" + RemoteSaveCommand.RestoreCopyPath(Temporary) + "'", "the uploaded copy and the restore copy are removed");

        host.Calls.Clear();
        job.KeepTemporaryFiles = true;
        saver.RemoveTemporaryFile(job);
        Equal(host.Calls.Count, 0, "kept files are not removed");

        host = new FakeHost { FailOnRun = new IOException("gone") };
        saver = Saver(host);
        saver.RemoveTemporaryFile(Job());
        Check(true, "a failure of the clean-up is not reported");
    }

    private static void ServiceReload()
    {
        FakeHost host = new FakeHost();
        RemoteFileSaver saver = Saver(host);
        ServiceReload reload = new ServiceReload { Name = "nginx", Command = "nginx -t && nginx -s reload" };
        host.Commands[reload.Command] = new KeyValuePair<List<string>, int?>(new List<string> { "ok" }, 0);
        saver.RunServiceReload(reload, out List<string> output, out int? status);
        Equal(string.Join(",", output), "ok", "the output of the reload command");
        Equal(status, (int?)0, "and its status");
        Equal(host.Calls[0], "run nginx -t && nginx -s reload (timeout 30s)", "it runs with a timeout of 30 s, as it is");

        host = new FakeHost { FailOnRun = new IOException("gone") };
        saver = Saver(host);
        saver.RunServiceReload(reload, out output, out status);
        Check(output == null && status == null, "a failure of the connection is no answer");
    }

    private static int Main()
    {
        try
        {
            PlainSave();
            Validation();
            FailedReplacement();
            WindowsHost();
            Sudo();
            CleanUp();
            ServiceReload();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " remote file saver checks passed.");
        return 0;
    }
}
