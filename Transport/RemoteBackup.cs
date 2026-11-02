using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SmarTTY.Transport;

/// <summary>The kind of an entry of a remote directory listing.</summary>
public enum RemoteEntryKind
{
    Directory,

    File,

    /// <summary>A symbolic link: tar stores the link itself, so it is never expanded.</summary>
    SymbolicLink
}

/// <summary>An entry of <see cref="RemoteBackup.ListCommand"/>: the name inside its directory and what it is.</summary>
public readonly struct RemoteEntry
{
    public string Name { get; }

    public RemoteEntryKind Kind { get; }

    public RemoteEntry(string name, RemoteEntryKind kind)
    {
        Name = name;
        Kind = kind;
    }

    public override string ToString()
    {
        return Kind + " " + Name;
    }
}

/// <summary>How the backup treats a mount point of the server.</summary>
public enum BackupMountRole
{
    /// <summary>A disk or flash file system: part of the system, archived when selected.</summary>
    Ordinary,

    /// <summary>Memory, device or network file systems (tmpfs, devtmpfs, nfs) and image mounts: not selected by default, but may be.</summary>
    SkippedByDefault,

    /// <summary>A virtual file system of the kernel (proc, sysfs, cgroup): reading it as files never ends or makes no sense, so it is never archived.</summary>
    NeverArchived
}

/// <summary>A line of /proc/mounts: where a file system is mounted and its type.</summary>
public sealed class RemoteMount
{
    public string Path { get; }

    public string Type { get; }

    public RemoteMount(string path, string type)
    {
        Path = path;
        Type = type;
    }
}

/// <summary>What the backup window asks the server once it opens: who the user is and which file systems are mounted where.</summary>
public sealed class RemoteBackupHostInfo
{
    /// <summary>True for root (uid 0), false for another user, null when <c>id -u</c> did not answer.</summary>
    public bool? IsRoot { get; set; }

    /// <summary>The user may run commands with <c>sudo -n</c> (sudo exists and asks no password).</summary>
    public bool CanSudo { get; set; }

    /// <summary>The mount table (empty on a host without /proc/mounts, such as BSD).</summary>
    public List<RemoteMount> Mounts { get; } = new List<RemoteMount>();

    /// <summary>The root file system is an overlay (OpenWrt: the read-only /rom with the writable /overlay on top).</summary>
    public bool RootIsOverlay => Mounts.Any(mount => mount.Path == "/" && (mount.Type == "overlay" || mount.Type == "overlayfs"));
}

/// <summary>
/// Tools > Backup to tar.gz: the shell commands that the backup of the root file system (or of the folders selected in it) runs on
/// the server, and the reading of what they print. Everything is POSIX shell and tar, the tools that every Linux has, BusyBox on
/// OpenWrt included: tar writes the archive to its standard output, which the SSH channel carries straight into the file on this
/// computer, so the archive is never written on the server - not even to /tmp, which is often a small RAM disk there.
/// gzip support of tar (-z) is optional: without it the plain tar stream is compressed on this computer.
/// Pure logic without WPF or SSH, tested by tests/RemoteBackupTests.
/// </summary>
public static class RemoteBackup
{
    /// <summary>Printed by <see cref="TarProbeCommand"/> as "SMARTTY_TAR=yes" or "SMARTTY_TAR=no".</summary>
    public const string TarMarker = "SMARTTY_TAR=";

    /// <summary>
    /// The part of the host probe that tells whether the host has tar, the one program the backup needs besides the shell.
    /// It always prints an answer, so that "no" is told apart from a host that did not run the probe at all.
    /// </summary>
    public const string TarProbeCommand = "if command -v tar >/dev/null 2>&1; then echo " + TarMarker + "yes; else echo " + TarMarker + "no; fi";

