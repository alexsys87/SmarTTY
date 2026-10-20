using System;
using System.Text;

namespace SmarTTY.Terminal;

public class Utf8StreamDecoder
{
    private byte[] _carry = new byte[10];

    private int _carryLength;

    private int GetSequenceLength(byte firstByte)
    {
        if (firstByte < 128)
        {
            return 1;
        }
        int seqLen = 1;
        byte mask = 64;
        while (seqLen <= 6)
        {
            if ((firstByte & mask) == 0)
            {
                return seqLen;
            }
            seqLen++;
            mask >>= 1;
        }
        return 6;
    }

    private int CountIncompleteTail(byte[] buffer, int offset, int count)
    {
        int end = offset + count;
        int seqLen;
        for (int index = offset; index < end; index += seqLen)
        {
            seqLen = GetSequenceLength(buffer[index]);
            if (seqLen <= 0)
            {
                seqLen = 1;
            }
            if (index + seqLen > end)
            {
                return end - index;
            }
        }
        return 0;
    }

    public string Decode(byte[] buffer, int offset, int count)
    {
        if (this._carryLength != 0)
        {
            byte[] mergedBuffer = new byte[this._carry.Length + count];
            this._carry.CopyTo(mergedBuffer, 0);
            Array.Copy(buffer, offset, mergedBuffer, this._carryLength, count);
            count += this._carryLength;
            buffer = mergedBuffer;
            offset = 0;
            this._carryLength = 0;
        }
        int incompleteTailCount = this.CountIncompleteTail(buffer, offset, count);
        if (incompleteTailCount != 0)
        {
            this._carryLength = Math.Min(incompleteTailCount, this._carry.Length);
            Array.Copy(buffer, offset + count - this._carryLength, this._carry, 0, this._carryLength);
            count -= this._carryLength;
        }
        try
        {
            return Encoding.UTF8.GetString(buffer, offset, count);
        }
        catch
        {
            return Encoding.ASCII.GetString(buffer, offset, count);
        }
    }
}
