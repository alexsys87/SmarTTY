using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SmarTTY.IO;

/// <summary>
/// YMODEM batch transfer over an arbitrary byte link (the same "send bytes" / "read one byte with a timeout" callbacks as
/// <see cref="XModemTransfer"/>, whose block, CRC-16 and link code it shares).
///
/// A transfer is a sequence of files. Every file starts with a header block (block 0) that carries the file name and
/// size, followed by the data as numbered XMODEM-1K blocks (CRC-16 only) and an EOT; the batch ends with an empty
/// header block (block 0 with no file name). The receiver drives the handshake: it asks for each block 0 and for the
/// data of each file with 'C', and answers the first EOT of a file with NAK (the second one with ACK).
///
/// Not supported: YMODEM-G (streaming, 'G' handshake) and the checksum variant (receiver NAK instead of 'C').
/// </summary>
public class YModemTransfer
{
    private const byte SOH = XModemTransfer.SOH;
    private const byte STX = XModemTransfer.STX;
    private const byte EOT = XModemTransfer.EOT;
    private const byte ACK = XModemTransfer.ACK;
    private const byte NAK = XModemTransfer.NAK;
    private const byte CAN = XModemTransfer.CAN;
    private const byte C = XModemTransfer.C;

    private const int BlockSize = XModemTransfer.BlockSize;
    private const int LargeBlockSize = XModemTransfer.LargeBlockSize;
    private const int MaxRetries = XModemTransfer.MaxRetries;
    private const int EndOfBatchAttempts = 3; // the end-of-batch block carries no data, so it is not worth a long wait

    private const XModemTransfer.XModemMode Crc = XModemTransfer.XModemMode.Crc16;

