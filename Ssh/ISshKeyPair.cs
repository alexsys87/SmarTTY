using Renci.SshNet;
using System.IO;

namespace SmarTTY.Ssh;

public interface ISshKeyPair
{
    string Algorithm { get; }

    string ContainerName { get; }

    byte[] GetPublicKeyBlob();

    /// <summary>
    /// Creates the private key in the form used by SSH.NET for public key authentication
    /// (dispose it after use if it implements IDisposable).
    /// </summary>
    IPrivateKeySource CreatePrivateKeySource();

    void ExportPrivateKey(FileStream fileStream);

    void ImportPrivateKey(FileStream fileStream);

    void ResetContainer();
}
