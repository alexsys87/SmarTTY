using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SmarTTY.Transport;

/// <summary>
/// Turns the datagrams of a Linux netconsole sender into the bytes shown in the terminal.
/// <list type="bullet">
/// <item>Plain netconsole sends the console output as it is (several lines or part of one per datagram): it is passed on
/// unchanged.</item>
/// <item>Extended netconsole ("+" in the netconsole= parameter, or "extended" in configfs) sends one kernel record per
/// datagram in the /dev/kmsg format, "level,sequence,microseconds,flags;text" followed by " KEY=value" lines, with the
/// kernel release in front when "r" is set, and splits a long record into several datagrams marked
/// "ncfrag=offset/total". It is shown the way dmesg shows it: "[   12.345678] text", coloured by the level when that is
/// wanted. Characters the kernel escaped (\xNN: control characters, bytes from 127 up, the backslash) are restored, so
/// UTF-8 text and colour sequences come out as they were printed.</item>
/// </list>
/// Not thread-safe: one reader calls <see cref="Decode"/> for every datagram in the order they arrived.
/// </summary>
internal sealed class NetconsoleDecoder
{
    /// <summary>Recognise extended messages (otherwise every datagram is passed on as it is).</summary>
    public bool DecodeExtended { get; set; } = true;

    /// <summary>Colour the timestamp and the text of extended messages by their log level.</summary>
    public bool ColorizeLevels { get; set; } = true;

    /// <summary>Put "[address] " in front of every line.</summary>
    public bool ShowSender { get; set; }

    // The longest header looked for: "6.12.0-rc1-something,7,1234567890,12345678901234,-,ncfrag=1234/5678;".
    private const int MaxHeaderLength = 160;

    // A record the kernel splits is at most a few kilobytes (printk record plus user data); anything claiming more
    // is not reassembled but shown as it comes.
    private const int MaxReassembledLength = 64 * 1024;

    private const string ResetColor = "\u001b[0m";

    private readonly List<byte> _output = new List<byte>();

    // Whether the last byte shown ended a line (a datagram of plain netconsole may end in the middle of one).
    private bool _atLineStart = true;

    private string _lastSender;

    private Fragments _pending;

    private sealed class Fragments
    {
        public string Sender;
        public string Sequence;
        public Header Header;
        public byte[] Body;
        public bool[] Received;
        public int ReceivedCount;
    }

    /// <summary>The parsed header of an extended message.</summary>
    internal struct Header
    {
        public int Level;
        public int Facility;
        public string Sequence;
        public ulong TimestampMicroseconds;
        public string Flags;
        public int FragmentOffset;
        public int FragmentTotal;
        // Where the text starts (just after the ';').
        public int BodyOffset;

        public bool IsFragment => FragmentTotal > 0;
    }

    /// <summary>Forgets the state of the previous connection (a half line, a record that was being reassembled).</summary>
    public void Reset()
    {
        _atLineStart = true;
        _lastSender = null;
        _pending = null;
    }

    /// <summary>
    /// The bytes to show for one datagram (possibly none: a fragment of a record that is not complete yet).
    /// <paramref name="sender"/> is the address it came from, shown with <see cref="ShowSender"/>.
    /// </summary>
    public byte[] Decode(byte[] datagram, int offset, int count, string sender)
    {
        _output.Clear();
        if (count <= 0)
        {
            return Array.Empty<byte>();
        }
        if (DecodeExtended && TryParseHeader(datagram, offset, count, out Header header))
        {
            if (header.IsFragment)
            {
                AddFragment(datagram, offset, count, header, sender);
            }
            else
            {
                if (_pending != null)
                {
                    // A record that never got all its pieces: what arrived of it is still worth seeing.
                    FlushPending();
                }
                WriteRecord(header, datagram, offset + header.BodyOffset, count - header.BodyOffset, sender);
            }
        }
        else
        {
            if (_pending != null)
            {
                FlushPending();
            }
            WriteRaw(datagram, offset, count, sender);
        }
        return _output.ToArray();
    }

