using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SmarTTY.Transport;

/// <summary>What the restore window asks the server once it opens.</summary>
public sealed class RemoteRestoreHostInfo
{
    /// <summary>True for root, false for another user, null when <c>id -u</c> did not answer.</summary>
    public bool? IsRoot { get; set; }

    /// <summary>The user may run commands with <c>sudo -n</c>.</summary>
    public bool CanSudo { get; set; }

    /// <summary>tar on the server can unpack gzip data itself (-z).</summary>
    public bool TarHasGzip { get; set; }

    /// <summary>md5sum exists, so that the restored files can be checked.</summary>
    public bool HasMd5sum { get; set; }
}

/// <summary>
/// Tools > Restore from tar.gz: the commands that unpack an archive made by Tools > Backup to tar.gz (or any tar.gz) on the server,
/// the reverse of <see cref="RemoteBackup"/>. The archive is read on this computer, reduced to the members checked in the tree and
/// sent to the standard input of <c>tar -x</c> through the SSH channel, so the archive itself is never written on the server, only the
/// files it holds. Afterwards md5sum on the server is compared with the MD5 of every restored file computed here.
/// Pure logic without WPF or SSH, tested by tests/RemoteBackupTests.
/// </summary>
public static class RemoteRestore
{
    private const string UidMarker = "SMARTTY_UID=";

    private const string SudoMarker = "SMARTTY_SUDO=yes";

    private const string GzipMarker = "SMARTTY_TARZ=yes";

    private const string Md5Marker = "SMARTTY_MD5=yes";

    /// <summary>
    /// Prints the uid, whether passwordless sudo works, whether tar handles gzip (an archive of /dev/null, which stores the device
    /// node and reads nothing, is made and listed again with -z) and whether md5sum exists.
    /// </summary>
    public const string HostInfoCommand =
        "echo " + UidMarker + "$(id -u 2>/dev/null); " +
        "if [ \"$(id -u 2>/dev/null)\" != 0 ] && command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then echo " + SudoMarker + "; fi; " +
        "if tar -czf - /dev/null 2>/dev/null | tar -tzf - >/dev/null 2>&1; then echo " + GzipMarker + "; fi; " +
        "if command -v md5sum >/dev/null 2>&1; then echo " + Md5Marker + "; fi";

    public static RemoteRestoreHostInfo ParseHostInfo(IEnumerable<string> lines)
    {
        RemoteRestoreHostInfo info = new RemoteRestoreHostInfo();
        foreach (string rawLine in lines ?? Enumerable.Empty<string>())
        {
            string line = rawLine?.Trim();
            if (line == null)
            {
                continue;
            }
            if (line.StartsWith(UidMarker, StringComparison.Ordinal))
            {
                string uid = line.Substring(UidMarker.Length);
                if (uid.Length > 0 && uid.All(char.IsDigit))
                {
                    info.IsRoot = uid == "0";
                }
            }
            else if (line == SudoMarker)
            {
                info.CanSudo = true;
            }
            else if (line == GzipMarker)
            {
                info.TarHasGzip = true;
            }
            else if (line == Md5Marker)
            {
                info.HasMd5sum = true;
            }
        }
        return info;
    }

