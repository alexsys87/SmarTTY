using System;
using System.Text;

namespace SmarTTY.Terminal;

public class Utf8StreamDecoder
{
    private byte[] _carry = new byte[10];

    private int _carryLength;

    private int GetSequenceLength(byte b)
    {
        if (b < 128)
        {
            return 1;
        }
        byte b2 = 1;
        byte b3 = 64;
        while (b2 <= 6)
        {
            if ((b & b3) == 0)
            {
                return b2;
            }
            b2++;
            b3 >>= 1;
        }
        return 6;
    }

    private int CountIncompleteTail(byte[] buffer, int value2, int value3)
    {
        int num2;
        for (int num = value2; num < value3; num += num2)
        {
            num2 = GetSequenceLength(buffer[num]);
            if (num2 <= 0)
            {
                num2 = 1;
            }
            if (num + num2 > value3)
            {
                return value3 - num;
            }
        }
        return 0;
    }

    public string Decode(byte[] buffer, int value2, int value3)
    {
        if (this._carryLength != 0)
        {
            byte[] array = new byte[this._carry.Length + value3];
            this._carry.CopyTo(array, 0);
            Array.Copy(buffer, value2, array, this._carryLength, value3);
            value3 += this._carryLength;
            buffer = array;
            value2 = 0;
            this._carryLength = 0;
        }
        int num = this.CountIncompleteTail(buffer, value2, value3);
        if (num != 0)
        {
            this._carryLength = Math.Min(num, this._carry.Length);
            Array.Copy(buffer, value2 + value3 - this._carryLength, this._carry, 0, this._carryLength);
            value3 -= this._carryLength;
        }
        try
        {
            return Encoding.UTF8.GetString(buffer, value2, value3);
        }
        catch
        {
            return Encoding.ASCII.GetString(buffer, value2, value3);
        }
    }
}
