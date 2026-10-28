using SmarTTY.Ssh;
using System;

namespace SmarTTY.GUI.Backend;

/// <summary>
/// What the file list needs of the tab it is next to: the SSH session, the directory of the tab and a way to change it. The
/// Smart Terminal is one (its directory follows the "cd" commands typed in it); a classic terminal tab of a Windows host is
/// another (<see cref="WindowsFileListHost"/>), because cmd.exe and PowerShell report no directory to the client.
/// </summary>
public interface IFileListHost
{
    SshSession Session { get; }

    /// <summary>The directory the list shows; null while it is not known yet.</summary>
    string CurrentDirectory { get; }

    /// <summary>Raised when the directory has changed (possibly on another thread).</summary>
    event Action<string> DirectoryChanged;

    /// <summary>Changes the directory to a path, absolute or relative to the current one. Throws when it cannot be entered.</summary>
    void ChangeDirectory(string path);
}
