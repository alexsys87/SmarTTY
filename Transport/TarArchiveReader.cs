using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SmarTTY.Transport;

/// <summary>What a member of a tar archive is.</summary>
public enum TarEntryType
{
    File,

    Directory,

    SymbolicLink,

    HardLink,

    /// <summary>
    /// A device node or a FIFO (no data), or a kind this reader does not use: a GNU sparse file ('S'), a dump directory ('D') or a
    /// vendor type. Such a member is listed and passed on as it is, and the data that follows its header (the size of the header says
    /// how much) is passed with it.
    /// </summary>
    Other,

    /// <summary>A pax global header ('g'): it applies to the whole archive, so a filtered archive keeps it.</summary>
    GlobalHeader
}

/// <summary>A member of a tar archive as <see cref="TarArchiveReader"/> reads it.</summary>
public sealed class TarEntry
{
    /// <summary>The path relative to the root of the archive, without "./", a leading "/" or a trailing "/" ("etc/config").</summary>
    public string Name { get; set; }

    public TarEntryType Type { get; set; }

    /// <summary>
    /// The size of the data after the header of the member: the length of a regular file, or of what a type of another kind keeps
    /// (the stored part of a sparse file); 0 for a link, a folder, a device and a FIFO.
    /// </summary>
    public long Size { get; set; }

    public string LinkName { get; set; }

    public override string ToString()
    {
        return Type + " " + Name;
    }
}

/// <summary>
/// Reads a plain tar stream member by member, the way GNU and BusyBox tar write it: ustar headers with the prefix field, GNU long
/// names and link names ('L', 'K') and pax extended headers ('x', 'g'). The blocks of every member (its extension headers, its header
/// and its data) are available as they are, so that <see cref="Filter"/> can write a smaller archive with only the selected members
/// that unpacks exactly like the original. Pure logic without WPF or SSH, tested by tests/RemoteBackupTests.
/// </summary>
public sealed class TarArchiveReader
{
    private const int BlockSize = 512;

    private readonly Stream _stream;

    private readonly MemoryStream _headerBlocks = new MemoryStream();

    private long _dataLeft;

    private long _paddingLeft;

    /// <summary>The end marker of tar (a zero block) was read: the archive is complete.</summary>
    public bool ReachedEnd { get; private set; }

    /// <summary>Why the archive could not be read to its end; null while it can.</summary>
    public string Problem { get; private set; }

