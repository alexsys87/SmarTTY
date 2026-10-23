using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SmarTTY.LocalRepo;

/// <summary>
/// Builds the opkg package index ("Packages" and "Packages.gz") of a directory with .ipk files, the way
/// OpenWrt's scripts/ipkg-make-index.sh does. It is used when a build directory has no index yet, or when the
/// .ipk files are newer than the index (for example after "make package/foo/compile", which does not refresh it).
/// </summary>
public static class IpkIndexBuilder
{
    public sealed class Index
    {
        public byte[] Packages;

        public byte[] PackagesGz;

        /// <summary>How many packages the index lists.</summary>
        public int Count;

        /// <summary>The files that were skipped, with the reason.</summary>
        public List<string> Warnings = new List<string>();
    }

    private sealed class FileInfoCacheEntry
    {
        public long Size;

        public long Ticks;

        public string Control;

        public string Sha256;
    }

    private sealed class IndexCacheEntry
    {
        public string Signature;

        public Index Index;
    }

    private const int MaxControlTarBytes = 16 * 1024 * 1024;

    private static readonly object _CacheLock = new object();

    private static readonly Dictionary<string, FileInfoCacheEntry> _FileCache = new Dictionary<string, FileInfoCacheEntry>(StringComparer.Ordinal);

    private static readonly Dictionary<string, IndexCacheEntry> _IndexCache = new Dictionary<string, IndexCacheEntry>(StringComparer.Ordinal);

    /// <summary>True for the "kernel" and "libc" meta packages, which ipkg-make-index.sh leaves out of the index.</summary>
    private static bool IsSkippedName(string fileName)
    {
        int underscore = fileName.IndexOf('_');
        string name = underscore < 0 ? fileName : fileName.Substring(0, underscore);
        return name == "kernel" || name == "libc";
    }

