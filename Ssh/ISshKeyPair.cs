using Renci.SshNet;
using System.IO;

namespace SmarTTY.Ssh;

public interface ISshKeyPair
{
    string Algorithm { get; }

    /// <summary>Where the private key is kept: a CSP key container name or a file path.</summary>
    string ContainerName { get; }

    /// <summary>The file dialog filter and the suggested file name for <see cref="ExportPrivateKey"/>.</summary>
    string ExportFileFilter { get; }

    string DefaultKeyFileName { get; }

    /// <summary>The file dialog filter for <see cref="ImportPrivateKey"/>.</summary>
    string ImportFileFilter { get; }

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
