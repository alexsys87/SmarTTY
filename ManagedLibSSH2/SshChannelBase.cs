using System;
using System.IO;

namespace ManagedLibSSH2;

public class SshChannelBase : IDisposable
{
    public class ChannelStream : Stream
    {
        private int _streamId;

        private bool _canWrite;

        private SshChannelBase _channel;

        public bool ReadFully;

        public override bool CanRead => !_canWrite;

        public override bool CanSeek => false;

        public override bool CanWrite => _canWrite;

        public override long Length
        {
            get
            {
                throw new NotSupportedException();
            }
        }

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

        public ChannelStream(SshChannelBase sshChannelBase, int value2, bool flag)
        {
            this._channel = sshChannelBase;
            _streamId = value2;
            this._canWrite = flag;
        }

        public override void Flush()
        {
            _channel.Flush(_streamId);
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
            if (ReadFully)
            {
                int num;
                int num2;
                for (num = 0; num < count; num += num2)
                {
                    num2 = this._channel.ReadStream(_streamId, buffer, offset + num, count - num);
                    if (num2 <= 0)
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
            return this._channel.ReadStream(_streamId, buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            this._channel._session.WriteChannelFully(this._channel._channelHandle, _streamId, buffer, offset, count);
        }
    }

    protected internal IntPtr _channelHandle;

    protected bool _closed;

    protected readonly LibSsh2Session _session;

    public LibSsh2Session Session => _session;

    public bool IsEof
    {
        get
        {
            int num = _session.ExecuteBlocking(() => LibSsh2.ChannelEof(_channelHandle));
            if (num < 0)
            {
                throw new LibSsh2Exception(num);
            }
            return num != 0;
        }
    }

    protected SshChannelBase(LibSsh2Session libSsh2Session, IntPtr handle)
    {
        _session = libSsh2Session;
        _channelHandle = handle;
    }

    public void Flush(int value2)
    {
        int num = _session.ExecuteBlocking(() => LibSsh2.ChannelFlushEx(_channelHandle, value2));
        if (num < 0)
        {
            throw new LibSsh2Exception(num);
        }
    }

    public void SendEof()
    {
        int num = _session.ExecuteBlocking(() => LibSsh2.ChannelSendEof(_channelHandle));
        if (num != 0)
        {
            throw new LibSsh2Exception(num);
        }
    }

    public void WaitClosed()
    {
        int num = _session.ExecuteBlocking(() => LibSsh2.ChannelWaitClosed(_channelHandle));
        if (num != 0)
        {
            throw new LibSsh2Exception(num);
        }
    }

    public bool TryWaitClosed()
    {
        if (_session.ExecuteNonBlocking(() => LibSsh2.ChannelWaitClosed(_channelHandle)) > 0)
        {
            return true;
        }
        return false;
    }

    public void WaitEof()
    {
        int num = _session.ExecuteBlocking(() => LibSsh2.ChannelWaitEof(_channelHandle));
        if (num != 0)
        {
            throw new LibSsh2Exception(num);
        }
    }

    public virtual void Dispose()
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

    protected virtual int ReadStream(int value2, byte[] buffer, int value3, int value4)
    {
        return _session.ReadChannel(_channelHandle, value2, buffer, value3, value4);
    }
}