    /// <summary>
    /// Reads "[release,]level,sequence,microseconds,flags[,key=value...];" at the start of the datagram. False for
    /// anything else: plain console output is not mistaken for a header because all four numbers and the ';' must be
    /// there within the first <see cref="MaxHeaderLength"/> bytes, without a space or a line break before the ';'.
    /// </summary>
    internal static bool TryParseHeader(byte[] data, int offset, int count, out Header header)
    {
        header = default;
        int limit = Math.Min(count, MaxHeaderLength);
        int semicolon = -1;
        for (int i = 0; i < limit; i++)
        {
            byte b = data[offset + i];
            if (b == (byte)';')
            {
                semicolon = i;
                break;
            }
            if (b <= (byte)' ' || b >= 127)
            {
                return false;
            }
        }
        if (semicolon <= 0)
        {
            return false;
        }
        string[] fields = Encoding.ASCII.GetString(data, offset, semicolon).Split(',');
        // The level comes first, or second after the kernel release (which has dots and letters, never only digits).
        int first;
        if (fields.Length >= 4 && IsNumber(fields[0]) && IsNumber(fields[1]) && IsNumber(fields[2]) && IsFlags(fields[3]))
        {
            first = 0;
        }
        else if (fields.Length >= 5 && !IsNumber(fields[0]) && fields[0].Length > 0 && IsNumber(fields[1]) && IsNumber(fields[2]) && IsNumber(fields[3]) && IsFlags(fields[4]))
        {
            first = 1;
        }
        else
        {
            return false;
        }
        if (!int.TryParse(fields[first], NumberStyles.None, CultureInfo.InvariantCulture, out int priority)
            || !ulong.TryParse(fields[first + 2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong timestamp))
        {
            return false;
        }
        header.Level = priority & 7;
        header.Facility = priority >> 3;
        header.Sequence = fields[first + 1];
        header.TimestampMicroseconds = timestamp;
        header.Flags = fields[first + 3];
        header.BodyOffset = semicolon + 1;
        for (int i = first + 4; i < fields.Length; i++)
        {
            const string fragmentKey = "ncfrag=";
            if (fields[i].StartsWith(fragmentKey, StringComparison.Ordinal))
            {
                string[] parts = fields[i].Substring(fragmentKey.Length).Split('/');
                if (parts.Length != 2
                    || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int fragmentOffset)
                    || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int total)
                    || total <= 0 || fragmentOffset >= total)
                {
                    return false;
                }
                header.FragmentOffset = fragmentOffset;
                header.FragmentTotal = total;
            }
        }
        return true;
    }

    private static bool IsNumber(string text)
    {
        if (text.Length == 0 || text.Length > 20)
        {
            return false;
        }
        foreach (char c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }
        return true;
    }

    // "-" for an ordinary record, "c" for a continued one, "+" in some versions; one or two characters.
    private static bool IsFlags(string text)
    {
        return text.Length >= 1 && text.Length <= 2 && !IsNumber(text);
    }

    private void AddFragment(byte[] datagram, int offset, int count, Header header, string sender)
    {
        int chunkLength = count - header.BodyOffset;
        if (header.FragmentTotal > MaxReassembledLength || header.FragmentOffset + chunkLength > header.FragmentTotal)
        {
            // Not something that can be put together: show the piece as it is.
            WriteRecord(header, datagram, offset + header.BodyOffset, chunkLength, sender);
            return;
        }
        if (_pending != null && (_pending.Sender != sender || _pending.Sequence != header.Sequence || _pending.Body.Length != header.FragmentTotal))
        {
            FlushPending();
        }
        if (_pending == null)
        {
            _pending = new Fragments
            {
                Sender = sender,
                Sequence = header.Sequence,
                Header = header,
                Body = new byte[header.FragmentTotal],
                Received = new bool[header.FragmentTotal]
            };
        }
        Buffer.BlockCopy(datagram, offset + header.BodyOffset, _pending.Body, header.FragmentOffset, chunkLength);
        for (int i = header.FragmentOffset; i < header.FragmentOffset + chunkLength; i++)
        {
            if (!_pending.Received[i])
            {
                _pending.Received[i] = true;
                _pending.ReceivedCount++;
            }
        }
        if (_pending.ReceivedCount == _pending.Body.Length)
        {
            Fragments complete = _pending;
            _pending = null;
            WriteRecord(complete.Header, complete.Body, 0, complete.Body.Length, complete.Sender);
        }
    }

    /// <summary>Shows what arrived of a record whose other pieces were lost.</summary>
    private void FlushPending()
    {
        Fragments pending = _pending;
        _pending = null;
        List<byte> body = new List<byte>(pending.ReceivedCount);
        for (int i = 0; i < pending.Body.Length; i++)
        {
            if (pending.Received[i])
            {
                body.Add(pending.Body[i]);
            }
        }
        byte[] bytes = body.ToArray();
        WriteRecord(pending.Header, bytes, 0, bytes.Length, pending.Sender);
    }

    /// <summary>"[   12.345678] text" for one kernel record; the " KEY=value" lines after the text are left out.</summary>
    private void WriteRecord(Header header, byte[] data, int offset, int count, string sender)
    {
        int textLength = 0;
        while (textLength < count && data[offset + textLength] != (byte)'\n')
        {
            textLength++;
        }
        byte[] text = Unescape(data, offset, textLength);
        if (!_atLineStart)
        {
            // Plain output before it stopped in the middle of a line.
            AddAscii("\r\n");
            _atLineStart = true;
        }
        BeginLine(sender);
        ulong seconds = header.TimestampMicroseconds / 1000000UL;
        ulong microseconds = header.TimestampMicroseconds % 1000000UL;
        string timestamp = "[" + seconds.ToString(CultureInfo.InvariantCulture).PadLeft(5) + "." + microseconds.ToString("D6", CultureInfo.InvariantCulture) + "] ";
        string levelColor = ColorizeLevels ? LevelColor(header.Level) : null;
        if (ColorizeLevels)
        {
            AddAscii("\u001b[32m" + timestamp + ResetColor);
        }
        else
        {
            AddAscii(timestamp);
        }
        if (levelColor != null)
        {
            AddAscii(levelColor);
        }
        // A record with several lines (an escaped line break inside the text) is shown with its later lines indented
        // under the text, like dmesg does.
        string indent = new string(' ', timestamp.Length);
        foreach (byte b in text)
        {
            if (b == (byte)'\n')
            {
                if (levelColor != null)
                {
                    AddAscii(ResetColor);
                }
                AddAscii("\r\n");
                _atLineStart = true;
                BeginLine(sender);
                AddAscii(indent);
                if (levelColor != null)
                {
                    AddAscii(levelColor);
                }
                continue;
            }
            _output.Add(b);
        }
        if (levelColor != null)
        {
            AddAscii(ResetColor);
        }
        AddAscii("\r\n");
        _atLineStart = true;
    }

    /// <summary>Plain console output, passed on as it is (with the sender in front of each line when wanted).</summary>
    private void WriteRaw(byte[] data, int offset, int count, string sender)
    {
        if (!_atLineStart && ShowSender && sender != _lastSender)
        {
            // Another device continues where the previous one stopped in the middle of a line.
            AddAscii("\r\n");
            _atLineStart = true;
        }
        for (int i = 0; i < count; i++)
        {
            byte b = data[offset + i];
            if (_atLineStart && b != (byte)'\r')
            {
                BeginLine(sender);
                _atLineStart = false;
            }
            _output.Add(b);
            if (b == (byte)'\n')
            {
                _atLineStart = true;
            }
        }
    }

    private void BeginLine(string sender)
    {
        _lastSender = sender;
        if (ShowSender && !string.IsNullOrEmpty(sender))
        {
            AddAscii(ColorizeLevels ? "\u001b[36m[" + sender + "]" + ResetColor + " " : "[" + sender + "] ");
        }
    }

    /// <summary>The colour of a kernel log level, as dmesg uses them; null for an ordinary message.</summary>
    internal static string LevelColor(int level)
    {
        switch (level)
        {
            case 0: // KERN_EMERG
            case 1: // KERN_ALERT
            case 2: // KERN_CRIT
                return "\u001b[1;31m";
            case 3: // KERN_ERR
                return "\u001b[31m";
            case 4: // KERN_WARNING
                return "\u001b[33m";
            case 5: // KERN_NOTICE
                return "\u001b[1m";
            case 7: // KERN_DEBUG
                return "\u001b[90m";
            default: // KERN_INFO
                return null;
        }
    }

    /// <summary>Restores the bytes the kernel wrote as \xNN (it escapes control characters, bytes from 127 up and '\').</summary>
    internal static byte[] Unescape(byte[] data, int offset, int count)
    {
        byte[] result = new byte[count];
        int length = 0;
        for (int i = 0; i < count; i++)
        {
            byte b = data[offset + i];
            if (b == (byte)'\\' && i + 3 < count && data[offset + i + 1] == (byte)'x'
                && TryHexDigit(data[offset + i + 2], out int high) && TryHexDigit(data[offset + i + 3], out int low))
            {
                result[length++] = (byte)(high * 16 + low);
                i += 3;
                continue;
            }
            result[length++] = b;
        }
        Array.Resize(ref result, length);
        return result;
    }

    private static bool TryHexDigit(byte b, out int value)
    {
        if (b >= (byte)'0' && b <= (byte)'9')
        {
            value = b - '0';
            return true;
        }
        if (b >= (byte)'a' && b <= (byte)'f')
        {
            value = b - 'a' + 10;
            return true;
        }
        if (b >= (byte)'A' && b <= (byte)'F')
        {
            value = b - 'A' + 10;
            return true;
        }
        value = 0;
        return false;
    }

    private void AddAscii(string text)
    {
        foreach (char c in text)
        {
            _output.Add((byte)c);
        }
    }
}