    /// <summary>"/srv/restore/" is "/srv/restore"; an empty or relative folder is refused, since the archive holds paths relative to /.</summary>
    public static string NormalizeTarget(string target)
    {
        string trimmed = (target ?? "").Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("The folder to restore to must be an absolute path of the server, such as / or /mnt/restore.");
        }
        return RemoteBackup.NormalizeDirectory(trimmed);
    }

    /// <summary>
    /// The command line that unpacks the tar stream of its standard input into <paramref name="target"/> (made if it does not exist):
    /// <c>tar -xzvf -</c>. -v makes tar print every member to its standard output (the archive comes from stdin, so stdout is free),
    /// which the window shows as progress. With <paramref name="keepExisting"/> files that exist already are left alone (-k).
    /// </summary>
    public static string BuildExtractCommand(string target, bool gzip, bool keepExisting, bool useSudo)
    {
        string folder = NormalizeTarget(target);
        string script = (folder == "/" ? "" : "mkdir -p " + RemotePathUtils.ShellQuote(folder) + " && ") +
            "cd " + RemotePathUtils.ShellQuote(folder) + " && LC_ALL=C tar -x" + (gzip ? "z" : "") + "v" + (keepExisting ? "k" : "") + "f -";
        return WrapForUser(script, useSudo);
    }

    /// <summary>
    /// The command line that reads relative paths from its standard input, one per line, and prints for each the MD5 of the file in
    /// <paramref name="target"/>, or "-" when it is not a regular file there. The answers come in the order of the paths, so the
    /// names never have to be read back from the output of md5sum (which escapes some of them).
    /// </summary>
    public static string BuildVerifyCommand(string target, bool useSudo)
    {
        string folder = NormalizeTarget(target);
        string script = "cd " + RemotePathUtils.ShellQuote(folder) + " || exit 2; " +
            "while IFS= read -r f; do if [ -f \"$f\" ] && [ ! -L \"$f\" ]; then set -- $(md5sum < \"$f\" 2>/dev/null); echo \"${1:--}\"; else echo -; fi; done";
        return WrapForUser(script, useSudo);
    }

    /// <summary>The files whose MD5 on the server differs from the one computed here: the name and why.</summary>
    public static List<string> FindMismatches(IList<TarArchiveReader.FilteredFile> files, IList<string> answers)
    {
        List<string> problems = new List<string>();
        List<string> hashes = (answers ?? new List<string>()).Select(a => a?.Trim()).Where(a => !string.IsNullOrEmpty(a)).ToList();
        for (int i = 0; i < files.Count; i++)
        {
            string answer = i < hashes.Count ? hashes[i] : null;
            if (answer == null)
            {
                problems.Add(files[i].Name + ": no answer from the server");
            }
            else if (answer == "-")
            {
                problems.Add(files[i].Name + ": not found after the restore");
            }
            else if (!string.Equals(answer, files[i].Md5, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(files[i].Name + ": the content differs from the archive");
            }
        }
        return problems;
    }

    /// <summary>
    /// The command line that prints the free space of the file system of <paramref name="target"/> (of its nearest existing parent
    /// while it does not exist yet) in POSIX form: <c>df -P -k</c>, which GNU and BusyBox df both know.
    /// </summary>
    public static string BuildFreeSpaceCommand(string target)
    {
        string folder = NormalizeTarget(target);
        return SmarTTY.Utilities.PosixShell.Wrap("d=" + RemotePathUtils.ShellQuote(folder) + "; while [ ! -d \"$d\" ]; do d=$(dirname \"$d\"); done; LC_ALL=C df -P -k \"$d\"");
    }

    /// <summary>The available space in bytes from the output of <c>df -P -k</c>; null when it cannot be read.</summary>
    public static long? ParseFreeSpace(IEnumerable<string> lines)
    {
        string last = (lines ?? Enumerable.Empty<string>()).Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("Filesystem", StringComparison.Ordinal)).LastOrDefault();
        if (last == null)
        {
            return null;
        }
        string[] fields = last.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        // "Filesystem 1024-blocks Used Available Capacity Mounted-on": the fourth field, unless the name of the file system holds
        // spaces; then the one before the capacity ("23%").
        if (fields.Length < 6)
        {
            return null;
        }
        int capacity = Array.FindIndex(fields, 1, f => f.EndsWith("%", StringComparison.Ordinal));
        string available = fields.Length >= 4 && fields[4].EndsWith("%", StringComparison.Ordinal) ? fields[3] : capacity > 1 ? fields[capacity - 1] : null;
        return long.TryParse(available, NumberStyles.None, CultureInfo.InvariantCulture, out long kib) ? kib * 1024 : (long?)null;
    }

    /// <summary>
    /// The test for the members to restore: <paramref name="members"/> are the checked paths of the tree (absolute, "/etc/config");
    /// a member is restored when it is one of them or lies inside one, and a folder is restored as well when a checked path lies
    /// inside it, so that the folders keep their owner and permissions.
    /// </summary>
    public static Func<TarEntry, bool> MakeSelection(IEnumerable<string> members)
    {
        HashSet<string> selected = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> ancestors = new HashSet<string>(StringComparer.Ordinal);
        foreach (string member in members ?? Enumerable.Empty<string>())
        {
            string relative = TarArchiveReader.NormalizeName(member);
            if (relative.Length == 0)
            {
                continue;
            }
            selected.Add(relative);
            int slash = relative.LastIndexOf('/');
            while (slash > 0)
            {
                relative = relative.Substring(0, slash);
                ancestors.Add(relative);
                slash = relative.LastIndexOf('/');
            }
        }
        return entry =>
        {
            string name = entry.Name;
            if (name.Length == 0)
            {
                // The "./" member of an archive made with "tar -C / .": the root folder itself is left as it is.
                return false;
            }
            if (selected.Contains(name))
            {
                return true;
            }
            if (entry.Type == TarEntryType.Directory && ancestors.Contains(name))
            {
                return true;
            }
            int slash = name.LastIndexOf('/');
            while (slash > 0)
            {
                name = name.Substring(0, slash);
                if (selected.Contains(name))
                {
                    return true;
                }
                slash = name.LastIndexOf('/');
            }
            return false;
        };
    }

    /// <summary>
    /// Fills a tree of check boxes with the members of an archive (folders that only appear in the paths are added), everything
    /// checked. Files carry their size as the note.
    /// </summary>
    public static BackupNode BuildTree(IEnumerable<TarEntry> entries, Func<long, string> formatSize)
    {
        Folder root = new Folder();
        foreach (TarEntry entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }
            string[] parts = entry.Name.Split('/');
            Folder folder = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                folder = folder.GetFolder(parts[i]);
            }
            string last = parts[parts.Length - 1];
            if (entry.Type == TarEntryType.Directory)
            {
                folder.GetFolder(last);
            }
            else if (!folder.Folders.ContainsKey(last))
            {
                folder.Files[last] = entry;
            }
        }
        BackupNode node = BackupNode.CreateRoot();
        Apply(node, root, formatSize);
        return node;
    }

    private static void Apply(BackupNode node, Folder folder, Func<long, string> formatSize)
    {
        List<BackupNode.ChildSpec> specs = new List<BackupNode.ChildSpec>();
        foreach (string name in folder.Folders.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            specs.Add(new BackupNode.ChildSpec { Entry = new RemoteEntry(name, RemoteEntryKind.Directory), Role = BackupMountRole.Ordinary });
        }
        foreach (KeyValuePair<string, TarEntry> file in folder.Files.Where(f => !folder.Folders.ContainsKey(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            TarEntry entry = file.Value;
            string note = entry.Type == TarEntryType.SymbolicLink ? "link to " + entry.LinkName
                : entry.Type == TarEntryType.HardLink ? "hard link to " + entry.LinkName
                : entry.Type == TarEntryType.File ? formatSize?.Invoke(entry.Size) : null;
            specs.Add(new BackupNode.ChildSpec
            {
                Entry = new RemoteEntry(file.Key, entry.Type == TarEntryType.SymbolicLink ? RemoteEntryKind.SymbolicLink : RemoteEntryKind.File),
                Role = BackupMountRole.Ordinary,
                Note = note
            });
        }
        node.ApplyChildren(specs);
        foreach (BackupNode child in node.Children)
        {
            if (child.IsDirectory)
            {
                Apply(child, folder.Folders[child.Name], formatSize);
            }
        }
    }

    private sealed class Folder
    {
        public readonly Dictionary<string, Folder> Folders = new Dictionary<string, Folder>(StringComparer.Ordinal);

        public readonly Dictionary<string, TarEntry> Files = new Dictionary<string, TarEntry>(StringComparer.Ordinal);

        public Folder GetFolder(string name)
        {
            if (!Folders.TryGetValue(name, out Folder folder))
            {
                folder = new Folder();
                Folders[name] = folder;
            }
            return folder;
        }
    }

    private static string WrapForUser(string script, bool useSudo)
    {
        string command = SmarTTY.Utilities.PosixShell.Wrap(script, addAdminDirectoriesToPath: true);
        return useSudo ? "sudo -n " + command : command;
    }
}
