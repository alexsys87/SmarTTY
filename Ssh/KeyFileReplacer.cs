using System;

namespace SmarTTY.Ssh;

/// <summary>What <see cref="KeyFileReplacer"/> needs of the server: files, as SFTP gives them.</summary>
internal interface IReplaceableFiles
{
    /// <summary>The server renames over an existing file in one step (posix-rename@openssh.com).</summary>
    bool SupportsAtomicReplace { get; }

    /// <summary>Creates the file (closed to everybody but its owner, where the server has such a thing) and writes the content; an existing file is cut to nothing first.</summary>
    void Write(string path, byte[] content);

    /// <summary>The size of the file, or null when there is none.</summary>
    long? SizeOf(string path);

    bool Exists(string path);

    void Remove(string path);

    /// <summary>SSH_FXP_RENAME: refuses when the target exists.</summary>
    void Rename(string from, string to);

    /// <summary>The rename that replaces the target in one step; only when <see cref="SupportsAtomicReplace"/>.</summary>
    void ReplaceByRename(string from, string to);
}

/// <summary>
/// Replaces a key file on a Windows host so that the server is never left without a working file for longer than it must be.
/// The key file of the administrators is the one file for all of them (sshd refuses it unless only Administrators and SYSTEM have
/// access): while it is missing, nobody of them can log in with a key, and where the password is off, nobody can log in at all.
///
/// The new content is written beside the file, and the work that has to be done on it (closing the access to it) is done there,
/// before it takes the name: a file that is renamed keeps its access rights, so it is right from its first moment under the name.
/// Then it replaces the old file: in one step where the server has posix-rename (nothing is missing at any moment); otherwise
/// the old file is moved aside first and is kept until the new one is in place, and put back if that fails. A connection that is
/// lost between those two renames leaves the old content in "&lt;file&gt;.smartty-old", which the next call puts back
/// (<see cref="RecoverInterrupted"/>) before it reads the file.
/// </summary>
internal static class KeyFileReplacer
{
    public const string TemporarySuffix = ".smartty";

    public const string BackupSuffix = ".smartty-old";

    /// <summary>
    /// Puts the old content back when an earlier replacement was interrupted between the two renames (the file is missing and
    /// its copy is there). Returns whether it did. Must run before the content of the file is read: a missing file reads as an
    /// empty one, and the keys in the copy would be lost.
    /// </summary>
    public static bool RecoverInterrupted(IReplaceableFiles files, string path)
    {
        if (files.Exists(path) || !files.Exists(path + BackupSuffix))
        {
            return false;
        }
        files.Rename(path + BackupSuffix, path);
        return true;
    }

    /// <param name="prepare">
    /// Called with the path of the new file, before it takes the place of the old one, when there is something to do to it
    /// (closing the access to it with icacls). If it fails, the old file is not touched and the new one is removed.
    /// </param>
    public static void Replace(IReplaceableFiles files, string path, byte[] content, Action<string> prepare)
    {
        string temporary = path + TemporarySuffix;
        string backup = path + BackupSuffix;
        RecoverInterrupted(files, path);

        files.Write(temporary, content);
        long? size = files.SizeOf(temporary);
        if (size != content.Length)
        {
            TryRemove(files, temporary);
            throw new RemoteOperationException("Only " + (size ?? 0) + " of " + content.Length + " bytes of " + path + " were written; the file was not changed.");
        }
        if (prepare != null)
        {
            try
            {
                prepare(temporary);
            }
            catch (Exception exception) when (!(exception is System.IO.IOException))
            {
                // The old file is as it was; the new one is of no use and is not left behind.
                TryRemove(files, temporary);
                throw;
            }
        }

        if (!files.Exists(path))
        {
            PutInPlace(files, temporary, path, null);
            return;
        }
        if (files.SupportsAtomicReplace)
        {
            try
            {
                files.ReplaceByRename(temporary, path);
                return;
            }
            catch (Exception exception) when (!(exception is System.IO.IOException))
            {
                // The server announced the extension and refused it: nothing has changed, the two steps below do the same.
            }
        }

        // A copy that a replacement left behind after it had succeeded (it could not remove it): the file is there, so it is of no use.
        if (files.Exists(backup))
        {
            TryRemove(files, backup);
        }
        try
        {
            files.Rename(path, backup);
        }
        catch (Exception exception) when (!(exception is System.IO.IOException))
        {
            TryRemove(files, temporary);
            throw new RemoteOperationException("Cannot move the old " + path + " aside: " + exception.Message + " The file was not changed.");
        }
        PutInPlace(files, temporary, path, backup);
        TryRemove(files, backup);
    }

    private static void PutInPlace(IReplaceableFiles files, string temporary, string path, string backup)
    {
        try
        {
            files.Rename(temporary, path);
        }
        catch (Exception exception) when (!(exception is System.IO.IOException))
        {
            string kept = "The new content is in " + temporary + ".";
            if (backup != null)
            {
                kept = (TryRename(files, backup, path) ? "The old content is back in place. " : "The old content is in " + backup + ". ") + kept;
            }
            throw new RemoteOperationException("Cannot put the new " + path + " in place: " + exception.Message + " " + kept);
        }
    }

    private static void TryRemove(IReplaceableFiles files, string path)
    {
        try
        {
            files.Remove(path);
        }
        catch
        {
        }
    }

    private static bool TryRename(IReplaceableFiles files, string from, string to)
    {
        try
        {
            files.Rename(from, to);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
