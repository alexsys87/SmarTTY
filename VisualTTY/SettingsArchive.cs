using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VisualTTY;

/// <summary>
/// Exports and imports SmarTTY's XML settings as a single .tar.gz archive.
///
/// Archive layout (only these entries are ever read or written):
///   Settings.xml                         &lt;- %APPDATA%\SmarTTY\Settings.xml
///   KnownHosts.xml                       &lt;- %APPDATA%\SmarTTY\KnownHosts.xml
///   SavedConnections/*.sshconnection     &lt;- %APPDATA%\SmarTTY\SavedConnections\
///   SavedConnections/*.rawconnection     &lt;- %APPDATA%\SmarTTY\SavedConnections\
///   Local/GUIPreferences.xml             &lt;- %LOCALAPPDATA%\SmarTTY\GUIPreferences.xml
///
/// The class has no WPF/WinForms dependencies so it can be tested on any platform.
/// Directories are passed in explicitly, which also covers portable mode (PortableSettings).
/// </summary>
public static class SettingsArchive
{
    public const string SettingsFileName = "Settings.xml";
    public const string KnownHostsFileName = "KnownHosts.xml";
    public const string GuiPreferencesFileName = "GUIPreferences.xml";
    public const string ConnectionsDirectoryName = "SavedConnections";
    public const string SshConnectionExtension = ".sshconnection";
    public const string RawConnectionExtension = ".rawconnection";

    private const string LocalPrefix = "Local/";
    private const string ConnectionsPrefix = "SavedConnections/";
    private const string PasswordElementName = "EncryptedSavedPassword";

    // Limits that keep a malformed or malicious archive (for example a gzip bomb) from exhausting memory.
    internal const long MaxEntrySize = 16L * 1024 * 1024;
    internal const long MaxTotalSize = 64L * 1024 * 1024;
    internal const int MaxEntries = 10000;

    public sealed class ExportResult
    {
        public int FileCount;
        public bool PasswordsIncluded;
        public List<string> Skipped = new List<string>();
    }

    public sealed class ImportResult
    {
        public bool SettingsImported;
        public bool KnownHostsImported;
        public bool GuiPreferencesImported;
        public int SshProfiles;
        public int RawProfiles;
        public string BackupPath;
        public List<string> Ignored = new List<string>();

        public int FileCount
        {
            get
            {
                return (SettingsImported ? 1 : 0) + (KnownHostsImported ? 1 : 0) + (GuiPreferencesImported ? 1 : 0) + SshProfiles + RawProfiles;
            }
        }
    }

    private enum EntryKind
    {
        Settings,
        KnownHosts,
        GuiPreferences,
        SshConnection,
        RawConnection
    }

    private sealed class Target
    {
        public EntryKind Kind;
        public string FileName;
        public string RootElement;
    }

    private sealed class Planned
    {
        public Target Target;
        public XDocument Document;
        public byte[] Data;
    }

    internal sealed class TarEntry
    {
        public string Name;
        public byte[] Data;
    }

    public static string DefaultBackupPath(string dataDirectory)
    {
        return Path.Combine(dataDirectory, "Backups", "before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".tar.gz");
    }

    // ------------------------------------------------------------------ export

    /// <summary>
    /// Writes all existing settings files into <paramref name="archivePath"/>.
    /// Saved passwords are encrypted with a key container that belongs to the current Windows user,
    /// so they are useless on another computer. They are left out unless <paramref name="includePasswords"/> is set.
    /// </summary>
    public static ExportResult Export(string archivePath, string dataDirectory, string localDataDirectory, bool includePasswords)
    {
        if (string.IsNullOrEmpty(archivePath))
        {
            throw new ArgumentException("Archive path is empty.", "archivePath");
        }

        List<KeyValuePair<string, string>> files = CollectFiles(dataDirectory, localDataDirectory);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("There are no saved SmarTTY settings to export.");
        }

        string fullPath = Path.GetFullPath(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        string temp = fullPath + ".tmp";
        ExportResult result = new ExportResult { PasswordsIncluded = includePasswords };
        try
        {
            using (FileStream output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress))
                {
                    foreach (KeyValuePair<string, string> file in files)
                    {
                        byte[] data = ReadAllBytesShared(file.Value);
                        if (data.Length > MaxEntrySize)
                        {
                            throw new InvalidDataException("'" + file.Key + "' is too large to export.");
                        }
                        // Only well-formed files of the expected type go into the archive; otherwise the
                        // archive would be rejected on import.
                        Target target;
                        TryClassify(file.Key, out target);
                        try
                        {
                            XDocument document = LoadXml(data);
                            if (document.Root == null || document.Root.Name.LocalName != target.RootElement)
                            {
                                result.Skipped.Add(file.Key);
                                continue;
                            }
                            if (!includePasswords && target.Kind == EntryKind.SshConnection)
                            {
                                data = RemovePassword(document, data);
                            }
                        }
                        catch (XmlException)
                        {
                            result.Skipped.Add(file.Key);
                            continue;
                        }
                        TarWriter.WriteFile(gzip, file.Key, data, File.GetLastWriteTimeUtc(file.Value));
                        result.FileCount++;
                    }
                    TarWriter.Finish(gzip);
                }
            }
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
            File.Move(temp, fullPath);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return result;
    }

