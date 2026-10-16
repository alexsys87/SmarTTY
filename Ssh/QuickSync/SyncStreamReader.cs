using System;
using System.Text;

namespace SmarTTY.Ssh.QuickSync;

internal class SyncStreamReader
{
    private readonly SshShellChannel _channel;

    private int _position;

    private int _count;

    private byte[] _buffer;

    public SyncStreamReader(SshShellChannel sshShellChannel, int value2 = 65536)
    {
        _buffer = new byte[value2];
        _channel = sshShellChannel;
    }

    public void ReadBytes(byte[] buffer, int value2)
    {
        int num = 0;
        while (true)
        {
            if (_position < _count && value2 > 0)
            {
                buffer[num++] = _buffer[_position++];
                value2--;
                continue;
            }
            if (value2 != 0)
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
        byte[] array = new byte[1];
        ReadBytes(array, 1);
        return (SyncRecordType)array[0];
    }

    public int ReadInt32()
    {
        byte[] value = new byte[4];
        ReadBytes(value, 4);
        return BitConverter.ToInt32(value, 0);
    }

    public long ReadInt64()
    {
        byte[] value = new byte[8];
        ReadBytes(value, 8);
        return BitConverter.ToInt64(value, 0);
    }

    public string ReadString()
    {
        byte[] array = new byte[ReadInt32()];
        ReadBytes(array, array.Length);
        return Encoding.UTF8.GetString(array);
    }
}
