using SmarTTY.IO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;

// Command line client of SmarTTY's XMODEM, YMODEM and ZMODEM code for tests/ModemInterop/run_interop.py, which puts a
// real lrzsz program (sx/rx, sb/rb, sz/rz) on the other end of a TCP socket:
//   ModemInterop xsend|x1ksend <host> <port> <file>      XMODEM (XMODEM-1K) send
//   ModemInterop xreceive <host> <port> <file>           XMODEM receive into the file
//   ModemInterop ysend <host> <port> <file>...           YMODEM batch send
//   ModemInterop yreceive|ygreceive <host> <port> <dir>  YMODEM (YMODEM-G) batch receive into the directory
//   ModemInterop zsend <host> <port> <file>...           ZMODEM send (ZMODEM_OPTIONS: "escape", "crc16", "sub=N")
//   ModemInterop zreceive <host> <port> <dir>            ZMODEM receive into the directory
// The exit code is 0 for a completed transfer.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: ModemInterop mode host port file...|directory");
            return 2;
        }
        string mode = args[0];
        using TcpClient client = new TcpClient(args[1], int.Parse(args[2]));
        client.NoDelay = true;
        NetworkStream net = client.GetStream();
        BlockingCollection<byte> rx = new BlockingCollection<byte>();
        Thread reader = new Thread(() =>
        {
            byte[] buffer = new byte[65536];
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

        object sendLock = new object();
        // MODEM_TRACE=<file> records both directions with time stamps, for finding out what a real program did.
        string tracePath = Environment.GetEnvironmentVariable("MODEM_TRACE");
        System.Diagnostics.Stopwatch traceClock = System.Diagnostics.Stopwatch.StartNew();
        System.Text.StringBuilder trace = new System.Text.StringBuilder();
        void Trace(string direction, byte[] data, int offset, int length)
        {
            if (tracePath == null) return;
            lock (trace)
            {
                trace.Append(traceClock.ElapsedMilliseconds.ToString().PadLeft(7)).Append(' ').Append(direction).Append(' ');
                for (int i = 0; i < Math.Min(length, 48); i++) trace.Append(data[offset + i].ToString("x2"));
                if (length > 48) trace.Append("... (" + length + " bytes)");
                trace.AppendLine();
                if (trace.Length > 0)
                {
                    File.AppendAllText(tracePath, trace.ToString());
                    trace.Clear();
                }
            }
        }
        Func<int, byte?> read = timeout =>
        {
            if (!rx.TryTake(out byte b, timeout)) return null;
            Trace("<", new[] { b }, 0, 1);
            return b;
        };
        Action<byte[], int, int> send = (data, offset, length) => { lock (sendLock) { Trace(">", data, offset, length); net.Write(data, offset, length); } };
        string last = string.Empty;
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        int lastErrors = 0;
        Action<long, long, int, string> progress = (done, total, errors, status) =>
        {
            lastErrors = errors;
            if (!string.IsNullOrEmpty(status) && status != last && (Environment.GetEnvironmentVariable("MODEM_ALL") != null || status.IndexOf("ed block", StringComparison.OrdinalIgnoreCase) < 0 && !status.StartsWith("Damaged") && !status.StartsWith("Retransmitting") && !status.StartsWith("Sending ") && !status.StartsWith("Receiving ")))
            {
                last = status;
                Console.WriteLine("  " + (clock.ElapsedMilliseconds / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s [" + done + "/" + total + ", errors " + errors + "] " + status);
            }
        };
        XModemTransfer.SendDataHandler xsend = (d, o, l) => send(d, o, l);
        XModemTransfer.ProgressHandler xprogress = (d, t, e, s) => progress(d, t, e, s);
        ZModemTransfer.SendDataHandler zsend = (d, o, l) => send(d, o, l);
        ZModemTransfer.ProgressHandler zprogress = (d, t, e, s) => progress(d, t, e, s);

        bool ok;
        string[] paths = args.Skip(3).ToArray();
        switch (mode)
        {
            case "xsend":
            case "x1ksend":
                using (FileStream stream = File.OpenRead(paths[0]))
                {
                    ok = XModemTransfer.SendFile(stream, xsend, read, CancellationToken.None, xprogress, mode == "x1ksend");
                }
                break;
            case "xreceive":
                using (FileStream stream = File.Create(paths[0]))
                {
                    ok = XModemTransfer.ReceiveFile(stream, xsend, read, CancellationToken.None, xprogress);
                }
                break;
            case "ysend":
                {
                    List<FileStream> streams = paths.Select(File.OpenRead).ToList();
                    try
                    {
                        List<YModemTransfer.FileToSend> files = paths.Select((p, i) => new YModemTransfer.FileToSend(Path.GetFileName(p), streams[i], File.GetLastWriteTimeUtc(p))).ToList();
                        ok = YModemTransfer.SendFiles(files, xsend, read, CancellationToken.None, xprogress);
                    }
                    finally
                    {
                        streams.ForEach(s => s.Dispose());
                    }
                    break;
                }
            case "yreceive":
            case "ygreceive":
                {
                    string directory = paths[0];
                    Directory.CreateDirectory(directory);
                    ok = YModemTransfer.ReceiveFiles(header => File.Create(Path.Combine(directory, header.Name)), xsend, read,
                        CancellationToken.None, xprogress, null,
                        mode == "ygreceive" ? YModemTransfer.YModemReceiveMode.Streaming : YModemTransfer.YModemReceiveMode.Standard);
                    break;
                }
            case "zsend":
                {
                    ZModemOptions options = Options();
                    List<FileStream> streams = paths.Select(File.OpenRead).ToList();
                    try
                    {
                        List<ZModemSendItem> items = paths.Select((p, i) => new ZModemSendItem(Path.GetFileName(p), streams[i], streams[i].Length, File.GetLastWriteTimeUtc(p))).ToList();
                        ok = ZModemTransfer.SendFiles(items, zsend, read, CancellationToken.None, zprogress, options);
                    }
                    finally
                    {
                        streams.ForEach(s => s.Dispose());
                    }
                    break;
                }
            case "zreceive":
                {
                    string directory = paths[0];
                    Directory.CreateDirectory(directory);
                    ok = ZModemTransfer.ReceiveFiles(info => File.Create(Path.Combine(directory, info.Name)), (info, complete) => { },
                        zsend, read, CancellationToken.None, zprogress, Options());
                    break;
                }
            default:
                Console.Error.WriteLine("unknown mode " + mode);
                return 2;
        }
        if (tracePath != null) File.AppendAllText(tracePath, trace.ToString());
        Console.WriteLine((ok ? "RESULT: ok" : "RESULT: FAILED") + " (errors " + lastErrors + ")");
        return ok ? 0 : 1;
    }

    private static ZModemOptions Options()
    {
        ZModemOptions options = new ZModemOptions();
        foreach (string option in (Environment.GetEnvironmentVariable("ZMODEM_OPTIONS") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (option == "escape") options.EscapeControlCharacters = true;
            else if (option == "crc16") options.UseCrc32 = false;
            else if (option.StartsWith("sub=")) options.SubpacketBytes = int.Parse(option.Substring(4));
        }
        return options;
    }
}
