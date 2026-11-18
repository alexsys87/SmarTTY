using System.Collections.Generic;
using System.IO;

namespace SmarTTY.CodeEditing;

/// <summary>
/// What the remote file editor asks of the connection to read a file's state on the server: the calls it made on the SSH session
/// before they went behind this seam. The Unix host is reached with shell commands and SCP, a Windows host with SFTP (it has none of
/// the commands). The adapter over the SSH session is <c>Controls/SessionRemoteFileHost.cs</c>; it looks the session up on every call,
/// because a reconnect gives the tab a new one.
/// </summary>
public interface IRemoteFileHost
{
    /// <summary>The server is a Windows host.</summary>
    bool IsWindows { get; }

    /// <summary>
    /// Runs a command and returns its output lines, or null when the command failed (the exit status is not 0) or there is no
    /// connection (see <see cref="ThrowIfConnectionLost"/>).
    /// </summary>
    List<string> RunCommandChecked(string command);

    /// <summary>Throws when the connection is gone: a null from <see cref="RunCommandChecked"/> means "failed" as well.</summary>
    void ThrowIfConnectionLost();

    /// <summary>The file over SCP (a Unix host) and its size, known from the first header before any data is read.</summary>
    Stream OpenScpDownload(string path, out long length);

    /// <summary>The file over SFTP (a Windows host) and its size.</summary>
    Stream OpenSftpRead(string path, out long length);

    /// <summary>The file or the directory exists (a Windows host).</summary>
    bool SftpExists(string path);

    /// <summary>The user can write it: null when that cannot be found out (a Windows host).</summary>
    bool? SftpCanWrite(string path);

    /// <summary>The version of the file, as <see cref="RemoteFileServer.FetchSignature"/> says it; null when there is no such file (a Windows host).</summary>
    string SftpSignature(string path);
}
