using System;
using System.Net;

namespace SmarTTY.Ssh;

public class SshKeyEncoding
{
    public static void AppendMpInt(ref byte[] buffer, byte[] value)
    {
        int length = value.Length;
        if ((value[0] & 0x80) != 0)
        {
            length++;
        }
        int index = buffer.Length;
        Array.Resize(ref buffer, buffer.Length + length + 4);
        BitConverter.GetBytes(IPAddress.HostToNetworkOrder(length)).CopyTo(buffer, index);
        value.CopyTo(buffer, buffer.Length - value.Length);
    }
}
