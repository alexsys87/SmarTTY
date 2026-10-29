using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VisualTTY;

/// <summary>Parts of the SmarTTY configuration that can be exported to / imported from a settings archive.</summary>
[Flags]
public enum SettingsParts
{
    None = 0,
    MainSettings = 1,
    KnownHosts = 2,
    GuiPreferences = 4,
    SshProfiles = 8,
    RawProfiles = 16,
    /// <summary>Saved passwords inside SSH and COM/TCP/Telnet profiles (encrypted with the password key of the Windows user).</summary>
    SavedPasswords = 32,
    /// <summary>The SmarTTY public key authentication key (RSA, stored in a CSP key container of the Windows user).</summary>
    SshKey = 64,
    /// <summary>The key that encrypts saved passwords. Needed to make exported saved passwords usable on another computer.</summary>
    PasswordKey = 128,
    /// <summary>OpenSSH private and public keys (id_ed25519, id_ecdsa, id_rsa) from the .ssh folder of the user.</summary>
    OpenSshKeys = 256,
    CommandCompletions = 512,
    /// <summary>The TFTP server / U-Boot upload window: the files it serves and the commands it types (per computer).</summary>
    TftpFirmware = 1024,

    /// <summary>What the archive held before the parts were selectable (passwords are chosen separately).</summary>
    Legacy = MainSettings | KnownHosts | GuiPreferences | SshProfiles | RawProfiles,
    /// <summary>Selected by default in the export/import window: everything except passwords and private keys.</summary>
    Default = Legacy | CommandCompletions | TftpFirmware,
    Keys = SshKey | PasswordKey | OpenSshKeys,
    All = Default | SavedPasswords | Keys
}

/// <summary>
/// Access to the keys that live in Windows CSP key containers. It is an interface so that
/// <see cref="SettingsArchive"/> stays free of platform-specific code and can be tested anywhere.
/// A key is exchanged as the XML of <c>RSAParameters</c>, the same format as the .sshkey files of the key manager.
/// </summary>
public interface ISettingsKeyStore
{
    bool HasSshKey { get; }

    bool HasPasswordKey { get; }

    byte[] ExportSshKey();

    byte[] ExportPasswordKey();

    void ImportSshKey(byte[] data);

    void ImportPasswordKey(byte[] data);
}

/// <summary>Where the exportable files live. Paths and the key store are passed in, which also covers portable mode.</summary>
public sealed class SettingsLocations
{
    /// <summary>%APPDATA%\SmarTTY (or PortableSettings).</summary>
    public string DataDirectory;

    /// <summary>%LOCALAPPDATA%\SmarTTY.</summary>
    public string LocalDataDirectory;

    /// <summary>CommandCompletions.xml next to the exe. Null when command completions are not handled.</summary>
    public string CommandCompletionsPath;

    /// <summary>The .ssh folder of the user. Null when OpenSSH keys are not handled.</summary>
    public string UserSshDirectory;

    /// <summary>Null when keys from CSP containers are not handled.</summary>
    public ISettingsKeyStore KeyStore;
}

/// <summary>What exists on this computer (<see cref="SettingsArchive.Describe"/>) or inside an archive (<see cref="SettingsArchive.Inspect"/>).</summary>
public sealed class SettingsContents
{
    public SettingsParts Available;
    public int SshProfiles;
    public int RawProfiles;
    public List<string> OpenSshFiles = new List<string>();
    public List<string> Ignored = new List<string>();
}

