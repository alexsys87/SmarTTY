using SmarTTY.IO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Regression harness for the ZMODEM protocol (IO/ZModemTransfer.cs). The code under test talks to an in-memory peer
// that is either the other half of the protocol (a sender against a receiver) or a script, and the link between the
// two can damage, drop or swallow bytes the way a bad serial line does.
// Run with: dotnet run --project tests/ZModemTransferTests
internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new List<string>();

    // ---------- in-memory link ----------

    /// <summary>One direction of the link. A tamper function may change or drop (null) any byte by its running index.</summary>
    private sealed class Pipe
    {
        private readonly BlockingCollection<byte> _bytes = new BlockingCollection<byte>();
        private long _index;
        public Func<long, byte, byte?> Tamper;

        public void Write(byte[] data, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                byte b = data[offset + i];
                long index = Interlocked.Increment(ref _index) - 1;
                byte? t = Tamper == null ? b : Tamper(index, b);
                if (t.HasValue)
                {
                    _bytes.Add(t.Value);
                }
            }
        }

        public byte? Read(int timeoutMs)
        {
            return _bytes.TryTake(out byte b, timeoutMs) ? b : (byte?)null;
        }
    }

    /// <summary>One end of the link; what it sends is also recorded.</summary>
    private sealed class End
    {
        public Pipe Out, In;
        private readonly List<byte> _sent = new List<byte>();

        public void Send(byte[] data, int offset, int count)
        {
            lock (_sent)
            {
                _sent.AddRange(data.Skip(offset).Take(count));
            }
            Out.Write(data, offset, count);
        }

        public byte? Read(int timeoutMs)
        {
            return In.Read(timeoutMs);
        }

        public byte[] Sent()
        {
            lock (_sent)
            {
                return _sent.ToArray();
            }
        }
    }

    private static void NewLink(out End sender, out End receiver)
    {
        Pipe a = new Pipe();
        Pipe b = new Pipe();
        sender = new End { Out = a, In = b };   // the sender writes into a, the receiver reads a
        receiver = new End { Out = b, In = a };
    }

    private static ZModemTransfer.Timeouts Fast(int replyMs = 600)
    {
        return new ZModemTransfer.Timeouts
        {
            HandshakeMs = 3000,
            RqinitIntervalMs = 200,
            ReplyMs = replyMs,
            HeaderMs = 400,
            ByteMs = 500,
            TrailerMs = 20,
            DuplicateRequestMs = 100,
            OverAndOutMs = 100
        };
    }

    // ---------- helpers ----------

    private sealed class Progress
    {
        public readonly List<string> Status = new List<string>();
        public int LastErrors;
        public ZModemTransfer.ProgressHandler Handler => (done, total, errors, status) =>
        {
            lock (Status) { Status.Add(status); LastErrors = errors; }
        };
        public string Last { get { lock (Status) { return Status.Count == 0 ? "" : Status[Status.Count - 1]; } } }
    }

    private sealed class Received
    {
        public readonly List<ZModemFileInfo> Infos = new List<ZModemFileInfo>();
        public readonly List<MemoryStream> Streams = new List<MemoryStream>();
        public readonly List<bool> Complete = new List<bool>();
        public HashSet<string> Skip = new HashSet<string>();

        public Func<ZModemFileInfo, Stream> Open => info =>
        {
            if (Skip.Contains(info.RemoteName)) return null;
            lock (this)
            {
                MemoryStream ms = new MemoryStream();
                Infos.Add(info);
                Streams.Add(ms);
                return new NonDisposingStream(ms);
            }
        };

        public Action<ZModemFileInfo, bool> Finished => (info, ok) => { lock (this) { Complete.Add(ok); } };
    }

    /// <summary>A stream the receiver may close without the test losing the content.</summary>
    private sealed class NonDisposingStream : Stream
    {
        private readonly MemoryStream _inner;
        public NonDisposingStream(MemoryStream inner) { _inner = inner; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { _inner.Write(buffer, offset, count); }
    }

    private sealed class FailingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { throw new IOException("disk full"); }
    }

    /// <summary>A source that cannot seek.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner;
        public ForwardOnlyStream(byte[] data) { _inner = new MemoryStream(data); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] RandomData(int n, int seed = 5)
    {
        byte[] d = new byte[n];
        new Random(seed).NextBytes(d);
        return d;
    }

    /// <summary>Data full of the bytes that need escaping, in the awkward combinations (CR after '@', ZDLE, XON/XOFF).</summary>
    private static byte[] NastyData(int n)
    {
        byte[] pattern = { 0x18, 0x11, 0x13, 0x91, 0x93, 0x10, 0x90, 0x40, 0x0D, 0x8D, 0x40, 0x8D, 0x7F, 0xFF, 0x00, 0x2A, 0x18, 0x18, 0x18, 0x18, 0x18, 0x58 };
        byte[] d = new byte[n];
        for (int i = 0; i < n; i++)
        {
            d[i] = i % 3 == 2 ? (byte)(i & 0xFF) : pattern[i % pattern.Length];
        }
        return d;
    }

    private static readonly DateTime SampleTime = new DateTime(2024, 5, 17, 10, 30, 15, DateTimeKind.Utc);

    private static ZModemSendItem Item(string name, byte[] data)
    {
        return new ZModemSendItem(name, new MemoryStream(data), data.Length, SampleTime);
    }

    private sealed class Outcome
    {
        public bool SenderOk, ReceiverOk;
        public Received Rx = new Received();
        public Progress SenderProgress = new Progress();
        public Progress ReceiverProgress = new Progress();
        public End SenderEnd, ReceiverEnd;
    }

    /// <summary>Runs a sender against a receiver over the link until both have finished.</summary>
    private static Outcome Transfer(IList<ZModemSendItem> files, ZModemOptions senderOptions, ZModemOptions receiverOptions,
        ZModemTransfer.Timeouts timeouts, Action<Pipe, Pipe> shapeLink = null, CancellationToken senderToken = default,
        CancellationToken receiverToken = default, Received rx = null)
    {
        NewLink(out End s, out End r);
        shapeLink?.Invoke(s.Out, r.Out);
        Outcome o = new Outcome { SenderEnd = s, ReceiverEnd = r };
        if (rx != null) o.Rx = rx;
        Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(o.Rx.Open, o.Rx.Finished, r.Send, r.Read, receiverToken,
            o.ReceiverProgress.Handler, receiverOptions, timeouts));
        Task<bool> st = Task.Run(() => ZModemTransfer.SendFiles(files, s.Send, s.Read, senderToken,
            o.SenderProgress.Handler, senderOptions, timeouts));
        o.SenderOk = st.Result;
        o.ReceiverOk = rt.Result;
        return o;
    }

    // ---------- hand-made frames (independent of the code under test, except for the CRC and hex header helpers) ----------

    /// <summary>A binary header with a CRC-16 ('A'), with ZDLE, DLE, XON and XOFF escaped as the protocol demands.</summary>
    private static byte[] BinHeader(byte type, byte p0 = 0, byte p1 = 0, byte p2 = 0, byte p3 = 0)
    {
        byte[] h = { type, p0, p1, p2, p3 };
        ushort crc = ZModemTransfer.Crc16(h, 0, 5);
        List<byte> frame = new List<byte> { 0x2A, 0x18, 0x41 };
        foreach (byte b in h.Concat(new[] { (byte)(crc >> 8), (byte)crc }))
        {
            if ((b & 0x7F) == 0x10 || (b & 0x7F) == 0x11 || (b & 0x7F) == 0x13 || b == 0x18)
            {
                frame.Add(0x18);
                frame.Add((byte)(b ^ 0x40));
            }
            else
            {
                frame.Add(b);
            }
        }
        return frame.ToArray();
    }

    /// <summary>A data subpacket with a CRC-16 for plain text (no byte of it needs escaping); <paramref name="damage"/> spoils the CRC.</summary>
    private static byte[] TextSubpacket(string text, byte terminator, bool damage = false)
    {
        byte[] data = Encoding.ASCII.GetBytes(text);
        ushort crc = ZModemTransfer.Crc16(data.Concat(new[] { terminator }).ToArray(), 0, data.Length + 1);
        if (damage) crc ^= 0x0101;
        return data.Concat(new byte[] { 0x18, terminator, (byte)(crc >> 8), (byte)crc }).ToArray();
    }

    private static int CountOf(byte[] haystack, byte[] needle)
    {
        int n = 0;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) n++;
        }
        return n;
    }

    /// <summary>The start of a hex header of this type, as a receiver writes it ("**" ZDLE 'B' and two hex digits).</summary>
    private static byte[] HexStart(byte type)
    {
        return new byte[] { 0x2A, 0x2A, 0x18, 0x42, (byte)"0123456789abcdef"[type >> 4], (byte)"0123456789abcdef"[type & 15] };
    }

    /// <summary>Waits until the receiver under test has sent a hex header of this type (a new one, after <paramref name="from"/> bytes).</summary>
    private static bool WaitForHex(End e, byte type, int from, int timeoutMs = 3000)
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            byte[] sent = e.Sent();
            if (CountOf(sent.Skip(from).ToArray(), HexStart(type)) > 0) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    /// <summary>
    /// Reads frame headers sent by a sender under test and returns the next one's type (CRC-16 binary or hex), or -1.
    /// Data in between is skipped.
    /// </summary>
    private static int NextHeaderType(End e, int timeoutMs = 3000)
    {
        while (true)
        {
            byte? b = e.Read(timeoutMs);
            if (!b.HasValue) return -1;
            if (b != 0x2A) continue;
            byte? c = e.Read(500);
            while (c == 0x2A) c = e.Read(500);
            if (c != 0x18) continue;
            byte? f = e.Read(500);
            if (f == 0x41 || f == 0x43)
            {
                byte? t = e.Read(500);
                if (t == 0x18) t = (byte)(e.Read(500).Value ^ 0x40);
                return t ?? -1;
            }
            if (f == 0x42)
            {
                int hi = "0123456789abcdef".IndexOf((char)e.Read(500).Value);
                int lo = "0123456789abcdef".IndexOf((char)e.Read(500).Value);
                return hi * 16 + lo;
            }
        }
    }

    /// <summary>
    /// Runs our sender against our receiver, but the receiver's ZRINIT is replaced by one with this buffer size and these
    /// flags (as a small embedded receiver would send it).
    /// </summary>
    private static Outcome TransferWithReceiverLimits(byte[] data, int bufferSize, byte flags)
    {
        NewLink(out End s, out End r);
        Outcome o = new Outcome { SenderEnd = s, ReceiverEnd = r };
        byte[] original = ZModemTransfer.BuildHexHeader(ZModemTransfer.ZRINIT, new byte[] { 0, 0, 0, 0x23 }); // CANFDX|CANOVIO|CANFC32
        byte[] replacement = ZModemTransfer.BuildHexHeader(ZModemTransfer.ZRINIT, new byte[] { (byte)bufferSize, (byte)(bufferSize >> 8), 0, flags });
        ZModemTransfer.SendDataHandler rewrite = (d, off, len) =>
        {
            if (len == original.Length && d.Skip(off).Take(len).SequenceEqual(original))
            {
                r.Send(replacement, 0, replacement.Length);
                return;
            }
            r.Send(d, off, len);
        };
        Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(o.Rx.Open, o.Rx.Finished, rewrite, r.Read, CancellationToken.None,
            o.ReceiverProgress.Handler, null, Fast()));
        Task<bool> st = Task.Run(() => ZModemTransfer.SendFiles(new[] { Item("limits.bin", data) }, s.Send, s.Read, CancellationToken.None,
            o.SenderProgress.Handler, null, Fast()));
        o.SenderOk = st.Result;
        o.ReceiverOk = rt.Result;
        return o;
    }

    // ---------- test plumbing ----------

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new Exception("FAIL: " + what);
        _passed++;
    }

    private static void Run(string name, Action test)
    {
        Task t = Task.Run(test);
        bool finished;
        try { finished = t.Wait(60000); }
        catch (AggregateException ex)
        {
            Failures.Add(name + " -> " + ex.InnerException.Message);
            Console.WriteLine("FAIL: " + name + " -> " + ex.InnerException.Message);
            return;
        }
        if (!finished)
        {
            Failures.Add(name + " -> hung");
            Console.WriteLine("FAIL: " + name + " -> hung (no result within 60 s)");
            return;
        }
        Console.WriteLine("PASS: " + name);
    }

    private static int Main()
    {
        // =============== wire format ===============

        Run("crc: standard check values", () =>
        {
            byte[] check = Encoding.ASCII.GetBytes("123456789");
            Check(ZModemTransfer.Crc16(check, 0, check.Length) == 0x31C3, "CRC-16/XMODEM of 123456789");
            Check(ZModemTransfer.Crc32(check, 0, check.Length) == 0xCBF43926u, "CRC-32 of 123456789");
            Check(ZModemTransfer.Crc16(new byte[5], 0, 5) == 0, "CRC-16 of zeros is zero");
        });

        Run("hex header: ZRQINIT is the classic **^XB00000000000000 CR LF XON", () =>
        {
            byte[] frame = ZModemTransfer.BuildHexHeader(ZModemTransfer.ZRQINIT, new byte[4]);
            string expected = "**\u0018B00000000000000\r\u008a\u0011";
            byte[] expectedBytes = expected.Select(c => (byte)c).ToArray();
            Check(frame.SequenceEqual(expectedBytes), "exact bytes: " + BitConverter.ToString(frame));
        });

        Run("hex header: ZACK and ZFIN carry no XON", () =>
        {
            Check(ZModemTransfer.BuildHexHeader(ZModemTransfer.ZACK, new byte[4]).Last() == 0x8A, "ZACK ends with LF");
            Check(ZModemTransfer.BuildHexHeader(ZModemTransfer.ZFIN, new byte[4]).Last() == 0x8A, "ZFIN ends with LF");
            Check(ZModemTransfer.BuildHexHeader(ZModemTransfer.ZRPOS, new byte[4]).Last() == 0x11, "ZRPOS ends with XON");
        });

        Run("file names: directories, reserved characters and DOS device names are made harmless", () =>
        {
            Check(ZModemTransfer.SanitizeFileName("report.txt") == "report.txt", "plain name unchanged");
            Check(ZModemTransfer.SanitizeFileName("../../etc/passwd") == "passwd", "unix traversal");
            Check(ZModemTransfer.SanitizeFileName("..\\..\\windows\\evil.dll") == "evil.dll", "windows traversal");
            Check(ZModemTransfer.SanitizeFileName("C:\\boot.ini") == "boot.ini", "drive letter");
            Check(ZModemTransfer.SanitizeFileName("a:b*c?.txt") == "a_b_c_.txt", "reserved characters");
            Check(ZModemTransfer.SanitizeFileName("CON") == "_CON", "CON");
            Check(ZModemTransfer.SanitizeFileName("nul.txt") == "_nul.txt", "nul.txt");
            Check(ZModemTransfer.SanitizeFileName("com1") == "_com1", "com1");
            Check(ZModemTransfer.SanitizeFileName("console.log") == "console.log", "console is not CON");
            Check(ZModemTransfer.SanitizeFileName("") == "file", "empty");
            Check(ZModemTransfer.SanitizeFileName("..") == "file", "dot dot");
            Check(ZModemTransfer.SanitizeFileName("name. . ") == "name", "trailing dots and spaces");
            Check(ZModemTransfer.SanitizeFileName("tab\tname") == "tab_name", "control characters");
            Check(ZModemTransfer.SanitizeFileName(new string('x', 500)).Length == 200, "long name is cut");
        });

        // =============== sender against receiver ===============

        foreach (bool crc32 in new[] { true, false })
        {
            foreach (bool escapeAll in new[] { false, true })
            {
                foreach (int size in new[] { 0, 1, 127, 1024, 1025, 20000, 300000 })
                {
                    bool c32 = crc32; bool esc = escapeAll; int n = size;
                    Run("round trip: " + n + " bytes, CRC-" + (c32 ? "32" : "16") + (esc ? ", escape all control characters" : ""), () =>
                    {
                        byte[] data = RandomData(n, n + 1);
                        Outcome o = Transfer(new[] { Item("data.bin", data) },
                            new ZModemOptions { UseCrc32 = c32, EscapeControlCharacters = esc },
                            new ZModemOptions { UseCrc32 = c32 },
                            Fast());
                        Check(o.SenderOk, "sender reports success (" + o.SenderProgress.Last + ")");
                        Check(o.ReceiverOk, "receiver reports success (" + o.ReceiverProgress.Last + ")");
                        Check(o.Rx.Streams.Count == 1, "one file arrived");
                        Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical, no padding");
                        Check(o.Rx.Complete.SequenceEqual(new[] { true }), "the file was reported complete");
                        Check(o.Rx.Infos[0].Name == "data.bin" && o.Rx.Infos[0].Length == n, "name and size announced");
                        Check(o.Rx.Infos[0].ModifiedUtc == SampleTime, "modification time announced");
                        Check(o.SenderProgress.LastErrors == 0 && o.ReceiverProgress.LastErrors == 0, "a clean line has no errors");
                    });
                }
            }
        }

        Run("round trip: bytes that need escaping (ZDLE, XON, XOFF, CR after @) survive", () =>
        {
            byte[] data = NastyData(50000);
            Outcome o = Transfer(new[] { Item("nasty.bin", data) }, null, null, Fast());
            Check(o.SenderOk && o.ReceiverOk, "both succeed");
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
            byte[] wire = o.SenderEnd.Sent();
            // After the first header no raw XON/XOFF may be on the wire except the ones the protocol itself adds (after ZCRCW).
            int rawFlowControl = 0;
            for (int i = 0; i < wire.Length; i++)
            {
                if (wire[i] == 0x13 || wire[i] == 0x93) rawFlowControl++;
            }
            Check(rawFlowControl == 0, "no unescaped XOFF on the wire (" + rawFlowControl + ")");
        });

        Run("round trip: every byte value in every position of a subpacket", () =>
        {
            byte[] data = new byte[256 * 5];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)((i + i / 256) & 0xFF);
            Outcome o = Transfer(new[] { Item("all.bin", data) }, new ZModemOptions { SubpacketBytes = 100 }, null, Fast());
            Check(o.SenderOk && o.ReceiverOk, "both succeed");
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
        });

        Run("round trip: small subpackets, escape-all control characters on both sides", () =>
        {
            byte[] data = RandomData(30000, 77);
            Outcome o = Transfer(new[] { Item("x.bin", data) },
                new ZModemOptions { SubpacketBytes = 64 }, new ZModemOptions { EscapeControlCharacters = true }, Fast());
            Check(o.SenderOk && o.ReceiverOk, "both succeed");
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
        });

        Run("round trip: a large stream makes the receiver acknowledge (ZCRCQ) and the sender keep its window", () =>
        {
            byte[] data = RandomData(400000, 3);
            Outcome o = Transfer(new[] { Item("big.bin", data) }, null, null, Fast());
            Check(o.SenderOk && o.ReceiverOk, "both succeed");
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
            string rxText = new string(o.ReceiverEnd.Sent().Select(b => (char)b).ToArray());
            int acks = 0, at = 0;
            while ((at = rxText.IndexOf("B03", at, StringComparison.Ordinal)) >= 0) { acks++; at += 3; }
            Check(acks >= 40, "the receiver acknowledged along the way (" + acks + " ACKs)");
        });

        Run("batch: three files, one of them empty, arrive in order with their names", () =>
        {
            byte[] a = RandomData(5000, 1), c = RandomData(70000, 2);
            Outcome o = Transfer(new[] { Item("first.bin", a), Item("empty.bin", new byte[0]), Item("dir/third.bin", c) }, null, null, Fast());
            Check(o.SenderOk && o.ReceiverOk, "both succeed");
            Check(o.Rx.Infos.Select(i => i.Name).SequenceEqual(new[] { "first.bin", "empty.bin", "third.bin" }), "names, directory part dropped");
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(a) && o.Rx.Streams[1].Length == 0 && o.Rx.Streams[2].ToArray().SequenceEqual(c), "contents");
            Check(o.Rx.Complete.SequenceEqual(new[] { true, true, true }), "all reported complete");
            Check(o.Rx.Infos[0].FilesRemaining == 3 && o.Rx.Infos[2].FilesRemaining == 1, "files remaining announced (" + o.Rx.Infos[0].FilesRemaining + ", " + o.Rx.Infos[2].FilesRemaining + ")");
        });

        Run("batch: a hostile name from the sender is made safe before the receiver sees it", () =>
        {
            byte[] data = RandomData(100);
            Outcome o = Transfer(new[] { new ZModemSendItem("..\\..\\Windows\\System32\\CON", new MemoryStream(data), data.Length, SampleTime) }, null, null, Fast());
            Check(o.SenderOk && o.ReceiverOk, "both succeed");
            Check(o.Rx.Infos[0].Name == "_CON", "sanitized to " + o.Rx.Infos[0].Name);
            Check(o.Rx.Infos[0].RemoteName == "CON", "the sender itself drops the directory part: " + o.Rx.Infos[0].RemoteName);
        });

        Run("batch: the receiver skips a file and the others still arrive", () =>
        {
            byte[] a = RandomData(3000, 1), b = RandomData(3000, 2), c = RandomData(3000, 3);
            Received rx = new Received { Skip = new HashSet<string> { "b.bin" } };
            Outcome o = Transfer(new[] { Item("a.bin", a), Item("b.bin", b), Item("c.bin", c) }, null, null, Fast(), rx: rx);
            Check(o.SenderOk && o.ReceiverOk, "both succeed");
            Check(o.Rx.Infos.Select(i => i.Name).SequenceEqual(new[] { "a.bin", "c.bin" }), "the skipped file never opened");
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(a) && o.Rx.Streams[1].ToArray().SequenceEqual(c), "the others are intact");
            Check(o.SenderProgress.Status.Any(s => s != null && s.StartsWith("Skipped by the receiver")), "the sender reported the skip");
        });

        Run("source: a stream that cannot seek works on a clean line", () =>
        {
            byte[] data = RandomData(50000, 9);
            Outcome o = Transfer(new[] { new ZModemSendItem("fwd.bin", new ForwardOnlyStream(data), data.Length, SampleTime) }, null, null, Fast());
            Check(o.SenderOk && o.ReceiverOk, "both succeed");
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
        });

        Run("noise: a banner and the echo of the command before the frames are skipped", () =>
        {
            byte[] data = RandomData(4000, 4);
            Outcome o = Transfer(new[] { Item("n.bin", data) }, null, null, Fast(), (toReceiver, toSender) =>
            {
                byte[] banner = Encoding.ASCII.GetBytes("rz\r\nrz waiting to receive.**B0100 garbage \u0018\u0018 *\u0018 x\r\n");
                toReceiver.Write(banner, 0, banner.Length);
            });
            Check(o.SenderOk && o.ReceiverOk, "both succeed");
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
        });

        // =============== a damaged line ===============

        foreach (int position in new[] { 2000, 5000, 9000, 20000 })
        {
            int at = position;
            Run("line errors: one flipped byte at wire offset " + at + " is repaired by retransmission", () =>
            {
                byte[] data = RandomData(40000, 6);
                Outcome o = Transfer(new[] { Item("f.bin", data) }, null, null, Fast(), (toReceiver, toSender) =>
                {
                    toReceiver.Tamper = (i, b) => i == at ? (byte)(b ^ 0x20) : b;
                });
                Check(o.SenderOk && o.ReceiverOk, "both succeed (" + o.SenderProgress.Last + " / " + o.ReceiverProgress.Last + ")");
                Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
                Check(o.ReceiverProgress.LastErrors > 0 || o.SenderProgress.LastErrors > 0, "the repair was counted as an error");
            });
        }

        Run("line errors: a dropped byte is repaired", () =>
        {
            byte[] data = RandomData(40000, 7);
            Outcome o = Transfer(new[] { Item("f.bin", data) }, null, null, Fast(), (toReceiver, toSender) =>
            {
                toReceiver.Tamper = (i, b) => i == 7777 ? (byte?)null : b;
            });
            Check(o.SenderOk && o.ReceiverOk, "both succeed (" + o.SenderProgress.Last + " / " + o.ReceiverProgress.Last + ")");
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
        });

        Run("line errors: several damaged places in one transfer, 16-bit CRC", () =>
        {
            byte[] data = RandomData(60000, 8);
            Outcome o = Transfer(new[] { Item("f.bin", data) }, new ZModemOptions { UseCrc32 = false }, new ZModemOptions { UseCrc32 = false }, Fast(), (toReceiver, toSender) =>
            {
                toReceiver.Tamper = (i, b) => i == 3000 || i == 15000 || i == 15001 || i == 40000 ? (byte)(b ^ 0x01) : b;
            });
            Check(o.SenderOk && o.ReceiverOk, "both succeed (" + o.SenderProgress.Last + " / " + o.ReceiverProgress.Last + ")");
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
        });

        Run("line errors: the file offer is damaged and sent again", () =>
        {
            byte[] data = RandomData(3000, 10);
            Outcome o = Transfer(new[] { Item("f.bin", data) }, null, null, Fast(), (toReceiver, toSender) =>
            {
                // The first bytes of the wire are ZRQINIT (20 bytes), then ZFILE and its info subpacket.
                toReceiver.Tamper = (i, b) => i == 45 ? (byte)(b ^ 0x04) : b;
            });
            Check(o.SenderOk && o.ReceiverOk, "both succeed (" + o.SenderProgress.Last + " / " + o.ReceiverProgress.Last + ")");
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
        });

        Run("line errors: the answer to the file offer is lost, the repeated offer is not taken for a second file", () =>
        {
            byte[] data = RandomData(3000, 11);
            Received rx = new Received();
            Outcome o = Transfer(new[] { Item("f.bin", data) }, null, null, Fast(300), (toReceiver, toSender) =>
            {
                // The receiver's own bytes: ZRINIT (about 24 bytes) and then the ZRPOS(0) that answers ZFILE: swallow the ZRPOS.
                toSender.Tamper = (i, b) => i >= 24 && i < 48 ? (byte?)null : b;
            }, rx: rx);
            Check(o.SenderOk && o.ReceiverOk, "both succeed (" + o.SenderProgress.Last + " / " + o.ReceiverProgress.Last + ")");
            Check(rx.Streams.Count == 1, "still one file (" + rx.Streams.Count + ")");
            Check(rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
        });

        Run("line errors: the end-of-file header is damaged and sent again", () =>
        {
            byte[] data = RandomData(2000, 12);
            Outcome o = Transfer(new[] { Item("f.bin", data) }, null, null, Fast(300), (toReceiver, toSender) =>
            {
                // Spoil the ZEOF binary header: it is the last binary header of the sender's stream before ZFIN. Corrupt a
                // byte that is certainly inside it by looking at the stream as it goes by: ZPAD ZDLE 'C' 0x0B.
                byte[] window = new byte[4];
                bool armed = true;
                toReceiver.Tamper = (i, b) =>
                {
                    window[0] = window[1]; window[1] = window[2]; window[2] = window[3]; window[3] = b;
                    if (armed && window[0] == 0x2A && window[1] == 0x18 && window[2] == 0x43 && window[3] == 0x0B)
                    {
                        armed = false;
                        return (byte)(b ^ 0x01); // the frame type: ZEOF becomes something else, the CRC no longer matches
                    }
                    return b;
                };
            });
            Check(o.SenderOk && o.ReceiverOk, "both succeed (" + o.SenderProgress.Last + " / " + o.ReceiverProgress.Last + ")");
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
            Check(o.SenderProgress.LastErrors > 0, "the repeat was counted");
        });

        Run("line errors: lost acknowledgements do not stall the sender for good", () =>
        {
            byte[] data = RandomData(300000, 13);
            int acksDropped = 0;
            Outcome o = Transfer(new[] { Item("f.bin", data) }, null, null, Fast(800), (toReceiver, toSender) =>
            {
                // Swallow the first few hex ZACK headers of the receiver (they start with "**\u0018B03").
                List<byte> recent = new List<byte>();
                int skipping = 0;
                toSender.Tamper = (i, b) =>
                {
                    if (skipping > 0) { skipping--; return null; }
                    recent.Add(b);
                    if (recent.Count > 5) recent.RemoveAt(0);
                    if (recent.Count == 5 && recent[0] == 0x2A && recent[1] == 0x18 && recent[2] == 0x42 && recent[3] == '0' && recent[4] == '3' && acksDropped < 3)
                    {
                        acksDropped++;
                        skipping = 17;
                        return null;
                    }
                    return b;
                };
            });
            Check(o.SenderOk && o.ReceiverOk, "both succeed (" + o.SenderProgress.Last + " / " + o.ReceiverProgress.Last + ")");
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
        });

        Run("line errors: a source that cannot seek fails cleanly when a repeat is needed", () =>
        {
            byte[] data = RandomData(40000, 14);
            Outcome o = Transfer(new[] { new ZModemSendItem("f.bin", new ForwardOnlyStream(data), data.Length, SampleTime) }, null, null, Fast(), (toReceiver, toSender) =>
            {
                toReceiver.Tamper = (i, b) => i == 5000 ? (byte)(b ^ 0x20) : b;
            });
            Check(!o.SenderOk, "the sender reports failure");
            Check(o.SenderProgress.Last.Contains("cannot seek"), "and says why: " + o.SenderProgress.Last);
            Check(!o.ReceiverOk, "the receiver is told to stop");
            Check(o.Rx.Complete.SequenceEqual(new[] { false }), "the partial file is reported incomplete");
        });

        // =============== cancelling and failing ===============

        Run("cancel: the sender's cancel button ends both sides", () =>
        {
            using CancellationTokenSource cts = new CancellationTokenSource();
            byte[] data = RandomData(2000000, 15);
            int count = 0;
            Outcome o = Transfer(new[] { Item("f.bin", data) }, null, null, Fast(), (toReceiver, toSender) =>
            {
                toReceiver.Tamper = (i, b) => { if (Interlocked.Increment(ref count) == 20000) cts.Cancel(); return b; };
            }, senderToken: cts.Token);
            Check(!o.SenderOk, "sender reports failure");
            Check(!o.ReceiverOk, "receiver reports failure");
            Check(o.Rx.Complete.SequenceEqual(new[] { false }), "the partial file is reported incomplete");
            Check(o.ReceiverProgress.Last.Contains("Cancelled by the sender"), "the receiver knows who cancelled: " + o.ReceiverProgress.Last);
            byte[] tail = o.SenderEnd.Sent().Skip(Math.Max(0, o.SenderEnd.Sent().Length - 16)).ToArray();
            Check(tail.Take(8).All(b => b == 0x18), "eight CAN went out");
        });

        Run("cancel: the receiver's cancel button ends both sides", () =>
        {
            using CancellationTokenSource cts = new CancellationTokenSource();
            byte[] data = RandomData(2000000, 16);
            int count = 0;
            Outcome o = Transfer(new[] { Item("f.bin", data) }, null, null, Fast(), (toReceiver, toSender) =>
            {
                toReceiver.Tamper = (i, b) => { if (Interlocked.Increment(ref count) == 20000) cts.Cancel(); return b; };
            }, receiverToken: cts.Token);
            Check(!o.SenderOk, "sender reports failure");
            Check(!o.ReceiverOk, "receiver reports failure");
            Check(o.SenderProgress.Last.Contains("Cancelled by the receiver"), "the sender knows who cancelled: " + o.SenderProgress.Last);
        });

        Run("cancel: five CAN from the peer abort a waiting receiver", () =>
        {
            NewLink(out End s, out End r);
            Received rx = new Received();
            Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(rx.Open, rx.Finished, r.Send, r.Read, CancellationToken.None, null, null, Fast()));
            Thread.Sleep(50);
            byte[] cans = Enumerable.Repeat((byte)0x18, 8).ToArray();
            s.Send(cans, 0, cans.Length);
            Check(rt.Wait(5000) && !rt.Result, "the receiver gave up");
        });

        Run("failure: a receiver that cannot write the file stops the transfer", () =>
        {
            NewLink(out End s, out End r);
            Progress rp = new Progress(), sp = new Progress();
            byte[] data = RandomData(20000, 17);
            Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(info => new FailingWriteStream(), null, r.Send, r.Read, CancellationToken.None, rp.Handler, null, Fast()));
            Task<bool> st = Task.Run(() => ZModemTransfer.SendFiles(new[] { Item("f.bin", data) }, s.Send, s.Read, CancellationToken.None, sp.Handler, null, Fast()));
            Check(st.Result == false && rt.Result == false, "both fail");
            Check(rp.Last.Contains("disk full"), "the reason is reported: " + rp.Last);
        });

        Run("failure: a write to the line that throws closes the file in progress as incomplete", () =>
        {
            NewLink(out End s, out End r);
            Received rx = new Received();
            byte[] data = RandomData(20000, 20);
            int writes = 0;
            // The receiver's line breaks after its third write (ZRINIT, ZRPOS, then the first ZACK).
            ZModemTransfer.SendDataHandler brokenLine = (d, off, len) =>
            {
                if (Interlocked.Increment(ref writes) > 3) throw new IOException("line gone");
                r.Send(d, off, len);
            };
            Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(rx.Open, rx.Finished, brokenLine, r.Read, CancellationToken.None, null, null, Fast()));
            Task<bool> st = Task.Run(() => ZModemTransfer.SendFiles(new[] { Item("f.bin", data) }, s.Send, s.Read, CancellationToken.None, null, null, Fast(300)));
            bool threw = false;
            try { rt.Wait(10000); } catch (AggregateException ex) { threw = ex.InnerException is IOException; }
            Check(threw, "the exception reaches the caller");
            Check(rx.Complete.SequenceEqual(new[] { false }), "the open file was closed as incomplete");
            byte[] cans = Enumerable.Repeat((byte)0x18, 8).ToArray();
            r.Send(cans, 0, cans.Length);
            st.Wait(5000);
        });

        Run("failure: a receiver that cannot create the file skips it", () =>
        {
            NewLink(out End s, out End r);
            byte[] data = RandomData(2000, 18);
            Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(info => throw new IOException("access denied"), null, r.Send, r.Read, CancellationToken.None, null, null, Fast()));
            Task<bool> st = Task.Run(() => ZModemTransfer.SendFiles(new[] { Item("f.bin", data) }, s.Send, s.Read, CancellationToken.None, null, null, Fast()));
            Check(st.Result && rt.Result, "the session still ends normally");
        });

        Run("failure: nobody answers a sender", () =>
        {
            NewLink(out End s, out End r);
            Progress sp = new Progress();
            ZModemTransfer.Timeouts t = Fast();
            t.HandshakeMs = 700;
            bool ok = ZModemTransfer.SendFiles(new[] { Item("f.bin", RandomData(10)) }, s.Send, s.Read, CancellationToken.None, sp.Handler, null, t);
            Check(!ok, "reports failure");
            Check(sp.Last.Contains("Handshake timeout"), "and the reason: " + sp.Last);
            Check(r.Read(0) != null, "meanwhile it asked for a receiver (ZRQINIT)");
        });

        Run("failure: nobody sends to a receiver", () =>
        {
            NewLink(out End s, out End r);
            Progress rp = new Progress();
            bool ok = ZModemTransfer.ReceiveFiles(new Received().Open, null, r.Send, r.Read, CancellationToken.None, rp.Handler, null, Fast());
            Check(!ok, "reports failure");
            Check(rp.Last.Contains("did not start"), "and the reason: " + rp.Last);
        });

        Run("cancel: the cancel button stops a waiting sender quickly", () =>
        {
            NewLink(out End s, out End r);
            using CancellationTokenSource cts = new CancellationTokenSource();
            Task<bool> st = Task.Run(() => ZModemTransfer.SendFiles(new[] { Item("f.bin", RandomData(10)) }, s.Send, s.Read, cts.Token, null, null, new ZModemTransfer.Timeouts()));
            Thread.Sleep(200);
            cts.Cancel();
            Check(st.Wait(3000) && !st.Result, "stopped within a few hundred milliseconds");
        });

        // =============== scripted peers ===============

        Run("security: a ZCOMMAND is read and refused, never acted on", () =>
        {
            NewLink(out End s, out End r);
            Received rx = new Received();
            Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(rx.Open, rx.Finished, r.Send, r.Read, CancellationToken.None, null, null, Fast()));
            // ZCOMMAND header (binary, CRC-16) followed by a data subpacket holding the "command", then ZFIN.
            ZModemTransfer.Timeouts t = Fast();
            Thread.Sleep(50);
            List<byte> frame = new List<byte>();
            byte[] header = { ZModemTransfer.ZCOMMAND, 0, 0, 0, 0 };
            ushort crc = ZModemTransfer.Crc16(header, 0, 5);
            frame.AddRange(new byte[] { 0x2A, 0x18, 0x41 });
            frame.AddRange(header);
            frame.Add((byte)(crc >> 8)); frame.Add((byte)crc);
            byte[] cmd = Encoding.ASCII.GetBytes("rm -rf /\0");
            frame.AddRange(cmd);
            frame.Add(0x18); frame.Add(ZModemTransfer.ZCRCW);
            ushort dcrc = ZModemTransfer.Crc16(cmd.Concat(new[] { ZModemTransfer.ZCRCW }).ToArray(), 0, cmd.Length + 1);
            frame.Add((byte)(dcrc >> 8)); frame.Add((byte)dcrc);
            s.Send(frame.ToArray(), 0, frame.Count);
            byte[] fin = ZModemTransfer.BuildHexHeader(ZModemTransfer.ZFIN, new byte[4]);
            s.Send(fin, 0, fin.Length);
            Check(rt.Wait(5000) && rt.Result, "the session ended normally");
            string reply = new string(r.Sent().Select(b => (char)b).ToArray());
            Check(reply.Contains("B0f"), "the receiver answered ZCOMPL (type 15 = 0f in a hex header)");
            Check(rx.Streams.Count == 0, "no file was created");
        });

        Run("scripted sender: ZDATA at the wrong position makes the receiver ask for the right one", () =>
        {
            NewLink(out End s, out End r);
            Received rx = new Received();
            Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(rx.Open, rx.Finished, r.Send, r.Read, CancellationToken.None, null, null, Fast()));
            // Offer a file with the sender code of this very class, but read the receiver's first answers by hand.
            byte[] data = RandomData(3000, 19);
            Task<bool> st = Task.Run(() => ZModemTransfer.SendFiles(new[] { Item("f.bin", data) }, s.Send, s.Read, CancellationToken.None, null, null, Fast()));
            Check(st.Result && rt.Result, "a normal session is unaffected");
            Check(rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
        });

        Run("sender: types \"rz\" CR before its first ZRQINIT (starts a receiver at a shell prompt); the option turns it off", () =>
        {
            foreach (bool on in new[] { true, false })
            {
                NewLink(out End s, out End r);
                ZModemTransfer.Timeouts t = Fast();
                t.HandshakeMs = 300;
                ZModemOptions options = new ZModemOptions { SendRzCommand = on };
                ZModemTransfer.SendFiles(new[] { Item("f.bin", RandomData(10)) }, s.Send, s.Read, CancellationToken.None, null, options, t);
                byte[] sent = s.Sent();
                bool startsWithRz = sent.Length > 3 && sent[0] == (byte)'r' && sent[1] == (byte)'z' && sent[2] == 0x0D && sent[3] == 0x2A;
                Check(startsWithRz == on, on ? "\"rz\\r\" first, then the ZRQINIT" : "only frames when switched off");
            }
        });

        Run("sender: a receiver with a 2 KB buffer gets the data in ZCRCW segments of at most 2 KB", () =>
        {
            byte[] data = RandomData(20000, 31);
            Outcome o = TransferWithReceiverLimits(data, 2048, 0x23);
            Check(o.SenderOk && o.ReceiverOk, "transfer ok: " + o.SenderProgress.Last + " / " + o.ReceiverProgress.Last);
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
            byte[] sent = o.SenderEnd.Sent();
            int crcw = CountOf(sent, new byte[] { 0x18, ZModemTransfer.ZCRCW });
            int crcq = CountOf(sent, new byte[] { 0x18, ZModemTransfer.ZCRCQ });
            Check(crcw >= 9, "a ZCRCW at the end of every 2 KB (" + crcw + ")");
            Check(crcq == 0, "no streaming ACK requests (" + crcq + ")");
        });

        Run("sender: a receiver that cannot overlap disk and line I/O gets a ZCRCW after every subpacket", () =>
        {
            byte[] data = RandomData(6000, 32);
            Outcome o = TransferWithReceiverLimits(data, 0, 0x21); // CANFDX|CANFC32, no CANOVIO
            Check(o.SenderOk && o.ReceiverOk, "transfer ok: " + o.SenderProgress.Last + " / " + o.ReceiverProgress.Last);
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
            int crcw = CountOf(o.SenderEnd.Sent(), new byte[] { 0x18, ZModemTransfer.ZCRCW });
            Check(crcw >= 5, "one ZCRCW per 1 KB subpacket (" + crcw + ")");
        });

        Run("sender: a ZSKIP while the data streams moves on to the next file at once", () =>
        {
            NewLink(out End s, out End r);
            byte[] big = RandomData(300000, 33);
            List<int> seen = new List<int>();
            Task script = Task.Run(() =>
            {
                byte[] init = ZModemTransfer.BuildHexHeader(ZModemTransfer.ZRINIT, new byte[] { 0, 0, 0, 0x03 }); // CRC-16 only
                r.Send(init, 0, init.Length);
                int files = 0;
                while (true)
                {
                    int type = NextHeaderType(r);
                    seen.Add(type);
                    if (type < 0) return;
                    byte[] answer = null;
                    if (type == ZModemTransfer.ZFILE)
                    {
                        files++;
                        answer = ZModemTransfer.BuildHexHeader(ZModemTransfer.ZRPOS, new byte[4]);
                    }
                    else if (type == ZModemTransfer.ZDATA && files == 1)
                    {
                        answer = ZModemTransfer.BuildHexHeader(ZModemTransfer.ZSKIP, new byte[4]);
                    }
                    else if (type == ZModemTransfer.ZEOF)
                    {
                        answer = init;
                    }
                    else if (type == ZModemTransfer.ZFIN)
                    {
                        answer = ZModemTransfer.BuildHexHeader(ZModemTransfer.ZFIN, new byte[4]);
                        r.Send(answer, 0, answer.Length);
                        return;
                    }
                    if (answer != null) r.Send(answer, 0, answer.Length);
                }
            });
            Progress sp = new Progress();
            bool ok = ZModemTransfer.SendFiles(new[] { Item("skipped.bin", big), Item("small.bin", RandomData(100)) }, s.Send, s.Read,
                CancellationToken.None, sp.Handler, new ZModemOptions { SendRzCommand = false }, Fast());
            script.Wait(5000);
            Check(ok, "the session ended normally: " + sp.Last);
            Check(sp.Status.Any(x => x.Contains("Skipped by the receiver: skipped.bin")), "the skip is reported");
            Check(s.Sent().Length < big.Length / 2, "the skipped file was not streamed to its end (" + s.Sent().Length + " bytes)");
            Check(seen.Count(t => t == ZModemTransfer.ZFILE) == 2, "the second file was offered");
        });

        Run("receiver: data that keeps arriving damaged makes it give up instead of asking forever", () =>
        {
            NewLink(out End s, out End r);
            Progress rp = new Progress();
            Received rx = new Received();
            Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(rx.Open, rx.Finished, r.Send, r.Read, CancellationToken.None, rp.Handler, null, Fast()));
            Check(WaitForHex(r, ZModemTransfer.ZRINIT, 0), "ZRINIT");
            int mark = r.Sent().Length;
            byte[] offer = BinHeader(ZModemTransfer.ZFILE).Concat(TextSubpacket("bad.bin\0" + "100", ZModemTransfer.ZCRCW)).ToArray();
            s.Send(offer, 0, offer.Length);
            Check(WaitForHex(r, ZModemTransfer.ZRPOS, mark), "ZRPOS for the offer");
            for (int i = 0; i < 40 && !rt.IsCompleted; i++)
            {
                byte[] frame = BinHeader(ZModemTransfer.ZDATA).Concat(TextSubpacket("garbage", ZModemTransfer.ZCRCG, damage: true)).ToArray();
                s.Send(frame, 0, frame.Length);
                Thread.Sleep(20);
            }
            Check(rt.Wait(5000) && !rt.Result, "gave up");
            Check(rp.Last.Contains("too many damaged data blocks"), "reported: " + rp.Last);
            Check(rx.Complete.SequenceEqual(new[] { false }), "the file was closed as incomplete");
        });

        Run("receiver: a sender that keeps streaming junk cannot keep it from asking again", () =>
        {
            NewLink(out End s, out End r);
            Received rx = new Received();
            Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(rx.Open, rx.Finished, r.Send, r.Read, CancellationToken.None, null, null, Fast()));
            Check(WaitForHex(r, ZModemTransfer.ZRINIT, 0), "ZRINIT");
            int mark = r.Sent().Length;
            byte[] offer = BinHeader(ZModemTransfer.ZFILE).Concat(TextSubpacket("junk.bin\0" + "100", ZModemTransfer.ZCRCW)).ToArray();
            s.Send(offer, 0, offer.Length);
            Check(WaitForHex(r, ZModemTransfer.ZRPOS, mark), "ZRPOS for the offer");
            mark = r.Sent().Length;
            byte[] junk = Encoding.ASCII.GetBytes("junk without any header ");
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 1500)
            {
                s.Send(junk, 0, junk.Length);
                Thread.Sleep(1);
            }
            int requests = CountOf(r.Sent().Skip(mark).ToArray(), HexStart(ZModemTransfer.ZRPOS));
            Check(requests >= 2, "it asked again while the junk kept coming (" + requests + " ZRPOS in 1.5 s)");
            byte[] cans = Enumerable.Repeat((byte)0x18, 8).ToArray();
            s.Send(cans, 0, cans.Length);
            rt.Wait(5000);
        });

        Run("receiver: ZNAK (the sender could not read our header) repeats the last header", () =>
        {
            NewLink(out End s, out End r);
            Received rx = new Received();
            Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(rx.Open, rx.Finished, r.Send, r.Read, CancellationToken.None, null, null, Fast(2000)));
            Check(WaitForHex(r, ZModemTransfer.ZRINIT, 0), "ZRINIT");
            int mark = r.Sent().Length;
            byte[] nak = BinHeader(ZModemTransfer.ZNAK);
            s.Send(nak, 0, nak.Length);
            Check(WaitForHex(r, ZModemTransfer.ZRINIT, mark, 300), "ZRINIT again at once");
            byte[] offer = BinHeader(ZModemTransfer.ZFILE).Concat(TextSubpacket("n.bin\0" + "5", ZModemTransfer.ZCRCW)).ToArray();
            s.Send(offer, 0, offer.Length);
            Check(WaitForHex(r, ZModemTransfer.ZRPOS, mark), "ZRPOS for the offer");
            mark = r.Sent().Length;
            s.Send(nak, 0, nak.Length);
            Check(WaitForHex(r, ZModemTransfer.ZRPOS, mark, 300), "ZRPOS again at once");
            byte[] cans = Enumerable.Repeat((byte)0x18, 8).ToArray();
            s.Send(cans, 0, cans.Length);
            rt.Wait(5000);
        });

        Run("receiver: the sender's attention string (ZSINIT) goes out before a request to go back", () =>
        {
            NewLink(out End s, out End r);
            Received rx = new Received();
            Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(rx.Open, rx.Finished, r.Send, r.Read, CancellationToken.None, null, null, Fast(2000)));
            Check(WaitForHex(r, ZModemTransfer.ZRINIT, 0), "ZRINIT");
            int mark = r.Sent().Length;
            byte[] sinit = BinHeader(ZModemTransfer.ZSINIT).Concat(TextSubpacket("ATTN!\0", ZModemTransfer.ZCRCW)).ToArray();
            s.Send(sinit, 0, sinit.Length);
            Check(WaitForHex(r, ZModemTransfer.ZACK, mark), "ZSINIT acknowledged");
            mark = r.Sent().Length;
            byte[] offer = BinHeader(ZModemTransfer.ZFILE).Concat(TextSubpacket("a.bin\0" + "100", ZModemTransfer.ZCRCW)).ToArray();
            s.Send(offer, 0, offer.Length);
            Check(WaitForHex(r, ZModemTransfer.ZRPOS, mark), "ZRPOS for the offer");
            Check(CountOf(r.Sent().Skip(mark).ToArray(), Encoding.ASCII.GetBytes("ATTN!")) == 0, "no attention string for the first request");
            mark = r.Sent().Length;
            byte[] damaged = BinHeader(ZModemTransfer.ZDATA).Concat(TextSubpacket("data", ZModemTransfer.ZCRCG, damage: true)).ToArray();
            s.Send(damaged, 0, damaged.Length);
            Check(WaitForHex(r, ZModemTransfer.ZRPOS, mark), "ZRPOS after the damaged data");
            byte[] after = r.Sent().Skip(mark).ToArray();
            int attn = CountOf(after, Encoding.ASCII.GetBytes("ATTN!"));
            Check(attn == 1, "the attention string went out once");
            string text = Encoding.ASCII.GetString(after);
            Check(text.IndexOf("ATTN!", StringComparison.Ordinal) < text.IndexOf("**", StringComparison.Ordinal), "before the ZRPOS");
            byte[] cans = Enumerable.Repeat((byte)0x18, 8).ToArray();
            s.Send(cans, 0, cans.Length);
            rt.Wait(5000);
        });

        Run("receiver: ZFREECNT is answered with plenty of space, not with 0", () =>
        {
            NewLink(out End s, out End r);
            Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(new Received().Open, null, r.Send, r.Read, CancellationToken.None, null, null, Fast(2000)));
            Check(WaitForHex(r, ZModemTransfer.ZRINIT, 0), "ZRINIT");
            int mark = r.Sent().Length;
            byte[] ask = BinHeader(ZModemTransfer.ZFREECNT);
            s.Send(ask, 0, ask.Length);
            Check(WaitForHex(r, ZModemTransfer.ZACK, mark), "answered with ZACK");
            string text = Encoding.ASCII.GetString(r.Sent().Skip(mark).ToArray());
            Check(!text.Contains("B0300000000"), "the free space is not reported as 0");
            byte[] cans = Enumerable.Repeat((byte)0x18, 8).ToArray();
            s.Send(cans, 0, cans.Length);
            rt.Wait(5000);
        });

        Run("line errors: a very noisy line (about one damaged byte in 600, both ways) still gets the file through", () =>
        {
            byte[] data = RandomData(60000, 34);
            Random noise = new Random(35);
            Outcome o = Transfer(new[] { Item("noisy.bin", data) }, null, null, Fast(), (toReceiver, toSender) =>
            {
                toReceiver.Tamper = (i, b) => noise.Next(600) == 0 ? (byte)(b ^ 0x10) : b;
                toSender.Tamper = (i, b) => noise.Next(600) == 0 ? (byte)(b ^ 0x10) : b;
            });
            Check(o.SenderOk && o.ReceiverOk, "transfer ok: " + o.SenderProgress.Last + " / " + o.ReceiverProgress.Last);
            Check(o.Rx.Streams[0].ToArray().SequenceEqual(data), "content identical");
        });

        Run("receiver: a repeated ZRQINIT right after the first reply is not answered twice, a late one is", () =>
        {
            NewLink(out End s, out End r);
            ZModemTransfer.Timeouts t = Fast();
            t.DuplicateRequestMs = 300;
            Task<bool> rt = Task.Run(() => ZModemTransfer.ReceiveFiles(new Received().Open, null, r.Send, r.Read, CancellationToken.None, null, null, t));
            byte[] rq = ZModemTransfer.BuildHexHeader(ZModemTransfer.ZRQINIT, new byte[4]);
            Thread.Sleep(30);
            int before = r.Sent().Length;
            s.Send(rq, 0, rq.Length);
            Thread.Sleep(100);
            Check(r.Sent().Length == before, "no second ZRINIT within the duplicate window");
            Thread.Sleep(350);
            s.Send(rq, 0, rq.Length);
            Thread.Sleep(100);
            Check(r.Sent().Length > before, "a later request is answered");
            byte[] cans = Enumerable.Repeat((byte)0x18, 8).ToArray();
            s.Send(cans, 0, cans.Length);
            rt.Wait(3000);
        });

        Console.WriteLine();
        if (Failures.Count > 0)
        {
            Console.WriteLine(Failures.Count + " test(s) FAILED:");
            foreach (string f in Failures) Console.WriteLine("  " + f);
            return 1;
        }
        Console.WriteLine("All ZMODEM checks passed (" + _passed + " assertions).");
        return 0;
    }
}
