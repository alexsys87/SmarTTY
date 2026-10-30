using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SmarTTY.Ssh;

/// <summary>
/// An Ed25519 or ECDSA key pair of SmarTTY. The key is kept as an OpenSSH private key without a passphrase (see
/// <see cref="OpenSshPrivateKey"/>) in an <see cref="IPrivateKeyStore"/>; SSH.NET loads it from that text for
/// authentication. The key is exported as an OpenSSH private key too, so it can be used with ssh, and an existing key
/// (id_ed25519, id_ecdsa, a PuTTY .ppk) can be imported.
/// </summary>
public class OpenSshKeyPair : ISshKeyPair
{
    // Key files are a few hundred bytes; anything bigger is not a key.
    private const long MaxKeyFileBytes = 64 * 1024;

    private readonly IPrivateKeyStore _store;

    private string _privateKey;

    private byte[] _publicKeyBlob;

    public string Algorithm { get; }

    public string ContainerName => _store.Location;

    public string ExportFileFilter => "OpenSSH private keys (id_*)|id_*|All files (*.*)|*.*";

    public string DefaultKeyFileName => Algorithm == OpenSshPrivateKey.Ed25519Algorithm ? "id_ed25519" : "id_ecdsa";

    public string ImportFileFilter => "Private keys (id_*, *.ppk)|id_*;*.ppk|All files (*.*)|*.*";

    /// <summary>
    /// Opens the key of type <paramref name="algorithm"/> ("ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384"
    /// or "ecdsa-sha2-nistp521"). A new key is generated and stored when there is none yet and <paramref name="create"/> is set.
    /// </summary>
    public OpenSshKeyPair(string algorithm, IPrivateKeyStore store, bool create = true)
    {
        if (!IsSupported(algorithm))
        {
            throw new ArgumentException("Unsupported SSH key type: " + algorithm);
        }
        Algorithm = algorithm;
        _store = store;
        byte[] stored = store.Read();
        if (stored != null)
        {
            try
            {
                string privateKey = Encoding.ASCII.GetString(stored);
                string keyType = OpenSshPrivateKey.ReadKeyType(OpenSshPrivateKey.ReadPublicKeyBlob(privateKey));
                if (keyType != algorithm)
                {
                    throw new InvalidDataException("it holds a " + keyType + " key.");
                }
                Accept(privateKey);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException("The SmarTTY key in " + store.Location + " is damaged: " + exception.Message + " Delete it to let SmarTTY create a new key.", exception);
            }
        }
        else if (create)
        {
            ResetContainer();
        }
        else
        {
            throw new FileNotFoundException("There is no " + algorithm + " key in " + store.Location + ".");
        }
    }

    public static bool IsSupported(string algorithm)
    {
        return algorithm == OpenSshPrivateKey.Ed25519Algorithm || GetCurveName(algorithm) != null;
    }

    public byte[] GetPublicKeyBlob()
    {
        return (byte[])_publicKeyBlob.Clone();
    }

    public IPrivateKeySource CreatePrivateKeySource()
    {
        return new PrivateKeyFile(new MemoryStream(Encoding.ASCII.GetBytes(_privateKey)));
    }

    public void ExportPrivateKey(FileStream fileStream)
    {
        byte[] data = Encoding.ASCII.GetBytes(_privateKey);
        fileStream.Write(data, 0, data.Length);
    }