    /// <summary>True or false from the output of <see cref="TarProbeCommand"/>; null when the output has no answer.</summary>
    public static bool? FromTarProbe(IEnumerable<string> lines)
    {
        if (lines == null)
        {
            return null;
        }
        foreach (string rawLine in lines)
        {
            string line = rawLine?.Trim();
            if (line == TarMarker + "yes")
            {
                return true;
            }
            if (line == TarMarker + "no")
            {
                return false;
            }
        }
        return null;
    }

    private const string UidMarker = "SMARTTY_UID=";

    private const string SudoMarker = "SMARTTY_SUDO=yes";

    private const string MountMarker = "SMARTTY_MOUNT ";

    /// <summary>
    /// Prints the uid of the user, whether passwordless sudo works and the mount table, one "SMARTTY_MOUNT path type" line per
    /// mount (the path keeps the octal escapes of /proc/mounts, so that a space in it does not split the line).
    /// </summary>
    public const string HostInfoCommand =
        "echo " + UidMarker + "$(id -u 2>/dev/null); " +
        "if [ \"$(id -u 2>/dev/null)\" != 0 ] && command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then echo " + SudoMarker + "; fi; " +
        "if [ -r /proc/mounts ]; then while read -r dev mp type rest; do echo \"" + MountMarker + "$mp $type\"; done < /proc/mounts; fi";

    public static RemoteBackupHostInfo ParseHostInfo(IEnumerable<string> lines)
    {
        RemoteBackupHostInfo info = new RemoteBackupHostInfo();
        if (lines == null)
        {
            return info;
        }
        foreach (string rawLine in lines)
        {
            string line = rawLine?.TrimEnd('\r');
            if (line == null)
            {
                continue;
            }
            if (line.StartsWith(UidMarker, StringComparison.Ordinal))
            {
                string uid = line.Substring(UidMarker.Length).Trim();
                if (uid.Length > 0 && uid.All(char.IsDigit))
                {
                    info.IsRoot = uid == "0";
                }
            }
            else if (line.Trim() == SudoMarker)
            {
                info.CanSudo = true;
            }
            else if (line.StartsWith(MountMarker, StringComparison.Ordinal))
            {
                string[] parts = line.Substring(MountMarker.Length).Split(' ');
                if (parts.Length >= 2 && parts[0].StartsWith("/", StringComparison.Ordinal) && parts[1].Length > 0)
                {
                    info.Mounts.Add(new RemoteMount(NormalizeDirectory(DecodeMountPath(parts[0])), parts[1]));
                }
            }
        }
        return info;
    }

    /// <summary>Turns the octal escapes of /proc/mounts ("\040" for a space) back into the characters.</summary>
    public static string DecodeMountPath(string path)
    {
        if (path == null || path.IndexOf('\\') < 0)
        {
            return path;
        }
        List<byte> bytes = new List<byte>();
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '\\' && IsOctal(path, i + 1))
            {
                bytes.Add((byte)Convert.ToInt32(path.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(path[i].ToString()));
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsOctal(string text, int start)
    {
        if (start + 3 > text.Length)
        {
            return false;
        }
        for (int i = start; i < start + 3; i++)
        {
            if (text[i] < '0' || text[i] > '7')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The virtual file systems of the kernel: their "files" are generated on reading (/proc/kcore is as big as the address space).</summary>
    private static readonly HashSet<string> NeverArchivedTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "proc", "sysfs", "debugfs", "tracefs", "securityfs", "cgroup", "cgroup2", "pstore", "bpf", "configfs", "fusectl",
        "efivarfs", "selinuxfs", "binfmt_misc", "nsfs", "autofs", "rpc_pipefs", "nfsd"
    };

    /// <summary>File systems in memory, of devices and of the network, and read-only images: archived only when selected by hand.</summary>
    private static readonly HashSet<string> SkippedByDefaultTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "tmpfs", "devtmpfs", "devpts", "ramfs", "mqueue", "hugetlbfs", "squashfs", "iso9660", "nfs", "nfs4", "cifs", "smb3", "smbfs",
        "9p", "fuse.sshfs", "fuse.gvfsd-fuse", "fuse.portal"
    };

    /// <summary>The top-level folders that hold no part of the installed system, or only things made at run time.</summary>
    private static readonly HashSet<string> SkippedByDefaultTopLevelNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "dev", "run", "tmp", "mnt", "media", "lost+found", "swapfile", "swap.img"
    };

    /// <summary>The top-level folders of the kernel's virtual file systems, for a host whose mount table could not be read.</summary>
    private static readonly HashSet<string> NeverArchivedTopLevelNames = new HashSet<string>(StringComparer.Ordinal) { "proc", "sys" };

    /// <summary>How the backup treats a file system of the given type.</summary>
    public static BackupMountRole ClassifyMountType(string type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return BackupMountRole.Ordinary;
        }
        if (NeverArchivedTypes.Contains(type))
        {
            return BackupMountRole.NeverArchived;
        }
        if (SkippedByDefaultTypes.Contains(type) || type.StartsWith("fuse.", StringComparison.Ordinal))
        {
            return BackupMountRole.SkippedByDefault;
        }
        return BackupMountRole.Ordinary;
    }

