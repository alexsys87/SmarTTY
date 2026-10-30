using SmarTTY.IO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

// Regression harness for the gzip + base64 transfer (IO/GzipBase64Transfer.cs). The "device" is a real shell on a pseudo
// terminal (socat pty, cooked mode with echo, like a serial console): busybox ash, which is what OpenWrt has, and bash with the
// GNU tools. The code under test types into it and reads from it exactly as it does on a COM port, and the bytes that go in
// and out can be damaged on the way. Needs: socat, busybox (static), bash, gzip, base64, coreutils (and sharutils for the
// GNU uuencode cases, which are skipped when it is missing).
// Run with: dotnet run --project tests/GzipBase64TransferTests
internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new List<string>();

    private sealed class Device : IDisposable
    {
        private readonly Process _process;
        private readonly BlockingCollection<byte> _rx = new BlockingCollection<byte>();
        private readonly Thread _reader;
        private readonly string _shellName;

        /// <summary>Changes what goes to the device (null = unchanged). Called for each block the code under test sends.</summary>
        public Func<byte[], byte[]> TxFilter;

        /// <summary>Changes what comes from the device. Called for each block read.</summary>
        public Func<byte[], byte[]> RxFilter;

        public long SentBytes;

        /// <summary>The speed of the line in bytes per second in both directions (11520 = 115200 baud); 0 = as fast as the pipe goes.</summary>
        public int BytesPerSecond;

        public Device(string shellCommand, string shellName)
        {
            _shellName = shellName;
            ProcessStartInfo info = new ProcessStartInfo("socat", "- \"EXEC:" + shellCommand + ",pty,setsid,ctty,stderr\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            _process = Process.Start(info);
            _process.ErrorDataReceived += (s, e) => { };
            _process.BeginErrorReadLine();
            _reader = new Thread(() =>
            {
                Stream stream = _process.StandardOutput.BaseStream;
                byte[] buffer = new byte[4096];
                int count;
                try
                {
                    while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        byte[] chunk = new byte[count];
                        Array.Copy(buffer, chunk, count);
                        if (Environment.GetEnvironmentVariable("SMTTY_TRACE") == "1")
                        {
                            Console.Error.Write(Encoding.Latin1.GetString(chunk).Replace("\r", "\\r"));
                        }
                        if (BytesPerSecond > 0)
                        {
                            Thread.Sleep(count * 1000 / BytesPerSecond);
                        }
                        Func<byte[], byte[]> filter = RxFilter;
                        if (filter != null)
                        {
                            chunk = filter(chunk);
                        }
                        foreach (byte b in chunk)
                        {
                            _rx.Add(b);
                        }
                    }
                }
                catch (IOException)
                {
                }
            });
            _reader.IsBackground = true;
            _reader.Start();
            Thread.Sleep(600); // the shell's first prompt
            Drain();
        }

        public void Write(byte[] data, int offset, int count)
        {
            byte[] chunk = new byte[count];
            Array.Copy(data, offset, chunk, 0, count);
            Func<byte[], byte[]> filter = TxFilter;
            if (filter != null)
            {
                chunk = filter(chunk);
            }
            Interlocked.Add(ref SentBytes, count);
            Stream input = _process.StandardInput.BaseStream;
            input.Write(chunk, 0, chunk.Length);
            input.Flush();
            if (BytesPerSecond > 0)
            {
                Thread.Sleep(chunk.Length * 1000 / BytesPerSecond);
            }
        }

        public byte? Read(int timeoutMs)
        {
            return _rx.TryTake(out byte b, timeoutMs) ? b : (byte?)null;
        }

        public void Drain()
        {
            while (_rx.TryTake(out _, 100)) { }
        }

        /// <summary>Runs a command in the device's shell and returns what it printed (to check that the shell is alive and what is on disk).</summary>
        public string Run(string command)
        {
            Drain();
            string marker = "END" + Guid.NewGuid().ToString("N").Substring(0, 8);
            byte[] bytes = Encoding.ASCII.GetBytes(command + "; echo " + marker.Substring(0, 3) + "''" + marker.Substring(3) + "\r");
            Write(bytes, 0, bytes.Length);
            StringBuilder text = new StringBuilder();
            int deadline = Environment.TickCount + 15000;
            while (Environment.TickCount < deadline)
            {
                byte? b = Read(100);
                if (b == null) continue;
                text.Append((char)b.Value);
                string all = text.ToString();
                int firstEcho = all.IndexOf(command, StringComparison.Ordinal);
                int end = all.LastIndexOf(marker, StringComparison.Ordinal);
                if (end > 0 && all.IndexOf("\n", all.IndexOf(marker, StringComparison.Ordinal) > -1 ? Math.Max(0, firstEcho) : 0, StringComparison.Ordinal) >= 0 && end > all.IndexOf("echo ", StringComparison.Ordinal) + 5 + marker.Length)
                {
                    break;
                }
            }
            Drain();
            return text.ToString();
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch (Exception)
            {
            }
            _process.Dispose();
        }

        public override string ToString() => _shellName;
    }

    private static string NewWorkDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "smtty-b64-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] RandomBytes(int count, int seed)
    {
        byte[] data = new byte[count];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] TextBytes(int lines)
    {
        StringBuilder text = new StringBuilder();
        for (int i = 0; i < lines; i++)
        {
            text.Append("line ").Append(i).Append(" of a text file that compresses well; the quick brown fox jumps over the lazy dog\n");
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static Device Ash() => new Device("busybox ash -i", "busybox ash");

    private static Device Bash() => new Device("env PS1=\\\"$ \\\" bash --norc --noprofile -i", "bash");

    private sealed class Run
    {
        public bool Result;
        public Exception Error;
        public int Errors;
        public string LastStatus;
        public long ProgressDone, ProgressTotal;
    }

    private static Run RunSend(Device device, string local, string remote, GzipBase64Transfer.Options options, CancellationToken token = default)
    {
        Run run = new Run();
        try
        {
            run.Result = GzipBase64Transfer.SendFile(local, remote, options, device.Write, device.Read, token,
                (done, total, errors, status) => { run.Errors = errors; run.ProgressDone = done; run.ProgressTotal = total; if (status != null) run.LastStatus = status; });
        }
        catch (Exception ex)
        {
            run.Error = ex;
        }
        return run;
    }

    private static Run RunReceive(Device device, string remote, string local, GzipBase64Transfer.Options options, CancellationToken token = default, Run run = null)
    {
        run = run ?? new Run();
        try
        {
            run.Result = GzipBase64Transfer.ReceiveFile(remote, local, options, device.Write, device.Read, token,
                (done, total, errors, status) => { run.Errors = errors; run.ProgressDone = done; run.ProgressTotal = total; if (status != null) run.LastStatus = status; });
        }
        catch (Exception ex)
        {
            run.Error = ex;
        }
        return run;
    }

    private static GzipBase64Transfer.Options Fast(GzipBase64Transfer.EncodingKind encoding = GzipBase64Transfer.EncodingKind.Auto,
        GzipBase64Transfer.ChecksumKind checksum = GzipBase64Transfer.ChecksumKind.Auto, int attempts = 2, bool overwrite = false)
    {
        return new GzipBase64Transfer.Options { Encoding = encoding, Checksum = checksum, Attempts = attempts, Overwrite = overwrite, NoEchoBytesPerSecond = 60000 };
    }

    private static void Check(bool condition, string name, string detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("  ok   " + name);
        }
        else
        {
            Failures.Add(name + (detail != null ? " -- " + detail : string.Empty));
            Console.WriteLine("  FAIL " + name + (detail != null ? " -- " + detail : string.Empty));
        }
    }

    private static string Describe(Run run) => run.Error != null ? run.Error.GetType().Name + ": " + run.Error.Message : "result=" + run.Result + " status=" + run.LastStatus;

    private static void Section(string title) => Console.WriteLine(title);

    // ---------- the cases ----------

    private static void SendCases(Func<Device> make, bool gnuUu, bool cksum)
    {
        string dir = NewWorkDir();
        string remoteDir = Path.Combine(dir, "remote");
        Directory.CreateDirectory(remoteDir);

        string binary = Path.Combine(dir, "binary.bin");
        File.WriteAllBytes(binary, RandomBytes(150_000, 1));
        string text = Path.Combine(dir, "text.txt");
        File.WriteAllBytes(text, TextBytes(4000));
        string empty = Path.Combine(dir, "empty.dat");
        File.WriteAllBytes(empty, new byte[0]);

        using (Device device = make())
        {
            Section(device + ": send");
            foreach (GzipBase64Transfer.ChecksumKind kind in new[] { GzipBase64Transfer.ChecksumKind.Auto, GzipBase64Transfer.ChecksumKind.Sha256, GzipBase64Transfer.ChecksumKind.Md5 }
                .Concat(cksum ? new[] { GzipBase64Transfer.ChecksumKind.Cksum } : new GzipBase64Transfer.ChecksumKind[0]))
            {
                string target = Path.Combine(remoteDir, "bin-" + kind + ".bin");
                Run run = RunSend(device, binary, target, Fast(checksum: kind));
                Check(run.Error == null && run.Result && File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(binary)), "binary file, base64, checksum " + kind, Describe(run));
            }

            {
                string target = Path.Combine(remoteDir, "text.txt");
                Run run = RunSend(device, text, target, Fast());
                Check(run.Error == null && run.Result && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(text)), "text file", Describe(run));
            }
            {
                // The debug log tells what was typed and what the device answered; a small block size works too.
                List<string> log = new List<string>();
                GzipBase64Transfer.Options options = Fast();
                options.Log = log.Add;
                options.WindowBytes = 64;
                string target = Path.Combine(remoteDir, "logged.txt");
                Run run = RunSend(device, text, target, options);
                string all = string.Join("\n", log);
                Check(run.Error == null && run.Result && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(text)), "block size of 64 bytes", Describe(run));
                Check(all.Contains("SEND ") && all.Contains("The device has:") && all.Contains("Line 1:") && all.Contains("All ") && all.Contains(":READY"), "the debug log has the commands, the answers and the windows", all.Length > 600 ? all.Substring(0, 600) : all);
                Check(!all.Contains("ECHO MISMATCH"), "no echo mismatch is reported on a clean line");
                // Every line typed to the shell stays under what a router's line editor keeps (512 characters).
                int longest = log.Where(l => l.Contains("] TX ") && l.Contains(" bytes: ")).Select(l => int.Parse(l.Substring(l.IndexOf("TX ") + 3, l.IndexOf(" bytes") - l.IndexOf("TX ") - 3))).DefaultIfEmpty(0).Max();
                Check(longest > 100 && longest <= 481, "the typed commands are complete lines of at most 480 characters", "longest " + longest);
                Run tooLong = RunSend(device, text, "/tmp/" + new string('x', 600), Fast());
                Check(tooLong.Error is IOException && tooLong.Error.Message.Contains("too long"), "a path that would not fit the line editor is refused with a clear message", Describe(tooLong));
            }
            {
                string cyr = Path.Combine(dir, "файл №1.txt");
                File.WriteAllBytes(cyr, TextBytes(30));
                Run run = RunSend(device, cyr, remoteDir, Fast());
                string target = Path.Combine(remoteDir, "файл №1.txt");
                Check(run.Error == null && run.Result && File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(cyr)), "a file name with national letters", Describe(run));
            }
            {
                string target = Path.Combine(remoteDir, "empty.dat");
                Run run = RunSend(device, empty, target, Fast());
                Check(run.Error == null && run.Result && File.Exists(target) && new FileInfo(target).Length == 0, "empty file", Describe(run));
            }
            if (gnuUu)
            {
                string target = Path.Combine(remoteDir, "uu.bin");
                Run run = RunSend(device, binary, target, Fast(GzipBase64Transfer.EncodingKind.UuEncode));
                Check(run.Error == null && run.Result && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(binary)), "binary file, uuencode", Describe(run));
            }
            {
                // A directory as the destination, and a name that the shell would take apart if it were not quoted.
                string odd = Path.Combine(dir, "it's a $file; name.txt");
                File.WriteAllBytes(odd, TextBytes(50));
                Run run = RunSend(device, odd, remoteDir, Fast());
                string target = Path.Combine(remoteDir, "it's a $file; name.txt");
                Check(run.Error == null && run.Result && File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(odd)), "directory destination, name with quote, $ and ;", Describe(run));
            }
            {
                string target = Path.Combine(remoteDir, "text.txt");
                Run run = RunSend(device, binary, target, Fast());
                Check(run.Error != null && run.Error.Message.Contains("already exists") && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(text)), "existing destination is kept without Overwrite", Describe(run));
                Check(!File.Exists(target + ".smtty~"), "no unfinished file left after \"already exists\"");
                run = RunSend(device, binary, target, Fast(overwrite: true));
                Check(run.Error == null && run.Result && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(binary)), "Overwrite replaces it", Describe(run));
            }
            {
                Run run = RunSend(device, text, Path.Combine(remoteDir, "no-such-dir", "x.txt"), Fast());
                Check(run.Error != null && run.Error.Message.Contains("cannot write"), "unwritable destination is reported", Describe(run));
            }
            Check(device.Run("echo alive").Contains("alive"), "the shell is still alive after the cases above");
        }
        Directory.Delete(dir, true);
    }

    private static void FaultCases()
    {
        string dir = NewWorkDir();
        string binary = Path.Combine(dir, "binary.bin");
        File.WriteAllBytes(binary, RandomBytes(int.Parse(Environment.GetEnvironmentVariable("SMTTY_FAULTSIZE") ?? "60000"), 2));
        string remoteDir = Path.Combine(dir, "remote");
        Directory.CreateDirectory(remoteDir);

        using (Device device = Ash())
        {
            Section("busybox ash: damaged data on the way to the device");
            {
                // One flipped character in the first attempt only: the checksum catches it, the second attempt is clean.
                int hits = 0;
                device.TxFilter = chunk =>
                {
                    if (chunk.Length > 500 && Interlocked.Increment(ref hits) == 3)
                    {
                        chunk[200] = (byte)(chunk[200] == 'A' ? 'B' : 'A');
                    }
                    return chunk;
                };
                string target = Path.Combine(remoteDir, "retry.bin");
                List<string> log = new List<string>();
                GzipBase64Transfer.Options options = Fast();
                options.Log = log.Add;
                Run run = RunSend(device, binary, target, options);
                device.TxFilter = null;
                Check(run.Error == null && run.Result && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(binary)) && run.Errors >= 1, "a damaged line is caught by the echo, erased and typed again", Describe(run) + " errors=" + run.Errors);
                Check(log.Any(l => l.Contains("ECHO MISMATCH") && l.Contains("echoed")), "the log shows where the echo differs from what was typed", string.Join("\n", log.Where(l => l.Contains("ECHO") || l.Contains("refused")).ToArray()));
                Check(!File.Exists(target + ".smtty~"), "no unfinished file left");
            }
            {
                device.TxFilter = chunk =>
                {
                    if (chunk.Length > 500)
                    {
                        chunk[100] = (byte)(chunk[100] == 'A' ? 'B' : 'A');
                    }
                    return chunk;
                };
                string target = Path.Combine(remoteDir, "always-bad.bin");
                Run run = RunSend(device, binary, target, Fast(attempts: 2));
                device.TxFilter = null;
                Check(run.Error is IOException && !File.Exists(target) && !File.Exists(target + ".smtty~"), "damage in every attempt fails and leaves nothing behind", Describe(run));
                Check(run.Error != null && run.Error.Message.Contains("could not be typed"), "a line that cannot be typed intact fails with a clear message", Describe(run));
                Check(device.Run("echo alive2").Contains("alive2"), "the shell is alive after a failed transfer");
            }
            {
                // A character that never arrives: the device waits for the missing byte, the sender fills the gap and the check fails.
                int hits = 0;
                device.TxFilter = chunk =>
                {
                    if (chunk.Length > 500 && Interlocked.Increment(ref hits) == 4)
                    {
                        byte[] shorter = new byte[chunk.Length - 1];
                        Array.Copy(chunk, 0, shorter, 0, 300);
                        Array.Copy(chunk, 301, shorter, 300, chunk.Length - 301);
                        return shorter;
                    }
                    return chunk;
                };
                string target = Path.Combine(remoteDir, "lost.bin");
                Run run = RunSend(device, binary, target, Fast());
                device.TxFilter = null;
                Check(run.Error == null && run.Result && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(binary)), "a lost character is caught by the echo, the line erased and typed again", Describe(run));
                Check(device.Run("echo alive3").Contains("alive3"), "the shell is alive after a lost character");
            }
            {
                // The noise seen on a real line: a burst of bytes that includes Ctrl-C, Backspace, DEL, Ctrl-Z and Ctrl-\ (all of which
                // a terminal acts on). Once is enough to show that the transfer survives it.
                int hits = 0;
                device.TxFilter = chunk =>
                {
                    if (chunk.Length > 500 && Interlocked.Increment(ref hits) == 5)
                    {
                        byte[] noise = { 0xbb, 0x50, 0x56, 0x25, 0xa6, 0x26, 0xd5, 0x03, 0x55, 0x7f, 0x08, 0x1a, 0x1c, 0x94 };
                        Array.Copy(noise, 0, chunk, 300, noise.Length);
                    }
                    return chunk;
                };
                string target = Path.Combine(remoteDir, "noise.bin");
                Run run = RunSend(device, binary, target, Fast());
                device.TxFilter = null;
                Check(run.Error == null && run.Result && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(binary)) && run.Errors >= 1, "a burst of noise with control characters (Ctrl-C, DEL, Backspace...) is survived", Describe(run) + " errors=" + run.Errors);
                Check(device.Run("echo alive8").Contains("alive8"), "the shell is alive and Ctrl-C works again after it (the signals are ignored only while the data is read)");
            }
            {
                // Noise that contains a line end commits a damaged line: the attempt is given up at once and repeated.
                int hits = 0;
                device.TxFilter = chunk =>
                {
                    if (chunk.Length > 500 && Interlocked.Increment(ref hits) == 3)
                    {
                        chunk[300] = (byte)'\n';
                    }
                    return chunk;
                };
                string target = Path.Combine(remoteDir, "committed.bin");
                Run run = RunSend(device, binary, target, Fast());
                device.TxFilter = null;
                Check(run.Error == null && run.Result && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(binary)) && run.Errors >= 1, "a damaged line that got committed makes the attempt start again", Describe(run) + " errors=" + run.Errors);
                Check(!File.Exists(target + ".smtty~"), "no unfinished file left after it");
            }
            {
                device.Run("stty -echo");
                string target = Path.Combine(remoteDir, "noecho.bin");
                Run run = RunSend(device, binary, target, Fast());
                Check(run.Error == null && run.Result && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(binary)), "device without echo: sends at the speed limit", Describe(run));
                device.Run("stty echo");
            }
        }
        Directory.Delete(dir, true);
    }

    private static void CancelCases()
    {
        string dir = NewWorkDir();
        string binary = Path.Combine(dir, "binary.bin");
        File.WriteAllBytes(binary, RandomBytes(400_000, 3));
        string remoteDir = Path.Combine(dir, "remote");
        Directory.CreateDirectory(remoteDir);

        using (Device device = Ash())
        {
            Section("busybox ash: cancel");
            CancellationTokenSource cts = new CancellationTokenSource();
            string target = Path.Combine(remoteDir, "cancelled.bin");
            Run run = new Run();
            Thread worker = new Thread(() =>
            {
                run = RunSend(device, binary, target, Fast(), cts.Token);
            });
            worker.Start();
            int deadline = Environment.TickCount + 20000;
            while (Interlocked.Read(ref device.SentBytes) < 100_000 && Environment.TickCount < deadline)
            {
                Thread.Sleep(20);
            }
            cts.Cancel();
            worker.Join(20000);
            Check(!worker.IsAlive && run.Error == null && run.Result == false, "cancel while sending returns false", Describe(run));
            Thread.Sleep(500);
            device.Drain();
            Check(device.Run("echo alive4; ls " + remoteDir).Contains("alive4"), "the shell answers after the cancel");
            Check(!File.Exists(target) && !File.Exists(target + ".smtty~"), "no file and no unfinished file after the cancel");
        }
        Directory.Delete(dir, true);
    }

    private static void ReceiveCases(Func<Device> make, bool gnuUu, bool cksum)
    {
        string dir = NewWorkDir();
        string remoteDir = Path.Combine(dir, "remote");
        Directory.CreateDirectory(remoteDir);
        string binary = Path.Combine(remoteDir, "binary.bin");
        File.WriteAllBytes(binary, RandomBytes(200_000, 4));
        string text = Path.Combine(remoteDir, "text file's.txt");
        File.WriteAllBytes(text, TextBytes(5000));
        string empty = Path.Combine(remoteDir, "empty.dat");
        File.WriteAllBytes(empty, new byte[0]);
        string localDir = Path.Combine(dir, "local");
        Directory.CreateDirectory(localDir);

        using (Device device = make())
        {
            Section(device + ": receive");
            foreach (GzipBase64Transfer.ChecksumKind kind in new[] { GzipBase64Transfer.ChecksumKind.Auto, GzipBase64Transfer.ChecksumKind.Sha256, GzipBase64Transfer.ChecksumKind.Md5 }
                .Concat(cksum ? new[] { GzipBase64Transfer.ChecksumKind.Cksum } : new GzipBase64Transfer.ChecksumKind[0]))
            {
                string local = Path.Combine(localDir, "b-" + kind + ".bin");
                Run run = RunReceive(device, binary, local, Fast(checksum: kind));
                Check(run.Error == null && run.Result && File.Exists(local) && File.ReadAllBytes(local).SequenceEqual(File.ReadAllBytes(binary)), "binary file, base64, checksum " + kind, Describe(run));
            }
            {
                string local = Path.Combine(localDir, "t.txt");
                Run run = RunReceive(device, text, local, Fast());
                Check(run.Error == null && run.Result && File.ReadAllBytes(local).SequenceEqual(File.ReadAllBytes(text)), "text file with a quote in its name", Describe(run));
                Check(run.ProgressTotal > 0 && run.ProgressDone == run.ProgressTotal || run.LastStatus.StartsWith("Done"), "progress reaches the end", run.ProgressDone + "/" + run.ProgressTotal + " " + run.LastStatus);
            }
            {
                string local = Path.Combine(localDir, "e.dat");
                Run run = RunReceive(device, empty, local, Fast());
                Check(run.Error == null && run.Result && new FileInfo(local).Length == 0, "empty file", Describe(run));
            }
            if (gnuUu)
            {
                string local = Path.Combine(localDir, "uu.bin");
                Run run = RunReceive(device, binary, local, Fast(GzipBase64Transfer.EncodingKind.UuEncode));
                Check(run.Error == null && run.Result && File.ReadAllBytes(local).SequenceEqual(File.ReadAllBytes(binary)), "binary file, uuencode", Describe(run));
            }
            {
                string local = Path.Combine(localDir, "nofile.bin");
                Run run = RunReceive(device, Path.Combine(remoteDir, "no-such-file"), local, Fast());
                Check(run.Error != null && run.Error.Message.Contains("not found") && !File.Exists(local), "missing file on the device is reported", Describe(run));
            }
            {
                string local = Path.Combine(localDir, "t.txt");
                Run run = RunReceive(device, text, local, Fast());
                Check(run.Error != null && run.Error.Message.Contains("already exists"), "existing local file is refused", Describe(run));
                run = RunReceive(device, binary, local, Fast(overwrite: true));
                Check(run.Error == null && run.Result && File.ReadAllBytes(local).SequenceEqual(File.ReadAllBytes(binary)), "Overwrite replaces the existing local file", Describe(run));
            }
            Check(device.Run("echo alive5").Contains("alive5"), "the shell is alive");
        }
        Directory.Delete(dir, true);
    }

    private static void ReceiveFaultCases()
    {
        string dir = NewWorkDir();
        string remoteDir = Path.Combine(dir, "remote");
        Directory.CreateDirectory(remoteDir);
        string binary = Path.Combine(remoteDir, "binary.bin");
        File.WriteAllBytes(binary, RandomBytes(80_000, 5));
        string localDir = Path.Combine(dir, "local");
        Directory.CreateDirectory(localDir);

        using (Device device = Ash())
        {
            Section("busybox ash: receive, damaged data and noise");
            {
                int chunks = 0;
                bool done = false;
                device.RxFilter = chunk =>
                {
                    if (!done && chunk.Length > 1000 && ++chunks == 3)
                    {
                        done = true;
                        // A kernel message in the middle of the data, and one damaged character.
                        List<byte> list = new List<byte>(chunk);
                        int at = list.IndexOf((byte)'\n') + 1;
                        list.InsertRange(at, Encoding.ASCII.GetBytes("[   12.345678] usb 1-1: new high-speed USB device number 4\r\n"));
                        return list.ToArray();
                    }
                    return chunk;
                };
                string local = Path.Combine(localDir, "noise.bin");
                Run run = RunReceive(device, binary, local, Fast());
                device.RxFilter = null;
                Check(run.Error == null && run.Result && File.ReadAllBytes(local).SequenceEqual(File.ReadAllBytes(binary)), "a console message inside the data is skipped", Describe(run));
            }
            {
                int chunks = 0;
                device.RxFilter = chunk =>
                {
                    if (chunk.Length > 1000 && ++chunks == 3)
                    {
                        // Flip a character of a data line (keeps it valid base64, so only the checksum can tell).
                        int i = Array.IndexOf(chunk, (byte)'\n') + 10;
                        chunk[i] = (byte)(chunk[i] == 'A' ? 'B' : 'A');
                    }
                    return chunk;
                };
                string local = Path.Combine(localDir, "flip.bin");
                Run run = RunReceive(device, binary, local, Fast());
                device.RxFilter = null;
                Check(run.Error == null && run.Result && File.ReadAllBytes(local).SequenceEqual(File.ReadAllBytes(binary)) && run.Errors >= 1, "a damaged character is caught and the second attempt succeeds", Describe(run) + " errors=" + run.Errors);
            }
            {
                device.RxFilter = chunk =>
                {
                    if (chunk.Length > 1000)
                    {
                        int i = Array.IndexOf(chunk, (byte)'\n') + 10;
                        chunk[i] = (byte)(chunk[i] == 'A' ? 'B' : 'A');
                    }
                    return chunk;
                };
                string local = Path.Combine(localDir, "bad.bin");
                Run run = RunReceive(device, binary, local, Fast());
                device.RxFilter = null;
                Check(run.Error is IOException && !File.Exists(local) && !File.Exists(local + ".smtty~"), "damage in every attempt fails and leaves nothing behind", Describe(run));
                Thread.Sleep(1500);
                device.Drain();
                Check(device.Run("echo alive6").Contains("alive6"), "the shell is alive");
            }
            {
                CancellationTokenSource cts = new CancellationTokenSource();
                string big = Path.Combine(remoteDir, "big.bin");
                File.WriteAllBytes(big, RandomBytes(900_000, 6));
                string local = Path.Combine(localDir, "cancelled.bin");
                Run run = new Run();
                Thread worker = new Thread(() => { RunReceive(device, big, local, Fast(), cts.Token, run); });
                worker.Start();
                int deadline = Environment.TickCount + 20000;
                while (Environment.TickCount < deadline && run.ProgressDone < 50_000 && worker.IsAlive)
                {
                    Thread.Sleep(20);
                }
                cts.Cancel();
                worker.Join(20000);
                Check(!worker.IsAlive && run.Error == null && run.Result == false && !File.Exists(local) && !File.Exists(local + ".smtty~"), "cancel while receiving returns false and leaves nothing", Describe(run));
                Thread.Sleep(1500);
                device.Drain();
                Check(device.Run("echo alive7").Contains("alive7"), "the shell answers after the cancel");
            }
        }
        Directory.Delete(dir, true);
    }

    private static void SlowLineCases()
    {
        string dir = NewWorkDir();
        string remoteDir = Path.Combine(dir, "remote");
        Directory.CreateDirectory(remoteDir);
        string binary = Path.Combine(dir, "binary.bin");
        File.WriteAllBytes(binary, RandomBytes(25_000, 11));
        string onDevice = Path.Combine(remoteDir, "big.bin");

        using (Device device = Ash())
        {
            device.BytesPerSecond = 11520; // 115200 baud, both ways, with the time it takes
            Section("busybox ash on a 115200 baud line");
            Run run = RunSend(device, binary, Path.Combine(remoteDir, "slow-send.bin"), Fast());
            Check(run.Error == null && run.Result && File.ReadAllBytes(Path.Combine(remoteDir, "slow-send.bin")).SequenceEqual(File.ReadAllBytes(binary)), "send at the speed of a real line", Describe(run));
            File.Copy(binary, onDevice);
            string local = Path.Combine(dir, "slow-receive.bin");
            run = RunReceive(device, onDevice, local, Fast());
            Check(run.Error == null && run.Result && File.ReadAllBytes(local).SequenceEqual(File.ReadAllBytes(binary)), "receive at the speed of a real line", Describe(run));
        }
        Directory.Delete(dir, true);
    }

    /// <summary>True when the shell of the device executes a command (and is not stuck in a reader that eats what is typed).</summary>
    private static bool Responds(Device device)
    {
        device.Drain();
        byte[] bytes = Encoding.ASCII.GetBytes("echo RE''SP\r");
        device.Write(bytes, 0, bytes.Length);
        StringBuilder text = new StringBuilder();
        int deadline = Environment.TickCount + 2500;
        while (Environment.TickCount < deadline)
        {
            byte? b = device.Read(100);
            if (b != null) text.Append((char)b.Value);
            if (text.ToString().Contains("RESP")) return true;
        }
        return false;
    }

    private static string Output(Device device, string command)
    {
        device.Drain();
        byte[] bytes = Encoding.ASCII.GetBytes(command + "\r");
        device.Write(bytes, 0, bytes.Length);
        StringBuilder text = new StringBuilder();
        int deadline = Environment.TickCount + 2000;
        while (Environment.TickCount < deadline)
        {
            byte? b = device.Read(100);
            if (b != null) text.Append((char)b.Value);
        }
        return text.ToString();
    }

    /// <summary>
    /// The connection goes away in the middle of a transfer (a reconnect, a cable pulled) while the console of the device stays
    /// where it is: the transfer cannot tell the device to stop, so it hands over what ends the device's reader, to be sent
    /// when there is a connection again. Here "connection again" is the same console, written to by the test.
    /// </summary>
    private static void RecoveryCases()
    {
        string dir = NewWorkDir();
        string remoteDir = Path.Combine(dir, "remote");
        Directory.CreateDirectory(remoteDir);
        string binary = Path.Combine(dir, "binary.bin");
        File.WriteAllBytes(binary, RandomBytes(120_000, 21));
        string big = Path.Combine(remoteDir, "big.bin");
        File.WriteAllBytes(big, RandomBytes(900_000, 22));

        using (Device device = Ash())
        {
            Section("busybox ash: the connection is lost in the middle of a transfer");
            {
                byte[] recovery = null;
                long sent = 0;
                bool cut = false;
                GzipBase64Transfer.Options options = Fast();
                options.RecoveryRequired = bytes => recovery = bytes;
                string target = Path.Combine(remoteDir, "cut.bin");
                Exception error = null;
                try
                {
                    GzipBase64Transfer.SendFile(binary, target, options,
                        (data, offset, length) =>
                        {
                            if (cut || (sent += length) > 30_000)
                            {
                                cut = true;
                                throw new IOException("The connection is closed.");
                            }
                            device.Write(data, offset, length);
                        },
                        timeout => cut ? null : device.Read(timeout), CancellationToken.None, null);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                Check(error is IOException && recovery != null && recovery.Length > 20, "a lost connection hands over the recovery", error == null ? "no error" : error.Message);
                Check(!Responds(device), "without it the console of the device is stuck and eats what is typed (the problem)");
                if (recovery != null)
                {
                    device.Write(recovery, 0, recovery.Length);
                    Thread.Sleep(1500);
                }
                Check(Responds(device), "after the recovery the shell answers again");
                Check(!File.Exists(target + ".smtty~") && !File.Exists(target), "the unfinished file is gone");
                Check(!Output(device, "trap").Contains("'' INT"), "the signals are restored (Ctrl-C works again)");
            }
            {
                byte[] recovery = null;
                long received = 0;
                bool cut = false;
                GzipBase64Transfer.Options options = Fast();
                options.RecoveryRequired = bytes => recovery = bytes;
                string local = Path.Combine(dir, "cut-receive.bin");
                Exception error = null;
                try
                {
                    GzipBase64Transfer.ReceiveFile(big, local, options,
                        (data, offset, length) =>
                        {
                            if (cut) throw new IOException("The connection is closed.");
                            device.Write(data, offset, length);
                        },
                        timeout =>
                        {
                            if (cut) return null;
                            byte? b = device.Read(timeout);
                            if (b != null && ++received > 60_000) { cut = true; return null; }
                            return b;
                        }, CancellationToken.None, null);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                Check(error is IOException && recovery != null, "a receive that loses its connection hands over a recovery too", error == null ? "no error" : error.Message);
                Check(!File.Exists(local) && !File.Exists(local + ".smtty~"), "no local file is left from it");
                if (recovery != null)
                {
                    device.Write(recovery, 0, recovery.Length);
                }
                Thread.Sleep(1500);
                device.Drain();
                Check(Responds(device), "the shell answers after the recovery of a receive");
            }
        }
        Directory.Delete(dir, true);
    }

    /// <summary>Putting the received file in place must never lose the old file or the checked copy.</summary>
    private static void FinalizeCases()
    {
        string dir = NewWorkDir();
        string remoteDir = Path.Combine(dir, "remote");
        Directory.CreateDirectory(remoteDir);
        string onDevice = Path.Combine(remoteDir, "data.bin");
        byte[] content = RandomBytes(30_000, 31);
        File.WriteAllBytes(onDevice, content);

        using (Device device = Ash())
        {
            Section("busybox ash: putting the received file in place");
            {
                // The destination is an existing folder: the file cannot be put there. The checked copy must stay.
                string local = Path.Combine(dir, "isfolder");
                Directory.CreateDirectory(local);
                Run run = RunReceive(device, onDevice, local, Fast());
                Check(run.Error is IOException && run.Error.Message.Contains("checked copy is kept") && Directory.Exists(local), "a destination that cannot be written reports it and keeps the checked copy", Describe(run));
                Check(File.Exists(local + ".smtty~") && File.ReadAllBytes(local + ".smtty~").SequenceEqual(content), "the checked copy is there and complete");
            }
            {
                // An existing file that cannot be replaced (immutable): the old file and the checked copy must both survive.
                string local = Path.Combine(dir, "locked.bin");
                byte[] old = RandomBytes(500, 32);
                File.WriteAllBytes(local, old);
                bool locked = RunTool("chattr", "+i \"" + local + "\"");
                if (!locked)
                {
                    Console.WriteLine("  skip the file that cannot be replaced (chattr +i is not available here)");
                }
                else
                {
                    Run run = RunReceive(device, onDevice, local, Fast(overwrite: true));
                    RunTool("chattr", "-i \"" + local + "\"");
                    Check(run.Error is IOException && File.ReadAllBytes(local).SequenceEqual(old) && File.Exists(local + ".smtty~") && File.ReadAllBytes(local + ".smtty~").SequenceEqual(content), "an existing file that cannot be replaced keeps both the old file and the checked copy", Describe(run));
                }
            }
            {
                // And the plain case: the old file is replaced in one step, nothing is left over.
                string local = Path.Combine(dir, "replace.bin");
                File.WriteAllBytes(local, RandomBytes(500, 33));
                Run run = RunReceive(device, onDevice, local, Fast(overwrite: true));
                Check(run.Error == null && run.Result && File.ReadAllBytes(local).SequenceEqual(content) && !File.Exists(local + ".smtty~"), "an existing file is replaced and nothing is left over", Describe(run));
            }
        }
        Directory.Delete(dir, true);
    }

    private static bool RunTool(string tool, string arguments)
    {
        try
        {
            using (Process p = Process.Start(new ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SilentDeviceCases()
    {
        string dir = NewWorkDir();
        string file = Path.Combine(dir, "x.bin");
        File.WriteAllBytes(file, RandomBytes(1000, 9));
        // "cat" only echoes: there is no shell to answer, which is what a console in the wrong state looks like.
        using (Device device = new Device("cat", "cat (no shell)"))
        {
            Section("a console without a shell");
            Run run = RunSend(device, file, "/tmp/x.bin", Fast());
            Check(run.Error is IOException && run.Error.Message.Contains("did not answer") && run.Error.Message.Contains("last output of the device"), "the error shows what the device last printed", Describe(run));
        }
        Directory.Delete(dir, true);
    }

    private static void UnitCases()
    {
        Section("helpers");
        Check(GzipBase64Transfer.ShellQuote("a'b") == "'a'\\''b'", "ShellQuote");
        Check(GzipBase64Transfer.ShellPath("~/x y") == "\"$HOME\"/'x y'" && GzipBase64Transfer.ShellPath("~") == "\"$HOME\"" && GzipBase64Transfer.ShellPath("/tmp/a") == "'/tmp/a'", "ShellPath");
        // cksum of "123456789" is 930766865 (0x377A6011) for the POSIX CRC with the length appended.
        Check(GzipBase64Transfer.Cksum(Encoding.ASCII.GetBytes("123456789")) == 930766865u, "Cksum matches the POSIX test value", GzipBase64Transfer.Cksum(Encoding.ASCII.GetBytes("123456789")).ToString());
    }

    private static bool Has(string tool)
    {
        try
        {
            using (Process p = Process.Start(new ProcessStartInfo("sh", "-c \"command -v " + tool + "\"") { RedirectStandardOutput = true, UseShellExecute = false }))
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int Main(string[] args)
    {
        if (!Has("socat") || !Has("busybox"))
        {
            Console.WriteLine("socat and busybox are needed.");
            return 2;
        }
        bool gnuUu = Has("uuencode") && Has("uudecode");
        // Optional first argument: run one group only (units, send-ash, receive-ash, fault, receive-fault, cancel, send-bash, receive-bash).
        string only = args.Length > 0 ? args[0] : null;
        bool Want(string group) => only == null || only == group;
        if (Want("units")) UnitCases();
        if (Want("silent")) SilentDeviceCases();
        if (Want("finalize")) FinalizeCases();
        if (Want("recovery")) RecoveryCases();
        if (Want("slow")) SlowLineCases();
        if (Want("send-ash")) SendCases(Ash, gnuUu: true, cksum: false);
        if (Want("receive-ash")) ReceiveCases(Ash, gnuUu: true, cksum: false);
        if (Want("fault")) FaultCases();
        if (Want("receive-fault")) ReceiveFaultCases();
        if (Want("cancel")) CancelCases();
        if (Want("send-bash")) SendCases(Bash, gnuUu: gnuUu, cksum: true);
        if (Want("receive-bash")) ReceiveCases(Bash, gnuUu: gnuUu, cksum: true);

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed, " + Failures.Count + " failed" + (gnuUu ? string.Empty : " (GNU uuencode cases skipped: sharutils missing)"));
        foreach (string failure in Failures)
        {
            Console.WriteLine("FAILED: " + failure);
        }
        return Failures.Count == 0 ? 0 : 1;
    }
}