    /// <summary>A file to be sent: a seekable stream (the size goes into the header) and the name the receiver will see.</summary>
    public sealed class FileToSend
    {
        public FileToSend(string name, Stream stream, DateTime? modifiedUtc = null)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanSeek)
            {
                throw new ArgumentException("YMODEM sends the file size in advance, so the stream must be seekable.", nameof(stream));
            }
            Name = name;
            Stream = stream;
            Length = stream.Length - stream.Position;
            ModifiedUtc = modifiedUtc;
        }

        public string Name { get; }

        public Stream Stream { get; }

        /// <summary>The number of bytes that will be sent: from the position the stream had when it was handed over.</summary>
        public long Length { get; }

        public DateTime? ModifiedUtc { get; }
    }

    /// <summary>What a sender announced in the header block of a file.</summary>
    public sealed class FileHeader
    {
        /// <summary>1-based position of the file in the batch.</summary>
        public int Index { get; set; }

        /// <summary>The name exactly as sent. May contain a path or characters a file system rejects: never use it to build a path.</summary>
        public string RawName { get; set; }

        /// <summary>The name reduced to a plain, safe file name (see <see cref="SanitizeFileName"/>).</summary>
        public string Name { get; set; }

        /// <summary>The size in bytes, or -1 when the sender did not say (then the padding of the last block cannot be cut off).</summary>
        public long Size { get; set; }

        public DateTime? ModifiedUtc { get; set; }
    }

    // ---------- file names and the header block ----------

    private static readonly char[] ForbiddenInFileName = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    private static readonly string[] ReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private const int MaxFileNameLength = 128;

    /// <summary>
    /// Reduces a name received from a peer (or about to be sent to one) to a plain file name: any directory part is cut
    /// off, characters that Windows rejects and control characters become '_', trailing dots and spaces go, a reserved
    /// device name (CON, NUL, COM1...) gets a '_' in front and the length is limited. The result never contains a
    /// path separator and is never "." or "..", so a peer cannot make the receiver write outside the chosen folder.
    /// Behaves the same on every OS, so it can be tested anywhere.
    /// </summary>
    public static string SanitizeFileName(string name, string fallback = "file")
    {
        if (string.IsNullOrEmpty(name))
        {
            return fallback;
        }

        int cut = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        string tail = cut >= 0 ? name.Substring(cut + 1) : name;

        StringBuilder sb = new StringBuilder(tail.Length);
        foreach (char ch in tail)
        {
            bool bad = ch < 0x20 || ch == 0x7F || Array.IndexOf(ForbiddenInFileName, ch) >= 0;
            sb.Append(bad ? '_' : ch);
        }
        string result = sb.ToString().TrimStart(' ').TrimEnd(' ', '.');
        if (result.Length == 0)
        {
            return fallback;
        }

        int dot = result.IndexOf('.');
        string stem = (dot >= 0 ? result.Substring(0, dot) : result).TrimEnd(' ');
        if (Array.IndexOf(ReservedDeviceNames, stem.ToUpperInvariant()) >= 0)
        {
            result = "_" + result;
        }

        if (result.Length > MaxFileNameLength)
        {
            int extensionAt = result.LastIndexOf('.');
            int extensionLength = extensionAt > 0 ? result.Length - extensionAt : 0;
            result = extensionLength > 0 && extensionLength <= 16
                ? result.Substring(0, MaxFileNameLength - extensionLength) + result.Substring(extensionAt)
                : result.Substring(0, MaxFileNameLength);
        }
        return result;
    }

    private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The payload of block 0 for a file: "name NUL size modification-time(octal Unix seconds)", zero padded to 128 bytes,
    /// or to 1024 when the name is too long for that.
    /// </summary>
    internal static byte[] BuildHeaderPayload(string name, long size, DateTime? modifiedUtc)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(SanitizeFileName(name));

        StringBuilder meta = new StringBuilder(size.ToString(CultureInfo.InvariantCulture));
        if (modifiedUtc.HasValue)
        {
            long seconds = (long)(modifiedUtc.Value.ToUniversalTime() - UnixEpoch).TotalSeconds;
            if (seconds > 0)
            {
                meta.Append(' ').Append(Convert.ToString(seconds, 8));
            }
        }
        byte[] metaBytes = Encoding.ASCII.GetBytes(meta.ToString());

        int content = nameBytes.Length + 1 + metaBytes.Length;
        if (content >= LargeBlockSize)
        {
            throw new ArgumentException("The file name is too long for a YMODEM header.", nameof(name));
        }

        // Always at least one 0 after the last field, so the block size is the first one that leaves room for it.
        byte[] payload = new byte[content < BlockSize ? BlockSize : LargeBlockSize];
        Buffer.BlockCopy(nameBytes, 0, payload, 0, nameBytes.Length);
        Buffer.BlockCopy(metaBytes, 0, payload, nameBytes.Length + 1, metaBytes.Length);
        return payload;
    }

    /// <summary>Reads block 0. Null for the empty block that ends a batch (no file name).</summary>
    internal static FileHeader ParseHeader(byte[] payload, int length)
    {
        if (length <= 0 || payload[0] == 0)
        {
            return null;
        }

        int nameEnd = Array.IndexOf(payload, (byte)0, 0, length);
        if (nameEnd < 0)
        {
            nameEnd = length;
        }
        string rawName = Encoding.UTF8.GetString(payload, 0, nameEnd);

        long size = -1;
        DateTime? modified = null;
        if (nameEnd + 1 < length)
        {
            int metaEnd = Array.IndexOf(payload, (byte)0, nameEnd + 1, length - nameEnd - 1);
            if (metaEnd < 0)
            {
                metaEnd = length;
            }
            string[] fields = Encoding.ASCII.GetString(payload, nameEnd + 1, metaEnd - nameEnd - 1)
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // A size of 0 means "unknown" for some senders; for a real empty file there is no data anyway.
            if (fields.Length > 0 && long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out long parsedSize) && parsedSize > 0)
            {
                size = parsedSize;
            }
            if (fields.Length > 1 && TryParseOctal(fields[1], out long seconds) && seconds > 0 && seconds < 253402300799L)
            {
                modified = UnixEpoch.AddSeconds(seconds);
            }
        }

        return new FileHeader
        {
            RawName = rawName,
            Name = SanitizeFileName(rawName, "received.bin"),
            Size = size,
            ModifiedUtc = modified
        };
    }

    private static bool TryParseOctal(string text, out long value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > 20)
        {
            return false;
        }
        foreach (char ch in text)
        {
            if (ch < '0' || ch > '7')
            {
                return false;
            }
            value = value * 8 + (ch - '0');
        }
        return true;
    }

    // ---------- sender ----------

    private enum Request
    {
        Got,
        Timeout,
        Aborted
    }

    private enum Delivery
    {
        Acked,
        NoAnswer,
        Aborted
    }

    /// <summary>
    /// Waits for the receiver's 'C'. Anything else (a banner, the echo of the command that started the receiver) is
    /// ignored, and so are copies of the 'C' that piled up meanwhile. Aborted means the transfer ended and was reported.
    /// </summary>
    private static Request AwaitRequest(XModemTransfer.Link link, int timeoutMs, XModemTransfer.ProgressHandler onProgress, long done, long total, int errors)
    {
        Stopwatch timer = Stopwatch.StartNew();
        bool previousWasCan = false;
        while (true)
        {
            int left = timeoutMs - (int)timer.ElapsedMilliseconds;
            byte? b = left > 0 ? link.Read(left) : null;
            if (link.IsCancelled)
            {
                link.SendCancel();
                XModemTransfer.Report(onProgress, done, total, errors, "Transfer cancelled.");
                return Request.Aborted;
            }
            if (!b.HasValue)
            {
                return Request.Timeout;
            }
            if (b.Value == CAN)
            {
                if (previousWasCan)
                {
                    XModemTransfer.Report(onProgress, done, total, errors, "Cancelled by the receiver.");
                    return Request.Aborted;
                }
                previousWasCan = true;
                continue;
            }
            previousWasCan = false;
            if (b.Value == C)
            {
                link.Purge(0);
                return Request.Got;
            }
        }
    }

    /// <summary>AwaitRequest where running out of time ends the transfer with <paramref name="timeoutMessage"/>.</summary>
    private static bool RequireRequest(XModemTransfer.Link link, int timeoutMs, XModemTransfer.ProgressHandler onProgress, long done, long total, ref int errors, string timeoutMessage)
    {
        Request request = AwaitRequest(link, timeoutMs, onProgress, done, total, errors);
        if (request == Request.Got)
        {
            return true;
        }
        if (request == Request.Timeout)
        {
            link.SendCancel();
            XModemTransfer.Report(onProgress, done, total, ++errors, timeoutMessage);
        }
        return false;
    }

    /// <summary>Sends one block until it is acknowledged, at most <paramref name="maxAttempts"/> times.</summary>
    private static Delivery SendBlock(XModemTransfer.Link link, byte[] packet, int length, string what, int maxAttempts, int ackMs,
        XModemTransfer.ProgressHandler onProgress, long done, long total, ref int errors)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (link.IsCancelled)
            {
                link.SendCancel();
                XModemTransfer.Report(onProgress, done, total, errors, "Transfer cancelled.");
                return Delivery.Aborted;
            }

            link.Send(packet, length);
            XModemTransfer.Reply reply = XModemTransfer.WaitForReply(link, ackMs);
            if (reply == XModemTransfer.Reply.Ack)
            {
                return Delivery.Acked;
            }
            if (reply == XModemTransfer.Reply.Cancelled)
            {
                XModemTransfer.Report(onProgress, done, total, errors, "Cancelled by the receiver.");
                return Delivery.Aborted;
            }
            if (link.IsCancelled)
            {
                continue; // reported at the top of the loop
            }

            errors++;
            if (attempt >= maxAttempts)
            {
                return Delivery.NoAnswer;
            }
            XModemTransfer.Report(onProgress, done, total, errors, "Retrying " + what + " (attempt " + (attempt + 1) + ")");
        }
    }

    /// <summary>Sends one file with the given name and modification time (UTC, optional) as a batch of one.</summary>
    public static bool SendFile(
        Stream sourceStream,
        string name,
        DateTime? modifiedUtc,
        XModemTransfer.SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        XModemTransfer.ProgressHandler onProgress)
    {
        return SendFiles(new[] { new FileToSend(name, sourceStream, modifiedUtc) }, sendData, readByteWithTimeout, cancellationToken, onProgress);
    }

    public static bool SendFiles(
        IReadOnlyList<FileToSend> files,
        XModemTransfer.SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        XModemTransfer.ProgressHandler onProgress)
    {
        return SendFiles(files, sendData, readByteWithTimeout, cancellationToken, onProgress, new XModemTransfer.Timeouts());
    }

    /// <summary>
    /// Sends the files one after another and ends the batch. A false result means the transfer was cancelled or failed;
    /// the last status passed to <paramref name="onProgress"/> says why. Progress counts the bytes of all files together.
    /// </summary>
    internal static bool SendFiles(
        IReadOnlyList<FileToSend> files,
        XModemTransfer.SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        XModemTransfer.ProgressHandler onProgress,
        XModemTransfer.Timeouts timeouts)
    {
        if (files == null) throw new ArgumentNullException(nameof(files));
        if (files.Count == 0) throw new ArgumentException("There is no file to send.", nameof(files));
        if (sendData == null) throw new ArgumentNullException(nameof(sendData));
        if (readByteWithTimeout == null) throw new ArgumentNullException(nameof(readByteWithTimeout));

        XModemTransfer.Link link = new XModemTransfer.Link(sendData, readByteWithTimeout, cancellationToken);
        long total = 0;
        foreach (FileToSend file in files)
        {
            total += file.Length;
        }
        long done = 0;
        int errors = 0;

        byte[] data = new byte[LargeBlockSize];
        byte[] packet = new byte[3 + LargeBlockSize + 2];

        XModemTransfer.Report(onProgress, 0, total, errors, "Waiting for the receiver...");
        if (!RequireRequest(link, timeouts.SenderHandshakeMs, onProgress, done, total, ref errors,
            "Handshake timeout: the receiver did not ask for data ('C'). YMODEM needs a receiver that uses CRC-16."))
        {
            return false;
        }

        for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            FileToSend file = files[fileIndex];
            string label = SanitizeFileName(file.Name) + (files.Count > 1 ? " (" + (fileIndex + 1) + "/" + files.Count + ")" : string.Empty);

            // Block 0: name and size.
            byte[] header = BuildHeaderPayload(file.Name, file.Length, file.ModifiedUtc);
            int headerLength = XModemTransfer.BuildPacket(packet, header, header.Length, header.Length, 0, Crc);
            XModemTransfer.Report(onProgress, done, total, errors, "Sending the header of " + label);
            Delivery delivery = SendBlock(link, packet, headerLength, "the header of " + label, MaxRetries, timeouts.AckMs, onProgress, done, total, ref errors);
            if (delivery == Delivery.Aborted)
            {
                return false;
            }
            if (delivery == Delivery.NoAnswer)
            {
                link.SendCancel();
                XModemTransfer.Report(onProgress, done, total, errors, "Transfer failed: the receiver did not accept the header of " + label + ".");
                return false;
            }
            if (!RequireRequest(link, timeouts.NextRequestMs, onProgress, done, total, ref errors,
                "Transfer failed: the receiver did not ask for the data of " + label + " ('C' expected after the header)."))
            {
                return false;
            }

            // Data: numbered from 1; 1K blocks, a tail of up to 128 bytes as one 128-byte block.
            long remaining = file.Length;
            int blockIndex = 1;
            while (remaining > 0)
            {
                int want = (int)Math.Min(LargeBlockSize, remaining);
                int read;
                try
                {
                    read = XModemTransfer.ReadFully(file.Stream, data, want);
                }
                catch (Exception ex)
                {
                    link.SendCancel();
                    XModemTransfer.Report(onProgress, done, total, ++errors, "Cannot read the file: " + ex.Message);
                    return false;
                }
                if (read < want)
                {
                    // The size is already announced: sending less would leave the receiver with a damaged file.
                    link.SendCancel();
                    XModemTransfer.Report(onProgress, done, total, ++errors, "Cannot read the file: " + label + " is shorter than it was a moment ago.");
                    return false;
                }

                int size = read > BlockSize ? LargeBlockSize : BlockSize;
                int packetLength = XModemTransfer.BuildPacket(packet, data, read, size, blockIndex, Crc);
                delivery = SendBlock(link, packet, packetLength, "block " + blockIndex, MaxRetries, timeouts.AckMs, onProgress, done, total, ref errors);
                if (delivery == Delivery.Aborted)
                {
                    return false;
                }
                if (delivery == Delivery.NoAnswer)
                {
                    link.SendCancel();
                    XModemTransfer.Report(onProgress, done, total, errors,
                        "Transfer failed: no valid answer to block " + blockIndex + " of " + label + " after " + MaxRetries + " attempts.");
                    return false;
                }

                done += read;
                remaining -= read;
                XModemTransfer.Report(onProgress, done, total, errors, label + ": sent block " + blockIndex);
                blockIndex++;
            }

            // EOT. A NAK to the first one is normal (it protects against a damaged byte that looks like EOT), so the EOT is repeated.
            bool eotAcknowledged = false;
            for (int attempt = 1; attempt <= MaxRetries && !eotAcknowledged; attempt++)
            {
                if (link.IsCancelled)
                {
                    link.SendCancel();
                    XModemTransfer.Report(onProgress, done, total, errors, "Transfer cancelled.");
                    return false;
                }

                link.Send(EOT);
                XModemTransfer.Reply reply = XModemTransfer.WaitForReply(link, timeouts.AckMs);
                if (reply == XModemTransfer.Reply.Ack)
                {
                    eotAcknowledged = true;
                }
                else if (reply == XModemTransfer.Reply.Cancelled)
                {
                    XModemTransfer.Report(onProgress, done, total, errors, "Cancelled by the receiver.");
                    return false;
                }
                else if (!link.IsCancelled && (reply == XModemTransfer.Reply.Timeout || attempt > 1))
                {
                    errors++;
                }
            }
            if (!eotAcknowledged)
            {
                link.SendCancel();
                XModemTransfer.Report(onProgress, done, total, errors, "Transfer failed: the end of " + label + " was not acknowledged.");
                return false;
            }

            XModemTransfer.Report(onProgress, done, total, errors, label + " sent");

            if (fileIndex < files.Count - 1
                && !RequireRequest(link, timeouts.NextRequestMs, onProgress, done, total, ref errors,
                    "Transfer failed: the receiver did not ask for the next file after " + label + " ('C' expected)."))
            {
                return false;
            }
        }

        // End of the batch: an empty block 0. The files are already acknowledged, so a receiver that goes away here
        // (typically a bootloader that starts the new program) does not make the transfer a failure.
        Request end = AwaitRequest(link, timeouts.BatchEndRequestMs, onProgress, done, total, errors);
        if (end == Request.Aborted)
        {
            return false;
        }
        if (end == Request.Timeout)
        {
            XModemTransfer.Report(onProgress, done, total, errors, "Transfer complete (the receiver did not ask for the end-of-batch block).");
            return true;
        }

        byte[] terminator = new byte[BlockSize];
        int terminatorLength = XModemTransfer.BuildPacket(packet, terminator, terminator.Length, terminator.Length, 0, Crc);
        Delivery last = SendBlock(link, packet, terminatorLength, "the end-of-batch block", EndOfBatchAttempts, timeouts.AckMs, onProgress, done, total, ref errors);
        if (last == Delivery.Aborted)
        {
            return false;
        }
        XModemTransfer.Report(onProgress, done, total, errors, last == Delivery.Acked
            ? "Transfer complete."
            : "Transfer complete (the end-of-batch block was not acknowledged).");
        return true;
    }

    // ---------- receiver ----------

    private enum HeaderResult
    {
        File,
        EndOfBatch,
        Failed
    }

    /// <summary>
    /// Asks for block 0 with 'C' and reads it. Failed means the transfer ended and was reported. The block is not
    /// acknowledged here: the caller does that once it knows where the file goes.
    /// </summary>
    private static HeaderResult ReceiveHeader(XModemTransfer.Link link, byte[] payload, XModemTransfer.ProgressHandler onProgress,
        ref int errors, XModemTransfer.Timeouts timeouts, out FileHeader info)
    {
        info = null;

        byte? start = null;
        for (int attempt = 0; attempt < MaxRetries && !start.HasValue; attempt++)
        {
            if (link.IsCancelled)
            {
                link.SendCancel();
                XModemTransfer.Report(onProgress, 0, 0, errors, "Transfer cancelled.");
                return HeaderResult.Failed;
            }
            link.Send(C);
            start = XModemTransfer.ReadHeader(link, timeouts.ReceiverHandshakeAttemptMs);
            if (start == EOT)
            {
                link.Send(ACK); // the repeated EOT of the previous file: its ACK was lost, the next 'C' follows
                start = null;
            }
        }
        if (link.IsCancelled)
        {
            link.SendCancel();
            XModemTransfer.Report(onProgress, 0, 0, errors, "Transfer cancelled.");
            return HeaderResult.Failed;
        }
        if (!start.HasValue)
        {
            XModemTransfer.Report(onProgress, 0, 0, ++errors, "The sender did not respond to the handshake.");
            return HeaderResult.Failed;
        }

        int consecutiveErrors = 0;
        while (true)
        {
            if (start == CAN)
            {
                XModemTransfer.Report(onProgress, 0, 0, errors, "Cancelled by the sender.");
                return HeaderResult.Failed;
            }

            int size = start == STX ? LargeBlockSize : BlockSize;
            if (XModemTransfer.TryReadBlock(link, size, Crc, timeouts.ByteMs, payload, out byte number))
            {
                if (number != 0)
                {
                    // A plain XMODEM(-1K) sender starts with block 1: there is no header to take the file name from.
                    link.SendCancel();
                    XModemTransfer.Report(onProgress, 0, 0, ++errors,
                        "The sender does not use YMODEM: the file header (block 0) is missing, block " + number + " came first. Use XMODEM for this sender.");
                    return HeaderResult.Failed;
                }
                info = ParseHeader(payload, size);
                return info == null ? HeaderResult.EndOfBatch : HeaderResult.File;
            }

            if (link.IsCancelled)
            {
                link.SendCancel();
                XModemTransfer.Report(onProgress, 0, 0, errors, "Transfer cancelled.");
                return HeaderResult.Failed;
            }
            errors++;
            if (++consecutiveErrors > MaxRetries)
            {
                link.SendCancel();
                XModemTransfer.Report(onProgress, 0, 0, errors, "Transfer failed: too many damaged header blocks.");
                return HeaderResult.Failed;
            }
            link.Purge(timeouts.PurgeQuietMs);
            XModemTransfer.Report(onProgress, 0, 0, errors, "Damaged file header, asking again");
            link.Send(NAK);
            start = XModemTransfer.ReadHeader(link, timeouts.HeaderMs);
            if (!start.HasValue)
            {
                if (link.IsCancelled)
                {
                    link.SendCancel();
                    XModemTransfer.Report(onProgress, 0, 0, errors, "Transfer cancelled.");
                    return HeaderResult.Failed;
                }
                XModemTransfer.Report(onProgress, 0, 0, ++errors, "Transfer failed: the sender stopped responding after a damaged header.");
                link.SendCancel();
                return HeaderResult.Failed;
            }
        }
    }

    /// <summary>
    /// Receives the data blocks of one file, after its header has been acknowledged, up to the EOT. The padding of the
    /// last block is cut off when the size is known. False means the transfer ended and was reported.
    /// </summary>
    private static bool ReceiveFileData(XModemTransfer.Link link, Stream target, FileHeader info, byte[] payload,
        XModemTransfer.ProgressHandler onProgress, ref int errors, XModemTransfer.Timeouts timeouts)
    {
        long total = info.Size > 0 ? info.Size : 0;
        long received = 0;
        int blocksAccepted = 0;
        byte expectedBlock = 1;
        bool eotSeen = false;
        int consecutiveErrors = 0;

        // 'C' asks for the data. Without a data block the sender is idle or does not know the file is accepted.
        byte? header = null;
        for (int attempt = 0; attempt < MaxRetries && !header.HasValue; attempt++)
        {
            if (link.IsCancelled)
            {
                link.SendCancel();
                XModemTransfer.Report(onProgress, received, total, errors, "Transfer cancelled.");
                return false;
            }
            link.Send(C);
            header = XModemTransfer.ReadHeader(link, timeouts.ReceiverHandshakeAttemptMs);
        }
        if (link.IsCancelled)
        {
            link.SendCancel();
            XModemTransfer.Report(onProgress, received, total, errors, "Transfer cancelled.");
            return false;
        }
        if (!header.HasValue)
        {
            link.SendCancel();
            XModemTransfer.Report(onProgress, received, total, ++errors, "Transfer failed: the sender did not send the data of " + info.Name + ".");
            return false;
        }

        while (true)
        {
            if (link.IsCancelled)
            {
                link.SendCancel();
                XModemTransfer.Report(onProgress, received, total, errors, "Transfer cancelled.");
                return false;
            }

            if (!header.HasValue)
            {
                header = XModemTransfer.ReadHeader(link, timeouts.HeaderMs);
                if (link.IsCancelled)
                {
                    continue; // reported at the top of the loop
                }
            }

            if (!header.HasValue)
            {
                errors++;
                if (++consecutiveErrors > MaxRetries)
                {
                    link.SendCancel();
                    XModemTransfer.Report(onProgress, received, total, errors, "Transfer failed: the sender stopped responding.");
                    return false;
                }
                XModemTransfer.Report(onProgress, received, total, errors, "Timeout waiting for block " + (blocksAccepted + 1) + ", asking again");
                link.Send(NAK);
                continue;
            }

            byte start = header.Value;
            header = null;

            if (start == EOT)
            {
                if (!eotSeen)
                {
                    // NAK the first EOT: a damaged byte must not end a file. A real sender repeats it.
                    eotSeen = true;
                    link.Send(NAK);
                    continue;
                }
                if (info.Size > 0 && received != info.Size)
                {
                    link.SendCancel();
                    XModemTransfer.Report(onProgress, received, total, ++errors,
                        "Transfer failed: " + info.Name + " ended after " + received + " of " + info.Size + " bytes.");
                    return false;
                }
                try
                {
                    target.Flush();
                }
                catch (Exception ex)
                {
                    link.SendCancel();
                    XModemTransfer.Report(onProgress, received, total, ++errors, "Cannot write the file: " + ex.Message);
                    return false;
                }
                link.Send(ACK);
                XModemTransfer.Report(onProgress, received, total, errors, info.Name + " received");
                return true;
            }
            if (start == CAN)
            {
                XModemTransfer.Report(onProgress, received, total, errors, "Cancelled by the sender.");
                return false;
            }

            int size = start == STX ? LargeBlockSize : BlockSize;
            if (!XModemTransfer.TryReadBlock(link, size, Crc, timeouts.ByteMs, payload, out byte blockNumber))
            {
                if (link.IsCancelled)
                {
                    continue; // reported at the top of the loop
                }
                errors++;
                if (++consecutiveErrors > MaxRetries)
                {
                    link.SendCancel();
                    XModemTransfer.Report(onProgress, received, total, errors, "Transfer failed: too many damaged blocks.");
                    return false;
                }
                // Drop the rest of the damaged block before asking for it again, or it would be taken for the next block.
                link.Purge(timeouts.PurgeQuietMs);
                XModemTransfer.Report(onProgress, received, total, errors, "Damaged block " + (blocksAccepted + 1) + ", asking again");
                link.Send(NAK);
                continue;
            }

            consecutiveErrors = 0;
            eotSeen = false;
            if (blockNumber == expectedBlock)
            {
                int count = size;
                if (info.Size > 0)
                {
                    count = (int)Math.Max(0, Math.Min(size, info.Size - received));
                }
                try
                {
                    if (count > 0)
                    {
                        target.Write(payload, 0, count);
                    }
                }
                catch (Exception ex)
                {
                    link.SendCancel();
                    XModemTransfer.Report(onProgress, received, total, ++errors, "Cannot write the file: " + ex.Message);
                    return false;
                }
                received += count;
                blocksAccepted++;
                expectedBlock++;
                link.Send(ACK);
                XModemTransfer.Report(onProgress, received, total, errors, info.Name + ": received block " + blocksAccepted);
            }
            else if (blocksAccepted > 0 && blockNumber == (byte)(expectedBlock - 1))
            {
                // The sender repeated a block whose ACK it did not see: acknowledge it again, keep the data once.
                link.Send(ACK);
                XModemTransfer.Report(onProgress, received, total, errors, "Repeated block " + blocksAccepted + " acknowledged");
            }
            else if (blocksAccepted == 0 && blockNumber == 0)
            {
                // The sender repeated the header whose ACK (or 'C') it did not see.
                link.Send(ACK, C);
            }
            else
            {
                link.SendCancel();
                XModemTransfer.Report(onProgress, received, total, ++errors,
                    "Transfer failed: block " + blockNumber + " arrived while block " + expectedBlock + " was expected.");
                return false;
            }
        }
    }

    /// <summary>
    /// Receives a batch of files. For each file <paramref name="openTarget"/> is given its header and returns the stream
    /// to write it to (the stream is disposed when the file is complete or the transfer fails), or null to refuse the
    /// file, which ends the transfer. <paramref name="onFileReceived"/> (optional) is called once a file has been
    /// received completely and its stream is closed. Progress is per file: the size announced by the sender is the
    /// total (0 when it did not announce one).
    /// </summary>
    public static bool ReceiveFiles(
        Func<FileHeader, Stream> openTarget,
        XModemTransfer.SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        XModemTransfer.ProgressHandler onProgress,
        Action<FileHeader> onFileReceived = null)
    {
        return ReceiveFiles(openTarget, sendData, readByteWithTimeout, cancellationToken, onProgress, onFileReceived, new XModemTransfer.Timeouts());
    }

    internal static bool ReceiveFiles(
        Func<FileHeader, Stream> openTarget,
        XModemTransfer.SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        XModemTransfer.ProgressHandler onProgress,
        Action<FileHeader> onFileReceived,
        XModemTransfer.Timeouts timeouts)
    {
        if (openTarget == null) throw new ArgumentNullException(nameof(openTarget));
        if (sendData == null) throw new ArgumentNullException(nameof(sendData));
        if (readByteWithTimeout == null) throw new ArgumentNullException(nameof(readByteWithTimeout));

        XModemTransfer.Link link = new XModemTransfer.Link(sendData, readByteWithTimeout, cancellationToken);
        byte[] payload = new byte[LargeBlockSize];
        int errors = 0;
        int filesReceived = 0;

        // Text that arrived before the handshake (a banner, an echo) must not be mistaken for the peer's first answer.
        link.Purge(0);
        XModemTransfer.Report(onProgress, 0, 0, errors, "Waiting for the sender...");

        while (true)
        {
            FileHeader info;
            HeaderResult result = ReceiveHeader(link, payload, onProgress, ref errors, timeouts, out info);
            if (result == HeaderResult.Failed)
            {
                return false;
            }
            if (result == HeaderResult.EndOfBatch)
            {
                link.Send(ACK);
                XModemTransfer.Report(onProgress, 0, 0, errors, filesReceived == 1
                    ? "File received successfully."
                    : filesReceived + " files received successfully.");
                return true;
            }

            info.Index = filesReceived + 1;
            Stream target;
            try
            {
                target = openTarget(info);
            }
            catch (Exception ex)
            {
                link.SendCancel();
                XModemTransfer.Report(onProgress, 0, 0, ++errors, "Cannot create " + info.Name + ": " + ex.Message);
                return false;
            }
            if (target == null)
            {
                link.SendCancel();
                XModemTransfer.Report(onProgress, 0, 0, errors, "Receiving " + info.Name + " was declined.");
                return false;
            }

            bool received;
            using (target)
            {
                link.Send(ACK); // the header: the file is accepted and its data may start
                XModemTransfer.Report(onProgress, 0, info.Size > 0 ? info.Size : 0, errors, "Receiving " + info.Name);
                received = ReceiveFileData(link, target, info, payload, onProgress, ref errors, timeouts);
            }
            if (!received)
            {
                return false;
            }

            filesReceived++;
            onFileReceived?.Invoke(info);
        }
    }
}
