using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SmarTTY.IO;

/// <summary>
/// File transfer through the shell of a Linux device (OpenWrt/busybox included) that has nothing but a terminal line:
/// no sz/rz, no sftp. The file is packed with gzip, the archive is turned into text (base64, or uuencode when the
/// device has no base64) and typed into the device's shell; the device decodes and unpacks it and the checksum of the
/// result is compared with the checksum of the original. A transfer from the device runs the same way in reverse.
/// Only tools that busybox has are used on the device: gzip/gunzip, base64 or uuencode/uudecode, sha256sum/md5sum/cksum
/// and head (or dd). Nothing is installed on the device and no temporary file is kept on it except the unfinished
/// "name.smtty~" next to the destination, which is renamed to the real name only after the checksum matched.
/// The protocol is the same one the user would type by hand, so it is easy to repeat in any terminal.
/// </summary>
public static class GzipBase64Transfer
{
    public enum EncodingKind
    {
        /// <summary>base64 when the device has it, otherwise uuencode.</summary>
        Auto,
        Base64,
        UuEncode
    }

    public enum ChecksumKind
    {
        /// <summary>The strongest the device has: sha256sum, md5sum or cksum.</summary>
        Auto,
        Sha256,
        Md5,
        /// <summary>The POSIX CRC of "cksum": weak, but every Linux has it.</summary>
        Cksum
    }

    public delegate byte? ReadByteHandler(int timeoutMs);

    /// <summary>What the user asked for in the transfer window: the file here, the path on the device and the options.</summary>
    public sealed class Request
    {
        public string LocalPath;
        public string RemotePath;
        public Options Options;
    }

    public sealed class Options
    {
        public EncodingKind Encoding = EncodingKind.Auto;
        public ChecksumKind Checksum = ChecksumKind.Auto;

        /// <summary>How many times the whole transfer is tried when the checksum does not match (1 = no repeat).</summary>
        public int Attempts = 2;

        /// <summary>Replace the destination when it exists. Otherwise an existing destination stops the transfer.</summary>
        public bool Overwrite;

        /// <summary>
        /// Characters in a line of the text typed to the device (base64). A line is typed without its line end, compared with the
        /// echo of the device's terminal and committed only when the echo is the same; a line that came back damaged is erased
        /// and typed again, so a smaller value costs less when the line is noisy. 64 to 2048.
        /// </summary>
        public int WindowBytes = 1024;

        /// <summary>The speed limit used when the device does not echo (the echo is what tells that a line has arrived undamaged).</summary>
        public int NoEchoBytesPerSecond = 6000;

        /// <summary>
        /// Called when the transfer had to give up while the device was still running its end of it and the connection was
        /// already gone, so that the device could not be told to stop (a reconnect, a cable pulled). The bytes are what to send to
        /// the device once there is a connection to it again: they end its reader, remove the unfinished file and restore its
        /// signals, so that its console is not left waiting for the rest of the data and eating whatever is typed next.
        /// May be called on any thread.
        /// </summary>
        public Action<byte[]> RecoveryRequired;

        /// <summary>Receives the lines of the debug log (what was typed to the device, what it answered, what was decided); null for no log.</summary>
        public Action<string> Log;
    }

    // A gzip file of no data. GZipStream writes nothing at all for an empty input on some versions of .NET, and gunzip refuses that.
    private static readonly byte[] EmptyGzip =
    {
        0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    private const int UuBytesPerLine = 45;       // 61 characters per line, as uuencode writes them
    private const string MarkerStart = "SMTTY:";

    /// <summary>Sends <paramref name="localPath"/> to <paramref name="remotePath"/> (a file name, or an existing directory) on the device.</summary>
    /// <returns>True when the file arrived and the checksums matched; false when the user cancelled. Failures throw <see cref="IOException"/>.</returns>
    public static bool SendFile(string localPath, string remotePath, Options options,
        XModemTransfer.SendDataHandler send, ReadByteHandler readByte, CancellationToken token, XModemTransfer.ProgressHandler progress)
    {
        Session session = new Session(options ?? new Options(), send, readByte, token, progress);
        return session.Send(localPath, remotePath);
    }

    /// <summary>Fetches <paramref name="remotePath"/> from the device into <paramref name="localPath"/> (which must not exist yet, unless <see cref="Options.Overwrite"/> is set).</summary>
    public static bool ReceiveFile(string remotePath, string localPath, Options options,
        XModemTransfer.SendDataHandler send, ReadByteHandler readByte, CancellationToken token, XModemTransfer.ProgressHandler progress)
    {
        Session session = new Session(options ?? new Options(), send, readByte, token, progress);
        return session.Receive(remotePath, localPath);
    }

    // ---------- text forms and checksums (public for the tests) ----------

    /// <summary>Quotes a text for a POSIX shell: 'text', with every quote inside it written as '\''.</summary>
    public static string ShellQuote(string text)
    {
        return "'" + text.Replace("'", "'\\''") + "'";
    }

    /// <summary>A path as the shell should see it: "~" and "~/..." are the home directory, everything else is taken literally.</summary>
    public static string ShellPath(string path)
    {
        if (path == "~")
        {
            return "\"$HOME\"";
        }
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return "\"$HOME\"/" + ShellQuote(path.Substring(2));
        }
        return ShellQuote(path);
    }

    /// <summary>The POSIX "cksum" CRC of the data (the length of the data is part of it).</summary>
    public static uint Cksum(byte[] data)
    {
        CksumState state = new CksumState();
        state.Update(data, 0, data.Length);
        return state.Crc();
    }

