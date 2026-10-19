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
    private const string RawXml = "<?xml version=\"1.0\"?><RawConnectionSettings><Icon>a.png</Icon></RawConnectionSettings>";

    private static string Ssh(string host, string password)
    {
        return "<?xml version=\"1.0\"?><SSHConnectionSettings>" +
               (password == null ? "" : "<EncryptedSavedPassword>" + password + "</EncryptedSavedPassword>") +
               "<HostName>" + host + "</HostName><UserName>root</UserName></SSHConnectionSettings>";
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
        Check(export.FileCount == 6 && names.Length == 6, "export writes exactly the 6 settings files");
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
