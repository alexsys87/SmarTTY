using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SmarTTY.Transport;

// Regression harness for Tools > Backup to tar.gz (Transport/RemoteBackup.cs, Transport/BackupSelection.cs and
// Transport/TarArchiveChecker.cs): the probe for tar, the reading of the mount table, the classification of /proc, /sys and the
// memory file systems, the folder listing, the tree of check boxes and the members it hands to tar, and the archive command
// itself, which is run by /bin/sh on a temporary tree with GNU tar and, when installed, with BusyBox sh and BusyBox tar (the
// tools of OpenWrt). The archive comes out of the standard output of tar, as it does through the SSH channel.
// Run with: dotnet run --project tests/RemoteBackupTests
internal static class Program
{
    private static int _passed;

    private static string _work;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private static int Main()
    {
        _work = Path.Combine(Path.GetTempPath(), "smartty-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        try
        {
            ProbeTests();
            HostInfoTests();
            ClassificationTests();
            MemberAndCommandTests();
            SelectionTests();
            ArchiveCheckerTests();
            if (OperatingSystem.IsWindows())
            {
                Console.WriteLine("SKIP: the shell commands are not run on Windows");
            }
            else
            {
                ListingTests("/bin/sh", null);
                ArchiveTests("/bin/sh", null);
                string busybox = new[] { "/usr/bin/busybox", "/bin/busybox" }.FirstOrDefault(File.Exists);
                if (busybox == null)
                {
                    Console.WriteLine("SKIP: busybox is not installed, the commands are not run with the tools of OpenWrt");
                }
                else
                {
                    string applets = MakeBusyBoxApplets(busybox);
                    ListingTests(busybox + " sh", applets);
                    ArchiveTests(busybox + " sh", applets);
                }
                if (!OperatingSystem.IsWindows())
                {
                    MissingGzipTests();
                }
            }
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(_work, true);
            }
            catch
            {
            }
        }
    }

    private static void ProbeTests()
    {
        Equal(RemoteBackup.FromTarProbe(new[] { "SMARTTY_OS=other", "SMARTTY_TAR=yes", "SMARTTY_KERNEL=Linux" }), (bool?)true, "tar probe: yes");
        Equal(RemoteBackup.FromTarProbe(new[] { " SMARTTY_TAR=no\r" }), (bool?)false, "tar probe: no");
        Equal(RemoteBackup.FromTarProbe(new[] { "'command' is not recognized" }), (bool?)null, "tar probe: an error is no answer");
        Equal(RemoteBackup.FromTarProbe(null), (bool?)null, "tar probe: no output is no answer");
        if (!OperatingSystem.IsWindows())
        {
            Run("/bin/sh", RemoteBackup.TarProbeCommand, null, out byte[] output, out _);
            Equal(RemoteBackup.FromTarProbe(Lines(output)), (bool?)(File.Exists("/usr/bin/tar") || File.Exists("/bin/tar")), "tar probe: this machine's answer agrees with its tar");
            string empty = Path.Combine(_work, "nobin");
            Directory.CreateDirectory(empty);
            int exit = Run("/bin/sh", "PATH=" + empty + "; " + RemoteBackup.TarProbeCommand, null, out output, out _);
            Check(exit == 0 && RemoteBackup.FromTarProbe(Lines(output)) == false, "tar probe: a host without tar answers no, and the probe still succeeds");
        }
    }

    private static void HostInfoTests()
    {
        RemoteBackupHostInfo info = RemoteBackup.ParseHostInfo(new[]
        {
            "SMARTTY_UID=1000",
            "SMARTTY_SUDO=yes",
            "SMARTTY_MOUNT / ext4",
            "SMARTTY_MOUNT /proc proc",
            "SMARTTY_MOUNT /sys sysfs",
            "SMARTTY_MOUNT /sys/fs/cgroup cgroup2",
            "SMARTTY_MOUNT /mnt/my\\040disk ext4",
            "SMARTTY_MOUNT /run/ tmpfs",
            "garbage line",
            "SMARTTY_MOUNT relative ext4"
        });
        Equal(info.IsRoot, (bool?)false, "host info: uid 1000 is not root");
        Check(info.CanSudo, "host info: passwordless sudo");
        Equal(info.Mounts.Count, 6, "host info: the mounts with an absolute path are read");
        Check(info.Mounts.Any(m => m.Path == "/mnt/my disk" && m.Type == "ext4"), "host info: the octal escape of a space is decoded");
        Check(info.Mounts.Any(m => m.Path == "/run" && m.Type == "tmpfs"), "host info: a trailing slash is dropped");
        Check(!info.RootIsOverlay, "host info: an ext4 root is no overlay");
        Equal(RemoteBackup.ParseHostInfo(new[] { "SMARTTY_UID=0" }).IsRoot, (bool?)true, "host info: uid 0 is root");
        Equal(RemoteBackup.ParseHostInfo(new[] { "SMARTTY_UID=" }).IsRoot, (bool?)null, "host info: no uid is no answer");
        Equal(RemoteBackup.DecodeMountPath("/a\\134b\\011c"), "/a\\b\tc", "mount path: backslash and tab escapes");
        Equal(RemoteBackup.DecodeMountPath("/a\\9b"), "/a\\9b", "mount path: a backslash without an octal number stays");
        Equal(RemoteBackup.DecodeMountPath("/\\320\\277"), "/п", "mount path: escaped UTF-8 bytes make one character");

        if (!OperatingSystem.IsWindows())
        {
            int exit = Run("/bin/sh", RemoteBackup.HostInfoCommand, null, out byte[] output, out _);
            RemoteBackupHostInfo local = RemoteBackup.ParseHostInfo(Lines(output));
            Check(exit == 0 && local.IsRoot.HasValue, "host info: this machine reports its uid (" + Encoding.UTF8.GetString(output).Split('\n')[0] + ")");
            if (File.Exists("/proc/mounts"))
            {
                Check(local.Mounts.Any(m => m.Path == "/proc" && m.Type == "proc"), "host info: this machine's /proc is found in the mount table");
            }
        }
    }