/// <summary>
/// Exports and imports SmarTTY's settings as a single .tar.gz archive. The caller chooses the parts (<see cref="SettingsParts"/>).
///
/// Archive layout (only these entries are ever read or written):
///   Settings.xml                         &lt;- %APPDATA%\SmarTTY\Settings.xml
///   KnownHosts.xml                       &lt;- %APPDATA%\SmarTTY\KnownHosts.xml
///   SavedConnections/*.sshconnection     &lt;- %APPDATA%\SmarTTY\SavedConnections\
///   SavedConnections/*.rawconnection     &lt;- %APPDATA%\SmarTTY\SavedConnections\
///   Local/GUIPreferences.xml             &lt;- %LOCALAPPDATA%\SmarTTY\GUIPreferences.xml
///   Local/TftpFirmware.xml               &lt;- %LOCALAPPDATA%\SmarTTY\TftpFirmware.xml (the TFTP / U-Boot upload window)
///   CommandCompletions.xml               &lt;- CommandCompletions.xml next to SmarTTY.exe, or
///   CommandCompletions.xml.gz            &lt;- CommandCompletions.xml.gz (gzip) when that is the file SmarTTY uses; stored as it is
///   Keys/SmarTTY-ssh-key.xml             &lt;- the SmarTTY public key authentication key (CSP container, RSAParameters XML)
///   Keys/SmarTTY-password-key.xml        &lt;- the key that protects saved passwords (CSP container, RSAParameters XML)
///   OpenSSH/id_ed25519[.pub], id_ecdsa[.pub], id_rsa[.pub]   &lt;- the .ssh folder of the user
///
/// Private keys are stored in the archive as they are (not encrypted), so an archive with keys must be kept secret.
///
/// The class has no WPF/WinForms dependencies so it can be tested on any platform.
/// Directories are passed in explicitly, which also covers portable mode (PortableSettings).
/// </summary>
public static class SettingsArchive
{
    public const string SettingsFileName = "Settings.xml";
    public const string KnownHostsFileName = "KnownHosts.xml";
    public const string GuiPreferencesFileName = "GUIPreferences.xml";
    public const string TftpFirmwareFileName = "TftpFirmware.xml";
    public const string CommandCompletionsFileName = "CommandCompletions.xml";
    public const string CommandCompletionsGzipEntryName = "CommandCompletions.xml.gz";
    public const string ConnectionsDirectoryName = "SavedConnections";
    public const string SshConnectionExtension = ".sshconnection";
    public const string RawConnectionExtension = ".rawconnection";
    public const string SshKeyEntryName = "Keys/SmarTTY-ssh-key.xml";
    public const string PasswordKeyEntryName = "Keys/SmarTTY-password-key.xml";

    private const string LocalPrefix = "Local/";
    private const string ConnectionsPrefix = "SavedConnections/";
    private const string KeysPrefix = "Keys/";
    private const string OpenSshPrefix = "OpenSSH/";
    private const string PasswordElementName = "EncryptedSavedPassword";
    // The credentials of the HTTP proxy are saved passwords too: encrypted, and in plain text in profiles of earlier versions.
    private const string ProxyPasswordElementName = "EncryptedProxyAuth";
    private const string LegacyProxyAuthElementName = "ProxyAuth";

    private static readonly string[] SavedSecretElementNames = { PasswordElementName, ProxyPasswordElementName, LegacyProxyAuthElementName };

    private static readonly string[] OpenSshKeyFiles =
    {
        "id_ed25519", "id_ed25519.pub", "id_ecdsa", "id_ecdsa.pub", "id_rsa", "id_rsa.pub"
    };

    // Limits that keep a malformed or malicious archive (for example a gzip bomb) from exhausting memory.
    internal const long MaxEntrySize = 16L * 1024 * 1024;
    internal const long MaxTotalSize = 64L * 1024 * 1024;
    internal const int MaxEntries = 10000;
    internal const int MaxKeyFileSize = 256 * 1024;

    public sealed class ExportResult
    {
        public int FileCount;
        public bool PasswordsIncluded;

        /// <summary>Entries that were left out because they are damaged or could not be read.</summary>
        public List<string> Skipped = new List<string>();

        /// <summary>Selected parts of which nothing exists on this computer.</summary>
        public List<string> NotFound = new List<string>();
    }

    public sealed class ImportResult
    {
        public bool SettingsImported;
        public bool KnownHostsImported;
        public bool GuiPreferencesImported;
        public bool TftpFirmwareImported;
        public bool CommandCompletionsImported;
        public bool SshKeyImported;
        public bool PasswordKeyImported;
        public int SshProfiles;
        public int RawProfiles;
        public int OpenSshKeyFiles;
        public string BackupPath;
        public List<string> Ignored = new List<string>();

        /// <summary>Valid entries of the archive that were not imported because their part was not selected.</summary>
        public List<string> NotSelected = new List<string>();

        public int FileCount
        {
            get
            {
                return (SettingsImported ? 1 : 0) + (KnownHostsImported ? 1 : 0) + (GuiPreferencesImported ? 1 : 0) +
                       (TftpFirmwareImported ? 1 : 0) + (CommandCompletionsImported ? 1 : 0) + (SshKeyImported ? 1 : 0) +
                       (PasswordKeyImported ? 1 : 0) + SshProfiles + RawProfiles + OpenSshKeyFiles;
            }
        }
    }

    private enum EntryKind
    {
        Settings,
        KnownHosts,
        GuiPreferences,
        TftpFirmware,
        SshConnection,
        RawConnection,
        CommandCompletions,
        SshKey,
        PasswordKey,
        OpenSshKey
    }

    private sealed class Target
    {
        public EntryKind Kind;
        public SettingsParts Part;
        public string FileName;
        /// <summary>Null for entries that are not XML (OpenSSH key files).</summary>
        public string RootElement;
        /// <summary>The entry is gzip data that holds the XML (CommandCompletions.xml.gz).</summary>
        public bool Compressed;
    }

