using System;
using System.Text;

namespace SmarTTY.Ssh.QuickSync;

internal class SyncStreamReader
{
    private readonly SshShellChannel _channel;

    private int _position;

    private int _count;

    private byte[] _buffer;

    public SyncStreamReader(SshShellChannel sshShellChannel, int bufferSize = 65536)
    {
        _buffer = new byte[bufferSize];
        _channel = sshShellChannel;
    }

    public void ReadBytes(byte[] buffer, int length)
    {
        int destOffset = 0;
        while (true)
        {
            if (_position < _count && length > 0)
            {
                buffer[destOffset++] = _buffer[_position++];
                length--;
                continue;
            }
            if (length != 0)
            {
                _position = 0;
                _count = _channel.Output.Read(_buffer, 0, _buffer.Length);
                if (_count <= 0)
                {
                    throw new RemoteFileSet.RemoteFileSetReader.RemoteFileSetFormatException("SysprogsSync stream ended unexpectedly");
                }
                continue;
            }
            break;
        }
    }

    public SyncRecordType ReadRecordType()
    {
        byte[] bytes = new byte[1];
        ReadBytes(bytes, 1);
        return (SyncRecordType)bytes[0];
    }

    public int ReadInt32()
    {
        byte[] bytes = new byte[4];
        ReadBytes(bytes, 4);
        return BitConverter.ToInt32(bytes, 0);
    }

    public long ReadInt64()
    {
        byte[] bytes = new byte[8];
        ReadBytes(bytes, 8);
        return BitConverter.ToInt64(bytes, 0);
    }

    public string ReadString()
    {
        byte[] bytes = new byte[ReadInt32()];
        ReadBytes(bytes, bytes.Length);
        return Encoding.UTF8.GetString(bytes);
    }
}
