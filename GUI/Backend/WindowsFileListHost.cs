using SmarTTY.Ssh;
using System;
using System.Threading;

namespace SmarTTY.GUI.Backend;

/// <summary>
/// The directory of the file list of a classic terminal tab on a Windows host. The shell there (cmd.exe, PowerShell) tells the
/// client nothing about its directory, so the list keeps its own: it starts in the profile of the user, where the SFTP
/// server starts, and moves when the user opens a folder or goes up. It does not follow a "cd" typed in the terminal.
/// </summary>
public sealed class WindowsFileListHost : IFileListHost
{
    private readonly Func<SshSession> _sessionProvider;

    private string _currentDirectory;

    public WindowsFileListHost(Func<SshSession> sessionProvider)
    {
        _sessionProvider = sessionProvider;
    }

    /// <summary>The current session of the tab: the connection may be replaced by a reconnect.</summary>
    public SshSession Session => _sessionProvider();

    public string CurrentDirectory => _currentDirectory;

    public event Action<string> DirectoryChanged;

    /// <summary>
    /// Finds the start directory in the background and announces it. When the server cannot say (no SFTP), the list starts at
    /// the root, where it shows the reason of the failure instead of waiting for a directory that never comes.
    /// </summary>
    public void Start()
    {
        new Thread(() =>
        {
            string directory = "/";
            try
            {
                using SftpChannel sftp = SftpChannel.Open(Session);
                directory = WindowsSftpPath.Normalize(new SftpFileSystem(sftp).HomeDirectory());
            }
            catch
            {
            }
            SetCurrentDirectory(directory);
        })
        {
            IsBackground = true
        }.Start();
    }

    public void ChangeDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        string target = WindowsSftpPath.Combine(_currentDirectory ?? "/", path);
        if (!WindowsSftpPath.IsRoot(target))
        {
            using SftpChannel sftp = SftpChannel.Open(Session);
            SftpFileAttributes attributes = sftp.Stat(WindowsSftpPath.ForServer(target));
            if (attributes == null)
            {
                throw new RemoteOperationException(target + ": No such directory");
            }
            if (!attributes.IsDirectory)
            {
                throw new RemoteOperationException(target + " is not a directory");
            }
        }
        SetCurrentDirectory(target);
    }

    private void SetCurrentDirectory(string directory)
    {
        if (directory == _currentDirectory)
        {
            return;
        }
        _currentDirectory = directory;
        DirectoryChanged?.Invoke(directory);
    }
}
