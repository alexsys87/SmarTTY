using System;
using System.IO;
using System.Threading;

namespace SmarTTY.IO;

public class XModemTransfer
{
    public const byte SOH = 0x01; // Start of Header (128-byte block)
    public const byte EOT = 0x04; // End of Transmission
    public const byte ACK = 0x06; // Acknowledge
    public const byte NAK = 0x15; // Negative Acknowledge (Checksum mode)
    public const byte CAN = 0x18; // Cancel
    public const byte C   = 0x43; // 'C' (CRC16 mode)
    public const byte SUB = 0x1A; // Padding character

    public enum XModemMode
    {
        Checksum,
        Crc16
    }

    public delegate void ProgressHandler(long bytesTransferred, long totalBytes, int errorCount, string status);
    public delegate void SendDataHandler(byte[] data, int offset, int length);

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

    public static bool SendFile(
        Stream sourceStream,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress)
    {
        long totalBytes = sourceStream.Length;
        long bytesTransferred = 0;
        int errorCount = 0;

        // Step 1: Wait for receiver initial handshaking byte (NAK or 'C')
        XModemMode mode = XModemMode.Checksum;
        bool handshakeSuccess = false;

        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (cancellationToken.IsCancellationRequested) return false;

            byte? b = readByteWithTimeout(3000);
            if (b.HasValue)
            {
                if (b.Value == C)
                {
                    mode = XModemMode.Crc16;
                    handshakeSuccess = true;
                    break;
                }
                if (b.Value == NAK)
                {
                    mode = XModemMode.Checksum;
                    handshakeSuccess = true;
                    break;
                }
                if (b.Value == CAN)
                {
                    return false;
                }
            }
        }

        if (!handshakeSuccess)
        {
            onProgress?.Invoke(0, totalBytes, ++errorCount, "Handshake timeout waiting for receiver (NAK or 'C').");
            return false;
        }

        byte blockNumber = 1;
        byte[] payload = new byte[128];

        while (bytesTransferred < totalBytes || totalBytes == 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                sendData(new byte[] { CAN, CAN }, 0, 2);
                return false;
            }

            int read = sourceStream.Read(payload, 0, 128);
            if (read < 128)
            {
                for (int i = read; i < 128; i++)
                {
                    payload[i] = SUB; // Pad with SUB (0x1A)
                }
            }

            // Build 132 or 133 byte packet
            int packetSize = 1 + 2 + 128 + (mode == XModemMode.Crc16 ? 2 : 1);
            byte[] packet = new byte[packetSize];
            packet[0] = SOH;
            packet[1] = blockNumber;
            packet[2] = (byte)(255 - blockNumber);
            Array.Copy(payload, 0, packet, 3, 128);

            if (mode == XModemMode.Crc16)
            {
                ushort crc = CalculateCrc16(payload, 0, 128);
                packet[131] = (byte)(crc >> 8);
                packet[132] = (byte)(crc & 0xFF);
            }
            else
            {
                packet[131] = CalculateChecksum(payload, 0, 128);
            }

            bool blockAcked = false;
            int retries = 0;