    private static void ClassificationTests()
    {
        RemoteBackupHostInfo linux = RemoteBackup.ParseHostInfo(new[]
        {
            "SMARTTY_MOUNT / ext4", "SMARTTY_MOUNT /proc proc", "SMARTTY_MOUNT /sys sysfs", "SMARTTY_MOUNT /dev devtmpfs",
            "SMARTTY_MOUNT /run tmpfs", "SMARTTY_MOUNT /home xfs", "SMARTTY_MOUNT /var/lib/nfs/rpc_pipefs rpc_pipefs",
            "SMARTTY_MOUNT /srv/share nfs4", "SMARTTY_MOUNT /snap/core/1 squashfs", "SMARTTY_MOUNT /srv/sshfs fuse.sshfs"
        });
        Equal(RemoteBackup.Classify("/proc", linux, out string note), BackupMountRole.NeverArchived, "classify: /proc is never archived");
        Check(note != null && note.Contains("proc"), "classify: the note names the file system");
        Equal(RemoteBackup.Classify("/sys/", linux, out _), BackupMountRole.NeverArchived, "classify: /sys is never archived");
        Equal(RemoteBackup.Classify("/var/lib/nfs/rpc_pipefs", linux, out _), BackupMountRole.NeverArchived, "classify: a virtual file system deeper inside");
        Equal(RemoteBackup.Classify("/dev", linux, out _), BackupMountRole.SkippedByDefault, "classify: /dev (devtmpfs) is not selected by default");
        Equal(RemoteBackup.Classify("/run", linux, out note), BackupMountRole.SkippedByDefault, "classify: /run (tmpfs) is not selected by default");
        Equal(note, "tmpfs", "classify: the note of a memory file system");
        Equal(RemoteBackup.Classify("/tmp", linux, out _), BackupMountRole.SkippedByDefault, "classify: /tmp by its name");
        Equal(RemoteBackup.Classify("/mnt", linux, out _), BackupMountRole.SkippedByDefault, "classify: /mnt by its name");
        Equal(RemoteBackup.Classify("/srv/share", linux, out _), BackupMountRole.SkippedByDefault, "classify: a network file system");
        Equal(RemoteBackup.Classify("/snap/core/1", linux, out _), BackupMountRole.SkippedByDefault, "classify: an image mount");
        Equal(RemoteBackup.Classify("/srv/sshfs", linux, out _), BackupMountRole.SkippedByDefault, "classify: a FUSE file system");
        Equal(RemoteBackup.Classify("/home", linux, out note), BackupMountRole.Ordinary, "classify: a disk mount is part of the system");
        Equal(note, "xfs mount", "classify: the note of a disk mount");
        Equal(RemoteBackup.Classify("/etc", linux, out note), BackupMountRole.Ordinary, "classify: /etc");
        Check(note == null, "classify: an ordinary folder has no note");
        Equal(RemoteBackup.Classify("/", linux, out _), BackupMountRole.Ordinary, "classify: the root");
        Equal(RemoteBackup.Classify("/usr/tmp", linux, out _), BackupMountRole.Ordinary, "classify: the names count at the top level only");

        List<string> never = RemoteBackup.NeverArchivedPaths(linux);
        Check(never.Contains("/proc") && never.Contains("/sys") && never.Contains("/var/lib/nfs/rpc_pipefs") && !never.Contains("/run"), "never archived paths: the virtual file systems only");

        // OpenWrt: / is an overlay of /rom (squashfs) and /overlay (the changes); both are already part of /.
        RemoteBackupHostInfo openWrt = RemoteBackup.ParseHostInfo(new[]
        {
            "SMARTTY_UID=0", "SMARTTY_MOUNT / overlay", "SMARTTY_MOUNT /rom squashfs", "SMARTTY_MOUNT /overlay jffs2", "SMARTTY_MOUNT /proc proc",
            "SMARTTY_MOUNT /sys sysfs", "SMARTTY_MOUNT /tmp tmpfs", "SMARTTY_MOUNT /dev tmpfs", "SMARTTY_MOUNT /dev/pts devpts",
            "SMARTTY_MOUNT /sys/kernel/debug debugfs", "SMARTTY_MOUNT /sys/fs/bpf bpf"
        });
        Check(openWrt.RootIsOverlay, "OpenWrt: the root is an overlay");
        Equal(RemoteBackup.Classify("/rom", openWrt, out note), BackupMountRole.SkippedByDefault, "OpenWrt: /rom is not selected by default");
        Check(note.Contains("already part of /"), "OpenWrt: the note says why");
        Equal(RemoteBackup.Classify("/overlay", openWrt, out _), BackupMountRole.SkippedByDefault, "OpenWrt: /overlay is not selected by default");
        Equal(RemoteBackup.Classify("/etc", openWrt, out _), BackupMountRole.Ordinary, "OpenWrt: /etc is selected");
        Equal(RemoteBackup.Classify("/dev/pts", openWrt, out _), BackupMountRole.SkippedByDefault, "OpenWrt: devpts holds device nodes only");

        // A host without /proc/mounts (BSD, or a restricted user): /proc and /sys by their names.
        RemoteBackupHostInfo unknown = RemoteBackup.ParseHostInfo(new[] { "SMARTTY_UID=0" });
        Equal(RemoteBackup.Classify("/proc", unknown, out _), BackupMountRole.NeverArchived, "no mount table: /proc by its name");
        Equal(RemoteBackup.Classify("/sys", null, out _), BackupMountRole.NeverArchived, "no host info: /sys by its name");
        Check(RemoteBackup.NeverArchivedPaths(unknown).SequenceEqual(new[] { "/proc", "/sys" }) || RemoteBackup.NeverArchivedPaths(unknown).OrderBy(p => p).SequenceEqual(new[] { "/proc", "/sys" }), "no mount table: /proc and /sys are left out");
        // The table is known and nothing is mounted on /proc: an empty folder, as in a chroot.
        Equal(RemoteBackup.Classify("/proc", RemoteBackup.ParseHostInfo(new[] { "SMARTTY_MOUNT / ext4" }), out _), BackupMountRole.SkippedByDefault, "chroot: an unmounted /proc is only skipped by default");
    }

