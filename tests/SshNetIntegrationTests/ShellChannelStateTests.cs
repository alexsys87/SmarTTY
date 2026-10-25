using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Checks the channel state rules that a live server cannot produce on demand: a channel closed without an exit
/// status must not report success, and a chunk delivered between EOF and the close must not be dropped.
/// </summary>
internal static class ShellChannelStateTests
{
    private static readonly Type ChannelType = typeof(SshShellChannel);

    public static void Run()
    {
        Test.Section("Shell channel state without a live server");
        MissingExitStatusIsNotSuccess();
        DataDeliveredAfterEofIsNotDropped();
    }

    private static void MissingExitStatusIsNotSuccess()
    {
        SshShellChannel closed = NewChannel();
        SetField(closed, "_eofReceived", true);
        SetField(closed, "_closed", true);
        Test.Check(!closed.HasExitStatus, "a channel closed without a status reports no status");
        Test.Equal(-1, closed.ExitStatus, "a channel closed without an exit status does not report success");

        SetField(closed, "_exitStatus", (int?)0);
        Test.Check(closed.HasExitStatus, "a reported status of 0 is a known status");
        Test.Equal(0, closed.ExitStatus, "a reported status of 0 still reads as success");

        SetField(closed, "_exitStatus", (int?)7);
        Test.Equal(7, closed.ExitStatus, "a reported non-zero status is kept");

        SshShellChannel signaled = NewChannel();
        SetField(signaled, "_eofReceived", true);
        SetField(signaled, "_closed", true);
        SetField(signaled, "_exitSignal", "TERM");
        Test.Check(!signaled.HasExitStatus, "a signal is not an exit status");
        Test.Equal("TERM", signaled.ExitSignal, "the signal is reported");
        Test.Equal(-1, signaled.ExitStatus, "a signal still reads as -1");

        SshShellChannel running = NewChannel();
        Test.Check(!running.HasExitStatus, "a running channel has no exit status");
        Test.Equal(-1, running.ExitStatus, "a running channel does not report success either");
    }

    private static void DataDeliveredAfterEofIsNotDropped()
    {
        SshShellChannel channel = NewChannelWithQueues(out object queue, out object sync);
        SetField(channel, "_eofReceived", true);
        MethodInfo readStream = ChannelType.GetMethod("ReadStream", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo add = queue.GetType().GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
        byte[] buffer = new byte[8];
        int read = -2;
        Exception failure = null;
        ManualResetEvent reading = new ManualResetEvent(false);
        Thread reader = new Thread(() =>
        {
            try
            {
                reading.Set();
                read = (int)readStream.Invoke(channel, new object[] { 0, buffer, 0, 4 });
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true
        };
        reader.Start();
        reading.WaitOne();
        // Let the reader reach the wait after EOF, then deliver the chunk that the old code dropped.
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (reader.IsAlive && (reader.ThreadState & ThreadState.WaitSleepJoin) == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }
        lock (sync)
        {
            add.Invoke(queue, new object[] { Encoding.ASCII.GetBytes("late") });
            Monitor.PulseAll(sync);
        }
        Test.Check(reader.Join(TimeSpan.FromSeconds(5)), "the reader finishes after the late chunk");
        Test.Check(failure == null, "reading after EOF does not fail" + (failure == null ? "" : ": " + failure.Message));
        Test.Equal(4, read, "a chunk delivered between EOF and the close is returned");
        Test.Equal("late", Encoding.ASCII.GetString(buffer, 0, 4), "the delivered chunk is intact");
        lock (sync)
        {
            SetField(channel, "_closed", true);
            Monitor.PulseAll(sync);
        }
    }

    private static SshShellChannel NewChannel()
    {
        SshShellChannel channel = (SshShellChannel)RuntimeHelpers.GetUninitializedObject(ChannelType);
        SetField(channel, "_sync", new object());
        return channel;
    }

    private static SshShellChannel NewChannelWithQueues(out object queue, out object sync)
    {
        SshShellChannel channel = (SshShellChannel)RuntimeHelpers.GetUninitializedObject(ChannelType);
        sync = new object();
        Type queueType = ChannelType.GetNestedType("ChunkQueue", BindingFlags.NonPublic);
        if (queueType == null)
        {
            throw new InvalidOperationException("SshShellChannel.ChunkQueue was not found");
        }
        Array queues = Array.CreateInstance(queueType, 2);
        for (int i = 0; i < queues.Length; i++)
        {
            queues.SetValue(Activator.CreateInstance(queueType, nonPublic: true), i);
        }
        SetField(channel, "_sync", sync);
        SetField(channel, "_queues", queues);
        queue = queues.GetValue(0);
        return channel;
    }

    private static void SetField(object target, string name, object value)
    {
        FieldInfo field = ChannelType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            throw new InvalidOperationException("SshShellChannel." + name + " was not found");
        }
        field.SetValue(target, value);
    }
}
