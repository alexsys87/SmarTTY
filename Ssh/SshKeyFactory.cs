using System;
using System.IO;

namespace SmarTTY.Ssh;

public class SshKeyFactory
{
    public readonly string containerPrefix;

    /// <summary>The folder of the DPAPI-protected files of the Ed25519 and ECDSA keys.</summary>
    public readonly string keyDirectory;

    public SshKeyFactory(string containerPrefix, string keyDirectory)
    {
        this.containerPrefix = containerPrefix;
        this.keyDirectory = keyDirectory;
    }

    public ISshKeyPair CreateKey(PublicKeyType publicKeyType, bool create = true)
    {
        if (publicKeyType == PublicKeyType.RSA)
        {
            return new RsaSshKey(containerPrefix + "rsa", 2048, create);
        }
        return new OpenSshKeyPair(GetAlgorithm(publicKeyType), new DpapiPrivateKeyFile(GetKeyFilePath(publicKeyType)), create);
    }

    /// <summary>The SSH name of the key type ("ssh-rsa", "ssh-ed25519", "ecdsa-sha2-nistp256"...).</summary>
    public static string GetAlgorithm(PublicKeyType publicKeyType)
    {
        return publicKeyType switch
        {
            PublicKeyType.RSA => "ssh-rsa",
            PublicKeyType.Ed25519 => "ssh-ed25519",
            PublicKeyType.ECDSA256 => "ecdsa-sha2-nistp256",
            PublicKeyType.ECDSA384 => "ecdsa-sha2-nistp384",
            PublicKeyType.ECDSA521 => "ecdsa-sha2-nistp521",
            _ => throw new ArgumentException("Invalid SSH key type: " + publicKeyType),
        };
    }

    /// <summary>
    /// The short name of the key type ("rsa", "ed25519", "ecdsa-nistp256"...), used for the key container, the key file
    /// and the settings archive.
    /// </summary>
    public static string GetKeyName(PublicKeyType publicKeyType)
    {
        string algorithm = GetAlgorithm(publicKeyType);
        return algorithm.StartsWith("ssh-", StringComparison.Ordinal) ? algorithm.Substring(4) : algorithm.Replace("-sha2", "");
    }

    /// <summary>The key type with the short name <paramref name="keyName"/>, or null for an unknown name.</summary>
    public static PublicKeyType? FromKeyName(string keyName)
    {
        foreach (PublicKeyType publicKeyType in Enum.GetValues(typeof(PublicKeyType)))
        {
            if (publicKeyType != PublicKeyType.Auto && GetKeyName(publicKeyType) == keyName)
            {
                return publicKeyType;
            }
        }
        return null;
    }

    /// <summary>The DPAPI-protected file of an Ed25519 or ECDSA key.</summary>
    public string GetKeyFilePath(PublicKeyType publicKeyType)
    {
        return Path.Combine(keyDirectory, containerPrefix + GetKeyName(publicKeyType) + ".key");
    }
}
