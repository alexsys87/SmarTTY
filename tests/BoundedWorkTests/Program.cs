using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmarTTY.Utilities;

// Checks of the bounded wait that closing a session uses (Utilities/BoundedWork.cs): the feeds of the local repository server are taken
// out of the device on a pool thread, for at most so long, and the sessions of a window that is closed are waited for together. A
// device that went away without closing the TCP connection never answers, and used to freeze the window for the network timeout of each
// such session in turn.
// Run with: dotnet run --project tests/BoundedWorkTests
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

    private static int Main()
    {
        // The test blocks a few pool threads on purpose; a machine with two cores would add them one by one and the times below would be those of the pool.
        ThreadPool.SetMinThreads(64, 64);
        bool unobserved = false;
        TaskScheduler.UnobservedTaskException += (sender, args) => unobserved = true;
        List<ManualResetEventSlim> gates = new List<ManualResetEventSlim>();
        ManualResetEventSlim Gate()
        {
            ManualResetEventSlim gate = new ManualResetEventSlim(false);
            lock (gates)
            {
                gates.Add(gate);
            }
            return gate;
        }
        try
        {
            // Work that finishes is waited for to its end.
            bool ran = false;
            Check(BoundedWork.Run(() => { Thread.Sleep(50); ran = true; }, TimeSpan.FromSeconds(5)) && ran, "run: work that finishes in time has run when Run returns true");

            // Work that never ends is given up at the limit, and keeps running without being waited for.
            ManualResetEventSlim hang = Gate();
            bool released = false;
            Stopwatch watch = Stopwatch.StartNew();
            bool finished = BoundedWork.Run(() => { hang.Wait(); released = true; }, TimeSpan.FromMilliseconds(300));
            watch.Stop();
            Check(!finished, "run: work that does not end is given up (false)");
            Check(watch.ElapsedMilliseconds >= 250 && watch.ElapsedMilliseconds < 2000, "run: that took about the limit, not as long as the work (" + watch.ElapsedMilliseconds + " ms)");
            Check(!released, "run: the work given up is still running");
            hang.Set();
            SpinWait.SpinUntil(() => released, 5000);
            Check(released, "run: the work given up ends when what it waited for is gone (the connection is closed)");

            // An error is not the caller's business.
            Check(BoundedWork.Run(() => throw new InvalidOperationException("the connection was lost"), TimeSpan.FromSeconds(5)), "run: work that throws counts as finished");

            // Work that is given up and fails later leaves nothing unobserved behind.
            ManualResetEventSlim failLater = Gate();
            Check(!BoundedWork.Run(() => { failLater.Wait(); throw new InvalidOperationException("late"); }, TimeSpan.FromMilliseconds(100)), "run: work that fails after it was given up is given up");
            failLater.Set();
            Thread.Sleep(300);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(100);
            Check(!unobserved, "run: the late error is observed");

            // Sessions that do not answer are waited for together: the time of one, not the sum.
            int sessions = 6;
            TimeSpan limit = TimeSpan.FromMilliseconds(400);
            List<Task> cleanups = new List<Task>();
            for (int index = 0; index < sessions; index++)
            {
                ManualResetEventSlim silent = Gate();
                cleanups.Add(Task.Run(() => BoundedWork.Run(() => silent.Wait(), limit)));
            }
            watch.Restart();
            bool allDone = BoundedWork.WaitAll(cleanups, TimeSpan.FromSeconds(10));
            watch.Stop();
            Check(allDone, "wait all: the cleanups of sessions that do not answer all end at their limit");
            Check(watch.ElapsedMilliseconds < limit.TotalMilliseconds * 3, sessions + " sessions that do not answer cost about one limit, not " + sessions + " (" + watch.ElapsedMilliseconds + " ms of " + (int)(limit.TotalMilliseconds * sessions) + " ms in turn)");

            // The wait itself is bounded.
            ManualResetEventSlim never = Gate();
            Task stuck = Task.Run(() => never.Wait());
            watch.Restart();
            Check(!BoundedWork.WaitAll(new[] { stuck, Task.CompletedTask }, TimeSpan.FromMilliseconds(200)), "wait all: a task that does not end is reported (false)");
            watch.Stop();
            Check(watch.ElapsedMilliseconds < 2000, "wait all: and it is not waited for longer than the limit (" + watch.ElapsedMilliseconds + " ms)");
            Task failed = Task.Run(() => throw new InvalidOperationException("failed"));
            Check(BoundedWork.WaitAll(new[] { failed }, TimeSpan.FromSeconds(5)), "wait all: a task that failed counts as finished");
            Check(BoundedWork.WaitAll(new Task[0], TimeSpan.FromMilliseconds(10)) && BoundedWork.WaitAll(null, TimeSpan.FromMilliseconds(10)), "wait all: no tasks is done at once");
            Check(BoundedWork.WaitAll(new Task[] { null, Task.CompletedTask }, TimeSpan.FromMilliseconds(10)), "wait all: a missing task is skipped");

            Console.WriteLine();
            Console.WriteLine("All " + _passed + " checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            lock (gates)
            {
                foreach (ManualResetEventSlim gate in gates)
                {
                    gate.Set();
                }
            }
        }
    }
}
