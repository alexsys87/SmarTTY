using System;
using System.IO;

// Stand-ins for the Windows-only types AsyncTransportConnection mentions (and the thread of the windows it posts to), so the real class can run on any machine.
namespace SmarTTY.Tests
{
    /// <summary>Runs posted callbacks right away on the posting thread.</summary>
    public class FakeUiThread : SmarTTY.Utilities.IUiThread
    {
        public bool IsCurrent => true;

        public void Post(Action action) => action();

        public void Send(Action action) => action();
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
