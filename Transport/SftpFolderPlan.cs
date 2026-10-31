using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.IO;

namespace SmarTTY.Transport;

/// <summary>One file of a folder transfer.</summary>
internal sealed class FolderFile
{
    public string Source;

    public string Destination;

    public long Size;
}

/// <summary>What a folder transfer consists of: the folders to create on the receiving side and the files to send.</summary>
internal sealed class FolderPlan
{
    /// <summary>The folders to create on the receiving side, the folder itself first and every folder after the one it is in.</summary>
    public readonly List<string> Directories = new List<string>();

    public readonly List<FolderFile> Files = new List<FolderFile>();

    /// <summary>What is not transferred, with the reason (links, folders that are links, things that are no files).</summary>
    public readonly List<string> Skipped = new List<string>();

    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (FolderFile file in Files)
            {
                total += file.Size;
            }
            return total;
        }
    }
}

/// <summary>
/// Plans the transfer of a whole folder to or from a Windows host, where tar and the file-by-file synchronisation (which ask the
/// host for find and stat) cannot be used: the tree is walked over SFTP (or on the local disk), and each file then goes through
/// the usual transfer queue. Nothing here transfers anything.
/// </summary>
internal static class SftpFolderPlan
{
    /// <summary>
    /// More entries than this means that the walk is going round in a loop: a junction of Windows (the "Application Data" of an old
    /// profile points to the folder it is in) cannot be told from a folder over SFTP, and every round would list the whole tree again.
    /// </summary>
    public const int MaxEntries = 100000;

    public const int MaxDepth = 64;

    /// <summary>A file name of the host as a local file name: what Windows does not allow in one is replaced.</summary>
    public static string LocalName(string remoteName)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            remoteName = remoteName.Replace(invalid, '_');
        }
        return remoteName;
    }

    /// <summary>
    /// The plan for downloading <paramref name="remoteDirectory"/> into <paramref name="localDirectory"/> (the folder itself, not
    /// what is above it). A link to a file is downloaded as the file; a link to a folder is skipped, like in copying.
    /// </summary>
    public static FolderPlan PlanDownload(SftpChannel sftp, string remoteDirectory, string localDirectory)
    {
        FolderPlan plan = new FolderPlan();
        plan.Directories.Add(localDirectory);
        int entries = 0;
        DownloadFolder(new SftpFileSystem(sftp), sftp, remoteDirectory, localDirectory, plan, 0, ref entries);
        return plan;
    }

    private static void DownloadFolder(SftpFileSystem fileSystem, SftpChannel sftp, string remote, string local, FolderPlan plan, int depth, ref int entries)
    {
        if (depth > MaxDepth)
        {
            throw new RemoteOperationException("The folder is nested deeper than " + MaxDepth + " levels at " + remote + ": it probably has a link (a junction) that leads back to a folder above it.");
        }
        foreach (SftpDirectoryEntry entry in fileSystem.List(remote))
        {
            if (++entries > MaxEntries)
            {
                throw new RemoteOperationException("More than " + MaxEntries + " files and folders were found: the folder probably has a link (a junction) that leads back to a folder above it. Download the folders inside it one by one.");
            }
            string remotePath = SftpFileSystem.Child(remote, entry.Name);
            string localPath = Path.Combine(local, LocalName(entry.Name));
            SftpFileAttributes attributes = entry.Attributes;
            if (attributes.IsSymbolicLink)
            {
                SftpFileAttributes target = sftp.Stat(remotePath);
                if (target == null || target.IsDirectory)
                {
                    plan.Skipped.Add(remotePath + " (a link to " + (target == null ? "nothing" : "a folder") + ")");
                    continue;
                }
                attributes = target;
            }
            if (attributes.IsDirectory)
            {
                plan.Directories.Add(localPath);
                DownloadFolder(fileSystem, sftp, remotePath, localPath, plan, depth + 1, ref entries);
            }
            else if (attributes.IsRegularFile || attributes.Mode == null)
            {
                plan.Files.Add(new FolderFile { Source = remotePath, Destination = localPath, Size = attributes.Size ?? 0 });
            }
            else
            {
                plan.Skipped.Add(remotePath + " (not a regular file)");
            }
        }
    }

    /// <summary>
    /// The plan for uploading <paramref name="localDirectory"/> into <paramref name="remoteDirectory"/> (the folder itself).
    /// A local folder that is a link or a junction is skipped, so that a loop on the local disk cannot make the walk endless.
    /// </summary>
    public static FolderPlan PlanUpload(string localDirectory, string remoteDirectory)
    {
        FolderPlan plan = new FolderPlan();
        plan.Directories.Add(remoteDirectory);
        int entries = 0;
        UploadFolder(localDirectory, remoteDirectory, plan, 0, ref entries);
        return plan;
    }

    private static void UploadFolder(string local, string remote, FolderPlan plan, int depth, ref int entries)
    {
        if (depth > MaxDepth)
        {
            throw new IOException("The folder is nested deeper than " + MaxDepth + " levels at " + local + ".");
        }
        foreach (string path in Directory.EnumerateFileSystemEntries(local))
        {
            if (++entries > MaxEntries)
            {
                throw new IOException("More than " + MaxEntries + " files and folders were found in " + local + ". Upload the folders inside it one by one.");
            }
            string name = Path.GetFileName(path);
            string remotePath = SftpFileSystem.Child(remote, name);
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    plan.Skipped.Add(path + " (a folder that is a link)");
                    continue;
                }
                plan.Directories.Add(remotePath);
                UploadFolder(path, remotePath, plan, depth + 1, ref entries);
            }
            else
            {
                plan.Files.Add(new FolderFile { Source = path, Destination = remotePath, Size = new FileInfo(path).Length });
            }
        }
    }
}
