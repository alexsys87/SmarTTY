using System;
using System.IO;
using System.IO.Compression;

namespace SmarTTY.Transport;

/// <summary>What <see cref="TarArchiveChecker"/> found in an archive.</summary>
public sealed class TarCheckResult
{
    /// <summary>The members of the archive (files, folders, links), without the extension headers of GNU and pax.</summary>
    public long Entries { get; set; }

    /// <summary>The size of the regular files in the archive, before compression.</summary>
    public long DataBytes { get; set; }

    /// <summary>The archive ends with the end marker of tar (zero blocks) and every header is valid.</summary>
    public bool IsComplete { get; set; }

    /// <summary>Why the archive is not complete; null when it is.</summary>
    public string Problem { get; set; }
}

/// <summary>
/// Reads a tar.gz archive from start to end the way tar unpacks it, without writing anything: every header must have a valid
/// checksum, the data of every member must be there, and the archive must end with the end marker that tar writes when it
/// has finished. That tells a complete backup from one that was cut off (the connection was lost, tar was killed). Pure logic,
/// tested by tests/RemoteBackupTests.
/// </summary>
public static class TarArchiveChecker
{
    private const int BlockSize = 512;

    /// <summary>Checks the gzip-compressed archive at <paramref name="path"/>.</summary>
    public static TarCheckResult CheckGzipFile(string path, Func<bool> isCancelled = null)
    {
        using FileStream file = File.OpenRead(path);
        using GZipStream gzip = new GZipStream(file, CompressionMode.Decompress);
        return Check(gzip, isCancelled);
    }

    /// <summary>Checks a plain (already decompressed) tar stream.</summary>
    public static TarCheckResult Check(Stream tar, Func<bool> isCancelled = null)
    {
        TarCheckResult result = new TarCheckResult();
        byte[] header = new byte[BlockSize];
        byte[] skip = new byte[64 * 1024];
        try
        {
            while (true)
            {
                if (isCancelled?.Invoke() == true)
                {
                    throw new OperationCanceledException();
                }
                int read = ReadBlock(tar, header);
                if (read == 0)
                {
                    result.Problem = "the archive ends without the end marker of tar: it was cut off";
                    return result;
                }
                if (read < BlockSize)
                {
                    result.Problem = "the archive ends in the middle of a header: it was cut off";
                    return result;
                }
                if (IsZeroBlock(header))
                {
                    // The end marker: tar writes two zero blocks (and pads the last record). One zero block followed by the end of
                    // the data is accepted too: some small tar programs write only that.
                    result.IsComplete = true;
                    return result;
                }
                if (!ChecksumMatches(header))
                {
                    result.Problem = "a header of the archive is damaged (after " + result.Entries + " entries)";
                    return result;
                }
                long size = ParseNumber(header, 124, 12);
                if (size < 0)
                {
                    result.Problem = "a header of the archive has an invalid size (after " + result.Entries + " entries)";
                    return result;
                }
                char type = (char)header[156];
                if (CarriesNoData(type))
                {
                    // Links, folders, devices and FIFOs carry no data, whatever the size field says (a hard link may repeat it).
                    size = 0;
                }
                string mapProblem = ReadSparseExtensions(tar, header, null);
                if (mapProblem != null)
                {
                    result.Problem = mapProblem;
                    return result;
                }
                if (type != 'L' && type != 'K' && type != 'x' && type != 'g')
                {
                    result.Entries++;
                    if (type == '0' || type == '\0' || type == '7')
                    {
                        result.DataBytes += size;
                    }
                }
                long padded = (size + BlockSize - 1) / BlockSize * BlockSize;
                while (padded > 0)
                {
                    int chunk = tar.Read(skip, 0, (int)Math.Min(skip.Length, padded));
                    if (chunk <= 0)
                    {
                        result.Problem = "the archive ends in the middle of a file: it was cut off";
                        return result;
                    }
                    padded -= chunk;
                }
            }
        }
        catch (InvalidDataException exception)
        {
            result.Problem = "the gzip data is damaged: " + exception.Message;
            return result;
        }
        catch (EndOfStreamException)
        {
            result.Problem = "the gzip data ends too early: the archive was cut off";
            return result;
        }
    }