    private sealed class Planned
    {
        public string EntryName;
        public Target Target;
        public XDocument Document;
        public byte[] Data;
    }

    private sealed class ExportItem
    {
        public string Name;
        public string Path;
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

    private static bool Has(SettingsParts value, SettingsParts flag)
    {
        return (value & flag) != SettingsParts.None;
    }

    private static bool IsConnection(EntryKind kind)
    {
        return kind == EntryKind.SshConnection || kind == EntryKind.RawConnection;
    }

    private static bool IsKeyStoreKey(EntryKind kind)
    {
        return kind == EntryKind.SshKey || kind == EntryKind.PasswordKey;
    }

    private static SettingsLocations Locations(string dataDirectory, string localDataDirectory)
    {
        return new SettingsLocations { DataDirectory = dataDirectory, LocalDataDirectory = localDataDirectory };
    }

    // ------------------------------------------------------------------ describe

    /// <summary>Finds out which of the parts exist on this computer, so that the export window only offers those.</summary>
    public static SettingsContents Describe(SettingsLocations locations)
    {
        SettingsContents contents = new SettingsContents();
        ExportResult unused = new ExportResult();
        foreach (ExportItem item in CollectItems(locations, SettingsParts.All & ~SettingsParts.SavedPasswords & ~SettingsParts.SshKey & ~SettingsParts.PasswordKey, unused))
        {
            Target target;
            if (!TryClassify(item.Name, out target))
            {
                continue;
            }
            contents.Available |= target.Part;
            switch (target.Kind)
            {
                case EntryKind.SshConnection:
                    contents.SshProfiles++;
                    break;
                case EntryKind.RawConnection:
                    contents.RawProfiles++;
                    break;
                case EntryKind.OpenSshKey:
                    contents.OpenSshFiles.Add(target.FileName);
                    break;
            }
            if (IsConnection(target.Kind) && !Has(contents.Available, SettingsParts.SavedPasswords) && FileHoldsPassword(item.Path))
            {
                contents.Available |= SettingsParts.SavedPasswords;
            }
        }
        // Asking whether a key exists does not read the private key.
        if (locations.KeyStore != null)
        {
            if (locations.KeyStore.HasSshKey)
            {
                contents.Available |= SettingsParts.SshKey;
            }
            if (locations.KeyStore.HasPasswordKey)
            {
                contents.Available |= SettingsParts.PasswordKey;
            }
        }
        return contents;
    }

