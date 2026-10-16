using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace SmarTTY.Utilities;

public sealed class Crc32 : HashAlgorithm
{
    public const uint DefaultPolynomial = 3988292384u;

    public const uint DefaultSeed = uint.MaxValue;

    private static uint[] _defaultTable;

    private readonly uint _seed;

    private readonly uint[] _table;

    private uint _hash;

    public override int HashSize => 32;

    public Crc32()
        : this(3988292384u, uint.MaxValue)
    {
    }

    public Crc32(uint value2, uint value3)
    {
        _table = Crc32.InitializeTable(value2);
        _seed = (_hash = value3);
    }

    public override void Initialize()
    {
        _hash = _seed;
    }

    protected override void HashCore(byte[] array, int ibStart, int cbSize)
    {
        _hash = UpdateHash(_table, _hash, array, ibStart, cbSize);
    }

    protected override byte[] HashFinal()
    {
        return HashValue = ToBigEndianBytes(~_hash);
    }

    public static uint Compute(byte[] buffer)
    {
        return ComputeWithSeed(uint.MaxValue, buffer);
    }

    public static uint ComputeWithSeed(uint value2, byte[] buffer)
    {
        return ComputeWithPolynomial(3988292384u, value2, buffer);
    }

    public static uint ComputeWithPolynomial(uint value2, uint value3, byte[] buffer)
    {
        return ~UpdateHash(Crc32.InitializeTable(value2), value3, buffer, 0, buffer.Length);
    }

    private static uint[] InitializeTable(uint value2)
    {
        if (value2 == 3988292384u && _defaultTable != null)
        {
            return _defaultTable;
        }
        uint[] array = new uint[256];
        for (int num = 0; num < 256; num++)
        {
            uint num2 = (uint)num;
            for (int num3 = 0; num3 < 8; num3++)
            {
                num2 = (((num2 & 1) != 1) ? (num2 >> 1) : ((num2 >> 1) ^ value2));
            }
            array[num] = num2;
        }
        if (value2 == 3988292384u)
        {
            _defaultTable = array;
        }
        return array;
    }

    private static uint UpdateHash(uint[] items, uint value2, IList<byte> list, int value3, int value4)
    {
        uint num = value2;
        for (int num2 = value3; num2 < value4 - value3; num2++)
        {
            num = (num >> 8) ^ items[list[num2] ^ (num & 0xFF)];
        }
        return num;
    }

    private static byte[] ToBigEndianBytes(uint value2)
    {
        byte[] bytes = BitConverter.GetBytes(value2);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }
        return bytes;
    }
}
