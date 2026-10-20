using System;
using System.Globalization;
using System.IO;

namespace SmarTTY.Ssh;

/// <summary>
/// Downloads a single file via "scp -p -f"; <see cref="Length"/> and the timestamps are known once the stream is open.
/// </summary>
public class ScpDownloadStream : ScpChannelStream
{
    /// <summary>
    /// Not transferred by SCP; always <see cref="SshSession.UnixEpoch"/>.
    /// </summary>
    public readonly DateTime ChangeTime;

    public readonly DateTime ModifyTime;

    public readonly DateTime AccessTime;

    private bool _endReceived;

    private ScpDownloadStream(SshShellChannel channel, long length, DateTime modifyTime, DateTime accessTime)
        : base(channel, canRead: true, length)
    {
        ChangeTime = SshSession.UnixEpoch;
        ModifyTime = modifyTime;
        AccessTime = accessTime;
    }

    internal static ScpDownloadStream Open(SshSession session, string path)
    {
        SshShellChannel channel = session.OpenShellChannel("scp -p -f " + QuotePath(path));
        try
        {
            DateTime modifyTime = SshSession.UnixEpoch;
            DateTime accessTime = SshSession.UnixEpoch;
            SendAck(channel);
            while (true)
            {
                int type = ReadByte(channel);
                if (type == -1)
                {
                    throw new IOException("The remote scp command exited unexpectedly. Please check that scp is installed on the target.");
                }
                string line = ReadLine(channel);
                string[] fields = line.Split(' ');
                switch (type)
                {
                    case 1:
                    case 2:
                        throw new RemoteOperationException(line.Trim());
                    case 'T':
                        // T<mtime> 0 <atime> 0
                        modifyTime = SshSession.UnixEpoch.AddSeconds(long.Parse(fields[0], CultureInfo.InvariantCulture));
                        accessTime = SshSession.UnixEpoch.AddSeconds(long.Parse(fields[2], CultureInfo.InvariantCulture));
                        SendAck(channel);
                        break;
                    case 'C':
                        // C<mode> <size> <name>
                        SendAck(channel);
                        return new ScpDownloadStream(channel, long.Parse(fields[1], CultureInfo.InvariantCulture), modifyTime, accessTime);
                    case 'D':
                        throw new RemoteOperationException(path + " is a directory");
                    default:
                        throw new IOException("Unexpected SCP reply: " + ((char)type + line).Trim());
                }
            }
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long remaining = Length - _bytesTransferred;
        if (count > remaining)
        {
            count = (int)remaining;
        }
        int done = 0;
        while (done < count)
        {
            int read = _channel.Output.Read(buffer, offset + done, count - done);
            if (read <= 0)
            {
                break;
            }
            done += read;
            if (AbortOnShortRead)
            {
                break;
            }
        }
        if (done == 0 && count > 0)
        {
            // The channel ended before all the announced bytes arrived. Reporting that as a normal end of the file
            // would make a truncated download look complete.
            throw new IOException("The SCP transfer was interrupted: " + _bytesTransferred + " of " + Length + " bytes were received.");
        }
        _bytesTransferred += done;
        return done;
    }

    protected override void FinishTransfer()
    {
        if (_bytesTransferred != Length || _endReceived)
        {
            return;
        }
        // After the data the source sends its status byte and waits for our acknowledgement.
        _endReceived = true;
        ReadAck(_channel);
        SendAck(_channel);
    }
}
