using SmarTTY.IO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Regression harness for the XMODEM protocol (IO/XModemTransfer.cs): the code under test talks to an in-memory
// peer that is either the other half of the protocol or a script that injects the faults seen on real serial lines
// (banners and echo, repeated 'C', damaged blocks, lost ACKs, a dead or cancelling peer).
// Run with: dotnet run --project tests/XModemTransferTests
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

    /// <summary>One end of the link: what it sends arrives at the other end; everything it sends is also recorded.</summary>
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
        public string Kind; // block, eot, can, timeout
        public byte Number, Complement;
        public byte[] Data;
        public bool CheckOk;
    }

    private static Packet ReadPacket(End e, bool crc, int firstTimeoutMs)
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
        Packet p = new Packet { Kind = "block", Data = new byte[size] };
        p.Number = e.Read(1000) ?? 0;
        p.Complement = e.Read(1000) ?? 0;
        for (int i = 0; i < size; i++) p.Data[i] = e.Read(1000) ?? 0;
        if (crc)
        {
            int hi = e.Read(1000) ?? 0, lo = e.Read(1000) ?? 0;
            p.CheckOk = (ushort)((hi << 8) | lo) == XModemTransfer.CalculateCrc16(p.Data, 0, size);
        }
        else
        {
            p.CheckOk = (e.Read(1000) ?? 0) == XModemTransfer.CalculateChecksum(p.Data, 0, size);
        }
        p.CheckOk &= (byte)(p.Number + p.Complement) == 255;
        return p;
    }

    private static byte[] BuildBlock(byte number, byte[] payload, bool crc)
    {
        List<byte> l = new List<byte> { SOH, number, (byte)(255 - number) };
        l.AddRange(payload);
        if (crc)
        {
            ushort c = XModemTransfer.CalculateCrc16(payload, 0, payload.Length);
            l.Add((byte)(c >> 8));
            l.Add((byte)c);
        }
        else
        {
            l.Add(XModemTransfer.CalculateChecksum(payload, 0, payload.Length));
        }
        return l.ToArray();
    }

    private static byte[] Chunk(byte[] data, int index, int size = 128)
    {
        byte[] p = Enumerable.Repeat(SUB, size).ToArray();
        Array.Copy(data, index * size, p, 0, Math.Min(size, data.Length - index * size));
        return p;
    }

    private static byte[] Padded(byte[] data)
    {
        int len = (data.Length + 127) / 128 * 128;
        return Chunk(data, 0, Math.Max(len, 0)).Take(len).ToArray();
    }

    /// <summary>The block sizes an XMODEM-1K sender must use: 1K blocks, and a tail of up to 128 bytes as one 128-byte block.</summary>
    private static List<int> OneKBlockSizes(int length)
    {
        List<int> sizes = new List<int>();
        int left = length;
        while (left > 0)
        {
            int size = left > 128 ? 1024 : 128;
            sizes.Add(size);
            left -= size;
        }
        return sizes;
    }

    /// <summary>The data as an XMODEM-1K receiver must get it: every block padded with 0x1A up to its own size.</summary>
    private static byte[] PaddedOneK(byte[] data)
    {
        List<byte> result = new List<byte>();
        int offset = 0;
        foreach (int size in OneKBlockSizes(data.Length))
        {
            byte[] block = Enumerable.Repeat(SUB, size).ToArray();
            Array.Copy(data, offset, block, 0, Math.Min(size, data.Length - offset));
            result.AddRange(block);
            offset += size;
        }
        return result.ToArray();
    }

    private static byte[] RandomData(int n, int seed = 11)
    {
        byte[] d = new byte[n];
        new Random(seed).NextBytes(d);
        return d;
    }

    // ---------- scripted receiver (peer of a sender under test) ----------

    private sealed class RxScript
    {
        public byte[] Initial = { C };
        public bool Crc = true;
        public int NakFirstArrivalOfBlock;     // 1-based sequence number; answered with NAK the first time it arrives
        public int CanAfterPackets;            // answer this packet with CAN CAN and stop
        public int SingleCanBeforeAckAt;       // send one CAN before the ACK of this packet
        public int DeadAfterPackets;           // stop answering after this many packets
        public int EotNaks;                    // NAKs to answer EOT with before ACK
        public int TimeoutMs = 3000;
        public readonly List<byte> Output = new List<byte>();
        public readonly List<byte> WireNumbers = new List<byte>();
        public readonly List<int> BlockSizes = new List<int>(); // payload size of every packet that arrived (128 or 1024)
        public int PacketsSeen;
        public int EotSeen;
        public bool BadPacket;
    }

    private static Task Receiver(End e, RxScript s)
    {
        return Task.Run(() =>
        {
            e.Send(s.Initial);
            bool nakedOnce = false;
            while (true)
            {
                Packet p = ReadPacket(e, s.Crc, s.TimeoutMs);
                if (p.Kind == "timeout" || p.Kind == "can") return;
                if (p.Kind == "eot")
                {
                    s.EotSeen++;
                    if (s.EotSeen <= s.EotNaks) { e.Send(NAK); continue; }
                    e.Send(ACK);
                    return;
                }
                if (p.Kind != "block") { s.BadPacket = true; return; }
                s.PacketsSeen++;
                s.WireNumbers.Add(p.Number);
                s.BlockSizes.Add(p.Data.Length);
                if (!p.CheckOk) { s.BadPacket = true; e.Send(NAK); continue; }
                if (s.DeadAfterPackets > 0 && s.PacketsSeen >= s.DeadAfterPackets)
                {
                    // never answers; keep draining so the sender's retransmissions are counted
                    continue;
                }
                if (s.CanAfterPackets > 0 && s.PacketsSeen == s.CanAfterPackets) { e.Send(CAN, CAN); return; }
                if (s.NakFirstArrivalOfBlock > 0 && s.PacketsSeen == s.NakFirstArrivalOfBlock && !nakedOnce) { nakedOnce = true; e.Send(NAK); continue; }
                if (s.SingleCanBeforeAckAt > 0 && s.PacketsSeen == s.SingleCanBeforeAckAt) e.Send(CAN);
                s.Output.AddRange(p.Data);
                e.Send(ACK);
            }
        });
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
            PurgeQuietMs = 60
        };
    }

    private sealed class Progress
    {
        public readonly List<string> Status = new List<string>();
        public readonly List<long[]> Samples = new List<long[]>(); // done, total, errors
        public int LastErrors;
        public XModemTransfer.ProgressHandler Handler => (done, total, errors, status) =>
        {
            lock (Status) { Status.Add(status); Samples.Add(new[] { done, total, errors }); LastErrors = errors; }
        };
        public string Last => Status.Count == 0 ? "" : Status[Status.Count - 1];
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

    private static bool Send(byte[] data, End u, CancellationToken token, XModemTransfer.Timeouts t, Progress p, Stream src = null)
    {
        return XModemTransfer.SendFile(src ?? new MemoryStream(data), u.Send, u.Read, token, p?.Handler, t);
    }

    private static bool SendOneK(byte[] data, End u, XModemTransfer.Timeouts t, Progress p, Stream src = null)
    {
        return XModemTransfer.SendFile(src ?? new MemoryStream(data), u.Send, u.Read, CancellationToken.None, p?.Handler, true, t);
    }

    private static int Main()
    {
        // =============== sender under test ===============

        foreach (int size in new[] { 1, 127, 128, 129, 300, 128 * 255, 128 * 258 + 5 })
        {
            int n = size;
            Run("sender: data of " + n + " bytes arrives intact, padded with 0x1A", () =>
            {
                NewLink(out End u, out End peer);
                byte[] data = RandomData(n);
                RxScript script = new RxScript();
                Task rx = Receiver(peer, script);
                Progress prog = new Progress();
                Check(Send(data, u, CancellationToken.None, Fast(), prog), "send succeeded");
                rx.Wait(5000);
                Check(script.Output.SequenceEqual(Padded(data)), "payload identical");
                Check(script.PacketsSeen == (n + 127) / 128, "every block sent exactly once (" + script.PacketsSeen + ")");
                Check(!script.BadPacket, "all checks valid");
                Check(prog.Samples.Last()[0] == n, "progress ends at the real file size");
                Check(prog.Status.All(s => !s.Contains("-")), "no negative block numbers in the status text");
            });
        }

        Run("sender: wire block numbers wrap 255 -> 0 -> 1 and the complement byte stays valid", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript();
            Task rx = Receiver(peer, script);
            Check(Send(RandomData(128 * 258), u, CancellationToken.None, Fast(), null), "send succeeded");
            rx.Wait(5000);
            Check(script.WireNumbers.Count == 258, "258 blocks");
            Check(script.WireNumbers[254] == 255 && script.WireNumbers[255] == 0 && script.WireNumbers[256] == 1, "255, 0, 1 around the wrap");
        });

        Run("sender: checksum mode (receiver asks with NAK)", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(400);
            RxScript script = new RxScript { Initial = new[] { NAK }, Crc = false };
            Task rx = Receiver(peer, script);
            Check(Send(data, u, CancellationToken.None, Fast(), null), "send succeeded");
            rx.Wait(5000);
            Check(script.Output.SequenceEqual(Padded(data)) && !script.BadPacket, "data intact with checksum trailer");
        });

        Run("sender: >60 bytes of noise before the receiver's 'C' do not spoil the handshake", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(200);
            byte[] initial = Encoding.ASCII.GetBytes(new string('x', 200) + "\r\n").Concat(new[] { C }).ToArray();
            RxScript script = new RxScript { Initial = initial };
            Task rx = Receiver(peer, script);
            Check(Send(data, u, CancellationToken.None, Fast(), null), "send succeeded");
            rx.Wait(5000);
            Check(script.Output.SequenceEqual(Padded(data)), "data intact");
        });

        Run("sender: a repeated 'C' is not taken for an answer, block 1 is sent once", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript { Initial = new[] { C, C, C } };
            Task rx = Receiver(peer, script);
            Progress prog = new Progress();
            Check(Send(RandomData(300), u, CancellationToken.None, Fast(), prog), "send succeeded");
            rx.Wait(5000);
            Check(script.PacketsSeen == 3, "3 blocks, no duplicate (" + script.PacketsSeen + ")");
            Check(prog.LastErrors == 0, "no errors counted");
        });

        Run("sender: NAK makes it repeat the same block, then continues", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(300);
            RxScript script = new RxScript { NakFirstArrivalOfBlock = 2 };
            Task rx = Receiver(peer, script);
            Progress prog = new Progress();
            Check(Send(data, u, CancellationToken.None, Fast(), prog), "send succeeded");
            rx.Wait(5000);
            Check(script.PacketsSeen == 4, "one block repeated (" + script.PacketsSeen + " packets)");
            Check(script.Output.SequenceEqual(Padded(data)), "data intact");
            Check(prog.LastErrors == 1, "one error counted");
        });

        Run("sender: EOT is repeated until it is acknowledged", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript { EotNaks = 2 };
            Task rx = Receiver(peer, script);
            Check(Send(RandomData(10), u, CancellationToken.None, Fast(), null), "send succeeded after two NAKs for EOT");
            rx.Wait(5000);
            Check(script.EotSeen == 3, "EOT sent 3 times (" + script.EotSeen + ")");
        });

        Run("sender: CAN CAN from the receiver aborts at once", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript { CanAfterPackets = 1 };
            Task rx = Receiver(peer, script);
            Progress prog = new Progress();
            bool ok = Send(RandomData(500), u, CancellationToken.None, Fast(), prog);
            rx.Wait(5000);
            Check(!ok, "send reports failure");
            Check(script.PacketsSeen == 1, "block 1 is not retransmitted after the cancel (" + script.PacketsSeen + ")");
            Check(prog.Last.Contains("Cancelled by the receiver"), "status says who cancelled: " + prog.Last);
        });

        Run("sender: a single CAN is not an abort", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(300);
            RxScript script = new RxScript { SingleCanBeforeAckAt = 2 };
            Task rx = Receiver(peer, script);
            Check(Send(data, u, CancellationToken.None, Fast(), null), "send succeeded");
            rx.Wait(5000);
            Check(script.Output.SequenceEqual(Padded(data)), "data intact");
        });

        Run("sender: empty file is just EOT", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript();
            Task rx = Receiver(peer, script);
            Check(Send(new byte[0], u, CancellationToken.None, Fast(), null), "send succeeded");
            rx.Wait(5000);
            Check(script.PacketsSeen == 0 && script.EotSeen == 1, "no data block, one EOT");
        });

        Run("sender: short reads from the source stream do not put padding inside the data", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(300);
            RxScript script = new RxScript();
            Task rx = Receiver(peer, script);
            Check(Send(data, u, CancellationToken.None, Fast(), null, new ShortReadStream(data, 10)), "send succeeded");
            rx.Wait(5000);
            Check(script.Output.SequenceEqual(Padded(data)), "payload intact");
        });

        Run("sender: a file that ends earlier than its Length ends the transfer instead of looping", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript();
            Task rx = Receiver(peer, script);
            Check(Send(null, u, CancellationToken.None, Fast(), null, new LyingLengthStream(300, 1000000)), "send succeeded");
            rx.Wait(5000);
            Check(script.PacketsSeen == 3, "3 blocks (" + script.PacketsSeen + ")");
        });

        Run("sender: gives up after 10 unanswered attempts and tells the receiver to stop", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript { DeadAfterPackets = 1, TimeoutMs = 1500 };
            Task rx = Receiver(peer, script);
            Progress prog = new Progress();
            Check(!Send(RandomData(300), u, CancellationToken.None, Fast(40), prog), "send reports failure");
            rx.Wait(5000);
            Check(script.PacketsSeen == 10, "block 1 sent exactly 10 times (" + script.PacketsSeen + ")");
            byte[] sent = u.Sent();
            Check(sent.Length >= 2 && sent[sent.Length - 1] == CAN && sent[sent.Length - 2] == CAN, "CAN CAN sent at the end");
            Check(prog.Last.Contains("block 1"), "status names the block: " + prog.Last);
        });

        Run("sender: no handshake at all times out", () =>
        {
            NewLink(out End u, out End peer);
            XModemTransfer.Timeouts t = Fast();
            t.SenderHandshakeMs = 400;
            Progress prog = new Progress();
            Check(!Send(RandomData(10), u, CancellationToken.None, t, prog), "send reports failure");
            Check(prog.Last.StartsWith("Handshake timeout"), "status: " + prog.Last);
        });

        Run("sender: cancel button stops it quickly with CAN CAN", () =>
        {
            NewLink(out End u, out End peer);
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                RxScript script = new RxScript { DeadAfterPackets = 1, TimeoutMs = 3000 };
                Task rx = Receiver(peer, script);
                Task<bool> send = Task.Run(() => Send(RandomData(300), u, cts.Token, Fast(5000), null));
                Thread.Sleep(300);
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                cts.Cancel();
                Check(send.Wait(2000), "returns within 2 s of the cancel although the ACK timeout is 5 s");
                Check(!send.Result, "reports failure");
                byte[] sent = u.Sent();
                Check(sent[sent.Length - 1] == CAN && sent[sent.Length - 2] == CAN, "CAN CAN sent");
                rx.Wait(5000);
            }
        });

        Run("sender: cancelled while waiting for the receiver", () =>
        {
            NewLink(out End u, out End peer);
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                Task<bool> send = Task.Run(() => Send(RandomData(10), u, cts.Token, Fast(), null));
                Thread.Sleep(200);
                cts.Cancel();
                Check(send.Wait(2500) && !send.Result, "returns false promptly");
            }
        });

        // =============== sender under test: XMODEM-1K ===============

        foreach (int size in new[] { 1, 127, 128, 129, 1024, 1025, 1152, 1153, 5000 })
        {
            int n = size;
            Run("sender 1K: data of " + n + " bytes arrives intact in the right block sizes", () =>
            {
                NewLink(out End u, out End peer);
                byte[] data = RandomData(n);
                RxScript script = new RxScript();
                Task rx = Receiver(peer, script);
                Progress prog = new Progress();
                Check(SendOneK(data, u, Fast(), prog), "send succeeded");
                rx.Wait(5000);
                Check(script.Output.SequenceEqual(PaddedOneK(data)), "payload identical (each block padded with 0x1A to its own size)");
                Check(script.BlockSizes.SequenceEqual(OneKBlockSizes(n)), "block sizes " + string.Join(",", script.BlockSizes) + " instead of " + string.Join(",", OneKBlockSizes(n)));
                Check(!script.BadPacket, "all checks valid");
                Check(prog.Samples.Last()[0] == n, "progress ends at the real file size, not at the padded size");
                Check(prog.LastErrors == 0, "no errors counted");
            });
        }

        Run("sender 1K: block numbers wrap 255 -> 0 -> 1 with 1K blocks too", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(1024 * 258);
            RxScript script = new RxScript();
            Task rx = Receiver(peer, script);
            Check(SendOneK(data, u, Fast(), null), "send succeeded");
            rx.Wait(5000);
            Check(script.WireNumbers.Count == 258, "258 blocks (" + script.WireNumbers.Count + ")");
            Check(script.WireNumbers[254] == 255 && script.WireNumbers[255] == 0 && script.WireNumbers[256] == 1, "255, 0, 1 around the wrap");
            Check(script.BlockSizes.All(s => s == 1024), "every block is a 1K block");
            Check(script.Output.SequenceEqual(data), "payload identical");
        });

        Run("sender 1K: a receiver that asks with NAK (checksum) gets 128-byte blocks", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(3000);
            RxScript script = new RxScript { Initial = new[] { NAK }, Crc = false };
            Task rx = Receiver(peer, script);
            Progress prog = new Progress();
            Check(SendOneK(data, u, Fast(), prog), "send succeeded");
            rx.Wait(5000);
            Check(script.BlockSizes.All(s => s == 128) && script.BlockSizes.Count == (3000 + 127) / 128, "only 128-byte blocks (" + script.BlockSizes.Count + " blocks)");
            Check(script.Output.SequenceEqual(Padded(data)) && !script.BadPacket, "data intact with checksum trailer");
            Check(prog.Status.Any(s => s.Contains("checksum mode")), "status explains the fallback");
        });

        Run("sender 1K: NAK makes it repeat the same 1K block, then continues", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(3000);
            RxScript script = new RxScript { NakFirstArrivalOfBlock = 2 };
            Task rx = Receiver(peer, script);
            Progress prog = new Progress();
            Check(SendOneK(data, u, Fast(), prog), "send succeeded");
            rx.Wait(5000);
            Check(script.PacketsSeen == 4, "one block repeated (" + script.PacketsSeen + " packets)");
            Check(script.BlockSizes.All(s => s == 1024), "the repeat is again a 1K block");
            Check(script.WireNumbers.SequenceEqual(new byte[] { 1, 2, 2, 3 }), "the repeat has the same block number (" + string.Join(",", script.WireNumbers) + ")");
            Check(script.Output.SequenceEqual(PaddedOneK(data)), "data intact");
            Check(prog.LastErrors == 1, "one error counted");
        });

        Run("sender 1K: gives up after 10 unanswered attempts and tells the receiver to stop", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript { DeadAfterPackets = 1, TimeoutMs = 1500 };
            Task rx = Receiver(peer, script);
            Progress prog = new Progress();
            Check(!SendOneK(RandomData(3000), u, Fast(40), prog), "send reports failure");
            rx.Wait(5000);
            Check(script.PacketsSeen == 10, "block 1 sent exactly 10 times (" + script.PacketsSeen + ")");
            byte[] sent = u.Sent();
            Check(sent.Length >= 2 && sent[sent.Length - 1] == CAN && sent[sent.Length - 2] == CAN, "CAN CAN sent at the end");
            Check(prog.Last.Contains("block 1"), "status names the block: " + prog.Last);
        });

        Run("sender 1K: short reads from the source stream do not put padding inside the data", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(1500);
            RxScript script = new RxScript();
            Task rx = Receiver(peer, script);
            Check(SendOneK(data, u, Fast(), null, new ShortReadStream(data, 10)), "send succeeded");
            rx.Wait(5000);
            Check(script.BlockSizes.SequenceEqual(new[] { 1024, 1024 }), "two 1K blocks (" + string.Join(",", script.BlockSizes) + ")");
            Check(script.Output.SequenceEqual(PaddedOneK(data)), "payload intact");
        });

        Run("sender 1K: empty file is just EOT", () =>
        {
            NewLink(out End u, out End peer);
            RxScript script = new RxScript();
            Task rx = Receiver(peer, script);
            Check(SendOneK(new byte[0], u, Fast(), null), "send succeeded");
            rx.Wait(5000);
            Check(script.PacketsSeen == 0 && script.EotSeen == 1, "no data block, one EOT");
        });

        Run("sender: without the 1K option only 128-byte blocks are sent, whatever the receiver asks for", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(5000);
            RxScript script = new RxScript();
            Task rx = Receiver(peer, script);
            Check(Send(data, u, CancellationToken.None, Fast(), null), "send succeeded");
            rx.Wait(5000);
            Check(script.BlockSizes.Count == (5000 + 127) / 128 && script.BlockSizes.All(s => s == 128), "only 128-byte blocks");
        });

        foreach (int size in new[] { 1, 129, 1025, 5000, 1024 * 258 + 5 })
        {
            int n = size;
            Run("sender 1K -> own receiver, " + n + " bytes", () =>
            {
                NewLink(out End a, out End b);
                byte[] data = RandomData(n, 5);
                MemoryStream target = new MemoryStream();
                Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(target, a.Send, a.Read, CancellationToken.None, null, Fast(2000)));
                bool sent = XModemTransfer.SendFile(new MemoryStream(data), b.Send, b.Read, CancellationToken.None, null, true, Fast(2000));
                Check(rx.Wait(15000) && rx.Result && sent, "both sides succeed");
                Check(target.ToArray().SequenceEqual(PaddedOneK(data)), "file identical (plus SUB padding of the last block)");
                // 3 header bytes + payload + 2 CRC bytes per block, plus one EOT: this length differs from the 128-byte layout
                int expectedWire = OneKBlockSizes(n).Sum(s => s + 5) + 1;
                Check(b.Sent().Length == expectedWire, "1K blocks were really used (" + b.Sent().Length + " bytes on the wire, expected " + expectedWire + ")");
            });
        }

        // =============== receiver under test ===============

        foreach (int size in new[] { 1, 128, 129, 5000, 128 * 258 + 5 })
        {
            int n = size;
            Run("receiver: own sender -> own receiver, " + n + " bytes", () =>
            {
                NewLink(out End a, out End b);
                byte[] data = RandomData(n, 5);
                MemoryStream target = new MemoryStream();
                Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(target, a.Send, a.Read, CancellationToken.None, null, Fast(2000)));
                bool sent = XModemTransfer.SendFile(new MemoryStream(data), b.Send, b.Read, CancellationToken.None, null, Fast(2000));
                Check(rx.Wait(10000) && rx.Result && sent, "both sides succeed");
                Check(target.ToArray().SequenceEqual(Padded(data)), "file identical (plus SUB padding)");
            });
        }

        Run("receiver: text before the first block does not make it fall back to checksum mode", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(256);
            MemoryStream target = new MemoryStream();
            Progress prog = new Progress();
            Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(target, u.Send, u.Read, CancellationToken.None, prog.Handler, Fast()));
            peer.Read(2000); // first 'C'
            peer.Send(Encoding.ASCII.GetBytes("Ready, start sending\r\n"));
            peer.Send(BuildBlock(1, Chunk(data, 0), true));
            Check(peer.Read(2000) == ACK, "first block acknowledged");
            peer.Send(BuildBlock(2, Chunk(data, 1), true));
            Check(peer.Read(2000) == ACK, "second block acknowledged");
            peer.Send(EOT);
            Check(peer.Read(2000) == ACK, "EOT acknowledged");
            Check(rx.Wait(5000) && rx.Result, "receive succeeded");
            Check(target.ToArray().SequenceEqual(data), "data intact");
            Check(prog.LastErrors == 0, "no errors");
        });

        Run("receiver: checksum fallback for a sender that does not know 'C'", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(200);
            MemoryStream target = new MemoryStream();
            Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(target, u.Send, u.Read, CancellationToken.None, null, Fast()));
            byte? b;
            while ((b = peer.Read(3000)) == C) { } // an old sender ignores 'C' and waits for NAK
            Check(b == NAK, "receiver switched to NAK");
            peer.Send(BuildBlock(1, Chunk(data, 0), false));
            Check(peer.Read(2000) == ACK, "checksum block 1 acknowledged");
            peer.Send(BuildBlock(2, Chunk(data, 1), false));
            Check(peer.Read(2000) == ACK, "checksum block 2 acknowledged");
            peer.Send(EOT);
            Check(peer.Read(2000) == ACK && rx.Wait(5000) && rx.Result, "finished");
            Check(target.ToArray().SequenceEqual(Padded(data)), "data intact (two full blocks, the last one padded with SUB)");
        });

        Run("receiver: damaged block is NAKed once the line is quiet, the repeat is accepted", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(256);
            MemoryStream target = new MemoryStream();
            Progress prog = new Progress();
            Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(target, u.Send, u.Read, CancellationToken.None, prog.Handler, Fast()));
            peer.Read(2000);
            byte[] bad = BuildBlock(1, Chunk(data, 0), true);
            bad[50] ^= 0x55;
            peer.Send(bad);
            Check(peer.Read(2000) == NAK, "damaged block answered with NAK");
            peer.Send(BuildBlock(1, Chunk(data, 0), true));
            Check(peer.Read(2000) == ACK, "repeat acknowledged");
            peer.Send(BuildBlock(2, Chunk(data, 1), true));
            Check(peer.Read(2000) == ACK, "block 2 acknowledged");
            peer.Send(EOT);
            peer.Read(2000);
            Check(rx.Wait(5000) && rx.Result, "receive succeeded");
            Check(target.ToArray().SequenceEqual(data), "file has each block exactly once");
            Check(prog.LastErrors == 1, "one error counted");
        });

        Run("receiver: leftovers of a torn block are purged before the NAK", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(128);
            MemoryStream target = new MemoryStream();
            Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(target, u.Send, u.Read, CancellationToken.None, null, Fast()));
            peer.Read(2000);
            byte[] torn = BuildBlock(1, Chunk(data, 0), true);
            peer.Send(torn, 0, 60);                       // the rest never comes ...
            Thread.Sleep(700);                              // ... the receiver gives up on the block and NAKs
            Check(peer.Read(2000) == NAK, "NAK for the torn block");
            peer.Send(new byte[] { SOH, 9, 9, 9 });         // stray header-looking noise after the NAK
            Thread.Sleep(10);
            // Like a real sender: the block is repeated on every NAK. The receiver may swallow the first repeat together
            // with the noise (it waits for a quiet line), but must converge and must never take leftovers for a block.
            int repeats = 0;
            byte? answer;
            do
            {
                peer.Send(BuildBlock(1, Chunk(data, 0), true));
                repeats++;
                answer = peer.Read(3000);
            }
            while (answer == NAK && repeats < 4);
            Check(answer == ACK, "the clean repeat is acknowledged (after " + repeats + " repeat(s))");
            peer.Send(EOT);
            peer.Read(2000);
            Check(rx.Wait(5000) && rx.Result && target.ToArray().SequenceEqual(data), "data intact");
        });

        Run("receiver: repeated block (lost ACK) is acknowledged but written once", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(256);
            MemoryStream target = new MemoryStream();
            Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(target, u.Send, u.Read, CancellationToken.None, null, Fast()));
            peer.Read(2000);
            peer.Send(BuildBlock(1, Chunk(data, 0), true));
            Check(peer.Read(2000) == ACK, "ack 1");
            peer.Send(BuildBlock(1, Chunk(data, 0), true));
            Check(peer.Read(2000) == ACK, "ack for the repeat");
            peer.Send(BuildBlock(2, Chunk(data, 1), true));
            Check(peer.Read(2000) == ACK, "ack 2");
            peer.Send(EOT);
            peer.Read(2000);
            Check(rx.Wait(5000) && rx.Result, "receive succeeded");
            Check(target.ToArray().SequenceEqual(data), "no duplicate data");
        });

        Run("receiver: a lost block (gap) aborts instead of producing a corrupt file", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(128 * 6);
            MemoryStream target = new MemoryStream();
            Progress prog = new Progress();
            Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(target, u.Send, u.Read, CancellationToken.None, prog.Handler, Fast()));
            peer.Read(2000);
            peer.Send(BuildBlock(1, Chunk(data, 0), true));
            peer.Read(2000);
            peer.Send(BuildBlock(5, Chunk(data, 4), true));
            Check(peer.Read(2000) == CAN && peer.Read(2000) == CAN, "CAN CAN");
            Check(rx.Wait(5000) && !rx.Result, "receive reports failure");
            Check(target.Length == 128, "only the good block was written");
            Check(prog.Last.Contains("block 5") && prog.Last.Contains("expected"), "status explains: " + prog.Last);
        });

        Run("receiver: gives up after bounded NAKs when the sender goes silent", () =>
        {
            NewLink(out End u, out End peer);
            MemoryStream target = new MemoryStream();
            Progress prog = new Progress();
            Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(target, u.Send, u.Read, CancellationToken.None, prog.Handler, Fast()));
            peer.Read(2000);
            peer.Send(BuildBlock(1, Chunk(RandomData(128), 0), true));
            peer.Read(2000);
            Check(rx.Wait(15000), "returns on its own");
            Check(!rx.Result, "reports failure");
            int naks = u.Sent().Count(x => x == NAK);
            Check(naks == 10, "10 NAKs then give up (" + naks + ")");
            byte[] sent = u.Sent();
            Check(sent[sent.Length - 1] == CAN && sent[sent.Length - 2] == CAN, "CAN CAN at the end");
        });

        Run("receiver: CAN CAN from the sender ends the transfer", () =>
        {
            NewLink(out End u, out End peer);
            Progress prog = new Progress();
            Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(new MemoryStream(), u.Send, u.Read, CancellationToken.None, prog.Handler, Fast()));
            peer.Read(2000);
            peer.Send(CAN, CAN);
            Check(rx.Wait(3000) && !rx.Result, "returns false");
            Check(prog.Last.Contains("Cancelled by the sender"), "status: " + prog.Last);
        });

        Run("receiver: a single CAN in the noise is ignored", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(128);
            MemoryStream target = new MemoryStream();
            Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(target, u.Send, u.Read, CancellationToken.None, null, Fast()));
            peer.Read(2000);
            peer.Send(CAN);
            peer.Send(BuildBlock(1, Chunk(data, 0), true));
            Check(peer.Read(2000) == ACK, "block acknowledged");
            peer.Send(EOT);
            peer.Read(2000);
            Check(rx.Wait(5000) && rx.Result && target.ToArray().SequenceEqual(data), "received");
        });

        Run("receiver: 1K blocks (STX) from the sender are accepted", () =>
        {
            NewLink(out End u, out End peer);
            byte[] data = RandomData(2048);
            MemoryStream target = new MemoryStream();
            Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(target, u.Send, u.Read, CancellationToken.None, null, Fast()));
            peer.Read(2000);
            for (int i = 0; i < 2; i++)
            {
                byte[] payload = Chunk(data, i, 1024);
                List<byte> block = new List<byte> { STX, (byte)(i + 1), (byte)(254 - i) };
                block.AddRange(payload);
                ushort crc = XModemTransfer.CalculateCrc16(payload, 0, 1024);
                block.Add((byte)(crc >> 8));
                block.Add((byte)crc);
                peer.Send(block.ToArray());
                Check(peer.Read(2000) == ACK, "1K block " + (i + 1) + " acknowledged");
            }
            peer.Send(EOT);
            peer.Read(2000);
            Check(rx.Wait(5000) && rx.Result, "receive succeeded");
            Check(target.ToArray().SequenceEqual(data), "2048 bytes intact");
        });

        Run("receiver: EOT right away is an empty file", () =>
        {
            NewLink(out End u, out End peer);
            MemoryStream target = new MemoryStream();
            Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(target, u.Send, u.Read, CancellationToken.None, null, Fast()));
            peer.Read(2000);
            peer.Send(EOT);
            Check(peer.Read(2000) == ACK && rx.Wait(3000) && rx.Result && target.Length == 0, "ACK and an empty file");
        });

        Run("receiver: progress has no total (size unknown) and grows with the data", () =>
        {
            NewLink(out End a, out End b);
            Progress prog = new Progress();
            Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(new MemoryStream(), a.Send, a.Read, CancellationToken.None, prog.Handler, Fast(2000)));
            XModemTransfer.SendFile(new MemoryStream(RandomData(128 * 3)), b.Send, b.Read, CancellationToken.None, null, Fast(2000));
            Check(rx.Wait(5000) && rx.Result, "receive succeeded");
            lock (prog.Status)
            {
                Check(prog.Samples.All(s => s[1] == 0), "total is 0 (unknown), so the progress bar is not stuck at 100 %");
                Check(prog.Samples.Last()[0] == 384, "received byte count at the end");
            }
        });

        Run("receiver: a failing target stream aborts cleanly with CAN CAN (no exception)", () =>
        {
            NewLink(out End u, out End peer);
            Progress prog = new Progress();
            Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(new FailingStream(), u.Send, u.Read, CancellationToken.None, prog.Handler, Fast()));
            peer.Read(2000);
            peer.Send(BuildBlock(1, Chunk(RandomData(128), 0), true));
            Check(peer.Read(2000) == CAN && peer.Read(2000) == CAN, "CAN CAN");
            Check(rx.Wait(3000) && !rx.Result, "returns false");
            Check(prog.Last.Contains("Cannot write"), "status: " + prog.Last);
        });

        Run("receiver: no sender at all gives up after the handshake attempts", () =>
        {
            NewLink(out End u, out End peer);
            Progress prog = new Progress();
            Check(!XModemTransfer.ReceiveFile(new MemoryStream(), u.Send, u.Read, CancellationToken.None, prog.Handler, Fast()), "reports failure");
            byte[] sent = u.Sent();
            Check(sent.Count(x => x == C) == 10 && sent.Count(x => x == NAK) == 10, "10 x 'C' then 10 x NAK");
            Check(prog.Last.Contains("did not respond"), "status: " + prog.Last);
        });

        Run("receiver: cancel button stops it quickly", () =>
        {
            NewLink(out End u, out End peer);
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                XModemTransfer.Timeouts t = Fast();
                t.ReceiverHandshakeAttemptMs = 5000;
                Task<bool> rx = Task.Run(() => XModemTransfer.ReceiveFile(new MemoryStream(), u.Send, u.Read, cts.Token, null, t));
                Thread.Sleep(300);
                cts.Cancel();
                Check(rx.Wait(2000) && !rx.Result, "returns false within 2 s although a handshake attempt waits 5 s");
            }
        });

        Console.WriteLine();
        if (Failures.Count > 0)
        {
            Console.WriteLine(Failures.Count + " FAILED:");
            foreach (string f in Failures) Console.WriteLine("  " + f);
            return 1;
        }
        Console.WriteLine("All XMODEM checks passed (" + _passed + " assertions).");
        return 0;
    }

    private sealed class ShortReadStream : MemoryStream
    {
        private readonly int _max;
        public ShortReadStream(byte[] data, int max) : base(data) { _max = max; }
        public override int Read(byte[] buffer, int offset, int count) { return base.Read(buffer, offset, Math.Min(count, _max)); }
    }

    private sealed class LyingLengthStream : MemoryStream
    {
        private readonly long _length;
        public LyingLengthStream(int real, long length) : base(new byte[real]) { _length = length; }
        public override long Length { get { return _length; } }
    }

    private sealed class FailingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { throw new IOException("disk full"); }
    }
}
