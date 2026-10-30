using SmarTTY.Utilities;

namespace SmarTTY.Ssh;

/// <summary>
/// The type of the key pair SmarTTY generates for public key authentication. The names are stored in the connection
/// profiles, so existing ones must not be renamed; <see cref="Auto"/> means RSA, the only type of earlier versions.
/// </summary>
public enum PublicKeyType
{
    Auto,
    RSA,
    Ed25519,
    [EnumDisplayText("ECDSA P-256")]
    ECDSA256,
    [EnumDisplayText("ECDSA P-384")]
    ECDSA384,
    [EnumDisplayText("ECDSA P-521")]
    ECDSA521
}
