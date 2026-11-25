using System.Text;

namespace SmarTTY.IO;

// Stands for the protector of the drafts (IO/DpapiDraftProtector.cs, left out of the build of the stand): the Windows data protection
// (DPAPI) exists only on Windows, and the stand runs on Linux. It turns the bytes over with a key, so that a draft is still not stored as
// its plain text and is read back only through the protector, as with DPAPI.
public sealed class DpapiDraftProtector : IDraftProtector
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("SmarTTY editor draft");

    public byte[] Protect(byte[] data)
    {
        return Turn(data);
    }

    public byte[] Unprotect(byte[] data)
    {
        return Turn(data);
    }

    private static byte[] Turn(byte[] data)
    {
        byte[] result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ Key[i % Key.Length]);
        }
        return result;
    }
}