    private sealed class CksumState
    {
        private static readonly uint[] Table = BuildTable();
        private uint _crc;
        private long _length;

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int bit = 0; bit < 8; bit++)
                {
                    c = (c & 0x80000000u) != 0 ? (c << 1) ^ 0x04C11DB7u : c << 1;
                }
                table[i] = c;
            }
            return table;
        }

        public void Update(byte[] data, int offset, int count)
        {
            uint crc = _crc;
            for (int i = 0; i < count; i++)
            {
                crc = (crc << 8) ^ Table[((crc >> 24) ^ data[offset + i]) & 0xFF];
            }
            _crc = crc;
            _length += count;
        }

        public uint Crc()
        {
            uint crc = _crc;
            long length = _length;
            while (length != 0)
            {
                crc = (crc << 8) ^ Table[((crc >> 24) ^ (uint)(length & 0xFF)) & 0xFF];
                length >>= 8;
            }
            return ~crc;
        }

        public long Length => _length;
    }

    /// <summary>A checksum calculated while the data goes by.</summary>
    private sealed class RunningChecksum : IDisposable
    {
        private readonly HashAlgorithm _hash;
        private readonly CksumState _cksum;

        public RunningChecksum(ChecksumKind kind)
        {
            if (kind == ChecksumKind.Sha256)
            {
                _hash = SHA256.Create();
            }
            else if (kind == ChecksumKind.Md5)
            {
                _hash = MD5.Create();
            }
            else
            {
                _cksum = new CksumState();
            }
        }

        public void Update(byte[] data, int offset, int count)
        {
            if (_hash != null)
            {
                _hash.TransformBlock(data, offset, count, null, 0);
            }
            else
            {
                _cksum.Update(data, offset, count);
            }
        }

        /// <summary>The checksum the way the device's tool writes it: the hash in lower case hex, or "crc length" for cksum.</summary>
        public string Finish()
        {
            if (_hash != null)
            {
                _hash.TransformFinalBlock(new byte[0], 0, 0);
                StringBuilder text = new StringBuilder();
                foreach (byte b in _hash.Hash)
                {
                    text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return text.ToString();
            }
            return _cksum.Crc().ToString(CultureInfo.InvariantCulture) + " " + _cksum.Length.ToString(CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            _hash?.Dispose();
        }
    }

    // ---------- the transfer itself ----------

    private sealed class Capabilities
    {
        public readonly HashSet<string> Tools;

        public Capabilities(IEnumerable<string> tools)
        {
            Tools = new HashSet<string>(tools, StringComparer.Ordinal);
        }

        public bool Has(string tool)
        {
            return Tools.Contains(tool);
        }
    }

    private sealed class Session
    {
        private readonly Options _options;
        private readonly XModemTransfer.SendDataHandler _send;
        private readonly ReadByteHandler _readByte;
        private readonly CancellationToken _token;
        private readonly XModemTransfer.ProgressHandler _progress;
        private readonly string _nonce;
        private readonly string _marker;      // what the device prints: SMTTY:<nonce>:
        private readonly string _endMarker;   // the line that ends the data typed to the device: SMTTY_END_<nonce>
        private readonly StringBuilder _line = new StringBuilder();

        // Lines of the device's script that arrived while this side was busy with something else (typing the data): the
        // device may answer before the last window has been typed.
        private readonly Queue<string> _early = new Queue<string>();
        private int _errors;

        // Set while a script typed into the device is still running and would have to be stopped (Ctrl-C) when this side gives up:
        // "" when there is nothing to remove, otherwise the shell text that sets $d to the destination whose "$d.smtty~" is to be removed.
        private string _pendingCleanup;

        // True while the device's reader waits for the lines of the data (it ends on the end marker): ending it is how a
        // transfer is stopped, and how a damaged one is told to give up.
        private bool _readerWaiting;

        // For the debug log and for the error texts: when did the transfer start, what did the device print last.
        private readonly int _startedAt = Environment.TickCount;
        private readonly StringBuilder _recent = new StringBuilder();
        private byte _lastByte;
        private bool _quietRx; // the lines that go by are the data itself: they are not logged one by one

        public Session(Options options, XModemTransfer.SendDataHandler send, ReadByteHandler readByte, CancellationToken token, XModemTransfer.ProgressHandler progress)
        {
            _options = options;
            _send = send;
            _readByte = readByte;
            _token = token;
            _progress = progress;
            byte[] random = new byte[4];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(random);
            }
            StringBuilder text = new StringBuilder();
            foreach (byte b in random)
            {
                text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            _nonce = text.ToString();
            _marker = MarkerStart + _nonce + ":";
            _endMarker = "SMTTY_END_" + _nonce;
        }

        private void Report(long done, long total, string status)
        {
            _progress?.Invoke(done, total, _errors, status);
        }

        // ----- the line -----

        private void SendBytes(byte[] data)
        {
            Trace("TX " + data.Length + " bytes: " + Escape(Latin1(data), 400));
            _send(data, 0, data.Length);
        }

        /// <summary>Text for the line: UTF-8, so that a name with national letters reaches the shell as it is.</summary>
        private static byte[] Ascii(string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        private static string Latin1(byte[] data)
        {
            char[] chars = new char[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                chars[i] = (char)data[i];
            }
            return new string(chars);
        }

        private void Trace(string text)
        {
            Action<string> log = _options.Log;
            if (log == null)
            {
                return;
            }
            try
            {
                log("[" + (Environment.TickCount - _startedAt).ToString(CultureInfo.InvariantCulture).PadLeft(7) + " ms] " + text);
            }
            catch (IOException)
            {
                // A log that cannot be written must not stop the transfer.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>The last of what the device printed, readable (control characters shown as \r, \n, \xNN), for an error text.</summary>
        private string RecentOutput()
        {
            string text = _recent.ToString() + _line.ToString();
            if (text.Length == 0)
            {
                return " The device has printed nothing.";
            }
            return " The last output of the device: \"" + Escape(text.Length > 300 ? text.Substring(text.Length - 300) : text, 600) + "\"";
        }

        private static string Escape(string text, int max)
        {
            StringBuilder result = new StringBuilder();
            foreach (char c in text)
            {
                if (result.Length >= max)
                {
                    result.Append("...");
                    break;
                }
                if (c == '\r')
                {
                    result.Append("\\r");
                }
                else if (c == '\n')
                {
                    result.Append("\\n");
                }
                else if (c < 32 || c > 126)
                {
                    result.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                }
                else
                {
                    result.Append(c);
                }
            }
            return result.ToString();
        }

        // The line editor of a busybox shell built for a router keeps 512 characters of a line (OpenWrt) and silently drops the rest.
        private const int MaxLineLength = 480;

        /// <summary>Types lines as the user would: each one ends with Enter, and the shell gets a moment to take it.</summary>
        private void TypeLines(params string[] lines)
        {
            foreach (string line in lines)
            {
                _token.ThrowIfCancellationRequested();
                if (line.Length > MaxLineLength)
                {
                    throw new IOException("A command of " + line.Length + " characters is too long for the shell's line editor (about " + MaxLineLength
                        + " are safe). Use a shorter path on the device.");
                }
                SendBytes(Ascii(line + "\r"));
                Pause(40);
            }
        }

        private void Pause(int ms)
        {
            if (_token.WaitHandle.WaitOne(ms))
            {
                _token.ThrowIfCancellationRequested();
            }
        }

        /// <summary>Throws the answers away until the device has been quiet for <paramref name="quietMs"/> (or <paramref name="maxMs"/> have passed).</summary>
        private void Drain(int quietMs, int maxMs)
        {
            int start = Environment.TickCount;
            int lastByte = start;
            while (true)
            {
                _token.ThrowIfCancellationRequested();
                int now = Environment.TickCount;
                if (now - lastByte >= quietMs || now - start >= maxMs)
                {
                    return;
                }
                if (_readByte(50) != null)
                {
                    lastByte = Environment.TickCount;
                }
            }
        }

        /// <summary>
        /// Takes one byte of the device's output. <paramref name="line"/> is the line that this byte has completed (without
        /// the line break), or null. False when nothing came within <paramref name="timeoutMs"/>.
        /// </summary>
        private bool Pump(int timeoutMs, out string line)
        {
            line = null;
            byte? b = _readByte(timeoutMs);
            if (b == null)
            {
                return false;
            }
            _lastByte = b.Value;
            if (b.Value == (byte)'\n')
            {
                line = _line.ToString().Trim('\r');
                _line.Length = 0;
                if (!_quietRx)
                {
                    Trace("RX " + Escape(line, 400));
                }
                _recent.Append(line).Append('\n');
                if (_recent.Length > 1200)
                {
                    _recent.Remove(0, _recent.Length - 600);
                }
            }
            else if (_line.Length < 1 << 16)
            {
                _line.Append((char)b.Value);
            }
            return true;
        }

        /// <summary>Like <see cref="Pump"/> for a caller that is not interested in lines but must not lose the device's answers.</summary>
        private bool PumpKeepingAnswers(int timeoutMs, out bool lineEnded)
        {
            bool got = Pump(timeoutMs, out string line);
            lineEnded = line != null;
            if (line != null && line.IndexOf(_marker, StringComparison.Ordinal) >= 0)
            {
                _early.Enqueue(line);
            }
            return got;
        }

        /// <summary>
        /// The next line of the device's output (without the line break), or null when nothing came for <paramref name="timeoutMs"/>.
        /// A line that is only half there at the timeout is kept for the next call.
        /// </summary>
        private string ReadLine(int timeoutMs)
        {
            if (_early.Count > 0)
            {
                return _early.Dequeue();
            }
            int deadline = Environment.TickCount + timeoutMs;
            while (true)
            {
                _token.ThrowIfCancellationRequested();
                int left = deadline - Environment.TickCount;
                if (left <= 0)
                {
                    return null;
                }
                if (Pump(Math.Min(left, 100), out string line) && line != null)
                {
                    return line;
                }
            }
        }

        /// <summary>
        /// Waits for a line the device's script printed (the line has the transfer's marker: what the terminal echoes of the
        /// typed command never has it, the script builds it from two pieces). Returns the text after the marker.
        /// </summary>
        private string WaitForMarker(int timeoutMs, string what)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (true)
            {
                int left = deadline - Environment.TickCount;
                if (left <= 0)
                {
                    throw new IOException("The device did not answer (" + what + "). Is a shell prompt waiting there, and is the line free?" + RecentOutput());
                }
                string line = ReadLine(left);
                if (line == null)
                {
                    continue;
                }
                int at = line.IndexOf(_marker, StringComparison.Ordinal);
                if (at >= 0)
                {
                    return line.Substring(at + _marker.Length);
                }
            }
        }

        /// <summary>The shell's way of printing "SMTTY:nonce:tag": the text is cut in two so that the echo of this very command does not contain it.</summary>
        private string Say(string tag)
        {
            string text = _marker + tag;
            return "'" + text.Substring(0, 3) + "''" + text.Substring(3) + "\\n'";
        }

        private void StartShell()
        {
            Report(0, 0, "Waiting for the shell...");
            _early.Clear();
            _line.Length = 0;
            // Ctrl-U clears whatever was typed on the prompt and not sent; Enter then shows a fresh prompt to wait for.
            SendBytes(new byte[] { 0x15, (byte)'\r' });
            Drain(300, 3000);
        }

        // ----- what the device can do -----

        private Capabilities Probe()
        {
            Report(0, 0, "Checking the tools on the device...");
            TypeLines(
                "c=; for t in base64 uudecode uuencode gzip gunzip sha256sum md5sum cksum sed wc cat mv rm; do command -v $t >/dev/null 2>&1 && c=\"$c $t\"; done",
                "printf " + Say("CAPS:%s") + " \"$c\"");
            string answer = WaitForMarker(15000, "tool check");
            if (!answer.StartsWith("CAPS:", StringComparison.Ordinal))
            {
                throw new IOException("Unexpected answer of the device: " + answer);
            }
            Trace("The device has: " + answer.Substring(5).Trim());
            List<string> tools = new List<string>(answer.Substring(5).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return new Capabilities(tools);
        }

        private static string Missing(Capabilities caps, params string[] tools)
        {
            List<string> missing = new List<string>();
            foreach (string tool in tools)
            {
                if (!caps.Has(tool))
                {
                    missing.Add(tool);
                }
            }
            return missing.Count == 0 ? null : string.Join(", ", missing.ToArray());
        }

        private ChecksumKind ChooseChecksum(Capabilities caps)
        {
            ChecksumKind wanted = _options.Checksum;
            if (wanted == ChecksumKind.Auto)
            {
                wanted = caps.Has("sha256sum") ? ChecksumKind.Sha256 : caps.Has("md5sum") ? ChecksumKind.Md5 : ChecksumKind.Cksum;
            }
            string tool = ChecksumTool(wanted);
            if (!caps.Has(tool))
            {
                throw new IOException("The device has no \"" + tool + "\" to compare the checksums with. Choose another checksum in the options.");
            }
            return wanted;
        }

        private static string ChecksumTool(ChecksumKind kind)
        {
            switch (kind)
            {
                case ChecksumKind.Sha256: return "sha256sum";
                case ChecksumKind.Md5: return "md5sum";
                default: return "cksum";
            }
        }

        /// <summary>Picks the text form: base64 if the device has the tool for the direction in question, uuencode otherwise.</summary>
        private EncodingKind ChooseEncoding(Capabilities caps, bool sending)
        {
            string base64Tool = "base64";
            string uuTool = sending ? "uudecode" : "uuencode";
            EncodingKind wanted = _options.Encoding;
            if (wanted == EncodingKind.Auto)
            {
                wanted = caps.Has(base64Tool) ? EncodingKind.Base64 : EncodingKind.UuEncode;
            }
            string tool = wanted == EncodingKind.Base64 ? base64Tool : uuTool;
            if (!caps.Has(tool))
            {
                throw new IOException("The device has no \"" + tool + "\" for this transfer. Choose another text form in the options.");
            }
            return wanted;
        }

        // ----- sending -----

        private sealed class Prepared : IDisposable
        {
            public string EncodedPath;
            public long EncodedLength;
            public long CompressedLength;
            public long OriginalLength;
            public string Checksum;

            public void Dispose()
            {
                try
                {
                    if (EncodedPath != null)
                    {
                        File.Delete(EncodedPath);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>Characters in a line of base64: a multiple of 4, and well under the 4095 that a terminal line may hold.</summary>
        private int BlockChars()
        {
            return Math.Max(64, Math.Min(2048, _options.WindowBytes)) / 4 * 4;
        }

        /// <summary>Packs the file, calculates its checksum and writes the text form of the archive to a temporary file.</summary>
        private Prepared Prepare(string localPath, string storedName, EncodingKind encoding, ChecksumKind checksum)
        {
            Prepared prepared = new Prepared();
            string compressedPath = Path.GetTempFileName();
            prepared.EncodedPath = Path.GetTempFileName();
            try
            {
                using (RunningChecksum sum = new RunningChecksum(checksum))
                {
                    using (FileStream input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                    using (FileStream compressedOut = new FileStream(compressedPath, FileMode.Create, FileAccess.Write))
                    using (GZipStream gzip = new GZipStream(compressedOut, CompressionMode.Compress))
                    {
                        prepared.OriginalLength = input.Length;
                        byte[] buffer = new byte[65536];
                        long done = 0;
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            _token.ThrowIfCancellationRequested();
                            sum.Update(buffer, 0, count);
                            gzip.Write(buffer, 0, count);
                            done += count;
                            Report(done, prepared.OriginalLength, "Compressing " + FormatSize(done) + " of " + FormatSize(prepared.OriginalLength) + "...");
                        }
                    }
                    prepared.Checksum = sum.Finish();
                }
                if (prepared.OriginalLength == 0)
                {
                    File.WriteAllBytes(compressedPath, EmptyGzip);
                }
                prepared.CompressedLength = new FileInfo(compressedPath).Length;
                using (FileStream compressed = new FileStream(compressedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream encodedOut = new FileStream(prepared.EncodedPath, FileMode.Create, FileAccess.Write))
                {
                    EncodeText(compressed, encodedOut, encoding, storedName, BlockChars(), _endMarker);
                }
                prepared.EncodedLength = new FileInfo(prepared.EncodedPath).Length;
                return prepared;
            }
            catch
            {
                prepared.Dispose();
                throw;
            }
            finally
            {
                try
                {
                    File.Delete(compressedPath);
                }
                catch (IOException)
                {
                }
            }
        }

        public bool Send(string localPath, string remotePath)
        {
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                throw new IOException("Enter the destination path on the device.");
            }
            string name = Path.GetFileName(localPath);
            Prepared prepared = null;
            Trace("SEND " + localPath + " -> " + remotePath + "; encoding " + _options.Encoding + ", checksum " + _options.Checksum + ", attempts " + _options.Attempts + ", overwrite " + _options.Overwrite);
            try
            {
                StartShell();
                Capabilities caps = Probe();
                string missing = Missing(caps, "gunzip", "mv", "rm", "cat", "sed");
                if (missing != null)
                {
                    throw new IOException("The device has no: " + missing + ".");
                }
                EncodingKind encoding = ChooseEncoding(caps, sending: true);
                ChecksumKind checksum = ChooseChecksum(caps);

                prepared = Prepare(localPath, name, encoding, checksum);
                string summary = FormatSize(prepared.OriginalLength) + " -> gzip " + FormatSize(prepared.CompressedLength)
                    + " -> " + (encoding == EncodingKind.Base64 ? "base64 " : "uuencode ") + FormatSize(prepared.EncodedLength);

                int attempts = Math.Max(1, _options.Attempts);
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    string attemptText = attempts > 1 ? " (attempt " + attempt + " of " + attempts + ")" : string.Empty;
                    string failure = SendAttempt(prepared, caps, encoding, checksum, name, remotePath, summary, attemptText);
                    if (failure == null)
                    {
                        Report(prepared.EncodedLength, prepared.EncodedLength, "Done: " + checksum + " matches.");
                        return true;
                    }
                    if (attempt == attempts)
                    {
                        throw new IOException(failure);
                    }
                    _errors++;
                    Report(0, prepared.EncodedLength, failure + " Trying again...");
                    StartShell();
                }
                return false;
            }
            catch (OperationCanceledException)
            {
                Trace("Cancelled");
                AbortDevice();
                return false;
            }
            catch (IOException ex)
            {
                Trace("FAILED: " + ex.Message);
                AbortDevice();
                throw;
            }
            finally
            {
                prepared?.Dispose();
            }
        }

        /// <summary>The shell text that sets $d to the destination: the path as given, or the file name inside it when it is a directory.</summary>
        private static string DestinationScript(string remotePath, string name)
        {
            return "d=" + ShellPath(remotePath) + "; [ -d \"$d\" ] && d=\"$d\"/" + ShellQuote(name);
        }

        /// <summary>One try. Returns null on success, the reason when the data did not arrive intact; throws when the device is not there.</summary>
        private string SendAttempt(Prepared prepared, Capabilities caps, EncodingKind encoding, ChecksumKind checksum, string name, string remotePath,
            string summary, string attemptText)
        {
            string decoder = encoding == EncodingKind.Base64 ? "base64 -d" : "uudecode -o /dev/stdout";
            string sumTool = ChecksumTool(checksum);
            string trim = checksum == ChecksumKind.Cksum ? string.Empty : " s=${s%% *};";

            // The script is typed as three lines, and each of them is a complete command (a one-line "if", a function
            // definition): the shell never answers with its continuation prompt "> " in the middle of the script, which is
            // the fragile part of typing into a console, and no line is longer than the line editor of a small
            // busybox takes (OpenWrt builds it with 512 characters).
            // The data is read by sed up to its end marker line, so that nothing depends on counting bytes that a noisy
            // line may lose, the shell never gets a line of the data (a uuencoded line can hold anything) whatever fails on
            // the way, and no Ctrl-D is needed (it would log a shell out). Ctrl-C, Ctrl-\ and Ctrl-Z are ignored while the
            // data is read: they are what the noise of a bad line is made of now and then, and they would stop the reader.
            string overwriteCheck = _options.Overwrite
                ? "if false; then :; "
                : "if [ -e \"$d\" ]; then printf " + Say("EXISTS") + "; ";
            string[] script =
            {
                DestinationScript(remotePath, name) + "; p=\"$d.smtty~\"",
                "_smtty_end() { s=$(" + sumTool + " <\"$p\");" + trim + " if [ $r -eq 0 ] && [ \"$s\" = " + ShellQuote(prepared.Checksum) + " ]; then mv -f \"$p\" \"$d\" && printf "
                    + Say("OK:%s") + " \"$s\"; else z=$(wc -c <\"$p\" 2>/dev/null); rm -f \"$p\"; printf " + Say("FAIL:%s:%s:%s") + " \"$r\" \"$s\" \"$z\"; fi; }",
                overwriteCheck + "elif ! ( : >\"$p\" ) 2>/dev/null; then printf " + Say("NOWRITE") + "; else trap '' INT QUIT TSTP; printf " + Say("READY")
                    + "; sed -n " + ShellQuote("/^" + _endMarker + "$/q;p") + " | ( " + decoder
                    + " | gunzip -c >\"$p\"; r=$?; cat >/dev/null; exit $r ); r=$?; trap - INT QUIT TSTP; _smtty_end; unset -f _smtty_end 2>/dev/null; fi"
            };

            Report(0, prepared.EncodedLength, "Starting the receiver on the device...");
            _pendingCleanup = DestinationScript(remotePath, name);
            _readerWaiting = false;
            TypeLines(script);
            string answer = WaitForMarker(20000, "receiver start");
            if (answer == "EXISTS" || answer == "NOWRITE")
            {
                _pendingCleanup = null;
            }
            _readerWaiting = answer == "READY";
            if (answer == "EXISTS")
            {
                throw new IOException("The file already exists on the device. Choose \"Overwrite\" to replace it.");
            }
            if (answer == "NOWRITE")
            {
                throw new IOException("The device cannot write to the destination.");
            }
            if (answer != "READY")
            {
                throw new IOException("Unexpected answer of the device: " + answer);
            }
            Pause(250);

            bool intact = SendData(prepared, summary + attemptText);
            if (!intact)
            {
                // A line that came back damaged was committed (the device took a line end out of the noise): the data that the
                // device has is spoiled. End its reader so that it removes the partial file and answers, then try again.
                Trace("Ending the device's reader after a damaged line");
                EndReader();
            }
            else
            {
                _readerWaiting = false; // the end marker has been typed: the reader is over
            }

            Report(prepared.EncodedLength, prepared.EncodedLength, "Unpacking and checking on the device...");
            string result = WaitForResult(prepared);
            _readerWaiting = false;
            _pendingCleanup = null; // the script is over, and it has removed its partial file itself when it failed
            if (!intact)
            {
                return "A damaged line reached the device before it could be taken back.";
            }
            if (result.StartsWith("OK:", StringComparison.Ordinal))
            {
                string deviceSum = result.Substring(3).Trim();
                if (deviceSum == prepared.Checksum)
                {
                    return null;
                }
                return "The checksum reported by the device differs.";
            }
            if (result.StartsWith("FAIL:", StringComparison.Ordinal))
            {
                string[] parts = result.Split(new[] { ':' }, 4);
                string code = parts.Length > 1 ? parts[1] : "?";
                string deviceSum = parts.Length > 2 ? parts[2].Trim() : string.Empty;
                string unpacked = parts.Length > 3 ? parts[3].Trim() : string.Empty;
                string size = unpacked.Length > 0 ? " It unpacked " + unpacked + " bytes; the original has " + prepared.OriginalLength + "." : string.Empty;
                Trace("The device refused the data: gunzip status " + code + ", checksum " + deviceSum + ", unpacked " + unpacked + " bytes");
                if (code == "0")
                {
                    return "Checksum mismatch: the device has " + deviceSum + ", expected " + prepared.Checksum + "." + size;
                }
                return "The device could not unpack the data (gunzip status " + code + "): the text was damaged on the way, or a tool on the device failed." + size + RecentOutput();
            }
            throw new IOException("Unexpected answer of the device: " + result);
        }

        /// <summary>Waits for the device's verdict (the checksum is compared after the data has been unpacked, which takes a while for a big file).</summary>
        private string WaitForResult(Prepared prepared)
        {
            int timeout = 30000 + (int)Math.Min(int.MaxValue / 4, prepared.EncodedLength / 200);
            return WaitForMarker(timeout, "result of the transfer");
        }

        private const int MaxLineAttempts = 8;

        private static readonly byte[] NewLine = { (byte)'\n' };

        // Ctrl-U: erases the line that has been typed and not committed.
        private static readonly byte[] KillLine = { 0x15 };

        /// <summary>
        /// Types the text of the archive, one line at a time, the way a person does at a terminal: a line is typed without its
        /// line end, and the device's terminal sends back what it received. When that is the same as what was typed, the line
        /// end commits the line to the device's reader; when it is not (the noise of the line has changed or lost characters)
        /// the line is erased with Ctrl-U and typed again, so nothing damaged ever reaches the reader. Returns false only
        /// when a damaged line got committed anyway.
        /// </summary>
        private bool SendData(Prepared prepared, string status)
        {
            // The echoed lines are the data itself: only what is wrong with them goes to the log.
            bool wasQuiet = _quietRx;
            _quietRx = true;
            try
            {
                bool echoWorks = true;
                int rate = Math.Max(500, _options.NoEchoBytesPerSecond);
                long sent = 0;
                int lineNumber = 0;
                Trace("Sending " + prepared.EncodedLength + " bytes of text in lines of up to " + BlockChars() + " characters, each checked against the echo; without echo " + rate + " bytes/s");
                using (StreamReader reader = new StreamReader(prepared.EncodedPath, Encoding.ASCII))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        _token.ThrowIfCancellationRequested();
                        lineNumber++;
                        int startedAt = Environment.TickCount;
                        if (lineNumber <= 3 || lineNumber % 100 == 0)
                        {
                            Trace("Line " + lineNumber + ": " + line.Length + " characters, " + sent + " of " + prepared.EncodedLength + " bytes done; starts with " + Escape(line.Substring(0, Math.Min(line.Length, 40)), 80));
                        }
                        if (!SendVerifiedLine(line, lineNumber, ref echoWorks, rate))
                        {
                            return false;
                        }
                        sent += line.Length + 1;
                        Report(sent, prepared.EncodedLength, status + " - sending " + FormatSize(sent) + " of " + FormatSize(prepared.EncodedLength));
                        if (!echoWorks)
                        {
                            // No echo to wait for: keep to the speed limit and keep the receive queue empty.
                            int due = startedAt + (int)((line.Length + 1) * 1000L / rate);
                            while (Environment.TickCount - due < 0)
                            {
                                _token.ThrowIfCancellationRequested();
                                PumpKeepingAnswers(Math.Max(1, Math.Min(50, due - Environment.TickCount)), out _);
                            }
                        }
                    }
                }
                // sed acts on the end marker only when it has read something of the line after it (GNU sed one character, busybox
                // a whole line): an empty line does it. It is not checked against the echo: the device may be answering already.
                _send(NewLine, 0, 1);
                Trace("All " + sent + " bytes sent in " + lineNumber + " lines");
                return true;
            }
            finally
            {
                _quietRx = wasQuiet;
            }
        }

        private bool SendVerifiedLine(string line, int lineNumber, ref bool echoWorks, int rate)
        {
            byte[] text = Encoding.ASCII.GetBytes(line);
            for (int attempt = 1; attempt <= MaxLineAttempts; attempt++)
            {
                _token.ThrowIfCancellationRequested();
                _send(text, 0, text.Length);
                if (!echoWorks)
                {
                    _send(NewLine, 0, 1);
                    return true;
                }

                // 1 s plus what the line itself needs on the wire; the first line has more time, it also finds out whether there is an echo.
                int timeout = (lineNumber == 1 && attempt == 1 ? 2500 : 1000) + (int)(line.Length * 1000L / rate) * 3;
                string echoed = ReadEcho(line.Length, timeout);
                if (echoed == line)
                {
                    _send(NewLine, 0, 1);
                    WaitForLineEnd(1000);
                    return true;
                }

                if (lineNumber == 1 && attempt == 1 && echoed.Length == 0)
                {
                    Trace("The device does not echo what is typed: the lines cannot be checked, the speed limit is used instead.");
                    echoWorks = false;
                    EraseLine();
                    continue;
                }

                _errors++;
                int at = 0;
                while (at < echoed.Length && at < line.Length && echoed[at] == line[at])
                {
                    at++;
                }
                Trace("ECHO MISMATCH in line " + lineNumber + " (attempt " + attempt + ") at character " + at + ": typed "
                    + Escape(line.Substring(Math.Max(0, at - 10), Math.Min(30, line.Length - Math.Max(0, at - 10))), 100)
                    + " but the device echoed " + Escape(echoed.Substring(Math.Max(0, at - 10), Math.Min(30, echoed.Length - Math.Max(0, at - 10))), 120)
                    + " (" + echoed.Length + " of " + line.Length + " characters)");
                if (echoed.IndexOf('\n') >= 0 || echoed.IndexOf('\r') >= 0)
                {
                    Trace("The noise contained a line end: the damaged line has been committed on the device.");
                    return false;
                }
                EraseLine();
            }
            throw new IOException("A line of the data could not be typed to the device without damage in " + MaxLineAttempts
                + " attempts: the line is too noisy (a smaller block size, a lower baud rate or flow control may help)." + RecentOutput());
        }

        /// <summary>Takes what the device echoes of a line that was typed: until all of it is there and the line has been quiet for a moment, or until the time is up.</summary>
        private string ReadEcho(int expectedLength, int timeoutMs)
        {
            StringBuilder echoed = new StringBuilder();
            int deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount - deadline < 0)
            {
                _token.ThrowIfCancellationRequested();
                bool enough = echoed.Length >= expectedLength;
                if (PumpKeepingAnswers(enough ? 12 : 50, out _))
                {
                    echoed.Append((char)_lastByte);
                }
                else if (enough)
                {
                    break; // all of it is there and nothing more comes
                }
            }
            return echoed.ToString();
        }

        /// <summary>Waits for the line end that was typed to come back (the device's terminal echoes it as a line break).</summary>
        private void WaitForLineEnd(int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount - deadline < 0)
            {
                _token.ThrowIfCancellationRequested();
                if (PumpKeepingAnswers(50, out _) && _lastByte == (byte)'\n')
                {
                    return;
                }
            }
            Trace("The echo of a line end did not come back; going on.");
        }

        /// <summary>Erases the line that was typed and not committed. Its echo (the terminal rubs it out) is thrown away.</summary>
        private void EraseLine()
        {
            _send(KillLine, 0, 1);
            Drain(100, 3000);
            _line.Length = 0;
        }

        /// <summary>Ends the device's reader by typing the end marker (after erasing what may be on the line).</summary>
        private void EndReader()
        {
            _send(KillLine, 0, 1);
            Thread.Sleep(50); // not Pause: this also runs after a cancel, when the token is cancelled already
            byte[] end = Encoding.ASCII.GetBytes(_endMarker + "\n\n"); // and an empty line: sed reads ahead
            _send(end, 0, end.Length);
            _readerWaiting = false;
        }

        /// <summary>
        /// When this side gives up (cancel, an error) while a script it typed is still running, the device may be waiting for
        /// more data: the end marker ends its reader (the script then removes the unfinished file by itself), Ctrl-C ends
        /// anything else, and the unfinished file is removed once more to be sure.
        /// </summary>
        private void AbortDevice()
        {
            string cleanup = _pendingCleanup;
            bool readerWaiting = _readerWaiting;
            _pendingCleanup = null;
            _readerWaiting = false;
            if (cleanup == null)
            {
                return;
            }
            try
            {
                if (readerWaiting)
                {
                    EndReader();
                    Thread.Sleep(600);
                }
                SendBytes(new byte[] { 0x03 });
                Thread.Sleep(200);
                SendBytes(new byte[] { 0x03, (byte)'\r' });
                Thread.Sleep(300);
                if (cleanup.Length > 0)
                {
                    SendBytes(Ascii("trap - INT QUIT TSTP; " + cleanup + "; rm -f \"$d.smtty~\"\r"));
                    Thread.Sleep(100);
                }
                while (_readByte(150) != null)
                {
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
            {
                // The line is gone, so the device cannot be told now. It is left in its reader (the console would wait for the
                // rest of the data and eat what is typed next): hand over what ends that, for the next connection to it.
                Trace("The connection is gone, the device could not be told to stop (" + ex.Message + "): the recovery is handed over for the next connection");
                try
                {
                    _options.RecoveryRequired?.Invoke(BuildRecovery(cleanup));
                }
                catch (Exception callbackError) when (callbackError is IOException || callbackError is InvalidOperationException || callbackError is ObjectDisposedException)
                {
                    Trace("The recovery could not be handed over: " + callbackError.Message);
                }
            }
        }

        /// <summary>
        /// What brings a console back that a lost transfer left in the middle of it. For a receive (nothing is removed): Ctrl-C ends the
        /// sender. For a send: Ctrl-U clears the line, the end marker ends the reader (the script then removes the unfinished file by
        /// itself and restores its signals, as it does after any ended transfer), and the last two lines do the same once more in
        /// case there was no script any more. At a plain prompt all of this is harmless: the marker is an unknown command.
        /// </summary>
        private byte[] BuildRecovery(string cleanup)
        {
            if (cleanup.Length == 0)
            {
                return new byte[] { 0x03, (byte)'\r' };
            }
            return Ascii("\u0015" + _endMarker + "\n\ntrap - INT QUIT TSTP\r" + cleanup + "; rm -f \"$d.smtty~\"\r");
        }

        // ----- receiving -----

        public bool Receive(string remotePath, string localPath)
        {
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                throw new IOException("Enter the path of the file on the device.");
            }
            if (File.Exists(localPath) && !_options.Overwrite)
            {
                throw new IOException("The local file already exists: " + localPath);
            }
            string partPath = localPath + ".smtty~";
            bool done = false;
            bool keepPart = false;
            Trace("RECEIVE " + remotePath + " -> " + localPath + "; encoding " + _options.Encoding + ", checksum " + _options.Checksum + ", attempts " + _options.Attempts + ", overwrite " + _options.Overwrite);
            try
            {
                StartShell();
                Capabilities caps = Probe();
                string missing = Missing(caps, "gzip");
                if (missing != null)
                {
                    throw new IOException("The device has no: " + missing + ".");
                }
                EncodingKind encoding = ChooseEncoding(caps, sending: false);
                ChecksumKind checksum = ChooseChecksum(caps);

                int attempts = Math.Max(1, _options.Attempts);
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    string attemptText = attempts > 1 ? " (attempt " + attempt + " of " + attempts + ")" : string.Empty;
                    string failure = ReceiveAttempt(caps, encoding, checksum, remotePath, Path.GetFileName(localPath), partPath, attemptText);
                    if (failure == null)
                    {
                        // From here the unfinished file is the only copy of the checked data: it is kept if it cannot be put in place,
                        // and the old file is replaced in one step (File.Replace), never deleted first.
                        keepPart = true;
                        try
                        {
                            if (File.Exists(localPath))
                            {
                                File.Replace(partPath, localPath, null);
                            }
                            else
                            {
                                File.Move(partPath, localPath);
                            }
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            throw new IOException("The file was received and its checksum matches, but it could not be saved as " + localPath + " (" + ex.Message
                                + "). The checked copy is kept as " + partPath + ": rename it when the file can be replaced.", ex);
                        }
                        keepPart = false;
                        done = true;
                        Report(1, 1, "Done: " + checksum + " matches.");
                        return true;
                    }
                    if (attempt == attempts)
                    {
                        throw new IOException(failure);
                    }
                    _errors++;
                    Report(0, 0, failure + " Trying again...");
                    Drain(500, 5000);
                    StartShell();
                }
                return false;
            }
            catch (OperationCanceledException)
            {
                Trace("Cancelled");
                AbortDevice();
                return false;
            }
            catch (IOException ex)
            {
                Trace("FAILED: " + ex.Message);
                AbortDevice();
                throw;
            }
            finally
            {
                if (!done && !keepPart)
                {
                    try
                    {
                        File.Delete(partPath);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        /// <summary>One try. Returns null on success (the file is complete in <paramref name="partPath"/>), the reason when the data was damaged.</summary>
        private string ReceiveAttempt(Capabilities caps, EncodingKind encoding, ChecksumKind checksum, string remotePath, string storedName,
            string partPath, string attemptText)
        {
            string encoder = encoding == EncodingKind.Base64 ? "base64" : "uuencode " + ShellQuote(storedName);
            string sumTool = ChecksumTool(checksum);
            string trim = checksum == ChecksumKind.Cksum ? string.Empty : " s=${s%% *};";
            string sizeStep = caps.Has("wc") ? "printf " + Say("SIZE:%s") + " \"$(wc -c <\"$f\")\"; " : string.Empty;
            string[] script =
            {
                "f=" + ShellPath(remotePath),
                "if [ ! -f \"$f\" ] || [ ! -r \"$f\" ]; then printf " + Say("NOFILE") + "; else " + sizeStep + "printf " + Say("BEGIN") + "; gzip -c <\"$f\" | " + encoder
                    + "; s=$(" + sumTool + " <\"$f\");" + trim + " printf " + Say("END:%s") + " \"$s\"; fi"
            };

            Report(0, 0, "Starting the sender on the device...");
            _pendingCleanup = string.Empty;
            TypeLines(script);

            long originalLength = -1;
            string answer;
            while (true)
            {
                answer = WaitForMarker(30000, "start of the data");
                if (answer.StartsWith("SIZE:", StringComparison.Ordinal))
                {
                    long.TryParse(answer.Substring(5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out originalLength);
                    continue;
                }
                break;
            }
            if (answer == "NOFILE")
            {
                _pendingCleanup = null;
                throw new IOException("The file was not found on the device, or it cannot be read: " + remotePath);
            }
            if (answer != "BEGIN")
            {
                throw new IOException("Unexpected answer of the device: " + answer);
            }

            long produced = 0;
            long received = 0;
            string endPayload = null;
            string decodeError = null;
            using (RunningChecksum sum = new RunningChecksum(checksum))
            using (BlockingPipe pipe = new BlockingPipe())
            using (FileStream output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                Exception inflateError = null;
                Thread inflater = new Thread(() =>
                {
                    try
                    {
                        using (GZipStream gzip = new GZipStream(pipe, CompressionMode.Decompress))
                        {
                            byte[] buffer = new byte[16384];
                            int count;
                            while ((count = gzip.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                sum.Update(buffer, 0, count);
                                output.Write(buffer, 0, count);
                                Interlocked.Add(ref produced, count);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        inflateError = ex;
                        pipe.Abandon(); // whatever still arrives is not wanted
                    }
                });
                inflater.IsBackground = true;
                inflater.Name = "gzip-base64 inflater";
                inflater.Start();
                try
                {
                    UuDecoder uu = encoding == EncodingKind.UuEncode ? new UuDecoder() : null;
                    _quietRx = true; // thousands of data lines: only the other lines are logged
                    Trace("Receiving the data (the device's own words are logged, the data lines are not)");
                    while (true)
                    {
                        _token.ThrowIfCancellationRequested();
                        string line = ReadLine(30000);
                        if (line == null)
                        {
                            throw new IOException("The device stopped sending data.");
                        }
                        int at = line.IndexOf(_marker, StringComparison.Ordinal);
                        if (at >= 0)
                        {
                            string tag = line.Substring(at + _marker.Length);
                            Trace("RX " + Escape(line, 200));
                            if (tag.StartsWith("END:", StringComparison.Ordinal))
                            {
                                endPayload = tag.Substring(4).Trim();
                                break;
                            }
                            continue;
                        }
                        byte[] decoded = uu != null ? uu.DecodeLine(line) : DecodeBase64Line(line);
                        if (decoded == null)
                        {
                            // Not a line of the archive: a kernel message on the console, or a damaged line.
                            if (line.Trim().Length > 0)
                            {
                                _errors++;
                                Trace("RX skipped, not a line of the archive: " + Escape(line, 200));
                            }
                            continue;
                        }
                        if (decoded.Length > 0)
                        {
                            pipe.Write(decoded, 0, decoded.Length);
                        }
                        received += line.Length + 1;
                        long shown = Interlocked.Read(ref produced);
                        if (originalLength > 0)
                        {
                            Report(Math.Min(shown, originalLength), originalLength, "Receiving " + FormatSize(received) + " of text, unpacked " + FormatSize(shown) + " of " + FormatSize(originalLength) + attemptText);
                        }
                        else
                        {
                            Report(shown, 0, "Receiving " + FormatSize(received) + " of text" + attemptText);
                        }
                    }
                    _pendingCleanup = null; // the sender is done
                    pipe.Complete();
                    inflater.Join();
                    Trace("The data has ended: " + received + " bytes of text, " + Interlocked.Read(ref produced) + " bytes unpacked");
                }
                catch
                {
                    pipe.Abandon();
                    inflater.Join(2000);
                    throw;
                }
                finally
                {
                    _quietRx = false;
                }
                if (inflateError != null)
                {
                    decodeError = "The data could not be unpacked (" + inflateError.Message + "): it was damaged on the way.";
                }
                else
                {
                    output.Flush();
                    string mine = sum.Finish();
                    if (!SameChecksum(mine, endPayload))
                    {
                        decodeError = "Checksum mismatch: received " + mine + ", the device has " + endPayload + ".";
                    }
                }
            }
            if (decodeError != null)
            {
                Trace("The received data was refused: " + decodeError);
            }
            return decodeError;
        }

        private static bool SameChecksum(string a, string b)
        {
            string[] left = a.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string[] right = (b ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", left) == string.Join(" ", right);
        }
    }

    // ---------- text forms ----------

    private static void EncodeText(Stream compressed, Stream output, EncodingKind encoding, string storedName, int blockChars, string endMarker)
    {
        if (encoding == EncodingKind.Base64)
        {
            // One line is one block of the archive: it is checked against the echo as a whole before the device is given it.
            byte[] block = new byte[blockChars / 4 * 3];
            int count;
            while ((count = ReadFull(compressed, block)) > 0)
            {
                byte[] line = Encoding.ASCII.GetBytes(Convert.ToBase64String(block, 0, count) + "\n");
                output.Write(line, 0, line.Length);
            }
        }
        else
        {
            byte[] header = Encoding.ASCII.GetBytes("begin 644 " + UuSafeName(storedName) + "\n");
            output.Write(header, 0, header.Length);
            byte[] data = new byte[UuBytesPerLine];
            int got;
            while ((got = ReadFull(compressed, data)) > 0)
            {
                byte[] line = Encoding.ASCII.GetBytes(UuEncodeLine(data, got) + "\n");
                output.Write(line, 0, line.Length);
            }
            byte[] footer = Encoding.ASCII.GetBytes("`\nend\n");
            output.Write(footer, 0, footer.Length);
        }
        // The end marker is the last line of the file. sed reads ahead (to know whether a line is the last), so it acts on the
        // marker only when something of the next line has come: SendData types an empty line after it, which sed reads and takes
        // away with it, so it never reaches the shell.
        byte[] end = Encoding.ASCII.GetBytes(endMarker + "\n");
        output.Write(end, 0, end.Length);
    }

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int count = stream.Read(buffer, total, buffer.Length - total);
            if (count <= 0)
            {
                break;
            }
            total += count;
        }
        return total;
    }

    private static string UuSafeName(string name)
    {
        StringBuilder text = new StringBuilder();
        foreach (char c in name)
        {
            text.Append(c > ' ' && c < 127 ? c : '_');
        }
        return text.Length == 0 ? "file" : text.ToString();
    }

    private static char UuChar(int value)
    {
        return (value & 0x3F) == 0 ? '`' : (char)((value & 0x3F) + 32);
    }

    private static string UuEncodeLine(byte[] data, int count)
    {
        StringBuilder line = new StringBuilder();
        line.Append(UuChar(count));
        for (int i = 0; i < count; i += 3)
        {
            int b0 = data[i];
            int b1 = i + 1 < count ? data[i + 1] : 0;
            int b2 = i + 2 < count ? data[i + 2] : 0;
            line.Append(UuChar(b0 >> 2));
            line.Append(UuChar(((b0 << 4) & 0x30) | (b1 >> 4)));
            line.Append(UuChar(((b1 << 2) & 0x3C) | (b2 >> 6)));
            line.Append(UuChar(b2 & 0x3F));
        }
        return line.ToString();
    }

    /// <summary>Decodes a line of base64; null for a line that is not one (empty lines and noise).</summary>
    private static byte[] DecodeBase64Line(string line)
    {
        string text = line.Trim();
        if (text.Length == 0 || text.Length % 4 != 0 || text.Length > 1024)
        {
            return null;
        }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/' || (c == '=' && i >= text.Length - 2);
            if (!ok)
            {
                return null;
            }
        }
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class UuDecoder
    {
        /// <summary>Decodes a uuencoded line; an empty array for the header, the footer and the zero-length line, null for noise.</summary>
        public byte[] DecodeLine(string line)
        {
            if (line.StartsWith("begin ", StringComparison.Ordinal) || line == "end")
            {
                return new byte[0];
            }
            if (line.Length == 0)
            {
                return null;
            }
            int length = (line[0] - 32) & 0x3F;
            if (length == 0)
            {
                return new byte[0];
            }
            int groups = (length + 2) / 3;
            string body = line.Substring(1).TrimEnd('\r');
            // Spaces at the end of a line may have been lost on the way; they stand for zero bits.
            if (body.Length < groups * 4)
            {
                body = body.PadRight(groups * 4, ' ');
            }
            if (body.Length > groups * 4 + 2)
            {
                return null;
            }
            byte[] result = new byte[groups * 3];
            for (int g = 0; g < groups; g++)
            {
                int c0 = Six(body[g * 4]);
                int c1 = Six(body[g * 4 + 1]);
                int c2 = Six(body[g * 4 + 2]);
                int c3 = Six(body[g * 4 + 3]);
                if (c0 < 0 || c1 < 0 || c2 < 0 || c3 < 0)
                {
                    return null;
                }
                result[g * 3] = (byte)((c0 << 2) | (c1 >> 4));
                result[g * 3 + 1] = (byte)((c1 << 4) | (c2 >> 2));
                result[g * 3 + 2] = (byte)((c2 << 6) | c3);
            }
            byte[] trimmed = new byte[length];
            Array.Copy(result, trimmed, length);
            return trimmed;
        }

        private static int Six(char c)
        {
            if (c < 32 || c > 96)
            {
                return -1;
            }
            return (c - 32) & 0x3F;
        }
    }

    /// <summary>A stream that one thread writes and another reads (the unpacker): reading waits for data and ends when the writer is complete.</summary>
    private sealed class BlockingPipe : Stream
    {
        private readonly object _gate = new object();
        private readonly Queue<byte[]> _chunks = new Queue<byte[]>();
        private byte[] _current;
        private int _offset;
        private bool _complete;
        private bool _abandoned;

        public void Complete()
        {
            lock (_gate)
            {
                _complete = true;
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>The reader is not interested any more (or has failed): the writer's data is dropped, the reader ends.</summary>
        public void Abandon()
        {
            lock (_gate)
            {
                _abandoned = true;
                _complete = true;
                _chunks.Clear();
                Monitor.PulseAll(_gate);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            byte[] copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            lock (_gate)
            {
                if (_abandoned)
                {
                    return;
                }
                _chunks.Enqueue(copy);
                Monitor.PulseAll(_gate);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                while (_current == null || _offset >= _current.Length)
                {
                    if (_chunks.Count > 0)
                    {
                        _current = _chunks.Dequeue();
                        _offset = 0;
                        continue;
                    }
                    if (_complete)
                    {
                        return 0;
                    }
                    Monitor.Wait(_gate);
                }
                int take = Math.Min(count, _current.Length - _offset);
                Buffer.BlockCopy(_current, _offset, buffer, offset, take);
                _offset += take;
                return take;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " B";
        }
        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        }
        return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }
}
