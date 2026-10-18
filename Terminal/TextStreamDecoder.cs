using System;
using System.Text;

namespace SmarTTY.Terminal;

/// <summary>
/// Stateful decoder for byte streams that arrive in arbitrary chunks (serial port, raw TCP).
/// A multi-byte character that is split between two reads is held back until its remaining
/// bytes arrive, so Cyrillic (and any other non-ASCII) text survives chunk boundaries.
/// Invalid bytes are replaced by the encoding's replacement character instead of throwing.
/// </summary>
/// <remarks>
/// Unlike <see cref="Utf8StreamDecoder"/> (used by SSH sessions, always UTF-8) this class works with
/// any <see cref="System.Text.Encoding"/>, which is what the COM/TCP terminal needs for legacy
/// single-byte Cyrillic code pages. Instances are not thread-safe; use one per connection.
/// </remarks>
public sealed class TextStreamDecoder
{
    private readonly Decoder _decoder;

    public TextStreamDecoder(Encoding encoding)
    {
        Encoding = encoding ?? throw new ArgumentNullException(nameof(encoding));
        _decoder = encoding.GetDecoder();
    }

    public Encoding Encoding { get; }

    public string Decode(byte[] buffer, int offset, int count)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }
        if (count <= 0)
        {
            return string.Empty;
        }
        int charCount = _decoder.GetCharCount(buffer, offset, count, false);
        char[] chars = new char[charCount];
        int written = _decoder.GetChars(buffer, offset, count, chars, 0, false);
        return new string(chars, 0, written);
    }

    /// <summary>Drops any partially received character, e.g. when the connection is re-established.</summary>
    public void Reset()
    {
        _decoder.Reset();
    }
}
