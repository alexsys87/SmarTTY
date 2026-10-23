using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// Regression harness for the TFTP server (IO/TftpServer.cs). A small TFTP client written here from the RFCs (including the
// windowsize rules of RFC 7440) downloads files from the server over the loopback interface and can lose, repeat and
// invent packets. Run with: dotnet run --project tests/TftpServerTests
internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new List<string>();

    private static void Check(string name, bool condition, string detail = null)
    {
        if (condition)
        {
            _passed++;
        }
        else
        {
            Failures.Add(name + (detail == null ? string.Empty : " - " + detail));
            Console.WriteLine("FAIL: " + name + (detail == null ? string.Empty : " - " + detail));
        }
    }

    // ---------- test client ----------

    private sealed class DownloadResult
    {
        public byte[] Data;
        public bool Completed;
        public int ErrorCode = -1;
        public string ErrorMessage;
        public string[] OackOptions = new string[0];
        public Dictionary<long, int> BlockArrivals = new Dictionary<long, int>();
        public bool GotNothing;
    }

    private sealed class ClientBehavior
    {
        public HashSet<long> DropDataOnce = new HashSet<long>();
        public HashSet<long> SkipAckOnce = new HashSet<long>();
        public HashSet<long> DoubleAck = new HashSet<long>();
        public long AbortAfterBlock = -1;
        public long VanishAfterBlock = -1;
        public int ReceiveTimeoutMs = 3000;
    }

    private static byte[] Request(int opcode, string name, string mode, params string[] options)
    {
        using MemoryStream ms = new MemoryStream();
        ms.WriteByte(0);
        ms.WriteByte((byte)opcode);
        foreach (string field in new[] { name, mode }.Concat(options))
        {
            byte[] b = Encoding.ASCII.GetBytes(field);
            ms.Write(b, 0, b.Length);
            ms.WriteByte(0);
        }
        return ms.ToArray();
    }

    private static byte[] Ack(long block)
    {
        return new byte[] { 0, 4, (byte)((block >> 8) & 0xFF), (byte)(block & 0xFF) };
    }

    private static long Resolve(int wire, long reference)
    {
        long n = (reference & ~0xFFFFL) | (uint)wire;
        if (n < reference - 0x8000) n += 0x10000;
        else if (n > reference + 0x8000) n -= 0x10000;
        return n;
    }

    private static DownloadResult Download(IPEndPoint server, byte[] request, ClientBehavior behavior = null)
    {
        behavior = behavior ?? new ClientBehavior();
        DownloadResult result = new DownloadResult();
        using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.SendTo(request, server);

        MemoryStream data = new MemoryStream();
        EndPoint peer = null;
        IPEndPoint serverTid = null;
        int blockSize = 512;
        int window = 1;
        long expected = 1;
        int inWindow = 0;
        HashSet<long> dropped = new HashSet<long>();
        HashSet<long> skipped = new HashSet<long>();
        byte[] buffer = new byte[70000];

        while (true)
        {
            if (!socket.Poll(behavior.ReceiveTimeoutMs * 1000, SelectMode.SelectRead))
            {
                result.GotNothing = serverTid == null;
                return result;
            }
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n = socket.ReceiveFrom(buffer, ref from);
            IPEndPoint source = (IPEndPoint)from;
            if (serverTid == null)
            {
                serverTid = source;
                peer = from;
            }
            else if (!source.Equals(serverTid))
            {
                continue;
            }
            int opcode = (buffer[0] << 8) | buffer[1];

            if (opcode == 5)
            {
                result.ErrorCode = (buffer[2] << 8) | buffer[3];
                result.ErrorMessage = Encoding.ASCII.GetString(buffer, 4, Math.Max(0, n - 5));
                return result;
            }

            if (opcode == 6)
            {
                List<string> fields = new List<string>();
                int pos = 2;
                while (pos < n)
                {
                    int end = Array.IndexOf(buffer, (byte)0, pos, n - pos);
                    fields.Add(Encoding.ASCII.GetString(buffer, pos, end - pos));
                    pos = end + 1;
                }
                result.OackOptions = fields.ToArray();
                for (int i = 0; i + 1 < fields.Count; i += 2)
                {
                    if (fields[i] == "blksize") blockSize = int.Parse(fields[i + 1]);
                    if (fields[i] == "windowsize") window = int.Parse(fields[i + 1]);
                }
                socket.SendTo(Ack(0), peer);
                continue;
            }

            if (opcode != 3)
            {
                continue;
            }

            long block = Resolve((buffer[2] << 8) | buffer[3], expected);
            result.BlockArrivals[block] = result.BlockArrivals.TryGetValue(block, out int seen) ? seen + 1 : 1;

            if (behavior.DropDataOnce.Contains(block) && dropped.Add(block))
            {
                continue; // lost on the way
            }
            if (behavior.VanishAfterBlock >= 0 && block > behavior.VanishAfterBlock)
            {
                return result;
            }
            if (behavior.AbortAfterBlock >= 0 && block > behavior.AbortAfterBlock)
            {
                socket.SendTo(new byte[] { 0, 5, 0, 0, (byte)'n', (byte)'o', 0 }, peer);
                return result;
            }

            int payload = n - 4;
            if (block == expected)
            {
                data.Write(buffer, 4, payload);
                expected++;
                inWindow++;
                bool last = payload < blockSize;
                if (last || inWindow >= window)
                {
                    SendAck(socket, peer, block, behavior, skipped);
                    inWindow = 0;
                }
                if (last)
                {
                    result.Completed = true;
                    result.Data = data.ToArray();
                    return result;
                }
            }
            else
            {
                // Out of order or repeated: say how far the data is complete.
                SendAck(socket, peer, expected - 1, behavior, skipped);
                inWindow = 0;
            }
        }
    }

    /// <summary>
    /// A client that answers like U-Boot's net/tftp.c: an acknowledgement only at the end of a window, and after a gap
    /// exactly one (tftp_last_nack: "let's just send one"); packets that arrive after the gap are ignored in silence.
    /// </summary>
    private static DownloadResult DownloadLikeUBoot(IPEndPoint server, byte[] request, ClientBehavior behavior = null)
    {
        behavior = behavior ?? new ClientBehavior();
        DownloadResult result = new DownloadResult();
        using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.SendTo(request, server);

        MemoryStream data = new MemoryStream();
        EndPoint peer = null;
        int blockSize = 512;
        int window = 1;
        int cur = 0;          // tftp_cur_block
        int nextAck = 1;      // tftp_next_ack
        int lastNack = 0;     // tftp_last_nack
        HashSet<long> dropped = new HashSet<long>();
        byte[] buffer = new byte[70000];

        while (true)
        {
            if (!socket.Poll(behavior.ReceiveTimeoutMs * 1000, SelectMode.SelectRead))
            {
                return result;
            }
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n = socket.ReceiveFrom(buffer, ref from);
            if (peer == null)
            {
                peer = from;
            }
            else if (!from.Equals(peer))
            {
                continue;
            }
            int opcode = (buffer[0] << 8) | buffer[1];
            if (opcode == 5)
            {
                result.ErrorCode = (buffer[2] << 8) | buffer[3];
                return result;
            }
            if (opcode == 6)
            {
                List<string> fields = new List<string>();
                int pos = 2;
                while (pos < n)
                {
                    int end = Array.IndexOf(buffer, (byte)0, pos, n - pos);
                    fields.Add(Encoding.ASCII.GetString(buffer, pos, end - pos));
                    pos = end + 1;
                }
                result.OackOptions = fields.ToArray();
                for (int i = 0; i + 1 < fields.Count; i += 2)
                {
                    if (fields[i] == "blksize") blockSize = int.Parse(fields[i + 1]);
                    if (fields[i] == "windowsize") window = int.Parse(fields[i + 1]);
                }
                nextAck = window;
                socket.SendTo(Ack(0), peer);
                continue;
            }
            if (opcode != 3)
            {
                continue;
            }

            int wire = (buffer[2] << 8) | buffer[3];
            result.BlockArrivals[wire] = result.BlockArrivals.TryGetValue(wire, out int seen) ? seen + 1 : 1;
            if (behavior.DropDataOnce.Contains(wire) && dropped.Add(wire))
            {
                continue; // lost on the way
            }

            if (wire != (ushort)(cur + 1))
            {
                if ((ushort)(cur + 1) - (short)wire > 0)
                {
                    continue; // old or repeated: no answer
                }
                if (lastNack != cur)
                {
                    socket.SendTo(Ack(cur), peer);
                    lastNack = cur;
                    nextAck = (ushort)(cur + window);
                }
                continue;
            }

            cur++;
            data.Write(buffer, 4, n - 4);
            if (n - 4 < blockSize)
            {
                socket.SendTo(Ack(cur), peer);
                result.Completed = true;
                result.Data = data.ToArray();
                return result;
            }
            if (cur == nextAck)
            {
                socket.SendTo(Ack(cur), peer);
                nextAck += window;
            }
        }
    }

    private static void SendAck(Socket socket, EndPoint peer, long block, ClientBehavior behavior, HashSet<long> skipped)
    {
        if (behavior.SkipAckOnce.Contains(block) && skipped.Add(block))
        {
            return; // the acknowledgement is lost
        }
        socket.SendTo(Ack(block), peer);
        if (behavior.DoubleAck.Contains(block))
        {
            socket.SendTo(Ack(block), peer);
        }
    }

    // ---------- fixtures ----------

    private static string _dir;

    // The copies the servers of these tests make go here, so that they can be counted without other programs getting in.
    private static string TempFolder => Path.Combine(_dir, "copies");

    private static string MakeFile(string name, int length, int seed = 1)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static TftpServer StartServer(params string[] files)
    {
        TftpServer server = new TftpServer(IPAddress.Loopback, 0) { DefaultTimeoutMs = 300, MaxRetries = 4, WindowPacingMicroseconds = 0, TempDirectory = TempFolder };
        foreach (string path in files)
        {
            server.AddFile(Path.GetFileName(path), path);
        }
        server.Start();
        return server;
    }

    private static bool Same(byte[] a, string path)
    {
        return a != null && a.SequenceEqual(File.ReadAllBytes(path));
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(10);
        }
        return condition();
    }

    // ---------- tests ----------

    private static void BasicTransfers()
    {
        string f10k = MakeFile("fw.bin", 10000);
        string f1024 = MakeFile("exact.bin", 1024);
        string empty = MakeFile("empty.bin", 0);
        using TftpServer server = StartServer(f10k, f1024, empty);

        DownloadResult plain = Download(server.LocalEndPoint, Request(1, "fw.bin", "octet"));
        Check("plain RRQ completes", plain.Completed);
        Check("plain RRQ data matches", Same(plain.Data, f10k));
        Check("plain RRQ has no OACK", plain.OackOptions.Length == 0);

        DownloadResult exact = Download(server.LocalEndPoint, Request(1, "exact.bin", "octet"));
        Check("a file of whole blocks ends with an empty block", exact.Completed && Same(exact.Data, f1024) && exact.BlockArrivals.ContainsKey(3));

        DownloadResult none = Download(server.LocalEndPoint, Request(1, "empty.bin", "octet"));
        Check("an empty file is one empty block", none.Completed && none.Data.Length == 0);

        DownloadResult upper = Download(server.LocalEndPoint, Request(1, "/FW.BIN", "OCTET"));
        Check("names ignore case, a leading slash and the mode's case", upper.Completed && Same(upper.Data, f10k));

        DownloadResult backslash = Download(server.LocalEndPoint, Request(1, ".\\fw.bin", "octet"));
        Check("a leading .\\ is ignored", backslash.Completed && Same(backslash.Data, f10k));
    }

    private static void OptionTests()
    {
        string f = MakeFile("opt.bin", 50000, 2);
        using TftpServer server = StartServer(f);

        DownloadResult r = Download(server.LocalEndPoint, Request(1, "opt.bin", "octet", "blksize", "1024", "tsize", "0"));
        Check("blksize and tsize are acknowledged", r.OackOptions.SequenceEqual(new[] { "blksize", "1024", "tsize", "50000" }), string.Join(",", r.OackOptions));
        Check("data with blksize 1024 matches", Same(r.Data, f));

        r = Download(server.LocalEndPoint, Request(1, "opt.bin", "OCTET", "BLKSIZE", "1468", "Timeout", "3"));
        Check("option names ignore case", r.OackOptions.SequenceEqual(new[] { "blksize", "1468", "timeout", "3" }), string.Join(",", r.OackOptions));
        Check("data with blksize 1468 matches", Same(r.Data, f));

        server.MaxBlockSize = 512;
        r = Download(server.LocalEndPoint, Request(1, "opt.bin", "octet", "blksize", "1468"));
        Check("a block size over the server's limit is cut down", r.OackOptions.SequenceEqual(new[] { "blksize", "512" }), string.Join(",", r.OackOptions));
        server.MaxBlockSize = 65464;

        r = Download(server.LocalEndPoint, Request(1, "opt.bin", "octet", "blksize", "4", "foo", "bar", "tsize", "7"));
        Check("invalid or unknown options are left out", r.Completed && r.OackOptions.Length == 0 && Same(r.Data, f), string.Join(",", r.OackOptions));

        r = Download(server.LocalEndPoint, Request(1, "opt.bin", "octet", "windowsize", "8", "blksize", "512"));
        Check("windowsize is acknowledged", r.OackOptions.SequenceEqual(new[] { "windowsize", "8", "blksize", "512" }), string.Join(",", r.OackOptions));
        Check("data with a window of 8 matches", Same(r.Data, f));

        server.MaxWindowSize = 4;
        r = Download(server.LocalEndPoint, Request(1, "opt.bin", "octet", "windowsize", "32"));
        Check("a window over the server's limit is cut down", r.OackOptions.SequenceEqual(new[] { "windowsize", "4" }) && Same(r.Data, f), string.Join(",", r.OackOptions));
        server.MaxWindowSize = 64;
    }

    private static void LossTests()
    {
        string f = MakeFile("loss.bin", 200 * 512 + 100, 3);
        using TftpServer server = StartServer(f);

        ClientBehavior skipAck = new ClientBehavior();
        skipAck.SkipAckOnce.Add(3);
        skipAck.SkipAckOnce.Add(7);
        DownloadResult r = Download(server.LocalEndPoint, Request(1, "loss.bin", "octet"), skipAck);
        Check("a lost acknowledgement is recovered by sending the block again", r.Completed && Same(r.Data, f));
        Check("the block after a lost acknowledgement arrives twice", r.BlockArrivals[3] == 2 && r.BlockArrivals[7] == 2, r.BlockArrivals[3] + "," + r.BlockArrivals[7]);

        ClientBehavior drop = new ClientBehavior();
        drop.DropDataOnce.Add(5);
        drop.DropDataOnce.Add(20);
        drop.DropDataOnce.Add(201); // the last block
        r = Download(server.LocalEndPoint, Request(1, "loss.bin", "octet", "windowsize", "8"), drop);
        Check("lost data blocks inside a window are recovered", r.Completed && Same(r.Data, f));

        ClientBehavior dropPlain = new ClientBehavior();
        dropPlain.DropDataOnce.Add(2);
        dropPlain.DropDataOnce.Add(201);
        r = Download(server.LocalEndPoint, Request(1, "loss.bin", "octet"), dropPlain);
        Check("lost data blocks without a window are recovered by the timeout", r.Completed && Same(r.Data, f));

        // A repeated acknowledgement must not make the server send the next block twice.
        ClientBehavior doubled = new ClientBehavior();
        doubled.DoubleAck.Add(2);
        doubled.DoubleAck.Add(2);
        r = Download(server.LocalEndPoint, Request(1, "loss.bin", "octet"), doubled);
        Check("a repeated acknowledgement does not duplicate data", r.Completed && Same(r.Data, f) && r.BlockArrivals[3] == 1, r.BlockArrivals.ContainsKey(3) ? r.BlockArrivals[3].ToString() : "none");
    }

    private static void WrapAroundTests()
    {
        // 8-byte blocks: the block number passes 65535 and wraps to 0.
        string f = MakeFile("wrap.bin", 8 * 65540 + 3, 4);
        using TftpServer server = StartServer(f);

        DownloadResult r = Download(server.LocalEndPoint, Request(1, "wrap.bin", "octet", "blksize", "8", "windowsize", "64"));
        Check("a transfer whose block number wraps around (window)", r.Completed && Same(r.Data, f));

        DateTime start = DateTime.UtcNow;
        r = Download(server.LocalEndPoint, Request(1, "wrap.bin", "octet", "blksize", "8"), new ClientBehavior { ReceiveTimeoutMs = 10000 });
        Check("a transfer whose block number wraps around (no window)", r.Completed && Same(r.Data, f), (DateTime.UtcNow - start).TotalSeconds.ToString("0.0") + " s");

        Check("ResolveBlock stays near the reference",
            Resolve(0, 65535) == 65536 && Resolve(65535, 65536) == 65535 && Resolve(1, 1) == 1 && Resolve(5, 131070) == 131077);
    }

    private static void RefusalTests()
    {
        string f = MakeFile("ok.bin", 1000, 5);
        using TftpServer server = StartServer(f);

        DownloadResult r = Download(server.LocalEndPoint, Request(1, "missing.bin", "octet"));
        Check("an unknown file gets error 1", r.ErrorCode == 1, r.ErrorCode.ToString());

        r = Download(server.LocalEndPoint, Request(1, "../ok.bin", "octet"));
        Check("a path outside the served names gets error 1", r.ErrorCode == 1, r.ErrorCode.ToString());

        r = Download(server.LocalEndPoint, Request(1, Path.Combine(_dir, "ok.bin"), "octet"));
        Check("a real path is not a served name", r.ErrorCode == 1, r.ErrorCode.ToString());

        r = Download(server.LocalEndPoint, Request(2, "ok.bin", "octet"));
        Check("a write request gets error 2", r.ErrorCode == 2, r.ErrorCode.ToString());
        Check("the served file was not touched by the write request", File.ReadAllBytes(f).Length == 1000);

        r = Download(server.LocalEndPoint, Request(1, "ok.bin", "netascii"));
        Check("netascii gets error 4", r.ErrorCode == 4, r.ErrorCode.ToString());

        byte[] unterminated = new byte[] { 0, 1, (byte)'o', (byte)'k', 0, (byte)'o', (byte)'c', (byte)'t' };
        r = Download(server.LocalEndPoint, unterminated);
        Check("a malformed request gets error 4", r.ErrorCode == 4, r.ErrorCode.ToString());

        r = Download(server.LocalEndPoint, new byte[] { 0, 1, 0, 0 });
        Check("an empty file name gets error 4", r.ErrorCode == 4, r.ErrorCode.ToString());

        server.AllowedClient = IPAddress.Parse("10.9.9.9");
        r = Download(server.LocalEndPoint, Request(1, "ok.bin", "octet"), new ClientBehavior { ReceiveTimeoutMs = 600 });
        Check("a client that is not allowed gets no answer", r.GotNothing && !r.Completed);
        server.AllowedClient = IPAddress.Loopback;
        r = Download(server.LocalEndPoint, Request(1, "ok.bin", "octet"));
        Check("the allowed client is served", r.Completed && Same(r.Data, f));
        server.AllowedClient = null;
    }

    private static void EventTests()
    {
        string f = MakeFile("ev.bin", 100000, 6);
        using TftpServer server = StartServer(f);
        List<string> logs = new List<string>();
        List<TftpTransfer> started = new List<TftpTransfer>();
        List<TftpTransfer> ended = new List<TftpTransfer>();
        int progress = 0;
        server.Log += m => { lock (logs) logs.Add(m); };
        server.TransferStarted += t => { lock (started) started.Add(t); };
        server.TransferProgress += t => Interlocked.Increment(ref progress);
        server.TransferEnded += t => { lock (ended) ended.Add(t); };

        DownloadResult r = Download(server.LocalEndPoint, Request(1, "ev.bin", "octet", "blksize", "1024"));
        Check("events: the download works", r.Completed && Same(r.Data, f));
        Check("events: started and ended once", WaitFor(() => { lock (ended) return ended.Count == 1; }) && started.Count == 1);
        TftpTransfer t = ended.Count > 0 ? ended[0] : null;
        Check("events: the transfer succeeded and reports the whole file", t != null && t.Succeeded && t.BytesAcknowledged == 100000 && t.Length == 100000 && t.BlockSize == 1024 && t.FileName == "ev.bin");
        Check("events: progress was reported", progress >= 1);
        Check("events: the log tells about the transfer", logs.Any(m => m.Contains("Sent \"ev.bin\"")));

        // The client gives up with an error message.
        r = Download(server.LocalEndPoint, Request(1, "ev.bin", "octet"), new ClientBehavior { AbortAfterBlock = 3 });
        Check("events: an aborted transfer is reported as failed", WaitFor(() => { lock (ended) return ended.Count == 2; }) && ended[1].State == TftpTransferState.Failed && ended[1].Error.Contains("stopped"), ended.Count > 1 ? ended[1].Error : "no event");

        // The client disappears.
        r = Download(server.LocalEndPoint, Request(1, "ev.bin", "octet"), new ClientBehavior { VanishAfterBlock = 3 });
        Check("events: a vanished client ends the transfer by timeout", WaitFor(() => { lock (ended) return ended.Count == 3; }, 8000) && ended[2].State == TftpTransferState.Failed && ended[2].Error.Contains("Timed out"), ended.Count > 2 ? ended[2].Error : "no event");
    }

    private static void StopTests()
    {
        string f = MakeFile("stop.bin", 100000, 7);
        TftpServer server = StartServer(f);
        TftpTransfer ended = null;
        server.TransferEnded += t => ended = t;

        // The client takes the first block and stays silent.
        using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.SendTo(Request(1, "stop.bin", "octet"), server.LocalEndPoint);
        byte[] buffer = new byte[2048];
        Check("stop: the first block arrives", socket.Poll(2000000, SelectMode.SelectRead));
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        socket.ReceiveFrom(buffer, ref from);
        Check("stop: one transfer is active", server.ActiveTransferCount == 1);

        server.Stop();
        Check("stop: Stop returns after the transfer ended", ended != null && ended.State == TftpTransferState.Cancelled, ended?.State.ToString());
        Check("stop: no transfer is active", server.ActiveTransferCount == 0 && !server.IsRunning);

        // The same server starts again.
        server.Start();
        DownloadResult r = Download(server.LocalEndPoint, Request(1, "stop.bin", "octet"));
        Check("stop: the server works again after a restart", r.Completed && Same(r.Data, f));
        server.Dispose();
    }

    private static void ConcurrencyAndMisc()
    {
        string a = MakeFile("a.bin", 300000, 8);
        string b = MakeFile("b.bin", 250000, 9);
        using TftpServer server = StartServer(a, b);

        DownloadResult ra = null, rb = null;
        Thread ta = new Thread(() => ra = Download(server.LocalEndPoint, Request(1, "a.bin", "octet", "windowsize", "4")));
        Thread tb = new Thread(() => rb = Download(server.LocalEndPoint, Request(1, "b.bin", "octet", "blksize", "1400")));
        ta.Start();
        tb.Start();
        ta.Join();
        tb.Join();
        Check("two clients at once get their own files", Same(ra?.Data, a) && Same(rb?.Data, b));

        // The same request twice in a row (the client did not wait for the answer) starts one transfer only.
        int started = 0;
        server.TransferStarted += t => Interlocked.Increment(ref started);
        using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            byte[] request = Request(1, "a.bin", "octet");
            socket.SendTo(request, server.LocalEndPoint);
            socket.SendTo(request, server.LocalEndPoint);
            Thread.Sleep(300);
            Check("a repeated request does not start a second transfer", started == 1, started.ToString());

            // A stranger's packet to the transfer's port gets error 5 and does not disturb the transfer.
            byte[] buffer = new byte[2048];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            Check("the transfer sends its first block", socket.Poll(2000000, SelectMode.SelectRead));
            socket.ReceiveFrom(buffer, ref from);
            IPEndPoint tid = (IPEndPoint)from;
            using Socket stranger = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            stranger.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            stranger.SendTo(Ack(1), tid);
            bool answered = stranger.Poll(2000000, SelectMode.SelectRead);
            int length = answered ? stranger.Receive(buffer) : 0;
            Check("a stranger gets error 5", answered && buffer[1] == 5 && buffer[3] == 5, answered ? buffer[3].ToString() : "no answer");
            socket.SendTo(Ack(1), tid);
            Check("the transfer goes on after the stranger", socket.Poll(2000000, SelectMode.SelectRead));
        }

        // Replacing the file under a served name serves the new content (a rebuilt firmware).
        string c = MakeFile("c.bin", 3000, 10);
        server.AddFile("a.bin", c);
        DownloadResult rc = Download(server.LocalEndPoint, Request(1, "a.bin", "octet"));
        Check("a name registered again serves the new file", Same(rc.Data, c));
        Check("GetFiles lists the registered names", server.GetFiles().Length == 2 && server.GetFiles().Any(p => p.Key == "a.bin" && p.Value == c));

        // The port cannot be shared.
        TftpServer second = new TftpServer(IPAddress.Loopback, server.LocalEndPoint.Port);
        bool failed = false;
        try
        {
            second.Start();
        }
        catch (SocketException)
        {
            failed = true;
        }
        Check("a second server on the same port does not start", failed && !second.IsRunning);

        // A bound-to-everything server answers from the address the request was sent to.
        using TftpServer any = new TftpServer(IPAddress.Any, 0) { DefaultTimeoutMs = 300, TempDirectory = TempFolder };
        any.AddFile("c.bin", c);
        any.Start();
        DownloadResult rd = Download(new IPEndPoint(IPAddress.Loopback, any.LocalEndPoint.Port), Request(1, "c.bin", "octet"));
        Check("a server bound to all interfaces works", Same(rd.Data, c));
    }

    // ---------- regression tests for the problems found in the review ----------

    private static TftpServer StartServerWith(int timeoutMs, int retries, params string[] files)
    {
        TftpServer server = new TftpServer(IPAddress.Loopback, 0) { DefaultTimeoutMs = timeoutMs, MaxRetries = retries, WindowPacingMicroseconds = 0, TempDirectory = TempFolder };
        foreach (string path in files)
        {
            server.AddFile(Path.GetFileName(path), path);
        }
        server.Start();
        return server;
    }

    /// <summary>Sends a request and returns the socket and the transfer's own endpoint once its first packet has arrived.</summary>
    private static Socket BeginTransfer(TftpServer server, byte[] request, out IPEndPoint tid, out byte[] first)
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.SendTo(request, server.LocalEndPoint);
        byte[] buffer = new byte[70000];
        tid = null;
        first = null;
        if (socket.Poll(3000000, SelectMode.SelectRead))
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n = socket.ReceiveFrom(buffer, ref from);
            tid = (IPEndPoint)from;
            first = new byte[n];
            Array.Copy(buffer, first, n);
        }
        return socket;
    }

    private static int DrainPackets(Socket socket, int opcode)
    {
        byte[] buffer = new byte[70000];
        int count = 0;
        while (socket.Poll(0, SelectMode.SelectRead))
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            socket.ReceiveFrom(buffer, ref from);
            if (((buffer[0] << 8) | buffer[1]) == opcode)
            {
                count++;
            }
        }
        return count;
    }

    private static void WindowGapTests()
    {
        // A client that behaves like U-Boot: one acknowledgement per gap, then silence. The server must start the window
        // again at once instead of waiting for its timeout (2 s here: a recovery that waits shows as seconds).
        string f = MakeFile("gap.bin", 100 * 512 + 100, 11);
        using TftpServer server = StartServerWith(2000, 4, f);

        ClientBehavior middle = new ClientBehavior { ReceiveTimeoutMs = 10000 };
        middle.DropDataOnce.Add(5);
        middle.DropDataOnce.Add(30);
        Stopwatch watch = Stopwatch.StartNew();
        DownloadResult r = DownloadLikeUBoot(server.LocalEndPoint, Request(1, "gap.bin", "octet", "windowsize", "8"), middle);
        long elapsed = watch.ElapsedMilliseconds;
        Check("U-Boot style client: lost blocks inside windows give the right file", r.Completed && Same(r.Data, f));
        Check("U-Boot style client: a lost block is resent without waiting for the timeout", elapsed < 1500, elapsed + " ms");

        ClientBehavior first = new ClientBehavior { ReceiveTimeoutMs = 10000 };
        first.DropDataOnce.Add(9);
        first.DropDataOnce.Add(17);
        watch.Restart();
        r = DownloadLikeUBoot(server.LocalEndPoint, Request(1, "gap.bin", "octet", "windowsize", "8"), first);
        elapsed = watch.ElapsedMilliseconds;
        Check("U-Boot style client: the first block of a window lost", r.Completed && Same(r.Data, f) && elapsed < 1500, elapsed + " ms");

        r = DownloadLikeUBoot(server.LocalEndPoint, Request(1, "gap.bin", "octet", "windowsize", "8"));
        Check("U-Boot style client: a clean transfer", r.Completed && Same(r.Data, f));
    }

    private static void StopOrderTests()
    {
        // TransferEnded takes its time (a slow handler widens the window of a race): Stop must not return before it is out.
        string f = MakeFile("stoporder.bin", 100000, 12);
        int late = 0;
        for (int i = 0; i < 8; i++)
        {
            using TftpServer server = StartServer(f);
            int delivered = 0;
            server.TransferEnded += t =>
            {
                Thread.Sleep(60);
                Volatile.Write(ref delivered, 1);
            };
            using Socket socket = BeginTransfer(server, Request(1, "stoporder.bin", "octet"), out IPEndPoint tid, out byte[] firstPacket);
            server.Stop();
            if (Volatile.Read(ref delivered) == 0)
            {
                late++;
            }
        }
        Check("stop: TransferEnded is out before Stop returns", late == 0, late + " of 8 runs came back too early");
    }

    private static void RestartTests()
    {
        string f = MakeFile("restart.bin", 20000, 13);
        using TftpServer server = StartServer(f);
        List<TftpTransfer> ended = new List<TftpTransfer>();
        server.TransferEnded += t => { lock (ended) ended.Add(t); };

        byte[] request = Request(1, "restart.bin", "octet");
        using Socket socket = BeginTransfer(server, request, out IPEndPoint tid, out byte[] firstPacket);
        Check("restart: the first block arrives", tid != null && firstPacket[3] == 1);
        socket.SendTo(Ack(1), tid);
        byte[] buffer = new byte[2048];
        Check("restart: the transfer is under way (block 2)", socket.Poll(2000000, SelectMode.SelectRead));
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        socket.ReceiveFrom(buffer, ref from);

        // The board starts the download again from the same address and port (a fixed tftpsrcp, a reset board).
        socket.SendTo(request, server.LocalEndPoint);
        IPEndPoint newTid = null;
        int block = -1;
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (newTid == null && DateTime.UtcNow < deadline && socket.Poll(500000, SelectMode.SelectRead))
        {
            from = new IPEndPoint(IPAddress.Any, 0);
            socket.ReceiveFrom(buffer, ref from);
            if (!from.Equals(tid))
            {
                newTid = (IPEndPoint)from;
                block = (buffer[2] << 8) | buffer[3];
            }
        }
        Check("restart: a request after the first acknowledgement starts a new transfer", newTid != null && block == 1, newTid == null ? "no answer" : "block " + block);
        Check("restart: the old transfer is cancelled", WaitFor(() => { lock (ended) return ended.Count >= 1 && ended[0].State == TftpTransferState.Cancelled; }));
    }

    private static void DeadlineTests()
    {
        string f = MakeFile("deadline.bin", 10000, 14);

        // A client that keeps sending acknowledgements that mean nothing must not keep the transfer alive.
        using (TftpServer server = StartServerWith(300, 2, f))
        {
            TftpTransfer ended = null;
            server.TransferEnded += t => ended = t;
            using Socket socket = BeginTransfer(server, Request(1, "deadline.bin", "octet"), out IPEndPoint tid, out byte[] firstPacket);
            Stopwatch watch = Stopwatch.StartNew();
            while (Volatile.Read(ref ended) == null && watch.ElapsedMilliseconds < 4000)
            {
                socket.SendTo(Ack(0), tid);
                Thread.Sleep(50);
                DrainPackets(socket, 3);
            }
            Check("deadline: stale acknowledgements do not extend the wait", Volatile.Read(ref ended) != null && ended.State == TftpTransferState.Failed && watch.ElapsedMilliseconds < 2500, watch.ElapsedMilliseconds + " ms");
        }

        // The same while the options are waiting for their acknowledgement: no resending at every stray packet, and a limit.
        using (TftpServer server = StartServerWith(300, 2, f))
        {
            TftpTransfer ended = null;
            server.TransferEnded += t => ended = t;
            using Socket socket = BeginTransfer(server, Request(1, "deadline.bin", "octet", "blksize", "1024"), out IPEndPoint tid, out byte[] firstPacket);
            int oacks = firstPacket != null && firstPacket[1] == 6 ? 1 : 0;
            Stopwatch watch = Stopwatch.StartNew();
            while (Volatile.Read(ref ended) == null && watch.ElapsedMilliseconds < 4000)
            {
                socket.SendTo(Ack(7), tid);
                Thread.Sleep(50);
                oacks += DrainPackets(socket, 6);
            }
            Check("deadline: a chatty client does not keep the options waiting for ever", Volatile.Read(ref ended) != null && ended.State == TftpTransferState.Failed && watch.ElapsedMilliseconds < 2500, watch.ElapsedMilliseconds + " ms");
            Check("deadline: the options are resent on the timeout only", oacks <= 4, oacks + " copies");
        }
    }

    private static void MinorTests()
    {
        string f = MakeFile("minor.bin", 3000, 15);
        using TftpServer server = StartServer(f);

        foreach (string name in new[] { "/./minor.bin", ".//minor.bin", "//minor.bin", "./././minor.bin", "/.\\minor.bin" })
        {
            DownloadResult r = Download(server.LocalEndPoint, Request(1, name, "octet"));
            Check("the name \"" + name + "\" is served", r.Completed && Same(r.Data, f));
        }

        // An ERROR packet from a stranger is not answered (two peers could keep answering each other); anything else is.
        using (Socket socket = BeginTransfer(server, Request(1, "minor.bin", "octet"), out IPEndPoint tid, out byte[] firstPacket))
        using (Socket stranger = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            stranger.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            stranger.SendTo(new byte[] { 0, 5, 0, 5, (byte)'x', 0 }, tid);
            Check("a stranger's ERROR gets no answer", !stranger.Poll(500000, SelectMode.SelectRead));
            stranger.SendTo(Ack(1), tid);
            Check("a stranger's ACK still gets error 5", stranger.Poll(2000000, SelectMode.SelectRead));
        }

        // The file is gone or cannot be opened: the right error, and the listener is not the one to wait for the disk.
        string gone = MakeFile("gone.bin", 100, 16);
        server.AddFile("gone.bin", gone);
        File.Delete(gone);
        DownloadResult missing = Download(server.LocalEndPoint, Request(1, "gone.bin", "octet"));
        Check("a registered file that was deleted gets error 1", missing.ErrorCode == 1, missing.ErrorCode.ToString());

        string locked = MakeFile("locked.bin", 100, 17);
        server.AddFile("locked.bin", locked);
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            DownloadResult busy = Download(server.LocalEndPoint, Request(1, "locked.bin", "octet"));
            if (!busy.Completed)
            {
                Check("a file that is in use by another program gets an error that makes the client try again (0, not 1 or 2)", busy.ErrorCode == 0, busy.ErrorCode.ToString());
            }
        }
    }

    private static void ChangedFileTests()
    {
        // The file is served from a copy: a firmware rebuilt while it is being sent reaches the board as it was when the
        // board asked, and the program that rebuilds it is not kept out of the file.
                byte[] original = new byte[20 * 512];
        new Random(18).NextBytes(original);
        string f = Path.Combine(_dir, "changed.bin");
        File.WriteAllBytes(f, original);
        using (TftpServer server = StartServer(f))
        {
            List<TftpTransfer> ended = new List<TftpTransfer>();
            server.TransferEnded += t => { lock (ended) ended.Add(t); };

            using Socket socket = BeginTransfer(server, Request(1, "changed.bin", "octet"), out IPEndPoint tid, out byte[] firstPacket);
            Check("changed file: the first block arrives", tid != null && firstPacket[3] == 1);
            bool written = false;
            try
            {
                using FileStream writer = new FileStream(f, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer.Write(new byte[100], 0, 100); // rewritten in place, the same size
                writer.Seek(0, SeekOrigin.End);
                writer.Write(new byte[100], 0, 100); // and grown
                written = true;
            }
            catch (IOException)
            {
            }
            Check("changed file: the program that writes the file is not kept out of it", written);

            List<byte> received = new List<byte>();
            received.AddRange(firstPacket.Skip(4));
            byte[] buffer = new byte[2048];
            long block = firstPacket[3];
            bool last = firstPacket.Length - 4 < 512;
            while (true)
            {
                socket.SendTo(Ack(block), tid);
                if (last || !socket.Poll(2000000, SelectMode.SelectRead))
                {
                    break;
                }
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n = socket.ReceiveFrom(buffer, ref from);
                if (buffer[1] != 3)
                {
                    break;
                }
                block = (buffer[2] << 8) | buffer[3];
                received.AddRange(buffer.Skip(4).Take(n - 4));
                last = n - 4 < 512;
            }

            Check("changed file: the transfer ends", WaitFor(() => { lock (ended) return ended.Count == 1; }));
            Check("changed file: it is a good transfer", ended.Count == 1 && ended[0].Succeeded, ended.Count == 1 ? ended[0].State + " " + ended[0].Error : "no event");
            Check("changed file: the board gets the content the file had when it asked", received.SequenceEqual(original), received.Count + " bytes");
        }
        Check("changed file: no copy is left behind", !Directory.Exists(TempFolder) || Directory.GetFiles(TempFolder).Length == 0);
        Check("changed file: the copy was made in the folder asked for", Directory.Exists(TempFolder));

        // The next request gets the new content.
        using (TftpServer server = StartServer(f))
        {
            DownloadResult r = Download(server.LocalEndPoint, Request(1, "changed.bin", "octet"));
            Check("changed file: the next transfer sends the new content", r.Completed && Same(r.Data, f) && r.Data.Length == original.Length + 100, r.Data == null ? "no data" : r.Data.Length.ToString());
        }
    }

    private static void SetFilesTests()
    {
        string a = MakeFile("set-a.bin", 3000, 19);
        string b = MakeFile("set-b.bin", 3000, 20);
        using TftpServer server = StartServer(a, b);
        KeyValuePair<string, string>[] both =
        {
            new KeyValuePair<string, string>("set-a.bin", a),
            new KeyValuePair<string, string>("set-b.bin", b)
        };

        // The list is replaced over and over while requests come in: none of them may find it empty.
        int stop = 0;
        Thread changer = new Thread(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                server.SetFiles(both);
            }
        });
        changer.Start();
        int notFound = 0;
        int completed = 0;
        for (int i = 0; i < 150; i++)
        {
            DownloadResult r = Download(server.LocalEndPoint, Request(1, i % 2 == 0 ? "set-a.bin" : "set-b.bin", "octet"));
            if (r.ErrorCode == 1)
            {
                notFound++;
            }
            if (r.Completed)
            {
                completed++;
            }
        }
        Volatile.Write(ref stop, 1);
        changer.Join();
        Check("SetFiles: a request during a replacement never finds the list empty", notFound == 0 && completed == 150, notFound + " not found, " + completed + " completed");

        server.SetFiles(new[] { new KeyValuePair<string, string>("./only.bin", a) });
        Check("SetFiles: the list is replaced", server.GetFiles().Length == 1 && server.GetFiles()[0].Key == "./only.bin");
        DownloadResult old = Download(server.LocalEndPoint, Request(1, "set-a.bin", "octet"));
        DownloadResult now = Download(server.LocalEndPoint, Request(1, "only.bin", "octet"));
        Check("SetFiles: the old names are gone, the new one is served", old.ErrorCode == 1 && now.Completed && Same(now.Data, a));
    }

    private static void PacingTests()
    {
        Check("pacing: 80 microseconds between the packets of a window by default", new TftpServer(IPAddress.Loopback, 0).WindowPacingMicroseconds == 80);

        string f = MakeFile("pace.bin", 2 * 8 * 512 + 100, 21); // windows of 8, 8 and 1 blocks
        using TftpServer server = StartServer(f);
        server.WindowPacingMicroseconds = 5000;
        Stopwatch watch = Stopwatch.StartNew();
        DownloadResult r = Download(server.LocalEndPoint, Request(1, "pace.bin", "octet", "windowsize", "8"));
        long paced = watch.ElapsedMilliseconds;
        Check("pacing: the transfer still gives the right file", r.Completed && Same(r.Data, f));
        Check("pacing: the packets of a window leave with a pause between them", paced >= 60, paced + " ms");

        server.WindowPacingMicroseconds = 0;
        watch.Restart();
        r = Download(server.LocalEndPoint, Request(1, "pace.bin", "octet", "windowsize", "8"));
        Check("pacing: switched off, the same transfer is quick", r.Completed && watch.ElapsedMilliseconds < paced, watch.ElapsedMilliseconds + " ms");
    }

    private static int Main()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tftp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        try
        {
            BasicTransfers();
            OptionTests();
            LossTests();
            WrapAroundTests();
            RefusalTests();
            EventTests();
            StopTests();
            ConcurrencyAndMisc();
            WindowGapTests();
            StopOrderTests();
            RestartTests();
            DeadlineTests();
            MinorTests();
            ChangedFileTests();
            SetFilesTests();
            PacingTests();
        }
        finally
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (Exception)
            {
            }
        }

        Console.WriteLine(Failures.Count == 0 ? "All " + _passed + " checks passed." : Failures.Count + " check(s) failed, " + _passed + " passed.");
        return Failures.Count == 0 ? 0 : 1;
    }
}
