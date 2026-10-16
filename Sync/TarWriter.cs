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

        private bool _initialized;

        public ExecutableFileMatcher(string[] items)
        {
            foreach (string item in items.EnsureNotNull())
            {
                if (item == "*")
                {
                    this._initialized = true;
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

        public bool IsExecutable(string text)
        {
            if (this._initialized)
            {
                if (_names.Contains(text))
                {
                    return false;
                }
                return true;
            }
            if (_extensions.Contains(text))
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

    private static void WriteString(byte[] buffer, int value2, int value3, string text)
    {
        if (text == null)
        {
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        for (int num = 0; num < value3; num++)
        {
            if (num < bytes.Length)
            {
                buffer[value2 + num] = bytes[num];
            }
            else
            {
                buffer[value2 + num] = 0;
            }
        }
    }

    private static void WriteOctal(byte[] buffer, int value2, int value3, long value4)
    {
        string text = Convert.ToString(value4, 8);
        if (text.Length < value3 - 1)
        {
            text = text.Insert(0, new string('0', value3 - 1 - text.Length));
        }
        WriteString(buffer, value2, value3, text);
    }

    public TarWriter(string[] items)
    {
        _executables = new ExecutableFileMatcher(items);
    }

    public byte[] CreateHeader(string text2, bool flag, long value2, DateTime dateTime, out int value3, bool flag2 = false, int? value4 = null)
    {
        byte[] array = new byte[512];
        byte[] array2 = null;
        text2 = text2.Replace('\\', '/');
        if (flag && !text2.EndsWith("/"))
        {
            text2 += "/";
        }
        string text = null;
        if (text2.Length > 100)
        {
            int startIndex = text2.Length - 100;
            startIndex = text2.IndexOf('/', startIndex);
            if (startIndex != -1 && startIndex != text2.Length - 1)
            {
                text = text2.Substring(0, startIndex);
                text2 = text2.Substring(startIndex + 1);
            }
            else
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text2);
                int num = (bytes.Length + 511) / 512;
                array2 = new byte[512 + num * 512];
                this.CreateHeader("././@LongLink", flag: false, bytes.Length, dateTime, out var _, flag2: true).CopyTo(array2, 0);
                array2[156] = 76;
                bytes.CopyTo(array2, 512);
            }
        }
        value3 = (int)value2 & 0x1FF;
        if (value3 != 0)
        {
            value3 = 512 - value3;
        }
        WriteString(array, 0, 100, text2);
        if (flag2)
        {
            WriteOctal(array, 100, 8, 0L);
            WriteOctal(array, 108, 8, 0L);
            WriteOctal(array, 116, 8, 0L);
            WriteOctal(array, 136, 12, 0L);
        }
        else
        {
            int num3;
            if (value4.HasValue)
            {
                num3 = value4.Value;
            }
            else
            {
                num3 = ((!flag && !this._executables.IsExecutable(RemotePathUtils.GetExtension(text2)?.TrimStart('.') ?? null)) ? Convert.ToInt32("644", 8) : Convert.ToInt32("755", 8));
            }
            WriteOctal(array, 100, 8, num3);
            WriteOctal(array, 108, 8, 500L);
            WriteOctal(array, 116, 8, 544L);
            WriteOctal(array, 136, 12, (long)(dateTime - UnixEpoch).TotalSeconds);
        }
        WriteOctal(array, 124, 12, value2);
        WriteString(array, 257, 8, "ustar\000");
        WriteString(array, 345, 155, text);
        if (flag2)
        {
            array[156] = 76;
        }
        else if (flag)
        {
            array[156] = 53;
        }
        else
        {
            array[156] = 48;
        }
        int num4 = 256;
        for (int num5 = 0; num5 < array.Length; num5++)
        {
            num4 += array[num5];
        }
        WriteOctal(array, 148, 8, num4);
        if (array2 != null)
        {
            byte[] array3 = new byte[array2.Length + array.Length];
            array2.CopyTo(array3, 0);
            array.CopyTo(array3, array2.Length);
            return array3;
        }
        return array;
    }

    private static void CopyStream(Stream stream, Stream stream2, ref int value2, ref long value3, byte[] buffer, long value4 = -1L)
    {
        long num = 0L;
        if (value4 < 0)
        {
            value4 = stream.Length;
        }
        while (num < value4)
        {
            long num2 = Math.Min(buffer.LongLength, value4 - num);
            int num3 = stream.Read(buffer, 0, (int)num2);
            if (num3 <= 0)
            {
                break;
            }
            stream2.Write(buffer, 0, num3);
            num += num3;
            value3 += num3;
        }
        value2++;
    }

    private void WriteDirectory(Stream stream, string text4, string text5, ref int value2, ref long value3, byte[] buffer, byte[] buffer2, Predicate<string> callback, Predicate<string> callback2 = null)
    {
        List<QueuedEntry> list = new List<QueuedEntry>();
        IntPtr intPtr = LocalFileSystem.FindFirstFile(text4 + "\\*.*", out var findData);
        if (intPtr == (IntPtr)(-1))
        {
            return;
        }
        int count;
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
                    list.Add(new QueuedEntry
                    {
                        Path = findData.FileName,
                        Time = DateTime.FromFileTime(findData.LastWriteTimeTicks)
                    });
                    continue;
                }
                string text = Path.Combine(text4, findData.FileName);
                if (callback != null && !callback(text))
                {
                    continue;
                }
                using FileStream fileStream = File.Open(text, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                string text2 = text5;
                if (text2 != "")
                {
                    text2 += "/";
                }
                text2 += findData.FileName;
                DateTime dateTime = DateTime.FromFileTime(findData.LastWriteTimeTicks);
                long length = fileStream.Length;
                byte[] array = this.CreateHeader(text2, flag: false, length, dateTime, out count);
                stream.Write(array, 0, array.Length);
                TarWriter.CopyStream(fileStream, stream, ref value2, ref value3, buffer2, length);
                stream.Write(buffer, 0, count);
            }
            while (LocalFileSystem.FindNextFile(intPtr, out findData));
        }
        finally
        {
            LocalFileSystem.FindClose(intPtr);
        }
        foreach (QueuedEntry item in list)
        {
            string text3 = text5;
            if (text3 != "")
            {
                text3 += "/";
            }
            text3 += item.Path;
            if (callback2 == null || callback2(item.Path))
            {
                stream.Write(this.CreateHeader(text3, flag: true, 0L, item.Time, out count), 0, 512);
                this.WriteDirectory(stream, Path.Combine(text4, item.Path), text3, ref value2, ref value3, buffer, buffer2, callback, callback2);
            }
        }
    }

    public void WriteTree(string text, string text2, Predicate<string> callback, Predicate<string> callback2 = null)
    {
        byte[] buffer = new byte[1024];
        byte[] array = new byte[1048576];
        int num = 0;
        long num2 = 0L;
        using FileStream stream = File.Create(text2);
        using GZipStream gZipStream = new GZipStream(stream, CompressionMode.Compress);
        WriteDirectory(gZipStream, text, "", ref num, ref num2, buffer, array, callback, callback2);
        gZipStream.Write(buffer, 0, 1024);
    }
}
