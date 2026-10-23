using System;
using VisualTTY.DeviceTerminal;

// Regression harness for the delay between automatic reconnects of COM/TCP/Telnet terminals.
// Run with: dotnet run --project tests/AutoReconnectPolicyTests
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
        // A new terminal reconnects at once.
        AutoReconnectPolicy policy = new AutoReconnectPolicy();
        Check(policy.NextDelayMs(true, 2000) == 0, "fresh policy: immediate mode has no delay");
        Check(policy.NextDelayMs(false, 2000) == 2000, "fresh policy: interval mode waits the interval");

        // A server that accepts and closes at once: the first retry is immediate, later ones back off.
        long now = 1000;
        int[] expected = { 0, 1000, 2000, 4000, 8000, 10000, 10000, 10000 };
        for (int i = 0; i < expected.Length; i++)
        {
            policy.OnConnected(now);
            now += 20;
            policy.OnEnded(now);
            Check(policy.NextDelayMs(true, 2000) == expected[i], "immediate mode, drop #" + (i + 1) + " waits " + expected[i] + " ms");
            now += expected[i];
        }

        // The backoff only lengthens the configured interval, it never shortens it.
        Check(policy.NextDelayMs(false, 2000) == 10000, "interval mode: backoff above the interval wins");
        policy = new AutoReconnectPolicy();
        for (int i = 0; i < 3; i++)
        {
            policy.OnConnected(now);
            policy.OnEnded(now + 10);
        }
        Check(policy.BackoffMs == 2000 && policy.NextDelayMs(false, 5000) == 5000, "interval mode: a longer interval is kept");

        // Attempts that never connected (Failed) count as well.
        policy = new AutoReconnectPolicy();
        policy.OnEnded(now);
        Check(policy.FailedAttempts == 1 && policy.NextDelayMs(true, 2000) == 0, "first failed attempt is retried at once");
        policy.OnEnded(now + 1500);
        Check(policy.NextDelayMs(true, 2000) == 1000, "second failed attempt backs off");

        // A connection that stayed up starts everything over.
        policy.OnConnected(now + 3000);
        policy.OnEnded(now + 3000 + AutoReconnectPolicy.StableConnectionMs);
        Check(policy.FailedAttempts == 0 && policy.NextDelayMs(true, 2000) == 0, "a stable connection resets the backoff");

        policy.OnConnected(now + 20000);
        policy.OnEnded(now + 20000 + AutoReconnectPolicy.StableConnectionMs - 1);
        Check(policy.FailedAttempts == 1, "a connection just under the limit still counts as failed");

        // A manual connect or disconnect forgets the history.
        for (int i = 0; i < 6; i++)
        {
            policy.OnEnded(now);
        }
        Check(policy.BackoffMs > 0, "backoff is active before reset");
        policy.Reset();
        Check(policy.FailedAttempts == 0 && policy.BackoffMs == 0, "Reset clears the backoff");

        // The counter never overflows into a negative delay.
        policy = new AutoReconnectPolicy();
        for (int i = 0; i < 100000; i++)
        {
            policy.OnEnded(now);
        }
        Check(policy.NextDelayMs(true, 0) == AutoReconnectPolicy.MaxBackoffMs, "delay stays capped after very many failures");

        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
