using System;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

/// <summary>What the sudo command did: the status the shell channel reported, and whether it reported one at all.</summary>
public sealed class SudoRunResult
{
    /// <summary>The exit code; -1 when the command was terminated by a signal or the status never came.</summary>
    public int ExitStatus;

    /// <summary>The server reported an exit status.</summary>
    public bool HasExitStatus;

    /// <summary>The command was terminated by a signal.</summary>
    public bool HasExitSignal;
}

/// <summary>
/// The calls the remote file editor makes on the SSH session to write the file, on top of those that read it (<see cref="IRemoteFileHost"/>):
/// the upload of the temporary copy, the commands that check and replace the file, the write over SFTP of a Windows host, and the sudo
/// command with its password prompt. The adapter is <c>Controls/SessionRemoteFileHost.cs</c>.
/// </summary>
public interface IRemoteFileWriteHost : IRemoteFileHost
{
    /// <summary>Uploads <paramref name="bytes"/> over SCP to <paramref name="path"/> with the access mode <paramref name="mode"/>.</summary>
    void UploadScp(string path, byte[] bytes, int mode);

    /// <summary>
    /// Runs a command and returns its output lines (null when the connection is gone); <paramref name="exitStatus"/> is the exit status, or null when
    /// there was none.
    /// </summary>
    List<string> RunCommand(string command, out int? exitStatus, TimeSpan? timeout);

    /// <summary>
    /// Writes the file over SFTP (a Windows host): created or truncated in place, with the "file~" backup first when asked, and the size checked afterwards.
    /// </summary>
    void SftpSave(string path, byte[] bytes, bool backup);

    /// <summary>
    /// Runs <paramref name="sudoCommand"/> on a shell channel and shows its password prompt (UI thread): returns when the channel is closed.
    /// </summary>
    SudoRunResult RunSudo(string sudoCommand);
}
