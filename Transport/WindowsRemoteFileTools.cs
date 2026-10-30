using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SmarTTY.Transport;

/// <summary>
/// What <see cref="RemoteFileTools"/> does with ls, mv, rm, chmod and sha256sum, for a Windows host: over SFTP, and with
/// certutil for the checksum. The file transfer manager asks about the file it is going to replace, replaces it, removes
/// what a failed upload left, and compares checksums with these.
/// </summary>
internal static class WindowsRemoteFileTools
{
    private static readonly Regex HexLine = new Regex("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// Describes a remote path like <see cref="RemoteFileTools.GetInfo"/>: it does not exist (the answer is still not null),
    /// or it does, as a link or as itself. The owner and the group are not known over SFTP, and the permission bits of a
    /// Windows file say nothing, so they are only passed on.
    /// </summary>
    public static RemoteFileInfo GetInfo(SftpChannel sftp, string path)
    {
        SftpFileAttributes link = sftp.Lstat(path);
        if (link == null)
        {
            return new RemoteFileInfo();
        }
        SftpFileAttributes target = link.IsSymbolicLink ? sftp.Stat(path) : link;
        return new RemoteFileInfo
        {
            Exists = true,
            IsSymbolicLink = link.IsSymbolicLink,
            IsDirectory = target != null && target.IsDirectory,
            Size = target?.Size ?? 0,
            Permissions = (target?.Mode ?? 0) & 0xFFF
        };
    }

    /// <summary>
    /// Puts <paramref name="source"/> in place of <paramref name="target"/>. SFTP version 3 does not rename over an existing
    /// file, so the old file is first moved aside, and put back if the rename fails: the target is never left missing.
    /// </summary>
    public static void Replace(SftpChannel sftp, string source, string target)
    {
        string backup = null;
        if (sftp.Lstat(target) != null)
        {
            backup = target + ".smartty-old";
            if (sftp.Lstat(backup) != null)
            {
                sftp.Remove(backup);
            }
            sftp.Rename(target, backup);
        }
        try
        {
            sftp.Rename(source, target);
        }
        catch
        {
            if (backup != null)
            {
                try
                {
                    sftp.Rename(backup, target);
                }
                catch
                {
                    // The old content stays in the backup file; the original error is the one to report.
                }
            }
            throw;
        }
        if (backup != null)
        {
            try
            {
                sftp.Remove(backup);
            }
            catch
            {
                // A backup that cannot be removed is a leftover, not a failure of the upload.
            }
        }
    }

    /// <summary>Removes a file; a path that is not there is not an error ("rm -f").</summary>
    public static void Remove(SftpChannel sftp, string path)
    {
        if (sftp.Lstat(path) != null)
        {
            sftp.Remove(path);
        }
    }

    /// <summary>
    /// The command that prints the SHA-256 of a file on Windows; null for a path without a drive. certutil is part of Windows
    /// and a program, so cmd.exe and PowerShell run it alike; the path is in double quotes.
    /// </summary>
    public static string ChecksumCommand(string sftpPath)
    {
        string windowsPath = WindowsSftpPath.ToWindows(sftpPath);
        return windowsPath == null ? null : "certutil -hashfile \"" + windowsPath + "\" SHA256";
    }

    /// <summary>
    /// The checksum in the output of certutil -hashfile. The words around it are translated, so the line is found by what it
    /// is: 64 hexadecimal digits (older versions put a space between each two of them).
    /// </summary>
    public static FileChecksum ParseChecksum(IEnumerable<string> output)
    {
        foreach (string line in output ?? new string[0])
        {
            string digits = (line ?? "").Replace(" ", "").Trim();
            if (HexLine.IsMatch(digits))
            {
                return new FileChecksum("SHA-256", digits.ToLowerInvariant());
            }
        }
        return null;
    }
}
