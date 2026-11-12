using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SmarTTY.Ssh;

// Checks of the flow control of received SSH output (Ssh/ReceiveFlowGate.cs): the thread that reads from the connection waits while
// the terminal is too far behind, so that a host writing faster than the terminal can draw (yes, cat of a huge file) is slowed down
// by TCP instead of the backlog growing without bound; and it never waits for good.
// Run with: dotnet run --project tests/ReceiveFlowGateTests
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
        try
        {
            TimeSpan shortWait = TimeSpan.FromMilliseconds(150);
            TimeSpan longWait = TimeSpan.FromSeconds(10);

            // Below the limit nothing waits.
            ReceiveFlowGate gate = new ReceiveFlowGate(1000);
            gate.Add(1000);
            Check(gate.WaitForRoom(longWait) && gate.Pending == 1000, "at the limit the reader goes on");

            // Past it the reader waits, and gives up after the time it was given (it must not wait for good).
            gate.Add(1);
            Stopwatch watch = Stopwatch.StartNew();
            Check(!gate.WaitForRoom(shortWait), "past the limit the reader waits and then gives up");
            Check(watch.ElapsedMilliseconds >= 100 && watch.ElapsedMilliseconds < 5000, "the wait is bounded by the time given (" + watch.ElapsedMilliseconds + " ms)");

            // Having been held back, it resumes only when the backlog is half the limit, not at the limit.
            gate.Release(400);
            Check(gate.Pending == 601 && !gate.WaitForRoom(shortWait), "below the limit but above half of it the reader still waits");
            Task<bool> waiting = Task.Run(() => gate.WaitForRoom(longWait));
            Thread.Sleep(100);
            Check(!waiting.IsCompleted, "the reader is asleep while the consumer is behind");
            gate.Release(101);
            Check(waiting.Wait(longWait) && waiting.Result && gate.Pending == 500, "released at half the limit, the reader wakes up in time");
            Check(gate.WaitForRoom(shortWait), "and goes on without waiting while the backlog stays small");

            // Open() ends the limiting for good (the channel is closed, nobody is left to release anything).
            ReceiveFlowGate closing = new ReceiveFlowGate(100);
            closing.Add(5000);
            Task<bool> stuck = Task.Run(() => closing.WaitForRoom(longWait));
            Thread.Sleep(100);
            closing.Open();
            Check(stuck.Wait(longWait) && stuck.Result, "Open wakes a waiting reader at once");
            Check(closing.WaitForRoom(shortWait), "after Open the reader never waits");

            // More released than added (a chunk that was never counted) does not make the backlog negative.
            ReceiveFlowGate over = new ReceiveFlowGate(100);
            over.Add(10);
            over.Release(50);
            Check(over.Pending == 0, "the backlog does not go below zero");

            // A fast producer and a slow consumer: the backlog stays near the limit instead of growing with the data.
            const int chunk = 32 * 1024;
            const long limit = 1024 * 1024;
            const int chunks = 600;
            ReceiveFlowGate flow = new ReceiveFlowGate(limit);
            long maxPending = 0;
            System.Collections.Concurrent.BlockingCollection<int> queue = new System.Collections.Concurrent.BlockingCollection<int>();
            Task consumer = Task.Run(() =>
            {
                foreach (int size in queue.GetConsumingEnumerable())
                {
                    Thread.Sleep(1);
                    flow.Release(size);
                }
            });
            for (int i = 0; i < chunks; i++)
            {
                flow.Add(chunk);
                queue.Add(chunk);
                maxPending = Math.Max(maxPending, flow.Pending);
                flow.WaitForRoom(longWait);
            }
            queue.CompleteAdding();
            consumer.Wait(longWait);
            Check(maxPending <= limit + chunk, "the backlog stays near the limit with a fast producer (max " + maxPending + " of " + chunks * (long)chunk + " bytes sent)");
            Check(flow.Pending == 0, "and is empty once everything is processed");

            Console.WriteLine();
            Console.WriteLine("All " + _passed + " checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
    }
}
