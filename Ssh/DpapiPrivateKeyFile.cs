using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SmarTTY.Ssh;

/// <summary>
/// A private key in a file protected with the Windows data protection (DPAPI) of the current user, the same protection
/// as the CSP key container of the RSA key: only this Windows user can read the key, not other accounts, backups or a
/// copied folder.
/// </summary>
public sealed class DpapiPrivateKeyFile : IPrivateKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SmarTTY SSH key");

    private readonly string _path;

    public DpapiPrivateKeyFile(string path)
    {
        _path = path;
    }

    public string Location => _path;

    public byte[] Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }
        byte[] data = File.ReadAllBytes(_path);
        try
        {
            return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new IOException("The SmarTTY key " + _path + " cannot be decrypted (it belongs to another Windows user or computer, or it is damaged): " + exception.Message + " Delete the file to let SmarTTY create a new key.", exception);
        }
    }

    public void Write(byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path));
        byte[] protectedData = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        // Written beside the key and moved over it, so that a failure halfway does not destroy the old key.
        string temp = _path + ".tmp";
        File.WriteAllBytes(temp, protectedData);
        if (File.Exists(_path))
        {
            File.Replace(temp, _path, null);
        }
        else
        {
            File.Move(temp, _path);
        }
    }
}