    /// <summary>
    /// How the backup treats <paramref name="path"/> (an absolute path of the server): by the type of the file system mounted there,
    /// and for the top-level folders also by their name. <paramref name="note"/> says why, for the tree of the window; null for an
    /// ordinary folder.
    /// </summary>
    public static BackupMountRole Classify(string path, RemoteBackupHostInfo info, out string note)
    {
        note = null;
        path = NormalizeDirectory(path);
        if (path == "/")
        {
            return BackupMountRole.Ordinary;
        }
        string relative = path.Substring(1);
        RemoteMount mount = info?.Mounts.LastOrDefault(m => m.Path == path);
        if (mount != null)
        {
            BackupMountRole role = ClassifyMountType(mount.Type);
            if (role == BackupMountRole.NeverArchived)
            {
                note = "virtual file system (" + mount.Type + "), never archived";
                return role;
            }
            if (info.RootIsOverlay && (path == "/rom" || path == "/overlay"))
            {
                // OpenWrt: / is /rom (the firmware) with /overlay (the changes) on top, so both are already in the backup of /.
                note = mount.Type + ", already part of / (overlay)";
                return BackupMountRole.SkippedByDefault;
            }
            if (role == BackupMountRole.SkippedByDefault)
            {
                note = mount.Type;
                return role;
            }
            note = mount.Type + " mount";
        }
        if (NeverArchivedTopLevelNames.Contains(relative) && mount == null)
        {
            if (info == null || info.Mounts.Count == 0)
            {
                // The mount table could not be read: /proc and /sys are where every Linux mounts the kernel's file systems.
                note = "virtual file system, never archived";
                return BackupMountRole.NeverArchived;
            }
            // The table is known and nothing is mounted there: an empty folder, as in a chroot.
            return BackupMountRole.SkippedByDefault;
        }
        if (SkippedByDefaultTopLevelNames.Contains(relative))
        {
            return BackupMountRole.SkippedByDefault;
        }
        return BackupMountRole.Ordinary;
    }

    /// <summary>The mount points of the virtual file systems of the kernel, which the archive must leave out.</summary>
    public static List<string> NeverArchivedPaths(RemoteBackupHostInfo info)
    {
        List<string> paths = new List<string>();
        if (info == null || info.Mounts.Count == 0)
        {
            paths.AddRange(NeverArchivedTopLevelNames.Select(name => "/" + name));
            return paths;
        }
        foreach (RemoteMount mount in info.Mounts)
        {
            if (mount.Path != "/" && ClassifyMountType(mount.Type) == BackupMountRole.NeverArchived && !paths.Contains(mount.Path))
            {
                paths.Add(mount.Path);
            }
        }
        return paths;
    }

