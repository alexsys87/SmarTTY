using System;
using System.Diagnostics;
using System.Threading;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Checks of Ssh/PtySetupGate.cs, which keeps the helper commands without a PTY from overlapping with setting up a
/// PTY channel (Dropbear on OpenWRT drops the PTY channel's request otherwise). They need no server.
/// </summary>
internal static class PtySetupGateTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);

    public static void Run()
    {
        Test.Section("PTY setup gate (Dropbear helper command race)");
        SetupWaitsForRunningHelper();
        HelperWaitsForSetup();
        HelpersRunTogether();
        SetupsAreSerialized();
        LongHelperDelaysSetupOnlyUntilTimeout();
        WaitingSetupKeepsNewHelpersBack();
        SecondSetupAfterTimeoutKeepsGateClosed();
        HelperStreamDoesNotStarveSetup();
    }

    // Starts the action on a thread and returns an event set once it has returned.
    private static ManualResetEventSlim StartWaiting(Action action)
    {
        ManualResetEventSlim done = new ManualResetEventSlim(false);
        new Thread(() =>
        {
            action();
            done.Set();
        })
        {
            IsBackground = true
        }.Start();
        return done;
    }

    private static void SetupWaitsForRunningHelper()
    {
        PtySetupGate gate = new PtySetupGate(TimeSpan.FromSeconds(30));
        gate.BeginPlainHelper();
        ManualResetEventSlim setupStarted = StartWaiting(gate.BeginPtySetup);
        Test.Check(!setupStarted.Wait(Settle), "a PTY setup waits while a helper command runs");
        gate.EndPlainHelper();
        Test.Check(setupStarted.Wait(TimeSpan.FromSeconds(5)), "the PTY setup starts once the helper command has finished");
        gate.EndPtySetup();
    }

    private static void HelperWaitsForSetup()
    {
        PtySetupGate gate = new PtySetupGate(TimeSpan.FromSeconds(30));
        gate.BeginPtySetup();
        ManualResetEventSlim helperStarted = StartWaiting(gate.BeginPlainHelper);
        Test.Check(!helperStarted.Wait(Settle), "a helper command waits while a PTY channel is being set up");
        gate.EndPtySetup();
        Test.Check(helperStarted.Wait(TimeSpan.FromSeconds(5)), "the helper command starts once the PTY setup is done");
        gate.EndPlainHelper();
    }

    private static void HelpersRunTogether()
    {
        PtySetupGate gate = new PtySetupGate(TimeSpan.FromSeconds(30));
        gate.BeginPlainHelper();
        ManualResetEventSlim secondStarted = StartWaiting(gate.BeginPlainHelper);
        Test.Check(secondStarted.Wait(TimeSpan.FromSeconds(5)), "helper commands do not wait for each other");
        gate.EndPlainHelper();
        gate.EndPlainHelper();
        ManualResetEventSlim setupStarted = StartWaiting(gate.BeginPtySetup);
        Test.Check(setupStarted.Wait(TimeSpan.FromSeconds(5)), "a PTY setup starts right away when no helper runs");
        gate.EndPtySetup();
    }

    private static void SetupsAreSerialized()
    {
        PtySetupGate gate = new PtySetupGate(TimeSpan.FromSeconds(30));
        gate.BeginPtySetup();
        ManualResetEventSlim secondSetup = StartWaiting(gate.BeginPtySetup);
        Test.Check(!secondSetup.Wait(Settle), "PTY setups run one after another");
        gate.EndPtySetup();
        Test.Check(secondSetup.Wait(TimeSpan.FromSeconds(5)), "the next PTY setup starts when the previous one is done");
        gate.EndPtySetup();
    }

    private static void LongHelperDelaysSetupOnlyUntilTimeout()
    {
        PtySetupGate gate = new PtySetupGate(TimeSpan.FromMilliseconds(500));
        gate.BeginPlainHelper();
        Stopwatch stopwatch = Stopwatch.StartNew();
        ManualResetEventSlim setupStarted = StartWaiting(gate.BeginPtySetup);
        Test.Check(setupStarted.Wait(TimeSpan.FromSeconds(5)), "a helper command that does not finish delays a PTY setup only until the timeout");
        Test.Check(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(400), "the PTY setup did wait for the helper command first");
        gate.EndPtySetup();
        gate.EndPlainHelper();
    }

    // A setup that waits for the running helpers goes first: a helper that is asked for meanwhile must not start in front of it.
    private static void WaitingSetupKeepsNewHelpersBack()
    {
        PtySetupGate gate = new PtySetupGate(TimeSpan.FromSeconds(30));
        gate.BeginPlainHelper();
        ManualResetEventSlim setupStarted = StartWaiting(gate.BeginPtySetup);
        Test.Check(!setupStarted.Wait(Settle), "the PTY setup waits for the running helper");
        ManualResetEventSlim lateHelper = StartWaiting(gate.BeginPlainHelper);
        Test.Check(!lateHelper.Wait(Settle), "a helper that is asked for meanwhile waits behind the waiting PTY setup");
        gate.EndPlainHelper();
        Test.Check(setupStarted.Wait(TimeSpan.FromSeconds(5)), "the PTY setup starts when the first helper has ended");
        Test.Check(!lateHelper.Wait(Settle), "the late helper still waits while the setup runs");
        gate.EndPtySetup();
        Test.Check(lateHelper.Wait(TimeSpan.FromSeconds(5)), "the late helper starts when the setup is done");
        gate.EndPlainHelper();
    }

    // Two setups that ran out of time together are both in progress: the end of the first must not let helpers in.
    private static void SecondSetupAfterTimeoutKeepsGateClosed()
    {
        PtySetupGate gate = new PtySetupGate(TimeSpan.FromMilliseconds(300));
        gate.BeginPlainHelper();
        ManualResetEventSlim first = StartWaiting(gate.BeginPtySetup);
        ManualResetEventSlim second = StartWaiting(gate.BeginPtySetup);
        Test.Check(first.Wait(TimeSpan.FromSeconds(5)) && second.Wait(TimeSpan.FromSeconds(5)), "both PTY setups go on after the timeout of the helper");
        gate.EndPtySetup();
        ManualResetEventSlim helper = StartWaiting(gate.BeginPlainHelper);
        Test.Check(!helper.Wait(Settle), "a helper waits while the second setup is still in progress");
        gate.EndPtySetup();
        Test.Check(helper.Wait(TimeSpan.FromSeconds(5)), "the helper starts when both setups are done");
        gate.EndPlainHelper();
        gate.EndPlainHelper();
    }

    // Short helpers that keep coming must not hold a PTY setup for the whole timeout.
    private static void HelperStreamDoesNotStarveSetup()
    {
        PtySetupGate gate = new PtySetupGate(TimeSpan.FromSeconds(30));
        bool stop = false;
        Thread[] streams = new Thread[4];
        for (int i = 0; i < streams.Length; i++)
        {
            streams[i] = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    gate.BeginPlainHelper();
                    Thread.Sleep(20);
                    gate.EndPlainHelper();
                }
            })
            {
                IsBackground = true
            };
            streams[i].Start();
        }
        Thread.Sleep(100);
        Stopwatch stopwatch = Stopwatch.StartNew();
        gate.BeginPtySetup();
        long waited = stopwatch.ElapsedMilliseconds;
        gate.EndPtySetup();
        Volatile.Write(ref stop, true);
        foreach (Thread stream in streams)
        {
            stream.Join(2000);
        }
        Test.Check(waited < 2000, "a stream of short helpers does not hold a PTY setup (" + waited + " ms)");
    }
}
