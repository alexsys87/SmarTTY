using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SmarTTY.IO;

/// <summary>
/// XMODEM (checksum and CRC-16, 128-byte blocks) and XMODEM-1K (CRC-16, 1024-byte blocks) over an arbitrary byte link.
/// The link is given as a "send bytes" callback plus a "read one byte with a timeout" callback, so the protocol logic
/// has no dependency on the terminal. The sender uses 1K blocks (STX) on request; the receiver always understands them.
/// </summary>
public class XModemTransfer
{
    public const byte SOH = 0x01; // Start of Header (128-byte block)
    public const byte STX = 0x02; // Start of Header (1024-byte block, XMODEM-1K; needs CRC-16)
    public const byte EOT = 0x04; // End of Transmission
    public const byte ACK = 0x06; // Acknowledge
    public const byte NAK = 0x15; // Negative Acknowledge (Checksum mode)
    public const byte CAN = 0x18; // Cancel (two in a row abort the transfer)
    public const byte C   = 0x43; // 'C' (CRC16 mode)
    public const byte SUB = 0x1A; // Padding character

    private const int BlockSize = 128;
    private const int LargeBlockSize = 1024;
    private const int MaxRetries = 10;

    public enum XModemMode
    {
        Checksum,
        Crc16
    }

    public delegate void ProgressHandler(long bytesTransferred, long totalBytes, int errorCount, string status);
    public delegate void SendDataHandler(byte[] data, int offset, int length);

    /// <summary>The waiting times of the protocol; tests shorten them.</summary>
    internal sealed class Timeouts
    {
        /// <summary>How long a sender waits for the receiver's first 'C' or NAK, ignoring everything else.</summary>
        public int SenderHandshakeMs = 90000;

        /// <summary>How long a sender waits for ACK/NAK after a block or EOT.</summary>
        public int AckMs = 3000;

        /// <summary>How long a receiver waits for the start of a block after each 'C' / NAK it sends while starting.</summary>
        public int ReceiverHandshakeAttemptMs = 3000;

        /// <summary>How long a receiver waits for the next block (or EOT) once the transfer runs.</summary>
        public int HeaderMs = 10000;

        /// <summary>The gap allowed between two bytes inside a block.</summary>
        public int ByteMs = 1000;

        /// <summary>Quiet time on the line that ends the discarding of a damaged block.</summary>
        public int PurgeQuietMs = 300;
    }