    /// <summary>"/etc/" and "/etc" are the same directory; the root stays "/".</summary>
    public static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }
        string trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    /// <summary>The full path of <paramref name="name"/> inside <paramref name="directory"/>.</summary>
    public static string Combine(string directory, string name)
    {
        string parent = NormalizeDirectory(directory);
        return (parent == "/" ? "/" : parent + "/") + name;
    }

    /// <summary>
    /// Lists <paramref name="directory"/> one entry per line as "d/name", "f/name" or "l/name" (a slash cannot be part of a name, so it
    /// separates the kind from the name whatever the name holds). Only the shell is used: ls differs between GNU, BusyBox and BSD,
    /// and colours its output on some hosts. The hidden entries are listed as well; a name with a line break is left out by
    /// <see cref="ParseListing"/>. Exits with 2 when the directory cannot be entered.
    /// </summary>
    public static string ListCommand(string directory)
    {
        return "cd " + RemotePathUtils.ShellQuote(NormalizeDirectory(directory)) + " 2>/dev/null || exit 2; " +
            "for f in * .[!.]* ..?*; do " +
            "if [ -L \"$f\" ]; then echo \"l/$f\"; elif [ -d \"$f\" ]; then echo \"d/$f\"; elif [ -e \"$f\" ]; then echo \"f/$f\"; fi; " +
            "done";
    }

    /// <summary>The entries printed by <see cref="ListCommand"/>, directories first, each group sorted by name.</summary>
    public static List<RemoteEntry> ParseListing(IEnumerable<string> lines)
    {
        List<RemoteEntry> entries = new List<RemoteEntry>();
        if (lines == null)
        {
            return entries;
        }
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string rawLine in lines)
        {
            string line = rawLine?.TrimEnd('\r');
            if (line == null || line.Length < 3 || line[1] != '/')
            {
                continue;
            }
            RemoteEntryKind kind;
            switch (line[0])
            {
                case 'd':
                    kind = RemoteEntryKind.Directory;
                    break;
                case 'f':
                    kind = RemoteEntryKind.File;
                    break;
                case 'l':
                    kind = RemoteEntryKind.SymbolicLink;
                    break;
                default:
                    continue;
            }
            string name = line.Substring(2);
            if (name == "." || name == ".." || name.IndexOf('/') >= 0 || !seen.Add(name))
            {
                continue;
            }
            entries.Add(new RemoteEntry(name, kind));
        }
        return entries
            .OrderBy(entry => entry.Kind == RemoteEntryKind.Directory ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The name of an absolute path as a member of the archive, relative to / ("/etc/config" is "etc/config"), so that the archive
    /// unpacks with <c>tar -xzf backup.tar.gz -C /</c> and tar prints no "removing leading /" warnings. A name that starts with '-'
    /// gets "./" in front, so that tar does not read it as an option.
    /// </summary>
    public static string MemberName(string path)
    {
        string relative = NormalizeDirectory(path).TrimStart('/');
        if (relative.Length == 0)
        {
            throw new ArgumentException("The root directory is archived by its entries, not as one member.", nameof(path));
        }
        return relative.StartsWith("-", StringComparison.Ordinal) ? "./" + relative : relative;
    }

    /// <summary>
    /// The command line that writes the archive of <paramref name="paths"/> (absolute paths of the server) to its standard output:
    /// <c>tar -czvf - -C / etc usr</c>, run through /bin/sh whatever the login shell is. -v makes tar print every member to its
    /// standard error (GNU and BusyBox tar both do when the archive goes to stdout), which the window shows as progress. Without
    /// <paramref name="compressOnServer"/> the archive is plain tar and is compressed on this computer. With <paramref name="useSudo"/>
    /// it runs under <c>sudo -n</c>, which never asks for a password (it fails instead).
    /// </summary>
    public static string BuildArchiveCommand(IEnumerable<string> paths, bool compressOnServer, bool useSudo)
    {
        List<string> members = (paths ?? Enumerable.Empty<string>()).Select(MemberName).ToList();
        if (members.Count == 0)
        {
            throw new ArgumentException("Nothing is selected to archive.", nameof(paths));
        }
        string script = "LC_ALL=C tar -c" + (compressOnServer ? "z" : "") + "vf - -C / " + string.Join(" ", members.Select(RemotePathUtils.ShellQuote));
        return WrapForUser(script, useSudo);
    }

    /// <summary>
    /// The command line that prints the size in KiB of everything in <paramref name="paths"/> (<c>du -sk</c>, which BusyBox has too),
    /// for the estimate of the window. du prints one line per member; <see cref="ParseSizeOutput"/> adds them up.
    /// </summary>
    public static string BuildSizeCommand(IEnumerable<string> paths, bool useSudo)
    {
        List<string> members = (paths ?? Enumerable.Empty<string>()).Select(MemberName).ToList();
        if (members.Count == 0)
        {
            throw new ArgumentException("Nothing is selected.", nameof(paths));
        }
        string script = "cd / && LC_ALL=C du -sk " + string.Join(" ", members.Select(RemotePathUtils.ShellQuote)) + " 2>/dev/null; true";
        return WrapForUser(script, useSudo);
    }

    private static string WrapForUser(string script, bool useSudo)
    {
        string command = SmarTTY.Utilities.PosixShell.Wrap(script, addAdminDirectoriesToPath: true);
        return useSudo ? "sudo -n " + command : command;
    }

    /// <summary>The total size in bytes of the "KiB&lt;tab&gt;name" lines of du; null when there is none.</summary>
    public static long? ParseSizeOutput(IEnumerable<string> lines)
    {
        if (lines == null)
        {
            return null;
        }
        long total = 0;
        bool any = false;
        foreach (string rawLine in lines)
        {
            string line = rawLine?.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }
            int end = 0;
            while (end < line.Length && char.IsDigit(line[end]))
            {
                end++;
            }
            if (end == 0 || end < line.Length && !char.IsWhiteSpace(line[end]))
            {
                continue;
            }
            if (long.TryParse(line.Substring(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out long kib))
            {
                total += kib * 1024;
                any = true;
            }
        }
        return any ? total : (long?)null;
    }

    /// <summary>
    /// True for a line of tar's standard error that is a message (a file it could not read, the final "error exit delayed"),
    /// false for a member name printed by -v. The members are relative names ("etc/passwd"), so none of them starts with "tar: ".
    /// sudo prints its own complaints the same way.
    /// </summary>
    public static bool IsTarMessage(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }
        return line.StartsWith("tar: ", StringComparison.Ordinal) || line.StartsWith("sudo: ", StringComparison.Ordinal)
            || line.StartsWith("/bin/sh: ", StringComparison.Ordinal) || line.StartsWith("sh: ", StringComparison.Ordinal)
            || line.StartsWith("gzip: ", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when tar failed before it wrote anything because it cannot compress (a BusyBox tar built without gzip, a GNU tar on a
    /// host without the gzip program): the archive is then made plain and compressed on this computer.
    /// </summary>
    public static bool LooksLikeMissingGzip(IEnumerable<string> messages)
    {
        if (messages == null)
        {
            return false;
        }
        foreach (string message in messages)
        {
            string text = message ?? "";
            if (text.IndexOf("invalid option", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("unrecognized option", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("compress", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("usage", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>"router-rootfs-20261101-1530.tar.gz": the host, what was archived and when, with no character Windows forbids.</summary>
    public static string DefaultFileName(string host, bool wholeSystem, DateTime time)
    {
        StringBuilder safe = new StringBuilder();
        foreach (char c in host ?? "")
        {
            safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '.' || c == '_' ? c : '_');
        }
        string name = safe.ToString().Trim('.', '_');
        if (name.Length == 0)
        {
            name = "server";
        }
        return name + (wholeSystem ? "-rootfs-" : "-backup-") + time.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".tar.gz";
    }
}
