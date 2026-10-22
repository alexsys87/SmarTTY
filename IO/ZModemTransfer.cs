using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SmarTTY.IO;

/// <summary>A file offered to a ZMODEM receiver.</summary>
public sealed class ZModemSendItem
{
    public ZModemSendItem(string name, Stream stream, long length, DateTime modifiedUtc)
    {
        Name = name;
        Stream = stream;
        Length = length;
        ModifiedUtc = modifiedUtc;
    }

    /// <summary>The name sent to the receiver (any directory part is dropped).</summary>
    public string Name { get; }

    /// <summary>The data; it must be seekable to recover from line errors (the receiver asks to resend from a position).</summary>
    public Stream Stream { get; }

    public long Length { get; }

    public DateTime ModifiedUtc { get; }
}

/// <summary>A file announced by a ZMODEM sender.</summary>
public sealed class ZModemFileInfo
{
    /// <summary>The name made safe to use as a local file name (no directories, no reserved characters).</summary>
    public string Name { get; internal set; }

    /// <summary>The name exactly as the sender wrote it; for display only.</summary>
    public string RemoteName { get; internal set; }

    /// <summary>The announced size, or -1 when the sender did not say.</summary>
    public long Length { get; internal set; } = -1;

    public DateTime? ModifiedUtc { get; internal set; }

    /// <summary>How many files the sender still has to send, this one included; 0 when unknown.</summary>
    public int FilesRemaining { get; internal set; }
}

public sealed class ZModemOptions
{
    /// <summary>Escape every control character (the ESCCTL option). Needed on lines that eat or alter control bytes.</summary>
    public bool EscapeControlCharacters { get; set; }

    /// <summary>Use 32-bit CRCs when the other side supports them. Turn off only to talk to very old programs.</summary>
    public bool UseCrc32 { get; set; } = true;

    /// <summary>
    /// Payload bytes per data subpacket (64..8192): the largest size the sender uses. On a noisy line the sender halves
    /// it after repeated errors and grows it back once the data gets through again.
    /// </summary>
    public int SubpacketBytes { get; set; } = 1024;

    /// <summary>
    /// The sender types "rz" + CR before its first frame, as lrzsz's sz does: a device waiting at a shell prompt starts its
    /// receiver by itself, and a receiver that is already running skips the text like any other noise.
    /// </summary>
    public bool SendRzCommand { get; set; } = true;
}

/// <summary>
/// ZMODEM (Chuck Forsberg's streaming protocol with crash-safe error recovery) over an arbitrary byte link: binary and
/// hex headers, 16- and 32-bit CRCs, ZDLE escaping, windowed streaming with acknowledgement requests, retransmission from
/// the position the receiver asks for, batches of files and cancelling with a burst of CAN bytes.
/// The link is a "send bytes" callback plus a "read one byte with a timeout" callback, like <see cref="XModemTransfer"/>,
/// so the protocol has no dependency on the terminal. The sender honours the receiver's buffer size (segmented
/// streaming with ZCRCW when the receiver cannot take a continuous stream), adapts the subpacket size to the line
/// quality and resumes from any position the receiver asks for (ZRPOS, ZCRC), so a receiver's crash recovery works.
/// The receiver keeps the sender's attention string (ZSINIT) and sends it before asking for a retransmission.
/// Not supported: ZCOMMAND (a sender must never be able to run commands here), compression, and crash recovery on the
/// receiving side (the receiver always takes files from the start).
/// </summary>
public static class ZModemTransfer
{
    public delegate void ProgressHandler(long bytesTransferred, long totalBytes, int errorCount, string status);
    public delegate void SendDataHandler(byte[] data, int offset, int length);

    // ---- wire constants ----
    public const byte ZPAD = 0x2A;  // '*'
    public const byte ZDLE = 0x18;  // also CAN
    public const byte XON = 0x11;
    public const byte XOFF = 0x13;

    private const byte FormatBin16 = 0x41; // 'A'
    private const byte FormatHex = 0x42;   // 'B'
    private const byte FormatBin32 = 0x43; // 'C'

    public const byte ZRQINIT = 0, ZRINIT = 1, ZSINIT = 2, ZACK = 3, ZFILE = 4, ZSKIP = 5, ZNAK = 6, ZABORT = 7, ZFIN = 8,
        ZRPOS = 9, ZDATA = 10, ZEOF = 11, ZFERR = 12, ZCRC = 13, ZCHALLENGE = 14, ZCOMPL = 15, ZCAN = 16, ZFREECNT = 17,
        ZCOMMAND = 18;

    // data subpacket terminators (the byte after ZDLE)
    public const byte ZCRCE = 0x68; // end of frame, a header follows
    public const byte ZCRCG = 0x69; // more data follows, no answer wanted
    public const byte ZCRCQ = 0x6A; // more data follows, an ACK is wanted
    public const byte ZCRCW = 0x6B; // end of frame, an ACK is wanted
    private const byte ZRUB0 = 0x6C; // escaped 0x7F
    private const byte ZRUB1 = 0x6D; // escaped 0xFF

    // ZRINIT capability flags (ZF0)
    private const byte CANFDX = 0x01, CANOVIO = 0x02, CANFC32 = 0x20, ESCCTL = 0x40;

    private const int MaxRetries = 10;
    private const int MaxDataErrors = 20;      // damaged subpackets in a row before a receiver gives up
    private const int MaxSubpacketBytes = 8192;
    private const int MinSubpacketBytes = 64;  // the smallest size the sender shrinks to on a noisy line
    private const int MaxPollGarbage = 4 * MaxSubpacketBytes; // bytes one look at the line may skip without a header
    private const int AckEverySubpackets = 4;
    private const int WindowAcks = 4; // the sender waits for an ACK once this many requests are unanswered

    /// <summary>The waiting times of the protocol; tests shorten them.</summary>
    internal sealed class Timeouts
    {
        /// <summary>How long a sender waits in total for the receiver's first ZRINIT.</summary>
        public int HandshakeMs = 90000;

        /// <summary>How often the sender repeats its ZRQINIT while waiting for the receiver.</summary>
        public int RqinitIntervalMs = 3000;

        /// <summary>How long a sender waits for the answer to ZFILE, ZEOF or an overdue ACK.</summary>
        public int ReplyMs = 10000;

        /// <summary>How long a receiver waits for the next header before it asks again.</summary>
        public int HeaderMs = 10000;

        /// <summary>The gap allowed between two bytes inside a header or a data subpacket.</summary>
        public int ByteMs = 3000;

        /// <summary>The time a trailing CR LF after a hex header is waited for.</summary>
        public int TrailerMs = 100;

        /// <summary>A repeated ZRQINIT closer than this to our last ZRINIT is the same request seen twice.</summary>
        public int DuplicateRequestMs = 1000;

        /// <summary>How long a receiver waits for the closing "OO" after ZFIN.</summary>
        public int OverAndOutMs = 500;

        /// <summary>
        /// A receiver that gets the end of a file at the wrong position repeats its request to go back when it made it at
        /// least this long ago: the sender has evidently not seen it.
        /// </summary>
        public int RepeatRequestMs = 1000;
    }

    // ================= CRC =================