    private static void MemberAndCommandTests()
    {
        Equal(RemoteBackup.MemberName("/etc"), "etc", "member: relative to /");
        Equal(RemoteBackup.MemberName("/etc/config/"), "etc/config", "member: a trailing slash is dropped");
        Equal(RemoteBackup.MemberName("/-rf"), "./-rf", "member: a name that starts with '-' is no option of tar");
        bool threw = false;
        try
        {
            RemoteBackup.MemberName("/");
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check(threw, "member: the root itself is never a member");

        string command = RemoteBackup.BuildArchiveCommand(new[] { "/etc", "/my dir", "/it's" }, compressOnServer: true, useSudo: false);
        Check(command.StartsWith("/bin/sh -c ", StringComparison.Ordinal), "archive command: run through /bin/sh whatever the login shell is");
        Check(command.Contains("tar -czvf - -C / "), "archive command: gzip on the server, names on stderr, archive on stdout");
        Check(!RemoteBackup.BuildArchiveCommand(new[] { "/etc" }, compressOnServer: false, useSudo: false).Contains("-czvf"), "archive command: plain tar without server compression");
        Check(RemoteBackup.BuildArchiveCommand(new[] { "/etc" }, true, useSudo: true).StartsWith("sudo -n /bin/sh -c ", StringComparison.Ordinal), "archive command: sudo -n never asks for a password");
        Check(!command.Contains("/tmp") && !command.Contains(">"), "archive command: nothing is written on the server");
        threw = false;
        try
        {
            RemoteBackup.BuildArchiveCommand(new string[0], true, false);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check(threw, "archive command: nothing selected is an error");

        Equal(RemoteBackup.ParseSizeOutput(new[] { "4\tetc", "1024\tusr", "du: cannot read directory 'x': Permission denied" }), (long?)(1028L * 1024), "size: the du lines are added up");
        Equal(RemoteBackup.ParseSizeOutput(new[] { "garbage" }), (long?)null, "size: no number is no answer");
        Check(RemoteBackup.IsTarMessage("tar: etc/shadow: Cannot open: Permission denied"), "messages: an error of GNU tar");
        Check(RemoteBackup.IsTarMessage("tar: error exit delayed from previous errors"), "messages: the final line of BusyBox tar");
        Check(RemoteBackup.IsTarMessage("sudo: a password is required"), "messages: sudo");
        Check(!RemoteBackup.IsTarMessage("etc/passwd") && !RemoteBackup.IsTarMessage("./-rf/tar: x"), "messages: a member name is progress");
        Check(RemoteBackup.LooksLikeMissingGzip(new[] { "tar: invalid option -- 'z'" }), "missing gzip: BusyBox without gzip");
        Check(RemoteBackup.LooksLikeMissingGzip(new[] { "tar (child): gzip: Cannot exec: No such file or directory" }), "missing gzip: GNU tar without the gzip program");
        Check(!RemoteBackup.LooksLikeMissingGzip(new[] { "tar: etc: Cannot stat: No such file or directory" }), "missing gzip: another error");

        DateTime time = new DateTime(2026, 11, 1, 15, 30, 0);
        Equal(RemoteBackup.DefaultFileName("router.lan", true, time), "router.lan-rootfs-20261101-1530.tar.gz", "file name: whole system");
        Equal(RemoteBackup.DefaultFileName("fe80::1", false, time), "fe80__1-backup-20261101-1530.tar.gz", "file name: no character Windows forbids");
        Equal(RemoteBackup.DefaultFileName(null, false, time), "server-backup-20261101-1530.tar.gz", "file name: no host name");
    }

    private static List<BackupNode.ChildSpec> Specs(string directory, RemoteBackupHostInfo info, params string[] entries)
    {
        List<BackupNode.ChildSpec> specs = new List<BackupNode.ChildSpec>();
        foreach (string entry in entries)
        {
            RemoteEntryKind kind = entry.EndsWith("/") ? RemoteEntryKind.Directory : entry.EndsWith("@") ? RemoteEntryKind.SymbolicLink : RemoteEntryKind.File;
            string name = entry.TrimEnd('/', '@');
            BackupMountRole role = RemoteBackup.Classify(RemoteBackup.Combine(directory, name), info, out string note);
            specs.Add(new BackupNode.ChildSpec { Entry = new RemoteEntry(name, kind), Role = role, Note = note });
        }
        return specs;
    }

    private static void SelectionTests()
    {
        RemoteBackupHostInfo info = RemoteBackup.ParseHostInfo(new[]
        {
            "SMARTTY_MOUNT / ext4", "SMARTTY_MOUNT /proc proc", "SMARTTY_MOUNT /sys sysfs", "SMARTTY_MOUNT /tmp tmpfs",
            "SMARTTY_MOUNT /var/lib/nfs/rpc_pipefs rpc_pipefs"
        });
        BackupNode root = BackupNode.CreateRoot();
        int changes = 0;
        root.SelectionChanged += () => changes++;
        Check(!root.ChildrenLoaded && root.Children.Count == 1 && root.Children[0].IsPlaceholder, "tree: an unread folder has a placeholder child");
        List<BackupNode> requested = new List<BackupNode>();
        root.LoadHandler = node => requested.Add(node);
        root.IsExpanded = true;
        Check(requested.Count == 1 && requested[0] == root, "tree: expanding an unread folder asks for its children");

        root.ApplyChildren(Specs("/", info, "bin@", "etc/", "home/", "proc/", "sys/", "tmp/", "usr/", "var/", "vmlinuz"));
        Check(root.ChildrenLoaded && !root.Children.Any(c => c.IsPlaceholder), "tree: the children replace the placeholder");
        Check(root.Find("/proc").IsLocked && root.Find("/proc").IsChecked == false && !root.Find("/proc").IsEnabled, "tree: /proc is locked and unchecked");
        Check(root.Find("/proc").ChildrenLoaded && root.Find("/proc").Children.Count == 0, "tree: a locked folder cannot be expanded");
        Check(root.Find("/tmp").IsChecked == false && root.Find("/tmp").IsEnabled, "tree: /tmp is unchecked by default but can be checked");
        Check(root.Find("/etc").IsChecked == true && root.Find("/bin").IsChecked == true && root.Find("/vmlinuz").IsChecked == true, "tree: the system is checked by default");
        Equal(root.IsChecked, (bool?)null, "tree: the root is partly checked by default");
        Check(root.IsWholeSystemSelected(), "tree: the default is the whole system");
        Check(root.Find("/bin").ChildrenLoaded && root.Find("/bin").Children.Count == 0, "tree: a link is not expanded");
        Check(root.Find("/var").Children.Count == 1 && root.Find("/var").Children[0].IsPlaceholder, "tree: a folder is read when expanded");

        List<string> members = root.CollectMembers();
        Check(members.SequenceEqual(new[] { "/bin", "/etc", "/home", "/usr", "/var", "/vmlinuz" }), "members: the checked top-level entries, never / itself (" + string.Join(" ", members) + ")");

        // /var holds a virtual file system deeper inside: the folders on the way are read before archiving.
        List<BackupNode> toRead = root.FoldersToReadBeforeArchiving(RemoteBackup.NeverArchivedPaths(info));
        Check(toRead.Count == 1 && toRead[0].FullPath == "/var", "before archiving: /var must be read (rpc_pipefs is inside)");
        root.Find("/var").ApplyChildren(Specs("/var", info, "lib/", "log/"));
        Check(root.Find("/var/lib").IsChecked == true && root.Find("/var/log").IsChecked == true, "tree: the children of a checked folder are checked");
        toRead = root.FoldersToReadBeforeArchiving(RemoteBackup.NeverArchivedPaths(info));
        Check(toRead.Count == 1 && toRead[0].FullPath == "/var/lib", "before archiving: then /var/lib");
        root.Find("/var/lib").ApplyChildren(Specs("/var/lib", info, "dpkg/", "nfs/"));
        root.Find("/var/lib/nfs").ApplyChildren(Specs("/var/lib/nfs", info, "rpc_pipefs/", "state"));
        Check(root.FoldersToReadBeforeArchiving(RemoteBackup.NeverArchivedPaths(info)).Count == 0, "before archiving: nothing more to read");
        Check(root.Find("/var/lib/nfs/rpc_pipefs").IsLocked, "tree: the virtual file system deeper inside is locked");
        Equal(root.Find("/var").IsChecked, (bool?)true, "tree: a locked child does not make its folder partly checked");
        members = root.CollectMembers();
        Check(members.Contains("/var/log") && members.Contains("/var/lib/dpkg") && members.Contains("/var/lib/nfs/state")
            && !members.Contains("/var") && !members.Any(m => m.StartsWith("/var/lib/nfs/rpc_pipefs")), "members: a checked folder with a virtual file system inside is split around it (" + string.Join(" ", members) + ")");

        // Unchecking an entry makes the folders above it partly checked; checking it again restores them.
        changes = 0;
        root.Find("/var/log").IsChecked = false;
        Check(changes == 1, "tree: a change of a check box is reported once");
        Equal(root.Find("/var").IsChecked, (bool?)null, "tree: unchecking a child makes the folder partly checked");
        Check(!root.CollectMembers().Contains("/var/log"), "members: the unchecked child is left out");
        Check(root.IsWholeSystemSelected() == false, "tree: no longer the whole system");
        root.Find("/var/log").IsChecked = true;
        Equal(root.Find("/var").IsChecked, (bool?)true, "tree: checking it again checks the folder");

        // An unchecked folder on the way ends the walk: nothing under it is archived.
        root.Find("/var").IsChecked = false;
        Check(root.Find("/var/lib/dpkg").IsChecked == false, "tree: unchecking a folder unchecks everything inside");
        Check(!root.CollectMembers().Any(m => m.StartsWith("/var")), "members: nothing of an unchecked folder");
        BackupNode root2 = BackupNode.CreateRoot();
        root2.ApplyChildren(Specs("/", info, "etc/", "var/"));
        root2.Find("/var").IsChecked = false;
        Check(root2.FoldersToReadBeforeArchiving(RemoteBackup.NeverArchivedPaths(info)).Count == 0, "before archiving: an unchecked folder is not read");

        // The quick selections.
        root.SetChecked(false);
        Equal(root.IsChecked, (bool?)false, "clear: the root is unchecked");
        Check(root.CollectMembers().Count == 0, "clear: nothing to archive");
        root.Find("/etc").SetChecked(true);
        Check(root.CollectMembers().SequenceEqual(new[] { "/etc" }), "configuration only: /etc");
        Check(!root.IsWholeSystemSelected(), "configuration only: not the whole system");
        root.SelectWholeSystem();
        Check(root.IsWholeSystemSelected() && root.Find("/tmp").IsChecked == false && root.Find("/proc").IsChecked == false, "whole system: the defaults again");
        root.SetChecked(true);
        Check(root.Find("/tmp").IsChecked == true && root.Find("/proc").IsChecked == false, "check all: /tmp too, /proc never");
        Check(!root.CollectMembers().Contains("/proc") && root.CollectMembers().Contains("/tmp"), "check all: the members never hold /proc");

        // A folder that could not be read stays checkable as a whole.
        BackupNode root3 = BackupNode.CreateRoot();
        root3.ApplyChildren(Specs("/", info, "secret/"));
        root3.Find("/secret").SetLoadFailed("cannot be opened");
        Check(root3.Find("/secret").ChildrenLoaded && root3.Find("/secret").Note == "cannot be opened" && root3.CollectMembers().SequenceEqual(new[] { "/secret" }), "unreadable folder: archived as a whole");
    }

    private static void ArchiveCheckerTests()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        string tree = Path.Combine(_work, "checker");
        Directory.CreateDirectory(Path.Combine(tree, "d"));
        File.WriteAllText(Path.Combine(tree, "d", "a.txt"), "hello");
        File.WriteAllBytes(Path.Combine(tree, "d", "big.bin"), Enumerable.Range(0, 300000).Select(i => (byte)(i * 7)).ToArray());
        string archive = Path.Combine(_work, "checker.tar.gz");
        Check(Run("/bin/sh", "tar -czf " + archive + " -C " + tree + " d", null, out _, out _) == 0, "checker: an archive is made with tar");
        TarCheckResult result = TarArchiveChecker.CheckGzipFile(archive);
        Check(result.IsComplete && result.Problem == null, "checker: a complete archive");
        Equal(result.Entries, 3L, "checker: the entries are counted (the folder and two files)");
        Equal(result.DataBytes, 300005L, "checker: the file data is counted");

        byte[] bytes = File.ReadAllBytes(archive);
        byte[] plain = Gunzip(bytes);
        // Cut in the middle of a file: the gzip data is complete, the tar stream is not.
        using (MemoryStream cut = new MemoryStream(plain, 0, 2048))
        {
            TarCheckResult cutResult = TarArchiveChecker.Check(cut);
            Check(!cutResult.IsComplete && cutResult.Problem.Contains("cut off"), "checker: a tar stream cut in a file (" + cutResult.Problem + ")");
        }
        // Cut right after the last member, before the end marker.
        int endOfData = 512 + 512 + 512 + 512 + (300000 + 511) / 512 * 512;
        using (MemoryStream noEnd = new MemoryStream(plain, 0, endOfData))
        {
            TarCheckResult noEndResult = TarArchiveChecker.Check(noEnd);
            Check(!noEndResult.IsComplete && noEndResult.Entries == 3, "checker: an archive without the end marker (" + noEndResult.Problem + ")");
        }
        // A damaged header.
        byte[] damaged = (byte[])plain.Clone();
        damaged[512 + 10] ^= 0x55;
        using (MemoryStream damagedStream = new MemoryStream(damaged))
        {
            TarCheckResult damagedResult = TarArchiveChecker.Check(damagedStream);
            Check(!damagedResult.IsComplete && damagedResult.Problem.Contains("damaged"), "checker: a damaged header (" + damagedResult.Problem + ")");
        }
        // A cut gzip file.
        string truncated = Path.Combine(_work, "truncated.tar.gz");
        File.WriteAllBytes(truncated, bytes.Take(bytes.Length / 2).ToArray());
        TarCheckResult truncatedResult = TarArchiveChecker.CheckGzipFile(truncated);
        Check(!truncatedResult.IsComplete, "checker: a gzip file cut in half (" + truncatedResult.Problem + ")");
        // The base-256 size of members of 8 GiB and more.
        byte[] header = new byte[512];
        header[124] = 0x80;
        header[131] = 0x02;
        header[135] = 0x01;
        Equal(TarArchiveChecker.ParseNumber(header, 124, 12), (2L << 32) + 1, "checker: a base-256 size");
    }

    /// <summary>
    /// Runs the listing command of a folder with names that need care: spaces, quotes, a leading '-', a '*', hidden entries, a
    /// link to a folder, a broken link and an empty folder.
    /// </summary>
    private static void ListingTests(string shell, string applets)
    {
        string label = shell.Contains("busybox") ? "BusyBox" : "sh";
        string dir = Path.Combine(_work, "list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub dir"));
        Directory.CreateDirectory(Path.Combine(dir, ".hidden"));
        Directory.CreateDirectory(Path.Combine(dir, "empty"));
        File.WriteAllText(Path.Combine(dir, "it's \"quoted\""), "x");
        File.WriteAllText(Path.Combine(dir, "-rf"), "x");
        File.WriteAllText(Path.Combine(dir, "*"), "x");
        File.WriteAllText(Path.Combine(dir, "..dots"), "x");
        File.WriteAllText(Path.Combine(dir, "файл"), "x");
        File.CreateSymbolicLink(Path.Combine(dir, "link"), Path.Combine(dir, "sub dir"));
        File.CreateSymbolicLink(Path.Combine(dir, "broken"), Path.Combine(dir, "nowhere"));

        int exit = Run(shell, RemoteBackup.ListCommand(dir), applets, out byte[] output, out string error);
        Check(exit == 0, label + " listing: the command succeeds (" + error.Trim() + ")");
        List<RemoteEntry> entries = RemoteBackup.ParseListing(Lines(output));
        string Describe(RemoteEntry e) => (e.Kind == RemoteEntryKind.Directory ? "d " : e.Kind == RemoteEntryKind.SymbolicLink ? "l " : "f ") + e.Name;
        string[] expected =
        {
            "d .hidden", "d empty", "d sub dir",
            "f *", "f -rf", "f ..dots", "l broken", "f it's \"quoted\"", "l link", "f файл"
        };
        string[] actual = entries.Select(Describe).ToArray();
        Check(actual.SequenceEqual(expected), label + " listing: every entry with its kind (" + string.Join(" | ", actual) + ")");

        exit = Run(shell, RemoteBackup.ListCommand(Path.Combine(dir, "empty")), applets, out output, out _);
        Check(exit == 0 && RemoteBackup.ParseListing(Lines(output)).Count == 0, label + " listing: an empty folder has no entries");
        exit = Run(shell, RemoteBackup.ListCommand(Path.Combine(dir, "missing")), applets, out _, out _);
        Equal(exit, 2, label + " listing: a folder that cannot be entered exits with 2");
    }

