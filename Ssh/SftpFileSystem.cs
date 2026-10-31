using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SmarTTY.Ssh;

/// <summary>
/// The file operations of the file list for a server that has no Unix shell to run cp, mv, rm and mkdir in (a Windows host):
/// all of them are SFTP requests. Works on one <see cref="SftpChannel"/>, so it is meant for one thread at a time. Paths are
/// the forward-slash paths of SFTP (see <see cref="WindowsSftpPath"/>).
/// </summary>
public sealed class SftpFileSystem
{
    private readonly SftpChannel _sftp;

    public SftpFileSystem(SftpChannel sftp)
    {
        _sftp = sftp ?? throw new ArgumentNullException(nameof(sftp));
    }

    /// <summary>The directory the server starts in: the profile of the user on Windows.</summary>
    public string HomeDirectory()
    {
        return _sftp.RealPath(".");
    }

    /// <summary>The entries of a directory without "." and "..".</summary>
    public List<SftpDirectoryEntry> List(string directory)
    {
        return _sftp.ListDirectory(WindowsSftpPath.ForServer(directory)).Where(entry => entry.Name != "." && entry.Name != "..").ToList();
    }

    /// <summary>
    /// The drives of a Windows host, as folders named "C:", "D:"... SFTP has no call for them (what "/" lists depends on the
    /// server), so each letter is asked for: a drive is there when its root can be examined. A and B (floppy disks) are
    /// not asked.
    /// </summary>
    public List<SftpDirectoryEntry> ListDrives()
    {
        List<SftpDirectoryEntry> drives = new List<SftpDirectoryEntry>();
        for (char letter = 'C'; letter <= 'Z'; letter++)
        {
            SftpFileAttributes attributes;
            try
            {
                attributes = _sftp.Stat("/" + letter + ":/");
            }
            catch (SftpStatusException)
            {
                // A drive that is not ready (an empty CD drive) or not allowed is not listed.
                continue;
            }
            if (attributes != null && attributes.IsDirectory)
            {
                drives.Add(new SftpDirectoryEntry { Name = letter + ":", Attributes = attributes });
            }
        }
        return drives;
    }

    /// <summary>
    /// "mkdir -p": creates the directory and the missing ones above it; nothing happens when it is there already. A file in the
    /// way is an error. The topmost thing that does not exist and cannot be created (a drive that is not there) fails with the
    /// status of the server.
    /// </summary>
    public void CreateDirectories(string path)
    {
        string trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        SftpFileAttributes attributes = _sftp.Stat(WindowsSftpPath.ForServer(trimmed));
        if (attributes != null)
        {
            if (!attributes.IsDirectory)
            {
                throw new RemoteOperationException(trimmed + " is not a directory.");
            }
            return;
        }
        int slash = trimmed.LastIndexOf('/');
        string parent = slash <= 0 ? "/" : trimmed.Substring(0, slash);
        if (parent != trimmed && parent != "/")
        {
            CreateDirectories(parent);
        }
        _sftp.MakeDirectory(WindowsSftpPath.ForServer(trimmed));
    }

    public void CreateDirectory(string path)
    {
        _sftp.MakeDirectory(WindowsSftpPath.ForServer(path));
    }

    /// <summary>Creates an empty file; fails when something of that name exists, as "Create file" must not overwrite.</summary>
    public void CreateFile(string path)
    {
        byte[] handle = _sftp.OpenFile(WindowsSftpPath.ForServer(path), SftpChannel.OpenFlags.Write | SftpChannel.OpenFlags.Create | SftpChannel.OpenFlags.Exclusive);
        _sftp.CloseHandle(handle);
    }

