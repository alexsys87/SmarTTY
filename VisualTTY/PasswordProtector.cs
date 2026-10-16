using SmarTTY.Ssh;
using System;
using System.Security.Cryptography;
using System.Text;

namespace VisualTTY;

public class PasswordProtector
{
    public static bool BytesEqual(byte[] buffer, byte[] buffer2)
    {
        if (buffer.Length != buffer2.Length)
        {
            return false;
        }
        for (int num = 0; num < buffer2.Length; num++)
        {
            if (buffer[num] != buffer2[num])
            {
                return false;
            }
        }
        return true;
    }

    public static byte[] Encrypt(string text, string text2)
    {
        if (text == null)
        {
            return null;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 200)
        {
            throw new Exception("Maximum saved password length is 200 bytes");
        }
        return (RsaSshKey.OpenContainer(text2, 2048) ?? throw new Exception("RSA key container is damaged or unreadable.")).Encrypt(bytes, fOAEP: false);
    }

    public static string Decrypt(byte[] buffer, string text)
    {
        if (buffer == null)
        {
            return null;
        }
        RSACryptoServiceProvider rSACryptoServiceProvider = RsaSshKey.OpenContainer(text, 2048);
        return Encoding.UTF8.GetString(rSACryptoServiceProvider.Decrypt(buffer, fOAEP: false));
    }
}