    private static List<KeyValuePair<string, string>> CollectFiles(string dataDirectory, string localDataDirectory)
    {
        List<KeyValuePair<string, string>> files = new List<KeyValuePair<string, string>>();
        AddIfExists(files, SettingsFileName, Path.Combine(dataDirectory, SettingsFileName));
        AddIfExists(files, KnownHostsFileName, Path.Combine(dataDirectory, KnownHostsFileName));
        string connections = Path.Combine(dataDirectory, ConnectionsDirectoryName);
        if (Directory.Exists(connections))
        {
            // Enumerate everything and classify by name instead of using a "*.ext" search pattern,
            // whose matching rules differ between runtimes.
            string[] all = Directory.GetFiles(connections);
            Array.Sort(all, StringComparer.Ordinal);
            foreach (string path in all)
            {
                string entryName = ConnectionsPrefix + Path.GetFileName(path);
                Target target;
                if (TryClassify(entryName, out target))
                {
                    files.Add(new KeyValuePair<string, string>(entryName, path));
                }
            }
        }
        AddIfExists(files, LocalPrefix + GuiPreferencesFileName, Path.Combine(localDataDirectory, GuiPreferencesFileName));
        return files;
    }

    private static void AddIfExists(List<KeyValuePair<string, string>> files, string entryName, string path)
    {
        if (File.Exists(path))
        {
            files.Add(new KeyValuePair<string, string>(entryName, path));
        }
    }

    // ------------------------------------------------------------------ import

    /// <summary>
    /// Validates the archive completely, optionally backs up the current settings to
    /// <paramref name="backupPath"/>, and then writes the files. Nothing is written when validation fails.
    /// Files with the same name are replaced; saved connections that are not in the archive are kept.
    /// A saved password already stored locally is kept when the archived profile carries none.
    /// </summary>
    public static ImportResult Import(string archivePath, string dataDirectory, string localDataDirectory, string backupPath)
    {
        List<TarEntry> entries;
        using (FileStream input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                entries = TarReader.ReadAll(gzip);
            }
        }

