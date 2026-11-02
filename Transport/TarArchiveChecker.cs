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
                bool hasData = type == '0' || type == '\0' || type == '7' || type == 'L' || type == 'K' || type == 'x' || type == 'g';
                if (!hasData)
                {
                    // Links, folders, devices and FIFOs carry no data, whatever the size field says (a hard link may repeat it).
                    size = 0;
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

    /// <summary>An octal number field, or the base-256 form that GNU and BusyBox tar use for sizes of 8 GiB and more; -1 when invalid.</summary>
    internal static long ParseNumber(byte[] header, int offset, int length)
    {
        if ((header[offset] & 0x80) != 0)
        {
            long big = header[offset] & 0x7F;
            for (int i = 1; i < length; i++)
            {
                big = (big << 8) | header[offset + i];
            }
            return big;
        }
        long value = 0;
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
                return -1;
            }
            value = value * 8 + (c - '0');
            any = true;
        }
        return value;
    }
}
