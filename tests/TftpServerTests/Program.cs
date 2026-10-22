using SmarTTY.IO;
using System;
using System.Collections.Generic;
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
        TftpServer server = new TftpServer(IPAddress.Loopback, 0) { DefaultTimeoutMs = 300, MaxRetries = 4 };
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
        using TftpServer any = new TftpServer(IPAddress.Any, 0) { DefaultTimeoutMs = 300 };
        any.AddFile("c.bin", c);
        any.Start();
        DownloadResult rd = Download(new IPEndPoint(IPAddress.Loopback, any.LocalEndPoint.Port), Request(1, "c.bin", "octet"));
        Check("a server bound to all interfaces works", Same(rd.Data, c));
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