            while (!blockAcked && retries < 10)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    sendData(new byte[] { CAN, CAN }, 0, 2);
                    return false;
                }

                sendData(packet, 0, packet.Length);

                byte? ackByte = readByteWithTimeout(3000);
                if (ackByte.HasValue && ackByte.Value == ACK)
                {
                    blockAcked = true;
                    bytesTransferred += read;
                    blockNumber++;
                    onProgress?.Invoke(bytesTransferred, totalBytes, errorCount, $"Sending block {blockNumber - 1}");
                }
                else
                {
                    retries++;
                    errorCount++;
                    onProgress?.Invoke(bytesTransferred, totalBytes, errorCount, $"Retrying block {blockNumber} (attempt {retries})");
                }
            }

            if (!blockAcked)
            {
                sendData(new byte[] { CAN, CAN }, 0, 2);
                return false;
            }

            if (read == 0) break; // End of empty stream
        }

        // Send EOT
        for (int i = 0; i < 10; i++)
        {
            if (cancellationToken.IsCancellationRequested) return false;

            sendData(new byte[] { EOT }, 0, 1);
            byte? ack = readByteWithTimeout(3000);
            if (ack.HasValue && ack.Value == ACK)
            {
                onProgress?.Invoke(bytesTransferred, totalBytes, errorCount, "Transfer complete.");
                return true;
            }
        }

        return false;
    }

    public static bool ReceiveFile(
        Stream targetStream,
        SendDataHandler sendData,
        Func<int, byte?> readByteWithTimeout,
        CancellationToken cancellationToken,
        ProgressHandler onProgress)
    {
        long bytesReceived = 0;
        int errorCount = 0;
        byte expectedBlock = 1;

        // Try CRC16 mode first by sending 'C'
        XModemMode mode = XModemMode.Crc16;
        bool started = false;

        for (int i = 0; i < 10; i++)
        {
            if (cancellationToken.IsCancellationRequested) return false;

            sendData(new byte[] { C }, 0, 1);
            byte? firstByte = readByteWithTimeout(3000);
            if (firstByte.HasValue)
            {
                if (firstByte.Value == SOH)
                {
                    started = true;
                    break;
                }
            }
        }

        // Fallback to Checksum mode (send NAK)
        if (!started)
        {
            mode = XModemMode.Checksum;
            for (int i = 0; i < 10; i++)
            {
                if (cancellationToken.IsCancellationRequested) return false;

                sendData(new byte[] { NAK }, 0, 1);
                byte? firstByte = readByteWithTimeout(3000);
                if (firstByte.HasValue)
                {
                    if (firstByte.Value == SOH)
                    {
                        started = true;
                        break;
                    }
                }
            }
        }

        if (!started)
        {
            onProgress?.Invoke(0, 0, ++errorCount, "Sender did not respond to initial handshaking.");
            return false;
        }

        // Receiving loop
        byte? header = SOH; // First block's SOH is already received

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                sendData(new byte[] { CAN, CAN }, 0, 2);
                return false;
            }

            if (!header.HasValue)
            {
                header = readByteWithTimeout(5000);
            }

            if (!header.HasValue)
            {
                errorCount++;
                sendData(new byte[] { NAK }, 0, 1);
                continue;
            }

            if (header.Value == EOT)
            {
                sendData(new byte[] { ACK }, 0, 1);
                onProgress?.Invoke(bytesReceived, bytesReceived, errorCount, "File received successfully.");
                return true;
            }

            if (header.Value == CAN)
            {
                return false;
            }

            if (header.Value != SOH)
            {
                errorCount++;
                header = null;
                sendData(new byte[] { NAK }, 0, 1);
                continue;
            }

            // Read remaining 131 (or 132 for CRC) bytes of block
            byte? blkNum = readByteWithTimeout(1000);
            byte? invBlkNum = readByteWithTimeout(1000);

            if (!blkNum.HasValue || !invBlkNum.HasValue || (byte)(blkNum.Value + invBlkNum.Value) != 255)
            {
                errorCount++;
                header = null;
                sendData(new byte[] { NAK }, 0, 1);
                continue;
            }

            byte[] payload = new byte[128];
            bool payloadOk = true;
            for (int i = 0; i < 128; i++)
            {
                byte? b = readByteWithTimeout(1000);
                if (!b.HasValue)
                {
                    payloadOk = false;
                    break;
                }
                payload[i] = b.Value;
            }

            if (!payloadOk)
            {
                errorCount++;
                header = null;
                sendData(new byte[] { NAK }, 0, 1);
                continue;
            }

            bool validCheck = false;
            if (mode == XModemMode.Crc16)
            {
                byte? crcHi = readByteWithTimeout(1000);
                byte? crcLo = readByteWithTimeout(1000);
                if (crcHi.HasValue && crcLo.HasValue)
                {
                    ushort expectedCrc = (ushort)((crcHi.Value << 8) | crcLo.Value);
                    ushort actualCrc = CalculateCrc16(payload, 0, 128);
                    validCheck = (expectedCrc == actualCrc);
                }
            }
            else
            {
                byte? checksum = readByteWithTimeout(1000);
                if (checksum.HasValue)
                {
                    byte actualChecksum = CalculateChecksum(payload, 0, 128);
                    validCheck = (checksum.Value == actualChecksum);
                }
            }

            if (!validCheck)
            {
                errorCount++;
                header = null;
                sendData(new byte[] { NAK }, 0, 1);
                continue;
            }

            if (blkNum.Value == expectedBlock)
            {
                targetStream.Write(payload, 0, 128);
                bytesReceived += 128;
                expectedBlock++;
            }

            // ACK current block and prepare for next
            header = null;
            sendData(new byte[] { ACK }, 0, 1);
            onProgress?.Invoke(bytesReceived, bytesReceived, errorCount, $"Received block {blkNum.Value}");
        }
    }
}