    public static ushort CalculateCrc16(byte[] data, int offset, int length)
    {
        ushort crc = 0;
        for (int i = offset; i < offset + length; i++)
        {
            crc = (ushort)(crc ^ ((ushort)data[i] << 8));
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x8000) != 0)
                {
                    crc = (ushort)((crc << 1) ^ 0x1021);
                }
                else
                {
                    crc = (ushort)(crc << 1);
                }
            }
        }
        return crc;
    }

    public static byte CalculateChecksum(byte[] data, int offset, int length)
    {
        byte checksum = 0;
        for (int i = offset; i < offset + length; i++)
        {
            checksum = (byte)(checksum + data[i]);
        }
        return checksum;
    }

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

        public void Send(params byte[] bytes)
        {
            _send(bytes, 0, bytes.Length);
        }

        public void Send(byte[] bytes, int length)
        {
            _send(bytes, 0, length);
        }

        /// <summary>Tells the peer to give up (two CAN); best effort, a broken link must not hide the real error.</summary>
        public void SendCancel()
        {
            try
            {
                Send(CAN, CAN);
            }
            catch (Exception)
            {
            }
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

    private static void Report(ProgressHandler onProgress, long done, long total, int errors, string status)
    {
        onProgress?.Invoke(done, total, errors, status);
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

    private enum Reply
    {
        Ack,
        Nak,
        Timeout,
        Cancelled
    }

    /// <summary>Waits for the answer to a block or EOT. Other bytes (a late 'C', echo, noise) are ignored.</summary>
    private static Reply WaitForReply(Link link, int timeoutMs)
    {
        Stopwatch timer = Stopwatch.StartNew();
        bool previousWasCan = false;
        while (true)
        {
            int left = timeoutMs - (int)timer.ElapsedMilliseconds;
            if (left <= 0)
            {
                return Reply.Timeout;
            }
            byte? b = link.Read(left);
            if (!b.HasValue)
            {
                return Reply.Timeout;
            }
            if (b.Value == ACK)
            {
                return Reply.Ack;
            }
            if (b.Value == NAK)
            {
                return Reply.Nak;
            }
            if (b.Value == CAN)
            {
                if (previousWasCan)
                {
                    return Reply.Cancelled;
                }
                previousWasCan = true;
                continue;
            }
            previousWasCan = false;
        }
    }

    public static bool SendFile(
        Stream sourceStream,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress)
    {
        return SendFile(sourceStream, sendData, readByteWithTimeout, cancellationToken, onProgress, false, new Timeouts());
    }

    /// <summary>
    /// Sends a file; with <paramref name="useLargeBlocks"/> as XMODEM-1K (1024-byte STX blocks). XMODEM-1K needs CRC-16,
    /// so when the receiver asks for the checksum variant (NAK) the file goes out in 128-byte blocks as usual.
    /// </summary>
    public static bool SendFile(
        Stream sourceStream,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress,
        bool useLargeBlocks)
    {
        return SendFile(sourceStream, sendData, readByteWithTimeout, cancellationToken, onProgress, useLargeBlocks, new Timeouts());
    }

    internal static bool SendFile(
        Stream sourceStream,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress,
        Timeouts timeouts)
    {
        return SendFile(sourceStream, sendData, readByteWithTimeout, cancellationToken, onProgress, false, timeouts);
    }

    internal static bool SendFile(
        Stream sourceStream,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress,
        bool useLargeBlocks,
        Timeouts timeouts)
    {
        if (sourceStream == null) throw new ArgumentNullException(nameof(sourceStream));
        if (sendData == null) throw new ArgumentNullException(nameof(sendData));
        if (readByteWithTimeout == null) throw new ArgumentNullException(nameof(readByteWithTimeout));

        Link link = new Link(sendData, readByteWithTimeout, cancellationToken);
        long totalBytes = sourceStream.CanSeek ? sourceStream.Length : 0;
        long bytesTransferred = 0;
        int errorCount = 0;

        // Step 1: wait for the receiver's initial request: 'C' (CRC-16) or NAK (checksum). Anything else is ignored,
        // so a banner or the echo of the command that started the receiver does not spoil the handshake.
        XModemMode mode = XModemMode.Checksum;
        bool started = false;
        bool previousWasCan = false;
        Stopwatch handshakeTimer = Stopwatch.StartNew();
        Report(onProgress, 0, totalBytes, errorCount, "Waiting for the receiver...");

        while (!started && handshakeTimer.ElapsedMilliseconds < timeouts.SenderHandshakeMs)
        {
            byte? b = link.Read(1000);
            if (link.IsCancelled)
            {
                link.SendCancel();
                Report(onProgress, 0, totalBytes, errorCount, "Transfer cancelled.");
                return false;
            }
            if (!b.HasValue)
            {
                continue;
            }
            if (b.Value == CAN)
            {
                if (previousWasCan)
                {
                    Report(onProgress, 0, totalBytes, errorCount, "Cancelled by the receiver.");
                    return false;
                }
                previousWasCan = true;
                continue;
            }
            previousWasCan = false;
            if (b.Value == C)
            {
                mode = XModemMode.Crc16;
                started = true;
            }
            else if (b.Value == NAK)
            {
                mode = XModemMode.Checksum;
                started = true;
            }
        }

        if (!started)
        {
            Report(onProgress, 0, totalBytes, ++errorCount, "Handshake timeout: the receiver did not ask for data (NAK or 'C').");
            return false;
        }

        // The receiver may have repeated its request while we were busy; those copies must not be mistaken for answers.
        link.Purge(0);

        // XMODEM-1K is defined for CRC-16 only; a receiver that asked for the checksum variant gets 128-byte blocks.
        bool largeBlocks = useLargeBlocks && mode == XModemMode.Crc16;
        if (useLargeBlocks && !largeBlocks)
        {
            Report(onProgress, 0, totalBytes, errorCount, "The receiver asked for checksum mode: 1K blocks need CRC-16, using 128-byte blocks.");
        }

        int trailerSize = mode == XModemMode.Crc16 ? 2 : 1;
        int maxPayload = largeBlocks ? LargeBlockSize : BlockSize;
        byte[] payload = new byte[LargeBlockSize];
        byte[] packet = new byte[3 + LargeBlockSize + trailerSize];
        int blockIndex = 1; // 1-based count for the display; only its low byte goes on the wire

        while (true)
        {
            int read;
            try
            {
                read = ReadFully(sourceStream, payload, maxPayload);
            }
            catch (Exception ex)
            {
                link.SendCancel();
                Report(onProgress, bytesTransferred, totalBytes, ++errorCount, "Cannot read the file: " + ex.Message);
                return false;
            }
            if (read == 0)
            {
                break; // End of the data (an empty file is just EOT)
            }

            // In 1K mode a tail of up to 128 bytes goes out as an ordinary 128-byte block (SOH) instead of being padded
            // to 1024 bytes; every other block, including a longer short one, is a full 1K block (STX).
            // Both kinds of block share one sequence of block numbers.
            int size = read > BlockSize ? LargeBlockSize : BlockSize;
            for (int i = read; i < size; i++)
            {
                payload[i] = SUB; // Only the last block is short
            }

            byte blockByte = (byte)blockIndex;
            packet[0] = size == LargeBlockSize ? STX : SOH;
            packet[1] = blockByte;
            packet[2] = (byte)(255 - blockByte);
            Buffer.BlockCopy(payload, 0, packet, 3, size);
            if (mode == XModemMode.Crc16)
            {
                ushort crc = CalculateCrc16(payload, 0, size);
                packet[3 + size] = (byte)(crc >> 8);
                packet[3 + size + 1] = (byte)(crc & 0xFF);
            }
            else
            {
                packet[3 + size] = CalculateChecksum(payload, 0, size);
            }
            int packetLength = 3 + size + trailerSize;

            int attempts = 0;
            while (true)
            {
                if (link.IsCancelled)
                {
                    link.SendCancel();
                    Report(onProgress, bytesTransferred, totalBytes, errorCount, "Transfer cancelled.");
                    return false;
                }

                link.Send(packet, packetLength);
                attempts++;
                Reply reply = WaitForReply(link, timeouts.AckMs);

                if (reply == Reply.Ack)
                {
                    bytesTransferred += read;
                    Report(onProgress, bytesTransferred, totalBytes, errorCount, "Sent block " + blockIndex);
                    break;
                }
                if (reply == Reply.Cancelled)
                {
                    Report(onProgress, bytesTransferred, totalBytes, errorCount, "Cancelled by the receiver.");
                    return false;
                }
                if (link.IsCancelled)
                {
                    continue; // reported at the top of the loop
                }

                errorCount++;
                if (attempts >= MaxRetries)
                {
                    link.SendCancel();
                    Report(onProgress, bytesTransferred, totalBytes, errorCount,
                        "Transfer failed: no valid answer to block " + blockIndex + " after " + attempts + " attempts.");
                    return false;
                }
                Report(onProgress, bytesTransferred, totalBytes, errorCount, "Retrying block " + blockIndex + " (attempt " + (attempts + 1) + ")");
            }

            blockIndex++;
        }

        // Send EOT; some receivers NAK the first one, so it is repeated until it is acknowledged.
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            if (link.IsCancelled)
            {
                link.SendCancel();
                Report(onProgress, bytesTransferred, totalBytes, errorCount, "Transfer cancelled.");
                return false;
            }

            link.Send(EOT);
            Reply reply = WaitForReply(link, timeouts.AckMs);
            if (reply == Reply.Ack)
            {
                Report(onProgress, bytesTransferred, totalBytes, errorCount, "Transfer complete.");
                return true;
            }
            if (reply == Reply.Cancelled)
            {
                Report(onProgress, bytesTransferred, totalBytes, errorCount, "Cancelled by the receiver.");
                return false;
            }
            if (!link.IsCancelled)
            {
                errorCount++;
            }
        }

        link.SendCancel();
        Report(onProgress, bytesTransferred, totalBytes, errorCount, "Transfer failed: the end of the file was not acknowledged.");
        return false;
    }

    /// <summary>
    /// Waits for the start of a block: SOH, STX or EOT. Other bytes are ignored. A pair of CAN is returned as CAN;
    /// null means nothing useful arrived in time (or the transfer was cancelled).
    /// </summary>
    private static byte? ReadHeader(Link link, int timeoutMs)
    {
        Stopwatch timer = Stopwatch.StartNew();
        bool previousWasCan = false;
        while (true)
        {
            int left = timeoutMs - (int)timer.ElapsedMilliseconds;
            if (left <= 0)
            {
                return null;
            }
            byte? b = link.Read(left);
            if (!b.HasValue)
            {
                return null;
            }
            if (b.Value == SOH || b.Value == STX || b.Value == EOT)
            {
                return b;
            }
            if (b.Value == CAN)
            {
                if (previousWasCan)
                {
                    return CAN;
                }
                previousWasCan = true;
                continue;
            }
            previousWasCan = false;
        }
    }

    /// <summary>Reads the rest of a block after its start byte. False when it is incomplete or damaged.</summary>
    private static bool TryReadBlock(Link link, int size, XModemMode mode, int byteTimeoutMs, byte[] payload, out byte blockNumber)
    {
        blockNumber = 0;
        byte? number = link.Read(byteTimeoutMs);
        byte? complement = link.Read(byteTimeoutMs);
        if (!number.HasValue || !complement.HasValue || (byte)(number.Value + complement.Value) != 255)
        {
            return false;
        }
        for (int i = 0; i < size; i++)
        {
            byte? b = link.Read(byteTimeoutMs);
            if (!b.HasValue)
            {
                return false;
            }
            payload[i] = b.Value;
        }

        if (mode == XModemMode.Crc16)
        {
            byte? hi = link.Read(byteTimeoutMs);
            byte? lo = link.Read(byteTimeoutMs);
            if (!hi.HasValue || !lo.HasValue || (ushort)((hi.Value << 8) | lo.Value) != CalculateCrc16(payload, 0, size))
            {
                return false;
            }
        }
        else
        {
            byte? sum = link.Read(byteTimeoutMs);
            if (!sum.HasValue || sum.Value != CalculateChecksum(payload, 0, size))
            {
                return false;
            }
        }

        blockNumber = number.Value;
        return true;
    }

    public static bool ReceiveFile(
        Stream targetStream,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress)
    {
        return ReceiveFile(targetStream, sendData, readByteWithTimeout, cancellationToken, onProgress, new Timeouts());
    }

    internal static bool ReceiveFile(
        Stream targetStream,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress,
        Timeouts timeouts)
    {
        if (targetStream == null) throw new ArgumentNullException(nameof(targetStream));
        if (sendData == null) throw new ArgumentNullException(nameof(sendData));
        if (readByteWithTimeout == null) throw new ArgumentNullException(nameof(readByteWithTimeout));

        Link link = new Link(sendData, readByteWithTimeout, cancellationToken);
        long bytesReceived = 0; // the size of the file is not known to a receiver, so progress is reported without a total
        int errorCount = 0;
        int blocksAccepted = 0;
        byte expectedBlock = 1;
        XModemMode mode = XModemMode.Crc16;
        byte? header = null;

        // Step 1: ask for the file: 'C' first (CRC-16), then NAK (checksum) for senders that do not know CRC.
        // Text that arrives meanwhile (a banner, an echo) is skipped until the first block starts.
        link.Purge(0);
        Report(onProgress, 0, 0, errorCount, "Waiting for the sender...");
        for (int phase = 0; phase < 2 && !header.HasValue; phase++)
        {
            mode = phase == 0 ? XModemMode.Crc16 : XModemMode.Checksum;
            byte request = phase == 0 ? C : NAK;
            for (int attempt = 0; attempt < MaxRetries && !header.HasValue; attempt++)
            {
                if (link.IsCancelled)
                {
                    link.SendCancel();
                    Report(onProgress, 0, 0, errorCount, "Transfer cancelled.");
                    return false;
                }
                link.Send(request);
                header = ReadHeader(link, timeouts.ReceiverHandshakeAttemptMs);
            }
        }

        if (link.IsCancelled)
        {
            link.SendCancel();
            Report(onProgress, 0, 0, errorCount, "Transfer cancelled.");
            return false;
        }
        if (!header.HasValue)
        {
            Report(onProgress, 0, 0, ++errorCount, "The sender did not respond to the initial handshake.");
            return false;
        }

        // Step 2: receive blocks until EOT.
        byte[] payload = new byte[LargeBlockSize];
        int consecutiveErrors = 0;

        while (true)
        {
            if (link.IsCancelled)
            {
                link.SendCancel();
                Report(onProgress, bytesReceived, 0, errorCount, "Transfer cancelled.");
                return false;
            }

            if (!header.HasValue)
            {
                header = ReadHeader(link, timeouts.HeaderMs);
                if (link.IsCancelled)
                {
                    continue; // reported at the top of the loop
                }
            }

            if (!header.HasValue)
            {
                // Nothing arrived: nudge the sender (a NAK makes it repeat its last block).
                errorCount++;
                if (++consecutiveErrors > MaxRetries)
                {
                    link.SendCancel();
                    Report(onProgress, bytesReceived, 0, errorCount, "Transfer failed: the sender stopped responding.");
                    return false;
                }
                Report(onProgress, bytesReceived, 0, errorCount, "Timeout waiting for block " + (blocksAccepted + 1) + ", asking again");
                link.Send(NAK);
                continue;
            }

            byte start = header.Value;
            header = null;

            if (start == EOT)
            {
                link.Send(ACK);
                Report(onProgress, bytesReceived, 0, errorCount, "File received successfully.");
                return true;
            }
            if (start == CAN)
            {
                Report(onProgress, bytesReceived, 0, errorCount, "Cancelled by the sender.");
                return false;
            }

            int size = start == STX ? LargeBlockSize : BlockSize;
            byte blockNumber;
            if (!TryReadBlock(link, size, mode, timeouts.ByteMs, payload, out blockNumber))
            {
                if (link.IsCancelled)
                {
                    continue; // reported at the top of the loop
                }
                errorCount++;
                if (++consecutiveErrors > MaxRetries)
                {
                    link.SendCancel();
                    Report(onProgress, bytesReceived, 0, errorCount, "Transfer failed: too many damaged blocks.");
                    return false;
                }
                // Drop the rest of the damaged block (and anything else in flight) before asking for it again,
                // otherwise leftovers would be taken for the start of the next block.
                link.Purge(timeouts.PurgeQuietMs);
                Report(onProgress, bytesReceived, 0, errorCount, "Damaged block " + (blocksAccepted + 1) + ", asking again");
                link.Send(NAK);
                continue;
            }

            consecutiveErrors = 0;
            if (blockNumber == expectedBlock)
            {
                try
                {
                    targetStream.Write(payload, 0, size);
                }
                catch (Exception ex)
                {
                    link.SendCancel();
                    Report(onProgress, bytesReceived, 0, ++errorCount, "Cannot write the file: " + ex.Message);
                    return false;
                }
                bytesReceived += size;
                blocksAccepted++;
                expectedBlock++;
                link.Send(ACK);
                Report(onProgress, bytesReceived, 0, errorCount, "Received block " + blocksAccepted);
            }
            else if (blocksAccepted > 0 && blockNumber == (byte)(expectedBlock - 1))
            {
                // The sender repeated a block whose ACK it did not see: acknowledge it again, but keep the data once.
                link.Send(ACK);
                Report(onProgress, bytesReceived, 0, errorCount, "Repeated block " + blocksAccepted + " acknowledged");
            }
            else
            {
                // A gap or a jump back means data is lost; acknowledging it would silently produce a corrupt file.
                link.SendCancel();
                Report(onProgress, bytesReceived, 0, ++errorCount,
                    "Transfer failed: block " + blockNumber + " arrived while block " + expectedBlock + " was expected.");
                return false;
            }
        }
    }
}
