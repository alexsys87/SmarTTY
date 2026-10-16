using System;
using System.Security.Cryptography;

namespace ManagedLibSSH2;

public class CryptoContainerCorruptException : Exception
{
    public CryptoContainerCorruptException(CryptographicException cryptographicException)
        : base("Cryptographic container is corrupt (" + cryptographicException.Message + "). Consider resetting containers by emptying %APPDATA%\\Microsoft\\Crypto.", cryptographicException)
    {
    }
}