    private static readonly ushort[] Crc16Table = BuildCrc16Table();
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static ushort[] BuildCrc16Table()
    {
        ushort[] table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int crc = i << 8;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
            }
            table[i] = (ushort)crc;
        }
        return table;
    }

    private static uint[] BuildCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            table[i] = crc;
        }
        return table;
    }

    /// <summary>CRC-16/XMODEM (polynomial 0x1021, start 0), the CRC of ZMODEM headers and 16-bit data.</summary>
    public static ushort Crc16(byte[] data, int offset, int length)
    {
        ushort crc = 0;
        for (int i = offset; i < offset + length; i++)
        {
            crc = Crc16Update(crc, data[i]);
        }
        return crc;
    }

    private static ushort Crc16Update(ushort crc, byte b)
    {
        return (ushort)((crc << 8) ^ Crc16Table[(crc >> 8) ^ b]);
    }

    /// <summary>The usual CRC-32 (zlib, PKZIP), the CRC of ZMODEM 32-bit frames; sent least significant byte first.</summary>
    public static uint Crc32(byte[] data, int offset, int length)
    {
        uint state = 0xFFFFFFFFu;
        for (int i = offset; i < offset + length; i++)
        {
            state = Crc32Update(state, data[i]);
        }
        return ~state;
    }

    private static uint Crc32Update(uint state, byte b)
    {
        return Crc32Table[(state ^ b) & 0xFF] ^ (state >> 8);
    }

    // ================= file names =================

    private static readonly string[] ReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Turns a name sent by the other side into a plain local file name: the directory part is dropped (both / and \),
    /// characters Windows does not allow become '_', trailing dots and spaces go, and DOS device names (CON, NUL, COM1...)
    /// are made harmless. A name that is empty afterwards becomes "file".
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "file";
        }
        int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0)
        {
            name = name.Substring(slash + 1);
        }
        StringBuilder sb = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            bool bad = ch < ' ' || ch == 0x7F || "<>:\"/\\|?*".IndexOf(ch) >= 0 || Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0;
            sb.Append(bad ? '_' : ch);
        }
        string result = sb.ToString().TrimEnd('.', ' ').TrimStart(' ');
        if (result.Length > 200)
        {
            result = result.Substring(0, 200).TrimEnd('.', ' ');
        }
        if (result.Length == 0)
        {
            return "file";
        }
        int dot = result.IndexOf('.');
        string stem = dot >= 0 ? result.Substring(0, dot) : result;
        foreach (string reserved in ReservedDeviceNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return "_" + result;
            }
        }
        return result;
    }

    // ================= the byte link =================

    private sealed class Link
    {
        private const int SliceMs = 200;

        private readonly SendDataHandler _send;
        private readonly Func<int, byte?> _read;
        private readonly CancellationToken _token;
        private readonly Stack<byte> _pushback = new Stack<byte>();

        public Link(SendDataHandler send, Func<int, byte?> read, CancellationToken token)
        {
            _send = send;
            _read = read;
            _token = token;
        }

        public bool IsCancelled => _token.IsCancellationRequested;

        public void Send(byte[] bytes, int length)
        {
            if (length > 0)
            {
                _send(bytes, 0, length);
            }
        }

        public void Send(params byte[] bytes)
        {
            Send(bytes, bytes.Length);
        }

        /// <summary>Tells the peer to give up: eight CAN (five in a row are enough) and backspaces to wipe them from a terminal.</summary>
        public void SendCancel()
        {
            try
            {
                Send(new byte[] { 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08 });
            }
            catch (Exception)
            {
                // A broken link must not hide the real error.
            }
        }

        public void Unread(byte b)
        {
            _pushback.Push(b);
        }

        /// <summary>The next byte (0..255), or -1 when nothing arrives within the time or the transfer is cancelled.</summary>
        public int Read(int timeoutMs)
        {
            if (_pushback.Count > 0)
            {
                return _pushback.Pop();
            }
            // Called for every byte of a transfer: the clock is a plain time stamp, not an allocated Stopwatch.
            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                if (_token.IsCancellationRequested)
                {
                    return -1;
                }
                int left = timeoutMs - ElapsedMs(start);
                byte? b = _read(Math.Max(0, Math.Min(SliceMs, left)));
                if (b.HasValue)
                {
                    return b.Value;
                }
                if (left <= SliceMs)
                {
                    return -1;
                }
            }
        }

        /// <summary>Drops whatever is on the line until it has been quiet for <paramref name="quietMs"/> (at most 5 s).</summary>
        public void Purge(int quietMs)
        {
            _pushback.Clear();
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 5000 && Read(quietMs) >= 0)
            {
            }
        }
    }

    private static int ElapsedMs(long start)
    {
        return (int)Math.Min(int.MaxValue, (Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency);
    }

    // ================= building frames =================

    /// <summary>Builds one frame in a buffer, escaping bytes the way the protocol demands.</summary>
    private sealed class Enc
    {
        public readonly byte[] Buf;
        public int Length;
        private readonly bool _escapeAll;
        private byte _last;

        public Enc(int capacity, bool escapeAll)
        {
            Buf = new byte[capacity];
            _escapeAll = escapeAll;
        }

        public void Clear()
        {
            Length = 0;
        }

        public void Raw(byte b)
        {
            Buf[Length++] = b;
            _last = b;
        }

        public void Esc(byte c)
        {
            switch (c)
            {
                case ZDLE:
                case 0x10:
                case 0x90:
                case XON:
                case 0x91:
                case XOFF:
                case 0x93:
                    Raw(ZDLE);
                    Raw((byte)(c ^ 0x40));
                    return;
                case 0x0D:
                case 0x8D:
                    // "@" CR is the start of an escape sequence on some networks, so a CR after '@' is always escaped.
                    if (_escapeAll || (_last & 0x7F) == 0x40)
                    {
                        Raw(ZDLE);
                        Raw((byte)(c ^ 0x40));
                        return;
                    }
                    Raw(c);
                    return;
                case 0x7F:
                    if (_escapeAll)
                    {
                        Raw(ZDLE);
                        Raw(ZRUB0);
                        return;
                    }
                    Raw(c);
                    return;
                case 0xFF:
                    if (_escapeAll)
                    {
                        Raw(ZDLE);
                        Raw(ZRUB1);
                        return;
                    }
                    Raw(c);
                    return;
                default:
                    if (_escapeAll && (c & 0x60) == 0)
                    {
                        Raw(ZDLE);
                        Raw((byte)(c ^ 0x40));
                        return;
                    }
                    Raw(c);
                    return;
            }
        }
    }

    private static byte[] PosBytes(long pos)
    {
        uint p = (uint)pos;
        return new[] { (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24) };
    }

    /// <summary>The four header bytes for a flags header: ZF0 is the LAST byte on the wire.</summary>
    private static byte[] FlagBytes(byte f0, byte f1 = 0, byte f2 = 0, byte f3 = 0)
    {
        return new[] { f3, f2, f1, f0 };
    }

    private static readonly char[] HexDigits = "0123456789abcdef".ToCharArray();

    /// <summary>A hex header: printable, so it survives 7-bit lines and is recognized even while a terminal is echoing.</summary>
    internal static byte[] BuildHexHeader(byte type, byte[] p)
    {
        List<byte> frame = new List<byte> { ZPAD, ZPAD, ZDLE, FormatHex };
        byte[] body = { type, p[0], p[1], p[2], p[3], 0, 0 };
        ushort crc = Crc16(body, 0, 5);
        body[5] = (byte)(crc >> 8);
        body[6] = (byte)crc;
        foreach (byte b in body)
        {
            frame.Add((byte)HexDigits[b >> 4]);
            frame.Add((byte)HexDigits[b & 0x0F]);
        }
        frame.Add(0x0D);
        frame.Add(0x8A);
        if (type != ZACK && type != ZFIN)
        {
            frame.Add(XON); // lets a receiver whose flow control got stuck by a lost XON carry on
        }
        return frame.ToArray();
    }

    private static void BuildBinaryHeader(Enc enc, byte type, byte[] p, bool crc32)
    {
        enc.Clear();
        enc.Raw(ZPAD);
        enc.Raw(ZDLE);
        enc.Raw(crc32 ? FormatBin32 : FormatBin16);
        byte[] body = { type, p[0], p[1], p[2], p[3] };
        foreach (byte b in body)
        {
            enc.Esc(b);
        }
        if (crc32)
        {
            uint crc = Crc32(body, 0, 5);
            for (int i = 0; i < 4; i++)
            {
                enc.Esc((byte)(crc >> (8 * i)));
            }
        }
        else
        {
            ushort crc = Crc16(body, 0, 5);
            enc.Esc((byte)(crc >> 8));
            enc.Esc((byte)crc);
        }
    }

    private static void BuildSubpacket(Enc enc, byte[] data, int count, byte terminator, bool crc32)
    {
        enc.Clear();
        for (int i = 0; i < count; i++)
        {
            enc.Esc(data[i]);
        }
        enc.Raw(ZDLE);
        enc.Raw(terminator);
        if (crc32)
        {
            uint state = 0xFFFFFFFFu;
            for (int i = 0; i < count; i++)
            {
                state = Crc32Update(state, data[i]);
            }
            state = Crc32Update(state, terminator);
            uint crc = ~state;
            for (int i = 0; i < 4; i++)
            {
                enc.Esc((byte)(crc >> (8 * i)));
            }
        }
        else
        {
            ushort crc = 0;
            for (int i = 0; i < count; i++)
            {
                crc = Crc16Update(crc, data[i]);
            }
            crc = Crc16Update(crc, terminator);
            enc.Esc((byte)(crc >> 8));
            enc.Esc((byte)crc);
        }
        if (terminator == ZCRCW)
        {
            enc.Raw(XON);
        }
    }

    // ================= reading frames =================

    private struct Header
    {
        public byte Type;
        public byte P0, P1, P2, P3;
        public bool Crc32;

        /// <summary>The header bytes read as a little-endian position (or any 32-bit number).</summary>
        public long Pos => (uint)(P0 | (P1 << 8) | (P2 << 16) | (P3 << 24));

        /// <summary>For flag headers: ZF0 is the last of the four bytes.</summary>
        public byte F0 => P3;
    }

    private enum HeaderStatus
    {
        Ok,
        Timeout,
        Cancelled,
        Bad
    }

    private const int ReadTimeout = -1;
    private const int ReadCancelled = -2;
    private const int ReadBadEscape = -3;
    private const int TerminatorFlag = 0x100;

    /// <summary>
    /// Reads one byte of a binary frame with ZDLE decoding: a data byte (0..255), a subpacket terminator
    /// (<see cref="TerminatorFlag"/> | ZCRCx), or one of the negative codes. Raw XON and XOFF are flow control and are skipped.
    /// </summary>
    private static int ReadEncoded(Link link, int timeoutMs)
    {
        int c;
        do
        {
            c = link.Read(timeoutMs);
            if (c < 0)
            {
                return ReadTimeout;
            }
        }
        while (c == XON || c == XOFF || c == (XON | 0x80) || c == (XOFF | 0x80));

        if (c != ZDLE)
        {
            return c;
        }
        int cans = 1;
        while (true)
        {
            c = link.Read(timeoutMs);
            if (c < 0)
            {
                return ReadTimeout;
            }
            switch (c)
            {
                case ZDLE:
                    if (++cans >= 5)
                    {
                        return ReadCancelled; // five CAN in a row: the other side aborts
                    }
                    continue;
                case ZCRCE:
                case ZCRCG:
                case ZCRCQ:
                case ZCRCW:
                    return TerminatorFlag | c;
                case ZRUB0:
                    return 0x7F;
                case ZRUB1:
                    return 0xFF;
                case XON:
                case XOFF:
                case XON | 0x80:
                case XOFF | 0x80:
                    continue; // flow control inside an escape sequence
            }
            if ((c & 0x60) == 0x40)
            {
                return c ^ 0x40;
            }
            return ReadBadEscape;
        }
    }

    private static int HexValue(int c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the start of a header (ZPAD ZDLE format) and reads it. Everything else
    /// on the line - echo, banners, the tail of an abandoned data stream - is skipped, but only until the time is up: a
    /// peer that keeps streaming must not keep this side from answering. A timeout of 0 only looks at what has already
    /// arrived (and at most <see cref="MaxPollGarbage"/> bytes of it).
    /// </summary>
    private static HeaderStatus ReadHeader(Link link, int timeoutMs, int byteMs, int trailerMs, out Header header)
    {
        header = default;
        long start = Stopwatch.GetTimestamp();
        int cans = 0;
        int skipped = 0;
        while (true)
        {
            int left = Math.Max(0, timeoutMs - ElapsedMs(start));
            if (timeoutMs > 0 ? left == 0 && skipped > 0 : skipped >= MaxPollGarbage)
            {
                return HeaderStatus.Timeout;
            }
            int c = link.Read(left);
            if (c < 0)
            {
                return HeaderStatus.Timeout;
            }
            skipped++;
            if (c == ZDLE)
            {
                if (++cans >= 5)
                {
                    return HeaderStatus.Cancelled;
                }
                continue;
            }
            cans = 0;
            if (c != ZPAD)
            {
                continue;
            }

            c = link.Read(byteMs);
            while (c == ZPAD)
            {
                c = link.Read(byteMs);
            }
            if (c < 0)
            {
                return HeaderStatus.Timeout;
            }
            if (c != ZDLE)
            {
                link.Unread((byte)c);
                continue;
            }
            int format = link.Read(byteMs);
            if (format < 0)
            {
                return HeaderStatus.Timeout;
            }
            switch (format)
            {
                case FormatHex:
                    return ParseHexHeader(link, byteMs, trailerMs, out header);
                case FormatBin16:
                    return ParseBinaryHeader(link, byteMs, false, out header);
                case FormatBin32:
                    return ParseBinaryHeader(link, byteMs, true, out header);
                default:
                    link.Unread((byte)format);
                    continue;
            }
        }
    }

    private static HeaderStatus ParseHexHeader(Link link, int byteMs, int trailerMs, out Header header)
    {
        header = default;
        byte[] v = new byte[7];
        for (int i = 0; i < 7; i++)
        {
            int hi = HexValue(link.Read(byteMs));
            int lo = HexValue(link.Read(byteMs));
            if (hi < 0 || lo < 0)
            {
                return link.IsCancelled ? HeaderStatus.Timeout : HeaderStatus.Bad;
            }
            v[i] = (byte)((hi << 4) | lo);
        }
        if (Crc16(v, 0, 5) != (ushort)((v[5] << 8) | v[6]))
        {
            return HeaderStatus.Bad;
        }
        header = new Header { Type = v[0], P0 = v[1], P1 = v[2], P2 = v[3], P3 = v[4], Crc32 = false };

        // A hex header ends with CR LF (and XON); eat them so they are not taken for data when a subpacket follows.
        int t = link.Read(trailerMs);
        if (t == 0x0D || t == 0x8D)
        {
            int t2 = link.Read(trailerMs);
            if (t2 >= 0 && t2 != 0x0A && t2 != 0x8A)
            {
                link.Unread((byte)t2);
            }
        }
        else if (t >= 0)
        {
            link.Unread((byte)t);
        }
        return HeaderStatus.Ok;
    }

    private static HeaderStatus ParseBinaryHeader(Link link, int byteMs, bool crc32, out Header header)
    {
        header = default;
        byte[] v = new byte[5];
        for (int i = 0; i < 5; i++)
        {
            int c = ReadEncoded(link, byteMs);
            if (c == ReadCancelled)
            {
                return HeaderStatus.Cancelled;
            }
            if (c < 0)
            {
                return link.IsCancelled || c == ReadTimeout ? HeaderStatus.Timeout : HeaderStatus.Bad;
            }
            if (c >= TerminatorFlag)
            {
                return HeaderStatus.Bad;
            }
            v[i] = (byte)c;
        }
        int crcBytes = crc32 ? 4 : 2;
        uint received = 0;
        for (int i = 0; i < crcBytes; i++)
        {
            int c = ReadEncoded(link, byteMs);
            if (c == ReadCancelled)
            {
                return HeaderStatus.Cancelled;
            }
            if (c < 0)
            {
                return c == ReadTimeout ? HeaderStatus.Timeout : HeaderStatus.Bad;
            }
            if (c >= TerminatorFlag)
            {
                return HeaderStatus.Bad;
            }
            received = crc32 ? received | ((uint)c << (8 * i)) : (received << 8) | (uint)c;
        }
        uint expected = crc32 ? Crc32(v, 0, 5) : Crc16(v, 0, 5);
        if (received != expected)
        {
            return HeaderStatus.Bad;
        }
        header = new Header { Type = v[0], P0 = v[1], P1 = v[2], P2 = v[3], P3 = v[4], Crc32 = crc32 };
        return HeaderStatus.Ok;
    }

    private enum DataStatus
    {
        Ok,
        Timeout,
        Cancelled,
        Bad
    }

    /// <summary>Reads one data subpacket: the escaped bytes up to ZDLE + terminator, then its CRC.</summary>
    private static DataStatus ReadSubpacket(Link link, bool crc32, byte[] buffer, int byteMs, out int length, out byte terminator)
    {
        length = 0;
        terminator = 0;
        while (true)
        {
            int c = ReadEncoded(link, byteMs);
            if (c == ReadTimeout)
            {
                return DataStatus.Timeout;
            }
            if (c == ReadCancelled)
            {
                return DataStatus.Cancelled;
            }
            if (c < 0)
            {
                return DataStatus.Bad;
            }
            if (c >= TerminatorFlag)
            {
                terminator = (byte)c;
                break;
            }
            if (length >= buffer.Length)
            {
                return DataStatus.Bad;
            }
            buffer[length++] = (byte)c;
        }

        uint expected;
        if (crc32)
        {
            uint state = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++)
            {
                state = Crc32Update(state, buffer[i]);
            }
            expected = ~Crc32Update(state, terminator);
        }
        else
        {
            ushort crc = 0;
            for (int i = 0; i < length; i++)
            {
                crc = Crc16Update(crc, buffer[i]);
            }
            expected = Crc16Update(crc, terminator);
        }

        uint received = 0;
        int crcBytes = crc32 ? 4 : 2;
        for (int i = 0; i < crcBytes; i++)
        {
            int c = ReadEncoded(link, byteMs);
            if (c == ReadTimeout)
            {
                return DataStatus.Timeout;
            }
            if (c == ReadCancelled)
            {
                return DataStatus.Cancelled;
            }
            if (c < 0 || c >= TerminatorFlag)
            {
                return DataStatus.Bad;
            }
            received = crc32 ? received | ((uint)c << (8 * i)) : (received << 8) | (uint)c;
        }
        return received == expected ? DataStatus.Ok : DataStatus.Bad;
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, total, count - total);
            if (n <= 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    private static int ClampSubpacket(ZModemOptions options)
    {
        int size = options.SubpacketBytes;
        if (size < 64) return 64;
        if (size > MaxSubpacketBytes) return MaxSubpacketBytes;
        return size;
    }

    // ================= public entry points =================

    /// <summary>
    /// Sends one or more files. Progress is reported for the file being sent. Returns true when the receiver acknowledged
    /// every file (a file the receiver chose to skip counts as handled) and closed the session.
    /// </summary>
    public static bool SendFiles(
        IList<ZModemSendItem> files,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress,
        ZModemOptions options = null)
    {
        return SendFiles(files, sendData, readByteWithTimeout, cancellationToken, onProgress, options, new Timeouts());
    }

    internal static bool SendFiles(
        IList<ZModemSendItem> files,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress,
        ZModemOptions options,
        Timeouts timeouts)
    {
        if (files == null) throw new ArgumentNullException(nameof(files));
        if (files.Count == 0) throw new ArgumentException("There is nothing to send.", nameof(files));
        if (sendData == null) throw new ArgumentNullException(nameof(sendData));
        if (readByteWithTimeout == null) throw new ArgumentNullException(nameof(readByteWithTimeout));

        Sender sender = new Sender(
            new Link(sendData, readByteWithTimeout, cancellationToken),
            options ?? new ZModemOptions(),
            timeouts ?? new Timeouts(),
            onProgress);
        return sender.Run(files);
    }

    /// <summary>
    /// Receives the files a sender offers. <paramref name="openFile"/> is asked for each announced file and returns the stream
    /// to write it to, or null to skip that file; the receiver closes every stream it was given.
    /// <paramref name="fileFinished"/> (optional) is called after the stream was closed, with false when the file is
    /// incomplete (the transfer failed or was cancelled), so the caller can apply the time stamp or delete the remains.
    /// Returns true when the sender finished the session normally.
    /// </summary>
    public static bool ReceiveFiles(
        Func<ZModemFileInfo, Stream> openFile,
        Action<ZModemFileInfo, bool> fileFinished,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress,
        ZModemOptions options = null)
    {
        return ReceiveFiles(openFile, fileFinished, sendData, readByteWithTimeout, cancellationToken, onProgress, options, new Timeouts());
    }

    internal static bool ReceiveFiles(
        Func<ZModemFileInfo, Stream> openFile,
        Action<ZModemFileInfo, bool> fileFinished,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress,
        ZModemOptions options,
        Timeouts timeouts)
    {
        if (openFile == null) throw new ArgumentNullException(nameof(openFile));
        if (sendData == null) throw new ArgumentNullException(nameof(sendData));
        if (readByteWithTimeout == null) throw new ArgumentNullException(nameof(readByteWithTimeout));

        Receiver receiver = new Receiver(
            new Link(sendData, readByteWithTimeout, cancellationToken),
            options ?? new ZModemOptions(),
            timeouts ?? new Timeouts(),
            openFile,
            fileFinished,
            onProgress);
        try
        {
            return receiver.Run();
        }
        catch (Exception)
        {
            // A write to the line failed (the connection is gone): the file in progress must not stay open or look complete.
            receiver.Abandon();
            throw;
        }
    }

    // ================= sender =================

    private sealed class Sender
    {
        private readonly Link _link;
        private readonly ZModemOptions _options;
        private readonly Timeouts _t;
        private readonly ProgressHandler _progress;
        private Enc _enc;
        private bool _crc32;
        private int _rxBufferLength;  // from ZRINIT: the receiver takes at most this much before a ZCRCW (0 = no limit)
        private bool _rxCanStream;    // from ZRINIT: full duplex and overlapped I/O, so a continuous stream is fine
        private int _errors;
        private string _fileLabel = string.Empty;
        private long _total;

        private enum FileResult
        {
            Sent,
            Skipped,
            Failed
        }

        public Sender(Link link, ZModemOptions options, Timeouts timeouts, ProgressHandler progress)
        {
            _link = link;
            _options = options;
            _t = timeouts;
            _progress = progress;
        }

        private void Report(long done, string status)
        {
            _progress?.Invoke(done, _total, _errors, status);
        }

        private bool Cancelled()
        {
            if (!_link.IsCancelled)
            {
                return false;
            }
            _link.SendCancel();
            Report(0, "Transfer cancelled.");
            return true;
        }

        private void SendHex(byte type, byte[] p)
        {
            byte[] frame = BuildHexHeader(type, p);
            _link.Send(frame, frame.Length);
        }

        private void SendBinary(byte type, byte[] p)
        {
            BuildBinaryHeader(_enc, type, p, _crc32);
            _link.Send(_enc.Buf, _enc.Length);
        }

        private void SendData(byte[] data, int count, byte terminator)
        {
            BuildSubpacket(_enc, data, count, terminator, _crc32);
            _link.Send(_enc.Buf, _enc.Length);
        }

        public bool Run(IList<ZModemSendItem> files)
        {
            _enc = new Enc(2 * MaxSubpacketBytes + 64, _options.EscapeControlCharacters);
            _total = 0;
            if (!Handshake())
            {
                return false;
            }

            long bytesLeft = 0;
            foreach (ZModemSendItem item in files)
            {
                bytesLeft += Math.Max(0, item.Length);
            }
            for (int i = 0; i < files.Count; i++)
            {
                ZModemSendItem item = files[i];
                _total = item.Length;
                _fileLabel = Path.GetFileName(item.Name ?? string.Empty);
                if (string.IsNullOrEmpty(_fileLabel))
                {
                    _fileLabel = "file";
                }
                if (files.Count > 1)
                {
                    _fileLabel += " (" + (i + 1) + " of " + files.Count + ")";
                }
                FileResult result = SendOne(item, files.Count - i, bytesLeft);
                if (result == FileResult.Failed)
                {
                    return false;
                }
                bytesLeft -= Math.Max(0, item.Length);
            }
            return Finish();
        }

        private bool Handshake()
        {
            Report(0, "Waiting for the receiver...");
            Stopwatch total = Stopwatch.StartNew();
            if (_options.SendRzCommand)
            {
                _link.Send(Encoding.ASCII.GetBytes("rz\r"));
            }
            SendHex(ZRQINIT, new byte[4]);
            while (total.ElapsedMilliseconds < _t.HandshakeMs)
            {
                HeaderStatus status = ReadHeader(_link, _t.RqinitIntervalMs, _t.ByteMs, _t.TrailerMs, out Header h);
                if (Cancelled())
                {
                    return false;
                }
                switch (status)
                {
                    case HeaderStatus.Cancelled:
                        Report(0, "Cancelled by the receiver.");
                        return false;
                    case HeaderStatus.Timeout:
                        SendHex(ZRQINIT, new byte[4]);
                        continue;
                    case HeaderStatus.Bad:
                        continue;
                }
                if (h.Type == ZRINIT)
                {
                    _crc32 = _options.UseCrc32 && (h.F0 & CANFC32) != 0;
                    _rxBufferLength = h.P0 | (h.P1 << 8);
                    _rxCanStream = (h.F0 & (CANFDX | CANOVIO)) == (CANFDX | CANOVIO);
                    if ((h.F0 & ESCCTL) != 0 && !_options.EscapeControlCharacters)
                    {
                        _enc = new Enc(2 * MaxSubpacketBytes + 64, true);
                    }
                    return true;
                }
                if (h.Type == ZCHALLENGE)
                {
                    SendHex(ZACK, PosBytes(h.Pos)); // the receiver wants its random number echoed
                }
                else if (h.Type == ZCAN || h.Type == ZABORT)
                {
                    Report(0, "Cancelled by the receiver.");
                    return false;
                }
            }
            _errors++;
            Report(0, "Handshake timeout: the receiver did not answer (start a ZMODEM receive, such as rz, on the device).");
            return false;
        }

        private byte[] BuildFileInfo(ZModemSendItem item, int filesLeft, long bytesLeft)
        {
            string name = Path.GetFileName((item.Name ?? string.Empty).Replace('\\', '/'));
            if (string.IsNullOrEmpty(name))
            {
                name = "file";
            }
            long unixTime = (long)Math.Max(0, (item.ModifiedUtc.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            string rest = item.Length.ToString(CultureInfo.InvariantCulture) + " " + Convert.ToString(unixTime, 8) + " 100644 0 "
                + filesLeft.ToString(CultureInfo.InvariantCulture) + " " + bytesLeft.ToString(CultureInfo.InvariantCulture);
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            byte[] restBytes = Encoding.ASCII.GetBytes(rest);
            byte[] result = new byte[nameBytes.Length + 1 + restBytes.Length + 1];
            Buffer.BlockCopy(nameBytes, 0, result, 0, nameBytes.Length);
            Buffer.BlockCopy(restBytes, 0, result, nameBytes.Length + 1, restBytes.Length);
            return result;
        }

        private FileResult SendOne(ZModemSendItem item, int filesLeft, long bytesLeft)
        {
            byte[] info = BuildFileInfo(item, filesLeft, bytesLeft);
            Report(0, "Offering " + _fileLabel + " (" + FormatBytes(Math.Max(0, item.Length)) + ")");
            int attempts = 0;
            bool send = true;
            while (true)
            {
                if (Cancelled())
                {
                    return FileResult.Failed;
                }
                if (send)
                {
                    SendBinary(ZFILE, FlagBytes(1)); // ZF0 = ZCBIN: binary transfer
                    SendData(info, info.Length, ZCRCW);
                    send = false;
                }
                HeaderStatus status = ReadHeader(_link, _t.ReplyMs, _t.ByteMs, _t.TrailerMs, out Header h);
                if (Cancelled())
                {
                    return FileResult.Failed;
                }
                if (status == HeaderStatus.Cancelled)
                {
                    Report(0, "Cancelled by the receiver.");
                    return FileResult.Failed;
                }
                if (status != HeaderStatus.Ok)
                {
                    _errors++;
                    if (++attempts >= MaxRetries)
                    {
                        _link.SendCancel();
                        Report(0, "Transfer failed: the receiver did not answer the file offer.");
                        return FileResult.Failed;
                    }
                    send = true;
                    continue;
                }
                switch (h.Type)
                {
                    case ZRPOS:
                        if (h.Pos > Math.Max(0, item.Length))
                        {
                            _link.SendCancel();
                            Report(0, "Transfer failed: the receiver asked for data beyond the end of the file.");
                            return FileResult.Failed;
                        }
                        return StreamFile(item, h.Pos);
                    case ZSKIP:
                        Report(0, "Skipped by the receiver: " + _fileLabel);
                        return FileResult.Skipped;
                    case ZCRC:
                        if (!AnswerCrcRequest(item, h.Pos))
                        {
                            _link.SendCancel();
                            Report(0, "Transfer failed: cannot compute the checksum of the file.");
                            return FileResult.Failed;
                        }
                        continue;
                    case ZNAK:
                        _errors++;
                        if (++attempts >= MaxRetries)
                        {
                            _link.SendCancel();
                            Report(0, "Transfer failed: the receiver keeps refusing the file offer.");
                            return FileResult.Failed;
                        }
                        send = true;
                        continue;
                    case ZABORT:
                    case ZFERR:
                    case ZCAN:
                        Report(0, "Cancelled by the receiver.");
                        return FileResult.Failed;
                    case ZCHALLENGE:
                        SendHex(ZACK, PosBytes(h.Pos));
                        continue;
                    case ZRINIT:
                        // The receiver is still waiting for a file: our offer was lost or damaged. A copy of its answer
                        // to our ZRQINIT may arrive here too; then the offer goes twice, which a receiver takes in its stride.
                        _errors++;
                        if (++attempts >= MaxRetries)
                        {
                            _link.SendCancel();
                            Report(0, "Transfer failed: the receiver did not take the file offer.");
                            return FileResult.Failed;
                        }
                        send = true;
                        continue;
                    default:
                        continue; // noise: the answer is still to come
                }
            }
        }

        /// <summary>ZCRC: the receiver wants the CRC-32 of the first N bytes (all when N is 0), to decide whether to resume.</summary>
        private bool AnswerCrcRequest(ZModemSendItem item, long count)
        {
            if (!item.Stream.CanSeek)
            {
                return false;
            }
            try
            {
                long limit = count == 0 || count > item.Length ? item.Length : count;
                long saved = item.Stream.Position;
                item.Stream.Seek(0, SeekOrigin.Begin);
                uint state = 0xFFFFFFFFu;
                byte[] buffer = new byte[4096];
                long left = limit;
                while (left > 0)
                {
                    int n = item.Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n <= 0)
                    {
                        break;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        state = Crc32Update(state, buffer[i]);
                    }
                    left -= n;
                }
                item.Stream.Seek(saved, SeekOrigin.Begin);
                SendHex(ZCRC, PosBytes(~state));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private enum PollResult
        {
            Nothing,
            Restart,
            Skip,
            Timeout,
            Damaged,
            Failed
        }

        /// <summary>
        /// Looks at the answers that arrived while data was streaming: a ZACK moves the acknowledged position, a ZRPOS means
        /// "go back to this position" (Restart, with <paramref name="position"/> set to it), ZSKIP drops the rest of the
        /// file, a cancel ends the transfer. Without <paramref name="wait"/> it never waits; with it, it returns on the
        /// first ZACK, or Timeout when the receiver stays silent. Damaged means a header arrived damaged: most likely the
        /// receiver's request to go back, whose position is lost with it.
        /// </summary>
        private PollResult Poll(ref long position, ref long acked, long length, bool wait)
        {
            while (true)
            {
                HeaderStatus status = ReadHeader(_link, wait ? _t.ReplyMs : 0, _t.ByteMs, _t.TrailerMs, out Header h);
                if (Cancelled())
                {
                    return PollResult.Failed; // the peer is told to stop too
                }
                if (status == HeaderStatus.Cancelled)
                {
                    Report(position, "Cancelled by the receiver.");
                    return PollResult.Failed;
                }
                if (status == HeaderStatus.Timeout)
                {
                    return wait ? PollResult.Timeout : PollResult.Nothing;
                }
                if (status == HeaderStatus.Bad)
                {
                    _errors++;
                    return PollResult.Damaged;
                }
                switch (h.Type)
                {
                    case ZACK:
                        if (h.Pos > acked && h.Pos <= position)
                        {
                            acked = h.Pos;
                        }
                        if (wait)
                        {
                            return PollResult.Nothing;
                        }
                        continue;
                    case ZRPOS:
                        if (h.Pos > length)
                        {
                            _link.SendCancel();
                            Report(position, "Transfer failed: the receiver asked for data beyond the end of the file.");
                            return PollResult.Failed;
                        }
                        _errors++;
                        position = h.Pos;
                        acked = h.Pos;
                        return PollResult.Restart;
                    case ZABORT:
                    case ZFERR:
                    case ZCAN:
                        Report(position, "Cancelled by the receiver.");
                        return PollResult.Failed;
                    case ZSKIP:
                        return PollResult.Skip;
                    case ZNAK:
                        // The receiver could not read our ZDATA header: start the frame again.
                        _errors++;
                        return PollResult.Damaged;
                    default:
                        continue;
                }
            }
        }

        /// <summary>
        /// Sends the data of a file from <paramref name="startPosition"/> and its ZEOF, answering the receiver's requests to
        /// go back. A receiver that announced a buffer size (or cannot take a continuous stream) gets the data in segments
        /// that each end with ZCRCW and wait for its ZACK; otherwise the data streams with an ACK request (ZCRCQ) every few
        /// subpackets and the sender never runs more than a window ahead of the acknowledged position.
        /// </summary>
        private FileResult StreamFile(ZModemSendItem item, long startPosition)
        {
            long length = Math.Max(0, item.Length);
            int maxSubpacket = ClampSubpacket(_options);
            if (_rxBufferLength > 0)
            {
                maxSubpacket = Math.Max(MinSubpacketBytes, Math.Min(maxSubpacket, _rxBufferLength));
            }
            // Bytes per segment that ends with ZCRCW; 0 = one continuous frame.
            int segment = _rxBufferLength > 0 ? Math.Max(_rxBufferLength, MinSubpacketBytes) : _rxCanStream ? 0 : maxSubpacket;
            long window = (long)WindowAcks * AckEverySubpackets * maxSubpacket;
            int subpacket = maxSubpacket;
            byte[] chunk = new byte[maxSubpacket];
            long position = startPosition;
            long acked = startPosition;
            int silentWaits = 0;      // waits for an acknowledgement that ended in silence, in a row
            int errorsInRow = 0;      // retransmission requests since the data last got through
            long lastRetransmit = -1; // the position of the last retransmission request
            int sameRetransmits = 0;  // requests for that same position in a row
            long goodBytes = 0;       // sent since the last request, for growing the subpackets again
            // Where the source stream is now; a stream that cannot seek has to be at the start position already.
            long streamPosition = item.Stream.CanSeek ? item.Stream.Position : startPosition;

            // A retransmission request (or silence) moved the position back: false when the transfer has to stop.
            bool OnRetransmit(bool silence)
            {
                goodBytes = 0;
                if (silence)
                {
                    _errors++;
                    if (++silentWaits > MaxRetries)
                    {
                        _link.SendCancel();
                        Report(position, "Transfer failed: the receiver stopped acknowledging the data.");
                        return false;
                    }
                }
                else
                {
                    silentWaits = 0;
                    sameRetransmits = position == lastRetransmit ? sameRetransmits + 1 : 0;
                    lastRetransmit = position;
                    if (sameRetransmits > MaxDataErrors)
                    {
                        _link.SendCancel();
                        Report(position, "Transfer failed: the receiver keeps asking for the data at " + FormatBytes(position) + ".");
                        return false;
                    }
                }
                // A noisy line: smaller subpackets lose less to each damaged byte.
                if (++errorsInRow >= 2 && subpacket > MinSubpacketBytes)
                {
                    subpacket = Math.Max(MinSubpacketBytes, subpacket / 2);
                    errorsInRow = 0;
                }
                Report(position, "Retransmitting " + _fileLabel + " from " + FormatBytes(position));
                return true;
            }

            while (true) // one pass per ZDATA frame
            {
                if (position != streamPosition)
                {
                    if (!item.Stream.CanSeek)
                    {
                        _link.SendCancel();
                        Report(position, "Transfer failed: the receiver asked to repeat data and the source cannot seek.");
                        return FileResult.Failed;
                    }
                    try
                    {
                        item.Stream.Seek(position, SeekOrigin.Begin);
                        streamPosition = position;
                    }
                    catch (Exception ex)
                    {
                        _link.SendCancel();
                        Report(position, "Cannot read the file: " + ex.Message);
                        return FileResult.Failed;
                    }
                }
                acked = position;
                SendBinary(ZDATA, PosBytes(position));
                long frameStart = position;
                int sinceAckRequest = 0;
                bool endOfData = false;

                while (true) // the subpackets of this frame
                {
                    if (Cancelled())
                    {
                        return FileResult.Failed;
                    }
                    int want = subpacket;
                    if (segment > 0)
                    {
                        want = (int)Math.Min(want, segment - (position - frameStart));
                    }
                    int n;
                    try
                    {
                        n = ReadFully(item.Stream, chunk, want);
                        streamPosition += n;
                    }
                    catch (Exception ex)
                    {
                        _link.SendCancel();
                        Report(position, "Cannot read the file: " + ex.Message);
                        return FileResult.Failed;
                    }
                    endOfData = n < want || position + n >= length;
                    byte terminator;
                    if (endOfData)
                    {
                        terminator = ZCRCE;
                    }
                    else if (segment > 0 && position + n - frameStart >= segment)
                    {
                        terminator = ZCRCW;
                    }
                    else if (segment == 0 && ++sinceAckRequest >= AckEverySubpackets)
                    {
                        sinceAckRequest = 0;
                        terminator = ZCRCQ;
                    }
                    else
                    {
                        terminator = ZCRCG;
                    }
                    SendData(chunk, n, terminator);
                    position += n;
                    goodBytes += n;
                    if (goodBytes >= 8L * subpacket)
                    {
                        // The line has been good for a while: back towards the configured size.
                        errorsInRow = 0;
                        goodBytes = 0;
                        subpacket = Math.Min(maxSubpacket, subpacket * 2);
                    }
                    Report(position, "Sending " + _fileLabel + ": " + FormatBytes(position) + " of " + FormatBytes(length));
                    if (endOfData)
                    {
                        break;
                    }

                    PollResult poll;
                    if (terminator == ZCRCW)
                    {
                        // The receiver's buffer is full: wait until it says it has written this segment.
                        do
                        {
                            poll = Poll(ref position, ref acked, length, wait: true);
                        }
                        while (poll == PollResult.Nothing && acked < position);
                        if (poll == PollResult.Nothing)
                        {
                            silentWaits = 0;
                            break; // acknowledged: the next segment is a new frame
                        }
                    }
                    else
                    {
                        poll = Poll(ref position, ref acked, length, wait: false);
                        // Too many unanswered requests: do not run ahead of a receiver that is slow or whose ACKs are lost.
                        while (poll == PollResult.Nothing && segment == 0 && position - acked >= window)
                        {
                            poll = Poll(ref position, ref acked, length, wait: true);
                            if (poll == PollResult.Nothing)
                            {
                                silentWaits = 0;
                            }
                        }
                        if (poll == PollResult.Nothing)
                        {
                            continue;
                        }
                    }

                    if (poll == PollResult.Failed)
                    {
                        return FileResult.Failed;
                    }
                    if (poll == PollResult.Skip)
                    {
                        Report(position, "Skipped by the receiver: " + _fileLabel);
                        return FileResult.Skipped;
                    }
                    if (poll == PollResult.Timeout || poll == PollResult.Damaged)
                    {
                        // An answer was lost or damaged (often a request to go back): start a new frame from what the
                        // receiver has confirmed, rather than stream on blindly; a receiver that has more says so with ZRPOS.
                        position = acked;
                    }
                    if (!OnRetransmit(poll == PollResult.Timeout))
                    {
                        return FileResult.Failed;
                    }
                    break;
                }
                if (!endOfData)
                {
                    continue; // a new frame: after a ZCRCW segment or from the position the receiver asked for
                }

                // The frame ended: announce the end of the file and wait for the receiver to take it or ask again.
                int tries = 0;
                bool again = false;
                SendBinary(ZEOF, PosBytes(position));
                while (!again)
                {
                    HeaderStatus status = ReadHeader(_link, _t.ReplyMs, _t.ByteMs, _t.TrailerMs, out Header h);
                    if (Cancelled())
                    {
                        return FileResult.Failed;
                    }
                    if (status == HeaderStatus.Cancelled)
                    {
                        Report(position, "Cancelled by the receiver.");
                        return FileResult.Failed;
                    }
                    if (status != HeaderStatus.Ok || h.Type == ZNAK)
                    {
                        _errors++;
                        if (++tries >= MaxRetries)
                        {
                            _link.SendCancel();
                            Report(position, "Transfer failed: the end of the file was not acknowledged.");
                            return FileResult.Failed;
                        }
                        if (status == HeaderStatus.Timeout && tries % 2 == 0 && acked < position)
                        {
                            // Silence twice in a row: the receiver may have lost the start of the data and ignore an
                            // end-of-file at a position it has not reached (lrzsz rz does, until its own timeout). A new
                            // frame from the confirmed position makes it answer at once.
                            position = acked;
                            if (!OnRetransmit(true))
                            {
                                return FileResult.Failed;
                            }
                            again = true;
                            continue;
                        }
                        SendBinary(ZEOF, PosBytes(position));
                        continue;
                    }
                    switch (h.Type)
                    {
                        case ZRINIT:
                            Report(position, "Sent " + _fileLabel);
                            return FileResult.Sent;
                        case ZSKIP:
                            Report(position, "Skipped by the receiver: " + _fileLabel);
                            return FileResult.Skipped;
                        case ZRPOS:
                            if (h.Pos > length)
                            {
                                _link.SendCancel();
                                Report(position, "Transfer failed: the receiver asked for data beyond the end of the file.");
                                return FileResult.Failed;
                            }
                            _errors++;
                            position = h.Pos;
                            if (!OnRetransmit(false))
                            {
                                return FileResult.Failed;
                            }
                            again = true; // back to the outer loop: a new ZDATA frame from that position
                            break;
                        case ZABORT:
                        case ZFERR:
                        case ZCAN:
                            Report(position, "Cancelled by the receiver.");
                            return FileResult.Failed;
                        default:
                            break; // a late ZACK or noise: keep waiting
                    }
                }
            }
        }

        /// <summary>ZFIN ends the session; the sender answers the receiver's ZFIN with "OO" ("over and out").</summary>
        private bool Finish()
        {
            _total = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (Cancelled())
                {
                    return false;
                }
                SendHex(ZFIN, new byte[4]);
                HeaderStatus status = ReadHeader(_link, _t.ReplyMs, _t.ByteMs, _t.TrailerMs, out Header h);
                if (status == HeaderStatus.Ok && h.Type == ZFIN)
                {
                    _link.Send((byte)'O', (byte)'O');
                    Report(0, "Transfer complete.");
                    return true;
                }
                if (status == HeaderStatus.Cancelled)
                {
                    break;
                }
            }
            // Every file was acknowledged; only the goodbye got lost.
            _link.Send((byte)'O', (byte)'O');
            Report(0, "Transfer complete (the receiver did not confirm the end of the session).");
            return true;
        }
    }

    // ================= receiver =================

    private sealed class Receiver
    {
        private readonly Link _link;
        private readonly ZModemOptions _options;
        private readonly Timeouts _t;
        private readonly Func<ZModemFileInfo, Stream> _openFile;
        private readonly Action<ZModemFileInfo, bool> _fileFinished;
        private readonly ProgressHandler _progress;
        private readonly byte[] _buffer = new byte[MaxSubpacketBytes + 1024];
        private readonly Stopwatch _sinceZrinit = new Stopwatch();
        private int _errors;
        private ZModemFileInfo _current;
        private Stream _currentStream;
        private long _position;
        private int _filesReceived;
        private int _dataErrors;                 // damaged or misplaced data in a row; reset by data that gets through
        private readonly Stopwatch _sinceRequest = new Stopwatch(); // since the last ZRPOS
        private byte[] _attention = new byte[0]; // the sender's Attn string from ZSINIT

        public Receiver(Link link, ZModemOptions options, Timeouts timeouts, Func<ZModemFileInfo, Stream> openFile,
            Action<ZModemFileInfo, bool> fileFinished, ProgressHandler progress)
        {
            _link = link;
            _options = options;
            _t = timeouts;
            _openFile = openFile;
            _fileFinished = fileFinished;
            _progress = progress;
        }

        private void Report(long done, long total, string status)
        {
            _progress?.Invoke(done, Math.Max(0, total), _errors, status);
        }

        private long CurrentTotal => _current == null ? 0 : _current.Length;

        private void SendHex(byte type, byte[] p)
        {
            byte[] frame = BuildHexHeader(type, p);
            _link.Send(frame, frame.Length);
        }

        /// <summary>
        /// Asks the sender to go back to <see cref="_position"/>. Its attention string comes first: it interrupts a sender
        /// that only looks for answers between its writes. In that string 0xDD is a break (not possible here) and 0xDE a
        /// pause of one second.
        /// </summary>
        private void RequestPosition()
        {
            if (_attention.Length > 0)
            {
                int from = 0;
                for (int i = 0; i <= _attention.Length; i++)
                {
                    if (i == _attention.Length || _attention[i] == 0xDD || _attention[i] == 0xDE)
                    {
                        if (i > from)
                        {
                            byte[] part = new byte[i - from];
                            Buffer.BlockCopy(_attention, from, part, 0, part.Length);
                            _link.Send(part, part.Length);
                        }
                        if (i < _attention.Length && _attention[i] == 0xDE)
                        {
                            Thread.Sleep(1000);
                        }
                        from = i + 1;
                    }
                }
            }
            SendHex(ZRPOS, PosBytes(_position));
            _sinceRequest.Restart();
        }

        /// <summary>Counts damaged or misplaced data; false (the transfer has ended) when there has been too much of it in a row.</summary>
        private bool CountDataError()
        {
            _errors++;
            if (++_dataErrors > MaxDataErrors)
            {
                Fail("Transfer failed: too many damaged data blocks.", true);
                return false;
            }
            return true;
        }

        private void SendZrinit()
        {
            byte flags = CANFDX | CANOVIO;
            if (_options.UseCrc32)
            {
                flags |= CANFC32;
            }
            if (_options.EscapeControlCharacters)
            {
                flags |= ESCCTL;
            }
            SendHex(ZRINIT, FlagBytes(flags));
            _sinceZrinit.Restart();
        }

        private void CloseCurrent(bool complete)
        {
            ZModemFileInfo info = _current;
            Stream stream = _currentStream;
            _current = null;
            _currentStream = null;
            if (stream != null)
            {
                try
                {
                    stream.Dispose();
                }
                catch (Exception)
                {
                    complete = false;
                }
            }
            if (info != null)
            {
                _fileFinished?.Invoke(info, complete);
            }
        }

        public void Abandon()
        {
            CloseCurrent(false);
        }

        /// <summary>Ends the transfer unsuccessfully: the file in progress is closed as incomplete.</summary>
        private bool Fail(string status, bool tellPeer)
        {
            long done = _position;
            long total = CurrentTotal;
            CloseCurrent(false);
            if (tellPeer)
            {
                _link.SendCancel();
            }
            Report(done, total, status);
            return false;
        }

        public bool Run()
        {
            SendZrinit();
            Report(0, 0, "Waiting for the sender...");
            int idle = 0;
            while (true)
            {
                if (_link.IsCancelled)
                {
                    return Fail("Transfer cancelled.", true);
                }
                HeaderStatus status = ReadHeader(_link, _t.HeaderMs, _t.ByteMs, _t.TrailerMs, out Header h);
                if (_link.IsCancelled)
                {
                    continue; // reported at the top of the loop
                }
                if (status == HeaderStatus.Cancelled)
                {
                    return Fail("Cancelled by the sender.", false);
                }
                if (status == HeaderStatus.Timeout)
                {
                    _errors++;
                    if (++idle > MaxRetries)
                    {
                        return Fail(_current == null ? "The sender did not start the transfer." : "Transfer failed: the sender stopped responding.", true);
                    }
                    if (_current == null)
                    {
                        SendZrinit();
                    }
                    else
                    {
                        RequestPosition();
                    }
                    continue;
                }
                if (status == HeaderStatus.Bad)
                {
                    _errors++;
                    if (++idle > MaxRetries * 3)
                    {
                        return Fail("Transfer failed: too many damaged headers.", true);
                    }
                    SendHex(ZNAK, new byte[4]);
                    continue;
                }
                idle = 0;

                switch (h.Type)
                {
                    case ZRQINIT:
                        if (_current == null && _sinceZrinit.ElapsedMilliseconds >= _t.DuplicateRequestMs)
                        {
                            SendZrinit();
                        }
                        break;

                    case ZSINIT:
                        {
                            DataStatus ds = ReadSubpacket(_link, h.Crc32, _buffer, _t.ByteMs, out int length, out _);
                            if (ds == DataStatus.Ok)
                            {
                                // The attention string, up to its terminating NUL (at most 32 bytes by the specification).
                                int end = Array.IndexOf(_buffer, (byte)0, 0, length);
                                int count = Math.Min(32, end < 0 ? length : end);
                                _attention = new byte[count];
                                Buffer.BlockCopy(_buffer, 0, _attention, 0, count);
                                SendHex(ZACK, PosBytes(1));
                            }
                            else
                            {
                                _errors++;
                                SendHex(ZNAK, new byte[4]);
                            }
                            break;
                        }

                    case ZFILE:
                        if (!HandleFile(h))
                        {
                            return false;
                        }
                        break;

                    case ZDATA:
                        if (_current == null)
                        {
                            _errors++;
                            SendHex(ZNAK, new byte[4]);
                            break;
                        }
                        if (h.Pos != _position)
                        {
                            if (!CountDataError())
                            {
                                return false;
                            }
                            RequestPosition();
                            break;
                        }
                        if (!ReceiveData(h.Crc32))
                        {
                            return false;
                        }
                        break;

                    case ZEOF:
                        if (_current == null)
                        {
                            SendZrinit(); // the ZRINIT that acknowledged the file was lost
                            break;
                        }
                        if (h.Pos != _position)
                        {
                            // The end of an abandoned stream. Usually the data it belonged to is already being sent again,
                            // but when our request is long gone it was lost (or damaged) on the way: ask again now
                            // instead of waiting for a timeout.
                            if (_sinceRequest.IsRunning && _sinceRequest.ElapsedMilliseconds >= _t.RepeatRequestMs)
                            {
                                if (!CountDataError())
                                {
                                    return false;
                                }
                                RequestPosition();
                            }
                            break;
                        }
                        {
                            long done = _position;
                            long total = CurrentTotal;
                            string name = _current.RemoteName;
                            CloseCurrent(true);
                            _filesReceived++;
                            Report(done, total, "Received " + name);
                        }
                        SendZrinit();
                        break;

                    case ZFIN:
                        SendHex(ZFIN, new byte[4]);
                        for (int i = 0; i < 2; i++)
                        {
                            if (_link.Read(_t.OverAndOutMs) != 'O')
                            {
                                break;
                            }
                        }
                        CloseCurrent(false);
                        Report(0, 0, _filesReceived == 1 ? "Received 1 file." : "Received " + _filesReceived + " files.");
                        return true;

                    case ZABORT:
                    case ZCAN:
                    case ZFERR:
                        return Fail("Cancelled by the sender.", false);

                    case ZCOMMAND:
                        {
                            // A sender asking to run a command here is never obeyed: just read it and say it failed.
                            ReadSubpacket(_link, h.Crc32, _buffer, _t.ByteMs, out _, out _);
                            SendHex(ZCOMPL, new byte[] { 1, 0, 0, 0 });
                            break;
                        }

                    case ZNAK:
                        // The sender could not read our last header: repeat it.
                        if (_current == null)
                        {
                            SendZrinit();
                        }
                        else
                        {
                            RequestPosition();
                        }
                        break;

                    case ZFREECNT:
                        // The free space is not known here (the caller picks the folder): "plenty", as lrzsz says.
                        SendHex(ZACK, PosBytes(int.MaxValue));
                        break;

                    case ZCHALLENGE:
                        SendHex(ZACK, PosBytes(h.Pos));
                        break;
                }
            }
        }

        private bool HandleFile(Header h)
        {
            DataStatus ds = ReadSubpacket(_link, h.Crc32, _buffer, _t.ByteMs, out int length, out _);
            if (_link.IsCancelled)
            {
                return true;
            }
            if (ds == DataStatus.Cancelled)
            {
                return Fail("Cancelled by the sender.", false);
            }
            if (ds != DataStatus.Ok)
            {
                _errors++;
                SendHex(ZNAK, new byte[4]);
                return true;
            }

            ZModemFileInfo info = ParseFileInfo(_buffer, length);
            if (_current != null && _current.RemoteName == info.RemoteName && _current.Length == info.Length)
            {
                // The sender repeated its offer because it did not see our answer.
                SendHex(ZRPOS, PosBytes(_position));
                return true;
            }
            if (_current != null)
            {
                CloseCurrent(false);
            }

            Stream stream;
            try
            {
                stream = _openFile(info);
            }
            catch (Exception ex)
            {
                _errors++;
                SendHex(ZSKIP, new byte[4]);
                Report(0, info.Length, "Cannot create " + info.Name + ": " + ex.Message);
                return true;
            }
            if (stream == null)
            {
                SendHex(ZSKIP, new byte[4]);
                Report(0, info.Length, "Skipped " + info.RemoteName);
                return true;
            }
            _current = info;
            _currentStream = stream;
            _position = 0;
            _dataErrors = 0;
            string more = info.FilesRemaining > 1 ? " (" + info.FilesRemaining + " files left)" : string.Empty;
            Report(0, info.Length, "Receiving " + info.RemoteName + more);
            SendHex(ZRPOS, PosBytes(0));
            return true;
        }

        private static ZModemFileInfo ParseFileInfo(byte[] buffer, int length)
        {
            int nul = Array.IndexOf(buffer, (byte)0, 0, length);
            int nameLength = nul < 0 ? length : nul;
            string remoteName = Encoding.UTF8.GetString(buffer, 0, nameLength);
            ZModemFileInfo info = new ZModemFileInfo
            {
                RemoteName = remoteName,
                Name = SanitizeFileName(remoteName)
            };
            if (nul >= 0)
            {
                int end = Array.IndexOf(buffer, (byte)0, nul + 1, length - nul - 1);
                int restLength = (end < 0 ? length : end) - nul - 1;
                string rest = Encoding.ASCII.GetString(buffer, nul + 1, Math.Max(0, restLength));
                string[] parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long size))
                {
                    info.Length = size;
                }
                if (parts.Length > 1)
                {
                    try
                    {
                        long seconds = Convert.ToInt64(parts[1], 8);
                        if (seconds > 0 && seconds < 253402300799L)
                        {
                            info.ModifiedUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
                        }
                    }
                    catch (Exception)
                    {
                        // An unreadable time stamp only means the file keeps the time of its arrival.
                    }
                }
                if (parts.Length > 4 && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out int left))
                {
                    info.FilesRemaining = left;
                }
            }
            return info;
        }

        /// <summary>Reads the subpackets of one ZDATA frame. False only when the whole transfer has to end.</summary>
        private bool ReceiveData(bool crc32)
        {
            while (true)
            {
                DataStatus ds = ReadSubpacket(_link, crc32, _buffer, _t.ByteMs, out int length, out byte terminator);
                if (_link.IsCancelled)
                {
                    return true; // reported by the caller's loop
                }
                if (ds == DataStatus.Cancelled)
                {
                    return Fail("Cancelled by the sender.", false);
                }
                if (ds != DataStatus.Ok)
                {
                    // Damaged or incomplete: ask for the data again from here; everything the sender streams until it
                    // reacts is skipped while waiting for the next ZDATA header.
                    if (!CountDataError())
                    {
                        return false;
                    }
                    RequestPosition();
                    Report(_position, CurrentTotal, "Damaged data at " + FormatBytes(_position) + ", asking again");
                    return true;
                }
                _dataErrors = 0;

                try
                {
                    _currentStream.Write(_buffer, 0, length);
                }
                catch (Exception ex)
                {
                    return Fail("Cannot write the file: " + ex.Message, true);
                }
                _position += length;
                Report(_position, CurrentTotal, "Receiving " + _current.RemoteName + ": " + FormatBytes(_position)
                    + (_current.Length > 0 ? " of " + FormatBytes(_current.Length) : string.Empty));

                switch (terminator)
                {
                    case ZCRCQ:
                        SendHex(ZACK, PosBytes(_position));
                        continue;
                    case ZCRCW:
                        SendHex(ZACK, PosBytes(_position));
                        return true;
                    case ZCRCE:
                        return true;
                    default:
                        continue; // ZCRCG
                }
            }
        }
    }
}