    /// <summary>True when the directory itself holds at least one .ipk file.</summary>
    public static bool HasPackageFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.ipk").Any((string file) => file.EndsWith(".ipk", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The .ipk files of the directory itself (not of its subdirectories), in the order of their names.</summary>
    public static string[] FindPackageFiles(string directory)
    {
        List<string> files = new List<string>();
        foreach (string file in Directory.GetFiles(directory, "*.ipk"))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".ipk", StringComparison.OrdinalIgnoreCase) && !IsSkippedName(name))
            {
                files.Add(file);
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files.ToArray();
    }

    /// <summary>
    /// True when the index of the directory has to be generated: there are packages but no "Packages" or "Packages.gz",
    /// or some package is newer than the index and the index does not list the packages as they are (a package was added,
    /// removed or has another size). A fresh index written by the OpenWrt build is served as is, and so is an older one that
    /// still describes exactly the files of the directory (a file that was only copied or touched later): the index of the
    /// build is signed, a generated one is not, and opkg with the signature check on drops a feed without a valid signature.
    /// </summary>
    public static bool NeedsGeneratedIndex(string directory)
    {
        string[] files;
        try
        {
            files = FindPackageFiles(directory);
        }
        catch
        {
            return false;
        }
        if (files.Length == 0)
        {
            return false;
        }
        DateTime newestPackage = files.Max((string file) => File.GetLastWriteTimeUtc(file));
        DateTime? indexTime = null;
        foreach (string indexName in new[] { "Packages", "Packages.gz" })
        {
            string indexPath = Path.Combine(directory, indexName);
            if (File.Exists(indexPath))
            {
                DateTime time = File.GetLastWriteTimeUtc(indexPath);
                if (indexTime == null || time > indexTime.Value)
                {
                    indexTime = time;
                }
            }
        }
        if (indexTime == null)
        {
            return true;
        }
        return newestPackage > indexTime.Value && !IndexDescribesFiles(directory, files);
    }

    /// <summary>
    /// True when the index of the build in the directory ("Packages", or "Packages.gz" without it) lists exactly the given
    /// .ipk files, each with the size it has: the index is still right although the files are newer than it.
    /// </summary>
    internal static bool IndexDescribesFiles(string directory, string[] files)
    {
        string text;
        try
        {
            string plain = Path.Combine(directory, "Packages");
            if (File.Exists(plain))
            {
                text = File.ReadAllText(plain, Encoding.UTF8);
            }
            else
            {
                using (FileStream stream = File.OpenRead(Path.Combine(directory, "Packages.gz")))
                using (GZipStream gzip = new GZipStream(stream, CompressionMode.Decompress))
                using (StreamReader reader = new StreamReader(gzip, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }
            }
        }
        catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is UnauthorizedAccessException)
        {
            return false;
        }
        Dictionary<string, long> listed = new Dictionary<string, long>(StringComparer.Ordinal);
        string fileName = null;
        long? size = null;
        foreach (string rawLine in (text + "\n").Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (fileName != null && size.HasValue)
                {
                    listed[fileName] = size.Value;
                }
                else if (fileName != null || size.HasValue)
                {
                    // A stanza without both fields is not one of a build's index.
                    return false;
                }
                fileName = null;
                size = null;
            }
            else if (line.StartsWith("Filename:", StringComparison.Ordinal))
            {
                fileName = Path.GetFileName(line.Substring("Filename:".Length).Trim());
            }
            else if (line.StartsWith("Size:", StringComparison.Ordinal) && long.TryParse(line.Substring("Size:".Length).Trim(), out long parsed))
            {
                size = parsed;
            }
        }
        if (listed.Count != files.Length)
        {
            return false;
        }
        foreach (string file in files)
        {
            if (!listed.TryGetValue(Path.GetFileName(file), out long listedSize) || listedSize != new FileInfo(file).Length)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Returns the generated index of the directory; it is rebuilt only when the set of .ipk files changed.</summary>
    public static Index GetIndex(string directory)
    {
        string[] files = FindPackageFiles(directory);
        StringBuilder signature = new StringBuilder();
        foreach (string file in files)
        {
            FileInfo info = new FileInfo(file);
            signature.Append(info.Name).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append(';');
        }
        string key = signature.ToString();
        lock (_CacheLock)
        {
            if (_IndexCache.TryGetValue(directory, out IndexCacheEntry cached) && cached.Signature == key)
            {
                return cached.Index;
            }
        }
        Index index = Build(files);
        lock (_CacheLock)
        {
            _IndexCache[directory] = new IndexCacheEntry { Signature = key, Index = index };
        }
        return index;
    }

    public static Index Build(string directory)
    {
        return Build(FindPackageFiles(directory));
    }

    private static Index Build(string[] files)
    {
        Index index = new Index();
        StringBuilder text = new StringBuilder();
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            try
            {
                FileInfoCacheEntry entry = ReadFileInfo(file);
                if (entry.Control == null)
                {
                    index.Warnings.Add(name + ": the package has no control file");
                    continue;
                }
                text.Append(InsertFileFields(entry.Control, name, entry.Size, entry.Sha256)).Append("\n\n");
                index.Count++;
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is UnauthorizedAccessException)
            {
                index.Warnings.Add(name + ": " + exception.Message);
            }
        }
        index.Packages = new UTF8Encoding(false).GetBytes(text.ToString());
        index.PackagesGz = Gzip(index.Packages);
        return index;
    }

    /// <summary>
    /// Adds the Filename, Size and SHA256sum fields in front of the Description field, as ipkg-make-index.sh does
    /// (at the end when the control file has no Description).
    /// </summary>
    internal static string InsertFileFields(string control, string fileName, long size, string sha256)
    {
        List<string> lines = control.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        string[] fields =
        {
            "Filename: " + fileName,
            "Size: " + size,
            "SHA256sum: " + sha256
        };
        int descriptionIndex = lines.FindIndex((string line) => line.StartsWith("Description:", StringComparison.Ordinal));
        if (descriptionIndex < 0)
        {
            lines.AddRange(fields);
        }
        else
        {
            lines.InsertRange(descriptionIndex, fields);
        }
        return string.Join("\n", lines.ToArray());
    }

    private static FileInfoCacheEntry ReadFileInfo(string file)
    {
        FileInfo info = new FileInfo(file);
        lock (_CacheLock)
        {
            if (_FileCache.TryGetValue(file, out FileInfoCacheEntry cached) && cached.Size == info.Length && cached.Ticks == info.LastWriteTimeUtc.Ticks)
            {
                return cached;
            }
        }
        FileInfoCacheEntry entry = new FileInfoCacheEntry
        {
            Size = info.Length,
            Ticks = info.LastWriteTimeUtc.Ticks,
            Control = ReadControl(file),
            Sha256 = ComputeSha256(file)
        };
        lock (_CacheLock)
        {
            _FileCache[file] = entry;
        }
        return entry;
    }

    private static string ComputeSha256(string file)
    {
        using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(stream);
            StringBuilder hex = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                hex.Append(b.ToString("x2"));
            }
            return hex.ToString();
        }
    }

    /// <summary>
    /// Reads the "control" file of an .ipk: control.tar.gz, which holds ./control, inside either a gzip-compressed TAR
    /// (OpenWrt's ipkg-build) or an "ar" archive like a .deb (opkg-build of opkg-utils: Entware, Yocto, some SDKs).
    /// Returns null when either of them is missing.
    /// </summary>
    internal static string ReadControl(string ipkFile)
    {
        byte[] controlTarGz;
        using (FileStream stream = new FileStream(ipkFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            byte[] magic = new byte[ArMagic.Length];
            int magicLength = ReadFully(stream, magic, magic.Length);
            stream.Position = 0;
            if (magicLength == ArMagic.Length && magic.SequenceEqual(ArMagic))
            {
                controlTarGz = FindArMember(stream, "control.tar.gz");
            }
            else
            {
                using (GZipStream outer = new GZipStream(stream, CompressionMode.Decompress))
                {
                    controlTarGz = FindTarEntry(outer, "control.tar.gz");
                }
            }
        }
        if (controlTarGz == null)
        {
            return null;
        }
        using (MemoryStream memory = new MemoryStream(controlTarGz))
        using (GZipStream inner = new GZipStream(memory, CompressionMode.Decompress))
        {
            byte[] control = FindTarEntry(inner, "control");
            return control == null ? null : new UTF8Encoding(false).GetString(control);
        }
    }

    private static readonly byte[] ArMagic = Encoding.ASCII.GetBytes("!<arch>\n");

    /// <summary>
    /// Returns the content of a member of an "ar" archive (positioned at its start), or null when there is no such member.
    /// Member names may end with "/" (GNU ar); members start at even offsets.
    /// </summary>
    internal static byte[] FindArMember(Stream stream, string wantedName)
    {
        Skip(stream, ArMagic.Length);
        byte[] header = new byte[60];
        while (ReadFully(stream, header, header.Length) == header.Length)
        {
            if (header[58] != '`' || header[59] != '\n')
            {
                throw new InvalidDataException("The ar archive is damaged");
            }
            string name = Encoding.ASCII.GetString(header, 0, 16).TrimEnd(' ');
            if (name.EndsWith("/", StringComparison.Ordinal) && name.Length > 1)
            {
                name = name.Substring(0, name.Length - 1);
            }
            if (!long.TryParse(Encoding.ASCII.GetString(header, 48, 10).Trim(), out long size) || size < 0)
            {
                throw new InvalidDataException("The ar archive is damaged");
            }
            if (name == wantedName)
            {
                return ReadExact(stream, size);
            }
            Skip(stream, size);
            if ((size & 1) != 0)
            {
                // The padding byte; an archive that ends without it is still read.
                stream.ReadByte();
            }
        }
        return null;
    }

    /// <summary>Reads a TAR stream sequentially and returns the content of the first regular file with the given name (a leading "./" is ignored).</summary>
    internal static byte[] FindTarEntry(Stream stream, string wantedName)
    {
        byte[] header = new byte[512];
        string longName = null;
        while (ReadFully(stream, header, 512) == 512)
        {
            if (header.All((byte b) => b == 0))
            {
                return null;
            }
            long size = ParseSize(header);
            long padded = (size + 511) / 512 * 512;
            char type = (char)header[156];
            if (type == 'L')
            {
                // GNU long name: the content of this entry is the name of the next one.
                byte[] nameBytes = ReadExact(stream, size);
                Skip(stream, padded - size);
                longName = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
                continue;
            }
            if (type == 'x' || type == 'g')
            {
                Skip(stream, padded);
                continue;
            }
            string name = longName ?? ParseName(header);
            longName = null;
            if (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name.Substring(2);
            }
            if ((type == '0' || type == '\0') && name == wantedName)
            {
                return ReadExact(stream, size);
            }
            Skip(stream, padded);
        }
        return null;
    }

    private static string ParseName(byte[] header)
    {
        string name = ReadString(header, 0, 100);
        bool ustar = header[257] == 'u' && header[258] == 's' && header[259] == 't' && header[260] == 'a' && header[261] == 'r';
        if (ustar)
        {
            string prefix = ReadString(header, 345, 155);
            if (prefix.Length > 0)
            {
                name = prefix + "/" + name;
            }
        }
        return name;
    }

    private static string ReadString(byte[] buffer, int offset, int length)
    {
        int end = offset;
        while (end < offset + length && buffer[end] != 0)
        {
            end++;
        }
        return Encoding.UTF8.GetString(buffer, offset, end - offset);
    }

    private static long ParseSize(byte[] header)
    {
        if ((header[124] & 0x80) != 0)
        {
            // Base-256 size of entries larger than 8 GiB.
            long value = header[124] & 0x7F;
            for (int i = 125; i < 136; i++)
            {
                value = (value << 8) | header[i];
                if (value < 0)
                {
                    throw new InvalidDataException("The TAR entry size is out of range");
                }
            }
            return value;
        }
        long result = 0;
        for (int i = 124; i < 136; i++)
        {
            byte b = header[i];
            if (b >= '0' && b <= '7')
            {
                result = result * 8 + (b - '0');
            }
            else if (b != ' ' && b != 0)
            {
                throw new InvalidDataException("The TAR header is damaged");
            }
        }
        return result;
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
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

    private static byte[] ReadExact(Stream stream, long size)
    {
        if (size > MaxControlTarBytes)
        {
            throw new InvalidDataException("The TAR entry is too large to be a control file");
        }
        byte[] data = new byte[size];
        if (ReadFully(stream, data, (int)size) != size)
        {
            throw new InvalidDataException("The TAR archive ends unexpectedly");
        }
        return data;
    }

    private static void Skip(Stream stream, long count)
    {
        byte[] scratch = new byte[16 * 1024];
        while (count > 0)
        {
            int read = stream.Read(scratch, 0, (int)Math.Min(scratch.Length, count));
            if (read <= 0)
            {
                throw new InvalidDataException("The TAR archive ends unexpectedly");
            }
            count -= read;
        }
    }

    internal static byte[] Gzip(byte[] data)
    {
        using (MemoryStream output = new MemoryStream())
        {
            using (GZipStream gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
    }
}
