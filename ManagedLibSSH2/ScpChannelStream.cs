using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ManagedLibSSH2;

/// <summary>
/// A file transfer using the SCP protocol over an exec channel ("scp -t" / "scp -f" on the remote side).
/// This class is the upload side; <see cref="ScpDownloadStream"/> implements downloads.
/// </summary>
public class ScpChannelStream : Stream
{
    protected readonly SshShellChannel _channel;

    private readonly bool _canRead;

    private readonly long _length;

    protected long _bytesTransferred;

    private bool _closed;

    public object Tag;

    public override bool CanRead => _canRead;

    public override bool CanSeek => false;

    public override bool CanWrite => !_canRead;

    public override long Length => _length;

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// If set, <see cref="Read"/> returns as soon as some data is available instead of filling the whole buffer.
    /// </summary>
    public bool AbortOnShortRead { get; set; }

    internal ScpChannelStream(SshShellChannel channel, bool canRead, long length)
    {
        _channel = channel;
        _canRead = canRead;
        _length = length;
    }

    /// <summary>
    /// Starts uploading a file of <paramref name="length"/> bytes; the caller then writes exactly that many bytes and closes the stream.
    /// </summary>
    internal static ScpChannelStream OpenUpload(SshSession session, string path, long length, DateTime modifyTime, int mode)
    {
        SshShellChannel channel = session.OpenShellChannel("scp -p -t " + QuotePath(path));
        try
        {
            ReadAck(channel);
            long time = (long)(modifyTime.ToUniversalTime() - SshSession.UnixEpoch).TotalSeconds;
            SendLine(channel, $"T{time} 0 {time} 0");
            ReadAck(channel);
            SendLine(channel, "C" + Convert.ToString(mode & 0xFFF, 8).PadLeft(4, '0') + " " + length + " " + GetFileName(path));
            ReadAck(channel);
            return new ScpChannelStream(channel, canRead: false, length);
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_canRead)
        {
            throw new NotSupportedException();
        }
        if (_bytesTransferred + count > _length)
        {
            throw new IOException("Attempted to upload more data than announced");
        }
        _channel.Input.Write(buffer, offset, count);
        _bytesTransferred += count;
    }

    /// <summary>
    /// Completes the protocol exchange after the file data (upload: end marker and the final acknowledgement).
    /// </summary>
    protected virtual void FinishTransfer()
    {
        if (_bytesTransferred != _length)
        {
            return;
        }
        _channel.Input.Write(new byte[1], 0, 1);
        ReadAck(_channel);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_closed)
        {
            _closed = true;
            try
            {
                FinishTransfer();
                _channel.SendEof();
            }
            finally
            {
                _channel.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    #region SCP protocol helpers

    internal static string QuotePath(string path)
    {
        return "'" + path.Replace("'", "'\\''") + "'";
    }

    private static string GetFileName(string path)
    {
        string name = path.Substring(path.LastIndexOf('/') + 1);
        return (name.Length > 0) ? name : path;
    }

    internal static void SendLine(SshShellChannel channel, string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        channel.Input.Write(bytes, 0, bytes.Length);
    }

    internal static void SendAck(SshShellChannel channel)
    {
        channel.Input.Write(new byte[1], 0, 1);
    }

    internal static int ReadByte(SshShellChannel channel)
    {
        byte[] buffer = new byte[1];
        if (channel.Output.Read(buffer, 0, 1) != 1)
        {
            return -1;
        }
        return buffer[0];
    }

    internal static string ReadLine(SshShellChannel channel)
    {
        List<byte> bytes = new List<byte>();
        int value;
        while ((value = ReadByte(channel)) != -1 && value != '\n')
        {
            bytes.Add((byte)value);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// Reads an acknowledgement: 0 means success, 1 (warning) or 2 (fatal) is followed by an error message.
    /// </summary>
    internal static void ReadAck(SshShellChannel channel)
    {
        int status = ReadByte(channel);
        switch (status)
        {
            case 0:
                return;
            case 1:
            case 2:
                throw new RemoteOperationException(ReadLine(channel).Trim());
            case -1:
                throw new IOException("The remote scp command exited unexpectedly. Please check that scp is installed on the target.");
            default:
                throw new IOException("Unexpected SCP reply: " + ((char)status + ReadLine(channel)).Trim());
        }
    }

    #endregion
}