    /// <summary>
    /// Makes the archive of a small tree the way the window does: the command of <see cref="RemoteBackup.BuildArchiveCommand"/>
    /// with its "-C /" pointed at the tree, the archive read from the standard output and the member names from the standard error.
    /// </summary>
    private static void ArchiveTests(string shell, string applets)
    {
        string label = shell.Contains("busybox") ? "BusyBox tar" : "GNU tar";
        string root = Path.Combine(_work, "root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "etc", "config"));
        Directory.CreateDirectory(Path.Combine(root, "usr", "lib"));
        Directory.CreateDirectory(Path.Combine(root, "my dir"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        File.WriteAllText(Path.Combine(root, "etc", "config", "network"), "config interface 'lan'\n");
        File.WriteAllText(Path.Combine(root, "etc", "passwd"), "root:x:0:0:root:/root:/bin/ash\n");
        File.WriteAllBytes(Path.Combine(root, "usr", "lib", "libbig.so"), Enumerable.Range(0, 200000).Select(i => (byte)(i % 251)).ToArray());
        File.WriteAllText(Path.Combine(root, "my dir", "it's"), "quoted\n");
        File.WriteAllText(Path.Combine(root, "-rf"), "dash\n");
        File.WriteAllText(Path.Combine(root, "tmp", "junk"), "junk\n");
        File.CreateSymbolicLink(Path.Combine(root, "bin"), "usr/lib");

        string[] paths = { "/etc", "/usr/lib/libbig.so", "/my dir", "/-rf", "/bin" };
        foreach (bool compressOnServer in new[] { true, false })
        {
            string what = label + (compressOnServer ? ", gzip on the server" : ", plain tar compressed here");
            string command = PointAt(RemoteBackup.BuildArchiveCommand(paths, compressOnServer, useSudo: false), root);
            Check(command.Contains(RootQuoted(root)), what + ": the test points the archive at the tree");
            int exit = Run(shell, Unwrap(command), applets, out byte[] stdout, out string stderr);
            Check(exit == 0, what + ": tar succeeds (" + stderr.Split('\n').FirstOrDefault(RemoteBackup.IsTarMessage) + ")");
            byte[] gzipped = compressOnServer ? stdout : Gzip(stdout);
            if (compressOnServer)
            {
                Check(stdout.Length > 2 && stdout[0] == 0x1F && stdout[1] == 0x8B, what + ": the output is gzip data");
            }
            else
            {
                Check(stdout.Length > 512 && stdout[0] != 0x1F, what + ": the output is plain tar");
            }
            string[] names = stderr.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !RemoteBackup.IsTarMessage(l)).ToArray();
            Check(names.Contains("etc/config/network") && names.Contains("my dir/it's") && names.Any(n => n.TrimEnd('/') == "./-rf" || n == "-rf"), what + ": -v names the members on the standard error (" + string.Join(" ", names) + ")");
            Check(!names.Any(n => n.StartsWith("tmp")), what + ": nothing that was not selected");

            string archive = Path.Combine(_work, "backup-" + Guid.NewGuid().ToString("N") + ".tar.gz");
            File.WriteAllBytes(archive, gzipped);
            TarCheckResult check = TarArchiveChecker.CheckGzipFile(archive);
            Check(check.IsComplete, what + ": the archive is complete (" + check.Problem + ")");
            Equal(check.Entries, (long)names.Length, what + ": the checker counts as many entries as tar named");

            string restore = Path.Combine(_work, "restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(restore);
            Check(Run("/bin/sh", "tar -xzf " + archive + " -C " + restore, null, out _, out string extractError) == 0, what + ": the archive unpacks with tar -xzf -C (" + extractError.Trim() + ")");
            Equal(File.ReadAllText(Path.Combine(restore, "etc", "config", "network")), "config interface 'lan'\n", what + ": a file comes back");
            Equal(new FileInfo(Path.Combine(restore, "usr", "lib", "libbig.so")).Length, 200000L, what + ": a single file deeper inside comes back");
            Check(!Directory.Exists(Path.Combine(restore, "usr", "lib", "other")) && !Directory.Exists(Path.Combine(restore, "tmp")), what + ": only the selection");
            Equal(File.ReadAllText(Path.Combine(restore, "my dir", "it's")), "quoted\n", what + ": names with a space and a quote");
            Check(File.Exists(Path.Combine(restore, "-rf")), what + ": a name that starts with '-'");
            Check(new FileInfo(Path.Combine(restore, "bin")).LinkTarget == "usr/lib", what + ": a link is stored as a link");
        }

        // A file that cannot be read (or vanished): tar names it, goes on, and ends with a non-zero status; the archive is still made.
        string missing = PointAt(RemoteBackup.BuildArchiveCommand(new[] { "/etc", "/nonexistent" }, true, false), root);
        int status = Run(shell, Unwrap(missing), applets, out byte[] partial, out string messages);
        string[] errorLines = messages.Split('\n').Where(RemoteBackup.IsTarMessage).ToArray();
        Check(status != 0 && partial.Length > 0 && errorLines.Any(l => l.Contains("nonexistent")), label + ": a missing member is reported as a message, the rest is archived (" + string.Join(" / ", errorLines) + ")");
        string partialArchive = Path.Combine(_work, "partial-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        File.WriteAllBytes(partialArchive, partial);
        Check(TarArchiveChecker.CheckGzipFile(partialArchive).IsComplete, label + ": that archive is complete");

        string size = PointAt(RemoteBackup.BuildSizeCommand(new[] { "/etc", "/usr/lib/libbig.so" }, false), root);
        Run(shell, Unwrap(size), applets, out byte[] duOutput, out _);
        long? bytes = RemoteBackup.ParseSizeOutput(Lines(duOutput));
        Check(bytes.HasValue && bytes.Value >= 200000, label + ": du estimates the selection (" + bytes + " bytes)");
    }

    /// <summary>A tar that cannot compress (a small BusyBox build): -z fails before anything is written.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void MissingGzipTests()
    {
        string bin = Path.Combine(_work, "nogzip");
        Directory.CreateDirectory(bin);
        string realTar = new[] { "/usr/bin/tar", "/bin/tar" }.First(File.Exists);
        string fake = Path.Combine(bin, "tar");
        File.WriteAllText(fake, "#!/bin/sh\ncase \"$1\" in *z*) echo \"tar: invalid option -- 'z'\" >&2; echo 'BusyBox v1.36.1 multi-call binary.' >&2; exit 1;; esac\nexec " + realTar + " \"$@\"\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string root = Path.Combine(_work, "nogzip-root");
        Directory.CreateDirectory(Path.Combine(root, "etc"));
        File.WriteAllText(Path.Combine(root, "etc", "hostname"), "OpenWrt\n");
        int exit = Run("/bin/sh", Unwrap(PointAt(RemoteBackup.BuildArchiveCommand(new[] { "/etc" }, true, false), root)), bin, out byte[] output, out string error);
        string[] messages = error.Split('\n').Where(RemoteBackup.IsTarMessage).ToArray();
        Check(exit != 0 && output.Length == 0 && RemoteBackup.LooksLikeMissingGzip(messages), "tar without gzip: nothing is written and the reason is recognised");
        exit = Run("/bin/sh", Unwrap(PointAt(RemoteBackup.BuildArchiveCommand(new[] { "/etc" }, false, false), root)), bin, out output, out _);
        string archive = Path.Combine(_work, "nogzip.tar.gz");
        File.WriteAllBytes(archive, Gzip(output));
        TarCheckResult check = TarArchiveChecker.CheckGzipFile(archive);
        Check(exit == 0 && check.IsComplete && check.Entries == 2, "tar without gzip: the plain archive compressed here is complete");
    }

    private static string RootQuoted(string root)
    {
        return "-C " + root + " ";
    }

    /// <summary>The archive command with its "-C /" (and "cd /") pointed at the test tree: the paths of the tree have no quotes.</summary>
    private static string PointAt(string command, string root)
    {
        return command.Replace("-C / ", RootQuoted(root)).Replace("cd / &&", "cd " + root + " &&");
    }

    /// <summary>"/bin/sh -c 'script'" becomes the script, which the shell under test then runs.</summary>
    private static string Unwrap(string command)
    {
        const string prefix = "/bin/sh -c '";
        if (!command.StartsWith(prefix, StringComparison.Ordinal) || !command.EndsWith("'", StringComparison.Ordinal))
        {
            throw new Exception("FAIL: unexpected command form: " + command);
        }
        return command.Substring(prefix.Length, command.Length - prefix.Length - 1).Replace("'\\''", "'");
    }

    private static string MakeBusyBoxApplets(string busybox)
    {
        string dir = Path.Combine(_work, "applets");
        Directory.CreateDirectory(dir);
        foreach (string applet in new[] { "tar", "gzip", "du", "id", "cat", "sh" })
        {
            File.CreateSymbolicLink(Path.Combine(dir, applet), busybox);
        }
        return dir;
    }

    /// <summary>Runs <paramref name="script"/> with "<paramref name="shell"/> -c"; <paramref name="pathPrefix"/> comes first in PATH.</summary>
    private static int Run(string shell, string script, string pathPrefix, out byte[] stdout, out string stderr)
    {
        string[] parts = shell.Split(' ');
        ProcessStartInfo start = new ProcessStartInfo(parts[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string part in parts.Skip(1))
        {
            start.ArgumentList.Add(part);
        }
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        if (pathPrefix != null)
        {
            start.Environment["PATH"] = pathPrefix + ":" + Environment.GetEnvironmentVariable("PATH");
        }
        using Process process = Process.Start(start);
        System.Threading.Tasks.Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using MemoryStream output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        stdout = output.ToArray();
        stderr = errorTask.Result;
        return process.ExitCode;
    }

    private static string[] Lines(byte[] output)
    {
        return Encoding.UTF8.GetString(output).Split('\n');
    }

    private static byte[] Gzip(byte[] data)
    {
        using MemoryStream result = new MemoryStream();
        using (GZipStream gzip = new GZipStream(result, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data, 0, data.Length);
        }
        return result.ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using MemoryStream result = new MemoryStream();
        using (GZipStream gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
        {
            gzip.CopyTo(result);
        }
        return result.ToArray();
    }
}
