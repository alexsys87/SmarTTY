using System;

namespace ManagedLibSSH2;

public class SshKeyFactory
{
    public readonly string containerPrefix;

    public SshKeyFactory(string text)
    {
        containerPrefix = text;
    }

    public ISshKeyPair CreateKey(PublicKeyType publicKeyType, bool flag = true)
    {
        return publicKeyType switch
        {
            PublicKeyType.DSA => (ISshKeyPair)new DsaSshKey(this.containerPrefix + "dsa", flag),
            PublicKeyType.RSA => new RsaSshKey(this.containerPrefix + "rsa", 2048, flag),
            _ => throw new ArgumentException("Invalid SSH key type: " + publicKeyType),
        };
    }
}