    public TarArchiveReader(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>The raw blocks of the current member up to its data: the extension headers ('L', 'K', 'x') and the header itself.</summary>
    public byte[] HeaderBlocks => _headerBlocks.ToArray();

    /// <summary>
    /// The next member, or null at the end of the archive (<see cref="ReachedEnd"/>) or when it is damaged or cut off
    /// (<see cref="Problem"/>). The data of the previous member is skipped if it was not read.
    /// </summary>
    public TarEntry Next()
    {
        SkipData();
        _headerBlocks.SetLength(0);
        string longName = null;
        string longLink = null;
        Dictionary<string, string> pax = null;
        byte[] header = new byte[BlockSize];
        while (true)
        {
            int read = ReadFully(_stream, header, 0, BlockSize);
            if (read == 0)
            {
                Problem = "the archive ends without the end marker of tar: it was cut off";
                return null;
            }
            if (read < BlockSize)
            {
                Problem = "the archive ends in the middle of a header: it was cut off";
                return null;
            }
            if (IsZeroBlock(header))
            {
                ReachedEnd = true;
                return null;
            }
            if (!ChecksumMatches(header))
            {
                Problem = "a header of the archive is damaged";
                return null;
            }
            _headerBlocks.Write(header, 0, BlockSize);
            long size = TarArchiveChecker.ParseNumber(header, 124, 12);
            if (size < 0)
            {
                Problem = "a header of the archive has an invalid size";
                return null;
            }
            char type = (char)header[156];
            string mapProblem = TarArchiveChecker.ReadSparseExtensions(_stream, header, _headerBlocks);
            if (mapProblem != null)
            {
                Problem = mapProblem;
                return null;
            }
            if (type == 'L' || type == 'K' || type == 'x')
            {
                // An extension header: its data belongs to the header blocks of the member that follows.
                byte[] data = ReadPadded(size);
                if (data == null)
                {
                    return null;
                }
                if (type == 'L')
                {
                    longName = CString(data, 0, (int)Math.Min(size, data.Length));
                }
                else if (type == 'K')
                {
                    longLink = CString(data, 0, (int)Math.Min(size, data.Length));
                }
                else
                {
                    pax = ParsePax(data, size);
                }
                continue;
            }

            TarEntry entry = new TarEntry();
            string name = longName;
            if (name == null)
            {
                name = CString(header, 0, 100);
                bool posixUstar = header[257] == 'u' && header[258] == 's' && header[259] == 't' && header[260] == 'a' && header[261] == 'r' && header[262] == 0;
                string prefix = posixUstar ? CString(header, 345, 155) : "";
                if (prefix.Length > 0)
                {
                    name = prefix + "/" + name;
                }
            }
            string link = longLink ?? CString(header, 157, 100);
            if (pax != null)
            {
                if (pax.TryGetValue("path", out string paxPath))
                {
                    name = paxPath;
                }
                if (pax.TryGetValue("linkpath", out string paxLink))
                {
                    link = paxLink;
                }
                if (pax.TryGetValue("size", out string paxSize) && long.TryParse(paxSize, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
                {
                    size = parsed;
                }
            }
            switch (type)
            {
                case '0':
                case '\0':
                case '7':
                    entry.Type = TarEntryType.File;
                    break;
                case '5':
                    entry.Type = TarEntryType.Directory;
                    break;
                case '2':
                    entry.Type = TarEntryType.SymbolicLink;
                    break;
                case '1':
                    entry.Type = TarEntryType.HardLink;
                    break;
                case 'g':
                    entry.Type = TarEntryType.GlobalHeader;
                    break;
                default:
                    entry.Type = TarEntryType.Other;
                    break;
            }
            if (entry.Type == TarEntryType.File && name.EndsWith("/", StringComparison.Ordinal))
            {
                // Old tar programs mark folders by the trailing slash only.
                entry.Type = TarEntryType.Directory;
            }
            // A type that is none of the above still has its data after the header (a sparse file, a dump directory): skipping it
            // by the size is what tells where the next header is. Devices and FIFOs have none, whatever the size field says.
            bool hasData = entry.Type == TarEntryType.File || entry.Type == TarEntryType.GlobalHeader
                || (entry.Type == TarEntryType.Other && !TarArchiveChecker.CarriesNoData(type));
            entry.Size = hasData ? size : 0;
            entry.Name = NormalizeName(name);
            entry.LinkName = link;
            _dataLeft = entry.Size;
            _paddingLeft = (entry.Size + BlockSize - 1) / BlockSize * BlockSize - entry.Size;
            return entry;
        }
    }

    /// <summary>
    /// Copies the data of the current member (with the padding to whole blocks when <paramref name="withPadding"/> is set) to
    /// <paramref name="destination"/> (null skips it); <paramref name="observer"/> sees the data bytes without the padding.
    /// False when the archive ends in the middle of it.
    /// </summary>
    public bool CopyData(Stream destination, bool withPadding, Action<byte[], int, int> observer = null)
    {
        byte[] buffer = new byte[64 * 1024];
        while (_dataLeft > 0)
        {
            int read = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, _dataLeft));
            if (read <= 0)
            {
                Problem = "the archive ends in the middle of a file: it was cut off";
                return false;
            }
            destination?.Write(buffer, 0, read);
            observer?.Invoke(buffer, 0, read);
            _dataLeft -= read;
        }
        while (_paddingLeft > 0)
        {
            int read = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, _paddingLeft));
            if (read <= 0)
            {
                Problem = "the archive ends in the middle of a file: it was cut off";
                return false;
            }
            if (withPadding)
            {
                destination?.Write(buffer, 0, read);
            }
            _paddingLeft -= read;
        }
        return true;
    }

    private void SkipData()
    {
        if (_dataLeft > 0 || _paddingLeft > 0)
        {
            CopyData(null, false);
        }
    }

    private byte[] ReadPadded(long size)
    {
        if (size > 16 * 1024 * 1024)
        {
            Problem = "an extension header of the archive is too big";
            return null;
        }
        int padded = (int)((size + BlockSize - 1) / BlockSize * BlockSize);
        byte[] data = new byte[padded];
        if (ReadFully(_stream, data, 0, padded) < padded)
        {
            Problem = "the archive ends in the middle of a header: it was cut off";
            return null;
        }
        _headerBlocks.Write(data, 0, padded);
        return data;
    }

    /// <summary>"./etc/config/" and "/etc/config" are both "etc/config"; "./" alone is "".</summary>
    public static string NormalizeName(string name)
    {
        string result = (name ?? "").Replace('\\', '/');
        while (true)
        {
            if (result.StartsWith("./", StringComparison.Ordinal))
            {
                result = result.Substring(2);
            }
            else if (result.StartsWith("/", StringComparison.Ordinal))
            {
                result = result.Substring(1);
            }
            else
            {
                break;
            }
        }
        result = result.TrimEnd('/');
        return result == "." ? "" : result;
    }

    /// <summary>The records "length key=value\n" of a pax extended header.</summary>
    internal static Dictionary<string, string> ParsePax(byte[] data, long size)
    {
        Dictionary<string, string> records = new Dictionary<string, string>(StringComparer.Ordinal);
        int position = 0;
        int end = (int)Math.Min(size, data.Length);
        while (position < end)
        {
            int space = Array.IndexOf(data, (byte)' ', position, end - position);
            if (space < 0)
            {
                break;
            }
            if (!int.TryParse(Encoding.ASCII.GetString(data, position, space - position), NumberStyles.None, CultureInfo.InvariantCulture, out int length) || length <= 0 || position + length > end)
            {
                break;
            }
            string record = Encoding.UTF8.GetString(data, space + 1, position + length - space - 2);
            int equals = record.IndexOf('=');
            if (equals > 0)
            {
                records[record.Substring(0, equals)] = record.Substring(equals + 1);
            }
            position += length;
        }
        return records;
    }

    private static string CString(byte[] data, int offset, int length)
    {
        int end = Array.IndexOf(data, (byte)0, offset, length);
        return Encoding.UTF8.GetString(data, offset, (end < 0 ? offset + length : end) - offset);
    }

    private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        int done = 0;
        while (done < count)
        {
            int read = stream.Read(buffer, offset + done, count - done);
            if (read <= 0)
            {
                break;
            }
            done += read;
        }
        return done;
    }

    private static bool IsZeroBlock(byte[] block)
    {
        foreach (byte value in block)
        {
            if (value != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static bool ChecksumMatches(byte[] header)
    {
        long stored = TarArchiveChecker.ParseNumber(header, 148, 8);
        long unsignedSum = 0;
        long signedSum = 0;
        for (int i = 0; i < BlockSize; i++)
        {
            bool field = i >= 148 && i < 156;
            unsignedSum += field ? ' ' : header[i];
            signedSum += field ? ' ' : (sbyte)header[i];
        }
        return stored == unsignedSum || stored == signedSum;
    }

    /// <summary>The members of an archive (without the global headers), and whether it reads to its end marker.</summary>
    public static List<TarEntry> List(Stream tar, out string problem, Func<bool> isCancelled = null)
    {
        TarArchiveReader reader = new TarArchiveReader(tar);
        List<TarEntry> entries = new List<TarEntry>();
        TarEntry entry;
        while ((entry = reader.Next()) != null)
        {
            if (isCancelled?.Invoke() == true)
            {
                throw new OperationCanceledException();
            }
            if (entry.Type != TarEntryType.GlobalHeader)
            {
                entries.Add(entry);
            }
        }
        problem = reader.ReachedEnd ? null : reader.Problem ?? "the archive could not be read";
        return entries;
    }

    /// <summary>A regular file written by <see cref="Filter"/>, with the MD5 of its data (for the check after the restore).</summary>
    public sealed class FilteredFile
    {
        public string Name;

        public string Md5;

        public long Size;
    }

    /// <summary>
    /// Writes to <paramref name="output"/> a tar archive with the members of <paramref name="input"/> for which
    /// <paramref name="include"/> is true (the global headers are always kept), block for block as they are in the original, and the
    /// end marker. Returns the regular files written, with the MD5 of each. Throws <see cref="InvalidDataException"/> when the
    /// archive is damaged or cut off.
    /// </summary>
    public static List<FilteredFile> Filter(Stream input, Stream output, Func<TarEntry, bool> include, Func<bool> isCancelled = null, Action<TarEntry> onEntry = null)
    {
        TarArchiveReader reader = new TarArchiveReader(input);
        List<FilteredFile> files = new List<FilteredFile>();
        TarEntry entry;
        while ((entry = reader.Next()) != null)
        {
            if (isCancelled?.Invoke() == true)
            {
                throw new OperationCanceledException();
            }
            bool keep = entry.Type == TarEntryType.GlobalHeader || include(entry);
            if (!keep)
            {
                continue;
            }
            onEntry?.Invoke(entry);
            byte[] headers = reader.HeaderBlocks;
            output.Write(headers, 0, headers.Length);
            if (entry.Type == TarEntryType.File)
            {
                using MD5 md5 = MD5.Create();
                bool complete = reader.CopyData(output, true, (buffer, offset, count) => md5.TransformBlock(buffer, offset, count, null, 0));
                md5.TransformFinalBlock(new byte[0], 0, 0);
                if (!complete)
                {
                    break;
                }
                files.Add(new FilteredFile { Name = entry.Name, Size = entry.Size, Md5 = ToHex(md5.Hash) });
            }
            else if (!reader.CopyData(output, true))
            {
                break;
            }
        }
        if (!reader.ReachedEnd)
        {
            throw new InvalidDataException("The archive cannot be restored: " + (reader.Problem ?? "it could not be read") + ".");
        }
        // The end marker: two zero blocks.
        output.Write(new byte[BlockSize * 2], 0, BlockSize * 2);
        return files;
    }

    public static string ToHex(byte[] hash)
    {
        StringBuilder text = new StringBuilder(hash.Length * 2);
        foreach (byte value in hash)
        {
            text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }
}