    /// <summary>
    /// Deletes a file, a link or a folder with everything in it; a path that is already gone is not an error. A link is
    /// removed itself and never followed. A folder is first tried with plain rmdir: that removes an empty folder and also a
    /// Windows junction or directory link (whose target must stay), and only a folder with something in it is emptied first.
    /// </summary>
    public void Delete(string path)
    {
        SftpFileAttributes attributes = _sftp.Lstat(path);
        if (attributes == null)
        {
            return;
        }
        if (!attributes.IsDirectory)
        {
            _sftp.Remove(path);
            return;
        }
        try
        {
            _sftp.RemoveDirectory(path);
            return;
        }
        catch (SftpStatusException)
        {
            // Not empty (or not allowed): empty it, and let the last rmdir tell the real reason if that fails too.
        }
        foreach (SftpDirectoryEntry entry in List(path))
        {
            Delete(Child(path, entry.Name));
        }
        _sftp.RemoveDirectory(path);
    }

    /// <summary>
    /// Moves a file or folder (SFTP rename: within one volume). The server refuses to replace an existing target, so with
    /// <paramref name="replace"/> the target is deleted first, as "mv" of the file list does after the user agreed.
    /// </summary>
    public void Move(string source, string destination, bool replace)
    {
        if (replace)
        {
            Delete(destination);
        }
        _sftp.Rename(source, destination);
    }

    /// <summary>
    /// Copies a file or a folder (recursively) to the exact path <paramref name="destination"/>. Links to files are copied as
    /// the files they point to; a link to a folder is refused, because a copy cannot tell it from a loop.
    /// </summary>
    public void Copy(string source, string destination, bool replace)
    {
        if (replace)
        {
            Delete(destination);
        }
        else if (_sftp.Lstat(destination) != null)
        {
            // Writing to the path would silently overwrite a file, and merge a folder with the one that is there.
            throw new SftpStatusException(SftpChannel.StatusFailure, destination + ": already exists");
        }
        CopyEntry(source, destination);
    }

    private void CopyEntry(string source, string destination)
    {
        SftpFileAttributes link = _sftp.Lstat(source);
        SftpFileAttributes attributes = _sftp.Stat(source) ?? throw new RemoteOperationException(source + ": No such file");
        if (attributes.IsDirectory)
        {
            if (link != null && link.IsSymbolicLink)
            {
                throw new RemoteOperationException(source + " is a link to a folder; links to folders are not copied.");
            }
            _sftp.MakeDirectory(WindowsSftpPath.ForServer(destination), attributes.Mode == null ? (int?)null : attributes.Mode.Value & 0xFFF);
            foreach (SftpDirectoryEntry entry in List(source))
            {
                CopyEntry(Child(source, entry.Name), Child(destination, entry.Name));
            }
            return;
        }
        using Stream input = _sftp.OpenRead(source, 0);
        using Stream output = _sftp.OpenWrite(destination, 0, truncate: true, mode: attributes.Mode == null ? 0x1A4 : attributes.Mode.Value & 0xFFF);
        input.CopyTo(output);
    }

    /// <summary>
    /// What each path is, like <see cref="SshSession.GetRemotePathKinds"/> does with "test" for a Unix host: a path whose
    /// status the server refuses to give is <see cref="RemotePathKind.Unknown"/>, not missing.
    /// </summary>
    public static RemotePathKind[] PathKinds(SftpChannel sftp, IList<string> paths)
    {
        RemotePathKind[] kinds = new RemotePathKind[paths.Count];
        for (int index = 0; index < paths.Count; index++)
        {
            try
            {
                SftpFileAttributes attributes = sftp.Stat(WindowsSftpPath.ForServer(paths[index]));
                kinds[index] = attributes == null ? RemotePathKind.Missing : (attributes.IsDirectory ? RemotePathKind.Directory : RemotePathKind.File);
            }
            catch (SftpStatusException)
            {
                kinds[index] = RemotePathKind.Unknown;
            }
        }
        return kinds;
    }

    /// <summary>The path of <paramref name="name"/> in <paramref name="directory"/>.</summary>
    public static string Child(string directory, string name)
    {
        return directory.TrimEnd('/') + "/" + name;
    }
}
