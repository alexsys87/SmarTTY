using ManagedLibSSH2;
using System;

namespace SmarTTY;

public static class SshKeyExtensions
{
    public static string ToAuthorizedKeysLine(this ISshKeyPair sshKeyPair, string text)
    {
        return sshKeyPair.Algorithm + " " + Convert.ToBase64String(sshKeyPair.GetPublicKeyBlob()) + " " + text;
    }

    public static PublicKeyType ResolveAuto(this PublicKeyType publicKeyType, PublicKeyType publicKeyType2)
    {
        if (publicKeyType == PublicKeyType.Auto)
        {
            return publicKeyType2;
        }
        return publicKeyType;
    }
}