        ImportResult result = new ImportResult();
        List<Planned> planned = new List<Planned>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TarEntry entry in entries)
        {
            Target target;
            if (!TryClassify(entry.Name, out target))
            {
                result.Ignored.Add(entry.Name);
                continue;
            }
            if (!seen.Add(target.Kind + "/" + target.FileName.ToUpperInvariant()))
            {
                throw new InvalidDataException("The archive contains '" + entry.Name + "' more than once.");
            }
            XDocument document;
            try
            {
                document = LoadXml(entry.Data);
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException("'" + entry.Name + "' is not valid XML: " + ex.Message, ex);
            }
            if (document.Root == null || document.Root.Name.LocalName != target.RootElement)
            {
                throw new InvalidDataException("'" + entry.Name + "' does not look like a SmarTTY file (expected <" + target.RootElement + ">).");
            }
            planned.Add(new Planned { Target = target, Document = document, Data = entry.Data });
        }
        if (planned.Count == 0)
        {
            throw new InvalidDataException("The archive does not contain any SmarTTY settings files.");
        }

        if (!string.IsNullOrEmpty(backupPath) && CollectFiles(dataDirectory, localDataDirectory).Count > 0)
        {
            try
            {
                Export(backupPath, dataDirectory, localDataDirectory, true);
                result.BackupPath = backupPath;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Importing without a safety copy would be irreversible, so stop here.
                throw new IOException("Could not back up the current settings to '" + backupPath + "': " + ex.Message, ex);
            }
        }

        foreach (Planned item in planned)
        {
            string path = Path.Combine(DirectoryFor(item.Target.Kind, dataDirectory, localDataDirectory), item.Target.FileName);
            byte[] data = item.Data;
            if (item.Target.Kind == EntryKind.SshConnection && File.Exists(path))
            {
                data = KeepExistingPassword(item.Document, data, path);
            }
            WriteFileReplacing(path, data);
            switch (item.Target.Kind)
            {
                case EntryKind.Settings:
                    result.SettingsImported = true;
                    break;
                case EntryKind.KnownHosts:
                    result.KnownHostsImported = true;
                    break;
                case EntryKind.GuiPreferences:
                    result.GuiPreferencesImported = true;
                    break;
                case EntryKind.SshConnection:
                    result.SshProfiles++;
                    break;
                case EntryKind.RawConnection:
                    result.RawProfiles++;
                    break;
            }
        }
        return result;
    }

    private static string DirectoryFor(EntryKind kind, string dataDirectory, string localDataDirectory)
    {
        switch (kind)
        {
            case EntryKind.GuiPreferences:
                return localDataDirectory;
            case EntryKind.SshConnection:
            case EntryKind.RawConnection:
                return Path.Combine(dataDirectory, ConnectionsDirectoryName);
            default:
                return dataDirectory;
        }
    }

    // Maps an archive entry name to a known settings file. Anything else (including names with
    // path separators, "..", drive letters or reserved device names) is rejected.
    private static bool TryClassify(string entryName, out Target target)
    {
        target = null;
        string name = entryName ?? "";
        while (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name.Substring(2);
        }

        if (name == SettingsFileName)
        {
            target = new Target { Kind = EntryKind.Settings, FileName = SettingsFileName, RootElement = "GlobalSettings" };
        }
        else if (name == KnownHostsFileName)
        {
            target = new Target { Kind = EntryKind.KnownHosts, FileName = KnownHostsFileName, RootElement = "ArrayOfKnownHostKey" };
        }
        else if (name == LocalPrefix + GuiPreferencesFileName)
        {
            target = new Target { Kind = EntryKind.GuiPreferences, FileName = GuiPreferencesFileName, RootElement = "CommonGUIPreferences" };
        }
        else if (name.StartsWith(ConnectionsPrefix, StringComparison.Ordinal))
        {
            string file = name.Substring(ConnectionsPrefix.Length);
            if (!IsSafeFileName(file))
            {
                return false;
            }
            if (file.EndsWith(SshConnectionExtension, StringComparison.OrdinalIgnoreCase) && file.Length > SshConnectionExtension.Length)
            {
                target = new Target { Kind = EntryKind.SshConnection, FileName = file, RootElement = "SSHConnectionSettings" };
            }
            else if (file.EndsWith(RawConnectionExtension, StringComparison.OrdinalIgnoreCase) && file.Length > RawConnectionExtension.Length)
            {
                target = new Target { Kind = EntryKind.RawConnection, FileName = file, RootElement = "RawConnectionSettings" };
            }
        }
        return target != null;
    }

    private static bool IsSafeFileName(string file)
    {
        if (string.IsNullOrEmpty(file) || file.Length > 200 || file == "." || file == "..")
        {
            return false;
        }
        foreach (char c in file)
        {
            if (c < 32 || "\\/:*?\"<>|".IndexOf(c) != -1)
            {
                return false;
            }
        }
        if (file.EndsWith(" ", StringComparison.Ordinal) || file.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }
        string baseName = file;
        int dot = baseName.IndexOf('.');
        if (dot >= 0)
        {
            baseName = baseName.Substring(0, dot);
        }
        string upper = baseName.ToUpperInvariant();
        if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL")
        {
            return false;
        }
        if (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)) && upper[3] >= '1' && upper[3] <= '9')
        {
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ XML helpers

    private static XDocument LoadXml(byte[] data)
    {
        XmlReaderSettings settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024
        };
        using (MemoryStream stream = new MemoryStream(data))
        {
            using (XmlReader reader = XmlReader.Create(stream, settings))
            {
                return XDocument.Load(reader);
            }
        }
    }

    private static byte[] ToBytes(XDocument document)
    {
        XmlWriterSettings settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
        using (MemoryStream stream = new MemoryStream())
        {
            using (XmlWriter writer = XmlWriter.Create(stream, settings))
            {
                document.Save(writer);
            }
            return stream.ToArray();
        }
    }

    private static byte[] RemovePassword(XDocument document, byte[] xml)
    {
        List<XElement> passwords = document.Descendants(PasswordElementName).ToList();
        if (passwords.Count == 0)
        {
            return xml;
        }
        foreach (XElement password in passwords)
        {
            password.Remove();
        }
        return ToBytes(document);
    }

    private static byte[] KeepExistingPassword(XDocument incoming, byte[] raw, string existingPath)
    {
        XElement incomingPassword = incoming.Root.Element(PasswordElementName);
        if (incomingPassword != null && !string.IsNullOrWhiteSpace(incomingPassword.Value))
        {
            return raw;
        }
        try
        {
            XDocument existing = LoadXml(ReadAllBytesShared(existingPath));
            XElement existingPassword = existing.Root == null ? null : existing.Root.Element(PasswordElementName);
            if (existingPassword == null || string.IsNullOrWhiteSpace(existingPassword.Value))
            {
                return raw;
            }
            if (incomingPassword != null)
            {
                incomingPassword.Remove();
            }
            incoming.Root.Add(new XElement(existingPassword));
            return ToBytes(incoming);
        }
        catch (Exception ex) when (ex is XmlException || ex is IOException || ex is UnauthorizedAccessException)
        {
            return raw;
        }
    }

    // ------------------------------------------------------------------ file helpers

    private static byte[] ReadAllBytesShared(string path)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using (MemoryStream memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }
    }

    private static void WriteFileReplacing(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + ".import.tmp";
        try
        {
            File.WriteAllBytes(temp, data);
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------------ minimal tar (ustar + GNU long names)

    internal static class TarWriter
    {
        private const int BlockSize = 512;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static void WriteFile(Stream stream, string name, byte[] data, DateTime lastWriteUtc)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            long mtime = (long)(DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc) - Epoch).TotalSeconds;
            if (mtime < 0)
            {
                mtime = 0;
            }
            if (nameBytes.Length > 100)
            {
                // GNU tar convention: a preceding 'L' entry carries the full name.
                byte[] longName = new byte[nameBytes.Length + 1];
                Array.Copy(nameBytes, longName, nameBytes.Length);
                WriteHeader(stream, Encoding.ASCII.GetBytes("././@LongLink"), longName.Length, 'L', 0);
                WriteData(stream, longName);
                byte[] truncated = new byte[100];
                Array.Copy(nameBytes, truncated, 100);
                nameBytes = truncated;
            }
            WriteHeader(stream, nameBytes, data.Length, '0', mtime);
            WriteData(stream, data);
        }

        public static void Finish(Stream stream)
        {
            stream.Write(new byte[BlockSize * 2], 0, BlockSize * 2);
        }

        private static void WriteData(Stream stream, byte[] data)
        {
            stream.Write(data, 0, data.Length);
            int padding = (BlockSize - (int)(data.Length % BlockSize)) % BlockSize;
            if (padding > 0)
            {
                stream.Write(new byte[padding], 0, padding);
            }
        }

        private static void WriteHeader(Stream stream, byte[] nameBytes, long size, char type, long mtime)
        {
            byte[] header = new byte[BlockSize];
            Array.Copy(nameBytes, header, Math.Min(nameBytes.Length, 100));
            PutOctal(header, 100, 8, Convert.ToInt64("644", 8));
            PutOctal(header, 108, 8, 0);
            PutOctal(header, 116, 8, 0);
            PutOctal(header, 124, 12, size);
            PutOctal(header, 136, 12, mtime);
            for (int i = 148; i < 156; i++)
            {
                header[i] = (byte)' ';
            }
            header[156] = (byte)type;
            byte[] magic = Encoding.ASCII.GetBytes("ustar\0");
            Array.Copy(magic, 0, header, 257, magic.Length);
            header[263] = (byte)'0';
            header[264] = (byte)'0';

            long sum = 0;
            foreach (byte b in header)
            {
                sum += b;
            }
            byte[] checksum = Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0'));
            Array.Copy(checksum, 0, header, 148, 6);
            header[154] = 0;
            header[155] = (byte)' ';
            stream.Write(header, 0, BlockSize);
        }

        private static void PutOctal(byte[] header, int offset, int length, long value)
        {
            string text = Convert.ToString(value, 8).PadLeft(length - 1, '0');
            if (text.Length > length - 1)
            {
                throw new InvalidDataException("Value does not fit a tar header field.");
            }
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            Array.Copy(bytes, 0, header, offset, bytes.Length);
            header[offset + length - 1] = 0;
        }
    }

    internal static class TarReader
    {
        private const int BlockSize = 512;

        public static List<TarEntry> ReadAll(Stream stream)
        {
            List<TarEntry> result = new List<TarEntry>();
            byte[] header = new byte[BlockSize];
            string longName = null;
            string paxPath = null;
            long total = 0;
            int count = 0;
            while (true)
            {
                int got = ReadUpTo(stream, header, BlockSize);
                if (got == 0)
                {
                    throw new InvalidDataException("The archive has no end marker and is probably truncated.");
                }
                if (got < BlockSize)
                {
                    throw new InvalidDataException("The archive is truncated.");
                }
                if (IsZeroBlock(header))
                {
                    return result;
                }
                if (++count > MaxEntries)
                {
                    throw new InvalidDataException("The archive contains too many entries.");
                }
                VerifyChecksum(header);

                char type = (char)header[156];
                long size = ParseSize(header);
                if (size > MaxEntrySize)
                {
                    throw new InvalidDataException("The archive contains an entry larger than " + (MaxEntrySize / (1024 * 1024)) + " MB.");
                }
                total += size;
                if (total > MaxTotalSize)
                {
                    throw new InvalidDataException("The archive is larger than " + (MaxTotalSize / (1024 * 1024)) + " MB when unpacked.");
                }

                string name = ReadName(header);
                byte[] data = new byte[size];
                if (ReadUpTo(stream, data, (int)size) != size)
                {
                    throw new InvalidDataException("The archive is truncated.");
                }
                int padding = (BlockSize - (int)(size % BlockSize)) % BlockSize;
                if (padding > 0 && ReadUpTo(stream, new byte[padding], padding) != padding)
                {
                    throw new InvalidDataException("The archive is truncated.");
                }

                switch (type)
                {
                    case 'L':
                        longName = TrimNul(Encoding.UTF8.GetString(data));
                        break;
                    case 'x':
                        paxPath = ParsePaxPath(data) ?? paxPath;
                        break;
                    case 'g':
                        break;
                    case '0':
                    case '\0':
                        result.Add(new TarEntry { Name = longName ?? paxPath ?? name, Data = data });
                        longName = null;
                        paxPath = null;
                        break;
                    default:
                        // Directories and other entry types carry nothing we import.
                        longName = null;
                        paxPath = null;
                        break;
                }
            }
        }

        private static int ReadUpTo(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0)
                {
                    break;
                }
                total += read;
            }
            return total;
        }

        private static bool IsZeroBlock(byte[] block)
        {
            foreach (byte b in block)
            {
                if (b != 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static void VerifyChecksum(byte[] header)
        {
            long stored;
            try
            {
                stored = ParseOctal(header, 148, 8);
            }
            catch (FormatException)
            {
                throw new InvalidDataException("The archive has a damaged header.");
            }
            long sum = 0;
            for (int i = 0; i < BlockSize; i++)
            {
                sum += (i >= 148 && i < 156) ? (byte)' ' : header[i];
            }
            if (sum != stored)
            {
                throw new InvalidDataException("The archive has a damaged header (checksum mismatch).");
            }
        }

        private static long ParseSize(byte[] header)
        {
            if ((header[124] & 0x80) != 0)
            {
                throw new InvalidDataException("The archive contains an entry with an unsupported size encoding.");
            }
            try
            {
                return ParseOctal(header, 124, 12);
            }
            catch (FormatException)
            {
                throw new InvalidDataException("The archive has a damaged header.");
            }
        }

        private static long ParseOctal(byte[] header, int offset, int length)
        {
            string text = Encoding.ASCII.GetString(header, offset, length).Trim('\0', ' ');
            return text.Length == 0 ? 0 : Convert.ToInt64(text, 8);
        }

        private static string ReadName(byte[] header)
        {
            string name = TrimNul(Encoding.UTF8.GetString(header, 0, 100));
            if (Encoding.ASCII.GetString(header, 257, 5) == "ustar")
            {
                string prefix = TrimNul(Encoding.UTF8.GetString(header, 345, 155));
                if (prefix.Length > 0)
                {
                    return prefix + "/" + name;
                }
            }
            return name;
        }

        private static string TrimNul(string text)
        {
            int nul = text.IndexOf('\0');
            return nul >= 0 ? text.Substring(0, nul) : text;
        }

        // PAX extended header records look like "<length> <key>=<value>\n".
        private static string ParsePaxPath(byte[] data)
        {
            int position = 0;
            string path = null;
            while (position < data.Length)
            {
                int space = Array.IndexOf(data, (byte)' ', position);
                if (space < 0)
                {
                    break;
                }
                int length;
                if (!int.TryParse(Encoding.ASCII.GetString(data, position, space - position), out length) || length <= 0 || position + length > data.Length)
                {
                    break;
                }
                string record = Encoding.UTF8.GetString(data, space + 1, position + length - space - 1).TrimEnd('\n');
                if (record.StartsWith("path=", StringComparison.Ordinal))
                {
                    path = record.Substring(5);
                }
                position += length;
            }
            return path;
        }
    }
}
