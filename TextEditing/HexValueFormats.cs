using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SmarTTY.TextEditing;

/// <summary>How the data inspector of the hex editor reads the bytes at the caret.</summary>
public enum HexValueType
{
    Binary,
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float32,
    Float64,
    UnixTime32,
    Utf8Char
}

/// <summary>The text forms the hex editor can copy bytes as.</summary>
public enum HexCopyFormat
{
    /// <summary>"48 65 6C".</summary>
    HexSpaced,

    /// <summary>"48656C".</summary>
    HexCompact,

    /// <summary>A C array: "unsigned char data[3] = { 0x48, 0x65, 0x6C };".</summary>
    CArray,

    /// <summary>A Python bytes literal: b"\x48\x65\x6c".</summary>
    PythonBytes,

    Base64,

    /// <summary>The bytes decoded as UTF-8.</summary>
    Utf8Text
}

/// <summary>
/// Reads and writes numbers in the bytes of the hex editor in either byte order, swaps the byte order of a block and
/// formats bytes for the clipboard. It has no UI: the data inspector and the context menu of
/// <see cref="Controls.HexEditorControl"/> use it.
/// </summary>
public static class HexValueFormats
{
    /// <summary>The rows of the data inspector, in order.</summary>
    public static readonly IReadOnlyList<HexValueType> InspectorTypes = new[]
    {
        HexValueType.Binary,
        HexValueType.Int8,
        HexValueType.UInt8,
        HexValueType.Int16,
        HexValueType.UInt16,
        HexValueType.Int32,
        HexValueType.UInt32,
        HexValueType.Int64,
        HexValueType.UInt64,
        HexValueType.Float32,
        HexValueType.Float64,
        HexValueType.UnixTime32,
        HexValueType.Utf8Char
    };

    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    public static string GetName(HexValueType type)
    {
        switch (type)
        {
            case HexValueType.Binary:
                return "Binary";
            case HexValueType.Int8:
                return "Int8";
            case HexValueType.UInt8:
                return "UInt8";
            case HexValueType.Int16:
                return "Int16";
            case HexValueType.UInt16:
                return "UInt16";
            case HexValueType.Int32:
                return "Int32";
            case HexValueType.UInt32:
                return "UInt32";
            case HexValueType.Int64:
                return "Int64";
            case HexValueType.UInt64:
                return "UInt64";
            case HexValueType.Float32:
                return "Float";
            case HexValueType.Float64:
                return "Double";
            case HexValueType.UnixTime32:
                return "time_t (32)";
            case HexValueType.Utf8Char:
                return "UTF-8 char";
            default:
                return type.ToString();
        }
    }

    /// <summary>The number of bytes the value takes (the shortest UTF-8 character for <see cref="HexValueType.Utf8Char"/>).</summary>
    public static int GetSize(HexValueType type)
    {
        switch (type)
        {
            case HexValueType.Int16:
            case HexValueType.UInt16:
                return 2;
            case HexValueType.Int32:
            case HexValueType.UInt32:
            case HexValueType.Float32:
            case HexValueType.UnixTime32:
                return 4;
            case HexValueType.Int64:
            case HexValueType.UInt64:
            case HexValueType.Float64:
                return 8;
            default:
                return 1;
        }
    }

    /// <summary>The byte order changes how the value is read (it takes more than one byte and is not text).</summary>
    public static bool UsesByteOrder(HexValueType type)
    {
        return GetSize(type) > 1;
    }

