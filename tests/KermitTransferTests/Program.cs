using SmarTTY.IO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Regression harness for the Kermit protocol (IO/KermitTransfer.cs): the sender and the receiver of the code under test
// talk to each other over an in-memory link that can echo, corrupt and drop bytes, or to a script that plays a misbehaving
// peer. Real Kermit programs (C-Kermit, G-Kermit) are checked separately by tests/KermitInterop.
// Run with: dotnet run --project tests/KermitTransferTests
internal static class Program
{
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

    /// <summary>One end of the link. What it sends arrives at the other end, after an optional fault filter and an optional echo.</summary>
    private sealed class End
    {
        public Pipe Out, In;
        public Func<byte[], byte[]> Filter;   // may corrupt or drop bytes on their way to the other end
        public bool EchoToSelf;               // a line that echoes everything back to the sender
        private readonly List<byte> _sent = new List<byte>();

        public void Send(byte[] data, int offset, int count)
        {
            byte[] chunk = data.Skip(offset).Take(count).ToArray();
            lock (_sent)
            {
                _sent.AddRange(chunk);
            }
            if (EchoToSelf)
            {
                In.Write(chunk, 0, chunk.Length);
            }
            byte[] onTheWire = Filter != null ? Filter(chunk) : chunk;
            Out.Write(onTheWire, 0, onTheWire.Length);
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

    private static void NewLink(out End a, out End b)
    {
        Pipe x = new Pipe();
        Pipe y = new Pipe();
        a = new End { Out = x, In = y };
        b = new End { Out = y, In = x };
    }

    /// <summary>Flips bits in and drops bytes of a stream at a fixed pseudo-random rate.</summary>
    private static Func<byte[], byte[]> Noise(int seed, double corruptRate, double dropRate)
    {
        Random random = new Random(seed);
        object gate = new object();
        return chunk =>
        {
            List<byte> result = new List<byte>(chunk.Length);
            lock (gate)
            {
                foreach (byte b in chunk)
                {
                    double roll = random.NextDouble();
                    if (roll < dropRate) continue;
                    result.Add(roll < dropRate + corruptRate ? (byte)(b ^ (1 << random.Next(8))) : b);
                }
            }
            return result.ToArray();
        };
    }

    // ---------- data ----------

    private static byte[] RandomData(int size, int seed = 1)
    {
        byte[] data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Every byte value, then runs (compressible), then text: exercises every quoting rule.</summary>
    private static byte[] TrickyData()
    {
        List<byte> data = new List<byte>();
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 256; i++) data.Add((byte)i);
        }
        foreach (byte b in new byte[] { 0, 1, (byte)'#', (byte)'&', (byte)'~', 0x7F, 0x80, 0xFF, (byte)' ' })
        {
            for (int i = 0; i < 200; i++) data.Add(b);
        }
        data.AddRange(Encoding.ASCII.GetBytes("#&~#&~ ~~~~ ####  &&&&\r\n\r\n"));
        data.AddRange(RandomData(3000, 7));
        return data.ToArray();
    }

    private static KermitTransfer.Timeouts Fast()
    {
        return new KermitTransfer.Timeouts
        {
            SenderInitTotalMs = 3000,
            SenderInitRetryMs = 300,
            ReceiverInitTotalMs = 3000,
            ReceiverNudgeMs = 300,
            AckMs = 400,
            PacketMs = 400,
            ByteMs = 150
        };
    }

    private sealed class Progress
    {
        public readonly List<string> Status = new List<string>();
        public long LastDone, LastTotal, MaxTotal;
        public int Errors;

        public void Handler(long done, long total, int errors, string status)
        {
            lock (Status)
            {
                LastDone = done;
                LastTotal = total;
                MaxTotal = Math.Max(MaxTotal, total);
                Errors = errors;
                if (!string.IsNullOrEmpty(status)) Status.Add(status);
            }
        }

        public string Last()
        {
            lock (Status)
            {
                return Status.Count == 0 ? string.Empty : Status[Status.Count - 1];
            }
        }
    }

    private sealed class Outcome
    {
        public bool SendOk, ReceiveOk;
        public readonly Dictionary<string, MemoryStream> Files = new Dictionary<string, MemoryStream>();
        public readonly List<string> Discarded = new List<string>();
        public Progress SenderProgress = new Progress(), ReceiverProgress = new Progress();
        public End Sender, Receiver;
        public byte[] Get(string name) => Files[name].ToArray();
    }

