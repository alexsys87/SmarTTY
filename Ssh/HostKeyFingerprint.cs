using System;
using System.Security.Cryptography;
using System.Text;

namespace SmarTTY.Ssh;

/// <summary>
/// The fingerprints of a server host key in the two forms users compare them in: "SHA256:..." as OpenSSH prints it
/// (ssh, ssh-keygen -l) and the older "MD5:aa:bb:..." that routers and old servers still show.
/// </summary>
public static class HostKeyFingerprint
{
    /// <summary>"SHA256:" and the unpadded base64 of the SHA-256 of the public key blob, exactly as OpenSSH writes it.</summary>
    public static string Sha256(byte[] keyBlob)
    {
        using (SHA256 hash = SHA256.Create())
        {
            return "SHA256:" + Convert.ToBase64String(hash.ComputeHash(keyBlob)).TrimEnd('=');
        }
    }

    /// <summary>"MD5:" and the MD5 of the public key blob as colon-separated hex bytes (ssh-keygen -l -E md5).</summary>
    public static string Md5(byte[] keyBlob)
    {
        using (MD5 hash = MD5.Create())
        {
            StringBuilder text = new StringBuilder("MD5:");
            foreach (byte value in hash.ComputeHash(keyBlob))
            {
                if (text.Length > 4)
                {
                    text.Append(':');
                }
                text.Append(value.ToString("x2"));
            }
            return text.ToString();
        }
    }

    /// <summary>Both fingerprints, one per line, SHA-256 first; null when there is no key.</summary>
    public static string Describe(byte[] keyBlob)
    {
        if (keyBlob == null || keyBlob.Length == 0)
        {
            return null;
        }
        return Sha256(keyBlob) + Environment.NewLine + Md5(keyBlob);
    }
}
