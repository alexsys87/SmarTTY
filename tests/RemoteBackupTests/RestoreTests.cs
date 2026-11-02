using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SmarTTY.Transport;

// Checks of Tools > Restore from tar.gz (Transport/TarArchiveReader.cs, Transport/RemoteRestore.cs): the reading of archives made
// by GNU tar (gnu and pax formats, long names) and BusyBox tar, the tree of their members, the archive reduced to the checked
// members, and its unpacking and md5sum check by the commands of the window, run by /bin/sh and by BusyBox sh.
internal static partial class Program
{
    private static void ReaderTests()
    {
        Equal(TarArchiveReader.NormalizeName("./etc/config/"), "etc/config", "reader: ./ and the trailing slash are dropped");
        Equal(TarArchiveReader.NormalizeName("/./etc"), "etc", "reader: a leading / is dropped");
        Equal(TarArchiveReader.NormalizeName("./"), "", "reader: ./ is the root");

        Func<TarEntry, bool> select = RemoteRestore.MakeSelection(new[] { "/etc/config/network", "/usr/lib" });
        TarEntry E(string name, TarEntryType type = TarEntryType.File) => new TarEntry { Name = name, Type = type };
        Check(select(E("etc/config/network")), "selection: a checked file");
        Check(select(E("usr/lib/libc.so")) && select(E("usr/lib/a/b")), "selection: the contents of a checked folder");
        Check(select(E("etc", TarEntryType.Directory)) && select(E("etc/config", TarEntryType.Directory)), "selection: the folders above a checked file keep their owner and mode");
        Check(!select(E("etc/config/system")) && !select(E("etc/passwd")) && !select(E("usr/libexec/x")), "selection: nothing else (usr/libexec is not usr/lib)");
        Check(!select(E("", TarEntryType.Directory)), "selection: the root folder itself is left alone");

        Check(RemoteRestore.ParseFreeSpace(new[] { "Filesystem     1024-blocks    Used Available Capacity Mounted on", "/dev/root           14336   14336         0     100% /rom", "overlayfs:/overlay   5120     372      4748       7% /" }) == 4748L * 1024, "free space: df -P -k (the last line)");
        Check(RemoteRestore.ParseFreeSpace(new[] { "Filesystem 1024-blocks Used Available Capacity Mounted on", "my disk 100 10 90 10% /mnt/my disk" }) == 90L * 1024, "free space: a name and a mount point with spaces");
        Check(RemoteRestore.ParseFreeSpace(new[] { "df: /x: No such file or directory" }) == null, "free space: an error is no answer");
        bool threw = false;
        try
        {
            RemoteRestore.NormalizeTarget("restore");
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check(threw, "target: a relative folder is refused");
        Equal(RemoteRestore.NormalizeTarget(" /mnt/restore/ "), "/mnt/restore", "target: trimmed");
        string extract = RemoteRestore.BuildExtractCommand("/", true, false, false);
        Check(Unwrap(extract).EndsWith(" && LC_ALL=C tar -xzvf -") && Unwrap(extract).Contains("cd '/' ") && !extract.Contains("mkdir"), "extract command: into / with gzip (" + extract + ")");
        Check(RemoteRestore.BuildExtractCommand("/mnt/r", false, true, true).StartsWith("sudo -n /bin/sh -c ") && Unwrap(RemoteRestore.BuildExtractCommand("/mnt/r", false, true, false)).Contains("mkdir -p") && RemoteRestore.BuildExtractCommand("/mnt/r", false, true, false).EndsWith("tar -xvkf -'"), "extract command: plain, keep existing files, sudo");

        List<TarArchiveReader.FilteredFile> files = new List<TarArchiveReader.FilteredFile>
        {
            new TarArchiveReader.FilteredFile { Name = "a", Md5 = "00ff" },
            new TarArchiveReader.FilteredFile { Name = "b", Md5 = "11ee" },
            new TarArchiveReader.FilteredFile { Name = "c", Md5 = "22dd" }
        };
        Check(RemoteRestore.FindMismatches(files, new[] { "00FF", "11ee", "22dd" }).Count == 0, "check: all MD5s match (case does not matter)");
        List<string> problems = RemoteRestore.FindMismatches(files, new[] { "00ff", "-", "9999" });
        Check(problems.Count == 2 && problems[0].StartsWith("b:") && problems[1].StartsWith("c:"), "check: a missing and a changed file");
        Check(RemoteRestore.FindMismatches(files, new[] { "00ff" }).Count == 2, "check: missing answers count as problems");

        RemoteRestoreHostInfo info = RemoteRestore.ParseHostInfo(new[] { "SMARTTY_UID=1000", "SMARTTY_SUDO=yes", "SMARTTY_TARZ=yes", "SMARTTY_MD5=yes" });
        Check(info.IsRoot == false && info.CanSudo && info.TarHasGzip && info.HasMd5sum, "restore host info: parsed");
        Check(!RemoteRestore.ParseHostInfo(new[] { "SMARTTY_UID=0" }).TarHasGzip && RemoteRestore.ParseHostInfo(new[] { "SMARTTY_UID=0" }).IsRoot == true, "restore host info: root without gzip");
    }

    /// <summary>A tree with names that need care, long paths, a link, an empty folder and a binary file.</summary>
    private static string MakeRestoreSource()
    {
        string source = Path.Combine(_work, "rsource-" + Guid.NewGuid().ToString("N"));
        string deep = Path.Combine(source, "etc", string.Join("/", Enumerable.Repeat("very-long-folder-name", 6)));
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(Path.Combine(source, "etc", "config"));
        Directory.CreateDirectory(Path.Combine(source, "empty dir"));
        Directory.CreateDirectory(Path.Combine(source, "usr", "lib"));
        File.WriteAllText(Path.Combine(deep, "file-with-a-long-name-" + new string('x', 60) + ".conf"), "deep\n");
        File.WriteAllText(Path.Combine(source, "etc", "config", "network"), "config interface 'lan'\n");
        File.WriteAllText(Path.Combine(source, "etc", "config", "system"), "config system\n");
        File.WriteAllText(Path.Combine(source, "etc", "it's файл"), "unicode\n");
        File.WriteAllBytes(Path.Combine(source, "usr", "lib", "lib.so"), Enumerable.Range(0, 70000).Select(i => (byte)(i * 13)).ToArray());
        File.WriteAllText(Path.Combine(source, "usr", "lib", "empty"), "");
        File.CreateSymbolicLink(Path.Combine(source, "etc", "link"), "config/network");
        return source;
    }

    private static void RestoreTests(string shell, string applets)
    {
        string label = shell.Contains("busybox") ? "BusyBox" : "sh";
        int exit = Run(shell, RemoteRestore.HostInfoCommand, applets, out byte[] infoOutput, out _);
        RemoteRestoreHostInfo info = RemoteRestore.ParseHostInfo(Lines(infoOutput));
        Check(exit == 0 && info.TarHasGzip && info.HasMd5sum && info.IsRoot.HasValue, label + " restore: the host info finds tar -z and md5sum (" + Encoding.UTF8.GetString(infoOutput).Replace('\n', ' ') + ")");

        string source = MakeRestoreSource();
        List<(string Name, string Command)> makers = new List<(string, string)>
        {
            ("GNU tar (gnu format)", "/bin/sh|tar -czf - -C " + source + " etc usr 'empty dir'"),
            ("GNU tar (pax format)", "/bin/sh|tar --format=pax -czf - -C " + source + " etc usr 'empty dir'")
        };
        if (applets != null)
        {
            makers.Add(("BusyBox tar", shell + "|tar -czf - -C " + source + " etc usr 'empty dir'"));
        }
        foreach ((string maker, string command) in makers)
        {
            string what = label + " restore of an archive of " + maker;
            string[] parts = command.Split('|');
            Check(Run(parts[0], parts[1], parts[0] == shell ? applets : null, out byte[] archive, out string makeError) == 0, what + ": the archive is made (" + makeError.Trim() + ")");
            byte[] plain = Gunzip(archive);

            List<TarEntry> entries = TarArchiveReader.List(new MemoryStream(plain), out string problem);
            Check(problem == null, what + ": the reader reads it to the end marker");
            string longFile = "etc/" + string.Join("/", Enumerable.Repeat("very-long-folder-name", 6)) + "/file-with-a-long-name-" + new string('x', 60) + ".conf";
            Check(entries.Any(e => e.Name == longFile && e.Type == TarEntryType.File && e.Size == 5), what + ": a path of " + longFile.Length + " characters is read whole");
            Check(entries.Any(e => e.Name == "etc/link" && e.Type == TarEntryType.SymbolicLink && e.LinkName == "config/network"), what + ": a link and its target");
            Check(entries.Any(e => e.Name == "etc/it's файл"), what + ": a UTF-8 name");
            Check(entries.Any(e => e.Name == "empty dir" && e.Type == TarEntryType.Directory), what + ": an empty folder");
            Check(entries.Any(e => e.Name == "usr/lib/lib.so" && e.Size == 70000), what + ": the size of a file");

            BackupNode tree = RemoteRestore.BuildTree(entries, size => size + " B");
            Check(tree.Find("/etc/config/network") != null && tree.Find("/etc/config").IsDirectory && tree.IsChecked == true, what + ": the tree holds everything, checked");
            Equal(tree.Find("/usr/lib/lib.so").Note, "70000 B", what + ": a file shows its size");
            Check(tree.Find("/etc/link").Kind == RemoteEntryKind.SymbolicLink && tree.Find("/etc/link").Note == "link to config/network", what + ": a link shows its target");

            // The whole archive, unpacked in a folder that does not exist yet.
            string target = Path.Combine(_work, "rtarget-" + Guid.NewGuid().ToString("N"), "sub");
            List<TarArchiveReader.FilteredFile> restored = RestoreInto(shell, applets, plain, tree.CollectMembers(), target, gzip: true, what + " (all)");
            Check(restored.Count == 6, what + " (all): the regular files are counted (" + restored.Count + ")");
            Equal(File.ReadAllText(Path.Combine(target, longFile)), "deep\n", what + " (all): the long path comes back");
            Check(new FileInfo(Path.Combine(target, "etc", "link")).LinkTarget == "config/network", what + " (all): the link comes back");
            Check(Directory.Exists(Path.Combine(target, "empty dir")), what + " (all): the empty folder comes back");
            Check(File.ReadAllBytes(Path.Combine(target, "usr", "lib", "lib.so")).SequenceEqual(File.ReadAllBytes(Path.Combine(source, "usr", "lib", "lib.so"))), what + " (all): the binary file is the same");

            // Only /etc/config/network and /usr/lib, as plain tar.
            tree.SetChecked(false);
            tree.Find("/etc/config/network").SetChecked(true);
            tree.Find("/usr/lib").SetChecked(true);
            List<string> members = tree.CollectMembers();
            Check(members.SequenceEqual(new[] { "/etc/config/network", "/usr" }), what + ": the checked members (" + string.Join(" ", members) + ")");
            string partial = Path.Combine(_work, "rpartial-" + Guid.NewGuid().ToString("N"));
            restored = RestoreInto(shell, applets, plain, members, partial, gzip: false, what + " (part)");
            Check(restored.Select(f => f.Name).OrderBy(n => n).SequenceEqual(new[] { "etc/config/network", "usr/lib/empty", "usr/lib/lib.so" }), what + " (part): only the checked files are sent (" + string.Join(" ", restored.Select(f => f.Name)) + ")");
            Check(File.Exists(Path.Combine(partial, "etc", "config", "network")) && !File.Exists(Path.Combine(partial, "etc", "config", "system"))
                && !File.Exists(Path.Combine(partial, "etc", "link")) && !Directory.Exists(Path.Combine(partial, "empty dir")), what + " (part): nothing else is unpacked");

            // -k keeps a file that exists; the check then finds that it differs from the archive.
            File.WriteAllText(Path.Combine(partial, "etc", "config", "network"), "changed\n");
            List<TarArchiveReader.FilteredFile> kept = RestoreInto(shell, applets, plain, members, partial, gzip: true, what + " (keep)", keepExisting: true, expectMismatch: "etc/config/network");
            Equal(File.ReadAllText(Path.Combine(partial, "etc", "config", "network")), "changed\n", what + " (keep): -k left the existing file alone");
        }

        // A damaged archive is refused before anything is sent.
        byte[] good = Gunzip(File.ReadAllBytes(WriteArchive(source)));
        bool refused = false;
        try
        {
            TarArchiveReader.Filter(new MemoryStream(good, 0, good.Length / 2), new MemoryStream(), e => true);
        }
        catch (InvalidDataException)
        {
            refused = true;
        }
        Check(refused, label + " restore: an archive cut in half is refused");
    }

    private static string WriteArchive(string source)
    {
        string path = Path.Combine(_work, "w-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        Run("/bin/sh", "tar -czf " + path + " -C " + source + " etc usr", null, out _, out _);
        return path;
    }

    /// <summary>Filters the archive, sends it to the extract command and checks the result with the verify command, as the window does.</summary>
    private static List<TarArchiveReader.FilteredFile> RestoreInto(string shell, string applets, byte[] plain, List<string> members, string target, bool gzip, string what, bool keepExisting = false, string expectMismatch = null)
    {
        MemoryStream filtered = new MemoryStream();
        List<TarArchiveReader.FilteredFile> files = TarArchiveReader.Filter(new MemoryStream(plain), filtered, RemoteRestore.MakeSelection(members));
        byte[] stream = gzip ? Gzip(filtered.ToArray()) : filtered.ToArray();
        string command = Unwrap(RemoteRestore.BuildExtractCommand(target, gzip, keepExisting, false));
        int exit = RunWithInput(shell, command, applets, stream, out byte[] stdout, out string stderr);
        string[] names = Lines(stdout).Where(l => l.Length > 0).ToArray();
        Check(keepExisting || exit == 0, what + ": tar unpacks the stream (" + stderr.Trim() + ")");
        Check(names.Length > 0 && !names.Any(RemoteBackup.IsTarMessage), what + ": -v names the members on the standard output");

        StringBuilder list = new StringBuilder();
        foreach (TarArchiveReader.FilteredFile file in files)
        {
            list.Append(file.Name).Append('\n');
        }
        exit = RunWithInput(shell, Unwrap(RemoteRestore.BuildVerifyCommand(target, false)), applets, Encoding.UTF8.GetBytes(list.ToString()), out byte[] answers, out string verifyError);
        List<string> problems = RemoteRestore.FindMismatches(files, Lines(answers));
        if (expectMismatch == null)
        {
            Check(exit == 0 && problems.Count == 0, what + ": md5sum on the server agrees with every file (" + string.Join("; ", problems) + verifyError + ")");
        }
        else
        {
            Check(problems.Count == 1 && problems[0].StartsWith(expectMismatch + ":"), what + ": the check finds the file that differs (" + string.Join("; ", problems) + ")");
        }
        return files;
    }

    private static int RunWithInput(string shell, string script, string pathPrefix, byte[] input, out byte[] stdout, out string stderr)
    {
        string[] parts = shell.Split(' ');
        ProcessStartInfo start = new ProcessStartInfo(parts[0])
        {
            RedirectStandardInput = true,
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
        MemoryStream output = new MemoryStream();
        System.Threading.Tasks.Task outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        try
        {
            process.StandardInput.BaseStream.Write(input, 0, input.Length);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The command ended without reading everything; its exit status tells why.
        }
        outputTask.Wait();
        process.WaitForExit();
        stdout = output.ToArray();
        stderr = errorTask.Result;
        return process.ExitCode;
    }
}