    private static KermitTransfer.Source Src(string name, byte[] data)
    {
        return new KermitTransfer.Source(name, data.Length, () => new MemoryStream(data));
    }

    /// <summary>Runs a sender and a receiver against each other and waits for both.</summary>
    private static Outcome Transfer(IList<KermitTransfer.Source> files, KermitTransfer.Settings senderSettings = null,
        KermitTransfer.Settings receiverSettings = null, Action<End, End> configureLink = null, CancellationToken senderToken = default,
        CancellationToken receiverToken = default)
    {
        NewLink(out End s, out End r);
        configureLink?.Invoke(s, r);
        Outcome outcome = new Outcome { Sender = s, Receiver = r };
        KermitTransfer.Timeouts timeouts = Fast();
        Task<bool> rx = Task.Run(() => KermitTransfer.ReceiveFiles(
            name =>
            {
                MemoryStream ms = new MemoryStream();
                lock (outcome.Files) { outcome.Files[name] = ms; }
                return ms;
            },
            name => { lock (outcome.Discarded) { outcome.Discarded.Add(name); } },
            r.Send, r.Read, receiverToken, outcome.ReceiverProgress.Handler, receiverSettings ?? new KermitTransfer.Settings(), timeouts));
        outcome.SendOk = KermitTransfer.SendFiles(files, s.Send, s.Read, senderToken, outcome.SenderProgress.Handler,
            senderSettings ?? new KermitTransfer.Settings(), timeouts);
        outcome.ReceiveOk = rx.Wait(15000) && rx.Result;
        return outcome;
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

    private static string Text(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    private static int Main()
    {
        // =============== block checks ===============

        Run("CRC-16/KERMIT of \"123456789\" is 0x2189", () =>
        {
            byte[] data = Encoding.ASCII.GetBytes("123456789");
            Check(KermitTransfer.Crc16(data, 0, data.Length) == 0x2189, "standard check value");
            Check(KermitTransfer.Crc16(data, 0, 0) == 0, "empty input");
        });

        Run("6-bit and 12-bit checks of a known packet body", () =>
        {
            // The famous NAK(0): MARK '#' ' ' 'N' '3'. Body "# N": 35 + 32 + 78 = 145, (145 + 2) & 63 = 19, 19 + 32 = '3'.
            byte[] body = Encoding.ASCII.GetBytes("# N");
            Check(KermitTransfer.Checksum6(body, 0, 3) == 19, "6-bit check");
            Check(KermitTransfer.Checksum12(body, 0, 3) == 145, "12-bit check");
        });

        Run("the receiver's first packet is the classic NAK(0): ^A # space N 3 CR", () =>
        {
            NewLink(out End u, out End peer);
            Task<bool> rx = Task.Run(() => KermitTransfer.ReceiveFiles(n => new MemoryStream(), null, u.Send, u.Read,
                CancellationToken.None, null, new KermitTransfer.Settings(), Fast()));
            byte[] first = new byte[6];
            for (int i = 0; i < first.Length; i++)
            {
                byte? b = peer.Read(2000);
                Check(b.HasValue, "a byte arrives");
                first[i] = b.Value;
            }
            Check(first.SequenceEqual(new byte[] { 0x01, (byte)'#', (byte)' ', (byte)'N', (byte)'3', 13 }), "frame bytes");
            rx.Wait(5000);
        });

        // =============== sender <-> receiver ===============

        foreach (int size in new[] { 0, 1, 50, 93, 94, 95, 1000, 1999, 2000, 2001, 50000 })
        {
            int n = size;
            Run("loopback: file of " + n + " bytes arrives intact (defaults: CRC-16, long packets, repeat)", () =>
            {
                byte[] data = RandomData(n);
                Outcome o = Transfer(new[] { Src("a.bin", data) });
                Check(o.SendOk && o.ReceiveOk, "both sides succeeded: " + o.SenderProgress.Last() + " / " + o.ReceiverProgress.Last());
                Check(o.Get("a.bin").SequenceEqual(data), "content identical");
                Check(o.SenderProgress.LastDone == n, "sender progress ends at the file size");
                Check(o.Sender.Sent().Length > 0 && o.Receiver.Sent().Length > 0, "both directions used");
            });
        }

        Run("loopback: every byte value, runs and prefix characters survive all negotiations", () =>
        {
            byte[] data = TrickyData();
            foreach (byte check in new[] { (byte)'1', (byte)'2', (byte)'3' })
            {
                foreach (bool longPackets in new[] { true, false })
                {
                    foreach (byte eightBit in new[] { (byte)'Y', (byte)'&', (byte)'N', (byte)'`' })
                    {
                        foreach (byte repeat in new byte[] { (byte)'~', 0 })
                        {
                            KermitTransfer.Settings s = new KermitTransfer.Settings
                            {
                                BlockCheck = check, LongPackets = longPackets, EightBitPrefix = eightBit, RepeatPrefix = repeat
                            };
                            byte[] input = eightBit == (byte)'N' ? data.Select(b => (byte)(b & 0x7F)).ToArray() : data;
                            Outcome o = Transfer(new[] { Src("t.bin", input) }, s);
                            string combo = "check " + (char)check + ", long " + longPackets + ", 8-bit " + (char)eightBit + ", repeat " + (repeat != 0);
                            Check(o.SendOk && o.ReceiveOk, combo + " succeeded: " + o.SenderProgress.Last() + " / " + o.ReceiverProgress.Last());
                            Check(o.Get("t.bin").SequenceEqual(input), combo + ": content identical");
                        }
                    }
                }
            }
        });

        Run("loopback: with an 8-bit clean line (QBIN 'Y' both ways) no byte is prefixed, only controls are quoted", () =>
        {
            byte[] data = Enumerable.Repeat((byte)0xE9, 300).ToArray();
            Outcome o = Transfer(new[] { Src("hi.bin", data) }, new KermitTransfer.Settings { RepeatPrefix = 0, LongPackets = false });
            Check(o.SendOk && o.ReceiveOk && o.Get("hi.bin").SequenceEqual(data), "transfer ok");
            // 300 bytes, no prefix, no repeat counts: about 300 bytes of data on the wire; a prefix would double that.
            Check(o.Sender.Sent().Length < 450, "no 8-bit prefixes: " + o.Sender.Sent().Length + " bytes sent");
        });

        Run("loopback: repeat counts shrink runs a lot", () =>
        {
            byte[] data = new byte[20000];
            Outcome plain = Transfer(new[] { Src("z", data) }, new KermitTransfer.Settings { RepeatPrefix = 0 });
            Outcome packed = Transfer(new[] { Src("z", data) });
            Check(plain.SendOk && packed.SendOk && packed.Get("z").SequenceEqual(data), "both transfers work");
            Check(packed.Sender.Sent().Length * 10 < plain.Sender.Sent().Length, "compressed " + packed.Sender.Sent().Length + " vs " + plain.Sender.Sent().Length + " bytes");
        });

        Run("loopback: long packets are used when both sides offer them, normal ones otherwise", () =>
        {
            byte[] data = RandomData(20000);
            Outcome longs = Transfer(new[] { Src("l", data) });
            Outcome shorts = Transfer(new[] { Src("l", data) }, null, new KermitTransfer.Settings { LongPackets = false });
            Check(longs.SendOk && shorts.SendOk, "both work");
            Check(longs.Get("l").SequenceEqual(data) && shorts.Get("l").SequenceEqual(data), "both intact");
            // A normal packet is at most 94 + MARK + EOL = 96 bytes; find the longest frame by splitting at MARK.
            int Longest(byte[] sent)
            {
                int best = 0, run = 0;
                foreach (byte b in sent)
                {
                    if (b == KermitTransfer.Mark) run = 0;
                    run++;
                    best = Math.Max(best, run);
                }
                return best;
            }
            Check(Longest(shorts.Sender.Sent()) <= 96, "no frame over 96 bytes without long packets (" + Longest(shorts.Sender.Sent()) + ")");
            Check(Longest(longs.Sender.Sent()) > 1000, "long frames with long packets (" + Longest(longs.Sender.Sent()) + ")");
            Check(longs.Sender.Sent().Length < shorts.Sender.Sent().Length, "fewer bytes on the wire with long packets");
        });

        Run("loopback: block check falls back to 6-bit when the sides do not offer the same one", () =>
        {
            byte[] data = RandomData(3000);
            Outcome o = Transfer(new[] { Src("c", data) }, new KermitTransfer.Settings { BlockCheck = (byte)'3' }, new KermitTransfer.Settings { BlockCheck = (byte)'2' });
            Check(o.SendOk && o.ReceiveOk && o.Get("c").SequenceEqual(data), "transfer ok");
        });

        Run("loopback: several files in a row, an empty one among them", () =>
        {
            byte[] one = RandomData(5000, 1), two = new byte[0], three = RandomData(77, 3);
            Outcome o = Transfer(new[] { Src("one.bin", one), Src("two.bin", two), Src("three.bin", three) });
            Check(o.SendOk && o.ReceiveOk, "succeeded: " + o.SenderProgress.Last() + " / " + o.ReceiverProgress.Last());
            Check(o.Files.Count == 3, "three files");
            Check(o.Get("one.bin").SequenceEqual(one) && o.Get("two.bin").Length == 0 && o.Get("three.bin").SequenceEqual(three), "contents");
            Check(o.SenderProgress.LastDone == 5077 && o.SenderProgress.LastTotal == 5077, "sender progress covers all files");
            Check(o.ReceiverProgress.Last().Contains("3 files"), "receiver reports three files: " + o.ReceiverProgress.Last());
        });

        Run("loopback: the receiver learns the size from the attribute packet", () =>
        {
            Outcome o = Transfer(new[] { Src("s.bin", RandomData(12345)) });
            Check(o.ReceiverProgress.MaxTotal == 12345, "receiver total " + o.ReceiverProgress.MaxTotal);
            Outcome none = Transfer(new[] { Src("s.bin", RandomData(12345)) }, null, new KermitTransfer.Settings { Attributes = false });
            Check(none.SendOk && none.ReceiverProgress.MaxTotal == 0, "no attributes, no total");
        });

        Run("loopback: file names are made safe on the receiving side", () =>
        {
            Outcome o = Transfer(new[] { Src("..\\..\\evil:name?.txt", RandomData(10)), Src("dir/inner.bin", RandomData(10)) });
            Check(o.SendOk && o.ReceiveOk, "succeeded");
            Check(o.Files.Keys.All(k => k.IndexOfAny(new[] { '/', '\\', ':', '?' }) < 0), "no separators or forbidden characters: " + string.Join(",", o.Files.Keys));
            Check(o.Files.ContainsKey("inner.bin"), "directory part dropped");
        });

        Run("loopback: a line that echoes everything back does not confuse either side", () =>
        {
            byte[] data = RandomData(8000);
            Outcome o = Transfer(new[] { Src("e.bin", data) }, null, null, (s, r) => { s.EchoToSelf = true; r.EchoToSelf = true; });
            Check(o.SendOk && o.ReceiveOk && o.Get("e.bin").SequenceEqual(data), "transfer ok: " + o.SenderProgress.Last() + " / " + o.ReceiverProgress.Last());
        });

        Run("loopback: damaged and lost bytes are repaired by retransmission", () =>
        {
            byte[] data = RandomData(30000);
            for (int seed = 1; seed <= 3; seed++)
            {
                int sd = seed;
                KermitTransfer.Settings small = new KermitTransfer.Settings { MaxPacket = 200 };
                Outcome o = Transfer(new[] { Src("n.bin", data) }, small, small, (s, r) =>
                {
                    s.Filter = Noise(sd, 0.001, 0.0005);
                    r.Filter = Noise(sd + 100, 0.001, 0.0005);
                });
                Check(o.SendOk && o.ReceiveOk, "seed " + sd + " succeeded: " + o.SenderProgress.Last() + " / " + o.ReceiverProgress.Last());
                Check(o.Get("n.bin").SequenceEqual(data), "seed " + sd + ": content identical");
                Check(o.SenderProgress.Errors + o.ReceiverProgress.Errors > 0, "seed " + sd + ": some errors were really injected");
            }
        });

        Run("loopback: normal (short) packets also survive noise", () =>
        {
            byte[] data = RandomData(8000);
            KermitTransfer.Settings noLong = new KermitTransfer.Settings { LongPackets = false };
            Outcome o = Transfer(new[] { Src("n.bin", data) }, noLong, noLong, (s, r) =>
            {
                s.Filter = Noise(11, 0.002, 0.001);
                r.Filter = Noise(12, 0.002, 0.001);
            });
            Check(o.SendOk && o.ReceiveOk && o.Get("n.bin").SequenceEqual(data), "transfer ok: " + o.SenderProgress.Last() + " / " + o.ReceiverProgress.Last());
        });

        Run("loopback: the sender can cancel; the receiver is told and drops the partial file", () =>
        {
            using CancellationTokenSource cts = new CancellationTokenSource();
            byte[] data = RandomData(200000);
            KermitTransfer.Settings small = new KermitTransfer.Settings { MaxPacket = 100 };
            int sent = 0;
            // Cancel once some data is on its way, so the transfer is really in the middle of the file.
            Outcome o = Transfer(new[] { Src("big.bin", data) }, small, small, (s, r) =>
            {
                s.Filter = chunk =>
                {
                    if (Interlocked.Add(ref sent, chunk.Length) > 30000) cts.Cancel();
                    return chunk;
                };
            }, cts.Token);
            Check(!o.SendOk && !o.ReceiveOk, "both failed");
            Check(o.SenderProgress.Last().Contains("cancel"), "sender says cancelled: " + o.SenderProgress.Last());
            Check(o.ReceiverProgress.Last().Contains("stopped"), "receiver says the sender stopped it: " + o.ReceiverProgress.Last());
            Check(o.Discarded.Contains("big.bin"), "partial file discarded");
            Check(o.Get("big.bin").Length < data.Length, "the file was not complete");
        });

        Run("loopback: the receiver can cancel; the sender reports the receiver's message", () =>
        {
            using CancellationTokenSource cts = new CancellationTokenSource();
            byte[] data = RandomData(200000);
            KermitTransfer.Settings small = new KermitTransfer.Settings { MaxPacket = 100 };
            int sent = 0;
            Outcome o = Transfer(new[] { Src("big.bin", data) }, small, small, (s, r) =>
            {
                s.Filter = chunk =>
                {
                    if (Interlocked.Add(ref sent, chunk.Length) > 30000) cts.Cancel();
                    return chunk;
                };
            }, default, cts.Token);
            Check(!o.SendOk && !o.ReceiveOk, "both failed");
            Check(o.SenderProgress.Last().Contains("Cancelled by the receiver"), "sender shows the receiver's text: " + o.SenderProgress.Last());
            Check(o.Discarded.Contains("big.bin"), "partial file discarded");
        });

        Run("cancel before the other side answered: the waiting side is told at once", () =>
        {
            using CancellationTokenSource cts = new CancellationTokenSource();
            NewLink(out End s, out End r);
            cts.Cancel();
            Progress p = new Progress();
            bool ok = KermitTransfer.SendFiles(new[] { Src("x", RandomData(10)) }, s.Send, s.Read, cts.Token, p.Handler, new KermitTransfer.Settings(), Fast());
            Check(!ok && p.Last().Contains("cancelled"), "sender: " + p.Last());
            Progress q = new Progress();
            ok = KermitTransfer.ReceiveFiles(n => new MemoryStream(), null, r.Send, r.Read, CancellationToken.None, q.Handler, new KermitTransfer.Settings(), Fast());
            Check(!ok && q.Last().Contains("Cancelled by the sender"), "receiver shows the sender's text: " + q.Last());
        });

        Run("loopback: a file that cannot be written ends the transfer on both sides with the reason", () =>
        {
            NewLink(out End s, out End r);
            KermitTransfer.Timeouts t = Fast();
            Task<bool> rx = Task.Run(() => KermitTransfer.ReceiveFiles(n => throw new IOException("disk full"), null, r.Send, r.Read,
                CancellationToken.None, null, new KermitTransfer.Settings(), t));
            Progress p = new Progress();
            bool ok = KermitTransfer.SendFiles(new[] { Src("x", RandomData(100)) }, s.Send, s.Read, CancellationToken.None, p.Handler, new KermitTransfer.Settings(), t);
            Check(!ok && !rx.Result, "both failed");
            Check(p.Last().Contains("Cannot create the file"), "sender sees the receiver's error: " + p.Last());
        });

        Run("loopback: a file that cannot be read ends the transfer", () =>
        {
            KermitTransfer.Source broken = new KermitTransfer.Source("broken.bin", 10, () => throw new IOException("locked"));
            Outcome o = Transfer(new[] { broken });
            Check(!o.SendOk && !o.ReceiveOk, "both failed");
            Check(o.SenderProgress.Last().Contains("locked"), "reason shown: " + o.SenderProgress.Last());
        });

        Run("loopback: the receiver acknowledges a repeated packet again but stores its data once", () =>
        {
            // A line that delivers every byte twice: every packet arrives twice.
            byte[] data = RandomData(3000);
            Outcome o = Transfer(new[] { Src("dup.bin", data) }, null, null, (s, r) =>
            {
                s.Filter = chunk => chunk.Concat(chunk).ToArray();
            });
            Check(o.SendOk && o.ReceiveOk, "succeeded: " + o.SenderProgress.Last() + " / " + o.ReceiverProgress.Last());
            Check(o.Get("dup.bin").SequenceEqual(data), "no duplicated data");
        });

        // =============== failures ===============

        Run("sender: silence from the receiver ends with a handshake timeout", () =>
        {
            NewLink(out End s, out End peer);
            KermitTransfer.Timeouts t = Fast();
            t.SenderInitTotalMs = 800;
            Progress p = new Progress();
            bool ok = KermitTransfer.SendFiles(new[] { Src("x", RandomData(10)) }, s.Send, s.Read, CancellationToken.None, p.Handler, new KermitTransfer.Settings(), t);
            Check(!ok, "send failed");
            Check(p.Last().Contains("Handshake timeout"), "message: " + p.Last());
            Check(s.Sent().Length >= 2 * 20, "the start packet was repeated: " + s.Sent().Length + " bytes");
        });

        Run("receiver: a sender that never starts ends with a message", () =>
        {
            NewLink(out End r, out End peer);
            KermitTransfer.Timeouts t = Fast();
            t.ReceiverInitTotalMs = 800;
            Progress p = new Progress();
            bool ok = KermitTransfer.ReceiveFiles(n => new MemoryStream(), null, r.Send, r.Read, CancellationToken.None, p.Handler, new KermitTransfer.Settings(), t);
            Check(!ok, "receive failed");
            Check(p.Last().Contains("did not start"), "message: " + p.Last());
        });

        Run("sender: an error packet from the receiver stops the transfer with its text", () =>
        {
            NewLink(out End s, out End peer);
            Task.Run(() =>
            {
                // ^A LEN SEQ 'E' text CHECK: count = 2 + 6 + 1 = 9
                byte[] body = Encoding.ASCII.GetBytes(" " + " " + "E" + "No room");
                body[0] = (byte)(32 + 2 + 7 + 1);
                body[1] = 32;
                int sum = body.Sum(b => b);
                byte check = (byte)(32 + ((sum + ((sum & 192) >> 6)) & 63));
                Thread.Sleep(100);
                peer.Send(new byte[] { 1 }.Concat(body).Concat(new[] { check, (byte)13 }).ToArray());
            });
            Progress p = new Progress();
            bool ok = KermitTransfer.SendFiles(new[] { Src("x", RandomData(10)) }, s.Send, s.Read, CancellationToken.None, p.Handler, new KermitTransfer.Settings(), Fast());
            Check(!ok, "send failed");
            Check(p.Last().Contains("No room"), "the peer's text is shown: " + p.Last());
        });

        Run("sender: noise and echoed text before the receiver answers do not spoil the handshake", () =>
        {
            NewLink(out End s, out End r);
            Outcome o = new Outcome { Sender = s, Receiver = r };
            // Garbage first (including a stray ^A), then a real receiver.
            byte[] junk = Encoding.ASCII.GetBytes("login: root\r\n\u0001garbage\r\n# kermit -r\r\n");
            r.Out.Write(junk, 0, junk.Length);
            Task<bool> rx = Task.Run(() => KermitTransfer.ReceiveFiles(n => { MemoryStream ms = new MemoryStream(); o.Files[n] = ms; return ms; },
                null, r.Send, r.Read, CancellationToken.None, null, new KermitTransfer.Settings(), Fast()));
            byte[] data = RandomData(500);
            bool ok = KermitTransfer.SendFiles(new[] { Src("g.bin", data) }, s.Send, s.Read, CancellationToken.None, null, new KermitTransfer.Settings(), Fast());
            Check(ok && rx.Result && o.Files["g.bin"].ToArray().SequenceEqual(data), "transfer ok");
        });

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed");
        if (Failures.Count > 0)
        {
            Console.WriteLine(Failures.Count + " FAILED:");
            foreach (string f in Failures) Console.WriteLine("  " + f);
            return 1;
        }
        Console.WriteLine("All Kermit protocol checks passed.");
        return 0;
    }
}