    private static bool FileHoldsPassword(string path)
    {
        try
        {
            return HoldsSavedSecret(LoadXml(ReadAllBytesShared(path)));
        }
        catch (Exception ex) when (ex is XmlException || ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ export

    /// <summary>
    /// Writes all existing settings files into <paramref name="archivePath"/>.
    /// Saved passwords are encrypted with a key container that belongs to the current Windows user,
    /// so they are useless on another computer. They are left out unless <paramref name="includePasswords"/> is set.
    /// </summary>
    public static ExportResult Export(string archivePath, string dataDirectory, string localDataDirectory, bool includePasswords)
    {
        return Export(archivePath, Locations(dataDirectory, localDataDirectory), SettingsParts.Legacy | (includePasswords ? SettingsParts.SavedPasswords : SettingsParts.None));
    }

    /// <summary>
    /// Writes the selected <paramref name="parts"/> that exist into <paramref name="archivePath"/>.
    /// Parts that do not exist are listed in <see cref="ExportResult.NotFound"/>.
    /// <see cref="SettingsParts.SavedPasswords"/> keeps the saved passwords inside the profiles; without it they are removed.
    /// </summary>
    public static ExportResult Export(string archivePath, SettingsLocations locations, SettingsParts parts)
    {
        if (string.IsNullOrEmpty(archivePath))
        {
            throw new ArgumentException("Archive path is empty.", "archivePath");
        }
        if (locations == null)
        {
            throw new ArgumentNullException("locations");
        }

        ExportResult result = new ExportResult { PasswordsIncluded = Has(parts, SettingsParts.SavedPasswords) };
        List<ExportItem> items = CollectItems(locations, parts, result);
        if (items.Count == 0)
        {
            throw new InvalidOperationException("There are no saved SmarTTY settings to export.");
        }

        string fullPath = Path.GetFullPath(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        string temp = fullPath + ".tmp";
        try
        {
            using (FileStream output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress))
                {
                    foreach (ExportItem item in items)
                    {
                        byte[] data = item.Data ?? ReadAllBytesShared(item.Path);
                        if (data.Length > MaxEntrySize)
                        {
                            throw new InvalidDataException("'" + item.Name + "' is too large to export.");
                        }
                        // Only well-formed files of the expected type go into the archive; otherwise the
                        // archive would be rejected on import.
                        Target target;
                        TryClassify(item.Name, out target);
                        try
                        {
                            if (target.RootElement == null)
                            {
                                ValidateKeyFile(item.Name, target.FileName, data);
                            }
                            else
                            {
                                XDocument document = LoadXml(target.Compressed ? Decompress(item.Name, data) : data);
                                if (document.Root == null || document.Root.Name.LocalName != target.RootElement)
                                {
                                    result.Skipped.Add(item.Name);
                                    continue;
                                }
                                // Telnet profiles carry a saved password too (SSH profiles at the top, telnet in Connection).
                                if (!result.PasswordsIncluded && IsConnection(target.Kind))
                                {
                                    data = RemovePassword(document, data);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is XmlException || ex is InvalidDataException)
                        {
                            result.Skipped.Add(item.Name);
                            continue;
                        }
                        DateTime time = item.Path != null ? File.GetLastWriteTimeUtc(item.Path) : DateTime.UtcNow;
                        TarWriter.WriteFile(gzip, item.Name, data, time);
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

    // Gathers what exists for the selected parts. Missing parts and keys that cannot be read go into the report.
    private static List<ExportItem> CollectItems(SettingsLocations locations, SettingsParts parts, ExportResult report)
    {
        List<ExportItem> items = new List<ExportItem>();
        string dataDirectory = locations.DataDirectory;
        if (Has(parts, SettingsParts.MainSettings))
        {
            AddIfExists(items, report, "main settings", SettingsFileName, Path.Combine(dataDirectory, SettingsFileName));
        }
        if (Has(parts, SettingsParts.KnownHosts))
        {
            AddIfExists(items, report, "trusted server keys", KnownHostsFileName, Path.Combine(dataDirectory, KnownHostsFileName));
        }

        bool wantSsh = Has(parts, SettingsParts.SshProfiles);
        bool wantRaw = Has(parts, SettingsParts.RawProfiles);
        if (wantSsh || wantRaw)
        {
            bool foundSsh = false;
            bool foundRaw = false;
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
                    if (!TryClassify(entryName, out target))
                    {
                        continue;
                    }
                    if ((target.Kind == EntryKind.SshConnection && wantSsh) || (target.Kind == EntryKind.RawConnection && wantRaw))
                    {
                        items.Add(new ExportItem { Name = entryName, Path = path });
                        foundSsh |= target.Kind == EntryKind.SshConnection;
                        foundRaw |= target.Kind == EntryKind.RawConnection;
                    }
                }
            }
            if (wantSsh && !foundSsh)
            {
                report.NotFound.Add("SSH profiles");
            }
            if (wantRaw && !foundRaw)
            {
                report.NotFound.Add("COM/TCP/Telnet profiles");
            }
        }

        if (Has(parts, SettingsParts.GuiPreferences))
        {
            AddIfExists(items, report, "interface preferences", LocalPrefix + GuiPreferencesFileName, Path.Combine(locations.LocalDataDirectory, GuiPreferencesFileName));
        }
        if (Has(parts, SettingsParts.TftpFirmware))
        {
            AddIfExists(items, report, "TFTP firmware settings", LocalPrefix + TftpFirmwareFileName, Path.Combine(locations.LocalDataDirectory, TftpFirmwareFileName));
        }
        if (Has(parts, SettingsParts.CommandCompletions))
        {
            if (string.IsNullOrEmpty(locations.CommandCompletionsPath))
            {
                report.NotFound.Add("command completions");
            }
            else if (File.Exists(locations.CommandCompletionsPath))
            {
                items.Add(new ExportItem { Name = CommandCompletionsFileName, Path = locations.CommandCompletionsPath });
            }
            else if (File.Exists(locations.CommandCompletionsPath + ".gz"))
            {
                // SmarTTY reads CommandCompletions.xml.gz when there is no plain file (the editor can store it that way).
                items.Add(new ExportItem { Name = CommandCompletionsGzipEntryName, Path = locations.CommandCompletionsPath + ".gz" });
            }
            else
            {
                report.NotFound.Add("command completions");
            }
        }

        if (Has(parts, SettingsParts.SshKey))
        {
            AddKey(items, report, "SmarTTY SSH key", SshKeyEntryName, locations.KeyStore == null ? (Func<bool>)null : () => locations.KeyStore.HasSshKey, () => locations.KeyStore.ExportSshKey());
        }
        if (Has(parts, SettingsParts.PasswordKey))
        {
            AddKey(items, report, "password protection key", PasswordKeyEntryName, locations.KeyStore == null ? (Func<bool>)null : () => locations.KeyStore.HasPasswordKey, () => locations.KeyStore.ExportPasswordKey());
        }
        if (Has(parts, SettingsParts.OpenSshKeys))
        {
            bool found = false;
            if (!string.IsNullOrEmpty(locations.UserSshDirectory))
            {
                foreach (string name in OpenSshKeyFiles)
                {
                    string path = Path.Combine(locations.UserSshDirectory, name);
                    if (File.Exists(path))
                    {
                        items.Add(new ExportItem { Name = OpenSshPrefix + name, Path = path });
                        found = true;
                    }
                }
            }
            if (!found)
            {
                report.NotFound.Add("OpenSSH keys");
            }
        }
        return items;
    }

    private static void AddIfExists(List<ExportItem> items, ExportResult report, string description, string entryName, string path)
    {
        if (File.Exists(path))
        {
            items.Add(new ExportItem { Name = entryName, Path = path });
        }
        else
        {
            report.NotFound.Add(description);
        }
    }

    private static void AddKey(List<ExportItem> items, ExportResult report, string description, string entryName, Func<bool> exists, Func<byte[]> export)
    {
        if (exists == null || !exists())
        {
            report.NotFound.Add(description);
            return;
        }
        try
        {
            byte[] data = export();
            if (data == null || data.Length == 0)
            {
                report.NotFound.Add(description);
                return;
            }
            items.Add(new ExportItem { Name = entryName, Data = data });
        }
        catch (Exception ex) when (ex is CryptographicException || ex is IOException)
        {
            // For example a key container that was created as non-exportable.
            report.Skipped.Add(entryName);
        }
    }

    // ------------------------------------------------------------------ import

    /// <summary>
    /// Reads the archive completely and tells what it contains, so that the import window only offers those parts.
    /// Throws <see cref="InvalidDataException"/> when the archive is damaged or contains an invalid entry.
    /// </summary>
    public static SettingsContents Inspect(string archivePath)
    {
        SettingsContents contents = new SettingsContents();
        List<string> ignored;
        foreach (Planned item in ReadPlan(archivePath, out ignored))
        {
            contents.Available |= item.Target.Part;
            switch (item.Target.Kind)
            {
                case EntryKind.SshConnection:
                    contents.SshProfiles++;
                    break;
                case EntryKind.RawConnection:
                    contents.RawProfiles++;
                    break;
                case EntryKind.OpenSshKey:
                    contents.OpenSshFiles.Add(item.Target.FileName);
                    break;
            }
            if (IsConnection(item.Target.Kind) && !Has(contents.Available, SettingsParts.SavedPasswords))
            {
                if (HoldsSavedSecret(item.Document))
                {
                    contents.Available |= SettingsParts.SavedPasswords;
                }
            }
        }
        contents.Ignored.AddRange(ignored);
        return contents;
    }

    // Reads and validates every known entry of the archive. Nothing is written here.
    private static List<Planned> ReadPlan(string archivePath, out List<string> ignored)
    {
        List<TarEntry> entries;
        using (FileStream input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                entries = TarReader.ReadAll(gzip);
            }
        }

        ignored = new List<string>();
        List<Planned> planned = new List<Planned>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TarEntry entry in entries)
        {
            Target target;
            if (!TryClassify(entry.Name, out target))
            {
                ignored.Add(entry.Name);
                continue;
            }
            // CommandCompletions.xml and CommandCompletions.xml.gz are the same file in two forms: only one of them may be present.
            string seenKey = target.Kind == EntryKind.CommandCompletions ? target.Kind.ToString() : target.Kind + "/" + target.FileName.ToUpperInvariant();
            if (!seen.Add(seenKey))
            {
                throw new InvalidDataException("The archive contains '" + entry.Name + "' more than once.");
            }
            XDocument document = null;
            if (target.RootElement == null)
            {
                ValidateKeyFile(entry.Name, target.FileName, entry.Data);
            }
            else
            {
                try
                {
                    document = LoadXml(target.Compressed ? Decompress(entry.Name, entry.Data) : entry.Data);
                }
                catch (XmlException ex)
                {
                    throw new InvalidDataException("'" + entry.Name + "' is not valid XML: " + ex.Message, ex);
                }
                if (document.Root == null || document.Root.Name.LocalName != target.RootElement)
                {
                    throw new InvalidDataException("'" + entry.Name + "' does not look like a SmarTTY file (expected <" + target.RootElement + ">).");
                }
            }
            planned.Add(new Planned { EntryName = entry.Name, Target = target, Document = document, Data = entry.Data });
        }
        if (planned.Count == 0)
        {
            throw new InvalidDataException("The archive does not contain any SmarTTY settings files.");
        }
        return planned;
    }

    /// <summary>
    /// Validates the archive completely, optionally backs up the current settings to
    /// <paramref name="backupPath"/>, and then writes the files. Nothing is written when validation fails.
    /// Files with the same name are replaced; saved connections that are not in the archive are kept.
    /// A saved password already stored locally is kept when the archived profile carries none.
    /// </summary>
    public static ImportResult Import(string archivePath, string dataDirectory, string localDataDirectory, string backupPath)
    {
        return Import(archivePath, Locations(dataDirectory, localDataDirectory), SettingsParts.Legacy | SettingsParts.SavedPasswords, backupPath);
    }

    /// <summary>
    /// Like the other overload, but only imports the selected <paramref name="parts"/>.
    /// Without <see cref="SettingsParts.SavedPasswords"/> the passwords of imported profiles are dropped and the local ones are kept.
    /// Before anything is replaced, the current files and keys that the import overwrites are saved to <paramref name="backupPath"/>
    /// (a backup that holds keys is as sensitive as the keys themselves).
    /// </summary>
    public static ImportResult Import(string archivePath, SettingsLocations locations, SettingsParts parts, string backupPath)
    {
        if (locations == null)
        {
            throw new ArgumentNullException("locations");
        }

        List<string> ignored;
        List<Planned> all = ReadPlan(archivePath, out ignored);
        ImportResult result = new ImportResult();
        result.Ignored.AddRange(ignored);

        List<Planned> planned = new List<Planned>();
        foreach (Planned item in all)
        {
            if (Has(parts, item.Target.Part))
            {
                planned.Add(item);
            }
            else
            {
                result.NotSelected.Add(item.EntryName);
            }
        }
        if (planned.Count == 0)
        {
            throw new InvalidDataException("The archive does not contain any of the selected settings.");
        }

        SettingsParts backupParts = SettingsParts.Legacy | SettingsParts.SavedPasswords;
        foreach (Planned item in planned)
        {
            if (IsKeyStoreKey(item.Target.Kind) && locations.KeyStore == null)
            {
                throw new InvalidOperationException("Importing keys is not available.");
            }
            if (item.Target.Kind == EntryKind.OpenSshKey && string.IsNullOrEmpty(locations.UserSshDirectory))
            {
                throw new InvalidOperationException("The .ssh folder of the user is not known.");
            }
            if (item.Target.Kind == EntryKind.CommandCompletions && string.IsNullOrEmpty(locations.CommandCompletionsPath))
            {
                throw new InvalidOperationException("The location of CommandCompletions.xml is not known.");
            }
            if (item.Target.Part == SettingsParts.SshKey || item.Target.Part == SettingsParts.PasswordKey ||
                item.Target.Part == SettingsParts.OpenSshKeys || item.Target.Part == SettingsParts.CommandCompletions)
            {
                backupParts |= item.Target.Part;
            }
        }

        if (!string.IsNullOrEmpty(backupPath) && CollectItems(locations, backupParts, new ExportResult()).Count > 0)
        {
            ExportResult backup;
            try
            {
                backup = Export(backupPath, locations, backupParts);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Importing without a safety copy would be irreversible, so stop here.
                throw new IOException("Could not back up the current settings to '" + backupPath + "': " + ex.Message, ex);
            }
            foreach (string skipped in backup.Skipped)
            {
                if (skipped.StartsWith(KeysPrefix, StringComparison.Ordinal))
                {
                    TryDelete(backupPath);
                    throw new IOException("The current key '" + skipped + "' cannot be read, so it cannot be backed up before it is replaced.");
                }
            }
            result.BackupPath = backupPath;
        }

        // Keys of the Windows key store go last: if one of them fails, the files are already consistent.
        foreach (Planned item in planned.Where(p => !IsKeyStoreKey(p.Target.Kind)).Concat(planned.Where(p => IsKeyStoreKey(p.Target.Kind))))
        {
            byte[] data = item.Data;
            switch (item.Target.Kind)
            {
                case EntryKind.Settings:
                    WriteFileReplacing(Path.Combine(locations.DataDirectory, item.Target.FileName), data);
                    result.SettingsImported = true;
                    break;
                case EntryKind.KnownHosts:
                    WriteFileReplacing(Path.Combine(locations.DataDirectory, item.Target.FileName), data);
                    result.KnownHostsImported = true;
                    break;
                case EntryKind.GuiPreferences:
                    WriteFileReplacing(Path.Combine(locations.LocalDataDirectory, item.Target.FileName), data);
                    result.GuiPreferencesImported = true;
                    break;
                case EntryKind.TftpFirmware:
                    WriteFileReplacing(Path.Combine(locations.LocalDataDirectory, item.Target.FileName), data);
                    result.TftpFirmwareImported = true;
                    break;
                case EntryKind.SshConnection:
                case EntryKind.RawConnection:
                    string path = Path.Combine(locations.DataDirectory, ConnectionsDirectoryName, item.Target.FileName);
                    if (!Has(parts, SettingsParts.SavedPasswords))
                    {
                        data = RemovePassword(item.Document, data);
                    }
                    if (File.Exists(path))
                    {
                        data = KeepExistingPassword(item.Document, data, path);
                    }
                    WriteFileReplacing(path, data);
                    if (item.Target.Kind == EntryKind.SshConnection)
                    {
                        result.SshProfiles++;
                    }
                    else
                    {
                        result.RawProfiles++;
                    }
                    break;
                case EntryKind.CommandCompletions:
                    // Keep the form of the archive. SmarTTY prefers the plain file, so the other form is removed:
                    // it would hide the imported data (a plain file) or bring back the old data (a stale gzip file).
                    string plainPath = locations.CommandCompletionsPath;
                    string gzipPath = plainPath + ".gz";
                    WriteFileReplacing(item.Target.Compressed ? gzipPath : plainPath, data);
                    string stalePath = item.Target.Compressed ? plainPath : gzipPath;
                    if (File.Exists(stalePath))
                    {
                        File.Delete(stalePath);
                    }
                    result.CommandCompletionsImported = true;
                    break;
                case EntryKind.OpenSshKey:
                    WriteFileReplacing(Path.Combine(locations.UserSshDirectory, item.Target.FileName), data);
                    result.OpenSshKeyFiles++;
                    break;
                case EntryKind.SshKey:
                    ImportKey(() => locations.KeyStore.ImportSshKey(data));
                    result.SshKeyImported = true;
                    break;
                case EntryKind.PasswordKey:
                    ImportKey(() => locations.KeyStore.ImportPasswordKey(data));
                    result.PasswordKeyImported = true;
                    break;
            }
        }
        return result;
    }

    private static void ImportKey(Action import)
    {
        try
        {
            import();
        }
        catch (CryptographicException ex)
        {
            throw new IOException("The key could not be stored in the Windows key container: " + ex.Message, ex);
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
            target = new Target { Kind = EntryKind.Settings, Part = SettingsParts.MainSettings, FileName = SettingsFileName, RootElement = "GlobalSettings" };
        }
        else if (name == KnownHostsFileName)
        {
            target = new Target { Kind = EntryKind.KnownHosts, Part = SettingsParts.KnownHosts, FileName = KnownHostsFileName, RootElement = "ArrayOfKnownHostKey" };
        }
        else if (name == LocalPrefix + GuiPreferencesFileName)
        {
            target = new Target { Kind = EntryKind.GuiPreferences, Part = SettingsParts.GuiPreferences, FileName = GuiPreferencesFileName, RootElement = "CommonGUIPreferences" };
        }
        else if (name == LocalPrefix + TftpFirmwareFileName)
        {
            target = new Target { Kind = EntryKind.TftpFirmware, Part = SettingsParts.TftpFirmware, FileName = TftpFirmwareFileName, RootElement = "TftpFirmwareSettings" };
        }
        else if (name == CommandCompletionsFileName)
        {
            target = new Target { Kind = EntryKind.CommandCompletions, Part = SettingsParts.CommandCompletions, FileName = CommandCompletionsFileName, RootElement = "CommandCompletionDatabase" };
        }
        else if (name == CommandCompletionsGzipEntryName)
        {
            target = new Target { Kind = EntryKind.CommandCompletions, Part = SettingsParts.CommandCompletions, FileName = CommandCompletionsGzipEntryName, RootElement = "CommandCompletionDatabase", Compressed = true };
        }
        else if (name == SshKeyEntryName)
        {
            target = new Target { Kind = EntryKind.SshKey, Part = SettingsParts.SshKey, FileName = name.Substring(KeysPrefix.Length), RootElement = "RSAParameters" };
        }
        else if (name == PasswordKeyEntryName)
        {
            target = new Target { Kind = EntryKind.PasswordKey, Part = SettingsParts.PasswordKey, FileName = name.Substring(KeysPrefix.Length), RootElement = "RSAParameters" };
        }
        else if (name.StartsWith(OpenSshPrefix, StringComparison.Ordinal))
        {
            // Only the fixed list of default key names, never an arbitrary file name.
            string file = name.Substring(OpenSshPrefix.Length);
            if (Array.IndexOf(OpenSshKeyFiles, file) >= 0)
            {
                target = new Target { Kind = EntryKind.OpenSshKey, Part = SettingsParts.OpenSshKeys, FileName = file, RootElement = null };
            }
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
                target = new Target { Kind = EntryKind.SshConnection, Part = SettingsParts.SshProfiles, FileName = file, RootElement = "SSHConnectionSettings" };
            }
            else if (file.EndsWith(RawConnectionExtension, StringComparison.OrdinalIgnoreCase) && file.Length > RawConnectionExtension.Length)
            {
                target = new Target { Kind = EntryKind.RawConnection, Part = SettingsParts.RawProfiles, FileName = file, RootElement = "RawConnectionSettings" };
            }
        }
        return target != null;
    }

    // Unpacks the gzip data of an entry. The size limit also covers a gzip bomb.
    private static byte[] Decompress(string entryName, byte[] data)
    {
        try
        {
            using (MemoryStream input = new MemoryStream(data))
            using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[16384];
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > MaxEntrySize)
                    {
                        throw new InvalidDataException("'" + entryName + "' is too large once unpacked.");
                    }
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }
        catch (IOException ex)
        {
            throw new InvalidDataException("'" + entryName + "' is not a valid gzip file: " + ex.Message, ex);
        }
    }

    // An OpenSSH key file is text: a PEM/OpenSSH private key, or a one-line public key. Anything else is not a key.
    private static void ValidateKeyFile(string entryName, string fileName, byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxKeyFileSize)
        {
            throw new InvalidDataException("'" + entryName + "' is empty or too large for a key file.");
        }
        string text = Encoding.UTF8.GetString(data).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        bool valid;
        if (fileName.EndsWith(".pub", StringComparison.Ordinal))
        {
            valid = text.StartsWith("ssh-", StringComparison.Ordinal) || text.StartsWith("ecdsa-", StringComparison.Ordinal);
        }
        else
        {
            valid = text.StartsWith("-----BEGIN ", StringComparison.Ordinal) && text.IndexOf("PRIVATE KEY-----", StringComparison.Ordinal) > 0;
        }
        if (!valid)
        {
            throw new InvalidDataException("'" + entryName + "' does not look like an OpenSSH key file.");
        }
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
        List<XElement> passwords = SavedSecretElementNames.SelectMany(name => document.Descendants(name)).ToList();
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

    // The saved password sits directly under the root of an SSH profile and in the Connection element of a raw one.
    private static XElement FindPassword(XDocument document, string elementName)
    {
        if (document.Root == null)
        {
            return null;
        }
        return document.Root.Element(elementName) ?? document.Root.Descendants(elementName).FirstOrDefault();
    }

    // Whether the profile holds a saved password or saved proxy credentials.
    private static bool HoldsSavedSecret(XDocument document)
    {
        foreach (string name in SavedSecretElementNames)
        {
            XElement element = FindPassword(document, name);
            if (element != null && !string.IsNullOrWhiteSpace(element.Value))
            {
                return true;
            }
        }
        return false;
    }

    // The saved password and the proxy credentials of the profile that is replaced stay with it, one after the other.
    private static byte[] KeepExistingPassword(XDocument incoming, byte[] raw, string existingPath)
    {
        foreach (string name in SavedSecretElementNames)
        {
            raw = KeepExistingElement(incoming, raw, existingPath, name);
        }
        return raw;
    }

    private static byte[] KeepExistingElement(XDocument incoming, byte[] raw, string existingPath, string elementName)
    {
        XElement incomingPassword = FindPassword(incoming, elementName);
        if (incomingPassword != null && !string.IsNullOrWhiteSpace(incomingPassword.Value))
        {
            return raw;
        }
        try
        {
            XDocument existing = LoadXml(ReadAllBytesShared(existingPath));
            XElement existingPassword = FindPassword(existing, elementName);
            if (existingPassword == null || string.IsNullOrWhiteSpace(existingPassword.Value))
            {
                return raw;
            }
            // The password goes back where the profile keeps it: the parent it had in the existing file, found by
            // name in the incoming one (the Connection element of a raw profile).
            XElement incomingContainer = incomingPassword?.Parent;
            if (incomingContainer == null)
            {
                incomingContainer = existingPassword.Parent == existing.Root
                    ? incoming.Root
                    : incoming.Root.Descendants(existingPassword.Parent.Name).FirstOrDefault();
            }
            if (incomingContainer == null)
            {
                // The incoming profile has no place for a password (another kind of connection): leave it alone.
                return raw;
            }
            incomingPassword?.Remove();
            incomingContainer.Add(new XElement(existingPassword));
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
