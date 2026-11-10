using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SmarTTY.Utilities;

// Regression harness for the paths that the device terminals (Serial, Telnet, TCP, Netconsole) make links of: the text comes
// from the device, so a network path must never be looked at, and painting must never wait for the file system.
// Run with: dotnet run --project tests/LocalLinkTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    /// <summary>The thread pool of a test: what is given to it runs only when the test says so.</summary>
    private sealed class ManualRunner
    {
        public readonly List<Action> Queue = new List<Action>();

        public void Run(Action work)
        {
            Queue.Add(work);
        }

        public void RunAll()
        {
            while (Queue.Count > 0)
            {
                Action work = Queue[0];
                Queue.RemoveAt(0);
                work();
            }
        }
    }

    /// <summary>What the terminal uses: a thread of its own for the worker, so a drive that hangs holds nothing but that thread.</summary>
    private static void RunOnOwnThread(Action work)
    {
        new Thread(() => work()) { IsBackground = true, Name = "test path check" }.Start();
    }

    private sealed class Fixture
    {
        public readonly ManualRunner Runner = new ManualRunner();

        public readonly Dictionary<string, bool> Disk = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public readonly List<string> Checked = new List<string>();

        public int Changed;

        public long Now;

        public Func<string, bool> ThrowFor;

        public PathExistenceCache Cache;

        public Fixture(int maxAge = 2000, int maxPending = 64, int maxWorkers = 2, int maxEntries = 2048)
        {
            Cache = new PathExistenceCache(
                path =>
                {
                    Checked.Add(path);
                    if (ThrowFor != null && ThrowFor(path))
                    {
                        throw new InvalidOperationException("the drive failed");
                    }
                    return Disk.TryGetValue(path, out bool exists) && exists;
                },
                Runner.Run,
                () => Changed++,
                () => Now,
                maxAge, maxPending, maxWorkers, maxEntries);
        }
    }

    private static int Main()
    {
        // --- network and device paths are never looked at ---
        foreach (string path in new[]
        {
            @"\\host\share\file.txt", "//host/share/file.txt", @"\\10.0.0.5\share\a", "//10.0.0.5/share/a", @"\/host\share", @"/\host/share",
            @"\\?\C:\Windows\x.log", @"\\?\UNC\host\share\x", @"\\.\pipe\x", @"\\.\COM3", "\\\\", "//",
        })
        {
            Check(LocalLinkRules.IsNetworkOrDevicePath(path), "rules: " + path + " is a network or device path");
        }
        foreach (string path in new[]
        {
            @"C:\Windows\x.log", "C:/Windows/x.log", "/etc/passwd", @"\Windows\x", "/", @"\", @"a\b.txt", "foo//bar", @"foo\\bar", "http://host/x",
            @"C:\\server\x", "relative.txt", "", null,
        })
        {
            Check(!LocalLinkRules.IsNetworkOrDevicePath(path), "rules: " + (path ?? "null") + " is an ordinary path");
        }

        // --- only a path with a drive letter is one of this machine ---
        // A Linux host behind a telnet or serial line prints its own paths: "cd /tmp" or the panel of mc showed /tmp as a link that
        // opened C:\tmp of this computer in Explorer.
        foreach (string path in new[] { @"C:\Windows\x.log", "C:/Windows/x.log", @"d:\build\out.txt", @"Z:\", "C:/" })
        {
            Check(LocalLinkRules.IsLocalPath(path), "local: " + path + " is a path of this machine");
        }
        foreach (string path in new[]
        {
            "/tmp", "/", "/etc/passwd", "/var/log/syslog", "/home/user/notes.txt", @"\Windows\x", @"\", "relative.txt", @"a\b.txt", "./run.sh", "../x.log",
            "C:", "C:foo.txt", "1:/x", "::/x", @"\\host\share\x.txt", "//host/share/x.txt", @"C:\\server\share", "C://server/share", "", null,
        })
        {
            Check(!LocalLinkRules.IsLocalPath(path), "local: " + (path ?? "null") + " is not a path of this machine");
        }

        // --- the cache: nothing is read on the thread that asks ---
        Fixture f = new Fixture();
        f.Disk["a"] = true;
        Check(!f.Cache.Exists("a"), "cache: a path that was never checked is reported as missing");
        Check(f.Checked.Count == 0, "cache: the question did not read anything on the caller's thread");
        Check(f.Runner.Queue.Count == 1, "cache: the check was handed to the background");
        f.Runner.RunAll();
        Check(f.Checked.SequenceEqual(new[] { "a" }), "cache: the background checked the path once");
        Check(f.Cache.Exists("a"), "cache: the answer is there after the check");
        Check(f.Changed == 1, "cache: the owner was told that a path turned out to exist (to paint again)");
        Check(f.Cache.Exists("a") && f.Checked.Count == 1 && f.Runner.Queue.Count == 0, "cache: a known answer costs no check");

        f = new Fixture();
        for (int i = 0; i < 100; i++)
        {
            f.Cache.Exists("b");
        }
        f.Runner.RunAll();
        Check(f.Checked.Count == 1, "cache: a hundred questions before the check ran make one check");

        f = new Fixture();
        f.Cache.Exists("missing");
        f.Runner.RunAll();
        Check(!f.Cache.Exists("missing") && f.Changed == 0, "cache: a path that does not exist is no change for the owner");

        // --- a result gets old ---
        f = new Fixture(maxAge: 2000);
        f.Disk["a"] = true;
        f.Cache.Exists("a");
        f.Runner.RunAll();
        f.Now = 1000;
        Check(f.Cache.Exists("a") && f.Runner.Queue.Count == 0, "cache: a result is kept within its age");
        f.Now = 3000;
        f.Disk["a"] = false;
        Check(f.Cache.Exists("a"), "cache: an old result is still the answer at once");
        Check(f.Runner.Queue.Count == 1, "cache: and it is checked again in the background");
        f.Runner.RunAll();
        Check(!f.Cache.Exists("a") && f.Changed == 2, "cache: a file that went away is seen on the next question and the owner is told");
        f.Now = 6000;
        f.Disk["a"] = true;
        f.Cache.Exists("a");
        f.Runner.RunAll();
        Check(f.Cache.Exists("a") && f.Changed == 3, "cache: a file that appeared is seen as well");
        f.Now = 9000;
        f.Cache.Exists("a");
        f.Runner.RunAll();
        Check(f.Changed == 3, "cache: a check with the same result does not make the owner paint again");

        // --- limits ---
        f = new Fixture(maxWorkers: 2);
        for (int i = 0; i < 10; i++)
        {
            f.Cache.Exists("p" + i);
        }
        Check(f.Runner.Queue.Count == 2, "limits: only two workers are started, a hanging drive cannot take the whole pool");
        f.Runner.RunAll();
        Check(f.Checked.Count == 10, "limits: the workers still check every path");

        f = new Fixture(maxPending: 5);
        for (int i = 0; i < 20; i++)
        {
            f.Cache.Exists("q" + i);
        }
        f.Runner.RunAll();
        Check(f.Checked.Count == 5, "limits: paths beyond the waiting limit are dropped, not queued without end");
        for (int round = 0; round < 10 && f.Checked.Count < 20; round++)
        {
            for (int i = 0; i < 20; i++)
            {
                f.Cache.Exists("q" + i);
            }
            f.Runner.RunAll();
        }
        Check(f.Checked.Distinct().Count() == 20, "limits: the dropped paths are asked for again by the next paint and get checked");

        f = new Fixture(maxEntries: 3);
        for (int i = 0; i < 10; i++)
        {
            f.Disk["e" + i] = i % 2 == 0;
            f.Cache.Exists("e" + i);
            f.Runner.RunAll();
        }
        // e8 was dropped when the cache started again: it is unknown (reported as missing) until it is checked again.
        bool droppedAtOnce = f.Cache.Exists("e8");
        f.Runner.RunAll();
        Check(!droppedAtOnce && f.Cache.Exists("e8") && !f.Cache.Exists("e9"), "limits: a cache that outgrew its size starts again, the dropped paths are checked again and answer right");

        // --- trouble in the check ---
        f = new Fixture();
        f.Disk["good"] = true;
        f.ThrowFor = path => path == "bad";
        f.Cache.Exists("bad");
        f.Cache.Exists("good");
        f.Runner.RunAll();
        Check(!f.Cache.Exists("bad") && f.Cache.Exists("good"), "trouble: a check that throws means 'missing' and the worker goes on");
        Check(!f.Cache.Exists(null) && !f.Cache.Exists(""), "trouble: no path, no answer, no check");

        f = new Fixture();
        PathExistenceCache refusing = new PathExistenceCache(path => true, work => throw new InvalidOperationException("no threads"), null);
        Check(!refusing.Exists("x") && !refusing.Exists("x"), "trouble: a pool that refuses the work does not break the caller");

        // --- a drive that does not answer ---
        using (ManualResetEvent release = new ManualResetEvent(false))
        using (ManualResetEvent entered = new ManualResetEvent(false))
        {
            int changed = 0;
            PathExistenceCache hung = new PathExistenceCache(
                path =>
                {
                    if (path.StartsWith("Z:"))
                    {
                        entered.Set();
                        release.WaitOne(10000);
                    }
                    return true;
                },
                RunOnOwnThread,
                () => Interlocked.Increment(ref changed));
            Stopwatch clock = Stopwatch.StartNew();
            bool answer = hung.Exists(@"Z:\share\file.txt");
            Check(!answer && clock.ElapsedMilliseconds < 2000, "hang: asking about a path on a dead drive returns at once");
            Check(entered.WaitOne(5000), "hang: the check is really stuck on it in the background");
            clock.Restart();
            bool other = hung.Exists(@"C:\ok\file.txt");
            Check(!other && clock.ElapsedMilliseconds < 2000, "hang: and so does the next question");
            Thread.Sleep(300);
            Check(hung.Exists(@"C:\ok\file.txt"), "hang: a second worker answers about other paths while one drive hangs");
            release.Set();
            for (int i = 0; i < 100 && !hung.Exists(@"Z:\share\file.txt"); i++)
            {
                Thread.Sleep(50);
            }
            Check(hung.Exists(@"Z:\share\file.txt"), "hang: the answer arrives when the drive answers");
            Check(Volatile.Read(ref changed) == 2, "hang: the owner was told about both");
        }

        // --- many threads at once (the painting thread and the mouse handler never meet, but the workers do) ---
        {
            int checksOnCallers = 0;
            HashSet<int> callerThreads = new HashSet<int>();
            object callersGate = new object();
            int calls = 0;
            PathExistenceCache shared = new PathExistenceCache(
                path =>
                {
                    Interlocked.Increment(ref calls);
                    lock (callersGate)
                    {
                        if (callerThreads.Contains(Thread.CurrentThread.ManagedThreadId))
                        {
                            checksOnCallers++;
                        }
                    }
                    return path.Length % 2 == 0;
                },
                RunOnOwnThread,
                null,
                maxPending: 100000, maxEntries: 100000);
            Thread[] threads = new Thread[8];
            for (int t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(() =>
                {
                    lock (callersGate)
                    {
                        callerThreads.Add(Thread.CurrentThread.ManagedThreadId);
                    }
                    for (int round = 0; round < 20; round++)
                    {
                        for (int i = 0; i < 500; i++)
                        {
                            shared.Exists("path-" + new string('x', i % 7) + i);
                        }
                    }
                });
                threads[t].Start();
            }
            foreach (Thread thread in threads)
            {
                thread.Join();
            }
            for (int i = 0; i < 200; i++)
            {
                int before = Volatile.Read(ref calls);
                Thread.Sleep(25);
                if (before == Volatile.Read(ref calls) && before >= 500)
                {
                    break;
                }
            }
            bool allRight = true;
            for (int i = 0; i < 500; i++)
            {
                string path = "path-" + new string('x', i % 7) + i;
                allRight &= shared.Exists(path) == (path.Length % 2 == 0);
            }
            Check(allRight, "threads: after the threads are done every path has the right answer");
            Check(Volatile.Read(ref calls) == 500, "threads: 80000 questions about 500 paths made 500 checks");
            Check(checksOnCallers == 0, "threads: no check ran on a thread that asked");
        }

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed.");
        return 0;
    }
}
