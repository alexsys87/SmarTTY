using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace SmarTTY.IO;

/// <summary>
/// The "ls" that the file and folder dialogs run on the host (open a remote file, download a file, upload to a folder, pick a
/// remote folder), for a host that has no ls: a Windows host. They use three forms, "ls -1dLF path" (what is this path), "ls -1ALF
/// dir" and "ls -1pLA dir" (the entries of a directory, with a slash after a folder), and read the lines and the exit code as
/// if ls had written them, so this answers in the same words over SFTP. A link counts as what it points to, as with -L.
/// "/" lists the drives. Runs in the background, like a command on a channel.
/// </summary>
internal sealed class SftpListingCommand : IRemoteCommand
{
    private static readonly Regex Arguments = new Regex("^(?<flags>-\\S+)\\s+\"(?<path>.*)\"\\s*$", RegexOptions.Compiled);

    private readonly ManualResetEvent _done = new ManualResetEvent(false);

    private List<string> _lines = new List<string>();

    private string _error;

    private int _exitCode;

    public SftpListingCommand(SshSession session, string arguments)
    {
        ThreadPool.QueueUserWorkItem(state => Run(session, arguments));
    }

    public int ExitCode => _exitCode;

    public List<string> OutputLines => new List<string>(_lines);

    public string ErrorText => _error;

    public bool WaitForExit(int milliseconds)
    {
        return _done.WaitOne(milliseconds);
    }

    public void SendInput(string text)
    {
    }

    public void Dispose()
    {
        // The event is left to the garbage collector: the background thread may still be about to set it.
    }

    private void Run(SshSession session, string arguments)
    {
        string path = arguments ?? "";
        try
        {
            Match match = Arguments.Match(path);
            if (!match.Success)
            {
                throw new RemoteOperationException("the arguments are not understood: " + path);
            }
            path = match.Groups["path"].Value;
            using SftpChannel sftp = SftpChannel.Open(session);
            _lines = match.Groups["flags"].Value.IndexOf('d') >= 0 ? Describe(sftp, path) : List(sftp, path);
        }
        catch (SftpStatusException exception)
        {
            _exitCode = 2;
            _error = "ls: cannot access '" + path + "': " + Reason(exception);
        }
        catch (Exception exception)
        {
            _exitCode = 2;
            _error = "ls: " + exception.Message;
        }
        finally
        {
            try
            {
                _done.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static string Reason(SftpStatusException exception)
    {
        switch (exception.Status)
        {
            case SftpChannel.StatusNoSuchFile:
                return "No such file or directory";
            case SftpChannel.StatusPermissionDenied:
                return "Permission denied";
            default:
                return exception.Message;
        }
    }

    /// <summary>"ls -d": the path itself, with a slash after it when it is a folder.</summary>
    private static List<string> Describe(SftpChannel sftp, string path)
    {
        if (WindowsSftpPath.IsRoot(path))
        {
            return new List<string> { "/" };
        }
        SftpFileAttributes attributes = sftp.Stat(WindowsSftpPath.ForServer(path)) ?? throw new SftpStatusException(SftpChannel.StatusNoSuchFile, path);
        return new List<string> { attributes.IsDirectory ? path.TrimEnd('/') + "/" : path };
    }

    /// <summary>"ls" of a directory: the names, a folder with a slash after it.</summary>
    private static List<string> List(SftpChannel sftp, string path)
    {
        SftpFileSystem fileSystem = new SftpFileSystem(sftp);
        bool root = WindowsSftpPath.IsRoot(path);
        List<SftpDirectoryEntry> entries = root ? fileSystem.ListDrives() : fileSystem.List(path);
        if (root && entries.Count == 0)
        {
            // No drive was found: the server may list "/" itself.
            entries = fileSystem.List(path);
        }
        List<string> lines = new List<string>();
        foreach (SftpDirectoryEntry entry in entries)
        {
            SftpFileAttributes attributes = entry.Attributes;
            if (attributes.IsSymbolicLink)
            {
                attributes = sftp.Stat(WindowsSftpPath.ForServer(SftpFileSystem.Child(path, entry.Name))) ?? attributes;
            }
            lines.Add(attributes.IsDirectory ? entry.Name + "/" : entry.Name);
        }
        return lines;
    }
}
