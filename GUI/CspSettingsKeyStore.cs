using SmarTTY.Ssh;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Serialization;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// Reads and writes the RSA keys that SmarTTY keeps in the CSP key containers of the Windows user, for
/// <see cref="SettingsArchive"/>. A key is exchanged as the XML of <see cref="RSAParameters"/>, the same format
/// as the *.sshkey files of the public key manager, so an exported key can also be imported there.
/// </summary>
internal sealed class CspSettingsKeyStore : ISettingsKeyStore
{
    private static string SshContainerName => AppEnvironment.KeyFactory.containerPrefix + "rsa";

    private static string PasswordContainerName => AppEnvironment.PasswordContainerName;

    public bool HasSshKey => Exists(SshContainerName);

    public bool HasPasswordKey => Exists(PasswordContainerName);

    public byte[] ExportSshKey()
    {
        return Export(SshContainerName);
    }

    public byte[] ExportPasswordKey()
    {
        return Export(PasswordContainerName);
    }

    public void ImportSshKey(byte[] data)
    {
        Import(SshContainerName, data);
    }

    public void ImportPasswordKey(byte[] data)
    {
        Import(PasswordContainerName, data);
    }

    private static bool Exists(string containerName)
    {
        try
        {
            RSACryptoServiceProvider csp = RsaSshKey.OpenContainer(containerName, 2048, create: false);
            if (csp == null)
            {
                return false;
            }
            Release(csp);
            return true;
        }
        catch (CryptoContainerCorruptException)
        {
            return false;
        }
    }

    private static byte[] Export(string containerName)
    {
        RSACryptoServiceProvider csp;
        try
        {
            csp = RsaSshKey.OpenContainer(containerName, 2048, create: false);
        }
        catch (CryptoContainerCorruptException ex)
        {
            throw new IOException("The key container '" + containerName + "' is damaged or unreadable.", ex);
        }
        if (csp == null)
        {
            return null;
        }
        try
        {
            RSAParameters parameters = csp.ExportParameters(includePrivateParameters: true);
            using (MemoryStream stream = new MemoryStream())
            {
                new XmlSerializer(typeof(RSAParameters)).Serialize(stream, parameters);
                return stream.ToArray();
            }
        }
        finally
        {
            Release(csp);
        }
    }

    private static void Import(string containerName, byte[] data)
    {
        RSAParameters parameters;
        XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using (MemoryStream stream = new MemoryStream(data))
            using (XmlReader reader = XmlReader.Create(stream, settings))
            {
                parameters = (RSAParameters)new XmlSerializer(typeof(RSAParameters)).Deserialize(reader);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is XmlException)
        {
            throw new InvalidDataException("The key in the archive cannot be read: " + ex.Message, ex);
        }
        if (parameters.Modulus == null || parameters.Exponent == null || parameters.D == null || parameters.Modulus.Length < 128)
        {
            throw new InvalidDataException("The key in the archive is not a complete RSA private key.");
        }

        RSACryptoServiceProvider csp;
        try
        {
            csp = RsaSshKey.OpenContainer(containerName, 2048);
        }
        catch (CryptoContainerCorruptException ex)
        {
            throw new IOException("The key container '" + containerName + "' is damaged or unreadable.", ex);
        }
        if (csp == null)
        {
            throw new IOException("The key container '" + containerName + "' could not be opened.");
        }
        try
        {
            csp.PersistKeyInCsp = true;
            csp.ImportParameters(parameters);
        }
        finally
        {
            Release(csp);
        }
    }

    // The key stays in the container: PersistKeyInCsp is set so that disposing the provider does not delete it.
    private static void Release(RSACryptoServiceProvider csp)
    {
        csp.PersistKeyInCsp = true;
        csp.Dispose();
    }
}
