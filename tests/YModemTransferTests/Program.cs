using SmarTTY.IO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Regression harness for the YMODEM protocol (IO/YModemTransfer.cs): the code under test talks to an in-memory peer
// that is either the other half of the protocol or a script that behaves like lrzsz/bootloaders and injects the
// faults seen on real serial lines (banners, lost ACKs, damaged blocks, a peer that vanishes or cancels).
// Run with: dotnet run --project tests/YModemTransferTests
internal static class Program
{
    private const byte SOH = 0x01, STX = 0x02, EOT = 0x04, ACK = 0x06, NAK = 0x15, CAN = 0x18, C = 0x43, SUB = 0x1A;

    private static int _passed;
    private static readonly List<string> Failures = new List<string>();

    // ---------- in-memory link ----------

    private sealed class Pipe
    {
        private readonly BlockingCollection<byte> _bytes = new BlockingCollection<byte>();

        public void Write(byte[] data, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _bytes.Add(data[offset + i]);
            }
        }

        public byte? Read(int timeoutMs)
        {
            return _bytes.TryTake(out byte b, timeoutMs) ? b : (byte?)null;
        }
    }

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

        public void Send(params byte[] data)
        {
            Send(data, 0, data.Length);
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

    private static void NewLink(out End underTest, out End peer)
    {
        Pipe a = new Pipe();
        Pipe b = new Pipe();
        underTest = new End { Out = a, In = b };
        peer = new End { Out = b, In = a };
    }

    // ---------- packets ----------

    private sealed class Packet
    {
        public string Kind; // block, eot, can, timeout, junk
        public byte Number, Complement;
        public byte[] Data;
        public bool StartLarge;
        public bool CheckOk;
    }

    private static Packet ReadPacket(End e, int firstTimeoutMs)
    {
        byte? h = e.Read(firstTimeoutMs);
        while (h == C || h == NAK) // a late handshake byte of our own is not a packet
        {
            h = e.Read(firstTimeoutMs);
        }
        if (!h.HasValue) return new Packet { Kind = "timeout" };
        if (h == EOT) return new Packet { Kind = "eot" };
        if (h == CAN) return new Packet { Kind = "can" };
        if (h != SOH && h != STX) return new Packet { Kind = "junk" };

        int size = h == STX ? 1024 : 128;
        Packet p = new Packet { Kind = "block", Data = new byte[size], StartLarge = h == STX };
        p.Number = e.Read(1000) ?? 0;
        p.Complement = e.Read(1000) ?? 0;
        for (int i = 0; i < size; i++) p.Data[i] = e.Read(1000) ?? 0;
        int hi = e.Read(1000) ?? 0, lo = e.Read(1000) ?? 0;
        p.CheckOk = (ushort)((hi << 8) | lo) == XModemTransfer.CalculateCrc16(p.Data, 0, size);
        p.CheckOk &= (byte)(p.Number + p.Complement) == 255;
        return p;
    }

    private static byte[] Block(byte number, byte[] payload)
    {
        List<byte> l = new List<byte> { payload.Length == 1024 ? STX : SOH, number, (byte)(255 - number) };
        l.AddRange(payload);
        ushort c = XModemTransfer.CalculateCrc16(payload, 0, payload.Length);
        l.Add((byte)(c >> 8));
        l.Add((byte)c);
        return l.ToArray();
    }

    /// <summary>A header block written by hand, independent of the code under test: text with \0 separators, zero padded.</summary>
    private static byte[] HeaderBlock(string text, int size = 128)
    {
        byte[] payload = new byte[size];
        byte[] t = Encoding.UTF8.GetBytes(text);
        Array.Copy(t, payload, t.Length);
        return Block(0, payload);
    }

    private static byte[] EmptyHeaderBlock()
    {
        return Block(0, new byte[128]);
    }

    /// <summary>The data blocks of a file as a YMODEM sender must produce them: 1K blocks, a tail of up to 128 bytes as a 128-byte block, SUB padding.</summary>
    private static List<byte[]> DataPayloads(byte[] data)
    {
        List<byte[]> blocks = new List<byte[]>();
        int offset = 0;
        while (offset < data.Length)
        {
            int left = data.Length - offset;
            int size = left > 128 ? 1024 : 128;
            byte[] block = Enumerable.Repeat(SUB, size).ToArray();
            Array.Copy(data, offset, block, 0, Math.Min(size, left));
            blocks.Add(block);
            offset += size;
        }
        return blocks;
    }

    private static byte[] RandomData(int n, int seed = 11)
    {
        byte[] d = new byte[n];
        new Random(seed).NextBytes(d);
        return d;
    }

    private static XModemTransfer.Timeouts Fast(int ackMs = 600)
    {
        return new XModemTransfer.Timeouts
        {
            SenderHandshakeMs = 3000,
            AckMs = ackMs,
            ReceiverHandshakeAttemptMs = 60,
            HeaderMs = 400,
            ByteMs = 500,
            PurgeQuietMs = 60,
            NextRequestMs = 2000,
            BatchEndRequestMs = 500
        };
    }

    private sealed class Progress
    {
        public readonly List<string> Status = new List<string>();
        public readonly List<long[]> Samples = new List<long[]>(); // done, total, errors
        public XModemTransfer.ProgressHandler Handler => (done, total, errors, status) =>
        {
            lock (Status) { Status.Add(status); Samples.Add(new[] { done, total, errors }); }
        };
        public string Last => Status.Count == 0 ? "" : Status[Status.Count - 1];
        public long LastErrors => Samples.Count == 0 ? 0 : Samples[Samples.Count - 1][2];
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new Exception("FAIL: " + what);
        _passed++;
    }

    private static void Run(string name, Action test)
    {
        Task t = Task.Run(test);
        bool finished;
        try { finished = t.Wait(40000); }
        catch (AggregateException ex)
        {
            Failures.Add(name + " -> " + ex.InnerException.Message);
            Console.WriteLine("FAIL: " + name + " -> " + ex.InnerException.Message);
            return;
        }
        if (!finished)
        {
            Failures.Add(name + " -> hung");
            Console.WriteLine("FAIL: " + name + " -> hung (no result within 40 s)");
            return;
        }
        Console.WriteLine("PASS: " + name);
    }

    private static YModemTransfer.FileToSend F(string name, byte[] data, DateTime? mtime = null)
    {
        return new YModemTransfer.FileToSend(name, new MemoryStream(data), mtime);
    }

    private static bool SendBatch(End u, Progress p, XModemTransfer.Timeouts t, params YModemTransfer.FileToSend[] files)
    {
        return YModemTransfer.SendFiles(files, u.Send, u.Read, CancellationToken.None, p?.Handler, t);
    }

    // ---------- scripted receiver (lrzsz / bootloader-like peer of a sender under test) ----------

    private sealed class RxFile
    {
        public string Name;
        public long Size = -1;
        public long ModSeconds;
        public readonly List<byte> Data = new List<byte>();
        public readonly List<byte> WireNumbers = new List<byte>();
        public readonly List<int> BlockSizes = new List<int>();
        public bool Complete;
    }

    private sealed class RxScript
    {
        public byte[] Initial = { C };
        public bool AckFirstEotAtOnce;        // false: NAK the first EOT (as the YMODEM spec and lrzsz do)
        public int NakFirstArrivalOfPacket;   // 1-based over all packets (header = 1); answered with NAK the first time it arrives
        public int SilentFirstArrivalOfPacket;// ... processed but not answered the first time (a lost ACK)
        public bool NoCAfterHeader;           // the receiver never asks for data after acknowledging the header
        public bool NoCAfterLastEot;          // a bootloader that restarts into the new program: no end-of-batch request
        public bool NoTerminatorAck;          // asks for the end-of-batch block but never acknowledges it
        public int CanAfterPackets;           // CAN CAN instead of answering this packet
        public int ExpectedFiles = 1;
        public int TimeoutMs = 3000;
        public readonly List<RxFile> Files = new List<RxFile>();
        public int Packets, EotSeen;
        public bool BadPacket, TerminatorSeen, TerminatorWasEmpty;
        public int WrongSizeHeaderPackets;    // header blocks that were not 128 bytes
    }

    private static Task Receiver(End e, RxScript s)
    {
        return Task.Run(() =>
        {
            e.Send(s.Initial);
            bool nakedOnce = false, silencedOnce = false;
            while (true)
            {
                // ---- block 0
                Packet h = ReadPacket(e, s.TimeoutMs);
                if (h.Kind == "timeout" || h.Kind == "can") return;
                if (h.Kind != "block" || h.Number != 0) { s.BadPacket = true; return; }
                s.Packets++;
                if (!h.CheckOk) { s.BadPacket = true; e.Send(NAK); continue; }
                if (s.CanAfterPackets > 0 && s.Packets == s.CanAfterPackets) { e.Send(CAN, CAN); return; }
                if (s.NakFirstArrivalOfPacket == s.Packets && !nakedOnce) { nakedOnce = true; e.Send(NAK); continue; }
                if (h.StartLarge) s.WrongSizeHeaderPackets++;
                if (h.Data[0] == 0)
                {
                    s.TerminatorSeen = true;
                    s.TerminatorWasEmpty = h.Data.All(b => b == 0);
                    if (!s.NoTerminatorAck) e.Send(ACK);
                    return;
                }
                RxFile file = s.Files.Count > 0 && !s.Files[s.Files.Count - 1].Complete && s.Files[s.Files.Count - 1].BlockSizes.Count == 0
                    ? s.Files[s.Files.Count - 1] // a repeated header
                    : null;
                if (file == null)
                {
                    int nameEnd = Array.IndexOf(h.Data, (byte)0);
                    file = new RxFile { Name = Encoding.UTF8.GetString(h.Data, 0, nameEnd) };
                    string[] meta = Encoding.ASCII.GetString(h.Data, nameEnd + 1, Array.IndexOf(h.Data, (byte)0, nameEnd + 1) - nameEnd - 1).Split(' ');
                    file.Size = long.Parse(meta[0]);
                    if (meta.Length > 1) file.ModSeconds = Convert.ToInt64(meta[1], 8);
                    s.Files.Add(file);
                }
                if (s.SilentFirstArrivalOfPacket == s.Packets && !silencedOnce) { silencedOnce = true; continue; }
                e.Send(ACK);
                if (!s.NoCAfterHeader) e.Send(C);

                // ---- data
                byte last = 0;
                int eots = 0;
                while (true)
                {
                    Packet p = ReadPacket(e, s.TimeoutMs);
                    if (p.Kind == "timeout" || p.Kind == "can") return;
                    if (p.Kind == "eot")
                    {
                        s.EotSeen++;
                        eots++;
                        if (!s.AckFirstEotAtOnce && eots == 1) { e.Send(NAK); continue; }
                        file.Complete = true;
                        e.Send(ACK);
                        if (s.Files.Count == s.ExpectedFiles && s.NoCAfterLastEot) return;
                        e.Send(C);
                        break;
                    }
                    if (p.Kind != "block") { s.BadPacket = true; return; }
                    s.Packets++;
                    if (!p.CheckOk) { s.BadPacket = true; e.Send(NAK); continue; }
                    if (s.CanAfterPackets > 0 && s.Packets == s.CanAfterPackets) { e.Send(CAN, CAN); return; }
                    if (s.NakFirstArrivalOfPacket == s.Packets && !nakedOnce) { nakedOnce = true; e.Send(NAK); continue; }
                    bool duplicate = file.WireNumbers.Count > 0 && p.Number == last;
                    if (!duplicate)
                    {
                        file.Data.AddRange(p.Data);
                        file.WireNumbers.Add(p.Number);
                        file.BlockSizes.Add(p.Data.Length);
                        last = p.Number;
                    }
                    if (s.SilentFirstArrivalOfPacket == s.Packets && !silencedOnce) { silencedOnce = true; continue; }
                    e.Send(ACK);
                }
            }
        });
    }

    // ---------- scripted sender (peer of a receiver under test) ----------

    private static bool Expect(End e, byte value, int timeoutMs = 3000)
    {
        while (true)
        {
            byte? b = e.Read(timeoutMs);
            if (!b.HasValue) return false;
            if (b.Value == value) return true;
            if (b.Value == C && value != C) continue; // repeated requests of the receiver
            return false;
        }
    }

    private sealed class Targets
    {
        public readonly List<YModemTransfer.FileHeader> Opened = new List<YModemTransfer.FileHeader>();
        public readonly List<YModemTransfer.FileHeader> Received = new List<YModemTransfer.FileHeader>();
        public readonly Dictionary<string, TrackedStream> Streams = new Dictionary<string, TrackedStream>();
        public bool Decline, Throw;

        public Func<YModemTransfer.FileHeader, Stream> Open => h =>
        {
            Opened.Add(h);
            if (Throw) throw new IOException("disk full");
            if (Decline) return null;
            TrackedStream s = new TrackedStream();
            Streams[h.Name] = s;
            return s;
        };

        public Action<YModemTransfer.FileHeader> Done => h => Received.Add(h);
    }

    private class TrackedStream : MemoryStream
    {
        public bool Closed;
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }

    private sealed class FailingStream : TrackedStream
    {
        public override void Write(byte[] buffer, int offset, int count) { throw new IOException("write failed"); }
    }

    private static bool Receive(End u, Targets t, Progress p, XModemTransfer.Timeouts timeouts, CancellationToken token = default)
    {
        return YModemTransfer.ReceiveFiles(t.Open, u.Send, u.Read, token, p?.Handler, t.Done, timeouts);
    }

    /// <summary>Plays a well-behaved YMODEM sender against the receiver under test; returns whether every step went as expected.</summary>
    private static Task Sender(End peer, string headerText, byte[] data)
    {
        return Task.Run(() =>
        {
            if (!Expect(peer, C)) return;
            peer.Send(HeaderBlock(headerText));
            if (!Expect(peer, ACK) || !Expect(peer, C)) return;
            byte n = 1;
            foreach (byte[] payload in DataPayloads(data))
            {
                peer.Send(Block(n++, payload));
                if (!Expect(peer, ACK)) return;
            }
            peer.Send(EOT);
            if (!Expect(peer, NAK)) return;
            peer.Send(EOT);
            if (!Expect(peer, ACK) || !Expect(peer, C)) return;
            peer.Send(EmptyHeaderBlock());
            Expect(peer, ACK);
        });
    }

    private static int Main()
    {
        // =============== file names ===============

        Run("names: path parts, forbidden characters and device names are neutralised", () =>
        {
            Check(YModemTransfer.SanitizeFileName("firmware.bin") == "firmware.bin", "plain name unchanged");
            Check(YModemTransfer.SanitizeFileName("../../Windows/evil.dll") == "evil.dll", "directory traversal cut off");
            Check(YModemTransfer.SanitizeFileName("..\\..\\evil.dll") == "evil.dll", "backslash traversal cut off");
            Check(YModemTransfer.SanitizeFileName("C:\\dir\\a.txt") == "a.txt", "drive and directory cut off");
            Check(YModemTransfer.SanitizeFileName("/etc/passwd") == "passwd", "absolute path cut off");
            Check(YModemTransfer.SanitizeFileName("a:b*c?d\"e<f>g|h.txt") == "a_b_c_d_e_f_g_h.txt", "forbidden characters replaced");
            Check(YModemTransfer.SanitizeFileName("a\u0001b\u007Fc.txt") == "a_b_c.txt", "control characters replaced");
            Check(YModemTransfer.SanitizeFileName("..") == "file", ".. is not a name");
            Check(YModemTransfer.SanitizeFileName(".") == "file", ". is not a name");
            Check(YModemTransfer.SanitizeFileName("dir/") == "file", "a trailing separator leaves no name");
            Check(YModemTransfer.SanitizeFileName("   ") == "file", "blank is not a name");
            Check(YModemTransfer.SanitizeFileName(null, "x.bin") == "x.bin", "null gives the fallback");
            Check(YModemTransfer.SanitizeFileName("name. . ") == "name", "trailing dots and spaces dropped");
            Check(YModemTransfer.SanitizeFileName(".hidden") == ".hidden", "a leading dot is kept");
            Check(YModemTransfer.SanitizeFileName("CON") == "_CON", "device name CON");
            Check(YModemTransfer.SanitizeFileName("nul.txt") == "_nul.txt", "device name with an extension");
            Check(YModemTransfer.SanitizeFileName("Com3.log") == "_Com3.log", "device name COM3, any case");
            Check(YModemTransfer.SanitizeFileName("console.txt") == "console.txt", "a name that only starts like a device is fine");
            string longName = new string('a', 300) + ".bin";
            string cut = YModemTransfer.SanitizeFileName(longName);
            Check(cut.Length == 128 && cut.EndsWith(".bin"), "a long name is cut but keeps its extension");
            Check(YModemTransfer.SanitizeFileName("uml\u00e4ut \u0444\u0430\u0439\u043b.txt") == "uml\u00e4ut \u0444\u0430\u0439\u043b.txt", "non-ASCII letters survive");
        });

        // =============== header block ===============

        Run("header: name, size and time are written and read back", () =>
        {
            DateTime when = new DateTime(2024, 5, 17, 12, 30, 45, DateTimeKind.Utc);
            byte[] payload = YModemTransfer.BuildHeaderPayload("fw v1.2.bin", 123456, when);
            Check(payload.Length == 128, "short header is a 128-byte block");
            string text = Encoding.UTF8.GetString(payload, 0, Array.IndexOf(payload, (byte)0));
            Check(text == "fw v1.2.bin", "name first, ended by a 0");
            YModemTransfer.FileHeader h = YModemTransfer.ParseHeader(payload, payload.Length);
            Check(h != null && h.RawName == "fw v1.2.bin" && h.Name == "fw v1.2.bin", "name read back");
            Check(h.Size == 123456, "size read back");
            Check(h.ModifiedUtc == when, "modification time read back");
        });

        Run("header: layout is 'name NUL size octal-mtime' as lrzsz writes it", () =>
        {
            byte[] payload = YModemTransfer.BuildHeaderPayload("a.bin", 300, new DateTime(1970, 1, 1, 0, 1, 0, DateTimeKind.Utc));
            int nameEnd = Array.IndexOf(payload, (byte)0);
            int metaEnd = Array.IndexOf(payload, (byte)0, nameEnd + 1);
            Check(Encoding.ASCII.GetString(payload, nameEnd + 1, metaEnd - nameEnd - 1) == "300 74", "decimal size, octal seconds (60 = 074)");
            Check(payload.Skip(metaEnd).All(b => b == 0), "zero padded to the end");
        });

        Run("header: no time is simply left out; a path in the name is not sent", () =>
        {
            byte[] payload = YModemTransfer.BuildHeaderPayload("C:\\data\\x.bin", 7, null);
            int nameEnd = Array.IndexOf(payload, (byte)0);
            Check(Encoding.UTF8.GetString(payload, 0, nameEnd) == "x.bin", "only the file name goes out");
            int metaEnd = Array.IndexOf(payload, (byte)0, nameEnd + 1);
            Check(Encoding.ASCII.GetString(payload, nameEnd + 1, metaEnd - nameEnd - 1) == "7", "just the size");
            Check(YModemTransfer.ParseHeader(payload, 128).ModifiedUtc == null, "no time read back");
        });

        Run("header: a very long name moves the header to a 1024-byte block", () =>
        {
            string name = new string('n', 120) + ".bin"; // the name is limited to 128 characters, so 124 bytes + size field overflows 128
            byte[] payload = YModemTransfer.BuildHeaderPayload(name, 1234567890123, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Check(payload.Length == 1024, "large header block");
            YModemTransfer.FileHeader h = YModemTransfer.ParseHeader(payload, payload.Length);
            Check(h.Name == name && h.Size == 1234567890123, "read back from the large block");
        });

        Run("header: parsing is tolerant (no size, size 0, junk, empty batch block)", () =>
        {
            byte[] only = new byte[128]; Encoding.ASCII.GetBytes("only.bin").CopyTo(only, 0);
            YModemTransfer.FileHeader h = YModemTransfer.ParseHeader(only, 128);
            Check(h.Name == "only.bin" && h.Size == -1 && h.ModifiedUtc == null, "a name alone: size unknown");

            byte[] zero = new byte[128]; Encoding.ASCII.GetBytes("z.bin\00 0").CopyTo(zero, 0);
            Check(YModemTransfer.ParseHeader(zero, 128).Size == -1, "size 0 counts as unknown");

            byte[] junk = new byte[128]; Encoding.ASCII.GetBytes("j.bin\0abc 99999999999999999999 x").CopyTo(junk, 0);
            h = YModemTransfer.ParseHeader(junk, 128);
            Check(h.Size == -1 && h.ModifiedUtc == null, "junk metadata is ignored");

            byte[] full = Enumerable.Repeat((byte)'x', 128).ToArray();
            h = YModemTransfer.ParseHeader(full, 128);
            Check(h.RawName.Length == 128, "a name filling the block without a 0 is still read");

            Check(YModemTransfer.ParseHeader(new byte[128], 128) == null, "an empty block ends the batch");

            byte[] evil = new byte[128]; Encoding.UTF8.GetBytes("../../evil.sh\0" + "10").CopyTo(evil, 0);
            h = YModemTransfer.ParseHeader(evil, 128);
            Check(h.RawName == "../../evil.sh" && h.Name == "evil.sh", "the safe name is separate from the raw one");
        });

        Run("send: a non-seekable stream is refused up front (the size must be in the header)", () =>
        {
            bool threw = false;
            try { new YModemTransfer.FileToSend("a.bin", new NonSeekable()); } catch (ArgumentException) { threw = true; }
            Check(threw, "ArgumentException");
        });

        // =============== sender under test, scripted receiver ===============

        foreach (int size in new[] { 1, 127, 128, 129, 1000, 1024, 1025, 1152, 1153, 5000, 1024 * 255 + 7 })
        {
            int n = size;
            Run("sender: " + n + " bytes arrive intact with name and size, 1K blocks and a 128-byte tail", () =>
            {
                NewLink(out End u, out End peer);
                byte[] data = RandomData(n);
                RxScript script = new RxScript();
                Task rx = Receiver(peer, script);
                Progress progress = new Progress();
                bool ok = SendBatch(u, progress, Fast(), F("data.bin", data));
                rx.Wait();
                Check(ok, "send succeeded: " + progress.Last);
                Check(script.TerminatorSeen && script.TerminatorWasEmpty, "the batch ends with an empty block 0");
                Check(!script.BadPacket, "no damaged packet");
                RxFile f = script.Files.Single();
                Check(f.Name == "data.bin" && f.Size == n, "name and size in the header");
                Check(f.Data.Take(n).SequenceEqual(data), "data intact");
                byte[] expectedWire = Enumerable.Range(1, f.WireNumbers.Count).Select(i => (byte)i).ToArray();
                Check(f.WireNumbers.SequenceEqual(expectedWire), "blocks numbered 1, 2, 3...");
                Check(f.BlockSizes.Take(f.BlockSizes.Count - 1).All(x => x == 1024 || n <= 0), "all but the last block are 1K");
                Check(f.Data.Skip(n).All(b => b == SUB), "padding is SUB");
                int tail = n % 1024;
                int expectedLast = tail == 0 ? 1024 : (tail <= 128 ? 128 : 1024);
                Check(f.BlockSizes.Last() == expectedLast, "the last block is " + expectedLast + " bytes");
                Check(script.WrongSizeHeaderPackets == 0, "header is a 128-byte SOH block");
                Check(f.Complete && script.EotSeen == 2, "EOT sent again after the receiver's NAK");
                Check(progress.Last == "Transfer complete.", "final status");
                Check(progress.Samples.Last()[0] == n && progress.Samples.Last()[1] == n, "progress ends at the file size");
            });
        }

        Run("sender: an empty file is a header, an EOT and the end of the batch", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript();
            Task rx = Receiver(peer, script);
            bool ok = SendBatch(u, null, Fast(), F("empty.bin", new byte[0]));
            rx.Wait();
            Check(ok, "send succeeded");
            RxFile f = script.Files.Single();
            Check(f.Name == "empty.bin" && f.Size == 0 && f.Data.Count == 0 && f.Complete, "empty file delivered");
            Check(script.TerminatorSeen, "batch ended");
        });

        Run("sender: three files in a batch, in order, each with its own numbering", () =>
        {
            NewLink(out End u, out End peer);
            byte[] a = RandomData(3000, 1), b = RandomData(10, 2), c = RandomData(2048, 3);
            RxScript script = new RxScript { ExpectedFiles = 3 };
            Task rx = Receiver(peer, script);
            Progress progress = new Progress();
            bool ok = SendBatch(u, progress, Fast(), F("a.bin", a), F("b.bin", b), F("c.bin", c));
            rx.Wait();
            Check(ok, "send succeeded: " + progress.Last);
            Check(script.Files.Select(f => f.Name).SequenceEqual(new[] { "a.bin", "b.bin", "c.bin" }), "names in order");
            Check(script.Files[0].Data.Take(3000).SequenceEqual(a), "file 1 intact");
            Check(script.Files[1].Data.Take(10).SequenceEqual(b), "file 2 intact");
            Check(script.Files[2].Data.Take(2048).SequenceEqual(c), "file 3 intact");
            Check(script.Files.All(f => f.WireNumbers.First() == 1), "every file restarts at block 1");
            Check(script.Files.All(f => f.Complete), "every file ended with EOT");
            Check(script.TerminatorSeen && script.TerminatorWasEmpty, "one empty block ends the whole batch");
            Check(progress.Samples.Last()[0] == 3000 + 10 + 2048 && progress.Samples.Last()[1] == 3000 + 10 + 2048, "progress counts all files together");
            Check(progress.Status.Any(s => s.Contains("(2/3)")), "status names the file position");
        });

        Run("sender: the modification time travels in the header", () =>
        {
            NewLink(out End u, out End peer);
            DateTime when = new DateTime(2023, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            RxScript script = new RxScript();
            Task rx = Receiver(peer, script);
            Check(SendBatch(u, null, Fast(), F("t.bin", new byte[5], when)), "send succeeded");
            rx.Wait();
            Check(script.Files.Single().ModSeconds == (long)(when - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds, "seconds since 1970 in octal");
        });

        Run("sender: a path in the file name is not sent", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript();
            Task rx = Receiver(peer, script);
            Check(SendBatch(u, null, Fast(), F("C:\\Users\\me\\secret dir\\x.bin", new byte[5])), "send succeeded");
            rx.Wait();
            Check(script.Files.Single().Name == "x.bin", "name only");
        });

        Run("sender: a receiver that ACKs the first EOT at once is fine", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript { AckFirstEotAtOnce = true };
            Task rx = Receiver(peer, script);
            Check(SendBatch(u, null, Fast(), F("a.bin", RandomData(2000))), "send succeeded");
            rx.Wait();
            Check(script.EotSeen == 1, "only one EOT was needed");
            Check(script.TerminatorSeen, "batch ended");
        });

        Run("sender: a banner and echo before the first 'C' are ignored", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript { Initial = Encoding.ASCII.GetBytes("rb -y\r\nReady to receive\r\n").Concat(new[] { C }).ToArray() };
            Task rx = Receiver(peer, script);
            Check(SendBatch(u, null, Fast(), F("a.bin", RandomData(500))), "send succeeded");
            rx.Wait();
            Check(script.Files.Single().Data.Take(500).SequenceEqual(RandomData(500)), "data intact");
        });

        Run("sender: repeated 'C' requests piling up do not disturb the acknowledgements", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(4000);
            RxScript script = new RxScript { Initial = new[] { C, C, C, C, C } };
            Task rx = Receiver(peer, script);
            Check(SendBatch(u, null, Fast(), F("a.bin", data)), "send succeeded");
            rx.Wait();
            Check(script.Files.Single().Data.Take(4000).SequenceEqual(data), "data intact");
            Check(!script.BadPacket, "no extra or damaged packets");
        });

        Run("sender: a NAK for the header and for a data block makes it resend", () =>
        {
            foreach (int nakPacket in new[] { 1, 3 })
            {
                NewLink(out End u, out End peer);
                byte[] data = RandomData(4000);
                RxScript script = new RxScript { NakFirstArrivalOfPacket = nakPacket };
                Task rx = Receiver(peer, script);
                Progress progress = new Progress();
                Check(SendBatch(u, progress, Fast(), F("a.bin", data)), "send succeeded (NAK at packet " + nakPacket + ")");
                rx.Wait();
                Check(script.Files.Single().Data.Take(4000).SequenceEqual(data), "data intact, repeated block written once");
                Check(progress.LastErrors >= 1, "the repeat is counted as an error");
                Check(progress.Status.Any(s => s.StartsWith("Retrying")), "retry reported");
            }
        });

        Run("sender: a lost ACK (header or data) is recovered by a resend after the timeout", () =>
        {
            foreach (int silent in new[] { 1, 2 })
            {
                NewLink(out End u, out End peer);
                byte[] data = RandomData(3000);
                RxScript script = new RxScript { SilentFirstArrivalOfPacket = silent };
                Task rx = Receiver(peer, script);
                Check(SendBatch(u, null, Fast(300), F("a.bin", data)), "send succeeded (ACK of packet " + silent + " lost)");
                rx.Wait();
                Check(script.Files.Single().Data.Take(3000).SequenceEqual(data), "data intact, repeated packet written once");
            }
        });

        Run("sender: a receiver that never asks for data after the header makes it give up", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript { NoCAfterHeader = true };
            Task rx = Receiver(peer, script);
            XModemTransfer.Timeouts t = Fast(); t.NextRequestMs = 400;
            Progress progress = new Progress();
            Check(!SendBatch(u, progress, t, F("a.bin", RandomData(100))), "send failed");
            Check(progress.Last.Contains("did not ask for the data"), "reason reported: " + progress.Last);
            Check(u.Sent().Skip(u.Sent().Length - 2).SequenceEqual(new[] { CAN, CAN }), "the peer is told to stop with CAN CAN");
        });

        Run("sender: a receiver that restarts after the file (no request for the end-of-batch block) is a success", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript { NoCAfterLastEot = true };
            Task rx = Receiver(peer, script);
            Progress progress = new Progress();
            Check(SendBatch(u, progress, Fast(), F("a.bin", RandomData(1500))), "send succeeded: " + progress.Last);
            rx.Wait();
            Check(progress.Last.Contains("did not ask for the end-of-batch"), "the situation is reported");
            Check(script.Files.Single().Complete, "the file was acknowledged");
        });

        Run("sender: an unacknowledged end-of-batch block does not fail a delivered file", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript { NoTerminatorAck = true };
            Task rx = Receiver(peer, script);
            Progress progress = new Progress();
            Check(SendBatch(u, progress, Fast(200), F("a.bin", RandomData(1500))), "send succeeded: " + progress.Last);
            rx.Wait();
            Check(progress.Last.Contains("end-of-batch block was not acknowledged"), "reported: " + progress.Last);
        });

        Run("sender: CAN CAN from the receiver ends the transfer", () =>
        {
            foreach (int canAt in new[] { 1, 2 })
            {
                NewLink(out End u, out End peer);
                RxScript script = new RxScript { CanAfterPackets = canAt };
                Task rx = Receiver(peer, script);
                Progress progress = new Progress();
                Check(!SendBatch(u, progress, Fast(), F("a.bin", RandomData(5000))), "send failed (CAN at packet " + canAt + ")");
                Check(progress.Last == "Cancelled by the receiver.", "reported: " + progress.Last);
            }
        });

        Run("sender: no receiver at all gives up after the handshake time", () =>
        {
            NewLink(out End u, out End peer);
            XModemTransfer.Timeouts t = Fast(); t.SenderHandshakeMs = 500;
            Progress progress = new Progress();
            Check(!SendBatch(u, progress, t, F("a.bin", RandomData(10))), "send failed");
            Check(progress.Last.StartsWith("Handshake timeout"), "reported: " + progress.Last);
            Check(u.Sent().Length == 2 && u.Sent()[0] == CAN, "only CAN CAN was sent: nothing was thrown at a silent line");
        });

        Run("sender: a checksum receiver (NAK instead of 'C') is not served and the reason is clear", () =>
        {
            NewLink(out End u, out End peer);
            peer.Send(NAK);
            XModemTransfer.Timeouts t = Fast(); t.SenderHandshakeMs = 500;
            Progress progress = new Progress();
            Check(!SendBatch(u, progress, t, F("a.bin", RandomData(10))), "send failed");
            Check(progress.Last.Contains("CRC-16"), "says YMODEM needs CRC-16: " + progress.Last);
        });

        Run("sender: a dead receiver after the header ends in bounded retries", () =>
        {
            NewLink(out End u, out End peer);
            peer.Send(C);
            Progress progress = new Progress();
            Check(!SendBatch(u, progress, Fast(50), F("a.bin", RandomData(10))), "send failed");
            Check(progress.Last.Contains("did not accept the header"), "reported: " + progress.Last);
            int headers = u.Sent().Count(b => b == SOH); // 128-byte header blocks (a SOH byte in the data is possible but the header holds none)
            Check(headers >= 10 && headers <= 12, "about ten attempts, not an endless stream (" + headers + ")");
        });

        Run("sender: cancel stops it quickly and tells the receiver", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript { TimeoutMs = 2000 };
            Task rx = Receiver(peer, script);
            using CancellationTokenSource cts = new CancellationTokenSource();
            Progress progress = new Progress();
            bool ok = true;
            Task send = Task.Run(() => ok = YModemTransfer.SendFiles(new[] { F("big.bin", RandomData(1024 * 200)) }, u.Send, u.Read, cts.Token, (d, t, e, s) =>
            {
                progress.Handler(d, t, e, s);
                if (d >= 10 * 1024) cts.Cancel();
            }, Fast()));
            Check(send.Wait(5000), "returned promptly");
            Check(!ok && progress.Last == "Transfer cancelled.", "reported: " + progress.Last);
            Check(u.Sent().Skip(u.Sent().Length - 2).SequenceEqual(new[] { CAN, CAN }), "CAN CAN sent");
        });

        Run("sender: a file that shrinks while being sent fails instead of sending a damaged file", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript();
            Task rx = Receiver(peer, script);
            ShrinkingStream shrinking = new ShrinkingStream(3000, 1500);
            Progress progress = new Progress();
            bool ok = YModemTransfer.SendFiles(new[] { new YModemTransfer.FileToSend("s.bin", shrinking) }, u.Send, u.Read, CancellationToken.None, progress.Handler, Fast());
            Check(!ok && progress.Last.Contains("shorter"), "reported: " + progress.Last);
        });

        Run("sender: a failing send callback (closed connection) surfaces as an exception", () =>
        {
            NewLink(out End u, out End peer);
            peer.Send(C);
            bool threw = false;
            try
            {
                YModemTransfer.SendFiles(new[] { F("a.bin", new byte[10]) }, (d, o, l) => throw new IOException("closed"), u.Read, CancellationToken.None, null, Fast());
            }
            catch (IOException) { threw = true; }
            Check(threw, "IOException propagates to the caller");
        });

        // =============== receiver under test, scripted sender ===============

        foreach (int size in new[] { 1, 127, 128, 129, 1024, 1025, 5000, 1024 * 255 + 7 })
        {
            int n = size;
            Run("receiver: " + n + " bytes arrive intact, the padding is cut off by the announced size", () =>
            {
                NewLink(out End u, out End peer);
                byte[] data = RandomData(n);
                Task tx = Sender(peer, "data.bin\0" + n + " 14000000000", data);
                Targets targets = new Targets();
                Progress progress = new Progress();
                bool ok = Receive(u, targets, progress, Fast());
                tx.Wait();
                Check(ok, "receive succeeded: " + progress.Last);
                Check(targets.Streams["data.bin"].ToArray().SequenceEqual(data), "exactly the file, no padding");
                Check(targets.Streams["data.bin"].Closed, "the file is closed");
                Check(targets.Received.Single().Name == "data.bin" && targets.Received.Single().Size == n, "completion reported with the header");
                Check(targets.Received.Single().ModifiedUtc.HasValue, "modification time read");
                Check(progress.Last == "File received successfully.", "final status: " + progress.Last);
                Check(u.Sent()[0] == C, "the receiver asked with 'C'");
                Check(progress.Samples.Where(s => s[1] == n).Any(s => s[0] == n), "progress reaches the announced size");
            });
        }

        Run("receiver: the first EOT is NAKed, the second ACKed (a damaged byte must not end a file)", () =>
        {
            NewLink(out End u, out End peer);
            bool sawNak = false, sawAck = false;
            Task tx = Task.Run(() =>
            {
                Expect(peer, C);
                peer.Send(HeaderBlock("a.bin\0" + 10));
                Expect(peer, ACK); Expect(peer, C);
                peer.Send(Block(1, DataPayloads(RandomData(10))[0]));
                Expect(peer, ACK);
                peer.Send(EOT);
                sawNak = Expect(peer, NAK);
                peer.Send(EOT);
                sawAck = Expect(peer, ACK);
                Expect(peer, C);
                peer.Send(EmptyHeaderBlock());
                Expect(peer, ACK);
            });
            Targets targets = new Targets();
            Check(Receive(u, targets, null, Fast()), "receive succeeded");
            tx.Wait();
            Check(sawNak && sawAck, "NAK then ACK");
        });

        Run("receiver: two files in a batch, each in its own stream, then 'N files received'", () =>
        {
            NewLink(out End u, out End peer);
            byte[] a = RandomData(3000, 1), b = RandomData(77, 2);
            bool ok = true;
            Task tx = Task.Run(() =>
            {
                ok &= Expect(peer, C);
                foreach (var f in new[] { new { Name = "a.bin", Data = a }, new { Name = "b.bin", Data = b } })
                {
                    peer.Send(HeaderBlock(f.Name + "\0" + f.Data.Length));
                    ok &= Expect(peer, ACK) && Expect(peer, C);
                    byte n = 1;
                    foreach (byte[] payload in DataPayloads(f.Data)) { peer.Send(Block(n++, payload)); ok &= Expect(peer, ACK); }
                    peer.Send(EOT); ok &= Expect(peer, NAK);
                    peer.Send(EOT); ok &= Expect(peer, ACK) && Expect(peer, C);
                }
                peer.Send(EmptyHeaderBlock());
                ok &= Expect(peer, ACK);
            });
            Targets targets = new Targets();
            Progress progress = new Progress();
            Check(Receive(u, targets, progress, Fast()), "receive succeeded: " + progress.Last);
            tx.Wait();
            Check(ok, "the sender saw every expected answer");
            Check(targets.Streams["a.bin"].ToArray().SequenceEqual(a) && targets.Streams["b.bin"].ToArray().SequenceEqual(b), "both files intact");
            Check(targets.Opened.Select(h => h.Index).SequenceEqual(new[] { 1, 2 }), "files numbered in the batch");
            Check(targets.Received.Count == 2 && targets.Streams.Values.All(s => s.Closed), "both completed and closed");
            Check(progress.Last == "2 files received successfully.", "final status: " + progress.Last);
        });

        Run("receiver: no size announced keeps the padding (it cannot be told from data)", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(100);
            Task tx = Sender(peer, "nosize.bin", data);
            Targets targets = new Targets();
            Progress progress = new Progress();
            Check(Receive(u, targets, progress, Fast()), "receive succeeded");
            tx.Wait();
            byte[] got = targets.Streams["nosize.bin"].ToArray();
            Check(got.Length == 128 && got.Take(100).SequenceEqual(data) && got.Skip(100).All(b => b == SUB), "whole 128-byte block");
            Check(targets.Opened.Single().Size == -1, "size reported as unknown");
            Check(progress.Samples.Where(s => s[0] > 0).All(s => s[1] == 0), "progress has no total");
        });

        Run("receiver: a hostile name is reduced to a plain file name before it reaches the caller", () =>
        {
            NewLink(out End u, out End peer);
            Task tx = Sender(peer, "..\\..\\Windows\\System32\\evil.dll\0" + 10, RandomData(10));
            Targets targets = new Targets();
            Check(Receive(u, targets, null, Fast()), "receive succeeded");
            tx.Wait();
            Check(targets.Opened.Single().Name == "evil.dll", "safe name: " + targets.Opened.Single().Name);
            Check(targets.Opened.Single().RawName.Contains("System32"), "the raw name is still available for display");
        });

        Run("receiver: a plain XMODEM sender (block 1 right after 'C') is detected, with a clear reason", () =>
        {
            NewLink(out End u, out End peer);
            Task tx = Task.Run(() =>
            {
                Expect(peer, C);
                peer.Send(Block(1, DataPayloads(RandomData(100))[0]));
            });
            Targets targets = new Targets();
            Progress progress = new Progress();
            Check(!Receive(u, targets, progress, Fast()), "receive failed");
            tx.Wait();
            Check(progress.Last.Contains("does not use YMODEM") && progress.Last.Contains("XMODEM"), "reason: " + progress.Last);
            Check(targets.Opened.Count == 0, "no file was created");
            Check(u.Sent().Skip(u.Sent().Length - 2).SequenceEqual(new[] { CAN, CAN }), "CAN CAN sent");
        });

        Run("receiver: a damaged header block is NAKed and the repeat is accepted", () =>
        {
            NewLink(out End u, out End peer);
            bool gotNak = false;
            Task tx = Task.Run(() =>
            {
                Expect(peer, C);
                byte[] bad = HeaderBlock("a.bin\0" + 10);
                bad[bad.Length - 1] ^= 0xFF; // broken CRC
                peer.Send(bad);
                gotNak = Expect(peer, NAK);
                peer.Send(HeaderBlock("a.bin\0" + 10));
                Expect(peer, ACK); Expect(peer, C);
                peer.Send(Block(1, DataPayloads(RandomData(10))[0]));
                Expect(peer, ACK);
                peer.Send(EOT); Expect(peer, NAK); peer.Send(EOT); Expect(peer, ACK); Expect(peer, C);
                peer.Send(EmptyHeaderBlock()); Expect(peer, ACK);
            });
            Targets targets = new Targets();
            Progress progress = new Progress();
            Check(Receive(u, targets, progress, Fast()), "receive succeeded: " + progress.Last);
            tx.Wait();
            Check(gotNak, "NAK for the damaged header");
            Check(progress.LastErrors >= 1, "counted as an error");
            Check(targets.Streams["a.bin"].ToArray().SequenceEqual(RandomData(10)), "file intact");
        });

        Run("receiver: a damaged data block is NAKed once the line is quiet, the repeat is accepted", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(3000);
            bool gotNak = false;
            Task tx = Task.Run(() =>
            {
                Expect(peer, C);
                peer.Send(HeaderBlock("a.bin\0" + 3000));
                Expect(peer, ACK); Expect(peer, C);
                List<byte[]> blocks = DataPayloads(data);
                byte[] bad = Block(1, blocks[0]);
                bad[10] ^= 0x55;
                peer.Send(bad);
                gotNak = Expect(peer, NAK);
                byte n = 1;
                foreach (byte[] payload in blocks) { peer.Send(Block(n++, payload)); Expect(peer, ACK); }
                peer.Send(EOT); Expect(peer, NAK); peer.Send(EOT); Expect(peer, ACK); Expect(peer, C);
                peer.Send(EmptyHeaderBlock()); Expect(peer, ACK);
            });
            Targets targets = new Targets();
            Check(Receive(u, targets, null, Fast()), "receive succeeded");
            tx.Wait();
            Check(gotNak, "NAK for the damaged block");
            Check(targets.Streams["a.bin"].ToArray().SequenceEqual(data), "file intact, nothing written from the damaged block");
        });

        Run("receiver: a repeated data block (lost ACK) is acknowledged but written once", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(2500);
            Task tx = Task.Run(() =>
            {
                Expect(peer, C);
                peer.Send(HeaderBlock("a.bin\0" + 2500));
                Expect(peer, ACK); Expect(peer, C);
                List<byte[]> blocks = DataPayloads(data);
                byte n = 1;
                foreach (byte[] payload in blocks)
                {
                    peer.Send(Block(n, payload)); Expect(peer, ACK);
                    if (n == 1) { peer.Send(Block(n, payload)); Expect(peer, ACK); } // the sender did not see the first ACK
                    n++;
                }
                peer.Send(EOT); Expect(peer, NAK); peer.Send(EOT); Expect(peer, ACK); Expect(peer, C);
                peer.Send(EmptyHeaderBlock()); Expect(peer, ACK);
            });
            Targets targets = new Targets();
            Check(Receive(u, targets, null, Fast()), "receive succeeded");
            tx.Wait();
            Check(targets.Streams["a.bin"].ToArray().SequenceEqual(data), "no duplicated data");
        });

        Run("receiver: a repeated header (lost ACK or 'C') is acknowledged again and the file is created once", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(1500);
            Task tx = Task.Run(() =>
            {
                Expect(peer, C);
                peer.Send(HeaderBlock("a.bin\0" + 1500));
                Expect(peer, ACK); Expect(peer, C);
                peer.Send(HeaderBlock("a.bin\0" + 1500)); // sender timed out and repeated it
                Expect(peer, ACK); Expect(peer, C);
                byte n = 1;
                foreach (byte[] payload in DataPayloads(data)) { peer.Send(Block(n++, payload)); Expect(peer, ACK); }
                peer.Send(EOT); Expect(peer, NAK); peer.Send(EOT); Expect(peer, ACK); Expect(peer, C);
                peer.Send(EmptyHeaderBlock()); Expect(peer, ACK);
            });
            Targets targets = new Targets();
            Check(Receive(u, targets, null, Fast()), "receive succeeded");
            tx.Wait();
            Check(targets.Opened.Count == 1, "one file opened");
            Check(targets.Streams["a.bin"].ToArray().SequenceEqual(data), "file intact");
        });

        Run("receiver: a lost block (gap) aborts instead of producing a corrupt file", () =>
        {
            NewLink(out End u, out End peer);
            Task tx = Task.Run(() =>
            {
                Expect(peer, C);
                peer.Send(HeaderBlock("a.bin\0" + 3000));
                Expect(peer, ACK); Expect(peer, C);
                List<byte[]> blocks = DataPayloads(RandomData(3000));
                peer.Send(Block(1, blocks[0])); Expect(peer, ACK);
                peer.Send(Block(3, blocks[2])); // block 2 lost
            });
            Targets targets = new Targets();
            Progress progress = new Progress();
            Check(!Receive(u, targets, progress, Fast()), "receive failed");
            tx.Wait();
            Check(progress.Last.Contains("block 3 arrived while block 2 was expected"), "reason: " + progress.Last);
            Check(targets.Streams["a.bin"].Closed, "the partial file is closed");
        });

        Run("receiver: a file that ends short of the announced size is not acknowledged", () =>
        {
            NewLink(out End u, out End peer);
            bool gotAck = true;
            Task tx = Task.Run(() =>
            {
                Expect(peer, C);
                peer.Send(HeaderBlock("a.bin\0" + 5000));
                Expect(peer, ACK); Expect(peer, C);
                peer.Send(Block(1, DataPayloads(RandomData(5000))[0])); Expect(peer, ACK);
                peer.Send(EOT); Expect(peer, NAK);
                peer.Send(EOT);
                gotAck = Expect(peer, ACK, 500);
            });
            Targets targets = new Targets();
            Progress progress = new Progress();
            Check(!Receive(u, targets, progress, Fast()), "receive failed");
            tx.Wait();
            Check(!gotAck, "no ACK for a truncated file");
            Check(progress.Last.Contains("ended after 1024 of 5000 bytes"), "reason: " + progress.Last);
            Check(targets.Received.Count == 0, "not reported as received");
        });

        Run("receiver: extra blocks beyond the announced size are acknowledged but not written", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(1024);
            Task tx = Task.Run(() =>
            {
                Expect(peer, C);
                peer.Send(HeaderBlock("a.bin\0" + 1024));
                Expect(peer, ACK); Expect(peer, C);
                peer.Send(Block(1, DataPayloads(data)[0])); Expect(peer, ACK);
                peer.Send(Block(2, new byte[1024])); Expect(peer, ACK);
                peer.Send(EOT); Expect(peer, NAK); peer.Send(EOT); Expect(peer, ACK); Expect(peer, C);
                peer.Send(EmptyHeaderBlock()); Expect(peer, ACK);
            });
            Targets targets = new Targets();
            Check(Receive(u, targets, null, Fast()), "receive succeeded");
            tx.Wait();
            Check(targets.Streams["a.bin"].ToArray().SequenceEqual(data), "file is exactly the announced size");
        });

        Run("receiver: an empty batch (just the empty block) is a clean end", () =>
        {
            NewLink(out End u, out End peer);
            Task tx = Task.Run(() => { Expect(peer, C); peer.Send(EmptyHeaderBlock()); Expect(peer, ACK); });
            Targets targets = new Targets();
            Progress progress = new Progress();
            Check(Receive(u, targets, progress, Fast()), "receive succeeded");
            tx.Wait();
            Check(targets.Opened.Count == 0, "nothing opened");
        });

        Run("receiver: a 1024-byte header block is accepted", () =>
        {
            NewLink(out End u, out End peer);
            Task tx = Task.Run(() =>
            {
                Expect(peer, C);
                peer.Send(HeaderBlock("big.bin\0" + 10, 1024));
                Expect(peer, ACK); Expect(peer, C);
                peer.Send(Block(1, DataPayloads(RandomData(10))[0]));
                Expect(peer, ACK);
                peer.Send(EOT); Expect(peer, NAK); peer.Send(EOT); Expect(peer, ACK); Expect(peer, C);
                peer.Send(EmptyHeaderBlock()); Expect(peer, ACK);
            });
            Targets targets = new Targets();
            Check(Receive(u, targets, null, Fast()), "receive succeeded");
            tx.Wait();
            Check(targets.Streams["big.bin"].ToArray().SequenceEqual(RandomData(10)), "file intact");
        });

        Run("receiver: text before the handshake (banner, echo) is not taken for the header", () =>
        {
            NewLink(out End u, out End peer);
            peer.Send(Encoding.ASCII.GetBytes("sb a.bin\r\n"));
            byte[] data = RandomData(300);
            Task tx = Sender(peer, "a.bin\0" + 300, data);
            Targets targets = new Targets();
            Check(Receive(u, targets, null, Fast()), "receive succeeded");
            tx.Wait();
            Check(targets.Streams["a.bin"].ToArray().SequenceEqual(data), "file intact");
        });

        Run("receiver: the target can be declined; nothing is created and the sender is stopped", () =>
        {
            NewLink(out End u, out End peer);
            Task tx = Task.Run(() => { Expect(peer, C); peer.Send(HeaderBlock("a.bin\0" + 10)); });
            Targets targets = new Targets { Decline = true };
            Progress progress = new Progress();
            Check(!Receive(u, targets, progress, Fast()), "receive failed");
            tx.Wait();
            Check(progress.Last.Contains("declined"), "reason: " + progress.Last);
            Check(!u.Sent().Contains(ACK), "the header was not acknowledged");
            Check(u.Sent().Skip(u.Sent().Length - 2).SequenceEqual(new[] { CAN, CAN }), "CAN CAN sent");
        });

        Run("receiver: a target that cannot be created aborts cleanly with its reason", () =>
        {
            NewLink(out End u, out End peer);
            Task tx = Task.Run(() => { Expect(peer, C); peer.Send(HeaderBlock("a.bin\0" + 10)); });
            Targets targets = new Targets { Throw = true };
            Progress progress = new Progress();
            Check(!Receive(u, targets, progress, Fast()), "receive failed");
            tx.Wait();
            Check(progress.Last.Contains("Cannot create a.bin") && progress.Last.Contains("disk full"), "reason: " + progress.Last);
        });

        Run("receiver: a failing target stream aborts with CAN CAN and no exception", () =>
        {
            NewLink(out End u, out End peer);
            Task tx = Task.Run(() =>
            {
                Expect(peer, C);
                peer.Send(HeaderBlock("a.bin\0" + 1000));
                Expect(peer, ACK); Expect(peer, C);
                peer.Send(Block(1, DataPayloads(RandomData(1000))[0]));
            });
            Progress progress = new Progress();
            bool ok = YModemTransfer.ReceiveFiles(h => new FailingStream(), u.Send, u.Read, CancellationToken.None, progress.Handler, null, Fast());
            tx.Wait();
            Check(!ok && progress.Last.Contains("Cannot write the file"), "reason: " + progress.Last);
            Check(u.Sent().Skip(u.Sent().Length - 2).SequenceEqual(new[] { CAN, CAN }), "CAN CAN sent");
        });

        Run("receiver: CAN CAN from the sender ends the transfer (at the header and in the data)", () =>
        {
            foreach (bool inData in new[] { false, true })
            {
                NewLink(out End u, out End peer);
                Task tx = Task.Run(() =>
                {
                    Expect(peer, C);
                    if (!inData) { peer.Send(CAN, CAN); return; }
                    peer.Send(HeaderBlock("a.bin\0" + 3000));
                    Expect(peer, ACK); Expect(peer, C);
                    peer.Send(Block(1, DataPayloads(RandomData(3000))[0])); Expect(peer, ACK);
                    peer.Send(CAN, CAN);
                });
                Targets targets = new Targets();
                Progress progress = new Progress();
                Check(!Receive(u, targets, progress, Fast()), "receive failed (in data: " + inData + ")");
                tx.Wait();
                Check(progress.Last == "Cancelled by the sender.", "reported: " + progress.Last);
                Check(targets.Streams.Values.All(s => s.Closed), "streams closed");
            }
        });

        Run("receiver: no sender at all gives up after the handshake attempts", () =>
        {
            NewLink(out End u, out End peer);
            Progress progress = new Progress();
            Check(!Receive(u, new Targets(), progress, Fast()), "receive failed");
            Check(progress.Last.Contains("did not respond"), "reason: " + progress.Last);
            int requests = u.Sent().Count(b => b == C);
            Check(requests == 10, "ten 'C' requests, then it stops (" + requests + ")");
        });

        Run("receiver: a sender that vanishes after the header gives up after bounded requests", () =>
        {
            NewLink(out End u, out End peer);
            Task tx = Task.Run(() => { Expect(peer, C); peer.Send(HeaderBlock("a.bin\0" + 100)); });
            Targets targets = new Targets();
            Progress progress = new Progress();
            Check(!Receive(u, targets, progress, Fast()), "receive failed");
            tx.Wait();
            Check(progress.Last.Contains("did not send the data of a.bin"), "reason: " + progress.Last);
            Check(targets.Streams["a.bin"].Closed, "the file is closed");
        });

        Run("receiver: a sender that goes silent mid-file is given up on after bounded NAKs", () =>
        {
            NewLink(out End u, out End peer);
            Task tx = Task.Run(() =>
            {
                Expect(peer, C);
                peer.Send(HeaderBlock("a.bin\0" + 3000));
                Expect(peer, ACK); Expect(peer, C);
                peer.Send(Block(1, DataPayloads(RandomData(3000))[0])); Expect(peer, ACK);
            });
            Progress progress = new Progress();
            Check(!Receive(u, new Targets(), progress, Fast()), "receive failed");
            tx.Wait();
            Check(progress.Last.Contains("stopped responding"), "reason: " + progress.Last);
            Check(u.Sent().Count(b => b == NAK) <= 12, "bounded number of NAKs");
        });

        Run("receiver: cancel stops it quickly", () =>
        {
            NewLink(out End u, out End peer);
            using CancellationTokenSource cts = new CancellationTokenSource();
            Progress progress = new Progress();
            bool ok = true;
            Task rx = Task.Run(() => ok = Receive(u, new Targets(), progress, Fast(), cts.Token));
            Thread.Sleep(150);
            cts.Cancel();
            Check(rx.Wait(3000), "returned promptly");
            Check(!ok && progress.Last == "Transfer cancelled.", "reported: " + progress.Last);
        });

        // =============== both halves of this implementation together ===============

        foreach (int size in new[] { 0, 1, 1023, 1024, 1025, 70000 })
        {
            int n = size;
            Run("loopback: " + n + " bytes from our sender to our receiver", () =>
            {
                NewLink(out End a, out End b);
                byte[] data = RandomData(n);
                Targets targets = new Targets();
                Progress sp = new Progress(), rp = new Progress();
                Task<bool> rx = Task.Run(() => Receive(b, targets, rp, Fast()));
                DateTime when = new DateTime(2022, 2, 2, 2, 2, 2, DateTimeKind.Utc);
                bool sent = SendBatch(a, sp, Fast(), F("loop.bin", data, when));
                Check(rx.Wait(20000) && rx.Result, "receiver finished: " + rp.Last);
                Check(sent, "sender finished: " + sp.Last);
                Check(targets.Streams["loop.bin"].ToArray().SequenceEqual(data), "file intact");
                Check(targets.Received.Single().ModifiedUtc == when, "modification time arrived");
                Check(sp.LastErrors == 0 && rp.LastErrors == 0, "no errors on a clean line");
            });
        }

        Run("loopback: a batch of four files (one empty) with special names", () =>
        {
            NewLink(out End a, out End b);
            var files = new[]
            {
                new { Name = "one.bin", Data = RandomData(5000, 1) },
                new { Name = "two words.txt", Data = RandomData(1, 2) },
                new { Name = "empty", Data = new byte[0] },
                new { Name = "\u0444\u0430\u0439\u043b.dat", Data = RandomData(1500, 3) }
            };
            Targets targets = new Targets();
            Progress rp = new Progress();
            Task<bool> rx = Task.Run(() => Receive(b, targets, rp, Fast()));
            bool sent = SendBatch(a, null, Fast(), files.Select(f => F(f.Name, f.Data)).ToArray());
            Check(rx.Wait(20000) && rx.Result, "receiver finished: " + rp.Last);
            Check(sent, "sender finished");
            foreach (var f in files)
            {
                Check(targets.Streams.ContainsKey(f.Name) && targets.Streams[f.Name].ToArray().SequenceEqual(f.Data), "file " + f.Name + " intact");
            }
            Check(rp.Last == "4 files received successfully.", "final status: " + rp.Last);
        });

        Console.WriteLine();
        if (Failures.Count > 0)
        {
            Console.WriteLine(Failures.Count + " YMODEM check(s) FAILED:");
            foreach (string f in Failures) Console.WriteLine("  " + f);
            return 1;
        }
        Console.WriteLine("All YMODEM checks passed (" + _passed + " assertions).");
        return 0;
    }

    private sealed class NonSeekable : MemoryStream
    {
        public override bool CanSeek => false;
    }

    /// <summary>Reports <c>declared</c> bytes but delivers only <c>actual</c> of them.</summary>
    private sealed class ShrinkingStream : Stream
    {
        private readonly long _declared;
        private readonly byte[] _data;
        private long _position;

        public ShrinkingStream(long declared, int actual)
        {
            _declared = declared;
            _data = RandomData(actual);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _declared;
        public override long Position { get => _position; set => _position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Max(0, Math.Min(count, _data.Length - _position));
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
