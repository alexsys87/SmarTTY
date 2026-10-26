using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SmarTTY.IO;
using SmarTTY.LocalRepo;

// Regression harness for the file list operations (IO/RemoteFileOperations.cs): name rules, copy names and the
// shell commands, which are run by /bin/sh on a temporary directory (busybox sh as well when it is installed, the
// shell of OpenWRT). Names with spaces, quotes and $ check the quoting.
// Run with: dotnet run --project tests/RemoteFileOperationsTests
internal static class Program
{
    private static int _passed;

    private static string _shell = "/bin/sh";

    // For BusyBox: a directory with links to its applets, put first in PATH, so cp, mv, rm and mkdir are BusyBox's.
    private static string _appletPath;

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

    // The drafts of the remote file editor (IO/EditorDraftStore.cs) and the limits for big files (IO/EditorFilePolicy.cs).
    private static void EditorChecks(string root)
    {
        string directory = Path.Combine(root, "drafts");
        EditorDraftStore store = new EditorDraftStore(directory);
        string key = EditorDraftStore.KeyFor("root@host", "/etc/my config.conf");
        Equal(key, EditorDraftStore.KeyFor("root@host", "/etc/my config.conf"), "draft key: the same file gives the same key");
        Check(key != EditorDraftStore.KeyFor("root@other", "/etc/my config.conf"), "draft key: another session gives another key");
        Check(key != EditorDraftStore.KeyFor("root@host", "/etc/other.conf"), "draft key: another path gives another key");
        Check(key.Length == 32 && key.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, "draft key is a file name");
        Check(store.TryLoad(key) == null, "no draft: nothing is loaded");

        string text = "\n\nline 1\r\nline 2 \u0431\u0443\u043a\u0432\u044b\n\nlast";
        DateTime saved = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        store.Save(key, new EditorDraft { RemotePath = "/etc/my config.conf", SavedUtc = saved, BaseHash = EditorDraftStore.HashText("old"), IsNewFile = false, Text = text });
        EditorDraft loaded = store.TryLoad(key);
        Check(loaded != null, "draft is loaded");
        Equal(loaded.Text, text, "draft text is kept byte for byte (empty lines, CR LF, Cyrillic)");
        Equal(loaded.RemotePath, "/etc/my config.conf", "draft path");
        Equal(loaded.SavedUtc, saved, "draft time");
        Equal(loaded.BaseHash, EditorDraftStore.HashText("old"), "draft base hash");
        Equal(loaded.IsNewFile, false, "draft is not a new file");

        store.Save(key, new EditorDraft { RemotePath = "/etc/my config.conf", SavedUtc = saved, BaseHash = "", IsNewFile = true, Text = "" });
        loaded = store.TryLoad(key);
        Check(loaded != null && loaded.IsNewFile && loaded.Text == "", "a second save replaces the draft (new file, empty text)");
        Equal(Directory.GetFiles(directory).Length, 1, "no temporary files are left behind");

        // The raw bytes of the hex view (format 3): any byte, an empty file, and encryption.
        string hexKey = EditorDraftStore.KeyFor("a", "/bin/blob");
        byte[] bytes = { 0x00, 0x01, 0x02, 0x0A, 0x0D, 0x7F, 0x80, 0xFE, 0xFF };
        store.Save(hexKey, new EditorDraft { RemotePath = "/bin/blob", SavedUtc = saved, BaseHash = "h", Bytes = bytes });
        EditorDraft hexDraft = store.TryLoad(hexKey);
        Check(hexDraft != null && hexDraft.Text == null, "a byte draft has no text");
        Equal(Convert.ToBase64String(hexDraft?.Bytes ?? Array.Empty<byte>()), Convert.ToBase64String(bytes), "a byte draft keeps every byte");
        string rawHex = File.ReadAllText(Path.Combine(directory, hexKey + ".draft"));
        Check(rawHex.StartsWith("SmarTTY-draft 3\n", StringComparison.Ordinal), "a byte draft has its own format");
        store.Save(hexKey, new EditorDraft { RemotePath = "/bin/blob", SavedUtc = saved, Bytes = Array.Empty<byte>() });
        hexDraft = store.TryLoad(hexKey);
        Check(hexDraft != null && hexDraft.Bytes != null && hexDraft.Bytes.Length == 0 && hexDraft.Text == null, "an empty byte draft is not an empty text draft");
        EditorDraftStore protectedHexStore = new EditorDraftStore(directory, new ReversingProtector());
        protectedHexStore.Save(hexKey, new EditorDraft { RemotePath = "/bin/blob", SavedUtc = saved, BaseHash = "h", Bytes = bytes });
        hexDraft = protectedHexStore.TryLoad(hexKey);
        Check(hexDraft != null && hexDraft.Text == null && Convert.ToBase64String(hexDraft.Bytes) == Convert.ToBase64String(bytes), "an encrypted byte draft is read back");
        Check(store.TryLoad(hexKey) == null, "an encrypted byte draft is not read without the protector");
        store.Delete(hexKey);

        File.WriteAllText(Path.Combine(directory, key + ".draft"), "not a draft");
        Check(store.TryLoad(key) == null, "a damaged draft is ignored");
        store.Delete(key);
        Check(!File.Exists(Path.Combine(directory, key + ".draft")), "a draft is deleted");
        store.Delete(key);

        // Encrypted at rest: the text is not in the file, a store with the protector reads it, one without does not.
        string secretKey = EditorDraftStore.KeyFor("a", "/etc/secret.conf");
        EditorDraftStore protectedStore = new EditorDraftStore(directory, new ReversingProtector());
        protectedStore.Save(secretKey, new EditorDraft { RemotePath = "/etc/secret.conf", SavedUtc = saved, BaseHash = "h", Text = "password=hunter2\n" });
        string raw = File.ReadAllText(Path.Combine(directory, secretKey + ".draft"));
        Check(!raw.Contains("hunter2") && !raw.Contains("secret.conf"), "an encrypted draft holds no readable text or path");
        EditorDraft secret = protectedStore.TryLoad(secretKey);
        Check(secret != null && secret.Text == "password=hunter2\n" && secret.RemotePath == "/etc/secret.conf", "an encrypted draft is read back");
        Check(store.TryLoad(secretKey) == null, "an encrypted draft is not read without the protector");
        store.Save(oldKeyPlain(), new EditorDraft { RemotePath = "/plain", SavedUtc = saved, BaseHash = "", Text = "old format" });
        Check(protectedStore.TryLoad(oldKeyPlain())?.Text == "old format", "a plain-text draft of the earlier format is still read");
        protectedStore.Save(secretKey, new EditorDraft { RemotePath = "/etc/secret.conf", SavedUtc = saved, BaseHash = "h", Text = "second" });
        Check(protectedStore.TryLoad(secretKey)?.Text == "second" && Directory.GetFiles(directory, "*.tmp").Length == 0, "a second save replaces the encrypted draft and leaves no temporary file");
        protectedStore.Delete(secretKey);
        store.Delete(oldKeyPlain());

        string oldKey = EditorDraftStore.KeyFor("a", "/old");
        string newKey = EditorDraftStore.KeyFor("a", "/new");
        store.Save(oldKey, new EditorDraft { RemotePath = "/old", SavedUtc = saved, BaseHash = "", Text = "x" });
        store.Save(newKey, new EditorDraft { RemotePath = "/new", SavedUtc = saved, BaseHash = "", Text = "y" });
        File.SetLastWriteTimeUtc(Path.Combine(directory, oldKey + ".draft"), DateTime.UtcNow - TimeSpan.FromDays(40));
        Equal(store.DeleteOlderThan(TimeSpan.FromDays(30)), 1, "old drafts are removed");
        Check(store.TryLoad(oldKey) == null && store.TryLoad(newKey) != null, "only the old draft is gone");
        Equal(new EditorDraftStore(Path.Combine(root, "missing")).DeleteOlderThan(TimeSpan.FromDays(1)), 0, "cleaning a missing directory is not an error");

        Equal(EditorFilePolicy.Classify(0), LargeFileAction.Normal, "an empty file is opened");
        Equal(EditorFilePolicy.Classify(EditorFilePolicy.WarnBytes - 1), LargeFileAction.Normal, "below the warning limit");
        Equal(EditorFilePolicy.Classify(EditorFilePolicy.WarnBytes), LargeFileAction.Warn, "at the warning limit");
        Equal(EditorFilePolicy.Classify(EditorFilePolicy.PreviewOnlyBytes - 1), LargeFileAction.Warn, "below the preview-only limit");
        Equal(EditorFilePolicy.Classify(EditorFilePolicy.PreviewOnlyBytes), LargeFileAction.PreviewOnly, "at the preview-only limit");
        Equal(EditorFilePolicy.Classify(long.MaxValue), LargeFileAction.PreviewOnly, "a huge file is a preview");
        Check(EditorFilePolicy.PreviewBytes <= EditorFilePolicy.PreviewOnlyBytes, "the preview is smaller than the limit");
    }

