using SmarTTY.IO;
using System;
using System.IO;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The file of the remote file editor as it is on the server: whether it (or a path) exists, whether it can be written, how to read it,
/// and the version that tells whether somebody else changed it. The code is that of <c>RemoteFileEditor</c> (probes of the load and of the
/// conflict check), moved here as it was; every call goes to the connection through <see cref="IRemoteFileHost"/>.
/// </summary>
public class RemoteFileServer
{
    private readonly IRemoteFileHost _host;

    private readonly string _remotePath;

    public RemoteFileServer(IRemoteFileHost host, string remotePath)
    {
        _host = host;
        _remotePath = remotePath;
    }

    /// <summary>The server is a Windows host, which has none of the commands that the rest of the editor uses (test, stat, cat, cp, rm, sudo).</summary>
    public bool IsWindows => _host.IsWindows;

    /// <summary>
    /// The file on the server as a stream, and its size: over SCP for a Unix host, over SFTP for a Windows host.
    /// </summary>
    public Stream OpenForReading(out long length)
    {
        if (IsWindows)
        {
            return _host.OpenSftpRead(_remotePath, out length);
        }
        return _host.OpenScpDownload(_remotePath, out length);
    }

    /// <summary>The path exists on the server. Without an answer from a Unix host the connection is checked: no answer is not "does not exist".</summary>
    public bool Exists(string path)
    {
        if (IsWindows)
        {
            return _host.SftpExists(path);
        }
        if (_host.RunCommandChecked("test -e " + RemotePathUtils.ShellQuote(path)) != null)
        {
            return true;
        }
        // No answer is not "does not exist": without a connection the file may be there.
        _host.ThrowIfConnectionLost();
        return false;
    }

    /// <summary>
    /// The user can write <paramref name="target"/>. A Windows host that cannot tell counts as writable. For a Unix host no answer is not
    /// "not writable" either: the connection is checked first, and a lost one throws.
    /// </summary>
    public bool IsWritable(string target)
    {
        if (IsWindows)
        {
            return _host.SftpCanWrite(target) != false;
        }
        if (_host.RunCommandChecked("test -w " + RemotePathUtils.ShellQuote(target)) != null)
        {
            return true;
        }
        // No answer is not "not writable": without a connection nothing is known.
        _host.ThrowIfConnectionLost();
        return false;
    }

    /// <summary>
    /// Identifies the version of the file on the server (modification time and size, or a checksum where the system
    /// has no stat) so that a change made by somebody else can be noticed. Null when it cannot be found out, or when
    /// there is no such file. Runs a command: call it from any thread but not often.
    /// </summary>
    public string FetchSignature()
    {
        try
        {
            if (IsWindows)
            {
                return _host.SftpSignature(_remotePath);
            }
            return RemoteFileSignature.Parse(_host.RunCommandChecked(RemoteFileSignature.BuildCommand(_remotePath)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The file on the server is not the one that was opened (or a new file has been created there meanwhile).
    /// Runs a command (any thread). Without a signature of the opened file nothing can be said: not a conflict.
    /// </summary>
    public bool HasChanged(string openedSignature, bool isNewFile)
    {
        string current = FetchSignature();
        if (current == null)
        {
            return false;
        }
        if (isNewFile)
        {
            return true;
        }
        return openedSignature != null && current != openedSignature;
    }

    /// <summary>The first sentence of the question asked when <see cref="HasChanged"/> says yes.</summary>
    public string ConflictDescription(bool isNewFile)
    {
        return isNewFile
            ? _remotePath + " has been created on the server since you started this new file."
            : _remotePath + " has been changed on the server since it was opened in the editor.";
    }
}
