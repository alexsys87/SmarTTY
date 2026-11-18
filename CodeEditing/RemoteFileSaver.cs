using SmarTTY.IO;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;

namespace SmarTTY.CodeEditing;

/// <summary>
/// What the remote file editor does on the server to save: uploads the text to a temporary file, runs the check of the file, replaces the file
/// (through sudo when the plain write is refused), reads the new version, and removes what it left. The code is that of <c>RemoteFileEditor</c>
/// (<c>UploadAndReplace</c>, <c>RunValidation</c>, <c>SaveWithSudo</c> without its question, <c>RunSudoCommand</c>, <c>RemoveTemporaryFile</c>,
/// and the reload command of a service), moved here as it was. The questions to the user, the threads, and what a save does to the text stay in the editor.
/// </summary>
public sealed class RemoteFileSaver
{
    // Permissions of the temporary copy uploaded on save: it may hold a file only root can read.
    private const int TemporaryFileMode = 0x180; // 0600

    private readonly IRemoteFileWriteHost _host;

    private readonly RemoteFileServer _server;

    private readonly string _remotePath;

    private readonly Func<bool> _sudoOnSave;

    /// <param name="sudoOnSave">The user agreed to write the file with sudo (asked when the file was opened): the save does not try the plain write first.</param>
    public RemoteFileSaver(IRemoteFileWriteHost host, RemoteFileServer server, string remotePath, Func<bool> sudoOnSave)
    {
        _host = host;
        _server = server;
        _remotePath = remotePath;
        _sudoOnSave = sudoOnSave;
    }

    /// <summary>
    /// Uploads the text to the temporary file and copies it over the target (any thread). Returns false when
    /// the target could not be written, so that sudo can be offered.
    /// </summary>
    public bool UploadAndReplace(RemoteSaveJob job)
    {
        if (_server.IsWindows)
        {
            // Written over SFTP straight into the file (with the "~" backup if the job asks for one): there is no temporary file
            // in /tmp and no cat, and a refused write is an error, not a question about sudo.
            _host.SftpSave(_remotePath, job.Bytes, job.IncludesBackup);
            job.NewSignature = _server.FetchSignature();
            return true;
        }
        _host.UploadScp(job.TemporaryPath, job.Bytes, TemporaryFileMode);
        if (job.Validator != null)
        {
            // The target is still the old file: a text that the check rejects never replaces it.
            ValidationResult validation = RunValidation(job);
            if (validation.Outcome == ValidationOutcome.Failed)
            {
                job.Validation = validation;
                return false;
            }
        }
        if (_sudoOnSave())
        {
            // The user said at opening that this file is written with sudo: no failing plain attempt first.
            return false;
        }
        List<string> replaceOutput = _host.RunCommand(PosixShell.Wrap(job.ReplaceCommand), out int? replaceStatus, null);
        if (replaceStatus == 0)
        {
            job.NewSignature = _server.FetchSignature();
            return true;
        }
        if (replaceStatus.HasValue)
        {
            // The space ran out or the write failed: the file is as it was (or, when even that failed, the old content is kept
            // next to the uploaded text). That is no refused write, so sudo is not offered for it.
            string description = RemoteSaveCommand.Describe(replaceStatus.Value, replaceOutput, _remotePath, job.TemporaryPath);
            if (description != null)
            {
                job.KeepTemporaryFiles = replaceStatus.Value == RemoteSaveCommand.ExitNotRestored;
                throw new IOException(description);
            }
        }
        // A lost connection is not a refused write: do not offer sudo for it.
        try
        {
            _host.ThrowIfConnectionLost();
        }
        catch (IOException)
        {
            throw new IOException("The connection to the server was lost while " + _remotePath + " was being written. Check the file on the server before you save again: your text is still in the editor.");
        }
        return false;
    }

    /// <summary>
    /// Runs the check of the file on the uploaded copy (any thread). A check that cannot run, or that finds nothing it
    /// can point to, does not stop the save.
    /// </summary>
    private ValidationResult RunValidation(RemoteSaveJob job)
    {
        // Through /bin/sh, as the command that replaces the file: the command is POSIX shell ("if command -v x; then ...; fi"),
        // which fish or tcsh as the login shell of the user cannot read (the check would then never run).
        string command = PosixShell.Wrap(job.Validator.BuildCommand(RemotePathUtils.ShellQuote(job.TemporaryPath)));
        List<string> output = _host.RunCommand(command, out int? exitStatus, TimeSpan.FromSeconds(20));
        return job.Validator.Interpret(output, exitStatus, job.TemporaryPath);
    }

    /// <summary>Replaces the file through sudo (UI thread); the caller has asked the user whether to do so, if it had to.</summary>
    public void ReplaceWithSudo(RemoteSaveJob job)
    {
        RunSudoCommand(job.ReplaceCommand, status =>
        {
            // What the script printed is not captured here (it goes to the terminal of the prompt), so the message has no details.
            job.KeepTemporaryFiles |= status == RemoteSaveCommand.ExitNotRestored;
            return RemoteSaveCommand.Describe(status, null, _remotePath, job.TemporaryPath);
        });
    }

    /// <summary>
    /// Runs a command through sudo on a shell channel, with the password prompt of the editor (UI thread).
    /// <paramref name="describeFailure"/> may turn an exit status into the message of the exception (null: the usual one).
    /// </summary>
    public void RunSudoCommand(string command, Func<int, string> describeFailure = null)
    {
        string sudoCommand = "sudo sh -c " + RemotePathUtils.ShellQuote(command);
        SudoRunResult result = _host.RunSudo(sudoCommand);
        if (!result.HasExitStatus && !result.HasExitSignal)
        {
            throw new Exception("Sudo did not report an exit status");
        }
        if (result.ExitStatus != 0)
        {
            throw new Exception(describeFailure?.Invoke(result.ExitStatus) ?? "Sudo exited with code " + result.ExitStatus);
        }
    }

    /// <summary>Removes what the save left on the server: the uploaded copy and the restore copy (any thread).</summary>
    public void RemoveTemporaryFile(RemoteSaveJob job)
    {
        if (_server.IsWindows)
        {
            // Nothing was uploaded to a temporary file.
            return;
        }
        if (job.KeepTemporaryFiles)
        {
            return;
        }
        try
        {
            // The script removes its restore copy itself; this is for the case that it was cut short.
            _host.RunCommandChecked("rm -f " + RemotePathUtils.ShellQuote(job.TemporaryPath) + " " + RemotePathUtils.ShellQuote(RemoteSaveCommand.RestoreCopyPath(job.TemporaryPath)));
        }
        catch
        {
        }
    }

    /// <summary>
    /// Asks the server to reload a service so that it reads the file again (any thread). The output and the exit status are what the command gave:
    /// an exception of the connection is no answer (both stay null).
    /// </summary>
    public void RunServiceReload(ServiceReload reload, out List<string> output, out int? exitStatus)
    {
        output = null;
        exitStatus = null;
        try
        {
            output = _host.RunCommand(reload.Command, out exitStatus, TimeSpan.FromSeconds(30));
        }
        catch
        {
        }
    }
}
