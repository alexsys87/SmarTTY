using System.IO;
using System.Net.Sockets;

namespace SmarTTY.Utilities;

public static class StreamSocketExtensions
{
    public static int ReadFully(this Stream stream, byte[] buffer, int value2)
    {
        int num;
        int num2;
        for (num = 0; num < value2; num += num2)
        {
            num2 = stream.Read(buffer, num, value2 - num);
            if (num2 <= 0)
            {
                return num;
            }
        }
        return num;
    }

    public static void WriteAll(this Stream stream, byte[] buffer)
    {
        stream.Write(buffer, 0, buffer.Length);
    }

    public static int ReceiveFully(this Socket socket, byte[] buffer, int value2, int value3)
    {
        int num;
        int num2;
        for (num = 0; num < value3; num += num2)
        {
            num2 = socket.Receive(buffer, value2 + num, value3 - num, SocketFlags.None);
            if (num2 <= 0)
            {
                return num;
            }
        }
        return num;
    }

    public static int SendFully(this Socket socket, byte[] buffer, int value2, int value3)
    {
        int num;
        int num2;
        for (num = 0; num < value3; num += num2)
        {
            num2 = socket.Send(buffer, value2 + num, value3 - num, SocketFlags.None);
            if (num2 <= 0)
            {
                return num;
            }
        }
        return num;
    }

    public static int ReceiveExactly(this Socket socket, byte[] buffer)
    {
        return socket.ReceiveFully(buffer, 0, buffer.Length);
    }
}
