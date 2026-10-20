using SmarTTY.Transport;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using VisualTTY.DeviceTerminal;

// Runs the real AsyncTransportConnection against a fake transport: what happens to written data when a write fails, and
// what the person is told when the link breaks.
internal static class Program
{
    private static int _failures;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            Console.WriteLine("PASS: " + name);
            return;
        }
        _failures++;
        Console.WriteLine("FAIL: " + name);
    }

    /// <summary>A byte stream whose reads block until bytes are fed (or it is closed) and whose writes are scripted.</summary>
    private sealed class FakeStream : Stream
    {
        private readonly BlockingCollection<byte[]> _incoming = new BlockingCollection<byte[]>();
        public readonly List<string> Written = new List<string>();
        public Action<string> OnWrite = text => { };
        public Exception ReadFailure;

        public void Feed(string text) => _incoming.Add(Encoding.ASCII.GetBytes(text));

        public void Break(Exception failure)
        {
            ReadFailure = failure;
            _incoming.Add(null);
        }

        public void Shut()
        {
            _incoming.Add(null);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            byte[] chunk = _incoming.Take();
            if (chunk == null)
            {
                if (ReadFailure != null)
                {
                    throw ReadFailure;
                }
                return 0;
            }
            Array.Copy(chunk, 0, buffer, offset, chunk.Length);
            return chunk.Length;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            string text = Encoding.ASCII.GetString(buffer, offset, count);
            OnWrite(text);
            lock (Written)
            {
                Written.Add(text);
            }
        }

        public string[] WrittenCopy()
        {
            lock (Written)
            {
                return Written.ToArray();
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class FakeTransport : IDeviceTransport
    {
        public readonly FakeStream Stream = new FakeStream();
        public string ConnectingMessage => "Connecting...";
        public void Open(IHostNameResolver hostNameResolver) { }
        public void Close() => Stream.Shut();
        public Stream GetStream() => Stream;
        public string GetDisplayName(bool includeParameters) => "fake";
        public void Dispose() { }
    }

    private sealed class Session : IDisposable
    {
        public readonly FakeTransport Transport = new FakeTransport();
        public readonly AsyncTransportConnection Connection;
        public readonly List<string> Failures = new List<string>();
        public readonly List<(ConnectionStateEnum State, string Message)> States = new List<(ConnectionStateEnum, string)>();
        public readonly ManualResetEventSlim Connected = new ManualResetEventSlim();
        public readonly ManualResetEventSlim Disconnected = new ManualResetEventSlim();

        public Session()
        {
            Connection = new AsyncTransportConnection(Transport, new Dispatcher());
            Connection.WriteFailed += (sender, message) => { lock (Failures) Failures.Add(message); };
            Connection.StateChanged += (sender, state, message) =>
            {
                lock (States) States.Add((state, message));
                if (state == ConnectionStateEnum.Connected) Connected.Set();
                if (state == ConnectionStateEnum.Disconnected) Disconnected.Set();
            };
            Connection.Start();
            if (!Connected.Wait(3000)) throw new Exception("did not connect");
        }

        public string[] FailuresCopy() { lock (Failures) return Failures.ToArray(); }

        public void Dispose() => Connection.Dispose();
    }

    private static bool WaitFor(Func<bool> condition, int ms = 3000)
    {
        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine("FAIL: " + name + " -> " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static int Main()
    {
        Run("a write that goes through reaches the device and leaves no error", () =>
        {
            using (Session s = new Session())
            {
                s.Connection.Send(Encoding.ASCII.GetBytes("hello"), 5);
                Check(WaitFor(() => s.Transport.Stream.WrittenCopy().Length == 1), "data written");
                Check(s.Transport.Stream.WrittenCopy()[0] == "hello", "data intact");
                Check(s.Connection.WriteError == null && s.FailuresCopy().Length == 0, "no error reported");
            }
        });

        Run("a failed write is reported with its reason instead of vanishing", () =>
        {
            using (Session s = new Session())
            {
                s.Transport.Stream.OnWrite = text => throw new IOException("the device did not accept any data");
                s.Connection.Send(Encoding.ASCII.GetBytes("x"), 1);
                Check(WaitFor(() => s.FailuresCopy().Length == 1), "WriteFailed raised once");
                Check(s.FailuresCopy()[0].Contains("did not accept any data"), "the reason is passed on: " + s.FailuresCopy().FirstOrDefault());
                Check(s.Connection.WriteError != null && s.Connection.WriteError.Contains("did not accept"), "WriteError holds the reason");
            }
        });

        Run("after a failed write the queued data is thrown away, not sent late", () =>
        {
            using (Session s = new Session())
            {
                ManualResetEventSlim release = new ManualResetEventSlim();
                ManualResetEventSlim started = new ManualResetEventSlim();
                int calls = 0;
                s.Transport.Stream.OnWrite = text =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        started.Set();
                        release.Wait(3000);
                        throw new IOException("stalled");
                    }
                };
                s.Connection.Send(Encoding.ASCII.GetBytes("first"), 5);
                Check(started.Wait(3000), "first write is in progress");
                s.Connection.Send(Encoding.ASCII.GetBytes("second"), 6);
                s.Connection.Send(Encoding.ASCII.GetBytes("third"), 5);
                release.Set();
                Check(WaitFor(() => s.FailuresCopy().Length == 1), "failure reported");
                Check(s.FailuresCopy()[0].Contains("2 queued writes were discarded"), "the notice says what was dropped: " + s.FailuresCopy().FirstOrDefault());
                Thread.Sleep(200);
                Check(Volatile.Read(ref calls) == 1, "the discarded data never reached the device");
            }
        });

        Run("the error clears when the next write goes through", () =>
        {
            using (Session s = new Session())
            {
                bool fail = true;
                s.Transport.Stream.OnWrite = text =>
                {
                    if (fail) throw new IOException("no luck");
                };
                s.Connection.Send(Encoding.ASCII.GetBytes("a"), 1);
                Check(WaitFor(() => s.Connection.WriteError != null), "error recorded");
                fail = false;
                s.Connection.Send(Encoding.ASCII.GetBytes("b"), 1);
                Check(WaitFor(() => s.Connection.WriteError == null), "error cleared after a good write");
                Check(s.Transport.Stream.WrittenCopy().SequenceEqual(new[] { "b" }), "only the good write reached the device");
            }
        });

        Run("a broken link says why the connection ended", () =>
        {
            using (Session s = new Session())
            {
                s.Transport.Stream.Break(new IOException("Cannot read from COM3: access denied (the adapter may have been unplugged)"));
                Check(s.Disconnected.Wait(3000), "disconnected");
                (ConnectionStateEnum State, string Message) last = s.States.Last();
                Check(last.State == ConnectionStateEnum.Disconnected && last.Message != null && last.Message.Contains("unplugged"), "reason passed to the UI: " + last.Message);
            }
        });

        Run("closing the connection on purpose gives no reason and no write failure", () =>
        {
            Session s = new Session();
            s.Transport.Stream.OnWrite = text => throw new IOException("closed under the writer");
            s.Connection.Send(Encoding.ASCII.GetBytes("x"), 1);
            s.Connection.Dispose();
            Thread.Sleep(300);
            Check(s.FailuresCopy().Length <= 1, "at most the write that was already running");
            // The state callbacks are suppressed after Dispose, so nothing can claim the link broke.
            Check(!s.States.Any(t => t.State == ConnectionStateEnum.Disconnected && t.Message != null), "no disconnect reason after a deliberate close");
        });

        Run("received data still reaches the UI", () =>
        {
            using (Session s = new Session())
            {
                StringBuilder got = new StringBuilder();
                s.Connection.DataReceived += (sender, buffer, count) => { lock (got) got.Append(Encoding.ASCII.GetString(buffer, 0, count)); };
                s.Transport.Stream.Feed("ready>");
                Check(WaitFor(() => { lock (got) return got.ToString() == "ready>"; }), "data delivered");
            }
        });

        Console.WriteLine();
        if (_failures > 0)
        {
            Console.WriteLine(_failures + " FAILED");
            return 1;
        }
        Console.WriteLine("All transport checks passed.");
        return 0;
    }
}
