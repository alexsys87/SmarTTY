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
/// counts, long (extended-length) packets and attribute packets (file size for the progress display). Sliding windows
/// are not offered, so the peer falls back to one packet at a time. Several files can be sent or received in a row.
/// The link is given as a "send bytes" callback plus a "read one byte with a timeout" callback, so the protocol logic
/// has no dependency on the terminal (same idea as <see cref="XModemTransfer"/>).
/// </summary>
public class KermitTransfer
{
    public const byte Mark = 0x01; // start of every packet (SOH)

    private const int MaxRetries = 10;
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

    /// <summary>A file to send: its name on the wire, its length (for the progress display) and a way to open it.</summary>
    public sealed class Source
    {
        public Source(string name, long length, Func<Stream> open)
        {
            Name = name;
            Length = length;
            Open = open;
        }

        public string Name { get; }
        public long Length { get; }
        public Func<Stream> Open { get; }
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
        public int MaxExtended; // the most a peer accepts in a long packet, 0 when it did not say

        public bool HasLongPackets => (Capabilities & CapsLongPackets) != 0;
        public bool HasAttributes => (Capabilities & CapsAttributes) != 0;
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

    private static byte[] BuildInit(Settings settings, byte repeat, byte eightBit, byte blockCheck, int capabilities)
    {
        int extended = Math.Min(settings.MaxPacket, MaxExtendedCount);
        byte[] data = new byte[13];
        data[0] = ToChar(MaxShortCount);
        data[1] = ToChar(10);          // TIME: how long the peer should wait for this side
        data[2] = ToChar(0);           // NPAD
        data[3] = Ctl(0);              // PADC
        data[4] = ToChar(DefaultEol);  // EOL
        data[5] = (byte)'#';           // QCTL
        data[6] = eightBit;            // QBIN
        data[7] = blockCheck;          // CHKT
        data[8] = repeat;              // REPT
        data[9] = ToChar(capabilities);
        data[10] = ToChar(0);          // WINDO: no sliding windows
        data[11] = ToChar(extended / 95);
        data[12] = ToChar(extended % 95);
        return data;
    }

