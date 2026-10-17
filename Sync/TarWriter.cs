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
        if (entryName.Length > 100)
        {
            int startIndex = entryName.Length - 100;
            startIndex = entryName.IndexOf('/', startIndex);
            if (startIndex != -1 && startIndex != entryName.Length - 1)
            {
                prefix = entryName.Substring(0, startIndex);
                entryName = entryName.Substring(startIndex + 1);
            }
            else
            {
                byte[] bytes = Encoding.UTF8.GetBytes(entryName);
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
            WriteOctal(headerBuffer, 136, 12, (long)(dateTime - UnixEpoch).TotalSeconds);
        }
        WriteOctal(headerBuffer, 124, 12, size);
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
