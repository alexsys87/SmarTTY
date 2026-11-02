using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;

namespace SmarTTY.TextEditing;

/// <summary>One checksum of the bytes: its name and its value as hexadecimal digits.</summary>
public sealed class HexChecksum
{
    public HexChecksum(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string Value { get; }
}

/// <summary>
/// The checksums the hex editor computes for the selection or the whole file: the simple sums of boot loaders and
/// serial protocols, the CRCs of firmware images (CRC-32 is the one of U-Boot and zlib) and the usual hashes.
/// </summary>
public static class HexChecksums
{
    private static readonly uint[] Crc32Table = CreateCrc32Table();

    public static IReadOnlyList<HexChecksum> Compute(byte[] bytes)
    {
        bytes = bytes ?? Array.Empty<byte>();
        List<HexChecksum> result = new List<HexChecksum>
        {
            new HexChecksum("Sum (8-bit)", Sum8(bytes).ToString("X2", CultureInfo.InvariantCulture)),
            new HexChecksum("Sum (32-bit)", Sum32(bytes).ToString("X8", CultureInfo.InvariantCulture)),
            new HexChecksum("XOR (8-bit)", Xor8(bytes).ToString("X2", CultureInfo.InvariantCulture)),
            new HexChecksum("CRC-16/MODBUS", Crc16Modbus(bytes).ToString("X4", CultureInfo.InvariantCulture)),
            new HexChecksum("CRC-16/CCITT-FALSE", Crc16Ccitt(bytes, 0xFFFF).ToString("X4", CultureInfo.InvariantCulture)),
            new HexChecksum("CRC-16/XMODEM", Crc16Ccitt(bytes, 0x0000).ToString("X4", CultureInfo.InvariantCulture)),
            new HexChecksum("CRC-32", Crc32(bytes).ToString("X8", CultureInfo.InvariantCulture))
        };
        using (MD5 md5 = MD5.Create())
        {
            result.Add(new HexChecksum("MD5", ToHex(md5.ComputeHash(bytes))));
        }
        using (SHA1 sha1 = SHA1.Create())
        {
            result.Add(new HexChecksum("SHA-1", ToHex(sha1.ComputeHash(bytes))));
        }
        using (SHA256 sha256 = SHA256.Create())
        {
            result.Add(new HexChecksum("SHA-256", ToHex(sha256.ComputeHash(bytes))));
        }
        return result;
    }

    public static byte Sum8(byte[] bytes)
    {
        int sum = 0;
        foreach (byte value in bytes)
        {
            sum += value;
        }
        return (byte)sum;
    }

    public static uint Sum32(byte[] bytes)
    {
        uint sum = 0;
        foreach (byte value in bytes)
        {
            sum = unchecked(sum + value);
        }
        return sum;
    }

    public static byte Xor8(byte[] bytes)
    {
        byte result = 0;
        foreach (byte value in bytes)
        {
            result ^= value;
        }
        return result;
    }

    /// <summary>CRC-16/MODBUS: polynomial 0x8005 reflected, initial value 0xFFFF.</summary>
    public static ushort Crc16Modbus(byte[] bytes)
    {
        ushort crc = 0xFFFF;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = ((crc & 1) != 0) ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }
        return crc;
    }

    /// <summary>CRC-16 with the CCITT polynomial 0x1021, not reflected: CCITT-FALSE starts at 0xFFFF, XMODEM at 0.</summary>
    public static ushort Crc16Ccitt(byte[] bytes, ushort initial)
    {
        ushort crc = initial;
        foreach (byte value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = ((crc & 0x8000) != 0) ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    /// <summary>CRC-32 of zlib, Ethernet and U-Boot images.</summary>
    public static uint Crc32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in bytes)
        {
            crc = Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }
        return ~crc;
    }

    private static uint[] CreateCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < 256; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = ((value & 1) != 0) ? (value >> 1) ^ 0xEDB88320 : value >> 1;
            }
            table[index] = value;
        }
        return table;
    }

    private static string ToHex(byte[] hash)
    {
        return HexDocument.FormatHex(hash).Replace(" ", "").ToLowerInvariant();
    }
}