    private static int OurCapabilities(Settings settings)
    {
        return (settings.LongPackets ? CapsLongPackets : 0) | (settings.Attributes ? CapsAttributes : 0);
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

        public void Send(byte[] bytes)
        {
            _send(bytes, 0, bytes.Length);
        }

        /// <summary>The next byte, or null when nothing arrives within <paramref name="timeoutMs"/> or the transfer is cancelled.</summary>
        public byte? Read(int timeoutMs)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (true)
            {
                if (_token.IsCancellationRequested)
                {
                    return null;
                }
                int left = timeoutMs - (int)timer.ElapsedMilliseconds;
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
        Stopwatch timer = Stopwatch.StartNew();
        while (true)
        {
            int left = timeoutMs - (int)timer.ElapsedMilliseconds;
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

        byte[] buffer = new byte[FrameBufferSize];
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
    /// (echo, a late answer to an earlier packet) are ignored. A NAK for the next packet counts as the ACK of this one.
    /// </summary>
    private static Exchange Roundtrip(Link link, byte[] frame, int seq, byte blockCheck, Timeouts timeouts, ref int errors,
        out Packet reply, out string message)
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
                if (packet.Type == TypeNak && packet.Seq == ((seq + 1) & 63))
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
        byte[] initData = BuildInit(settings, ourRepeat != 0 ? ourRepeat : (byte)' ', settings.EightBitPrefix, settings.BlockCheck, OurCapabilities(settings));
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

        byte[] raw = new byte[4096];
        byte[] encoded = new byte[capacity + 8];

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
            Exchange result = Roundtrip(link, frame, seq, session.BlockCheck, timeouts, ref errors, out reply, out message);
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

                if (session.Attributes)
                {
                    // Tag '1' = the length in bytes (the receiver can show a progress bar), tag '"' = the file type: "B8" is
                    // binary, 8 bits, so a receiver that would otherwise treat the file as text does not touch its line ends.
                    // An attribute the receiver rejects is not worth failing for, so only a dead line ends the transfer here.
                    string length = Math.Max(0, file.Length).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    byte[] attributes = new byte[2 + length.Length + 4];
                    attributes[0] = (byte)'1';
                    attributes[1] = ToChar(length.Length);
                    Encoding.ASCII.GetBytes(length, 0, length.Length, attributes, 2);
                    int at = 2 + length.Length;
                    attributes[at++] = (byte)'"';
                    attributes[at++] = ToChar(2);
                    attributes[at++] = (byte)'B';
                    attributes[at++] = (byte)'8';
                    result = Send(TypeAttributes, attributes, attributes.Length, out reply, out message);
                    if (Failed(result, message, "attributes", doneBytes))
                    {
                        return false;
                    }
                }

                int rawPos = 0;
                int rawLength = 0;
                bool endOfFile = false;
                bool skipFile = false;
                long fileDone = 0;
                while (!skipFile)
                {
                    int encodedLength = 0;
                    long consumed = 0;
                    try
                    {
                        while (encodedLength + 5 <= capacity)
                        {
                            if (rawPos == rawLength)
                            {
                                if (endOfFile) break;
                                rawLength = stream.Read(raw, 0, raw.Length);
                                rawPos = 0;
                                if (rawLength <= 0)
                                {
                                    rawLength = 0;
                                    endOfFile = true;
                                    break;
                                }
                            }
                            byte b = raw[rawPos];
                            int run = 1;
                            if (session.Repeat != 0)
                            {
                                while (run < 94 && rawPos + run < rawLength && raw[rawPos + run] == b)
                                {
                                    run++;
                                }
                            }
                            if (session.Repeat != 0 && run > 3)
                            {
                                encoded[encodedLength++] = session.Repeat;
                                encoded[encodedLength++] = ToChar(run);
                                encodedLength = EncodeByte(b, session.Qctl, session.Qbin, session.Repeat, encoded, encodedLength);
                                rawPos += run;
                                consumed += run;
                            }
                            else
                            {
                                encodedLength = EncodeByte(b, session.Qctl, session.Qbin, session.Repeat, encoded, encodedLength);
                                rawPos++;
                                consumed++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SendError("Cannot read the file");
                        Report(onProgress, doneBytes, totalBytes, ++errors, "Cannot read " + wireName + ": " + ex.Message);
                        return false;
                    }
                    if (encodedLength == 0)
                    {
                        break; // end of this file
                    }

                    result = Send(TypeData, encoded, encodedLength, out reply, out message);
                    if (Failed(result, message, "data", doneBytes))
                    {
                        return false;
                    }
                    doneBytes += consumed;
                    fileDone += consumed;
                    Report(onProgress, doneBytes, totalBytes, errors, "Sending " + wireName + where);

                    // 'X' in the ACK: skip the rest of this file; 'Z': skip this and all remaining files.
                    if (reply.Data.Length > 0 && (reply.Data[0] == (byte)'X' || reply.Data[0] == (byte)'Z'))
                    {
                        skipFile = true;
                        skipAll = reply.Data[0] == (byte)'Z';
                    }
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
    /// <paramref name="discardTarget"/> is called with the same name when the sender abandons a file half-way.
    /// </summary>
    public static bool ReceiveFiles(
        Func<string, Stream> openTarget,
        Action<string> discardTarget,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress)
    {
        return ReceiveFiles(openTarget, discardTarget, sendData, readByteWithTimeout, cancellationToken, onProgress, new Settings(), new Timeouts());
    }

    internal static bool ReceiveFiles(
        Func<string, Stream> openTarget,
        Action<string> discardTarget,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress,
        Settings settings,
        Timeouts timeouts)
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

        InitParameters peer = ParseInit(init.Data, init.Data.Length);
        byte replyBlockCheck = peer.BlockCheck;
        byte replyRepeat = ValidRepeat(peer.Repeat, peer.Qctl, peer.Qbin);
        byte replyEightBit = IsPrefixChar(peer.Qbin) && peer.Qbin != peer.Qctl ? (byte)'Y' : peer.Qbin == (byte)'Y' ? (byte)'Y' : (byte)'N';
        byte agreedEightBit = IsPrefixChar(peer.Qbin) && peer.Qbin != peer.Qctl ? peer.Qbin : (byte)0;
        int replyCaps = (peer.HasLongPackets && settings.LongPackets ? CapsLongPackets : 0)
            | (peer.HasAttributes && settings.Attributes ? CapsAttributes : 0);
        byte[] replyData = BuildInit(settings, replyRepeat != 0 ? replyRepeat : (byte)' ', replyEightBit, replyBlockCheck, replyCaps);

        Session session = new Session
        {
            BlockCheck = replyBlockCheck,
            LongPackets = (replyCaps & CapsLongPackets) != 0,
            Attributes = (replyCaps & CapsAttributes) != 0,
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
        byte[] lastAck = initAck;

        int expected = 1;
        string fileName = null;
        Stream target = null;
        long fileBytes = 0;
        long fileSize = -1;
        int consecutiveErrors = 0;

        void CloseTarget()
        {
            target?.Dispose();
            target = null;
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
            CloseTarget();
            if (fileName != null && discardTarget != null)
            {
                try
                {
                    discardTarget(fileName);
                }
                catch (Exception)
                {
                }
            }
            Report(onProgress, fileBytes, fileSize > 0 ? fileSize : 0, errors, status);
            return false;
        }

        while (true)
        {
            if (link.IsCancelled)
            {
                return Abort("Cancelled by the receiver", "Transfer cancelled.");
            }

            ReadStatus status = ReadPacket(link, timeouts.PacketMs, timeouts.ByteMs, session.BlockCheck, out Packet packet);
            if (link.IsCancelled)
            {
                continue; // reported at the top of the loop
            }
            if (status != ReadStatus.Packet)
            {
                errors++;
                if (++consecutiveErrors > MaxRetries)
                {
                    return Abort("Too many errors", "Transfer failed: the sender stopped responding.");
                }
                Report(onProgress, fileBytes, fileSize > 0 ? fileSize : 0, errors, status == ReadStatus.Timeout
                    ? "Timeout waiting for packet " + expected + ", asking again"
                    : "Damaged packet " + expected + ", asking again");
                if (status == ReadStatus.Damaged)
                {
                    link.Purge(200); // the rest of a damaged packet must not be taken for the start of the next one
                }
                link.Send(BuildFrame(expected, TypeNak, null, 0, session.BlockCheck, false, session.PadCount, session.PadChar, session.PeerEol));
                continue;
            }
            consecutiveErrors = 0;

            if (packet.Type == TypeError)
            {
                CloseTarget();
                if (fileName != null && discardTarget != null)
                {
                    discardTarget(fileName); // a file that was not finished is not kept
                }
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
            if (packet.Seq == ((expected + 63) & 63))
            {
                link.Send(lastAck); // a repeated packet: acknowledge it again, take its data once
                continue;
            }
            if (packet.Seq != expected)
            {
                errors++;
                link.Send(BuildFrame(expected, TypeNak, null, 0, session.BlockCheck, false, session.PadCount, session.PadChar, session.PeerEol));
                continue;
            }

            byte[] ackData = new byte[0];
            bool finished = false;
            switch (packet.Type)
            {
                case TypeFile:
                    {
                        CloseTarget();
                        string name = SanitizeFileName(DecodeText(packet.Data, session.ReceiveQctl));
                        fileName = name;
                        fileBytes = 0;
                        fileSize = -1;
                        try
                        {
                            target = openTarget(name);
                        }
                        catch (Exception ex)
                        {
                            fileName = null;
                            return Abort("Cannot create the file", "Cannot create " + name + ": " + ex.Message);
                        }
                        if (target == null)
                        {
                            fileName = null;
                            return Abort("Refused", "The file " + name + " was not accepted.");
                        }
                        Report(onProgress, 0, 0, errors, "Receiving " + name);
                        break;
                    }
                case TypeAttributes:
                    {
                        long size = ParseSizeAttribute(packet.Data, session.ReceiveQctl);
                        if (size >= 0)
                        {
                            fileSize = size;
                        }
                        break;
                    }
                case TypeData:
                    {
                        if (target == null)
                        {
                            return Abort("No file", "Transfer failed: data arrived before a file name.");
                        }
                        try
                        {
                            using MemoryStream decoded = new MemoryStream();
                            if (!TryDecode(packet.Data, packet.Data.Length, session.ReceiveQctl, session.ReceiveQbin, session.ReceiveRepeat, decoded))
                            {
                                errors++;
                                link.Send(BuildFrame(expected, TypeNak, null, 0, session.BlockCheck, false, session.PadCount, session.PadChar, session.PeerEol));
                                continue;
                            }
                            decoded.WriteTo(target);
                            fileBytes += decoded.Length;
                            received += decoded.Length;
                        }
                        catch (Exception ex)
                        {
                            return Abort("Cannot write the file", "Cannot write " + fileName + ": " + ex.Message);
                        }
                        Report(onProgress, fileBytes, fileSize > 0 ? fileSize : 0, errors, "Receiving " + fileName);
                        break;
                    }
                case TypeEof:
                    {
                        bool discard = packet.Data.Length > 0 && packet.Data[0] == (byte)'D';
                        CloseTarget();
                        if (discard)
                        {
                            if (fileName != null && discardTarget != null)
                            {
                                discardTarget(fileName);
                            }
                        }
                        else if (fileName != null)
                        {
                            filesDone++;
                        }
                        fileName = null;
                        break;
                    }
                case TypeBreak:
                    finished = true;
                    break;
                case TypeText:
                    break; // text the sender wants shown; nothing to do with it here
                default:
                    errors++;
                    link.Send(BuildFrame(expected, TypeNak, null, 0, session.BlockCheck, false, session.PadCount, session.PadChar, session.PeerEol));
                    continue;
            }

            lastAck = BuildFrame(expected, TypeAck, ackData, ackData.Length, session.BlockCheck, false, session.PadCount, session.PadChar, session.PeerEol);
            link.Send(lastAck);
            expected = (expected + 1) & 63;
            if (finished)
            {
                Report(onProgress, received, 0, errors, filesDone == 1 ? "File received successfully." : filesDone + " files received successfully.");
                return true;
            }
        }
    }

    /// <summary>The announced file size from an attribute packet: tag '1' is bytes, tag '!' is kilobytes. -1 when absent.</summary>
    private static long ParseSizeAttribute(byte[] data, byte qctl)
    {
        int i = 0;
        while (i + 1 < data.Length)
        {
            byte tag = data[i];
            int length = UnChar(data[i + 1]);
            if (length < 0 || i + 2 + length > data.Length)
            {
                break;
            }
            if (tag == (byte)'1' || tag == (byte)'!')
            {
                string text = Encoding.ASCII.GetString(data, i + 2, length);
                if (long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long value))
                {
                    return tag == (byte)'!' ? value * 1024 : value;
                }
            }
            i += 2 + length;
        }
        return -1;
    }

    #endregion
}
