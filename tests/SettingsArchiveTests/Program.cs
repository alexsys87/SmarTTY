using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using VisualTTY;

internal static class Program
{
    private static int _passed;
    private static string _root;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void ExpectThrows<T>(Action action, string name) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            _passed++;
            Console.WriteLine("PASS: " + name);
            return;
        }
        throw new Exception("FAIL (no " + typeof(T).Name + "): " + name);
    }

    private const string SettingsXml = "<?xml version=\"1.0\"?><GlobalSettings><AutoCloseTabs>true</AutoCloseTabs></GlobalSettings>";
    private const string KnownHostsXml = "<?xml version=\"1.0\"?><ArrayOfKnownHostKey><KnownHostKey><HostName>h</HostName><KeyType>ssh-rsa</KeyType><KeyBase64>AAAA</KeyBase64></KnownHostKey></ArrayOfKnownHostKey>";
    private const string GuiXml = "<?xml version=\"1.0\"?><CommonGUIPreferences><X>1</X></CommonGUIPreferences>";
    private const string TftpXml = "<?xml version=\"1.0\"?><TftpFirmwareSettings><ServerIp>192.168.1.10</ServerIp><LoadAddress>0x80060000</LoadAddress></TftpFirmwareSettings>";
    private const string RawXml = "<?xml version=\"1.0\"?><RawConnectionSettings><Icon>a.png</Icon></RawConnectionSettings>";

    private static string Ssh(string host, string password)
    {
        return "<?xml version=\"1.0\"?><SSHConnectionSettings>" +
               (password == null ? "" : "<EncryptedSavedPassword>" + password + "</EncryptedSavedPassword>") +
               "<HostName>" + host + "</HostName><UserName>root</UserName></SSHConnectionSettings>";
    }

    private static string SshWithProxy(string host, string encryptedProxyAuth, string legacyProxyAuth)
    {
        return "<?xml version=\"1.0\"?><SSHConnectionSettings>" +
               (encryptedProxyAuth == null ? "" : "<EncryptedProxyAuth>" + encryptedProxyAuth + "</EncryptedProxyAuth>") +
               "<HostName>" + host + "</HostName><UserName>root</UserName>" +
               (legacyProxyAuth == null ? "" : "<ProxyAuth>" + legacyProxyAuth + "</ProxyAuth>") +
               "<ProxyServer>proxy.example.com</ProxyServer><ProxyPort>3128</ProxyPort></SSHConnectionSettings>";
    }

    private static string NewDir(string name)
    {
        string path = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Put(string dir, string relative, string content)
    {
        string path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    // Layout: data = %APPDATA%\SmarTTY, local = %LOCALAPPDATA%\SmarTTY.
    private static void Populate(string data, string local)
    {
        Put(data, "Settings.xml", SettingsXml);
        Put(data, "KnownHosts.xml", KnownHostsXml);
        Put(data, "SavedConnections/root@192.168.1.1.sshconnection", Ssh("192.168.1.1", "SECRETPWD"));
        Put(data, "SavedConnections/root@10.0.0.2.sshconnection", Ssh("10.0.0.2", null));
        Put(data, "SavedConnections/USB Serial Port_COM3.rawconnection", RawXml);
        Put(data, "SavedConnections/_update_timestamp.txt", "ts");
        Put(data, "SavedConnections/old.sshconnection.bak", "junk");
        Put(data, "unrelated.txt", "x");
        Put(local, "GUIPreferences.xml", GuiXml);
        Put(local, "TftpFirmware.xml", TftpXml);
        Put(local, "other.dat", "x");
    }

    private static List<SettingsArchive.TarEntry> ReadArchive(string path)
    {
        using (FileStream f = File.OpenRead(path))
        using (GZipStream gz = new GZipStream(f, CompressionMode.Decompress))
        {
            return SettingsArchive.TarReader.ReadAll(gz);
        }
    }

    private static void MakeArchive(string path, params KeyValuePair<string, byte[]>[] entries)
    {
        using (FileStream f = File.Create(path))
        using (GZipStream gz = new GZipStream(f, CompressionLevel.Optimal))
        {
            foreach (var e in entries)
            {
                SettingsArchive.TarWriter.WriteFile(gz, e.Key, e.Value, DateTime.UtcNow);
            }
            SettingsArchive.TarWriter.Finish(gz);
        }
    }

    private static KeyValuePair<string, byte[]> E(string name, string content)
    {
        return new KeyValuePair<string, byte[]>(name, Encoding.UTF8.GetBytes(content));
    }

    private static string Read(string dir, string relative)
    {
        return File.ReadAllText(Path.Combine(dir, relative));
    }

    private static string RunTar(string args, string workDir)
    {
        ProcessStartInfo psi = new ProcessStartInfo("tar", args) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workDir };
        using (Process p = Process.Start(psi))
        {
            string output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new Exception("tar " + args + " failed");
            }
            return output;
        }
    }

    private static bool TarAvailable()
    {
        try
        {
            RunTar("--version", _root);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Main()
    {
        _root = Path.Combine(Path.GetTempPath(), "smartty-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        try
        {
            Run();
            Console.WriteLine("All " + _passed + " regression checks passed.");
        }
        finally
        {
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    private static void Run()
    {
        string srcData = NewDir("srcdata"), srcLocal = NewDir("srclocal");
        Populate(srcData, srcLocal);

        // ---- export without passwords
        string archive = Path.Combine(NewDir("out"), "settings.tar.gz");
        var export = SettingsArchive.Export(archive, srcData, srcLocal, false);
        var entries = ReadArchive(archive);
        var names = entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Check(export.FileCount == 6 && names.Length == 6, "export writes exactly the 6 legacy settings files");
        Check(names.SequenceEqual(new[] {
            "KnownHosts.xml", "Local/GUIPreferences.xml", "SavedConnections/USB Serial Port_COM3.rawconnection",
            "SavedConnections/root@10.0.0.2.sshconnection", "SavedConnections/root@192.168.1.1.sshconnection", "Settings.xml" }, StringComparer.Ordinal),
            "export entry names follow the documented layout (no timestamp, .bak or unrelated files)");
        Check(!File.Exists(archive + ".tmp"), "export leaves no temp file");
        Check(entries.All(e => !Encoding.UTF8.GetString(e.Data).Contains("SECRETPWD") && !Encoding.UTF8.GetString(e.Data).Contains("EncryptedSavedPassword")),
            "saved passwords are left out by default");
        var keep = entries.First(e => e.Name.EndsWith("root@192.168.1.1.sshconnection"));
        Check(XDocument.Parse(Encoding.UTF8.GetString(keep.Data)).Root.Element("HostName").Value == "192.168.1.1", "stripped profile keeps its other fields");

        // ---- export with passwords
        string archiveWithPwd = Path.Combine(NewDir("out"), "with-pwd.tar.gz");
        SettingsArchive.Export(archiveWithPwd, srcData, srcLocal, true);
        Check(ReadArchive(archiveWithPwd).Any(e => Encoding.UTF8.GetString(e.Data).Contains("SECRETPWD")), "includePasswords keeps saved passwords");

        // ---- import into a fresh profile
        string data = NewDir("dstdata"), local = NewDir("dstlocal");
        var import = SettingsArchive.Import(archive, data, local, null);
        Check(import.SettingsImported && import.KnownHostsImported && import.GuiPreferencesImported && import.SshProfiles == 2 && import.RawProfiles == 1 && import.FileCount == 6, "import reports what it restored");
        Check(Read(data, "Settings.xml") == SettingsXml && Read(data, "KnownHosts.xml") == KnownHostsXml && Read(local, "GUIPreferences.xml") == GuiXml, "settings, known hosts and GUI preferences are restored verbatim (GUI prefs go to the local directory)");
        Check(Read(data, "SavedConnections/USB Serial Port_COM3.rawconnection") == RawXml, "raw profile restored verbatim");
        Check(!Read(data, "SavedConnections/root@192.168.1.1.sshconnection").Contains("EncryptedSavedPassword"), "imported profile has no password");
        Check(Directory.GetFiles(Path.Combine(data, "SavedConnections"), "*.tmp").Length == 0, "import leaves no temp files");

        // ---- merge semantics, password preservation, backup
        string data2 = NewDir("merge-data"), local2 = NewDir("merge-local");
        Put(data2, "Settings.xml", "<GlobalSettings><AutoCloseTabs>false</AutoCloseTabs></GlobalSettings>");
        Put(data2, "SavedConnections/root@192.168.1.1.sshconnection", Ssh("192.168.1.1", "LOCALPWD"));
        Put(data2, "SavedConnections/admin@other.sshconnection", Ssh("other", "OTHERPWD"));
        string backup = Path.Combine(data2, "Backups", "b.tar.gz");
        var merged = SettingsArchive.Import(archive, data2, local2, backup);
        Check(merged.BackupPath == backup && File.Exists(backup), "import creates a backup of the current settings first");
        var backupEntries = ReadArchive(backup);
        Check(backupEntries.Any(e => Encoding.UTF8.GetString(e.Data).Contains("LOCALPWD")) && backupEntries.Any(e => e.Name == "Settings.xml" && Encoding.UTF8.GetString(e.Data).Contains("false")), "backup contains the previous settings and passwords");
        Check(Read(data2, "Settings.xml") == SettingsXml, "Settings.xml replaced");
        Check(Read(data2, "SavedConnections/root@192.168.1.1.sshconnection").Contains("LOCALPWD"), "local saved password survives importing a password-less profile");
        Check(Read(data2, "SavedConnections/admin@other.sshconnection").Contains("OTHERPWD"), "profiles not in the archive are kept");
        Check(Read(data2, "SavedConnections/root@192.168.1.1.sshconnection").Contains("192.168.1.1") && XDocument.Parse(Read(data2, "SavedConnections/root@192.168.1.1.sshconnection")).Root.Name.LocalName == "SSHConnectionSettings", "merged profile is still valid XML");
        SettingsArchive.Import(archiveWithPwd, data2, local2, null);
        Check(Read(data2, "SavedConnections/root@192.168.1.1.sshconnection").Contains("SECRETPWD") && !Read(data2, "SavedConnections/root@192.168.1.1.sshconnection").Contains("LOCALPWD"), "a password carried by the archive replaces the local one");

        // ---- long names (GNU @LongLink)
        string longName = new string('a', 130) + "@host.sshconnection";
        string longArchive = Path.Combine(NewDir("out"), "long.tar.gz");
        string data3 = NewDir("long-data"), local3 = NewDir("long-local");
        Put(data3, "SavedConnections/" + longName, Ssh("host", null));
        SettingsArchive.Export(longArchive, data3, local3, false);
        string data4 = NewDir("long-dst");
        SettingsArchive.Import(longArchive, data4, NewDir("l"), null);
        Check(File.Exists(Path.Combine(data4, "SavedConnections", longName)), "file names longer than 100 bytes round-trip");

        // ---- interoperability with the system tar
        if (TarAvailable())
        {
            string listing = RunTar("-tzf \"" + archive + "\"", _root);
            Check(listing.Contains("Settings.xml") && listing.Contains("SavedConnections/USB Serial Port_COM3.rawconnection"), "system tar can list our archive");
            string extract = NewDir("extract");
            RunTar("-xzf \"" + archive + "\" -C \"" + extract + "\"", _root);
            Check(File.ReadAllText(Path.Combine(extract, "Settings.xml")) == SettingsXml, "system tar can extract our archive");
            foreach (string format in new[] { "gnu", "pax", "ustar" })
            {
                string stage = NewDir("stage-" + format);
                Put(stage, "Settings.xml", SettingsXml);
                Put(stage, "SavedConnections/" + longName, Ssh("host", null));
                Put(stage, "SavedConnections/root@192.168.1.1.sshconnection", Ssh("192.168.1.1", null));
                Put(stage, "Local/GUIPreferences.xml", GuiXml);
                string theirs = Path.Combine(NewDir("out"), "tar-" + format + ".tar.gz");
                try
                {
                    RunTar("--format=" + format + " -czf \"" + theirs + "\" -C \"" + stage + "\" .", _root);
                }
                catch (Exception)
                {
                    Console.WriteLine("SKIP: tar --format=" + format + " unsupported");
                    continue;
                }
                string dst = NewDir("tar-dst"), dstLocal = NewDir("tar-local");
                var r = SettingsArchive.Import(theirs, dst, dstLocal, null);
                bool longOk = format == "ustar" || File.Exists(Path.Combine(dst, "SavedConnections", longName));
                Check(r.SettingsImported && r.GuiPreferencesImported && r.SshProfiles >= 1 && longOk && Read(dstLocal, "GUIPreferences.xml") == GuiXml, "imports an archive made by system tar (" + format + ")");
            }
        }
        else
        {
            Console.WriteLine("SKIP: system tar interoperability (tar not found)");
        }

        // ---- hostile entry names are ignored, never written
        string hostile = Path.Combine(NewDir("out"), "hostile.tar.gz");
        MakeArchive(hostile,
            E("Settings.xml", SettingsXml),
            E("../evil.sshconnection", Ssh("evil", null)),
            E("SavedConnections/../evil2.sshconnection", Ssh("evil", null)),
            E("/abs/evil3.sshconnection", Ssh("evil", null)),
            E("SavedConnections/a\\b.sshconnection", Ssh("evil", null)),
            E("SavedConnections/sub/x.sshconnection", Ssh("evil", null)),
            E("SavedConnections/CON.sshconnection", Ssh("evil", null)),
            E("SavedConnections/nul.rawconnection", RawXml),
            E("SavedConnections/c:evil.sshconnection", Ssh("evil", null)),
            E("SavedConnections/.sshconnection", Ssh("evil", null)),
            E("Local/other.xml", GuiXml),
            E("random.txt", "x"));
        string hostileParent = NewDir("hostile-parent");
        string hdata = Path.Combine(hostileParent, "data"), hlocal = Path.Combine(hostileParent, "local");
        var hr = SettingsArchive.Import(hostile, hdata, hlocal, null);
        Check(hr.SettingsImported && hr.FileCount == 1 && hr.Ignored.Count == 11, "hostile names are ignored and reported");
        Check(Directory.GetFiles(hostileParent, "*", SearchOption.AllDirectories).Length == 1 && !File.Exists(Path.Combine(hostileParent, "evil.sshconnection")), "nothing is written outside the whitelist");

        // ---- invalid content aborts before anything is written
        Action<string, string, string> expectRejected = (label, name, content) =>
        {
            string bad = Path.Combine(NewDir("out"), label + ".tar.gz");
            MakeArchive(bad, E("Settings.xml", SettingsXml), E(name, content));
            string d = NewDir("rej-data"), l = NewDir("rej-local");
            try
            {
                SettingsArchive.Import(bad, d, l, null);
                throw new Exception("FAIL (accepted): " + label);
            }
            catch (InvalidDataException)
            {
            }
            Check(Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length == 0, label + " is rejected without writing anything");
        };
        expectRejected("wrong-root", "KnownHosts.xml", "<Foo/>");
        expectRejected("not-xml", "KnownHosts.xml", "this is not xml");
        expectRejected("dtd", "KnownHosts.xml", "<!DOCTYPE x [<!ENTITY e \"y\">]><ArrayOfKnownHostKey/>");
        expectRejected("wrong-profile-root", "SavedConnections/a.sshconnection", RawXml);

        // ---- limits and corruption
        string big = Path.Combine(NewDir("out"), "big.tar.gz");
        MakeArchive(big, new KeyValuePair<string, byte[]>("Settings.xml", new byte[17 * 1024 * 1024]));
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(big, NewDir("x"), NewDir("y"), null), "an entry over the size limit is rejected");

        string many = Path.Combine(NewDir("out"), "total.tar.gz");
        MakeArchive(many, Enumerable.Range(0, 5).Select(i => new KeyValuePair<string, byte[]>("SavedConnections/p" + i + ".sshconnection", new byte[15 * 1024 * 1024])).ToArray());
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(many, NewDir("x"), NewDir("y"), null), "total unpacked size limit is enforced");

        byte[] raw;
        using (MemoryStream ms = new MemoryStream())
        {
            using (GZipStream gz = new GZipStream(new FileStream(archive, FileMode.Open), CompressionMode.Decompress)) gz.CopyTo(ms);
            raw = ms.ToArray();
        }
        Action<string, byte[]> writeGz = (path, bytes) =>
        {
            using (FileStream f = File.Create(path))
            using (GZipStream gz = new GZipStream(f, CompressionLevel.Optimal)) gz.Write(bytes, 0, bytes.Length);
        };
        byte[] damaged = (byte[])raw.Clone();
        damaged[10] ^= 0x55; // inside the first header's name field
        string damagedPath = Path.Combine(NewDir("out"), "damaged.tar.gz");
        writeGz(damagedPath, damaged);
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(damagedPath, NewDir("x"), NewDir("y"), null), "a damaged header (checksum) is rejected");

        byte[] cut = raw.Take(raw.Length - 1024 - 700).ToArray();
        string cutPath = Path.Combine(NewDir("out"), "cut.tar.gz");
        writeGz(cutPath, cut);
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(cutPath, NewDir("x"), NewDir("y"), null), "a truncated archive is rejected");

        byte[] noMarker = raw.Take(raw.Length - 1024).ToArray();
        string noMarkerPath = Path.Combine(NewDir("out"), "nomarker.tar.gz");
        writeGz(noMarkerPath, noMarker);
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(noMarkerPath, NewDir("x"), NewDir("y"), null), "an archive without end marker is rejected");

        string notGz = Path.Combine(NewDir("out"), "plain.tar.gz");
        File.WriteAllText(notGz, "definitely not gzip");
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(notGz, NewDir("x"), NewDir("y"), null), "a non-gzip file is rejected");

        string unrelated = Path.Combine(NewDir("out"), "unrelated.tar.gz");
        MakeArchive(unrelated, E("readme.txt", "hi"));
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(unrelated, NewDir("x"), NewDir("y"), null), "an archive with no SmarTTY files is rejected");

        string dup = Path.Combine(NewDir("out"), "dup.tar.gz");
        MakeArchive(dup, E("Settings.xml", SettingsXml), E("./Settings.xml", SettingsXml));
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(dup, NewDir("x"), NewDir("y"), null), "duplicate entries are rejected");

        // ---- export edge cases
        ExpectThrows<InvalidOperationException>(() => SettingsArchive.Export(Path.Combine(NewDir("out"), "e.tar.gz"), NewDir("empty1"), NewDir("empty2"), false), "exporting with nothing saved is an error");

        string data5 = NewDir("bad-data"), local5 = NewDir("bad-local");
        Put(data5, "Settings.xml", SettingsXml);
        Put(data5, "SavedConnections/broken.sshconnection", "<<<not xml");
        Put(data5, "SavedConnections/wrongroot.rawconnection", "<Foo/>");
        string skipArchive = Path.Combine(NewDir("out"), "skip.tar.gz");
        var skipResult = SettingsArchive.Export(skipArchive, data5, local5, true);
        Check(skipResult.FileCount == 1 && skipResult.Skipped.Count == 2, "malformed profiles are skipped and reported on export");
        SettingsArchive.Import(skipArchive, NewDir("x"), NewDir("y"), null);
        Check(true, "an archive exported from partly broken settings still imports");

        string again = Path.Combine(NewDir("out"), "settings.tar.gz");
        Check(SettingsArchive.Export(again, srcData, srcLocal, false).FileCount == 6, "exporting over an existing file works");
        Check(SettingsArchive.DefaultBackupPath(@"C:\data").Contains("before-import-"), "backup path helper");

        // ---- telnet profiles carry a saved password (the automatic login) inside <Connection>
        string telnetData = NewDir("telnet-data"), telnetLocal = NewDir("telnet-local");
        Put(telnetData, "Settings.xml", SettingsXml);
        Put(telnetData, "SavedConnections/telnet_10.0.0.9_23.rawconnection", Telnet("10.0.0.9", "TELNETSECRET"));
        Put(telnetData, "SavedConnections/telnet_10.0.0.8_23.rawconnection", Telnet("10.0.0.8", null));
        string telnetArchive = Path.Combine(NewDir("out"), "telnet.tar.gz");
        SettingsArchive.Export(telnetArchive, telnetData, telnetLocal, false);
        var telnetEntries = ReadArchive(telnetArchive);
        Check(telnetEntries.Count(e => e.Name.EndsWith(".rawconnection")) == 2, "telnet profiles are exported");
        Check(telnetEntries.All(e => !Encoding.UTF8.GetString(e.Data).Contains("TELNETSECRET") && !Encoding.UTF8.GetString(e.Data).Contains("EncryptedSavedPassword")),
            "telnet passwords are left out of an export without passwords");
        Check(Encoding.UTF8.GetString(telnetEntries.First(e => e.Name.Contains("10.0.0.9")).Data).Contains("<AutoLoginUserName>admin</AutoLoginUserName>"),
            "a stripped telnet profile keeps its user name and other fields");
        string telnetArchiveWithPwd = Path.Combine(NewDir("out"), "telnet-pwd.tar.gz");
        SettingsArchive.Export(telnetArchiveWithPwd, telnetData, telnetLocal, true);
        Check(ReadArchive(telnetArchiveWithPwd).Any(e => Encoding.UTF8.GetString(e.Data).Contains("TELNETSECRET")), "includePasswords keeps telnet passwords");

        // importing a password-less telnet profile over a local one keeps the local password, inside Connection
        string telnetData2 = NewDir("telnet-merge-data"), telnetLocal2 = NewDir("telnet-merge-local");
        Put(telnetData2, "SavedConnections/telnet_10.0.0.9_23.rawconnection", Telnet("10.0.0.9", "LOCALTELNETPWD"));
        SettingsArchive.Import(telnetArchive, telnetData2, telnetLocal2, null);
        string mergedTelnet = Read(telnetData2, "SavedConnections/telnet_10.0.0.9_23.rawconnection");
        XDocument mergedDocument = XDocument.Parse(mergedTelnet);
        Check(mergedTelnet.Contains("LOCALTELNETPWD") && mergedDocument.Root.Element("Settings").Element("Connection").Element("EncryptedSavedPassword") != null,
            "the local telnet password survives importing a password-less profile and stays in Connection");
        Check(mergedDocument.Root.Element("Settings").Element("Connection").Element("Host").Value == "10.0.0.9", "the merged telnet profile keeps the imported fields");
        SettingsArchive.Import(telnetArchiveWithPwd, telnetData2, telnetLocal2, null);
        string replaced = Read(telnetData2, "SavedConnections/telnet_10.0.0.9_23.rawconnection");
        Check(replaced.Contains("TELNETSECRET") && !replaced.Contains("LOCALTELNETPWD"), "a password carried by the archive replaces the local telnet one");
        // a raw profile without any password is not touched by the merge
        Put(telnetData2, "SavedConnections/USB Serial Port_COM3.rawconnection", RawXml);
        SettingsArchive.Import(archive, telnetData2, telnetLocal2, null);
        Check(Read(telnetData2, "SavedConnections/USB Serial Port_COM3.rawconnection") == RawXml, "a raw profile without a password is imported unchanged");

        RunSelectablePartsChecks();
        RunCompletionsGzipChecks();
        RunTftpFirmwareChecks();
    }

    // The TFTP server / U-Boot upload window keeps its settings in %LOCALAPPDATA%\SmarTTY\TftpFirmware.xml.
    private static void RunTftpFirmwareChecks()
    {
        string srcData = NewDir("tftp-srcdata"), srcLocal = NewDir("tftp-srclocal");
        Put(srcLocal, "TftpFirmware.xml", TftpXml);
        SettingsLocations src = MakeLocations(srcData, srcLocal, null, null, null);

        Check((SettingsArchive.Describe(src).Available & SettingsParts.TftpFirmware) != SettingsParts.None, "describe finds the TFTP settings file");

        string archive = Path.Combine(NewDir("tftp-out"), "tftp.tar.gz");
        var exported = SettingsArchive.Export(archive, src, SettingsParts.TftpFirmware);
        var entries = ReadArchive(archive);
        Check(exported.FileCount == 1 && entries.Count == 1 && entries[0].Name == "Local/TftpFirmware.xml", "the TFTP settings go into the archive as Local/TftpFirmware.xml");
        Check(Encoding.UTF8.GetString(entries[0].Data) == TftpXml, "the TFTP settings are stored as they are");
        Check(SettingsArchive.Inspect(archive).Available == SettingsParts.TftpFirmware, "inspect offers the TFTP settings of an archive");

        // selected by default, so a normal export carries them
        Check((SettingsParts.Default & SettingsParts.TftpFirmware) != SettingsParts.None, "the TFTP settings are selected by default");

        // import writes the file into the local directory (not %APPDATA%)
        string dstData = NewDir("tftp-dstdata"), dstLocal = NewDir("tftp-dstlocal");
        var imported = SettingsArchive.Import(archive, MakeLocations(dstData, dstLocal, null, null, null), SettingsParts.TftpFirmware, null);
        Check(imported.TftpFirmwareImported && imported.FileCount == 1 && Read(dstLocal, "TftpFirmware.xml") == TftpXml, "import restores the TFTP settings into the local directory");
        Check(!File.Exists(Path.Combine(dstData, "TftpFirmware.xml")), "the TFTP settings are not written to %APPDATA%");

        // importing another part leaves the TFTP file alone
        string keepLocal = NewDir("tftp-keeplocal");
        Put(keepLocal, "TftpFirmware.xml", TftpXml);
        Put(srcData, "Settings.xml", SettingsXml);
        string mainArchive = Path.Combine(NewDir("tftp-out"), "main.tar.gz");
        SettingsArchive.Export(mainArchive, src, SettingsParts.MainSettings);
        SettingsArchive.Import(mainArchive, MakeLocations(NewDir("tftp-dstdata2"), keepLocal, null, null, null), SettingsParts.MainSettings, null);
        Check(Read(keepLocal, "TftpFirmware.xml") == TftpXml, "importing another part does not touch the TFTP settings");

        // a damaged TFTP entry is rejected before anything is written
        string wrongRoot = Path.Combine(NewDir("tftp-out"), "wrong-root.tar.gz");
        MakeArchive(wrongRoot, E("Local/TftpFirmware.xml", "<GlobalSettings/>"));
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(wrongRoot, NewDir("tftp-x"), NewDir("tftp-y"), null), "a TFTP entry with another root element is rejected");
    }

    private static byte[] Gzip(string text)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            using (System.IO.Compression.GZipStream gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Compress, true))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                gzip.Write(bytes, 0, bytes.Length);
            }
            return stream.ToArray();
        }
    }

    private static string Gunzip(string path)
    {
        using (FileStream file = File.OpenRead(path))
        using (System.IO.Compression.GZipStream gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
        using (StreamReader reader = new StreamReader(gzip, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

    // CommandCompletions.xml.gz is what SmarTTY reads when there is no plain CommandCompletions.xml (the editor can save it that way).
    private static void RunCompletionsGzipChecks()
    {
        const string PlainXml = "<?xml version=\"1.0\"?><CommandCompletionDatabase><Completions><CommandCompletion><Command>plain</Command></CommandCompletion></Completions></CommandCompletionDatabase>";
        string app = NewDir("gz-app"), data = NewDir("gz-data"), local = NewDir("gz-local");
        string xmlPath = Path.Combine(app, "CommandCompletions.xml");
        File.WriteAllBytes(xmlPath + ".gz", Gzip(CompletionsXml));
        SettingsLocations loc = MakeLocations(data, local, xmlPath, null, null);

        Check((SettingsArchive.Describe(loc).Available & SettingsParts.CommandCompletions) != SettingsParts.None, "describe finds CommandCompletions.xml.gz when there is no plain file");
        string archive = Path.Combine(NewDir("gzout"), "gz.tar.gz");
        var exported = SettingsArchive.Export(archive, loc, SettingsParts.CommandCompletions);
        var entries = ReadArchive(archive);
        Check(exported.FileCount == 1 && exported.NotFound.Count == 0 && entries.Count == 1 && entries[0].Name == "CommandCompletions.xml.gz", "export takes CommandCompletions.xml.gz when it is the only form");
        Check(entries[0].Data.SequenceEqual(File.ReadAllBytes(xmlPath + ".gz")), "the gzip file is stored as it is");
        Check(SettingsArchive.Inspect(archive).Available == SettingsParts.CommandCompletions, "inspect offers the command completions of a gzip entry");

        Put(app, "CommandCompletions.xml", PlainXml);
        string both = Path.Combine(NewDir("gzout"), "both.tar.gz");
        SettingsArchive.Export(both, loc, SettingsParts.CommandCompletions);
        var bothEntries = ReadArchive(both);
        Check(bothEntries.Count == 1 && bothEntries[0].Name == "CommandCompletions.xml" && Encoding.UTF8.GetString(bothEntries[0].Data).Contains("plain"), "when both forms exist the plain file wins, as in SmarTTY");
        File.Delete(xmlPath);

        // import keeps the form of the archive and removes the other one
        string dstApp = NewDir("gz-dstapp");
        string dstXml = Path.Combine(dstApp, "CommandCompletions.xml");
        Put(dstApp, "CommandCompletions.xml", PlainXml);
        SettingsLocations dst = MakeLocations(NewDir("gz-dstdata"), NewDir("gz-dstlocal"), dstXml, null, null);
        string backup = Path.Combine(NewDir("gzout"), "backup.tar.gz");
        var imported = SettingsArchive.Import(archive, dst, SettingsParts.CommandCompletions, backup);
        Check(imported.CommandCompletionsImported && File.Exists(dstXml + ".gz") && !File.Exists(dstXml) && Gunzip(dstXml + ".gz") == CompletionsXml, "a gzip entry is imported as .xml.gz and replaces the plain file");
        Check(ReadArchive(backup).Any(e => e.Name == "CommandCompletions.xml" && Encoding.UTF8.GetString(e.Data).Contains("plain")), "the replaced plain file is in the backup");
        var back = SettingsArchive.Import(both, dst, SettingsParts.CommandCompletions, Path.Combine(NewDir("gzout"), "backup2.tar.gz"));
        Check(back.CommandCompletionsImported && File.Exists(dstXml) && !File.Exists(dstXml + ".gz") && File.ReadAllText(dstXml).Contains("plain"), "a plain entry is imported as .xml and replaces the gzip file");

        // validation
        string twice = Path.Combine(NewDir("gzout"), "twice.tar.gz");
        MakeArchive(twice, E("CommandCompletions.xml", PlainXml), new KeyValuePair<string, byte[]>("CommandCompletions.xml.gz", Gzip(PlainXml)));
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(twice, dst, SettingsParts.All, null), "both forms in one archive are rejected");
        string notGzip = Path.Combine(NewDir("gzout"), "notgzip.tar.gz");
        MakeArchive(notGzip, E("CommandCompletions.xml.gz", "this is not gzip data"));
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(notGzip, dst, SettingsParts.All, null), "a gzip entry that is not gzip data is rejected");
        string wrongRoot = Path.Combine(NewDir("gzout"), "wrongroot.tar.gz");
        MakeArchive(wrongRoot, new KeyValuePair<string, byte[]>("CommandCompletions.xml.gz", Gzip("<GlobalSettings />")));
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(wrongRoot, dst, SettingsParts.All, null), "a gzip entry with another root element is rejected");
        string notXml = Path.Combine(NewDir("gzout"), "notxml.tar.gz");
        MakeArchive(notXml, new KeyValuePair<string, byte[]>("CommandCompletions.xml.gz", Gzip("not xml at all")));
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(notXml, dst, SettingsParts.All, null), "a gzip entry that holds no XML is rejected");

        // a damaged gzip file is skipped on export instead of producing an archive that cannot be imported
        string badApp = NewDir("gz-badapp");
        File.WriteAllBytes(Path.Combine(badApp, "CommandCompletions.xml.gz"), new byte[] { 1, 2, 3, 4 });
        string badData = NewDir("gz-baddata");
        Put(badData, "Settings.xml", SettingsXml);
        var skipped = SettingsArchive.Export(Path.Combine(NewDir("gzout"), "bad.tar.gz"), MakeLocations(badData, NewDir("gz-badlocal"), Path.Combine(badApp, "CommandCompletions.xml"), null, null), SettingsParts.MainSettings | SettingsParts.CommandCompletions);
        Check(skipped.FileCount == 1 && skipped.Skipped.Contains("CommandCompletions.xml.gz"), "a damaged gzip file is skipped on export");
    }

    private sealed class FakeKeyStore : ISettingsKeyStore
    {
        public byte[] SshKey;
        public byte[] PasswordKey;
        public bool FailSshExport;

        public bool HasSshKey { get { return SshKey != null; } }
        public bool HasPasswordKey { get { return PasswordKey != null; } }

        public byte[] ExportSshKey()
        {
            if (FailSshExport)
            {
                throw new System.Security.Cryptography.CryptographicException("not exportable");
            }
            return SshKey;
        }

        public byte[] ExportPasswordKey() { return PasswordKey; }
        public void ImportSshKey(byte[] data) { SshKey = data; }
        public void ImportPasswordKey(byte[] data) { PasswordKey = data; }
    }

    private const string RsaKeyXml = "<?xml version=\"1.0\"?><RSAParameters><Exponent>AQAB</Exponent><Modulus>AAAA</Modulus><D>BBBB</D></RSAParameters>";
    private const string CompletionsXml = "<?xml version=\"1.0\"?><CommandCompletionDatabase><Completions /></CommandCompletionDatabase>";
    private const string OpenSshPrivate = "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n";
    private const string OpenSshPublic = "ssh-ed25519 AAAAC3Nza user@host\n";

    private static SettingsLocations MakeLocations(string data, string local, string completions, string sshDir, ISettingsKeyStore keys)
    {
        return new SettingsLocations { DataDirectory = data, LocalDataDirectory = local, CommandCompletionsPath = completions, UserSshDirectory = sshDir, KeyStore = keys };
    }

    private static void RunSelectablePartsChecks()
    {
        // Source computer: all settings, CommandCompletions.xml, both CSP keys and OpenSSH key files.
        string srcData = NewDir("sel-srcdata"), srcLocal = NewDir("sel-srclocal"), srcApp = NewDir("sel-srcapp"), srcSsh = NewDir("sel-srcssh");
        Populate(srcData, srcLocal);
        Put(srcApp, "CommandCompletions.xml", CompletionsXml);
        Put(srcSsh, "id_ed25519", OpenSshPrivate);
        Put(srcSsh, "id_ed25519.pub", OpenSshPublic);
        Put(srcSsh, "known_hosts", "not exported");
        Put(srcSsh, "config", "not exported");
        FakeKeyStore srcKeys = new FakeKeyStore { SshKey = Encoding.UTF8.GetBytes(RsaKeyXml), PasswordKey = Encoding.UTF8.GetBytes(RsaKeyXml.Replace("BBBB", "CCCC")) };
        SettingsLocations src = MakeLocations(srcData, srcLocal, Path.Combine(srcApp, "CommandCompletions.xml"), srcSsh, srcKeys);

        // ---- describe
        SettingsContents described = SettingsArchive.Describe(src);
        Check(described.Available == SettingsParts.All, "describe finds every part, including passwords, keys and command completions");
        Check(described.SshProfiles == 2 && described.RawProfiles == 1 && described.OpenSshFiles.Count == 2, "describe counts profiles and OpenSSH key files");
        SettingsContents bare = SettingsArchive.Describe(MakeLocations(NewDir("sel-empty"), NewDir("sel-empty2"), null, null, null));
        Check(bare.Available == SettingsParts.None, "describe on a computer without settings finds nothing");

        // ---- export of selected parts only
        string onlyKeys = Path.Combine(NewDir("out"), "keys.tar.gz");
        var keysExport = SettingsArchive.Export(onlyKeys, src, SettingsParts.Keys);
        var keyNames = ReadArchive(onlyKeys).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Check(keysExport.FileCount == 4 && keyNames.SequenceEqual(new[] { "Keys/SmarTTY-password-key.xml", "Keys/SmarTTY-ssh-key.xml", "OpenSSH/id_ed25519", "OpenSSH/id_ed25519.pub" }, StringComparer.Ordinal),
            "a keys-only export has the CSP keys and the default OpenSSH files, but not known_hosts or config");

        string selective = Path.Combine(NewDir("out"), "selective.tar.gz");
        SettingsArchive.Export(selective, src, SettingsParts.MainSettings | SettingsParts.CommandCompletions | SettingsParts.RawProfiles);
        var selectiveNames = ReadArchive(selective).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Check(selectiveNames.SequenceEqual(new[] { "CommandCompletions.xml", "SavedConnections/USB Serial Port_COM3.rawconnection", "Settings.xml" }, StringComparer.Ordinal),
            "only the selected parts are exported (no SSH profiles, no known hosts)");

        string notFoundArchive = Path.Combine(NewDir("out"), "notfound.tar.gz");
        var partial = SettingsArchive.Export(notFoundArchive, MakeLocations(srcData, srcLocal, null, null, null), SettingsParts.Legacy | SettingsParts.CommandCompletions | SettingsParts.SshKey | SettingsParts.OpenSshKeys);
        Check(partial.NotFound.Contains("command completions") && partial.NotFound.Contains("SmarTTY SSH key") && partial.NotFound.Contains("OpenSSH keys") && partial.FileCount == 6,
            "selected parts that do not exist are reported and the rest is still exported");
        ExpectThrows<InvalidOperationException>(() => SettingsArchive.Export(Path.Combine(NewDir("out"), "none.tar.gz"), MakeLocations(NewDir("sel-e1"), NewDir("sel-e2"), null, null, null), SettingsParts.Keys), "exporting only parts that do not exist is an error");

        FakeKeyStore failing = new FakeKeyStore { SshKey = Encoding.UTF8.GetBytes(RsaKeyXml), FailSshExport = true };
        var failedKey = SettingsArchive.Export(Path.Combine(NewDir("out"), "failing.tar.gz"), MakeLocations(srcData, srcLocal, null, null, failing), SettingsParts.MainSettings | SettingsParts.SshKey);
        Check(failedKey.Skipped.Contains("Keys/SmarTTY-ssh-key.xml") && failedKey.FileCount == 1, "a key that cannot be exported is skipped without failing the export");

        // ---- inspect
        string everything = Path.Combine(NewDir("out"), "everything.tar.gz");
        SettingsArchive.Export(everything, src, SettingsParts.All);
        SettingsContents inspected = SettingsArchive.Inspect(everything);
        Check(inspected.Available == SettingsParts.All && inspected.SshProfiles == 2 && inspected.RawProfiles == 1 && inspected.OpenSshFiles.Count == 2, "inspect lists what an archive contains");
        Check(SettingsArchive.Inspect(selective).Available == (SettingsParts.MainSettings | SettingsParts.CommandCompletions | SettingsParts.RawProfiles), "inspect of a partial archive offers only its parts");

        // ---- import of selected parts only
        string dstData = NewDir("sel-dstdata"), dstLocal = NewDir("sel-dstlocal"), dstApp = NewDir("sel-dstapp"), dstSsh = NewDir("sel-dstssh");
        FakeKeyStore dstKeys = new FakeKeyStore { SshKey = Encoding.UTF8.GetBytes(RsaKeyXml.Replace("BBBB", "OLDOLD")) };
        SettingsLocations dst = MakeLocations(dstData, dstLocal, Path.Combine(dstApp, "CommandCompletions.xml"), dstSsh, dstKeys);

        var onlyCompletions = SettingsArchive.Import(everything, dst, SettingsParts.CommandCompletions, null);
        Check(onlyCompletions.CommandCompletionsImported && onlyCompletions.FileCount == 1 && Read(dstApp, "CommandCompletions.xml") == CompletionsXml, "CommandCompletions.xml is restored on its own");
        Check(!File.Exists(Path.Combine(dstData, "Settings.xml")) && !File.Exists(Path.Combine(dstSsh, "id_ed25519")) && Encoding.UTF8.GetString(dstKeys.SshKey).Contains("OLDOLD"),
            "parts that are not selected are not touched");
        Check(onlyCompletions.NotSelected.Count > 5, "unselected entries are counted, not ignored");

        string backupPath = Path.Combine(dstData, "Backups", "keys-backup.tar.gz");
        var keyImport = SettingsArchive.Import(everything, dst, SettingsParts.Keys, backupPath);
        Check(keyImport.SshKeyImported && keyImport.PasswordKeyImported && keyImport.OpenSshKeyFiles == 2 && keyImport.FileCount == 4, "keys are imported into the key store and the .ssh folder");
        Check(Encoding.UTF8.GetString(dstKeys.SshKey) == RsaKeyXml && Encoding.UTF8.GetString(dstKeys.PasswordKey).Contains("CCCC"), "the CSP keys are handed to the key store unchanged");
        Check(Read(dstSsh, "id_ed25519") == OpenSshPrivate && Read(dstSsh, "id_ed25519.pub") == OpenSshPublic, "OpenSSH key files are restored verbatim");
        Check(keyImport.BackupPath == backupPath && ReadArchive(backupPath).Any(e => e.Name == SettingsArchive.SshKeyEntryName && Encoding.UTF8.GetString(e.Data).Contains("OLDOLD")),
            "the key that is replaced is saved in the backup first");

        // saved passwords: only with the SavedPasswords part
        string dstData2 = NewDir("sel-dstdata2"), dstLocal2 = NewDir("sel-dstlocal2");
        Put(dstData2, "SavedConnections/root@192.168.1.1.sshconnection", Ssh("192.168.1.1", "LOCALPWD"));
        string withPwd = Path.Combine(NewDir("out"), "with-pwd2.tar.gz");
        SettingsArchive.Export(withPwd, src, SettingsParts.SshProfiles | SettingsParts.SavedPasswords);
        SettingsLocations dst2 = MakeLocations(dstData2, dstLocal2, null, null, null);
        SettingsArchive.Import(withPwd, dst2, SettingsParts.SshProfiles, null);
        Check(Read(dstData2, "SavedConnections/root@192.168.1.1.sshconnection").Contains("LOCALPWD") && !Read(dstData2, "SavedConnections/root@192.168.1.1.sshconnection").Contains("SECRETPWD"),
            "importing profiles without the passwords part keeps the local password and drops the archived one");
        Check(!Read(dstData2, "SavedConnections/root@10.0.0.2.sshconnection").Contains("EncryptedSavedPassword"), "a new profile imported without passwords has none");
        SettingsArchive.Import(withPwd, dst2, SettingsParts.SshProfiles | SettingsParts.SavedPasswords, null);
        Check(Read(dstData2, "SavedConnections/root@192.168.1.1.sshconnection").Contains("SECRETPWD"), "with the passwords part the archived password is imported");

        // the credentials of the HTTP proxy are saved passwords too (encrypted in the profile, plain text in older profiles)
        string proxySrcData = NewDir("proxy-srcdata"), proxySrcLocal = NewDir("proxy-srclocal");
        Put(proxySrcData, "SavedConnections/root@proxied.sshconnection", SshWithProxy("proxied", "PROXYSECRET", null));
        Put(proxySrcData, "SavedConnections/root@legacy.sshconnection", SshWithProxy("legacy", null, "PLAINPROXY"));
        string proxyArchive = Path.Combine(NewDir("out"), "proxy.tar.gz");
        SettingsArchive.Export(proxyArchive, proxySrcData, proxySrcLocal, false);
        var proxyEntries = ReadArchive(proxyArchive);
        Check(proxyEntries.Count == 2 && proxyEntries.All(e => {
            string entryText = Encoding.UTF8.GetString(e.Data);
            return !entryText.Contains("PROXYSECRET") && !entryText.Contains("PLAINPROXY") && !entryText.Contains("EncryptedProxyAuth") && !entryText.Contains("<ProxyAuth>");
        }), "the proxy credentials are left out of an export without passwords, encrypted or not");
        Check(proxyEntries.All(e => Encoding.UTF8.GetString(e.Data).Contains("<ProxyServer>proxy.example.com</ProxyServer>")), "the proxy address stays in the stripped profiles");
        string proxyArchiveWithPwd = Path.Combine(NewDir("out"), "proxy-with-pwd.tar.gz");
        SettingsArchive.Export(proxyArchiveWithPwd, proxySrcData, proxySrcLocal, true);
        Check(ReadArchive(proxyArchiveWithPwd).Any(e => Encoding.UTF8.GetString(e.Data).Contains("PROXYSECRET")), "includePasswords keeps the proxy credentials");
        string proxyDstData = NewDir("proxy-dstdata"), proxyDstLocal = NewDir("proxy-dstlocal");
        Put(proxyDstData, "SavedConnections/root@proxied.sshconnection", SshWithProxy("proxied", "LOCALPROXY", null));
        SettingsLocations proxyDst = MakeLocations(proxyDstData, proxyDstLocal, null, null, null);
        SettingsArchive.Import(proxyArchiveWithPwd, proxyDst, SettingsParts.SshProfiles, null);
        Check(Read(proxyDstData, "SavedConnections/root@proxied.sshconnection").Contains("LOCALPROXY") && !Read(proxyDstData, "SavedConnections/root@proxied.sshconnection").Contains("PROXYSECRET"),
            "importing profiles without the passwords part keeps the local proxy credentials and drops the archived ones");
        Check(!Read(proxyDstData, "SavedConnections/root@legacy.sshconnection").Contains("PLAINPROXY"), "a new profile imported without passwords has no plain-text proxy credentials");
        SettingsArchive.Import(proxyArchiveWithPwd, proxyDst, SettingsParts.SshProfiles | SettingsParts.SavedPasswords, null);
        Check(Read(proxyDstData, "SavedConnections/root@proxied.sshconnection").Contains("PROXYSECRET"), "with the passwords part the archived proxy credentials are imported");

        // ---- validation
        string badKey = Path.Combine(NewDir("out"), "bad-key.tar.gz");
        MakeArchive(badKey, E("OpenSSH/id_rsa", "this is not a key"));
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(badKey, dst, SettingsParts.All, null), "an OpenSSH entry that is not a key is rejected");
        string badName = Path.Combine(NewDir("out"), "bad-name.tar.gz");
        MakeArchive(badName, E("Settings.xml", SettingsXml), E("OpenSSH/../evil", OpenSshPrivate), E("OpenSSH/authorized_keys", OpenSshPublic), E("Keys/other.xml", RsaKeyXml));
        var badNameResult = SettingsArchive.Import(badName, dst, SettingsParts.All, null);
        Check(badNameResult.Ignored.Count == 3 && badNameResult.FileCount == 1 && !File.Exists(Path.Combine(dstSsh, "authorized_keys")), "OpenSSH and Keys entries outside the fixed list are ignored");
        string wrongRoot = Path.Combine(NewDir("out"), "wrong-root.tar.gz");
        MakeArchive(wrongRoot, E("Keys/SmarTTY-ssh-key.xml", "<Something/>"));
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(wrongRoot, dst, SettingsParts.All, null), "a key entry without <RSAParameters> is rejected");
        string wrongCompletions = Path.Combine(NewDir("out"), "wrong-completions.tar.gz");
        MakeArchive(wrongCompletions, E("CommandCompletions.xml", "<GlobalSettings/>"));
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(wrongCompletions, dst, SettingsParts.All, null), "CommandCompletions.xml of another type is rejected");
        ExpectThrows<InvalidDataException>(() => SettingsArchive.Import(selective, dst, SettingsParts.Keys, null), "importing parts that the archive does not hold is an error");
        ExpectThrows<InvalidOperationException>(() => SettingsArchive.Import(everything, MakeLocations(NewDir("sel-n1"), NewDir("sel-n2"), null, null, null), SettingsParts.SshKey, null), "keys cannot be imported without a key store");
    }

    private static string Telnet(string host, string password)
    {
        return "<?xml version=\"1.0\"?><RawConnectionSettings xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><Settings>" +
               "<Connection xsi:type=\"com.sysprogs.terminal.connection.telnet\"><Host>" + host + "</Host><Port>23</Port>" +
               "<AutoLoginUserName>admin</AutoLoginUserName>" +
               (password == null ? "" : "<EncryptedSavedPassword>" + password + "</EncryptedSavedPassword>") +
               "</Connection></Settings></RawConnectionSettings>";
    }
}
