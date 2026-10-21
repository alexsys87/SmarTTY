using System;
using System.Text;

namespace SmarTTY.Terminal;

public class Utf8StreamDecoder
{
    /// <summary>
    /// Invalid bytes become U+DC80..U+DCFF (one character per byte, like Python's "surrogateescape") instead of
    /// U+FFFD, so the terminal can tell which byte arrived. A shell that counts bytes (BusyBox without Unicode
    /// support) echoes a letter typed in the middle of a line one byte at a time, with a redraw in between, and the
    /// terminal joins the halves again (see <see cref="ByteColumns"/>).
    /// </summary>
    public const char EscapedByteBase = '\uDC00';

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    private byte[] _carry = new byte[10];

    private int _carryLength;

    // Length of an unfinished UTF-8 sequence at the end of the buffer, to be completed by the next read. It is found
    // by looking back from the end for the last lead byte: walking forward from the start of the buffer lost its
    // alignment after an invalid byte and split a following letter (D0 | D0 B5 decoded "е" as two stray bytes).
    private static int CountIncompleteTail(byte[] buffer, int offset, int count)
    {
        int end = offset + count;
        for (int back = 1; back <= 3 && back <= count; back++)
        {
            byte value = buffer[end - back];
            if (ByteColumns.IsContinuation(value))
            {
                continue;
            }
            return (ByteColumns.SequenceLength(value) > back) ? back : 0;
        }
        return 0;
    }

    /// <summary>When set, invalid bytes are returned as U+DC80..U+DCFF instead of U+FFFD (see <see cref="EscapedByteBase"/>).</summary>
    public bool EscapeInvalidBytes { get; set; }

    public static bool IsEscapedByte(char ch)
    {
        return ch >= '\uDC80' && ch <= '\uDCFF';
    }

    // Strict UTF-8, except that every byte that does not belong to a valid sequence becomes U+DC80..U+DCFF.
    // (A DecoderFallback cannot do this: the decoder rejects unpaired surrogates in fallback output.)
    private static string DecodeEscaping(byte[] buffer, int offset, int count)
    {
        StringBuilder result = new StringBuilder(count);
        int end = offset + count;
        int index = offset;
        while (index < end)
        {
            byte value = buffer[index];
            if (value < 0x80)
            {
                result.Append((char)value);
                index++;
                continue;
            }
            int length = ByteColumns.SequenceLength(value);
            if (length > 0 && index + length <= end)
            {
                try
                {
                    result.Append(StrictUtf8.GetString(buffer, index, length));
                    index += length;
                    continue;
                }
                catch (DecoderFallbackException)
                {
                }
            }
            result.Append((char)(EscapedByteBase + value));
            index++;
        }
        return result.ToString();
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
        int incompleteTailCount = CountIncompleteTail(buffer, offset, count);
        if (incompleteTailCount != 0)
        {
            this._carryLength = Math.Min(incompleteTailCount, this._carry.Length);
            Array.Copy(buffer, offset + count - this._carryLength, this._carry, 0, this._carryLength);
            count -= this._carryLength;
        }
        try
        {
            return EscapeInvalidBytes ? DecodeEscaping(buffer, offset, count) : Encoding.UTF8.GetString(buffer, offset, count);
        }
        catch
        {
            return Encoding.ASCII.GetString(buffer, offset, count);
        }
    }
}
