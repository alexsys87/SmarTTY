using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SmarTTY.IO.Archives;

public class ZipReader
{
    public struct ZipEntry
    {
        public string Name;

        public ushort CompressionMethod;

        public ushort ModTime;

        public ushort ModDate;

        public uint Crc;

        public uint CompressedSize;

        public uint UncompressedSize;

        public bool IsDirectory;

        public uint LocalHeaderOffset;

        public override string ToString()
        {
            return Name;
        }
    }

    public delegate void ProgressHandler(ulong value2, ulong value3);

    private readonly Stream _stream;

    private List<ZipEntry> _entries = new List<ZipEntry>();

    public readonly ulong TotalCompressedSize;

    public readonly ulong TotalUncompressedSize;

    public IEnumerable<ZipEntry> Entries => _entries;

    public ZipReader(Stream stream)
    {
        _stream = stream;
        byte[] array = new byte[65568];
        stream.Seek(Math.Max(0L, stream.Length - array.Length), SeekOrigin.Begin);
        int num = stream.Read(array, 0, array.Length);
        uint num2 = uint.MaxValue;
        uint num3 = uint.MaxValue;
        for (int num4 = num - 4; num4 >= 0; num4--)
        {
            if (array[num4] == 80 && array[num4 + 1] == 75 && array[num4 + 2] == 5 && array[num4 + 3] == 6)
            {
                ushort num5 = BitConverter.ToUInt16(array, num4 + 20);
                if (num4 + num5 + 22 == num)
                {
                    num2 = BitConverter.ToUInt32(array, num4 + 16);
                    num3 = BitConverter.ToUInt32(array, num4 + 12);
                    BitConverter.ToUInt16(array, num4 + 10);
                    break;
                }
            }
        }
        if (num2 == uint.MaxValue || num3 == uint.MaxValue)
        {
            throw new Exception("Cannot find end of central directory record in ZIP file");
        }
        if (num2 >= stream.Length || num2 + num3 > stream.Length)
        {
            throw new Exception("Invalid central directory offset/size in ZIP file");
        }
        array = new byte[num3];
        stream.Seek(num2, SeekOrigin.Begin);
        if (stream.Read(array, 0, (int)num3) != (int)num3)
        {
            throw new Exception("Cannot read central directory from the zip file");
        }
        int num6 = 0;
        ZipEntry item = default;
        while (num6 < num3)
        {
            if (array[num6] != 80 || array[num6 + 1] != 75 || array[num6 + 2] != 1 || array[num6 + 3] != 2)
            {
                throw new Exception("Corrupt central directory file header in ZIP file");
            }
            item.CompressionMethod = BitConverter.ToUInt16(array, num6 + 10);
            item.ModTime = BitConverter.ToUInt16(array, num6 + 12);
            item.ModDate = BitConverter.ToUInt16(array, num6 + 14);
            item.Crc = BitConverter.ToUInt32(array, num6 + 16);
            item.CompressedSize = BitConverter.ToUInt32(array, num6 + 20);
            item.UncompressedSize = BitConverter.ToUInt32(array, num6 + 24);
            item.LocalHeaderOffset = BitConverter.ToUInt32(array, num6 + 42);
            ushort num7 = BitConverter.ToUInt16(array, num6 + 28);
            ushort num8 = BitConverter.ToUInt16(array, num6 + 30);
            ushort num9 = BitConverter.ToUInt16(array, num6 + 32);
            item.Name = Encoding.UTF8.GetString(array, num6 + 46, num7);
            item.IsDirectory = item.Name.EndsWith("/");
            num6 += 46 + num7 + num8 + num9;
            TotalCompressedSize += item.CompressedSize;
            this.TotalUncompressedSize += item.UncompressedSize;
            _entries.Add(item);
        }
    }

    public void ExtractTo(ZipEntry zipEntry, Stream stream2, ProgressHandler callback = null)
    {
        if (zipEntry.LocalHeaderOffset >= _stream.Length)
        {
            throw new Exception("Invalid ZIP entry offset");
        }
        _stream.Seek(zipEntry.LocalHeaderOffset, SeekOrigin.Begin);
        byte[] array = new byte[1048576];
        _stream.Read(array, 0, 30);
        ushort num = BitConverter.ToUInt16(array, 26);
        ushort num2 = BitConverter.ToUInt16(array, 28);
        _stream.Seek(zipEntry.LocalHeaderOffset + 30 + num + num2, SeekOrigin.Begin);
        Stream stream = zipEntry.CompressionMethod switch
        {
            0 => _stream,
            8 => new DeflateStream(_stream, CompressionMode.Decompress, leaveOpen: true),
            _ => throw new Exception("Unsupported ZIP compression method: " + zipEntry.CompressionMethod + " for " + zipEntry.Name),
        };
        Crc32 crc32 = new Crc32();
        uint num3 = 0u;
        while (num3 < zipEntry.UncompressedSize)
        {
            int num4 = (int)Math.Min(zipEntry.UncompressedSize - num3, (uint)array.Length);
            if (stream.Read(array, 0, num4) != num4)
            {
                throw new Exception("Cannot read data from ZIP stream for " + zipEntry.Name);
            }
            stream2.Write(array, 0, num4);
            crc32.TransformBlock(array, 0, num4, null, 0);
            num3 += (uint)num4;
            callback?.Invoke(num3, zipEntry.UncompressedSize);
        }
        crc32.TransformFinalBlock(array, 0, 0);
        byte[] hash = crc32.Hash;
        if (((hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3]) != (int)zipEntry.Crc)
        {
            throw new Exception("ZIP CRC32 mismatch for " + zipEntry.Name);
        }
        if (stream != _stream)
        {
            stream.Dispose();
        }
    }

    public bool ExtractToDirectory(ZipEntry zipEntry, string text2, bool flag = false)
    {
        if (Path.IsPathRooted(zipEntry.Name))
        {
            return false;
        }
        if (zipEntry.Name.EndsWith("/") || zipEntry.Name.EndsWith("\\"))
        {
            return false;
        }
        string text = zipEntry.Name;
        if (flag)
        {
            int num = text.IndexOfAny(new char[2] { '\\', '/' });
            if (num == -1)
            {
                return false;
            }
            text = text.Substring(num + 1);
        }
        string path = Path.Combine(text2, text);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            ExtractTo(zipEntry, fileStream);
        }
        return true;
    }
}