    /// <summary>Reads an unsigned number of <paramref name="size"/> bytes (1 to 8) at <paramref name="offset"/>.</summary>
    public static ulong ReadUnsigned(byte[] data, int offset, int size, bool bigEndian)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }
        if (size < 1 || size > 8 || offset < 0 || offset > data.Length - size)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }
        ulong value = 0;
        for (int index = 0; index < size; index++)
        {
            int position = bigEndian ? offset + index : offset + size - 1 - index;
            value = (value << 8) | data[position];
        }
        return value;
    }

    /// <summary>The lowest <paramref name="size"/> bytes of <paramref name="value"/> in the given byte order.</summary>
    public static byte[] WriteUnsigned(ulong value, int size, bool bigEndian)
    {
        if (size < 1 || size > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }
        byte[] bytes = new byte[size];
        for (int index = 0; index < size; index++)
        {
            byte part = (byte)(value >> (8 * index));
            bytes[bigEndian ? size - 1 - index : index] = part;
        }
        return bytes;
    }

    /// <summary>
    /// Reads the value that starts at the first of <paramref name="bytes"/> (the bytes from the caret on). False when
    /// there are too few bytes for it.
    /// </summary>
    public static bool TryRead(byte[] bytes, HexValueType type, bool bigEndian, out string text)
    {
        text = "";
        if (bytes == null)
        {
            return false;
        }
        int size = GetSize(type);
        if (bytes.Length < size)
        {
            return false;
        }
        ulong raw = ReadUnsigned(bytes, 0, size, bigEndian);
        switch (type)
        {
            case HexValueType.Binary:
                text = Convert.ToString(bytes[0], 2).PadLeft(8, '0');
                return true;
            case HexValueType.Int8:
                text = ((sbyte)bytes[0]).ToString(CultureInfo.InvariantCulture);
                return true;
            case HexValueType.UInt8:
                text = bytes[0].ToString(CultureInfo.InvariantCulture);
                return true;
            case HexValueType.Int16:
                text = ((short)raw).ToString(CultureInfo.InvariantCulture);
                return true;
            case HexValueType.UInt16:
                text = ((ushort)raw).ToString(CultureInfo.InvariantCulture);
                return true;
            case HexValueType.Int32:
                text = ((int)raw).ToString(CultureInfo.InvariantCulture);
                return true;
            case HexValueType.UInt32:
                text = ((uint)raw).ToString(CultureInfo.InvariantCulture);
                return true;
            case HexValueType.Int64:
                text = ((long)raw).ToString(CultureInfo.InvariantCulture);
                return true;
            case HexValueType.UInt64:
                text = raw.ToString(CultureInfo.InvariantCulture);
                return true;
            case HexValueType.Float32:
                text = FormatFloat(BitConverter.ToSingle(ToMachineOrder(WriteUnsigned(raw, 4, bigEndian: false)), 0));
                return true;
            case HexValueType.Float64:
                text = FormatDouble(BitConverter.ToDouble(ToMachineOrder(WriteUnsigned(raw, 8, bigEndian: false)), 0));
                return true;
            case HexValueType.UnixTime32:
                text = DateTimeOffset.FromUnixTimeSeconds((uint)raw).UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture) + " UTC";
                return true;
            case HexValueType.Utf8Char:
                return TryReadUtf8Char(bytes, out text);
            default:
                return false;
        }
    }

    /// <summary>
    /// Converts the text typed in the data inspector into the bytes of the value. Integers are decimal or 0x hexadecimal,
    /// the binary byte is eight 0 and 1 digits, the time is "yyyy-MM-dd HH:mm:ss" (UTC) or a number of seconds.
    /// </summary>
    public static bool TryParse(HexValueType type, string text, bool bigEndian, out byte[] bytes, out string error)
    {
        bytes = null;
        error = null;
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0 && type != HexValueType.Utf8Char)
        {
            error = "Enter a value.";
            return false;
        }
        int size = GetSize(type);
        switch (type)
        {
            case HexValueType.Binary:
            {
                string digits = trimmed.Replace(" ", "").Replace("_", "");
                if (digits.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                {
                    digits = digits.Substring(2);
                }
                if (digits.Length == 0 || digits.Length > 8 || digits.Trim('0', '1').Length != 0)
                {
                    error = "Enter up to eight binary digits, for example 01001000.";
                    return false;
                }
                bytes = new[] { Convert.ToByte(digits, 2) };
                return true;
            }
            case HexValueType.Int8:
            case HexValueType.Int16:
            case HexValueType.Int32:
            case HexValueType.Int64:
            {
                if (!TryParseInteger(trimmed, size, signed: true, out ulong value))
                {
                    error = "Enter a whole number from " + SignedMin(size).ToString(CultureInfo.InvariantCulture) + " to "
                        + SignedMax(size).ToString(CultureInfo.InvariantCulture) + " (or 0x hexadecimal).";
                    return false;
                }
                bytes = WriteUnsigned(value, size, bigEndian);
                return true;
            }
            case HexValueType.UInt8:
            case HexValueType.UInt16:
            case HexValueType.UInt32:
            case HexValueType.UInt64:
            {
                if (!TryParseInteger(trimmed, size, signed: false, out ulong value))
                {
                    error = "Enter a whole number from 0 to " + UnsignedMax(size).ToString(CultureInfo.InvariantCulture) + " (or 0x hexadecimal).";
                    return false;
                }
                bytes = WriteUnsigned(value, size, bigEndian);
                return true;
            }
            case HexValueType.Float32:
            {
                if (!TryParseReal(trimmed, out double value) || (!double.IsInfinity(value) && !double.IsNaN(value) && Math.Abs(value) > float.MaxValue))
                {
                    error = "Enter a number such as 1.5, -2e-3, NaN or Infinity (a float holds up to about 3.4e38).";
                    return false;
                }
                byte[] machine = BitConverter.GetBytes((float)value);
                bytes = WriteUnsigned(ReadUnsigned(ToLittleEndian(machine), 0, 4, bigEndian: false), 4, bigEndian);
                return true;
            }
            case HexValueType.Float64:
            {
                if (!TryParseReal(trimmed, out double value))
                {
                    error = "Enter a number such as 1.5, -2e-3, NaN or Infinity.";
                    return false;
                }
                byte[] machine = BitConverter.GetBytes(value);
                bytes = WriteUnsigned(ReadUnsigned(ToLittleEndian(machine), 0, 8, bigEndian: false), 8, bigEndian);
                return true;
            }
            case HexValueType.UnixTime32:
            {
                string time = trimmed.EndsWith("UTC", StringComparison.OrdinalIgnoreCase) ? trimmed.Substring(0, trimmed.Length - 3).Trim() : trimmed;
                long seconds;
                if (DateTime.TryParseExact(time, new[] { TimeFormat, "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss" }, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
                {
                    seconds = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc)).ToUnixTimeSeconds();
                }
                else if (TryParseInteger(time, 8, signed: true, out ulong number))
                {
                    seconds = (long)number;
                }
                else
                {
                    error = "Enter the time as yyyy-MM-dd HH:mm:ss (UTC) or as seconds since 1970.";
                    return false;
                }
                if (seconds < 0 || seconds > uint.MaxValue)
                {
                    error = "A 32-bit time holds 1970-01-01 to 2106-02-07.";
                    return false;
                }
                bytes = WriteUnsigned((ulong)seconds, 4, bigEndian);
                return true;
            }
            case HexValueType.Utf8Char:
            {
                if (text == null || text.Length == 0)
                {
                    error = "Enter one character.";
                    return false;
                }
                string character = (text.Trim().Length > 0) ? text.Trim() : text;
                int length = char.IsSurrogatePair(character, 0) ? 2 : 1;
                if (character.Length != length)
                {
                    error = "Enter one character.";
                    return false;
                }
                bytes = Encoding.UTF8.GetBytes(character);
                return true;
            }
            default:
                error = "This value cannot be edited.";
                return false;
        }
    }

    /// <summary>
    /// Reverses the order of the bytes in each group of <paramref name="groupSize"/> bytes (2: 16-bit words, 4: 32-bit,
    /// 8: 64-bit). Bytes after the last whole group stay as they are.
    /// </summary>
    public static byte[] SwapByteOrder(byte[] bytes, int groupSize)
    {
        if (bytes == null)
        {
            return Array.Empty<byte>();
        }
        if (groupSize < 2)
        {
            return (byte[])bytes.Clone();
        }
        byte[] result = (byte[])bytes.Clone();
        int whole = bytes.Length - bytes.Length % groupSize;
        for (int start = 0; start < whole; start += groupSize)
        {
            Array.Reverse(result, start, groupSize);
        }
        return result;
    }

    public static string GetName(HexCopyFormat format)
    {
        switch (format)
        {
            case HexCopyFormat.HexSpaced:
                return "Hex (48 65 6C)";
            case HexCopyFormat.HexCompact:
                return "Hex without spaces (48656C)";
            case HexCopyFormat.CArray:
                return "C array";
            case HexCopyFormat.PythonBytes:
                return "Python bytes";
            case HexCopyFormat.Base64:
                return "Base64";
            case HexCopyFormat.Utf8Text:
                return "Text (UTF-8)";
            default:
                return format.ToString();
        }
    }

    /// <summary>The bytes as text in <paramref name="format"/>.</summary>
    public static string Format(byte[] bytes, HexCopyFormat format)
    {
        bytes = bytes ?? Array.Empty<byte>();
        switch (format)
        {
            case HexCopyFormat.HexSpaced:
                return HexDocument.FormatHex(bytes);
            case HexCopyFormat.HexCompact:
                return HexDocument.FormatHex(bytes).Replace(" ", "");
            case HexCopyFormat.CArray:
            {
                StringBuilder text = new StringBuilder();
                text.Append("unsigned char data[").Append(bytes.Length.ToString(CultureInfo.InvariantCulture)).Append("] = {");
                for (int index = 0; index < bytes.Length; index++)
                {
                    text.Append((index % 16 == 0) ? "\r\n    " : " ");
                    text.Append("0x").Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
                    if (index + 1 < bytes.Length)
                    {
                        text.Append(',');
                    }
                }
                text.Append(bytes.Length > 0 ? "\r\n};" : " };");
                return text.ToString();
            }
            case HexCopyFormat.PythonBytes:
            {
                StringBuilder text = new StringBuilder(bytes.Length * 4 + 3);
                text.Append("b\"");
                foreach (byte value in bytes)
                {
                    text.Append("\\x").Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }
                text.Append('"');
                return text.ToString();
            }
            case HexCopyFormat.Base64:
                return Convert.ToBase64String(bytes);
            case HexCopyFormat.Utf8Text:
                return Encoding.UTF8.GetString(bytes);
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary><paramref name="count"/> bytes that repeat <paramref name="pattern"/> (the fill of a selection).</summary>
    public static byte[] RepeatPattern(byte[] pattern, int count)
    {
        if (pattern == null || pattern.Length == 0)
        {
            throw new ArgumentException("The pattern is empty.", nameof(pattern));
        }
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        byte[] result = new byte[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = pattern[index % pattern.Length];
        }
        return result;
    }

    private static bool TryReadUtf8Char(byte[] bytes, out string text)
    {
        text = "";
        byte first = bytes[0];
        int length;
        if (first < 0x80)
        {
            length = 1;
        }
        else if ((first & 0xE0) == 0xC0)
        {
            length = 2;
        }
        else if ((first & 0xF0) == 0xE0)
        {
            length = 3;
        }
        else if ((first & 0xF8) == 0xF0)
        {
            length = 4;
        }
        else
        {
            text = "(not the start of a character)";
            return true;
        }
        if (bytes.Length < length)
        {
            text = "(incomplete)";
            return true;
        }
        int codePoint = (length == 1) ? first : first & (0x7F >> length);
        for (int index = 1; index < length; index++)
        {
            if ((bytes[index] & 0xC0) != 0x80)
            {
                text = "(invalid sequence)";
                return true;
            }
            codePoint = (codePoint << 6) | (bytes[index] & 0x3F);
        }
        bool overlong = (length == 2 && codePoint < 0x80) || (length == 3 && codePoint < 0x800) || (length == 4 && codePoint < 0x10000);
        if (overlong || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
        {
            text = "(invalid sequence)";
            return true;
        }
        string code = "U+" + codePoint.ToString("X4", CultureInfo.InvariantCulture);
        bool visible = codePoint >= 0x20 && codePoint != 0x7F && !(codePoint >= 0x80 && codePoint < 0xA0);
        text = visible ? "'" + char.ConvertFromUtf32(codePoint) + "' " + code : code;
        return true;
    }

    private static bool TryParseInteger(string text, int size, bool signed, out ulong value)
    {
        value = 0;
        text = text.Replace("_", "").Trim();
        bool negative = false;
        if (text.StartsWith("-", StringComparison.Ordinal))
        {
            negative = true;
            text = text.Substring(1).Trim();
        }
        else if (text.StartsWith("+", StringComparison.Ordinal))
        {
            text = text.Substring(1).Trim();
        }
        ulong magnitude;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out magnitude))
            {
                return false;
            }
        }
        else if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out magnitude))
        {
            return false;
        }
        if (signed)
        {
            ulong limit = negative ? (ulong)(-(SignedMin(size) + 1)) + 1 : (ulong)SignedMax(size);
            // A hexadecimal value may be given as its unsigned bit pattern: 0xFF is -1 for Int8.
            bool hexPattern = !negative && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && magnitude <= UnsignedMax(size);
            if (magnitude > limit && !hexPattern)
            {
                return false;
            }
            value = negative ? unchecked(0UL - magnitude) : magnitude;
        }
        else
        {
            if (negative && magnitude != 0)
            {
                return false;
            }
            if (magnitude > UnsignedMax(size))
            {
                return false;
            }
            value = magnitude;
        }
        if (size < 8)
        {
            value &= UnsignedMax(size);
        }
        return true;
    }

    private static bool TryParseReal(string text, out double value)
    {
        string normalized = text.Trim();
        if (string.Equals(normalized, "nan", StringComparison.OrdinalIgnoreCase))
        {
            value = double.NaN;
            return true;
        }
        if (string.Equals(normalized, "inf", StringComparison.OrdinalIgnoreCase) || string.Equals(normalized, "infinity", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "+inf", StringComparison.OrdinalIgnoreCase) || string.Equals(normalized, "+infinity", StringComparison.OrdinalIgnoreCase))
        {
            value = double.PositiveInfinity;
            return true;
        }
        if (string.Equals(normalized, "-inf", StringComparison.OrdinalIgnoreCase) || string.Equals(normalized, "-infinity", StringComparison.OrdinalIgnoreCase))
        {
            value = double.NegativeInfinity;
            return true;
        }
        // A decimal comma is accepted too: the decimal separator of many locales.
        return double.TryParse(normalized.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string FormatDouble(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static long SignedMin(int size)
    {
        return (size >= 8) ? long.MinValue : -(1L << (size * 8 - 1));
    }

    private static long SignedMax(int size)
    {
        return (size >= 8) ? long.MaxValue : (1L << (size * 8 - 1)) - 1;
    }

    private static ulong UnsignedMax(int size)
    {
        return (size >= 8) ? ulong.MaxValue : (1UL << (size * 8)) - 1;
    }

    // BitConverter works in the byte order of the machine.
    private static byte[] ToMachineOrder(byte[] littleEndian)
    {
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(littleEndian);
        }
        return littleEndian;
    }

    private static byte[] ToLittleEndian(byte[] machine)
    {
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(machine);
        }
        return machine;
    }
}
