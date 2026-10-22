using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SmarTTY.IO;

/// <summary>
/// The Kermit file transfer protocol (basic Kermit with the usual extensions) over an arbitrary byte link: Send-Init
/// parameter negotiation, 6-bit checksum / 12-bit checksum / CRC-16 block checks, control and 8-bit prefixing, repeat
/// counts, long (extended-length) packets, sliding windows (up to 31 data packets in flight, selective retransmission of
/// the damaged ones) and attribute packets (file size for the progress display, file type, modification time; a receiver
/// that refuses a file in its answer to the attributes makes the sender skip it). Several files can be sent or received
/// in a row. The link is given as a "send bytes" callback plus a "read one byte with a timeout" callback, so the protocol
/// logic has no dependency on the terminal (same idea as <see cref="XModemTransfer"/>).
/// </summary>
public class KermitTransfer
{
    public const byte Mark = 0x01; // start of every packet (SOH)

    private const int MaxRetries = 10;
    private const int MaxWindow = 31;              // the largest sliding window: less than half of the 64 sequence numbers
    private const int MaxShortCount = 94;          // largest value of LEN - 32 in a normal packet that this side sends
    private const int MaxShortCountAccepted = 95;  // C-Kermit with "set send packet-length 94" sends LEN = 127 (count 95): be liberal
    private const int MaxExtendedCount = 9024;     // largest data + check length of a long packet (95 * 95 - 1)
    private const int FrameBufferSize = MaxExtendedCount + 16;
    private const byte DefaultEol = 13;            // CR ends a packet unless the peer asks for something else

    // Packet types.
    private const byte TypeData = (byte)'D';
    private const byte TypeAck = (byte)'Y';
    private const byte TypeNak = (byte)'N';
    private const byte TypeSendInit = (byte)'S';
    private const byte TypeFile = (byte)'F';
    private const byte TypeEof = (byte)'Z';
    private const byte TypeBreak = (byte)'B';
    private const byte TypeError = (byte)'E';
    private const byte TypeAttributes = (byte)'A';
    private const byte TypeText = (byte)'X';

    // CAPAS bits of the Send-Init packet.
    private const int CapsMore = 1;
    private const int CapsLongPackets = 2;
    private const int CapsSlidingWindows = 4;
    private const int CapsAttributes = 8;

    public delegate void ProgressHandler(long bytesTransferred, long totalBytes, int errorCount, string status);
    public delegate void SendDataHandler(byte[] data, int offset, int length);

    /// <summary>
    /// A file to send: its name on the wire, its length (for the progress display), a way to open it and, optionally, its
    /// modification time (sent as an attribute, so the receiver can give the copy the same time).
    /// </summary>
    public sealed class Source
    {
        public Source(string name, long length, Func<Stream> open, DateTime? modifiedUtc = null)
        {
            Name = name;
            Length = length;
            Open = open;
            ModifiedUtc = modifiedUtc;
        }

        public string Name { get; }
        public long Length { get; }
        public Func<Stream> Open { get; }
        public DateTime? ModifiedUtc { get; }
    }

    /// <summary>What this side offers in the Send-Init packet. The defaults suit a clean 8-bit line.</summary>
    internal sealed class Settings
    {
        public bool LongPackets = true;
        public bool Attributes = true;

        /// <summary>'3' CRC-16, '2' 12-bit checksum, '1' 6-bit checksum.</summary>
        public byte BlockCheck = (byte)'3';

        /// <summary>'Y' = the line is 8-bit clean and 8-bit data goes unprefixed, '&amp;' (or another prefix) = quote 8-bit bytes, 'N' = 7-bit only.</summary>
        public byte EightBitPrefix = (byte)'Y';

        /// <summary>The repeat-count prefix, 0 to switch compression off.</summary>
        public byte RepeatPrefix = (byte)'~';

        /// <summary>The most data a long packet may carry (what this side is willing to receive and to send).</summary>
        public int MaxPacket = 2000;

        /// <summary>The sliding window offered (1..31 packets in flight); 1 is one packet at a time. The peer may ask for fewer.</summary>
        public int WindowSize = MaxWindow;
    }

    /// <summary>The waiting times of the protocol; tests shorten them.</summary>
    internal sealed class Timeouts
    {
        /// <summary>How long a sender keeps offering its Send-Init to a receiver that does not answer.</summary>
        public int SenderInitTotalMs = 60000;

        /// <summary>The pause between two copies of the Send-Init.</summary>
        public int SenderInitRetryMs = 4000;

        /// <summary>How long a receiver waits for the sender to start before giving up.</summary>
        public int ReceiverInitTotalMs = 60000;

        /// <summary>The pause between the NAKs with which a receiver invites the sender to start.</summary>
        public int ReceiverNudgeMs = 4000;

        /// <summary>How long a sender waits for the answer to a packet.</summary>
        public int AckMs = 10000;

        /// <summary>How long a receiver waits for the next packet before asking again.</summary>
        public int PacketMs = 10000;

        /// <summary>The gap allowed between two bytes inside a packet.</summary>
        public int ByteMs = 3000;

        /// <summary>
        /// Sliding windows: the shortest quiet time after which a receiver with a gap in its window (or right after a
        /// damaged packet) asks for the missing packet again, and a sender repeats its oldest unacknowledged packet. The
        /// real time is a few times the usual gap between two packets, so a slow line gets more.
        /// </summary>
        public int WindowRepeatMs = 300;
    }

    #region Block checks and character helpers

    private static byte ToChar(int value) => (byte)(value + 32);

    private static int UnChar(int c) => c - 32;

    private static byte Ctl(int c) => (byte)(c ^ 64);

    public static int Checksum6(byte[] data, int offset, int length)
    {
        int sum = 0;
        for (int i = offset; i < offset + length; i++)
        {
            sum += data[i];
        }
        return (sum + ((sum & 192) >> 6)) & 63;
    }

    public static int Checksum12(byte[] data, int offset, int length)
    {
        int sum = 0;
        for (int i = offset; i < offset + length; i++)
        {
            sum += data[i];
        }
        return sum & 4095;
    }

