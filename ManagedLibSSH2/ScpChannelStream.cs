using System;
using System.IO;
using System.Threading;

namespace ManagedLibSSH2;

public class ScpChannelStream : Stream
{
    protected LibSsh2Session _session;

    protected IntPtr _channelHandle;

    private long _length;

    private long _bytesTransferred;

    private bool _canRead;

    private bool _closed;

    public object Tag;

    public override bool CanRead => _canRead;

    public override bool CanSeek => false;

    public override bool CanWrite => !_canRead;

    public override long Length => _length;

    public override long Position
    {
        get
        {
            throw new NotSupportedException();
        }
        set
        {
            throw new NotSupportedException();
        }
    }

    public bool AbortOnShortRead { get; set; }

    public ScpChannelStream(LibSsh2Session libSsh2Session, IntPtr handle, bool flag, long value2)
    {
        _session = libSsh2Session;
        this._channelHandle = handle;
        this._canRead = flag;
        this._length = value2;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value2)
    {
        throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!_canRead)
        {
            throw new NotSupportedException();
        }
        if (count > this._length - _bytesTransferred)
        {
            count = (int)(this._length - _bytesTransferred);
        }
        int num;
        int num2;
        for (num = 0; num < count; num += num2)
        {
            num2 = _session.ReadChannel(this._channelHandle, 0, buffer, offset + num, count - num);
            if (num2 > 0)
            {
                Interlocked.Add(ref _bytesTransferred, num2);
            }
            if (num2 <= 0 || AbortOnShortRead)
            {
                if (num != 0)
                {
                    break;
                }
                return num2;
            }
        }
        return num;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_canRead)
        {
            throw new NotSupportedException();
        }
        _session.WriteChannelFully(this._channelHandle, 0, buffer, offset, count);
        Interlocked.Add(ref _bytesTransferred, count);
    }

    private void CloseChannel()
    {
        if (!_closed)
        {
            _closed = true;
            _session.ExecuteBlocking(() => LibSsh2.ChannelClose(_channelHandle));
        }
        if (_channelHandle != IntPtr.Zero)
        {
            _session.FreeChannel(ref _channelHandle);
        }
    }

    public override void Close()
    {
        base.Close();
        CloseChannel();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        CloseChannel();
    }
}
