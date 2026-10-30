namespace SmarTTY.Ssh;

/// <summary>Where an <see cref="OpenSshKeyPair"/> keeps its private key.</summary>
public interface IPrivateKeyStore
{
    /// <summary>Where the key is kept, as shown to the user.</summary>
    string Location { get; }

    /// <summary>The stored key, or null when no key has been stored yet.</summary>
    byte[] Read();

    void Write(byte[] data);
}
