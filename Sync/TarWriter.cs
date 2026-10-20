using SmarTTY.IO;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SmarTTY.Sync;

public class TarWriter
{
    public class ExecutableFileMatcher
    {
        private readonly HashSet<string> _extensions = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

        private readonly HashSet<string> _names = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

        private bool _matchAll;

        public ExecutableFileMatcher(string[] items)
        {
            foreach (string item in items.EnsureNotNull())
            {
                if (item == "*")
                {
                    this._matchAll = true;
                }
                else if (item.StartsWith("-"))
                {
                    _names.Add(item.TrimStart('-'));
                }
                else
                {
                    _extensions.Add(item);
                }
            }
        }

        public bool IsExecutable(string extensionOrName)
        {
            if (this._matchAll)
            {
                if (_names.Contains(extensionOrName))
                {
                    return false;
                }
                return true;
            }
            if (_extensions.Contains(extensionOrName))
            {
                return true;
            }
            return false;
        }
    }

    private class QueuedEntry
    {
        public string Path;

        public DateTime Time;
    }

    private static DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, 0);

    public const int BlockSize = 512;

    private readonly ExecutableFileMatcher _executables;

    private static void WriteString(byte[] buffer, int offset, int length, string value)
    {
        if (value == null)
        {
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        for (int i = 0; i < length; i++)
        {
            if (i < bytes.Length)
            {
                buffer[offset + i] = bytes[i];
            }
            else
            {
                buffer[offset + i] = 0;
            }
        }
    }

    private static void WriteOctal(byte[] buffer, int offset, int length, long value)
    {
        string text = Convert.ToString(value, 8);
        if (text.Length < length - 1)
        {
            text = text.Insert(0, new string('0', length - 1 - text.Length));
        }
        WriteString(buffer, offset, length, text);
    }

    // The size field has room for 11 octal digits (up to 8 GiB - 1). Bigger values use the GNU/POSIX base-256 form: the
    // high bit of the first byte is set and the rest holds the number, big endian. Written as octal they would be cut
    // off and the archive would silently describe a file of a wrong size.
    private static void WriteNumber(byte[] buffer, int offset, int length, long value)
    {
        string text = Convert.ToString(value, 8);
        if (value >= 0 && text.Length <= length - 1)
        {
            WriteOctal(buffer, offset, length, value);
            return;
        }
        for (int i = length - 1; i >= 1; i--)
        {
            buffer[offset + i] = (byte)(value & 0xFF);
            value >>= 8;
        }
        buffer[offset] = 0x80;
    }

    // The mtime field holds seconds since 1970 in UTC. A time read as local time (DateTime.FromFileTime) has to be
    // converted first or every entry is off by the time zone offset; a time before 1970 is stored as 0 because a
    // negative number cannot be written as octal.
    internal static long ToUnixTime(DateTime dateTime)
    {
        if (dateTime.Kind == DateTimeKind.Local)
        {
            dateTime = dateTime.ToUniversalTime();
        }
        long seconds = (long)(dateTime - UnixEpoch).TotalSeconds;
        return seconds < 0 ? 0 : seconds;
    }

    // Finds the '/' at which a long entry name can be split into the ustar "prefix" (at most 155 bytes) and "name"
    // (at most 100 bytes) fields. Returns -1 when there is no such place and the GNU long name record is needed.
    // The limits are in bytes of UTF-8, not in characters: a name made of Cyrillic letters is twice as long as it looks.
    internal static int FindPrefixSplit(string entryName)
    {
        Encoding utf8 = Encoding.UTF8;
        for (int i = entryName.IndexOf('/'); i != -1 && i < entryName.Length - 1; i = entryName.IndexOf('/', i + 1))
        {
            if (utf8.GetByteCount(entryName.Substring(i + 1)) > 100)
            {
                continue;
            }
            return utf8.GetByteCount(entryName.Substring(0, i)) <= 155 ? i : -1;
        }
        return -1;
    }

    public TarWriter(string[] items)
    {
        _executables = new ExecutableFileMatcher(items);
    }

    public byte[] CreateHeader(string entryName, bool isDirectory, long size, DateTime dateTime, out int paddingBytes, bool isLongLink = false, int? customMode = null)
    {
        byte[] headerBuffer = new byte[512];
        byte[] longLinkHeader = null;
        entryName = entryName.Replace('\\', '/');
        if (isDirectory && !entryName.EndsWith("/"))
        {
            entryName += "/";
        }
        string prefix = null;
        if (Encoding.UTF8.GetByteCount(entryName) > 100)
        {
            int splitIndex = FindPrefixSplit(entryName);
            if (splitIndex != -1)
            {
                prefix = entryName.Substring(0, splitIndex);
                entryName = entryName.Substring(splitIndex + 1);
            }
            else
            {
                // GNU long name record: the name follows as data, terminated by a NUL that is counted in the size.
                byte[] bytes = Encoding.UTF8.GetBytes(entryName + "\0");
                int blocks = (bytes.Length + 511) / 512;
                longLinkHeader = new byte[512 + blocks * 512];
                this.CreateHeader("././@LongLink", isDirectory: false, bytes.Length, dateTime, out var _, isLongLink: true).CopyTo(longLinkHeader, 0);
                longLinkHeader[156] = 76;
                bytes.CopyTo(longLinkHeader, 512);
            }
        }
        paddingBytes = (int)size & 0x1FF;
        if (paddingBytes != 0)
        {
            paddingBytes = 512 - paddingBytes;
        }
        WriteString(headerBuffer, 0, 100, entryName);
        if (isLongLink)
        {
            WriteOctal(headerBuffer, 100, 8, 0L);
            WriteOctal(headerBuffer, 108, 8, 0L);
            WriteOctal(headerBuffer, 116, 8, 0L);
            WriteOctal(headerBuffer, 136, 12, 0L);
        }
        else
        {
            int mode;
            if (customMode.HasValue)
            {
                mode = customMode.Value;
            }
            else
            {
                mode = ((!isDirectory && !this._executables.IsExecutable(RemotePathUtils.GetExtension(entryName)?.TrimStart('.') ?? null)) ? Convert.ToInt32("644", 8) : Convert.ToInt32("755", 8));
            }
            WriteOctal(headerBuffer, 100, 8, mode);
            WriteOctal(headerBuffer, 108, 8, 500L);
            WriteOctal(headerBuffer, 116, 8, 544L);
            WriteOctal(headerBuffer, 136, 12, ToUnixTime(dateTime));
        }
        WriteNumber(headerBuffer, 124, 12, size);
        WriteString(headerBuffer, 257, 8, "ustar\000");
        WriteString(headerBuffer, 345, 155, prefix);
        if (isLongLink)
        {
            headerBuffer[156] = 76;
        }
        else if (isDirectory)
        {
            headerBuffer[156] = 53;
        }
        else
        {
            headerBuffer[156] = 48;
        }
        int checksum = 256;
        for (int i = 0; i < headerBuffer.Length; i++)
        {
            checksum += headerBuffer[i];
        }
        WriteOctal(headerBuffer, 148, 8, checksum);
        if (longLinkHeader != null)
        {
            byte[] combined = new byte[longLinkHeader.Length + headerBuffer.Length];
            longLinkHeader.CopyTo(combined, 0);
            headerBuffer.CopyTo(combined, longLinkHeader.Length);
            return combined;
        }
        return headerBuffer;
    }

    private static void CopyStream(Stream sourceStream, Stream destinationStream, ref int filesDone, ref long bytesDone, byte[] buffer, long length = -1L)
    {
        long totalRead = 0L;
        if (length < 0)
        {
            length = sourceStream.Length;
        }
        while (totalRead < length)
        {
            long bytesToRead = Math.Min(buffer.LongLength, length - totalRead);
            int bytesRead = sourceStream.Read(buffer, 0, (int)bytesToRead);
            if (bytesRead <= 0)
            {
                break;
            }
            destinationStream.Write(buffer, 0, bytesRead);
            totalRead += bytesRead;
            bytesDone += bytesRead;
        }
        // The header already announced "length" bytes. If the file got shorter while it was being read, the missing
        // part is zero-filled: otherwise every following header would be read from the wrong place.
        long missing = length - totalRead;
        while (missing > 0)
        {
            int zeroCount = (int)Math.Min(buffer.LongLength, missing);
            Array.Clear(buffer, 0, zeroCount);
            destinationStream.Write(buffer, 0, zeroCount);
            missing -= zeroCount;
            bytesDone += zeroCount;
        }
        filesDone++;
    }

    private void WriteDirectory(Stream destinationStream, string localDirPath, string relativePath, ref int filesDone, ref long bytesDone, byte[] paddingBuffer, byte[] copyBuffer, Predicate<string> fileFilter, Predicate<string> dirFilter = null)
    {
        List<QueuedEntry> subDirs = new List<QueuedEntry>();
        IntPtr findHandle = LocalFileSystem.FindFirstFile(localDirPath + "\\*.*", out var findData);
        if (findHandle == (IntPtr)(-1))
        {
            return;
        }
        int paddingBytes;
        try
        {
            do
            {
                if (findData.FileName == "." || findData.FileName == "..")
                {
                    continue;
                }
                if ((findData.Attributes & FileAttributes.Directory) != 0)
                {
                    subDirs.Add(new QueuedEntry
                    {
                        Path = findData.FileName,
                        Time = DateTime.FromFileTime(findData.LastWriteTimeTicks)
                    });
                    continue;
                }
                string localFilePath = Path.Combine(localDirPath, findData.FileName);
                if (fileFilter != null && !fileFilter(localFilePath))
                {
                    continue;
                }
                using FileStream fileStream = File.Open(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                string entryPath = relativePath;
                if (entryPath != "")
                {
                    entryPath += "/";
                }
                entryPath += findData.FileName;
                DateTime modifiedTime = DateTime.FromFileTime(findData.LastWriteTimeTicks);
                long length = fileStream.Length;
                byte[] headerBytes = this.CreateHeader(entryPath, isDirectory: false, length, modifiedTime, out paddingBytes);
                destinationStream.Write(headerBytes, 0, headerBytes.Length);
                TarWriter.CopyStream(fileStream, destinationStream, ref filesDone, ref bytesDone, copyBuffer, length);
                destinationStream.Write(paddingBuffer, 0, paddingBytes);
            }
            while (LocalFileSystem.FindNextFile(findHandle, out findData));
        }
        finally
        {
            LocalFileSystem.FindClose(findHandle);
        }
        foreach (QueuedEntry entry in subDirs)
        {
            string childRelativePath = relativePath;
            if (childRelativePath != "")
            {
                childRelativePath += "/";
            }
            childRelativePath += entry.Path;
            if (dirFilter == null || dirFilter(entry.Path))
            {
                destinationStream.Write(this.CreateHeader(childRelativePath, isDirectory: true, 0L, entry.Time, out paddingBytes), 0, 512);
                this.WriteDirectory(destinationStream, Path.Combine(localDirPath, entry.Path), childRelativePath, ref filesDone, ref bytesDone, paddingBuffer, copyBuffer, fileFilter, dirFilter);
            }
        }
    }

    public void WriteTree(string sourceDirPath, string archiveFilePath, Predicate<string> fileFilter, Predicate<string> dirFilter = null)
    {
        byte[] paddingBuffer = new byte[1024];
        byte[] copyBuffer = new byte[1048576];
        int filesDone = 0;
        long bytesDone = 0L;
        using FileStream stream = File.Create(archiveFilePath);
        using GZipStream gZipStream = new GZipStream(stream, CompressionMode.Compress);
        WriteDirectory(gZipStream, sourceDirPath, "", ref filesDone, ref bytesDone, paddingBuffer, copyBuffer, fileFilter, dirFilter);
        gZipStream.Write(paddingBuffer, 0, 1024);
    }
}
