using System;
using System.Net;

namespace SmarTTY.Ssh;

public class SshKeyEncoding
{
    public static void AppendMpInt(ref byte[] buffer, byte[] buffer2)
    {
        int num = buffer2.Length;
        if ((buffer2[0] & 0x80) != 0)
        {
            num++;
        }
        int index = buffer.Length;
        Array.Resize(ref buffer, buffer.Length + num + 4);
        BitConverter.GetBytes(IPAddress.HostToNetworkOrder(num)).CopyTo(buffer, index);
        buffer2.CopyTo(buffer, buffer.Length - buffer2.Length);
    }
}
