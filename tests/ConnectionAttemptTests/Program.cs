using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

// Regression harness for the cancelling of a connection attempt (ConnectionAttempt.cs):
//  * cancelling closes the session of the worker, off the cancelling thread, exactly once,
//  * a session that was handed over (the connection is established) is not closed by a later cancel,
//  * a cancel that comes first wins: the session is closed and nothing is handed over,
//  * a cancel never waits for the worker or for the closing of the session.
// Run with: dotnet run --project tests/ConnectionAttemptTests
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

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private static int Main()
    {
        try
        {
            CheckCancelClosesSession();
            CheckHandedOverSessionStaysOpen();
            CheckCancelWins();
            CheckCancelDoesNotWait();
            CheckCancelWithoutSession();
            CheckRace();
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
    }

    private sealed class FakeSession : IDisposable
    {
        private int _disposeCount;

        private readonly ManualResetEventSlim _disposed = new ManualResetEventSlim(false);

        private readonly ManualResetEventSlim _block;

        public FakeSession(ManualResetEventSlim block = null)
        {
            _block = block;
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool WaitDisposed(int milliseconds) => _disposed.Wait(milliseconds);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            _block?.Wait();
            _disposed.Set();
        }
    }

    private static void CheckCancelClosesSession()
    {
        ConnectionAttempt attempt = new ConnectionAttempt();
        FakeSession session = new FakeSession();
        attempt.SetSession(session);
        Check(!attempt.Cancelled, "cancel: not cancelled before");
        attempt.Dispose();
        Check(attempt.Cancelled, "cancel: marked as cancelled");
        Check(attempt.CancelEvent.WaitOne(0), "cancel: the cancel event is set");
        Check(session.WaitDisposed(5000), "cancel: the session of the worker is closed");
        attempt.Dispose();
        Thread.Sleep(100);
        Equal(session.DisposeCount, 1, "cancel: cancelling twice closes the session once");
        bool threw = false;
        try
        {
            attempt.ThrowIfCancelled();
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        Check(threw, "cancel: ThrowIfCancelled throws");
    }

    private static void CheckHandedOverSessionStaysOpen()
    {
        ConnectionAttempt attempt = new ConnectionAttempt();
        FakeSession session = new FakeSession();
        attempt.SetSession(session);
        Check(attempt.TryReleaseSession(), "handover: released while not cancelled");
        attempt.Dispose();
        Thread.Sleep(200);
        Equal(session.DisposeCount, 0, "handover: a later cancel does not close the session of the connection");
        Check(attempt.CancelEvent.WaitOne(0), "handover: the cancel event is still set");
    }

    private static void CheckCancelWins()
    {
        // The cancel arrives before the worker has registered its session.
        ConnectionAttempt early = new ConnectionAttempt();
        early.Dispose();
        FakeSession lateSession = new FakeSession();
        bool threw = false;
        try
        {
            early.SetSession(lateSession);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        Check(threw, "cancel first: registering a session throws");
        Equal(lateSession.DisposeCount, 1, "cancel first: the late session is closed");

        // The cancel arrives after registering and before the handover.
        ConnectionAttempt attempt = new ConnectionAttempt();
        FakeSession session = new FakeSession();
        attempt.SetSession(session);
        attempt.Dispose();
        Check(!attempt.TryReleaseSession(), "cancel before handover: nothing is handed over");
        Check(session.WaitDisposed(5000), "cancel before handover: the session is closed");
        Equal(session.DisposeCount, 1, "cancel before handover: closed once");
    }

    private static void CheckCancelDoesNotWait()
    {
        // A session whose closing takes long (a dead network): the UI thread must get back at once.
        using ManualResetEventSlim block = new ManualResetEventSlim(false);
        ConnectionAttempt attempt = new ConnectionAttempt();
        FakeSession slow = new FakeSession(block);
        attempt.SetSession(slow);
        Stopwatch watch = Stopwatch.StartNew();
        attempt.Dispose();
        long elapsed = watch.ElapsedMilliseconds;
        Check(elapsed < 1000, $"no wait: cancelling returns while the session is still closing ({elapsed} ms)");
        block.Set();
        Check(slow.WaitDisposed(5000), "no wait: the session is closed in the end");
    }

    private static void CheckCancelWithoutSession()
    {
        ConnectionAttempt attempt = new ConnectionAttempt();
        attempt.Dispose();
        Check(attempt.Cancelled, "no session: cancelling an attempt that has none works");
        Check(!attempt.TryReleaseSession(), "no session: nothing to hand over after a cancel");
    }

    // The worker registers and hands over its session while the UI thread cancels, at every possible moment:
    // the session ends up either handed over and open, or closed exactly once, never both and never leaked.
    private static void CheckRace()
    {
        const int Rounds = 3000;
        int handedOver = 0;
        int cancelled = 0;
        Random random = new Random(12345);
        for (int round = 0; round < Rounds; round++)
        {
            ConnectionAttempt attempt = new ConnectionAttempt();
            FakeSession session = new FakeSession();
            bool released = false;
            int spinWorker = random.Next(0, 200);
            int spinCancel = random.Next(0, 200);
            using ManualResetEventSlim go = new ManualResetEventSlim(false);
            Thread worker = new Thread(() =>
            {
                go.Wait();
                Thread.SpinWait(spinWorker);
                try
                {
                    attempt.SetSession(session);
                    Thread.SpinWait(spinWorker);
                    released = attempt.TryReleaseSession();
                }
                catch (OperationCanceledException)
                {
                }
            });
            Thread canceller = new Thread(() =>
            {
                go.Wait();
                Thread.SpinWait(spinCancel);
                attempt.Dispose();
            });
            worker.Start();
            canceller.Start();
            go.Set();
            worker.Join();
            canceller.Join();
            if (released)
            {
                handedOver++;
                Thread.Sleep(0);
                if (session.DisposeCount != 0)
                {
                    throw new Exception("FAIL: race: a handed-over session was closed (round " + round + ")");
                }
            }
            else
            {
                cancelled++;
                if (!session.WaitDisposed(5000) || session.DisposeCount != 1)
                {
                    throw new Exception("FAIL: race: a cancelled attempt's session was not closed exactly once (round " + round + ", count " + session.DisposeCount + ")");
                }
            }
        }
        Check(true, $"race: {Rounds} rounds, {handedOver} handed over and open, {cancelled} cancelled and closed once");
    }
}
