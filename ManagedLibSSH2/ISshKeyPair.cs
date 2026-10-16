using System.IO;

namespace ManagedLibSSH2;

public interface ISshKeyPair
{
    string Algorithm { get; }

    string ContainerName { get; }

    ISshSigner[] Signers { get; }

    byte[] GetPublicKeyBlob();

    void ExportPrivateKey(FileStream fileStream);

    void ImportPrivateKey(FileStream fileStream);

    void ResetContainer();
}
