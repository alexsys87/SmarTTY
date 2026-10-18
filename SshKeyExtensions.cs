using SmarTTY.Ssh;
using System;

namespace SmarTTY;

public static class SshKeyExtensions
{
    public static string ToAuthorizedKeysLine(this ISshKeyPair sshKeyPair, string text)
    {
        return sshKeyPair.Algorithm + " " + Convert.ToBase64String(sshKeyPair.GetPublicKeyBlob()) + " " + text;
    }

    public static PublicKeyType ResolveAuto(this PublicKeyType requestedType, PublicKeyType fallbackType)
    {
        if (requestedType == PublicKeyType.Auto)
        {
            return fallbackType;
        }
        return requestedType;
    }
}