    /// <summary>
    /// True for the types whose size field is not the length of data after the header: links, folders, devices and FIFOs ('1' to
    /// '6'). Every other type has its data after the header, by the size field: the regular files and the extension headers, and
    /// also a GNU sparse file ('S'), a dump directory ('D'), a multi-volume continuation ('M') and the vendor types ('A' to 'Z'),
    /// because POSIX has a reader treat a type it does not know like a regular file.
    /// </summary>
    internal static bool CarriesNoData(char type)
    {
        return type >= '1' && type <= '6';
    }

    /// <summary>The most extension blocks of the map of holes of one sparse file that are read (16 MiB), as a header cut by the same rule.</summary>
    internal const int MaxSparseExtensionBlocks = 32768;

    /// <summary>
    /// The old GNU format of a sparse file ('S') keeps the first four pieces of its map of holes in the header; when the flag at
    /// offset 482 is set, the blocks of further pieces follow the header (21 in each, with a flag of their own at offset 504 that says
    /// whether another block follows), and the data comes after them. Reads those blocks (and writes them to <paramref name="copy"/>
    /// when it is given). Returns null when there are none or they are read, otherwise the reason that the archive cannot be read on.
    /// </summary>
    internal static string ReadSparseExtensions(Stream tar, byte[] header, Stream copy)
    {
        if (header[156] != (byte)'S' || header[482] == 0)
        {
            return null;
        }
        byte[] block = new byte[BlockSize];
        for (int count = 0; count < MaxSparseExtensionBlocks; count++)
        {
            if (ReadBlock(tar, block) < BlockSize)
            {
                return "the archive ends in the middle of a header: it was cut off";
            }
            copy?.Write(block, 0, BlockSize);
            if (block[504] == 0)
            {
                return null;
            }
        }
        return "the map of holes of a sparse file in the archive is too big";
    }

    private static int ReadBlock(Stream stream, byte[] block)
    {
        int done = 0;
        while (done < block.Length)
        {
            int read = stream.Read(block, done, block.Length - done);
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

    /// <summary>The header checksum: the sum of all bytes with the checksum field taken as spaces (signed sums are accepted too).</summary>
    private static bool ChecksumMatches(byte[] header)
    {
        long stored = ParseNumber(header, 148, 8);
        long unsignedSum = 0;
        long signedSum = 0;
        for (int i = 0; i < BlockSize; i++)
        {
            int value = (i >= 148 && i < 156) ? ' ' : header[i];
            unsignedSum += value;
            signedSum += (i >= 148 && i < 156) ? ' ' : (sbyte)header[i];
        }
        return stored == unsignedSum || stored == signedSum;
    }

    /// <summary>An octal number field, or the base-256 form that GNU and BusyBox tar use for sizes of 8 GiB and more; -1 when invalid or negative.</summary>
    internal static long ParseNumber(byte[] header, int offset, int length)
    {
        return TryParseNumber(header, offset, length, out long value) && value >= 0 ? value : -1;
    }

    /// <summary>
    /// A number field of a header: octal (an empty field is 0), or base-256 that GNU and BusyBox tar use for what octal cannot hold,
    /// sizes of 8 GiB and more and times before 1970 or after 2242. A base-256 field is a big-endian two's complement number: bit 7
    /// of its first byte only marks the form, bit 6 is the sign. False when the field is no number or does not fit into a long.
    /// </summary>
    internal static bool TryParseNumber(byte[] header, int offset, int length, out long value)
    {
        value = 0;
        if ((header[offset] & 0x80) != 0)
        {
            bool negative = (header[offset] & 0x40) != 0;
            long big = negative ? -1 : 0;
            for (int i = 0; i < length; i++)
            {
                int current = header[offset + i];
                if (i == 0)
                {
                    // Bit 7 is the marker, not data: put the sign extension in its place.
                    current = negative ? (current | 0x80) : (current & 0x7F);
                }
                // The next byte shifts the number by 8 bits: it must not lose any bit that matters.
                if (big > (long.MaxValue >> 8) || big < (long.MinValue >> 8))
                {
                    return false;
                }
                big = (big << 8) | (long)current;
            }
            value = big;
            return true;
        }
        long number = 0;
        bool any = false;
        for (int i = offset; i < offset + length; i++)
        {
            byte c = header[i];
            if (c == 0 || c == ' ')
            {
                if (any)
                {
                    break;
                }
                continue;
            }
            if (c < '0' || c > '7')
            {
                return false;
            }
            number = number * 8 + (c - '0');
            any = true;
        }
        value = number;
        return true;
    }
}
