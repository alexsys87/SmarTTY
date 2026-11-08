using SmarTTY.Ssh;
using System;
using System.Security.Cryptography;
using System.Text;

namespace VisualTTY;

public class PasswordProtector
{
    public static bool BytesEqual(byte[] first, byte[] second)
    {
        if (first.Length != second.Length)
        {
            return false;
        }
        for (int i = 0; i < second.Length; i++)
        {
            if (first[i] != second[i])
            {
                return false;
            }
        }
        return true;
    }

    public static byte[] Encrypt(string password, string containerName)
    {
        if (password == null)
        {
            return null;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(password);
        if (bytes.Length > 200)
        {
            throw new Exception("Maximum saved password length is 200 bytes");
        }
        return (RsaSshKey.OpenContainer(containerName, 2048) ?? throw new Exception("RSA key container is damaged or unreadable.")).Encrypt(bytes, fOAEP: false);
    }

    public static string Decrypt(byte[] encryptedBytes, string containerName)
    {
        if (encryptedBytes == null)
        {
            return null;
        }
        // Only an existing container: a missing one (a new Windows profile) must not be created here, because the key that this would make
        // is not the one that the saved passwords were encrypted with, and it would stay for the next Encrypt, silently ending the
        // chance of ever reading the old ones.
        using (RSACryptoServiceProvider rsaProvider = RsaSshKey.OpenContainer(containerName, 2048, create: false)
            ?? throw new CryptographicException("The key container of the saved passwords does not exist on this computer."))
        {
            return Encoding.UTF8.GetString(rsaProvider.Decrypt(encryptedBytes, fOAEP: false));
        }
    }
}