    /// <summary>The CRC the Kermit protocol uses (CRC-16/KERMIT: polynomial 0x1021 reflected, start value 0).</summary>
    public static ushort Crc16(byte[] data, int offset, int length)
    {
        int crc = 0;
        for (int i = offset; i < offset + length; i++)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1;
            }
        }
        return (ushort)crc;
    }

    private static int CheckLength(byte blockCheck)
    {
        return blockCheck == (byte)'3' ? 3 : blockCheck == (byte)'2' ? 2 : 1;
    }

    /// <summary>Writes the block check of <paramref name="body"/> (LEN up to the end of the data) at <paramref name="pos"/>.</summary>
    private static int WriteCheck(byte[] frame, int pos, byte[] body, int bodyLength, byte blockCheck)
    {
        if (blockCheck == (byte)'3')
        {
            int crc = Crc16(body, 0, bodyLength);
            frame[pos++] = ToChar((crc >> 12) & 15);
            frame[pos++] = ToChar((crc >> 6) & 63);
            frame[pos++] = ToChar(crc & 63);
        }
        else if (blockCheck == (byte)'2')
        {
            int sum = Checksum12(body, 0, bodyLength);
            frame[pos++] = ToChar((sum >> 6) & 63);
            frame[pos++] = ToChar(sum & 63);
        }
        else
        {
            frame[pos++] = ToChar(Checksum6(body, 0, bodyLength));
        }
        return pos;
    }

    #endregion

    #region Negotiated parameters

    /// <summary>The parameters of one Send-Init packet, with the defaults the protocol assumes for absent fields.</summary>
    private sealed class InitParameters
    {
        public int MaxLength = 80;
        public int TimeoutSeconds = 5;
        public int PadCount;
        public byte PadChar;
        public byte Eol = DefaultEol;
        public byte Qctl = (byte)'#';
        public byte Qbin = (byte)'N';
        public byte BlockCheck = (byte)'1';
        public byte Repeat = (byte)' ';
        public int Capabilities;
        public int Window = 1;  // the sliding window size the peer offers (1 = none)
        public int MaxExtended; // the most a peer accepts in a long packet, 0 when it did not say

        public bool HasLongPackets => (Capabilities & CapsLongPackets) != 0;
        public bool HasAttributes => (Capabilities & CapsAttributes) != 0;
        public bool HasSlidingWindows => (Capabilities & CapsSlidingWindows) != 0;
    }

    private static bool IsPrefixChar(byte c)
    {
        return (c >= 33 && c <= 62) || (c >= 96 && c <= 126);
    }

    private static InitParameters ParseInit(byte[] data, int length)
    {
        InitParameters p = new InitParameters();
        int Field(int index) => index < length ? data[index] : -1;

        int v = Field(0);
        if (v >= 0 && UnChar(v) >= 10) p.MaxLength = Math.Min(MaxShortCount, UnChar(v));
        v = Field(1);
        if (v >= 0 && UnChar(v) > 0) p.TimeoutSeconds = UnChar(v);
        v = Field(2);
        if (v >= 0 && UnChar(v) >= 0) p.PadCount = Math.Min(UnChar(v), 64);
        v = Field(3);
        if (v >= 0) p.PadChar = Ctl(v);
        v = Field(4);
        if (v >= 0 && UnChar(v) > 0 && UnChar(v) < 32) p.Eol = (byte)UnChar(v);
        v = Field(5);
        if (v >= 33 && v <= 126) p.Qctl = (byte)v;
        v = Field(6);
        if (v >= 0) p.Qbin = (byte)v;
        v = Field(7);
        if (v == '1' || v == '2' || v == '3') p.BlockCheck = (byte)v;
        v = Field(8);
        if (v >= 0) p.Repeat = (byte)v;

        // CAPAS: the low bit says another capability byte follows; the window size and the long packet size come after them.
        int index = 9;
        int caps = 0;
        bool first = true;
        while (index < length)
        {
            int c = UnChar(data[index++]);
            if (first)
            {
                caps = c;
                first = false;
            }
            if ((c & CapsMore) == 0)
            {
                break;
            }
        }
        p.Capabilities = caps;
        if (p.HasSlidingWindows && index < length)
        {
            p.Window = Math.Max(1, Math.Min(MaxWindow, UnChar(data[index])));
        }
        index++; // WINDO
        if (p.HasLongPackets && index + 1 < length)
        {
            int extended = UnChar(data[index]) * 95 + UnChar(data[index + 1]);
            if (extended > 0)
            {
                p.MaxExtended = Math.Min(extended, MaxExtendedCount);
            }
        }
        return p;
    }

    private static byte[] BuildInit(Settings settings, byte repeat, byte eightBit, byte blockCheck, int capabilities, int window)
    {
        int extended = Math.Min(settings.MaxPacket, MaxExtendedCount);
        byte[] data = new byte[13];
        data[0] = ToChar(MaxShortCount);
        data[1] = ToChar(5);           // TIME: how long the peer should wait for this side
        data[2] = ToChar(0);           // NPAD
        data[3] = Ctl(0);              // PADC
        data[4] = ToChar(DefaultEol);  // EOL
        data[5] = (byte)'#';           // QCTL
        data[6] = eightBit;            // QBIN
        data[7] = blockCheck;          // CHKT
        data[8] = repeat;              // REPT
        data[9] = ToChar(capabilities);
        data[10] = ToChar((capabilities & CapsSlidingWindows) != 0 ? window : 0); // WINDO
        data[11] = ToChar(extended / 95);
        data[12] = ToChar(extended % 95);
        return data;
    }

    private static int OurCapabilities(Settings settings)
    {
        return (settings.LongPackets ? CapsLongPackets : 0) | (settings.Attributes ? CapsAttributes : 0)
            | (OurWindow(settings) > 1 ? CapsSlidingWindows : 0);
    }

    private static int OurWindow(Settings settings)
    {
        return Math.Max(1, Math.Min(MaxWindow, settings.WindowSize));
    }

    private static byte ValidRepeat(byte repeat, byte qctl, byte qbin)
    {
        return repeat != 0 && IsPrefixChar(repeat) && repeat != qctl && repeat != qbin ? repeat : (byte)0;
    }

    /// <summary>The agreed 8-bit prefix: 0 when 8-bit bytes go as they are, otherwise the prefix character.</summary>
    private static byte AgreedEightBit(byte sender, byte receiver)
    {
        if (IsPrefixChar(sender) && (receiver == (byte)'Y' || receiver == sender))
        {
            return sender;
        }
        if (sender == (byte)'Y' && IsPrefixChar(receiver))
        {
            return receiver;
        }
        return 0;
    }

    /// <summary>Everything both sides agreed on that the data transfer itself depends on.</summary>
    private sealed class Session
    {
        public byte BlockCheck = (byte)'1';   // from the first packet after the Send-Init on
        public bool LongPackets;
        public bool Attributes;
        public int Window = 1;                 // data packets that may be in flight at once
        public int PeerMaxLength = 80;         // the most the peer accepts in a normal packet (its LEN value)
        public int PeerMaxExtended;            // the same for a long packet (data + check)
        public int PadCount;
        public byte PadChar;
        public byte PeerEol = DefaultEol;
        public byte Qctl = (byte)'#';          // the prefix of the data that is sent from here on
        public byte Qbin;                      // 0 = off
        public byte Repeat;                    // 0 = off
        public byte ReceiveQctl = (byte)'#';   // the prefixes of the data that arrives
        public byte ReceiveQbin;
        public byte ReceiveRepeat;
    }

    #endregion

    #region Packets

    private sealed class Packet
    {
        public int Seq;
        public byte Type;
        public byte[] Data;
        public byte[] Decoded; // the file data of a D packet, decoded when it arrived
    }

    /// <summary>
    /// Builds one packet: padding, MARK, LEN, SEQ, TYPE, data, block check, terminator. A long packet (LEN = ' ' followed by
    /// the two length characters and a header check) is used only when <paramref name="allowLong"/> and the data does not
    /// fit into a normal one.
    /// </summary>
    private static byte[] BuildFrame(int seq, byte type, byte[] data, int dataLength, byte blockCheck, bool allowLong,
        int padCount, byte padChar, byte eol)
    {
        int checkLength = CheckLength(blockCheck);
        int count = 2 + dataLength + checkLength; // SEQ + TYPE + data + check
        bool isLong = count > MaxShortCount;
        if (isLong && !allowLong)
        {
            throw new InvalidOperationException("The data does not fit into a normal Kermit packet.");
        }

        byte[] body = new byte[(isLong ? 6 : 3) + dataLength];
        int pos = 0;
        if (isLong)
        {
            int extended = dataLength + checkLength;
            body[pos++] = ToChar(0);
            body[pos++] = ToChar(seq & 63);
            body[pos++] = type;
            body[pos++] = ToChar(extended / 95);
            body[pos++] = ToChar(extended % 95);
            body[pos++] = ToChar(Checksum6(body, 0, 5));
        }
        else
        {
            body[pos++] = ToChar(count);
            body[pos++] = ToChar(seq & 63);
            body[pos++] = type;
        }
        Buffer.BlockCopy(data ?? new byte[0], 0, body, pos, dataLength);

        byte[] frame = new byte[padCount + 1 + body.Length + checkLength + 1];
        int at = 0;
        for (int i = 0; i < padCount; i++)
        {
            frame[at++] = padChar;
        }
        frame[at++] = Mark;
        Buffer.BlockCopy(body, 0, frame, at, body.Length);
        at += body.Length;
        at = WriteCheck(frame, at, body, body.Length, blockCheck);
        frame[at++] = eol;
        return frame;
    }

    private enum ReadStatus
    {
        Packet,
        Timeout,
        Damaged
    }

    private const int ReadTimeout = -1;
    private const int ReadMark = -2;

    /// <summary>The byte link with cancellation: reads are done in short slices so a cancel is noticed within ~200 ms.</summary>
    private sealed class Link
    {
        private const int SliceMs = 200;

        private readonly SendDataHandler _send;
        private readonly Func<int, byte?> _read;
        private readonly CancellationToken _token;

        public Link(SendDataHandler send, Func<int, byte?> read, CancellationToken token)
        {
            _send = send;
            _read = read;
            _token = token;
        }

        public bool IsCancelled => _token.IsCancellationRequested;

        /// <summary>Where <see cref="ReadPacket"/> assembles a packet; reused, so reading does not allocate per packet.</summary>
        public readonly byte[] Frame = new byte[FrameBufferSize];

        public void Send(byte[] bytes)
        {
            _send(bytes, 0, bytes.Length);
        }

        /// <summary>The next byte, or null when nothing arrives within <paramref name="timeoutMs"/> or the transfer is cancelled.</summary>
        public byte? Read(int timeoutMs)
        {
            // Called for every byte of a transfer: the clock is a plain time stamp, not an allocated Stopwatch.
            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                if (_token.IsCancellationRequested)
                {
                    return null;
                }
                int left = timeoutMs - ElapsedMs(start);
                byte? b = _read(Math.Max(0, Math.Min(SliceMs, left)));
                if (b.HasValue)
                {
                    return b;
                }
                if (left <= SliceMs)
                {
                    return null;
                }
            }
        }

        /// <summary>Throws away whatever is on the line until it has been quiet for <paramref name="quietMs"/> (at most 5 s).</summary>
        public void Purge(int quietMs)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 5000 && Read(quietMs).HasValue)
            {
            }
        }
    }

    private static int ElapsedMs(long start)
    {
        return (int)Math.Min(int.MaxValue, (Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency);
    }

    /// <summary>Reads <paramref name="count"/> more bytes into the buffer; stops at a MARK, which starts a new packet.</summary>
    private static int Fill(Link link, int byteMs, byte[] buffer, ref int length, int count)
    {
        for (int i = 0; i < count; i++)
        {
            byte? b = link.Read(byteMs);
            if (!b.HasValue)
            {
                return ReadTimeout;
            }
            if (b.Value == Mark)
            {
                return ReadMark;
            }
            buffer[length++] = b.Value;
        }
        return 0;
    }

    private static bool TryParse(byte[] buffer, int length, bool isLong, byte blockCheck, out Packet packet)
    {
        packet = null;
        int checkLength = CheckLength(blockCheck);
        int headerLength = isLong ? 6 : 3;
        int dataLength = length - headerLength - checkLength;
        if (dataLength < 0)
        {
            return false;
        }
        int seq = UnChar(buffer[1]);
        if (seq < 0 || seq > 63)
        {
            return false;
        }
        int bodyLength = headerLength + dataLength;
        byte[] check = new byte[3];
        WriteCheck(check, 0, buffer, bodyLength, blockCheck);
        for (int i = 0; i < checkLength; i++)
        {
            if (buffer[bodyLength + i] != check[i])
            {
                return false;
            }
        }
        packet = new Packet { Seq = seq, Type = buffer[2], Data = new byte[dataLength] };
        Buffer.BlockCopy(buffer, headerLength, packet.Data, 0, dataLength);
        return true;
    }

    /// <summary>
    /// Waits for a packet. Anything before the MARK (echo, a prompt, noise) is skipped; a new MARK inside a packet restarts it.
    /// A packet with a wrong block check, an impossible length or too few bytes is reported as damaged. A Send-Init is always
    /// protected by the 6-bit check, whichever check the rest of the transfer uses.
    /// </summary>
    private static ReadStatus ReadPacket(Link link, int timeoutMs, int byteMs, byte blockCheck, out Packet packet)
    {
        packet = null;
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            int left = timeoutMs - ElapsedMs(start);
            if (left <= 0)
            {
                return ReadStatus.Timeout;
            }
            byte? b = link.Read(left);
            if (!b.HasValue)
            {
                return ReadStatus.Timeout;
            }
            if (b.Value == Mark)
            {
                break;
            }
        }

        byte[] buffer = link.Frame;
        while (true)
        {
            int length = 0;
            int r = Fill(link, byteMs, buffer, ref length, 1);
            if (r == ReadMark) continue;
            if (r == ReadTimeout) return ReadStatus.Damaged;

            bool isLong = buffer[0] == ToChar(0);
            if (isLong)
            {
                r = Fill(link, byteMs, buffer, ref length, 5);
                if (r == ReadMark) continue;
                if (r == ReadTimeout) return ReadStatus.Damaged;
                if (buffer[5] != ToChar(Checksum6(buffer, 0, 5)))
                {
                    return ReadStatus.Damaged;
                }
                int count = UnChar(buffer[3]) * 95 + UnChar(buffer[4]);
                if (count < 1 || count > MaxExtendedCount)
                {
                    return ReadStatus.Damaged;
                }
                r = Fill(link, byteMs, buffer, ref length, count);
            }
            else
            {
                int count = UnChar(buffer[0]);
                if (count < 3 || count > MaxShortCountAccepted)
                {
                    return ReadStatus.Damaged;
                }
                r = Fill(link, byteMs, buffer, ref length, count);
            }
            if (r == ReadMark) continue;
            if (r == ReadTimeout) return ReadStatus.Damaged;

            if (TryParse(buffer, length, isLong, blockCheck, out packet))
            {
                return ReadStatus.Packet;
            }
            if (blockCheck != (byte)'1' && buffer[2] == TypeSendInit && TryParse(buffer, length, isLong, (byte)'1', out packet))
            {
                return ReadStatus.Packet;
            }
            return ReadStatus.Damaged;
        }
    }

    #endregion

    #region Data encoding

    /// <summary>Appends the encoding of one byte: 8-bit prefix, control quoting (CTL), quoting of the prefix characters themselves.</summary>
    private static int EncodeByte(byte b, byte qctl, byte qbin, byte repeat, byte[] destination, int pos)
    {
        byte c = b;
        if ((c & 0x80) != 0 && qbin != 0)
        {
            destination[pos++] = qbin;
            c &= 0x7F;
        }
        int low = c & 0x7F;
        if (low < 32 || low == 127)
        {
            destination[pos++] = qctl;
            destination[pos++] = Ctl(c);
        }
        else if (c == qctl || (qbin != 0 && c == qbin) || (repeat != 0 && c == repeat))
        {
            destination[pos++] = qctl;
            destination[pos++] = c;
        }
        else
        {
            destination[pos++] = c;
        }
        return pos;
    }

    /// <summary>Decodes packet data; false when it ends in the middle of a prefix sequence.</summary>
    private static bool TryDecode(byte[] data, int length, byte qctl, byte qbin, byte repeat, Stream output)
    {
        byte[] chunk = new byte[4096];
        int chunkLength = 0;
        int i = 0;
        while (i < length)
        {
            int count = 1;
            byte b = data[i++];
            if (repeat != 0 && b == repeat)
            {
                if (i + 1 >= length) return false;
                count = UnChar(data[i++]);
                b = data[i++];
                if (count < 0 || count > 94) return false;
            }
            bool eightBit = false;
            if (qbin != 0 && b == qbin)
            {
                if (i >= length) return false;
                eightBit = true;
                b = data[i++];
            }
            if (b == qctl)
            {
                if (i >= length) return false;
                byte c = data[i++];
                int low = c & 0x7F;
                b = low >= 63 && low <= 95 ? Ctl(c) : c;
            }
            if (eightBit)
            {
                b |= 0x80;
            }
            for (int k = 0; k < count; k++)
            {
                if (chunkLength == chunk.Length)
                {
                    output.Write(chunk, 0, chunkLength);
                    chunkLength = 0;
                }
                chunk[chunkLength++] = b;
            }
        }
        output.Write(chunk, 0, chunkLength);
        return true;
    }

    private static string DecodeText(byte[] data, byte qctl)
    {
        using MemoryStream text = new MemoryStream();
        if (!TryDecode(data, data.Length, qctl, 0, 0, text))
        {
            return Encoding.ASCII.GetString(data);
        }
        StringBuilder builder = new StringBuilder();
        foreach (byte b in text.ToArray())
        {
            builder.Append(b >= 32 && b < 127 ? (char)b : ' ');
        }
        return builder.ToString().Trim();
    }

    private static byte[] EncodeText(string text, byte qctl, out int length)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        byte[] destination = new byte[bytes.Length * 2 + 4];
        int pos = 0;
        foreach (byte b in bytes)
        {
            pos = EncodeByte(b, qctl, 0, 0, destination, pos);
        }
        length = pos;
        return destination;
    }

    /// <summary>A name that is safe to create on this machine: no directory, no characters Windows forbids.</summary>
    private static string SanitizeFileName(string name)
    {
        string result = name ?? string.Empty;
        int slash = Math.Max(result.LastIndexOf('/'), result.LastIndexOf('\\'));
        if (slash >= 0)
        {
            result = result.Substring(slash + 1);
        }
        StringBuilder builder = new StringBuilder();
        foreach (char ch in result)
        {
            builder.Append(ch < 32 || "<>:\"/\\|?*".IndexOf(ch) >= 0 ? '_' : ch);
        }
        result = builder.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0)
        {
            return "kermit.dat";
        }
        // Windows device names (CON, NUL, COM1...) are not files, with or without an extension.
        int dot = result.IndexOf('.');
        string stem = (dot >= 0 ? result.Substring(0, dot) : result).ToUpperInvariant();
        bool reserved = stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9');
        return reserved ? "_" + result : result;
    }

    #endregion

    /// <summary>Sends a packet whose loss does not matter; a broken link must not hide the real error.</summary>
    private static void SendBestEffort(Link link, byte[] frame)
    {
        try
        {
            link.Send(frame);
        }
        catch (Exception)
        {
        }
    }

    private static void Report(ProgressHandler onProgress, long done, long total, int errors, string status)
    {
        onProgress?.Invoke(done, total, errors, status);
    }

    #region Sending

    public static bool SendFiles(
        IList<Source> files,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress)
    {
        return SendFiles(files, sendData, readByteWithTimeout, cancellationToken, onProgress, new Settings(), new Timeouts());
    }

    private enum Exchange
    {
        Done,
        Cancelled,
        PeerError,
        GaveUp
    }

    /// <summary>
    /// Sends a packet and waits for its ACK, repeating the packet on a NAK or silence. Packets of other sequence numbers
    /// (echo, a late answer to an earlier packet) are ignored. A NAK for the next packet counts as the ACK of this one, unless
    /// sliding windows are in use (<paramref name="windowed"/>): then receivers also ask in advance for packets not sent yet.
    /// </summary>
    private static Exchange Roundtrip(Link link, byte[] frame, int seq, byte blockCheck, Timeouts timeouts, ref int errors,
        out Packet reply, out string message, bool windowed = false)
    {
        reply = null;
        message = null;
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            if (link.IsCancelled)
            {
                return Exchange.Cancelled;
            }
            link.Send(frame);
            Stopwatch timer = Stopwatch.StartNew();
            bool resend = false;
            while (!resend)
            {
                int left = timeouts.AckMs - (int)timer.ElapsedMilliseconds;
                if (left <= 0)
                {
                    break;
                }
                ReadStatus status = ReadPacket(link, left, timeouts.ByteMs, blockCheck, out Packet packet);
                if (link.IsCancelled)
                {
                    return Exchange.Cancelled;
                }
                if (status == ReadStatus.Timeout)
                {
                    break;
                }
                if (status == ReadStatus.Damaged)
                {
                    resend = true; // the answer was garbled: asking again is quicker than waiting for the timeout
                    continue;
                }
                if (packet.Type == TypeError)
                {
                    message = DecodeText(packet.Data, (byte)'#');
                    return Exchange.PeerError;
                }
                if (packet.Type == TypeAck && packet.Seq == seq)
                {
                    reply = packet;
                    return Exchange.Done;
                }
                if (packet.Type == TypeNak && packet.Seq == ((seq + 1) & 63) && !windowed)
                {
                    reply = new Packet { Seq = seq, Type = TypeAck, Data = new byte[0] };
                    return Exchange.Done;
                }
                if (packet.Type == TypeNak && packet.Seq == seq)
                {
                    resend = true;
                }
            }
            errors++;
        }
        return Exchange.GaveUp;
    }

    /// <summary>A failure to read the file being sent, kept apart from failures of the link.</summary>
    private sealed class FileReadException : Exception
    {
        public FileReadException(string message) : base(message)
        {
        }
    }

    /// <summary>Turns the file into the data field of D packets: control and 8-bit prefixing, repeat counts.</summary>
    private sealed class DataEncoder
    {
        private readonly Stream _stream;
        private readonly Session _session;
        private readonly byte[] _raw = new byte[4096];
        private int _pos;
        private int _length;
        private bool _endOfFile;

        public DataEncoder(Stream stream, Session session)
        {
            _stream = stream;
            _session = session;
        }

        /// <summary>
        /// Encodes as many bytes as fit into <paramref name="capacity"/>; <paramref name="length"/> is 0 at the end of the
        /// file. <paramref name="consumed"/> gets the number of file bytes that went in.
        /// </summary>
        public byte[] Next(int capacity, out int length, out long consumed)
        {
            byte[] encoded = new byte[capacity + 8];
            length = 0;
            consumed = 0;
            try
            {
                while (length + 5 <= capacity)
                {
                    if (_pos == _length)
                    {
                        if (_endOfFile) break;
                        _length = _stream.Read(_raw, 0, _raw.Length);
                        _pos = 0;
                        if (_length <= 0)
                        {
                            _length = 0;
                            _endOfFile = true;
                            break;
                        }
                    }
                    byte b = _raw[_pos];
                    int run = 1;
                    if (_session.Repeat != 0)
                    {
                        while (run < 94 && _pos + run < _length && _raw[_pos + run] == b)
                        {
                            run++;
                        }
                    }
                    if (_session.Repeat != 0 && run > 3)
                    {
                        encoded[length++] = _session.Repeat;
                        encoded[length++] = ToChar(run);
                    }
                    else
                    {
                        run = 1;
                    }
                    length = EncodeByte(b, _session.Qctl, _session.Qbin, _session.Repeat, encoded, length);
                    _pos += run;
                    consumed += run;
                }
            }
            catch (Exception ex)
            {
                throw new FileReadException(ex.Message);
            }
            return encoded;
        }
    }

    /// <summary>A data packet that has been sent and is not yet acknowledged (or is acknowledged but not yet the oldest).</summary>
    private sealed class WindowSlot
    {
        public int Seq;
        public byte[] Frame;
        public long Consumed;   // the file bytes it carries
        public bool Acked;
        public int Resends;     // how often it was sent again, for any reason
        public int Silences;    // timeouts in a row while it was the oldest unacknowledged packet
        public long SentAt;
    }

    private const int MinDataCapacity = 40;      // the smallest data field the sender shrinks its packets to on a noisy line
    private const int MaxResendsPerPacket = 50;  // NAKs show the line is alive, so a packet may be repeated more often than after silence

    /// <summary>
    /// Sends the D packets of one file with a sliding window: up to <see cref="Session.Window"/> packets are in flight;
    /// each is acknowledged on its own, a NAK or a timeout repeats only the packet concerned, and the window moves on
    /// when its oldest packet is acknowledged. With a window of 1 this is the classic one packet at a time.
    /// The packet length adapts to the line: repeated errors halve it for the packets still to come (down to
    /// <see cref="MinDataCapacity"/>), a run of packets that got through doubles it again (up to
    /// <paramref name="maxCapacity"/>). A packet already sent is always repeated as it was: the receiver may have taken it
    /// and only its ACK was lost, and then a shorter copy would be taken for a plain repeat.
    /// <paramref name="onDone"/> gets the file bytes of each packet as the window moves past it. An 'X' or 'Z' in an ACK
    /// asks to interrupt the file (or the whole batch): no new packets are sent, the ones in flight are finished.
    /// </summary>
    private static Exchange SendData(Link link, Session session, Timeouts timeouts, ref int seq, ref int errors,
        out bool interrupted, out bool interruptAll, out string message, DataEncoder encoder, int maxCapacity, Action<long> onDone)
    {
        interrupted = false;
        interruptAll = false;
        message = null;
        List<WindowSlot> window = new List<WindowSlot>(session.Window);
        bool endOfData = false;
        int capacity = maxCapacity;
        int errorRun = 0;
        int goodRun = 0;
        double replyGapMs = 0;  // smoothed time between two answers: how busy the line is
        long lastReplyAt = 0;
        int silences = 0;       // quiet spells in a row

        WindowSlot Find(int s)
        {
            foreach (WindowSlot slot in window)
            {
                if (slot.Seq == s)
                {
                    return slot;
                }
            }
            return null;
        }

        while (true)
        {
            if (link.IsCancelled)
            {
                return Exchange.Cancelled;
            }

            // Fill the window.
            while (!endOfData && !interrupted && window.Count < session.Window)
            {
                byte[] data = encoder.Next(capacity, out int length, out long consumed);
                if (length == 0)
                {
                    endOfData = true;
                    break;
                }
                WindowSlot slot = new WindowSlot
                {
                    Seq = seq,
                    Frame = BuildFrame(seq, TypeData, data, length, session.BlockCheck, session.LongPackets, session.PadCount, session.PadChar, session.PeerEol),
                    Consumed = consumed,
                    SentAt = Stopwatch.GetTimestamp()
                };
                link.Send(slot.Frame);
                window.Add(slot);
                seq = (seq + 1) & 63;
            }
            if (window.Count == 0)
            {
                return Exchange.Done;
            }

            // The oldest packet still waiting for its ACK decides how long to wait.
            WindowSlot oldest = null;
            foreach (WindowSlot slot in window)
            {
                if (!slot.Acked)
                {
                    oldest = slot;
                    break;
                }
            }
            // One packet at a time the timeout is the classic fixed one. With a window the oldest packet is repeated when
            // the line falls quiet: the receiver has stopped answering although packets are in flight. "Quiet" is a few
            // times the usual gap between two answers (a slow line answers slowly), doubling with every quiet spell in a
            // row; the packet's own age never goes beyond the classic timeout.
            int wait = Math.Max(1, timeouts.AckMs - ElapsedMs(oldest.SentAt));
            bool quiet = false;
            if (session.Window > 1)
            {
                double idle = Math.Max(timeouts.WindowRepeatMs, 3 * replyGapMs) * (1 << Math.Min(silences, 10));
                if (idle < wait)
                {
                    wait = (int)idle;
                    quiet = true;
                }
            }
            ReadStatus status = ReadPacket(link, wait, timeouts.ByteMs, session.BlockCheck, out Packet packet);
            if (link.IsCancelled)
            {
                return Exchange.Cancelled;
            }

            if (status == ReadStatus.Packet && (packet.Type == TypeAck || packet.Type == TypeNak))
            {
                silences = 0;
                long now = Stopwatch.GetTimestamp();
                if (lastReplyAt != 0)
                {
                    double gap = (now - lastReplyAt) * 1000.0 / Stopwatch.Frequency;
                    if (gap < timeouts.AckMs)
                    {
                        replyGapMs = replyGapMs == 0 ? gap : (7 * replyGapMs + gap) / 8;
                    }
                }
                lastReplyAt = now;
            }

            WindowSlot resend = null;
            if (status == ReadStatus.Timeout)
            {
                resend = oldest;
                silences++;
                if (!quiet && ++oldest.Silences >= MaxRetries)
                {
                    return Exchange.GaveUp;
                }
            }
            else if (status == ReadStatus.Damaged)
            {
                oldest.Silences = 0; // the receiver is there, only the line is bad
                // A garbled answer, often the NAK we would act on. One packet at a time, or once nothing new can be sent,
                // repeating the oldest packet is quicker than waiting for the timeout. With a full window the receiver
                // asks for what it is missing when the next packets arrive.
                if (session.Window == 1 || endOfData || interrupted)
                {
                    resend = oldest;
                }
            }
            else if (packet.Type == TypeError)
            {
                message = DecodeText(packet.Data, (byte)'#');
                return Exchange.PeerError;
            }
            else if (packet.Type == TypeAck)
            {
                oldest.Silences = 0;
                WindowSlot slot = Find(packet.Seq);
                if (slot != null && !slot.Acked)
                {
                    slot.Acked = true;
                    if (packet.Data.Length > 0 && (packet.Data[0] == (byte)'X' || packet.Data[0] == (byte)'Z'))
                    {
                        interrupted = true;
                        interruptAll |= packet.Data[0] == (byte)'Z';
                    }
                    if (slot.Resends == 0 && ++goodRun >= 8)
                    {
                        // The line has been good for a while: back towards full-size packets.
                        goodRun = 0;
                        errorRun = 0;
                        capacity = Math.Min(maxCapacity, capacity * 2);
                    }
                }
            }
            else if (packet.Type == TypeNak)
            {
                oldest.Silences = 0;
                WindowSlot slot = Find(packet.Seq);
                if (slot != null)
                {
                    if (!slot.Acked)
                    {
                        resend = slot;
                    }
                }
                else if (packet.Seq == seq && session.Window == 1)
                {
                    // One packet at a time, a NAK for the next packet means the receiver has this one. (With a window
                    // it does not: receivers such as C-Kermit also ask in advance for packets not sent yet.)
                    foreach (WindowSlot pending in window)
                    {
                        pending.Acked = true;
                    }
                }
            }
            // Other packets (our own echo, a late answer to an earlier packet) are ignored.

            if (resend != null)
            {
                errors++;
                goodRun = 0;
                if (++resend.Resends >= MaxResendsPerPacket)
                {
                    return Exchange.GaveUp;
                }
                int floor = Math.Min(MinDataCapacity, maxCapacity);
                if (++errorRun >= 2 && capacity > floor)
                {
                    // A noisy line: shorter packets lose less to each damaged byte.
                    errorRun = 0;
                    capacity = Math.Max(floor, capacity / 2);
                }
                resend.SentAt = Stopwatch.GetTimestamp();
                link.Send(resend.Frame);
            }

            // Move the window past the acknowledged packets at its start.
            while (window.Count > 0 && window[0].Acked)
            {
                onDone(window[0].Consumed);
                window.RemoveAt(0);
            }
        }
    }

    /// <summary>The attribute packet of a file: its length, "binary, 8 bits", and its modification time when known.</summary>
    private static byte[] BuildAttributes(Source file)
    {
        List<byte> attributes = new List<byte>();
        void Add(char tag, string value)
        {
            attributes.Add((byte)tag);
            attributes.Add(ToChar(value.Length));
            attributes.AddRange(Encoding.ASCII.GetBytes(value));
        }
        Add('1', Math.Max(0, file.Length).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add('"', "B8");
        if (file.ModifiedUtc.HasValue)
        {
            Add('#', file.ModifiedUtc.Value.ToLocalTime().ToString("yyyyMMdd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        }
        return attributes.ToArray();
    }

    internal static bool SendFiles(
        IList<Source> files,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress,
        Settings settings,
        Timeouts timeouts)
    {
        if (files == null || files.Count == 0) throw new ArgumentException("No files to send.", nameof(files));
        if (sendData == null) throw new ArgumentNullException(nameof(sendData));
        if (readByteWithTimeout == null) throw new ArgumentNullException(nameof(readByteWithTimeout));

        Link link = new Link(sendData, readByteWithTimeout, cancellationToken);
        long totalBytes = 0;
        foreach (Source file in files)
        {
            totalBytes += Math.Max(0, file.Length);
        }
        long doneBytes = 0;
        int errors = 0;
        int seq = 0;

        // Step 1: offer our parameters until the receiver answers. Whatever else arrives (a banner, an echo of the command
        // that started the receiver, its own invitation NAKs) is ignored.
        byte ourRepeat = ValidRepeat(settings.RepeatPrefix, (byte)'#', settings.EightBitPrefix);
        byte[] initData = BuildInit(settings, ourRepeat != 0 ? ourRepeat : (byte)' ', settings.EightBitPrefix, settings.BlockCheck, OurCapabilities(settings), OurWindow(settings));
        byte[] initFrame = BuildFrame(0, TypeSendInit, initData, initData.Length, (byte)'1', false, 0, 0, DefaultEol);

        Packet initReply = null;
        Stopwatch handshake = Stopwatch.StartNew();
        Report(onProgress, 0, totalBytes, errors, "Waiting for the receiver...");
        while (initReply == null)
        {
            if (link.IsCancelled)
            {
                SendBestEffort(link, BuildFrame(0, TypeError, Encoding.ASCII.GetBytes("Cancelled by the sender"), 23, (byte)'1', false, 0, 0, DefaultEol));
                Report(onProgress, 0, totalBytes, errors, "Transfer cancelled.");
                return false;
            }
            if (handshake.ElapsedMilliseconds > timeouts.SenderInitTotalMs)
            {
                Report(onProgress, 0, totalBytes, ++errors, "Handshake timeout: the receiver did not answer the Kermit start packet.");
                return false;
            }
            link.Send(initFrame);
            Stopwatch attempt = Stopwatch.StartNew();
            while (initReply == null)
            {
                int left = timeouts.SenderInitRetryMs - (int)attempt.ElapsedMilliseconds;
                if (left <= 0)
                {
                    break;
                }
                ReadStatus status = ReadPacket(link, left, timeouts.ByteMs, (byte)'1', out Packet packet);
                if (link.IsCancelled || status == ReadStatus.Timeout)
                {
                    break;
                }
                if (status == ReadStatus.Damaged)
                {
                    continue;
                }
                if (packet.Type == TypeError)
                {
                    Report(onProgress, 0, totalBytes, ++errors, "The receiver refused the transfer: " + DecodeText(packet.Data, (byte)'#'));
                    return false;
                }
                if (packet.Type == TypeAck && packet.Seq == 0)
                {
                    initReply = packet;
                }
                else if (packet.Type == TypeNak && packet.Seq == 0 && attempt.ElapsedMilliseconds > 500)
                {
                    break; // our packet was damaged on the way: send it again at once
                }
            }
            if (initReply == null && !link.IsCancelled)
            {
                errors++;
            }
        }

        InitParameters peer = ParseInit(initReply.Data, initReply.Data.Length);
        Session session = new Session
        {
            BlockCheck = peer.BlockCheck == settings.BlockCheck ? settings.BlockCheck : (byte)'1',
            LongPackets = settings.LongPackets && peer.HasLongPackets,
            Attributes = settings.Attributes && peer.HasAttributes,
            Window = OurWindow(settings) > 1 && peer.HasSlidingWindows ? Math.Min(OurWindow(settings), peer.Window) : 1,
            PeerMaxLength = peer.MaxLength,
            PeerMaxExtended = peer.MaxExtended > 0 ? peer.MaxExtended : 500,
            PadCount = peer.PadCount,
            PadChar = peer.PadChar,
            PeerEol = peer.Eol,
            Qctl = (byte)'#',
            Qbin = AgreedEightBit(settings.EightBitPrefix, peer.Qbin),
            Repeat = ourRepeat != 0 && peer.Repeat == ourRepeat ? ourRepeat : (byte)0
        };
        seq = 1;

        int checkLength = CheckLength(session.BlockCheck);
        int capacity = session.LongPackets
            ? Math.Min(session.PeerMaxExtended, Math.Min(settings.MaxPacket, MaxExtendedCount)) - checkLength
            : session.PeerMaxLength - 2 - checkLength;
        if (capacity < 16)
        {
            link.Send(BuildFrame(seq, TypeError, new byte[] { (byte)'p' }, 1, session.BlockCheck, false, 0, 0, session.PeerEol));
            Report(onProgress, 0, totalBytes, ++errors, "The receiver's packets are too short for this transfer.");
            return false;
        }

        bool SendError(string text)
        {
            byte[] data = EncodeText(text, session.Qctl, out int length);
            try
            {
                link.Send(BuildFrame(seq, TypeError, data, Math.Min(length, 60), session.BlockCheck, false, session.PadCount, session.PadChar, session.PeerEol));
            }
            catch (Exception)
            {
                // A broken link must not hide the real error.
            }
            return false;
        }

        Exchange Send(byte type, byte[] data, int length, out Packet reply, out string message)
        {
            byte[] frame = BuildFrame(seq, type, data, length, session.BlockCheck, session.LongPackets, session.PadCount, session.PadChar, session.PeerEol);
            Exchange result = Roundtrip(link, frame, seq, session.BlockCheck, timeouts, ref errors, out reply, out message, session.Window > 1);
            if (result == Exchange.Done)
            {
                seq = (seq + 1) & 63;
            }
            return result;
        }

        // Turns a failed exchange into the final report; true when the transfer has to stop.
        bool Failed(Exchange result, string message, string what, long done)
        {
            switch (result)
            {
                case Exchange.Cancelled:
                    SendError("Cancelled by the sender");
                    Report(onProgress, done, totalBytes, errors, "Transfer cancelled.");
                    return true;
                case Exchange.PeerError:
                    Report(onProgress, done, totalBytes, ++errors, "The receiver stopped the transfer: " + message);
                    return true;
                case Exchange.GaveUp:
                    SendError("Too many retries");
                    Report(onProgress, done, totalBytes, errors, "Transfer failed: no valid answer to the " + what + " packet after " + MaxRetries + " attempts.");
                    return true;
            }
            return false;
        }

        bool skipAll = false;
        for (int fileIndex = 0; fileIndex < files.Count && !skipAll; fileIndex++)
        {
            Source file = files[fileIndex];
            string wireName = SanitizeFileName(file.Name);
            string where = files.Count > 1 ? " (file " + (fileIndex + 1) + " of " + files.Count + ")" : string.Empty;

            Stream stream;
            try
            {
                stream = file.Open();
            }
            catch (Exception ex)
            {
                SendError("Cannot open the file");
                Report(onProgress, doneBytes, totalBytes, ++errors, "Cannot read " + wireName + ": " + ex.Message);
                return false;
            }

            using (stream)
            {
                Report(onProgress, doneBytes, totalBytes, errors, "Sending " + wireName + where);

                byte[] nameData = EncodeText(wireName, session.Qctl, out int nameLength);
                Exchange result = Send(TypeFile, nameData, nameLength, out Packet reply, out string message);
                if (Failed(result, message, "file name", doneBytes))
                {
                    return false;
                }

                bool skipFile = false;
                if (session.Attributes)
                {
                    // Tag '1' = the length in bytes (the receiver can show a progress bar), tag '"' = the file type: "B8" is
                    // binary, 8 bits, so a receiver that would otherwise treat the file as text does not touch its line ends,
                    // tag '#' = the modification time (local time, as Kermit programs expect).
                    // An attribute the receiver does not know is not worth failing for, so only a dead line ends the
                    // transfer here; a receiver that refuses the file ('N' in its answer) makes it skipped.
                    byte[] attributes = BuildAttributes(file);
                    result = Send(TypeAttributes, attributes, attributes.Length, out reply, out message);
                    if (Failed(result, message, "attributes", doneBytes))
                    {
                        return false;
                    }
                    if (reply.Data.Length > 0 && reply.Data[0] == (byte)'N')
                    {
                        skipFile = true;
                        Report(onProgress, doneBytes, totalBytes, errors, "The receiver refused " + wireName + where + ", skipping it.");
                    }
                }

                long fileDone = 0;

                if (!skipFile)
                {
                    bool interrupted;
                    bool interruptAll;
                    try
                    {
                        result = SendData(link, session, timeouts, ref seq, ref errors, out interrupted, out interruptAll, out message,
                            new DataEncoder(stream, session), capacity,
                            consumed =>
                            {
                                doneBytes += consumed;
                                fileDone += consumed;
                                Report(onProgress, doneBytes, totalBytes, errors, "Sending " + wireName + where);
                            });
                    }
                    catch (FileReadException ex)
                    {
                        SendError("Cannot read the file");
                        Report(onProgress, doneBytes, totalBytes, ++errors, "Cannot read " + wireName + ": " + ex.Message);
                        return false;
                    }
                    if (Failed(result, message, "data", doneBytes))
                    {
                        return false;
                    }
                    // 'X' in an ACK: skip the rest of this file; 'Z': skip this and all remaining files.
                    skipFile = interrupted;
                    skipAll = interruptAll;
                }

                byte[] eofData = skipFile ? new byte[] { (byte)'D' } : new byte[0];
                result = Send(TypeEof, eofData, eofData.Length, out reply, out message);
                if (Failed(result, message, "end-of-file", doneBytes))
                {
                    return false;
                }
                if (skipFile)
                {
                    // What was not sent still counts as handled, so the bar does not stall below 100 %.
                    doneBytes += Math.Max(0, file.Length - fileDone);
                }
            }
        }

        Exchange breakResult = Send(TypeBreak, new byte[0], 0, out Packet breakReply, out string breakMessage);
        if (Failed(breakResult, breakMessage, "end-of-transmission", doneBytes))
        {
            return false;
        }
        Report(onProgress, totalBytes, totalBytes, errors, "Transfer complete.");
        return true;
    }

    #endregion

    #region Receiving

    /// <summary>
    /// Receives one or more files. <paramref name="openTarget"/> gets the name the sender gave (already made safe for a file
    /// system) and returns the stream to write to; the stream is disposed here.
    /// <paramref name="discardTarget"/> is called with the same name when the file is not complete: the sender abandoned it,
    /// or the transfer failed or was cancelled while it was being written. <paramref name="onFileReceived"/> (optional) is
    /// called with the name once a file is complete and closed, with the modification time the sender gave (UTC), if any.
    /// </summary>
    public static bool ReceiveFiles(
        Func<string, Stream> openTarget,
        Action<string> discardTarget,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress,
        Action<string, DateTime?> onFileReceived = null)
    {
        return ReceiveFiles(openTarget, discardTarget, sendData, readByteWithTimeout, cancellationToken, onProgress, new Settings(), new Timeouts(), onFileReceived);
    }

    internal static bool ReceiveFiles(
        Func<string, Stream> openTarget,
        Action<string> discardTarget,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress,
        Settings settings,
        Timeouts timeouts,
        Action<string, DateTime?> onFileReceived = null)
    {
        if (openTarget == null) throw new ArgumentNullException(nameof(openTarget));
        if (sendData == null) throw new ArgumentNullException(nameof(sendData));
        if (readByteWithTimeout == null) throw new ArgumentNullException(nameof(readByteWithTimeout));

        Link link = new Link(sendData, readByteWithTimeout, cancellationToken);
        long received = 0;
        int errors = 0;
        int filesDone = 0;

        // Step 1: invite the sender with NAKs (as Kermit receivers do) until its Send-Init arrives. Bytes that are already
        // waiting are not thrown away: a sender that started first may have its Send-Init among them, and anything that is
        // not a packet is skipped by the reader anyway.
        byte[] nudge = BuildFrame(0, TypeNak, null, 0, (byte)'1', false, 0, 0, DefaultEol);
        Packet init = null;
        Stopwatch total = Stopwatch.StartNew();
        Stopwatch sinceNudge = new Stopwatch();
        Report(onProgress, 0, 0, errors, "Waiting for the sender...");
        while (init == null)
        {
            if (link.IsCancelled)
            {
                SendBestEffort(link, BuildFrame(0, TypeError, Encoding.ASCII.GetBytes("Cancelled by the receiver"), 25, (byte)'1', false, 0, 0, DefaultEol));
                Report(onProgress, 0, 0, errors, "Transfer cancelled.");
                return false;
            }
            if (total.ElapsedMilliseconds > timeouts.ReceiverInitTotalMs)
            {
                Report(onProgress, 0, 0, ++errors, "The sender did not start: no Kermit packet arrived.");
                return false;
            }
            if (!sinceNudge.IsRunning || sinceNudge.ElapsedMilliseconds >= timeouts.ReceiverNudgeMs)
            {
                link.Send(nudge);
                sinceNudge.Restart();
            }
            int wait = Math.Max(0, Math.Min(500, timeouts.ReceiverNudgeMs - (int)sinceNudge.ElapsedMilliseconds));
            ReadStatus status = ReadPacket(link, Math.Max(wait, 50), timeouts.ByteMs, (byte)'1', out Packet packet);
            if (status != ReadStatus.Packet)
            {
                continue;
            }
            if (packet.Type == TypeError)
            {
                Report(onProgress, 0, 0, ++errors, "The sender stopped the transfer: " + DecodeText(packet.Data, (byte)'#'));
                return false;
            }
            if (packet.Type == TypeSendInit && packet.Seq == 0)
            {
                init = packet;
            }
        }

        // Step 2: answer with our parameters. The sender proposes, the receiver agrees or picks the safer choice.
        InitParameters peer = ParseInit(init.Data, init.Data.Length);
        byte replyBlockCheck = peer.BlockCheck;
        byte replyRepeat = settings.RepeatPrefix != 0 ? ValidRepeat(peer.Repeat, peer.Qctl, peer.Qbin) : (byte)0;
        byte replyEightBit;
        byte agreedEightBit;
        if (IsPrefixChar(peer.Qbin) && peer.Qbin != peer.Qctl)
        {
            replyEightBit = (byte)'Y'; // the sender wants to prefix 8-bit bytes: agreed
            agreedEightBit = peer.Qbin;
        }
        else if (peer.Qbin == (byte)'Y' && IsPrefixChar(settings.EightBitPrefix) && settings.EightBitPrefix != peer.Qctl && settings.EightBitPrefix != replyRepeat)
        {
            replyEightBit = settings.EightBitPrefix; // the sender is willing and this side's line is 7-bit: prefix
            agreedEightBit = settings.EightBitPrefix;
        }
        else
        {
            replyEightBit = peer.Qbin == (byte)'Y' && settings.EightBitPrefix == (byte)'Y' ? (byte)'Y' : (byte)'N';
            agreedEightBit = 0;
        }
        int window = OurWindow(settings) > 1 && peer.HasSlidingWindows ? Math.Min(OurWindow(settings), peer.Window) : 1;
        int replyCaps = (peer.HasLongPackets && settings.LongPackets ? CapsLongPackets : 0)
            | (peer.HasAttributes && settings.Attributes ? CapsAttributes : 0)
            | (window > 1 ? CapsSlidingWindows : 0);
        byte[] replyData = BuildInit(settings, replyRepeat != 0 ? replyRepeat : (byte)' ', replyEightBit, replyBlockCheck, replyCaps, window);

        Session session = new Session
        {
            BlockCheck = replyBlockCheck,
            LongPackets = (replyCaps & CapsLongPackets) != 0,
            Attributes = (replyCaps & CapsAttributes) != 0,
            Window = window,
            PadCount = peer.PadCount,
            PadChar = peer.PadChar,
            PeerEol = peer.Eol,
            ReceiveQctl = peer.Qctl,
            ReceiveQbin = agreedEightBit,
            ReceiveRepeat = replyRepeat
        };

        // The ACK of the Send-Init uses the 6-bit check and the normal format, always.
        byte[] initAck = BuildFrame(0, TypeAck, replyData, replyData.Length, (byte)'1', false, session.PadCount, session.PadChar, session.PeerEol);
        link.Send(initAck);

        int expected = 1;                                     // the oldest packet not yet received: the bottom of the window
        Dictionary<int, Packet> ahead = new Dictionary<int, Packet>(); // received (and acknowledged) beyond a gap
        HashSet<int> asked = new HashSet<int>();              // missing packets already asked for with a NAK
        string fileName = null;
        Stream target = null;
        long fileBytes = 0;
        long fileSize = -1;
        DateTime? fileDate = null;
        int consecutiveErrors = 0;
        int consecutiveDamaged = 0;
        bool completed = false;
        bool afterDamage = false; // a damaged packet arrived and nothing good since
        double packetGapMs = 0;   // smoothed time between the starts of two packets
        long lastPacketAt = 0;

        byte[] Frame(int seq, byte type) => BuildFrame(seq, type, null, 0, session.BlockCheck, false, session.PadCount, session.PadChar, session.PeerEol);

        // Closes the file being written and, when it is not complete, gets rid of it (once).
        void DropCurrent()
        {
            Stream stream = target;
            string name = fileName;
            target = null;
            fileName = null;
            try
            {
                stream?.Dispose();
            }
            catch (Exception)
            {
                // The file is being thrown away anyway.
            }
            if (name != null && discardTarget != null)
            {
                try
                {
                    discardTarget(name);
                }
                catch (Exception)
                {
                    // Nothing more can be done about a leftover.
                }
            }
        }

        bool Abort(string text, string status)
        {
            byte[] data = EncodeText(text, (byte)'#', out int length);
            try
            {
                link.Send(BuildFrame(expected, TypeError, data, Math.Min(length, 60), session.BlockCheck, false, session.PadCount, session.PadChar, session.PeerEol));
            }
            catch (Exception)
            {
                // A broken link must not hide the real error.
            }
            long done = fileBytes;
            DropCurrent();
            Report(onProgress, done, fileSize > 0 ? fileSize : 0, errors, status);
            return false;
        }

        // Handles a packet in sequence order. True to go on, false when the transfer has ended (finished or aborted:
        // see completed).
        bool Process(Packet packet)
        {
            switch (packet.Type)
            {
                case TypeFile:
                    {
                        DropCurrent();
                        string name = SanitizeFileName(DecodeText(packet.Data, session.ReceiveQctl));
                        fileBytes = 0;
                        fileSize = -1;
                        fileDate = null;
                        try
                        {
                            target = openTarget(name);
                        }
                        catch (Exception ex)
                        {
                            return Abort("Cannot create the file", "Cannot create " + name + ": " + ex.Message);
                        }
                        if (target == null)
                        {
                            return Abort("Refused", "The file " + name + " was not accepted.");
                        }
                        fileName = name;
                        Report(onProgress, 0, 0, errors, "Receiving " + name);
                        return true;
                    }
                case TypeAttributes:
                    ParseAttributes(packet.Data, out long size, out DateTime? date);
                    if (size >= 0)
                    {
                        fileSize = size;
                    }
                    if (date.HasValue)
                    {
                        fileDate = date;
                    }
                    return true;
                case TypeData:
                    if (target == null)
                    {
                        return Abort("No file", "Transfer failed: data arrived before a file name.");
                    }
                    try
                    {
                        target.Write(packet.Decoded, 0, packet.Decoded.Length);
                    }
                    catch (Exception ex)
                    {
                        return Abort("Cannot write the file", "Cannot write " + fileName + ": " + ex.Message);
                    }
                    fileBytes += packet.Decoded.Length;
                    received += packet.Decoded.Length;
                    Report(onProgress, fileBytes, fileSize > 0 ? fileSize : 0, errors, "Receiving " + fileName);
                    return true;
                case TypeEof:
                    if (packet.Data.Length > 0 && packet.Data[0] == (byte)'D')
                    {
                        DropCurrent(); // the sender abandoned the file
                        return true;
                    }
                    if (fileName != null)
                    {
                        string name = fileName;
                        Stream stream = target;
                        target = null;
                        fileName = null;
                        try
                        {
                            stream.Dispose();
                        }
                        catch (Exception ex)
                        {
                            fileName = name;
                            return Abort("Cannot write the file", "Cannot write " + name + ": " + ex.Message);
                        }
                        filesDone++;
                        onFileReceived?.Invoke(name, fileDate);
                    }
                    return true;
                case TypeBreak:
                    completed = true;
                    Report(onProgress, received, 0, errors, filesDone == 1 ? "File received successfully." : filesDone + " files received successfully.");
                    return false;
                default:
                    return true; // 'X': text the sender wants shown; nothing to do with it here
            }
        }

        try
        {
            while (true)
            {
                if (link.IsCancelled)
                {
                    return Abort("Cancelled by the receiver", "Transfer cancelled.");
                }

                // With a window, a gap (or a damaged packet that may have been the missing one) is asked for again soon
                // when nothing else arrives: waiting for the full timeout would leave the line idle.
                bool quick = session.Window > 1 && (ahead.Count > 0 || afterDamage);
                int quickMs = (int)Math.Min(timeouts.PacketMs, Math.Max(timeouts.WindowRepeatMs, 3 * packetGapMs));
                ReadStatus status = ReadPacket(link, quick ? quickMs : timeouts.PacketMs, timeouts.ByteMs, session.BlockCheck, out Packet packet);
                if (status != ReadStatus.Timeout)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (lastPacketAt != 0)
                    {
                        double gap = (now - lastPacketAt) * 1000.0 / Stopwatch.Frequency;
                        if (gap < timeouts.PacketMs)
                        {
                            packetGapMs = packetGapMs == 0 ? gap : (7 * packetGapMs + gap) / 8;
                        }
                    }
                    lastPacketAt = now;
                }
                if (link.IsCancelled)
                {
                    continue; // reported at the top of the loop
                }
                if (status == ReadStatus.Timeout && quick)
                {
                    afterDamage = false;
                    errors++;
                    asked.Clear();
                    link.Send(Frame(expected, TypeNak));
                    asked.Add(expected);
                    continue;
                }
                if (status != ReadStatus.Packet)
                {
                    errors++;
                    // Silence means the sender may be gone; damaged packets only mean a bad line, which may still get better.
                    if (status == ReadStatus.Timeout ? ++consecutiveErrors > MaxRetries : ++consecutiveDamaged > MaxResendsPerPacket)
                    {
                        return Abort("Too many errors", status == ReadStatus.Timeout
                            ? "Transfer failed: the sender stopped responding."
                            : "Transfer failed: too many damaged packets in a row.");
                    }
                    Report(onProgress, fileBytes, fileSize > 0 ? fileSize : 0, errors, status == ReadStatus.Timeout
                        ? "Timeout waiting for packet " + expected + ", asking again"
                        : "Damaged packet " + expected + ", asking again");
                    if (status == ReadStatus.Damaged && session.Window > 1)
                    {
                        afterDamage = true;
                        // Which packet it was is unknown: the packets that follow show the gap, and each missing one is
                        // asked for then. A NAK for the oldest one here would make the sender repeat the wrong packet.
                        continue;
                    }
                    if (status == ReadStatus.Damaged)
                    {
                        // One packet at a time nothing useful can follow a damaged packet before our answer: drop what is
                        // already here. No need to wait for quiet: a packet starts with MARK, which never occurs inside one.
                        link.Purge(0);
                    }
                    // Ask for the oldest missing packet: it is the one the sender has to repeat first. After a silence the
                    // other missing ones may be asked for again too (their NAKs or repeats may have been lost).
                    asked.Clear();
                    link.Send(Frame(expected, TypeNak));
                    asked.Add(expected);
                    continue;
                }
                consecutiveErrors = 0;
                consecutiveDamaged = 0;
                afterDamage = false;

                if (packet.Type == TypeError)
                {
                    DropCurrent(); // a file that was not finished is not kept
                    Report(onProgress, fileBytes, fileSize > 0 ? fileSize : 0, ++errors, "The sender stopped the transfer: " + DecodeText(packet.Data, session.ReceiveQctl));
                    return false;
                }
                if (packet.Type == TypeAck || packet.Type == TypeNak)
                {
                    continue; // our own answers coming back on a line that echoes, or a late answer: never worth an answer
                }
                if (packet.Type == TypeSendInit && packet.Seq == 0)
                {
                    link.Send(initAck); // the sender did not see our answer
                    continue;
                }

                int distance = (packet.Seq - expected) & 63;
                if (distance >= session.Window)
                {
                    int back = (expected - packet.Seq) & 63;
                    if (back >= 1 && back <= session.Window)
                    {
                        link.Send(Frame(packet.Seq, TypeAck)); // a repeated packet whose ACK was lost: acknowledge it again, take its data once
                    }
                    else
                    {
                        errors++;
                        link.Send(Frame(expected, TypeNak));
                    }
                    continue;
                }

                if (packet.Type == TypeData)
                {
                    using MemoryStream decoded = new MemoryStream(packet.Data.Length * 2);
                    if (!TryDecode(packet.Data, packet.Data.Length, session.ReceiveQctl, session.ReceiveQbin, session.ReceiveRepeat, decoded))
                    {
                        // The block check passed but the data makes no sense: it was damaged after all.
                        errors++;
                        link.Send(Frame(packet.Seq, TypeNak));
                        continue;
                    }
                    packet.Decoded = decoded.ToArray();
                }
                else if (packet.Type != TypeFile && packet.Type != TypeAttributes && packet.Type != TypeEof && packet.Type != TypeBreak && packet.Type != TypeText)
                {
                    errors++;
                    link.Send(Frame(packet.Seq, TypeNak));
                    continue;
                }

                if (distance > 0)
                {
                    // Beyond a gap: keep it, acknowledge it, and ask once for each packet missing before it.
                    if (!ahead.ContainsKey(packet.Seq))
                    {
                        ahead[packet.Seq] = packet;
                        for (int k = 0; k < distance; k++)
                        {
                            int missing = (expected + k) & 63;
                            if (!ahead.ContainsKey(missing) && asked.Add(missing))
                            {
                                link.Send(Frame(missing, TypeNak));
                            }
                        }
                    }
                    link.Send(Frame(packet.Seq, TypeAck));
                    continue;
                }

                // The packet the window was waiting for: handle it, then the ones that were waiting behind it.
                bool goOn = Process(packet);
                if (completed || goOn)
                {
                    link.Send(Frame(expected, TypeAck));
                }
                asked.Remove(expected);
                expected = (expected + 1) & 63;
                while (goOn && ahead.TryGetValue(expected, out Packet waiting))
                {
                    ahead.Remove(expected);
                    asked.Remove(expected);
                    goOn = Process(waiting);
                    expected = (expected + 1) & 63;
                }
                if (!goOn)
                {
                    return completed;
                }
            }
        }
        finally
        {
            if (!completed)
            {
                // Also when the link failed with an exception: the file being written must not stay open or look complete.
                DropCurrent();
            }
        }
    }

    /// <summary>
    /// Reads an attribute packet: the size (tag '1' in bytes, tag '!' in kilobytes; -1 when absent) and the modification
    /// time (tag '#': [yy]yymmdd[ hh:mm[:ss]] in the sender's local time; returned as UTC).
    /// </summary>
    private static void ParseAttributes(byte[] data, out long size, out DateTime? modifiedUtc)
    {
        size = -1;
        modifiedUtc = null;
        int i = 0;
        while (i + 1 < data.Length)
        {
            byte tag = data[i];
            int length = UnChar(data[i + 1]);
            if (length < 0 || i + 2 + length > data.Length)
            {
                break;
            }
            string text = Encoding.ASCII.GetString(data, i + 2, length);
            if ((tag == (byte)'1' || (tag == (byte)'!' && size < 0))
                && long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long value))
            {
                size = tag == (byte)'!' ? value * 1024 : value;
            }
            else if (tag == (byte)'#')
            {
                modifiedUtc = ParseKermitDate(text);
            }
            i += 2 + length;
        }
    }

    private static DateTime? ParseKermitDate(string text)
    {
        string[] formats =
        {
            "yyyyMMdd HH:mm:ss", "yyyyMMdd HH:mm", "yyyyMMdd",
            "yyMMdd HH:mm:ss", "yyMMdd HH:mm", "yyMMdd"
        };
        if (DateTime.TryParseExact(text.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out DateTime local))
        {
            return local.ToUniversalTime();
        }
        return null;
    }

    #endregion
}
