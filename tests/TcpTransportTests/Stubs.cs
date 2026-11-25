using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

// Stand-ins for the Windows-only types the transport classes mention (and the thread of the windows they post to), so the real classes can run on any machine.
namespace System.IO.Ports
{
    public enum Parity { None, Odd, Even, Mark, Space }

    public enum StopBits { None, One, Two, OnePointFive }

    public enum Handshake { None, XOnXOff, RequestToSend, RequestToSendXOnXOff }
}

namespace SmarTTY.Tests
{
    /// <summary>
    /// Runs posted callbacks one after another on a thread of its own, like the UI thread of the application, and
    /// remembers how long its queue ever got (what a flood of received data does to the UI).
    /// </summary>
    public class FakeUiThread : SmarTTY.Utilities.IUiThread, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private int _maxQueueLength;

        public int MaxQueueLength => Volatile.Read(ref _maxQueueLength);

        public FakeUiThread()
        {
            _thread = new Thread(() =>
            {
                foreach (Action callback in _queue.GetConsumingEnumerable())
                {
                    callback();
                }
            }) { IsBackground = true, Name = "Fake UI thread" };
            _thread.Start();
        }

        public bool IsCurrent => Thread.CurrentThread == _thread;

        public void Send(Action action) => throw new NotSupportedException("the connection only posts");

        public void Post(Action method)
        {
            _queue.Add(method);
            int length = _queue.Count;
            int max;
            while (length > (max = Volatile.Read(ref _maxQueueLength)) && Interlocked.CompareExchange(ref _maxQueueLength, length, max) != max)
            {
            }
        }

        public void Dispose() => _queue.CompleteAdding();
    }
}

namespace SmarTTY.Native
{
    public class SerialPortStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }
}

namespace SmarTTY.Transport
{
    // Only its type matters: AsyncTransportConnection asks "is the transport a SerialTransport".
    internal class SerialTransport : IDeviceTransport
    {
        public SmarTTY.Native.SerialPortStream SerialStream => null;
        public string PortName => "COM1";
        public string ConnectingMessage => "";
        public void Open(IHostNameResolver hostNameResolver) { }
        public void Close() { }
        public Stream GetStream() => null;
        public string GetDisplayName(bool includeParameters) => "COM1";
        public void Dispose() { }
    }
}