    public void ImportPrivateKey(FileStream fileStream)
    {
        if (fileStream.Length > MaxKeyFileBytes)
        {
            throw new InvalidDataException("The file is too large for a private key.");
        }
        string text;
        using (StreamReader reader = new StreamReader(fileStream, Encoding.ASCII, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
        {
            text = reader.ReadToEnd();
        }
        string privateKey = ConvertPrivateKey(text, Algorithm);
        _store.Write(Encoding.ASCII.GetBytes(privateKey));
        Accept(privateKey);
    }

    /// <summary>Replaces the key with a newly generated one.</summary>
    public void ResetContainer()
    {
        string privateKey = Generate(Algorithm, Environment.UserName + "@" + Environment.MachineName);
        _store.Write(Encoding.ASCII.GetBytes(privateKey));
        Accept(privateKey);
    }

    /// <summary>
    /// Converts a private key file without a passphrase in any format SSH.NET reads (OpenSSH, PEM, PKCS#8, PuTTY .ppk)
    /// to an OpenSSH private key. Throws <see cref="InvalidDataException"/> with the reason when the file is not a key
    /// of type <paramref name="algorithm"/> that can be used.
    /// </summary>
    public static string ConvertPrivateKey(string text, string algorithm)
    {
        PrivateKeyFile file;
        try
        {
            file = new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(text ?? "")));
        }
        catch (SshPassPhraseNullOrEmptyException exception)
        {
            throw new InvalidDataException("The key is protected by a passphrase. Remove the passphrase from a copy of the key first (ssh-keygen -p -N \"\" -f <copy>, or in PuTTYgen).", exception);
        }
        catch (Exception exception) when (!(exception is OutOfMemoryException))
        {
            throw new InvalidDataException("This is not a private key that SmarTTY can read: " + exception.Message, exception);
        }
        using (file)
        {
            string keyType = file.Key.ToString();
            if (keyType != algorithm)
            {
                throw new InvalidDataException("This is a " + keyType + " key, but the selected key type is " + algorithm + ". Select the key type of the file in the connection settings first.");
            }
            byte[] publicKeyBlob = file.HostKeyAlgorithms.First().Data;
            string comment = file.Key.Comment;
            if (file.Key is ED25519Key ed25519Key)
            {
                // SSH.NET keeps the 32-byte seed of the key.
                return OpenSshPrivateKey.WriteEd25519(ed25519Key.PrivateKey.Take(32).ToArray(), OpenSshPrivateKey.ReadEd25519PublicKey(publicKeyBlob), comment);
            }
            OpenSshPrivateKey.ReadEcdsaPublicKey(publicKeyBlob, out string curveName, out byte[] q);
            return OpenSshPrivateKey.WriteEcdsa(curveName, q, ((EcdsaKey)file.Key).PrivateKey, comment);
        }
    }

    /// <summary>Generates a new key of type <paramref name="algorithm"/> as an OpenSSH private key.</summary>
    public static string Generate(string algorithm, string comment)
    {
        if (algorithm == OpenSshPrivateKey.Ed25519Algorithm)
        {
            // An Ed25519 private key is 32 random bytes; SSH.NET derives the public key from them.
            byte[] seed = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(seed);
            }
            using ED25519Key key = new ED25519Key(seed);
            return OpenSshPrivateKey.WriteEd25519(seed, key.PublicKey, comment);
        }
        string curveName = GetCurveName(algorithm) ?? throw new ArgumentException("Unsupported SSH key type: " + algorithm);
        ECParameters parameters = GenerateEcdsa(curveName);
        int size = parameters.Q.X.Length;
        byte[] q = new byte[1 + 2 * size];
        q[0] = 4;
        parameters.Q.X.CopyTo(q, 1);
        parameters.Q.Y.CopyTo(q, 1 + size);
        return OpenSshPrivateKey.WriteEcdsa(curveName, q, parameters.D, comment);
    }

    private static ECParameters GenerateEcdsa(string curveName)
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            // A CNG key gives out its private part only when it was created with that export policy.
            CngAlgorithm cngAlgorithm = curveName == "nistp256" ? CngAlgorithm.ECDsaP256 : (curveName == "nistp384" ? CngAlgorithm.ECDsaP384 : CngAlgorithm.ECDsaP521);
            CngKeyCreationParameters creationParameters = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.AllowPlaintextExport };
            using CngKey cngKey = CngKey.Create(cngAlgorithm, null, creationParameters);
            using ECDsaCng cngEcdsa = new ECDsaCng(cngKey);
            return cngEcdsa.ExportParameters(includePrivateParameters: true);
        }
        ECCurve curve = curveName == "nistp256" ? ECCurve.NamedCurves.nistP256 : (curveName == "nistp384" ? ECCurve.NamedCurves.nistP384 : ECCurve.NamedCurves.nistP521);
        using ECDsa ecdsa = ECDsa.Create(curve);
        return ecdsa.ExportParameters(includePrivateParameters: true);
    }

    private static string GetCurveName(string algorithm)
    {
        if (algorithm == null || !algorithm.StartsWith(OpenSshPrivateKey.EcdsaAlgorithmPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        string curveName = algorithm.Substring(OpenSshPrivateKey.EcdsaAlgorithmPrefix.Length);
        return new[] { "nistp256", "nistp384", "nistp521" }.Contains(curveName) ? curveName : null;
    }

    private void Accept(string privateKey)
    {
        _publicKeyBlob = OpenSshPrivateKey.ReadPublicKeyBlob(privateKey);
        _privateKey = privateKey;
    }
}
