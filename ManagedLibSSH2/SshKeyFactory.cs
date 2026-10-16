using System;

namespace ManagedLibSSH2;

public class SshKeyFactory
{
    public readonly string containerPrefix;

    public SshKeyFactory(string containerPrefix)
    {
        this.containerPrefix = containerPrefix;
    }

    public ISshKeyPair CreateKey(PublicKeyType publicKeyType, bool create = true)
    {
        return publicKeyType switch
        {
            PublicKeyType.RSA => new RsaSshKey(containerPrefix + "rsa", 2048, create),
            _ => throw new ArgumentException("Invalid SSH key type: " + publicKeyType),
        };
    }
}
