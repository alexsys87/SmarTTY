using SmarTTY.IO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;

// Command line client of SmarTTY's Kermit code for tests/KermitInterop/run_interop.py, which puts a real Kermit program
// (C-Kermit, G-Kermit) on the other end of a TCP socket:
//   KermitInterop send <host> <port> <file>...       sends the files
//   KermitInterop receive <host> <port> <directory>  receives into the directory
// The exit code is 0 for a completed transfer.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: KermitInterop send|receive host port file...|directory");
            return 2;
        }
        string mode = args[0];
        using TcpClient client = new TcpClient(args[1], int.Parse(args[2]));
        NetworkStream net = client.GetStream();
        BlockingCollection<byte> rx = new BlockingCollection<byte>();
        Thread reader = new Thread(() =>
        {
            byte[] buffer = new byte[4096];
            try
            {
                int n;
                while ((n = net.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < n; i++) rx.Add(buffer[i]);
                }
            }
            catch (Exception)
            {
            }
        }) { IsBackground = true };
        reader.Start();

        // KERMIT_TRACE=<file> records the packets both ways with time stamps (milliseconds), for finding out why a real
        // program's packet was refused or where a transfer waited.
        string tracePath = Environment.GetEnvironmentVariable("KERMIT_TRACE");
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        System.Text.StringBuilder trace = new System.Text.StringBuilder();
        List<byte> incoming = new List<byte>();
        string Show(IEnumerable<byte> bytes) => new string(bytes.Take(24).Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray());
        KermitTransfer.SendDataHandler send = (data, offset, length) =>
        {
            if (tracePath != null) lock (trace) trace.AppendLine(clock.ElapsedMilliseconds + " > " + Show(data.Skip(offset).Take(length)));
            net.Write(data, offset, length);
        };
        Func<int, byte?> read = timeout =>
        {
            if (!rx.TryTake(out byte b, timeout)) return null;
            if (tracePath != null)
            {
                if (b == 1 && incoming.Count > 0)
                {
                    lock (trace) trace.AppendLine(clock.ElapsedMilliseconds + " < " + Show(incoming));
                    incoming.Clear();
                }
                incoming.Add(b);
            }
            return b;
        };
        string last = string.Empty;
        KermitTransfer.ProgressHandler progress = (done, total, errors, status) =>
        {
            if (!string.IsNullOrEmpty(status) && status != last)
            {
                last = status;
                Console.WriteLine("  [" + done + "/" + total + ", errors " + errors + "] " + status);
            }
        };

        bool ok;
        if (mode == "send")
        {
            List<KermitTransfer.Source> files = new List<KermitTransfer.Source>();
            foreach (string path in args.Skip(3))
            {
                string p = path;
                files.Add(new KermitTransfer.Source(Path.GetFileName(p), new FileInfo(p).Length, () => File.OpenRead(p)));
            }
            ok = KermitTransfer.SendFiles(files, send, read, CancellationToken.None, progress);
        }
        else
        {
            string directory = args[3];
            Directory.CreateDirectory(directory);
            ok = KermitTransfer.ReceiveFiles(
                name => File.Create(Path.Combine(directory, name)),
                name => File.Delete(Path.Combine(directory, name)),
                send, read, CancellationToken.None, progress);
        }
        if (tracePath != null) File.WriteAllText(tracePath, trace.ToString());
        Console.WriteLine(ok ? "RESULT: ok" : "RESULT: FAILED");
        return ok ? 0 : 1;
    }
}