    private static string oldKeyPlain()
    {
        return EditorDraftStore.KeyFor("a", "/plain");
    }

    // A stand-in for the Windows data protection (the test runs anywhere): reverses the bytes.
    private sealed class ReversingProtector : IDraftProtector
    {
        public byte[] Protect(byte[] data)
        {
            byte[] copy = (byte[])data.Clone();
            Array.Reverse(copy);
            return copy;
        }

        public byte[] Unprotect(byte[] data)
        {
            return Protect(data);
        }
    }

    private static string RunOutput(string command)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo(_shell.Split(' ')[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in _shell.Split(' ')[1..])
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        using Process process = Process.Start(startInfo);
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static int Run(string command, out string errors)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo(_shell.Split(' ')[0])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (string argument in _shell.Split(' ')[1..])
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (_appletPath != null)
        {
            startInfo.Environment["PATH"] = _appletPath + ":" + Environment.GetEnvironmentVariable("PATH");
        }
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        using Process process = Process.Start(startInfo);
        errors = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void NameChecks()
    {
        Check(RemoteFileOperations.ValidateName("report.txt") == null, "a plain name is valid");
        Check(RemoteFileOperations.ValidateName("my file $1 'x'") == null, "spaces, quotes and $ are valid");
        Check(RemoteFileOperations.ValidateName("") != null, "an empty name is rejected");
        Check(RemoteFileOperations.ValidateName("   ") != null, "a blank name is rejected");
        Check(RemoteFileOperations.ValidateName("a/b") != null, "a slash is rejected");
        Check(RemoteFileOperations.ValidateName("..") != null, ".. is rejected");
        Check(RemoteFileOperations.ValidateName(".") != null, ". is rejected");

        HashSet<string> existing = new HashSet<string> { "report.txt", "report (copy).txt", "dir", ".bashrc" };
        Equal(RemoteFileOperations.MakeCopyName("report.txt", false, existing), "report (copy 2).txt", "the next free copy name keeps the extension");
        Equal(RemoteFileOperations.MakeCopyName("dir", true, existing), "dir (copy)", "a folder gets the suffix at the end");
        Equal(RemoteFileOperations.MakeCopyName("archive.d", true, existing), "archive.d (copy)", "a folder with a dot is not split");
        Equal(RemoteFileOperations.MakeCopyName(".bashrc", false, existing), ".bashrc (copy)", "a dot file has no extension");
        Equal(RemoteFileOperations.MakeCopyName("Makefile", false, existing), "Makefile (copy)", "a name without an extension");

        Check(RemoteFileOperations.IsSameOrInside("/a/b", "/a/b"), "a folder is inside itself");
        Check(RemoteFileOperations.IsSameOrInside("/a/b/c", "/a/b/"), "a subfolder is inside");
        Check(!RemoteFileOperations.IsSameOrInside("/a/bc", "/a/b"), "a sibling with the same prefix is not inside");
        Check(!RemoteFileOperations.IsSameOrInside("/a/B", "/a/b"), "paths are case-sensitive");
        Check(RemoteFileOperations.IsSameOrInside("/x", "/"), "everything is inside /");
        Equal(RemoteFileOperations.Join("/", "etc"), "/etc", "join to the root");
        Equal(RemoteFileOperations.Join("/home/u/", "f"), "/home/u/f", "join with a trailing slash");

        Check(RemoteFileOperations.IsExtractableArchive("backup.tar.gz") && RemoteFileOperations.IsExtractableArchive("backup.tgz") && RemoteFileOperations.IsExtractableArchive("backup.tar"), ".tar.gz, .tgz and .tar can be extracted");
        Check(RemoteFileOperations.IsExtractableArchive("BACKUP.TAR.GZ") && RemoteFileOperations.IsExtractableArchive("a b.Tgz"), "the extension is not case-sensitive");
        Check(!RemoteFileOperations.IsExtractableArchive("backup.gz") && !RemoteFileOperations.IsExtractableArchive("backup.zip") && !RemoteFileOperations.IsExtractableArchive("tar") && !RemoteFileOperations.IsExtractableArchive("backup.tar.bz2") && !RemoteFileOperations.IsExtractableArchive(null), "other files are not offered");
        Equal(RemoteFileOperations.ExtractCommand("/a b/it's.tar.gz", "/a b"), "tar -xzf '/a b/it'\\''s.tar.gz' -C '/a b'", "the extract command quotes both paths and asks for gzip");
        Equal(RemoteFileOperations.ExtractCommand("/a/plain.tar", "/a"), "tar -xf '/a/plain.tar' -C '/a'", "a plain tar is not decompressed");
    }

    private static void PackageInstallChecks()
    {
        Check(RemotePackageInstall.IsIpk("luci-app-x_1.0_all.ipk") && RemotePackageInstall.IsIpk("A.IPK") && !RemotePackageInstall.IsIpk("a.apk") && !RemotePackageInstall.IsIpk("ipk") && !RemotePackageInstall.IsIpk(null), ".ipk is recognized, case-insensitively");
        Check(RemotePackageInstall.IsApk("curl-8.apk") && RemotePackageInstall.IsApk("A.Apk") && !RemotePackageInstall.IsApk("a.ipk") && !RemotePackageInstall.IsApk("a.apk.bak") && !RemotePackageInstall.IsApk(null), ".apk is recognized, case-insensitively");
        Check(RemotePackageInstall.IsPackageFile("a.ipk") && RemotePackageInstall.IsPackageFile("a.apk") && !RemotePackageInstall.IsPackageFile("a.tar.gz"), "both kinds get the package icon");
        Check(RemotePackageInstall.CanInstall("a.ipk", LocalRepoPackageManager.Opkg) && RemotePackageInstall.CanInstall("a.apk", LocalRepoPackageManager.Apk), "each package manager installs its own files");
        Check(!RemotePackageInstall.CanInstall("a.apk", LocalRepoPackageManager.Opkg) && !RemotePackageInstall.CanInstall("a.ipk", LocalRepoPackageManager.Apk), "opkg does not offer .apk and apk does not offer .ipk");
        Check(!RemotePackageInstall.CanInstall("a.ipk", null) && !RemotePackageInstall.CanInstall("a.apk", null), "nothing is offered on a host that is not OpenWrt");
        Equal(RemotePackageInstall.BuildInstallCommand(LocalRepoPackageManager.Opkg, new[] { "/tmp/a b.ipk", "/tmp/it's.ipk" }), "opkg install '/tmp/a b.ipk' '/tmp/it'\\''s.ipk'", "opkg installs all files in one run, with quoted paths");
        Equal(RemotePackageInstall.BuildInstallCommand(LocalRepoPackageManager.Apk, new[] { "/tmp/a.apk" }), "apk add --allow-untrusted '/tmp/a.apk'", "apk adds a local file without a signature check");
        Equal(RemotePackageInstall.BuildInstallCommand(LocalRepoPackageManager.Apk, new[] { "a.apk" }), "apk add --allow-untrusted './a.apk'", "a relative path is not taken for a package name");
        bool threw = false;
        try
        {
            RemotePackageInstall.BuildInstallCommand(LocalRepoPackageManager.Opkg, new string[0]);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check(threw, "no files, no command");
    }

    private static void FileIconChecks()
    {
        (string Name, ItemIcon Icon)[] cases =
        {
            ("backup.tar.gz", ItemIcon.ArchiveGzip), ("a.TGZ", ItemIcon.ArchiveGzip), ("site.zip", ItemIcon.ArchiveZip), ("data.7z", ItemIcon.Archive7z), ("kernel.xz", ItemIcon.ArchiveXz),
            ("a.tar", ItemIcon.ArchiveTar), ("a.rar", ItemIcon.ArchiveRar), ("a.tar.bz2", ItemIcon.ArchiveBzip2), ("a.tbz2", ItemIcon.ArchiveBzip2), ("a.tar.zst", ItemIcon.ArchiveZstd), ("a.txz", ItemIcon.ArchiveXz),
            ("app.jar", ItemIcon.ArchiveZip), ("a.cpio", ItemIcon.Archive), ("a.lz4", ItemIcon.Archive),
            ("libfoo.la", ItemIcon.Libtool), ("raw.bin", ItemIcon.Bin),
            ("photo.JPG", ItemIcon.Image), ("logo.svg", ItemIcon.Image), ("pic.png", ItemIcon.Image),
            ("song.mp3", ItemIcon.Audio), ("rec.flac", ItemIcon.Audio), ("film.mkv", ItemIcon.Video), ("clip.mp4", ItemIcon.Video),
            ("notes.txt", ItemIcon.Text), ("README.md", ItemIcon.Markdown), ("README", ItemIcon.Text), ("LICENSE", ItemIcon.Text),
            ("report.docx", ItemIcon.Document), ("book.epub", ItemIcon.Document), ("manual.pdf", ItemIcon.Pdf),
            ("table.xlsx", ItemIcon.Spreadsheet), ("export.csv", ItemIcon.Spreadsheet), ("slides.pptx", ItemIcon.Presentation),
            ("nginx.conf", ItemIcon.Config), ("network.yaml", ItemIcon.Config), ("sshd.rules", ItemIcon.Config),
            ("fstab", ItemIcon.Config), ("passwd", ItemIcon.Config), (".bashrc", ItemIcon.Config), (".vimrc", ItemIcon.Config), (".profile", ItemIcon.Config),
            ("boot.log", ItemIcon.Log), ("boot.log.1", ItemIcon.Log), ("boot.log.2.gz", ItemIcon.Log), ("syslog", ItemIcon.Log), (".bash_history", ItemIcon.Log),
            ("install.sh", ItemIcon.Shell), ("run.BASH", ItemIcon.Shell), ("tool.py", ItemIcon.Python), ("main.c", ItemIcon.CSource), ("util.hpp", ItemIcon.CSource),
            ("app.js", ItemIcon.JavaScript), ("app.tsx", ItemIcon.TypeScript), ("index.php", ItemIcon.Php), ("script.pl", ItemIcon.Perl), ("app.rb", ItemIcon.Ruby), ("Gemfile", ItemIcon.Ruby), ("main.go", ItemIcon.Go), ("go.mod", ItemIcon.Go), ("lib.rs", ItemIcon.Rust),
            ("Main.java", ItemIcon.Java), ("init.lua", ItemIcon.Lua), ("Makefile", ItemIcon.Make), ("CMakeLists.txt", ItemIcon.Make), ("rules.mk", ItemIcon.Make), ("Dockerfile", ItemIcon.Docker), ("docker-compose.yml", ItemIcon.Docker), ("board.dts", ItemIcon.Code), ("Program.kt", ItemIcon.Code),
            ("index.html", ItemIcon.Web), ("site.css", ItemIcon.Css), ("theme.scss", ItemIcon.Css), ("package.json", ItemIcon.Data), ("feed.xml", ItemIcon.Data),
            ("app.sqlite3", ItemIcon.Database), ("dump.sql", ItemIcon.Database),
            ("busybox.elf", ItemIcon.Binary), ("libc.so.6", ItemIcon.Library), ("libfoo.so", ItemIcon.Library), ("libz.a", ItemIcon.Library), ("mac80211.ko", ItemIcon.Binary), ("firmware.bin", ItemIcon.Bin),
            ("vmlinuz", ItemIcon.Firmware), ("openwrt.itb", ItemIcon.Firmware), ("root.squashfs", ItemIcon.Firmware), ("board.dtb", ItemIcon.Firmware),
            ("setup.exe", ItemIcon.Executable), ("App.AppImage", ItemIcon.Executable), ("sshd.service", ItemIcon.Service), ("backup.timer", ItemIcon.Service),
            (".gitignore", ItemIcon.Git), (".gitattributes", ItemIcon.Git), ("repo.bundle", ItemIcon.Git), ("inbox.mbox", ItemIcon.Mail), ("letter.eml", ItemIcon.Mail),
            ("ls.1", ItemIcon.Man), ("sshd_config.5", ItemIcon.Man), ("ls.1.gz", ItemIcon.Man), ("tcp.7", ItemIcon.Man), ("a.7z", ItemIcon.Archive7z), ("v.3gp", ItemIcon.Video), ("config.1", ItemIcon.Man),
            ("ubuntu.iso", ItemIcon.DiskImage), ("disk.qcow2", ItemIcon.DiskImage), ("sd.img", ItemIcon.DiskImage),
            ("server.pem", ItemIcon.Certificate), ("id_rsa", ItemIcon.Certificate), ("id_rsa.pub", ItemIcon.Certificate), ("authorized_keys", ItemIcon.Certificate), ("a.crt", ItemIcon.Certificate),
            ("DejaVuSans.ttf", ItemIcon.Font), ("x.woff2", ItemIcon.Font),
            ("a.conf.bak", ItemIcon.Backup), ("notes.txt~", ItemIcon.Backup), ("a.old", ItemIcon.Backup), ("sshd_config.dpkg-dist", ItemIcon.Backup), ("x.swp", ItemIcon.Backup),
            ("fix.patch", ItemIcon.Patch), ("a.diff", ItemIcon.Patch),
            ("tool_1.0_amd64.deb", ItemIcon.PackageDeb), ("tool-1.0.x86_64.rpm", ItemIcon.PackageRpm),
            ("luci-app-x_1.0_all.ipk", ItemIcon.PackageIpk), ("curl-8.apk", ItemIcon.PackageApk), ("A.IPK", ItemIcon.PackageIpk),
            ("noextension", ItemIcon.File), ("sshd_config", ItemIcon.File), ("weird.unknownext", ItemIcon.File), ("trailingdot.", ItemIcon.File), (".hidden", ItemIcon.File), (".", ItemIcon.File), ("", ItemIcon.File)
        };
        foreach ((string name, ItemIcon icon) in cases)
        {
            Equal(RemoteFileTypes.IconFor(name), icon, $"icon of '{name}'");
        }
        Equal(RemoteFileTypes.IconFor(null), ItemIcon.File, "no name, plain file icon");
    }

    // Every icon value must have a trigger in the XAML of the file list and a 16x16 picture: otherwise the list shows an empty slot.
    private static void FileIconResourceChecks()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "SmarTTY.csproj")))
        {
            root = Path.GetDirectoryName(root);
        }
        if (root == null)
        {
            Console.WriteLine("SKIP: repository root not found, icon resources are not checked");
            return;
        }
        string xaml = File.ReadAllText(Path.Combine(root, "GUI", "FileListControl.xaml"));
        foreach (ItemIcon icon in Enum.GetValues(typeof(ItemIcon)))
        {
            string trigger = $"<DataTrigger Value=\"{icon}\" Binding=\"{{Binding Path=Icon}}\">";
            int at = xaml.IndexOf(trigger, StringComparison.Ordinal);
            Check(at >= 0, $"the file list has a trigger for the {icon} icon");
            int sourceAt = xaml.IndexOf("Value=\"../icons/", at, StringComparison.Ordinal);
            int end = xaml.IndexOf('"', sourceAt + 7);
            string relative = xaml.Substring(sourceAt + 7, end - sourceAt - 7).Replace("../", "");
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Check(File.Exists(path), $"the picture of the {icon} icon exists ({relative})");
            byte[] header = new byte[24];
            using (FileStream stream = File.OpenRead(path))
            {
                stream.Read(header, 0, header.Length);
            }
            int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            Check(header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G' && width == 16 && height == 16, $"the picture of the {icon} icon is a 16x16 PNG");
        }
    }

    private static void ScriptNameChecks()
    {
        Equal(RemoteScript.MakeRemotePath("backup.sh", "ab12cd34"), "/tmp/smartty-ab12cd34-backup.sh", "a script goes to /tmp with a token in its name");
        Equal(RemoteScript.MakeRemotePath("my script $(rm -rf x).sh", "t"), "/tmp/smartty-t-my_script___rm_-rf_x_.sh", "unusual characters of the name are replaced");
        Equal(RemoteScript.MakeRemotePath("-rf", "t"), "/tmp/smartty-t-rf", "a leading dash is dropped");
        Equal(RemoteScript.MakeRemotePath("", "t"), "/tmp/smartty-t-script.sh", "an empty name gets a default");
        Check(RemoteScript.MakeRemotePath(new string('a', 300), "t").Length < 80, "a long name is cut");

        Check(RemoteScript.Validate(new byte[0]) != null, "an empty file is not a script");
        Check(RemoteScript.Validate(new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0, 1 }) != null, "a binary file is not a script");
        Check(RemoteScript.Validate(new byte[RemoteScript.MaxLength + 1]) != null, "a huge file is not a script");
        Check(RemoteScript.Validate(System.Text.Encoding.UTF8.GetBytes("echo hi\n")) == null, "a text file is a script");

        string windows = "\uFEFF#!/bin/sh\r\necho one\r\nif true; then\r\n  echo \"a\rb\"\r\nfi\r\n";
        byte[] normalized = RemoteScript.Normalize(System.Text.Encoding.UTF8.GetBytes(windows));
        Equal(System.Text.Encoding.UTF8.GetString(normalized), "#!/bin/sh\necho one\nif true; then\n  echo \"a\rb\"\nfi\n", "CR LF becomes LF, a lone CR stays, the byte order mark is dropped");
        Check(RemoteScript.HasShebang(normalized) && !RemoteScript.HasShebang(System.Text.Encoding.UTF8.GetBytes("echo hi")), "a shebang is recognized after the byte order mark is dropped");

        Equal(RemoteScript.BuildCommand("/tmp/s 1.sh", true), "('/tmp/s 1.sh'; rc=$?; rm -f '/tmp/s 1.sh'; exit $rc)", "a script with a shebang is run as a program, in a subshell");
        Equal(RemoteScript.BuildCommand("/tmp/s.sh", false), "(sh '/tmp/s.sh'; rc=$?; rm -f '/tmp/s.sh'; exit $rc)", "a script without one is given to sh");

        Equal(RemoteScript.BuildRunInPlaceCommand("/a b/x.sh", new[] { RemoteScript.ExecutableMarker }), "(cd '/a b' && './x.sh')", "an executable script is run as it is, from its folder");
        Equal(RemoteScript.BuildRunInPlaceCommand("/a/x.sh", new[] { "#!/bin/bash" }), "(cd '/a' && '/bin/bash' './x.sh')", "a script that is not executable is given to the interpreter of its shebang");
        Equal(RemoteScript.BuildRunInPlaceCommand("/x.sh", new[] { "#!/usr/bin/env  bash -e " }), "(cd '/' && '/usr/bin/env' 'bash' '-e' './x.sh')", "the words of a shebang line are kept apart");
        Equal(RemoteScript.BuildRunInPlaceCommand("/a/x.sh", new[] { "echo hi" }), "(cd '/a' && sh './x.sh')", "a script without a shebang is given to sh");
        Equal(RemoteScript.BuildRunInPlaceCommand("/a/x.sh", null), "(cd '/a' && sh './x.sh')", "without an answer from the server sh is used");
        Equal(RemoteScript.BuildRunInPlaceCommand("/a/-x's.sh", new[] { "#!/bin/sh; rm -rf ~" }), "(cd '/a' && '/bin/sh;' 'rm' '-rf' '~' './-x'\\''s.sh')", "text of the shebang line is quoted, it is never shell syntax");
    }

    // The two steps of "Run" in the file list: ask the server about the file, then build the command from the answer.
    private static string InPlaceCommand(string path)
    {
        string probe = RunOutput(RemoteScript.ProbeCommand(path));
        return RemoteScript.BuildRunInPlaceCommand(path, probe.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string[] RunLines(string command)
    {
        return RunOutput(command).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static Process StartHelper(string script)
    {
        return Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", script }, UseShellExecute = false });
    }

    // The process list of the Process manager (read from the local /proc by this shell) and the commands that stop a process.
    private static void ProcessChecks()
    {
        Process busy = StartHelper("while :; do :; done");
        Process sleeper = StartHelper("exec sleep 60");
        Process stubborn = StartHelper("trap '' 15; while :; do sleep 1; done");
        try
        {
            ProcessSnapshot first = RemoteProcessList.Parse(RunLines(RemoteProcessList.ListCommand), null);
            System.Threading.Thread.Sleep(1500);
            ProcessSnapshot second = RemoteProcessList.Parse(RunLines(RemoteProcessList.ListCommand), first);
            string shell = _shell + ": ";
            Check(second.Processes.Count >= 4, shell + "the list has the processes (" + second.Processes.Count + ")");
            Check(second.CpuTotalPercent.HasValue && second.CpuTotalPercent >= 0 && second.CpuTotalPercent <= 100, shell + "the load of all cores is worked out (" + second.CpuTotalPercent + " %)");
            Check(first.CpuTotalPercent == null && first.DiskBusyPercent == null, shell + "the first list has no load of the processor and the disks yet");
            Check(second.MemCachedKb.HasValue && second.MemCachedKb >= 0, shell + "buffers and cache are read (" + second.MemCachedKb + " kB)");
            Check(second.StorageTotalKb > 0 && second.StorageUsedKb >= 0 && second.StorageUsedKb <= second.StorageTotalKb, shell + "the space of / is read from df (" + second.StorageUsedKb + " of " + second.StorageTotalKb + " kB)");
            Check(second.DiskBusyPercent == null || (second.DiskBusyPercent >= 0 && second.DiskBusyPercent <= 100), shell + "the disk activity is a percentage (" + second.DiskBusyPercent + " % on " + second.BusiestDisk + ")");
            Check(second.TotalTicks > first.TotalTicks && second.CpuCount >= 1, shell + "the processor time of all cores grows and the cores are counted (" + second.CpuCount + ")");
            Check(second.UptimeSeconds > 0 && second.MemTotalKb > 0 && second.MemFreeKb.HasValue && second.LoadAverage != null && second.LoadAverage.Split(' ').Length == 3, shell + "uptime, memory and load are read");

            RemoteProcess self = second.Processes.Find(p => p.Pid == Environment.ProcessId);
            Check(self != null && self.Command.Length > 0 && self.MemoryKb > 0 && self.State.Length == 1 && self.ParentPid > 0, shell + "this test's own process is listed with command, memory, state and parent");
            Check(first.Processes.TrueForAll(p => p.CpuPercent == null), shell + "the first list has no processor shares yet");

            RemoteProcess busyRow = second.Processes.Find(p => p.Pid == busy.Id);
            Check(busyRow != null && busyRow.CpuPercent > 40 && busyRow.CpuPercent < 150, shell + "a process in a loop uses about one core (" + busyRow?.CpuPercent + " %)");
            RemoteProcess sleeperRow = second.Processes.Find(p => p.Pid == sleeper.Id);
            Check(sleeperRow != null && sleeperRow.CpuPercent < 5 && sleeperRow.Command.Contains("sleep 60") && sleeperRow.Name == "sleep" && sleeperRow.ParentPid == Environment.ProcessId, shell + "a sleeping process uses no processor, its name, command line and parent are right");
            Check(busyRow.User == sleeperRow.User && busyRow.User.Length > 0, shell + "the user is the same for both");
            Check(second.Processes.TrueForAll(p => p.Command.Length > 0 && p.Name.Length > 0 && p.State.Length > 0), shell + "every process has a name, a command and a state");

            Equal(RemoteProcessList.SignalCommand(42, force: false), "kill -15 42", "ending a process is kill -15");
            Equal(RemoteProcessList.SignalCommand(42, force: true), "kill -9 42", "killing a process is kill -9");
            Equal(Run(RemoteProcessList.SignalCommand(sleeper.Id, force: false), out _), 0, shell + "a process is asked to end");
            Check(sleeper.WaitForExit(3000), shell + "it ends");
            System.Threading.Thread.Sleep(500);
            Equal(Run(RemoteProcessList.SignalCommand(stubborn.Id, force: false), out _), 0, shell + "a process that ignores the request is signalled");
            System.Threading.Thread.Sleep(700);
            Check(!stubborn.HasExited, shell + "it ignores the request to end");
            Equal(Run(RemoteProcessList.SignalCommand(stubborn.Id, force: true), out _), 0, shell + "it is killed");
            Check(stubborn.WaitForExit(3000), shell + "and it is gone");
            Check(Run(RemoteProcessList.SignalCommand(sleeper.Id, force: false), out string errors) != 0 && errors.Length > 0, shell + "signalling a process that is gone fails with a message");
        }
        finally
        {
            foreach (Process helper in new[] { busy, sleeper, stubborn })
            {
                try
                {
                    if (!helper.HasExited)
                    {
                        helper.Kill();
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        // Lines of the shell that are not part of the list are skipped, a command line may hold the separator.
        ProcessSnapshot parsed = RemoteProcessList.Parse(new[]
        {
            "sh: can't open something", "T|1000|4", "U|3600.5", "L|0.10 0.05 0.01", "M|2048000|100000|1500000", "N|0|root", "N|0|toor", "N|1000|alice",
            "P|1|0|S|10|1200|0|init|/sbin/init splash", "P|20|1|R|50|0|1000|worker|worker --opt a|b  ", "garbage", "P|x|1|S|0|0|0|bad|bad"
        }, null);
        Equal(parsed.Processes.Count, 2, "only well-formed process lines are taken");
        Check(parsed.Processes[1].User == "alice" && parsed.Processes[1].Command == "worker --opt a|b" && parsed.Processes[1].MemoryKb == 0 && parsed.Processes[0].User == "root", "users are named, the first name of a number wins, a command line may hold a |");
        Check(parsed.MemTotalKb == 2048000 && parsed.MemFreeKb == 1500000 && parsed.CpuCount == 4 && parsed.UptimeSeconds == 3600.5, "the numbers around the list are read (available memory is preferred to free)");
        ProcessSnapshot later = RemoteProcessList.Parse(new[] { "T|1400|4", "P|1|0|S|10|1200|0|init|init", "P|20|1|R|150|0|1000|worker|worker", "P|30|1|S|5|0|0|new|new" }, parsed);
        Check(later.Processes[0].CpuPercent == 0 && later.Processes[1].CpuPercent == 100 && later.Processes[2].CpuPercent == null, "the share is the process time of the interval in the time of all cores, 4 cores x 100 ticks of 400 is 100 %, a new process has none yet");
        // The load of the whole server: processor, disks, cache and the space of / (made-up numbers).
        ProcessSnapshot load1 = RemoteProcessList.Parse(new[]
        {
            "T|1000|4|800", "U|100.0", "M|2048000|100000|1500000|50000|250000", "D|sda|1000|2000|4000", "D|loop0|0|0|0", "D|dm-0|500|10|10", "S|1000000|250000|/",
            "P|1|0|S|10|1200|0|init|init"
        }, null);
        Check(load1.IdleTicks == 800 && load1.MemCachedKb == 300000 && load1.StorageTotalKb == 1000000 && load1.StorageUsedKb == 250000 && load1.Disks.Count == 3, "idle time, buffers + cache, the space of / and the disks are read");
        Check(load1.CpuTotalPercent == null && load1.DiskBusyPercent == null, "nothing to compare with in the first list");
        ProcessSnapshot load2 = RemoteProcessList.Parse(new[]
        {
            "T|1400|4|1100", "U|102.0", "D|sda|1500|2000|6000", "D|loop0|0|0|0", "D|dm-0|900|10|10", "P|1|0|S|10|1200|0|init|init"
        }, load1);
        Check(load2.CpuTotalPercent == 25.0, "100 busy ticks of 400 is 25 % of the processor (" + load2.CpuTotalPercent + ")");
        Check(load2.DiskBusyPercent == 25.0 && load2.BusiestDisk == "sda", "500 ms of 2000 ms is 25 % busy, the disk below dm-0 counts and not dm-0 itself (" + load2.DiskBusyPercent + " on " + load2.BusiestDisk + ")");
        Check(load2.DiskReadBytesPerSecond == 0 && load2.DiskWriteBytesPerSecond == 512000.0, "2000 sectors of 512 bytes in 2 s are 512000 bytes a second (" + load2.DiskWriteBytesPerSecond + ")");
        ProcessSnapshot load3 = RemoteProcessList.Parse(new[] { "T|1800|4|1400", "U|104.0", "D|sda|10|2000|6000", "P|1|0|S|10|1200|0|init|init" }, load2);
        Check(load3.CpuTotalPercent == 25.0 && load3.DiskBusyPercent == null, "a disk counter that went down is not used");
        Check(RemoteProcessList.FormatRate(300 * 1024) == "300 kB/s" && RemoteProcessList.FormatRate(-5) == "0 kB/s", "rates are in kB/s for small numbers and never negative");
        Equal(RemoteProcessList.FormatKb(512), "512 kB", "small sizes are in kilobytes");
        Equal(RemoteProcessList.FormatKb(100 * 1024), "100 MB", "larger sizes are in megabytes");
        Equal(RemoteProcessList.FormatKb(3L * 1024 * 1024 * 10), "30 GB", "the largest in gigabytes");
    }

    private static void RunInPlaceChecks(string root)
    {
        Check(RemoteScript.IsScriptFileName("run.sh") && RemoteScript.IsScriptFileName("RUN.SH") && RemoteScript.IsScriptFileName("a.bash") && RemoteScript.IsScriptFileName("a.ash") && RemoteScript.IsScriptFileName("a b.ksh") && RemoteScript.IsScriptFileName("x.zsh") && RemoteScript.IsScriptFileName("x.dash"), "shell script extensions are recognized");
        Check(!RemoteScript.IsScriptFileName("sh") && !RemoteScript.IsScriptFileName(".sh") && !RemoteScript.IsScriptFileName("notes.txt") && !RemoteScript.IsScriptFileName("a.sh.bak") && !RemoteScript.IsScriptFileName("") && !RemoteScript.IsScriptFileName(null), "other names are not offered");

        string folder = Path.Combine(root, "it's a folder");
        Directory.CreateDirectory(folder);
        // The scripts print the folder they run in and what they were given as $0, and exit with 5.
        string body = "echo \"dir=$(pwd | sed 's|.*/||') name=$0\"\necho from-stderr >&2\nexit 5\n";
        (string Name, string Content, bool Executable, string Expect)[] cases =
        {
            ("exec shebang.sh", "#!/bin/sh\n" + body, true, "dir=it's a folder name=./exec shebang.sh"),
            ("plain shebang.sh", "#!/bin/sh\n" + body, false, "dir=it's a folder name=./plain shebang.sh"),
            ("env shebang.sh", "#!/usr/bin/env sh\n" + body, false, "dir=it's a folder name=./env shebang.sh"),
            ("crlf shebang.sh", "#!/bin/sh\r\n" + body, false, "dir=it's a folder name=./crlf shebang.sh"),
            ("no shebang.sh", body, false, "dir=it's a folder name=./no shebang.sh"),
            ("-dash.sh", body, false, "dir=it's a folder name=./-dash.sh"),
        };
        foreach (var testCase in cases)
        {
            string path = Path.Combine(folder, testCase.Name);
            File.WriteAllText(path, testCase.Content);
            Equal(Run("chmod " + (testCase.Executable ? "700" : "600") + " " + RemotePathUtils.ShellQuote(path), out _), 0, "set the mode of " + testCase.Name);
            string inPlace = InPlaceCommand(path);
            string text = RunOutput(inPlace);
            Check(text.Contains(testCase.Expect) && text.Contains("from-stderr"), _shell + ": " + testCase.Name + " runs from its own folder (got: " + text.Trim().Replace("\n", " | ") + ")");
            Equal(Run(inPlace, out _), 5, testCase.Name + ": the exit status of the script is kept");
            Check(RunOutput(inPlace + "; echo still-here").Contains("still-here"), testCase.Name + ": the shell that typed the command goes on afterwards");
            Check(File.Exists(path) && File.ReadAllText(path) == testCase.Content, testCase.Name + ": the script is left unchanged");
        }
        Check(Run(InPlaceCommand(Path.Combine(folder, "missing.sh")), out string errors) != 0 && errors.Length > 0, "a script that does not exist fails with a message");
    }

    private static void ScriptRunChecks(string root)
    {
        string work = Path.Combine(root, "work dir");
        Directory.CreateDirectory(work);
        foreach (bool shebang in new[] { true, false })
        {
            string path = Path.Combine(root, "it's a $script" + (shebang ? "-shebang" : "-plain") + ".sh");
            byte[] script = RemoteScript.Normalize(System.Text.Encoding.UTF8.GetBytes((shebang ? "#!/bin/sh\r\n" : "") + "echo \"in $(pwd | sed 's|.*/||')\"\r\necho to-stderr >&2\r\nexit 7\r\n"));
            File.WriteAllBytes(path, script);
            if (shebang)
            {
                Equal(Run("chmod 700 " + RemotePathUtils.ShellQuote(path), out _), 0, "make the script executable");
            }
            // The command is typed into a terminal that is in the directory the script is to run in.
            string typed = RemoteScript.BuildCommand(path, shebang);
            int exit = Run("cd " + RemotePathUtils.ShellQuote(work) + " && " + typed, out string errors);
            Equal(exit, 7, _shell + ": the exit status of the script is the exit status of the command (" + (shebang ? "shebang" : "no shebang") + ")");
            Check(errors.Contains("to-stderr"), "the script's error output comes through");
            Check(!File.Exists(path), "the script is removed after it has run");
        }
        string output = RunOutput(RemoteScript.BuildCommand(Path.Combine(root, "missing.sh"), true) + "; echo still-here");
        Check(output.Contains("still-here") && !File.Exists(Path.Combine(root, "missing.sh")), "a missing script fails, the shell that typed the command goes on, nothing is left behind");
    }

    private static void CommandChecks(string root)
    {
        string name = "it's a $file";
        string source = Path.Combine(root, name);
        File.WriteAllText(source, "data");
        string folder = Path.Combine(root, "my dir");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "inner.txt"), "inner");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);

        Equal(Run(RemoteFileOperations.CopyCommand(source, Path.Combine(target, name), replace: false), out _), 0, _shell + ": copy a file");
        Equal(File.ReadAllText(Path.Combine(target, name)), "data", "the copy has the content");
        Check(File.Exists(source), "copying keeps the source");

        int exit = Run(RemoteFileOperations.CopyCommand(source, Path.Combine(target, name), replace: false), out string errors);
        Check(exit != 0 && errors.Contains("already exists"), _shell + ": copying onto an existing file fails with a message");
        File.WriteAllText(source, "new data");
        Equal(Run(RemoteFileOperations.CopyCommand(source, Path.Combine(target, name), replace: true), out _), 0, "replace an existing file");
        Equal(File.ReadAllText(Path.Combine(target, name)), "new data", "the replaced file has the new content");

        Equal(Run(RemoteFileOperations.CopyCommand(folder, Path.Combine(target, "my dir"), replace: false), out _), 0, "copy a folder recursively");
        Check(File.Exists(Path.Combine(target, "my dir", "sub", "inner.txt")), "the folder copy has the nested file");
        exit = Run(RemoteFileOperations.CopyCommand(folder, Path.Combine(target, "my dir"), replace: false), out _);
        Check(exit != 0 && !Directory.Exists(Path.Combine(target, "my dir", "my dir")), "copying a folder onto an existing one does not nest it inside");
        File.WriteAllText(Path.Combine(target, "my dir", "stale.txt"), "stale");
        Equal(Run(RemoteFileOperations.CopyCommand(folder, Path.Combine(target, "my dir"), replace: true), out _), 0, "replace an existing folder");
        Check(!File.Exists(Path.Combine(target, "my dir", "stale.txt")), "the replaced folder has no stale files");

        string moved = Path.Combine(target, "moved $x");
        Equal(Run(RemoteFileOperations.MoveCommand(source, moved, replace: false), out _), 0, "move a file");
        Check(!File.Exists(source) && File.Exists(moved), "the moved file is gone from the source");
        File.WriteAllText(source, "again");
        exit = Run(RemoteFileOperations.MoveCommand(source, moved, replace: false), out errors);
        Check(exit != 0 && File.Exists(source) && File.ReadAllText(moved) == "new data", "moving onto an existing file does not overwrite it");
        Equal(Run(RemoteFileOperations.MoveCommand(source, Path.Combine(root, "renamed"), replace: false), out _), 0, "rename a file");
        Check(File.Exists(Path.Combine(root, "renamed")), "the renamed file exists");

        string newFolder = Path.Combine(root, "new 'folder'");
        Equal(Run(RemoteFileOperations.CreateDirectoryCommand(newFolder), out _), 0, "create a folder");
        Check(Directory.Exists(newFolder), "the folder exists");
        Check(Run(RemoteFileOperations.CreateDirectoryCommand(newFolder), out _) != 0, "creating an existing folder fails");
        string newFile = Path.Combine(root, "empty $file.txt");
        Equal(Run(RemoteFileOperations.CreateFileCommand(newFile), out _), 0, "create an empty file");
        Check(File.Exists(newFile) && new FileInfo(newFile).Length == 0, "the new file is empty");
        File.WriteAllText(newFile, "keep");
        Check(Run(RemoteFileOperations.CreateFileCommand(newFile), out _) != 0 && File.ReadAllText(newFile) == "keep", "creating an existing file fails and keeps its content");

        // Extract here: archives made with the host tar (names with spaces, quotes and $), unpacked by this shell's tar.
        string packed = Path.Combine(root, "packed dir");
        Directory.CreateDirectory(Path.Combine(packed, "docs"));
        File.WriteAllText(Path.Combine(packed, "it's $one.txt"), "one");
        File.WriteAllText(Path.Combine(packed, "docs", "two.txt"), "two");
        string archiveDirectory = Path.Combine(root, "it's archives");
        Directory.CreateDirectory(archiveDirectory);
        string gz = Path.Combine(archiveDirectory, "pack $1.tar.gz");
        string plain = Path.Combine(archiveDirectory, "pack.tar");
        Equal(Run("tar -czf " + RemotePathUtils.ShellQuote(gz) + " -C " + RemotePathUtils.ShellQuote(root) + " 'packed dir'", out _), 0, "make a .tar.gz");
        Equal(Run("tar -cf " + RemotePathUtils.ShellQuote(plain) + " -C " + RemotePathUtils.ShellQuote(root) + " 'packed dir'", out _), 0, "make a .tar");
        Equal(Run(RemoteFileOperations.ExtractCommand(gz, archiveDirectory), out errors), 0, _shell + ": extract a .tar.gz" + errors);
        Equal(File.ReadAllText(Path.Combine(archiveDirectory, "packed dir", "it's $one.txt")), "one", "the extracted file is next to the archive");
        Check(File.Exists(Path.Combine(archiveDirectory, "packed dir", "docs", "two.txt")) && File.Exists(gz), "the nested file is extracted and the archive is kept");
        Directory.Delete(Path.Combine(archiveDirectory, "packed dir"), recursive: true);
        Equal(Run(RemoteFileOperations.ExtractCommand(plain, archiveDirectory), out errors), 0, _shell + ": extract a .tar" + errors);
        Check(File.Exists(Path.Combine(archiveDirectory, "packed dir", "docs", "two.txt")), "the plain tar is extracted");
        File.WriteAllText(Path.Combine(archiveDirectory, "broken.tar.gz"), "this is not an archive");
        Check(Run(RemoteFileOperations.ExtractCommand(Path.Combine(archiveDirectory, "broken.tar.gz"), archiveDirectory), out errors) != 0 && errors.Length > 0, "a file that is not an archive fails with a message");

        Equal(Run(RemoteFileOperations.DeleteCommand(new[] { newFile, Path.Combine(target, "my dir") }), out _), 0, "delete a file and a folder");
        Check(!File.Exists(newFile) && !Directory.Exists(Path.Combine(target, "my dir")), "both are gone");
    }

    // One command after another, as the file list runs them (each is a command of its own; a command may end with exit).
    private static int RunAll(List<string> commands, out string errors)
    {
        errors = "";
        int result = 0;
        foreach (string command in commands)
        {
            int exit = Run(command, out string commandErrors);
            errors += commandErrors;
            if (exit != 0 && result == 0)
            {
                result = exit;
            }
        }
        return result;
    }

    // The POSIX file mode (CA1416): this harness runs the commands of the remote shell on this machine, so it only
    // ever runs on Unix, and the guard is what tells the analyzer that.
    private static int Mode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the file mode checks need a POSIX file system");
        }
        return (int)File.GetUnixFileMode(path);
    }

    private static void SetMode(string path, int mode)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the file mode checks need a POSIX file system");
        }
        File.SetUnixFileMode(path, (UnixFileMode)mode);
    }

    private static int Octal(string digits)
    {
        return Convert.ToInt32(digits, 8);
    }

    // The permission bits as ls prints and chmod takes them (IO/RemotePermissions.cs), without a shell.
    private static void PermissionChecks()
    {
        Equal(RemotePermissions.ParseMode("-rwxr-xr-x"), (int?)Octal("755"), "ls mode: rwxr-xr-x is 755");
        Equal(RemotePermissions.ParseMode("drwxrwxrwt"), (int?)Octal("1777"), "ls mode: sticky folder");
        Equal(RemotePermissions.ParseMode("-rwsr-xr-x"), (int?)Octal("4755"), "ls mode: setuid with execute");
        Equal(RemotePermissions.ParseMode("-rwSr--r--"), (int?)Octal("4644"), "ls mode: setuid without execute (capital S)");
        Equal(RemotePermissions.ParseMode("drwxr-sr-x+"), (int?)Octal("2755"), "ls mode: setgid, with the ACL mark");
        Equal(RemotePermissions.ParseMode("----------"), (int?)0, "ls mode: no permissions");
        Check(RemotePermissions.ParseMode(null) == null && RemotePermissions.ParseMode("") == null && RemotePermissions.ParseMode("total 20") == null, "ls mode: not a mode");
        Check(RemotePermissions.ParseMode("-rwxr-xr-?") == null && RemotePermissions.ParseMode("-rwxr-xr-q") == null, "ls mode: an unknown character is not a mode");
        Check(RemotePermissions.ParseMode("-rwtr-xr-x") == null, "ls mode: t is only valid in the last triplet");
        Check(RemotePermissions.ParseMode("-rwxr-xr") == null, "ls mode: too short");
        foreach (string digits in new[] { "000", "644", "755", "777", "1777", "2755", "4755", "7777" })
        {
            int mode = Octal(digits);
            Equal(RemotePermissions.ParseMode("-" + RemotePermissions.ToSymbolic(mode)), (int?)mode, "symbolic form of " + digits + " reads back");
            Equal(RemotePermissions.ToOctal(mode), digits, "octal form of " + digits);
        }
        Equal(RemotePermissions.ToSymbolic(Octal("4755")), "rwsr-xr-x", "symbolic setuid");
        Equal(RemotePermissions.ToSymbolic(Octal("1644")), "rw-r--r-T", "symbolic sticky without execute");
        Equal(RemotePermissions.Describe(Octal("750")), "rwxr-x--- (750)", "description");
        Equal(RemotePermissions.ToOctal(Octal("5")), "005", "octal form is padded to three digits");

        Check(RemotePermissions.TryParseOctal("7", out int parsed) && parsed == 7, "octal text: one digit");
        Check(RemotePermissions.TryParseOctal(" 0644 ", out parsed) && parsed == Octal("644"), "octal text: spaces and a leading zero");
        Check(RemotePermissions.TryParseOctal("7777", out parsed) && parsed == Octal("7777"), "octal text: four digits");
        Check(!RemotePermissions.TryParseOctal("", out _) && !RemotePermissions.TryParseOctal("789", out _) && !RemotePermissions.TryParseOctal("12345", out _) && !RemotePermissions.TryParseOctal("rwx", out _), "octal text: rejects an empty text, 8 and 9, five digits and letters");

        Check(new RemotePermissionChange().IsEmpty && new RemotePermissionChange().ToChmodMode() == null, "an empty change has no mode");
        Equal(new RemotePermissionChange { SetBits = RemotePermissions.OwnerExecute | RemotePermissions.GroupExecute | RemotePermissions.OtherExecute }.ToChmodMode(), "u+x,g+x,o+x", "chmod mode: execute for all");
        Equal(new RemotePermissionChange { SetBits = RemotePermissions.OwnerRead | RemotePermissions.OwnerWrite, ClearBits = RemotePermissions.OwnerExecute | RemotePermissions.OtherRead | RemotePermissions.OtherWrite }.ToChmodMode(), "u+rw,u-x,o-rw", "chmod mode: set and clear");
        Equal(new RemotePermissionChange { SetBits = RemotePermissions.SetUid | RemotePermissions.Sticky, ClearBits = RemotePermissions.SetGid }.ToChmodMode(), "u+s,g-s,a+t", "chmod mode: special bits");
        Equal(new RemotePermissionChange { ClearBits = RemotePermissions.Sticky }.ToChmodMode(), "a-t", "chmod mode: a mode that starts with a minus never reaches chmod");
        Equal(new RemotePermissionChange { SetBits = RemotePermissions.OwnerRead, ClearBits = RemotePermissions.OwnerRead }.ToChmodMode(), "u+r", "chmod mode: a bit both set and cleared is set");
    }

    // chmod from the file list, run by this shell on a temporary tree (names with spaces, quotes and $).
    private static void ChmodChecks(string root)
    {
        string name = "it's a $file";
        string file = Path.Combine(root, name);
        File.WriteAllText(file, "data");
        SetMode(file, Octal("644"));
        Equal(RunAll(RemoteFileOperations.ChmodCommands(new[] { file }, "+x", ChmodScope.Selected), out string errors), 0, _shell + ": chmod +x" + errors);
        Check((Mode(file) & Octal("111")) != 0 && (Mode(file) & Octal("600")) == Octal("600"), "chmod +x makes the file executable and keeps the rest");

        string tree = Path.Combine(root, "my tree");
        string sub = Path.Combine(tree, "sub dir");
        Directory.CreateDirectory(sub);
        string top = Path.Combine(tree, "top.txt");
        string inner = Path.Combine(sub, "inner $1.txt");
        File.WriteAllText(top, "top");
        File.WriteAllText(inner, "inner");
        void Reset()
        {
            foreach (string path in new[] { tree, sub })
            {
                SetMode(path, Octal("775"));
            }
            foreach (string path in new[] { top, inner })
            {
                SetMode(path, Octal("664"));
            }
        }

        Reset();
        RemotePermissionChange change = new RemotePermissionChange { ClearBits = RemotePermissions.GroupWrite | RemotePermissions.OtherRead, SetBits = RemotePermissions.OwnerExecute };
        string mode = change.ToChmodMode();
        Equal(RunAll(RemoteFileOperations.ChmodCommands(new[] { tree }, mode, ChmodScope.Selected), out errors), 0, _shell + ": chmod of a folder" + errors);
        Equal(Mode(tree), Octal("751"), "a symbolic change touches the named bits of the folder only");
        Equal(Mode(top), Octal("664"), "the contents are not changed without a recursive scope");

        Reset();
        Equal(RunAll(RemoteFileOperations.ChmodCommands(new[] { tree }, mode, ChmodScope.Recursive), out errors), 0, _shell + ": chmod -R" + errors);
        Check(Mode(tree) == Octal("751") && Mode(sub) == Octal("751"), "recursive: the folder and the folder inside are changed");
        Check(Mode(top) == Octal("740") && Mode(inner) == Octal("740"), "recursive: the files get the same bits changed, the other bits stay");

        Reset();
        Equal(RunAll(RemoteFileOperations.ChmodCommands(new[] { tree }, mode, ChmodScope.RecursiveFiles), out errors), 0, _shell + ": files only" + errors);
        Check(Mode(top) == Octal("740") && Mode(inner) == Octal("740"), "files only: the files inside are changed");
        Check(Mode(tree) == Octal("775") && Mode(sub) == Octal("775"), "files only: the folders are not");

        Reset();
        Equal(RunAll(RemoteFileOperations.ChmodCommands(new[] { tree }, mode, ChmodScope.RecursiveFolders), out errors), 0, _shell + ": folders only" + errors);
        Check(Mode(tree) == Octal("751") && Mode(sub) == Octal("751"), "folders only: the folder and the folder inside are changed");
        Check(Mode(top) == Octal("664") && Mode(inner) == Octal("664"), "folders only: the files are not");

        Reset();
        Equal(RemoteFileOperations.ChmodCommands(new[] { top, inner }, mode, ChmodScope.RecursiveFiles).Count, 2, "one command per path when find filters");
        Equal(RunAll(RemoteFileOperations.ChmodCommands(new[] { top, inner }, mode, ChmodScope.RecursiveFiles), out errors), 0, _shell + ": files only, on selected files" + errors);
        Check(Mode(top) == Octal("740") && Mode(inner) == Octal("740"), "files only: a selected file is changed itself");
        Reset();
        Equal(RunAll(RemoteFileOperations.ChmodCommands(new[] { top }, mode, ChmodScope.RecursiveFolders), out errors), 0, _shell + ": folders only, on a selected file" + errors);
        Equal(Mode(top), Octal("664"), "folders only: a selected file is left alone");

        // The special bits, with the "a+t" and "g+s" spelling.
        Reset();
        change = new RemotePermissionChange { SetBits = RemotePermissions.Sticky | RemotePermissions.SetGid };
        Equal(RunAll(RemoteFileOperations.ChmodCommands(new[] { sub }, change.ToChmodMode(), ChmodScope.Selected), out errors), 0, _shell + ": sticky and setgid" + errors);
        Equal(Mode(sub), Octal("3775"), "sticky and setgid are set");
        change = new RemotePermissionChange { ClearBits = RemotePermissions.Sticky | RemotePermissions.SetGid };
        Equal(RunAll(RemoteFileOperations.ChmodCommands(new[] { sub }, change.ToChmodMode(), ChmodScope.Selected), out errors), 0, _shell + ": clear sticky and setgid" + errors);
        Equal(Mode(sub), Octal("775"), "sticky and setgid are cleared");

        // Failures reach the exit status, for chmod and for the find variants.
        string missing = Path.Combine(root, "no such thing");
        foreach (ChmodScope scope in new[] { ChmodScope.Selected, ChmodScope.Recursive, ChmodScope.RecursiveFiles, ChmodScope.RecursiveFolders })
        {
            int exit = RunAll(RemoteFileOperations.ChmodCommands(new[] { missing }, "u+x", scope), out errors);
            Check(exit != 0 && errors.Length > 0, _shell + ": a missing path fails with a message (" + scope + ")");
        }
        // A chmod that fails inside find (here a mode chmod does not accept) is not lost: find itself exits with 0 after it.
        foreach (ChmodScope scope in new[] { ChmodScope.Selected, ChmodScope.Recursive, ChmodScope.RecursiveFiles, ChmodScope.RecursiveFolders })
        {
            Check(RunAll(RemoteFileOperations.ChmodCommands(new[] { tree }, "zz+q", scope), out errors) != 0, _shell + ": a chmod that fails fails the command (" + scope + ")");
        }
        Check(RemoteFileOperations.ChmodCommands(new string[0], "u+x", ChmodScope.Selected).Count == 0 && RemoteFileOperations.ChmodCommands(new[] { top }, null, ChmodScope.Selected).Count == 0, "nothing to do gives no command");
    }

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "smartty-fileops-" + Guid.NewGuid().ToString("N"));
        try
        {
            NameChecks();
            PermissionChecks();
            ScriptNameChecks();
            PackageInstallChecks();
            FileIconChecks();
            FileIconResourceChecks();
            List<string> shells = new List<string> { "/bin/sh" };
            foreach (string busybox in new[] { "/bin/busybox", "/usr/bin/busybox" })
            {
                if (File.Exists(busybox))
                {
                    shells.Add(busybox + " sh");
                    break;
                }
            }
            foreach (string shell in shells)
            {
                _shell = shell;
                _appletPath = null;
                if (shell.Contains("busybox"))
                {
                    _appletPath = Path.Combine(root, "applets");
                    Directory.CreateDirectory(_appletPath);
                    foreach (string applet in new[] { "cp", "mv", "rm", "mkdir", "tar", "sh", "tr", "chmod", "find" })
                    {
                        File.CreateSymbolicLink(Path.Combine(_appletPath, applet), shell.Split(' ')[0]);
                    }
                }
                string shellRoot = Path.Combine(root, Path.GetFileName(shell.Replace(" ", "-")));
                Directory.CreateDirectory(shellRoot);
                CommandChecks(shellRoot);
                ChmodChecks(shellRoot);
                ScriptRunChecks(shellRoot);
                RunInPlaceChecks(shellRoot);
                if (Directory.Exists("/proc/self"))
                {
                    ProcessChecks();
                }
            }
            EditorChecks(root);
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
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
        Console.WriteLine($"All {_passed} checks passed.");
        return 0;
    }
}
