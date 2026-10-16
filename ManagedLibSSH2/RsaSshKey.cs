using Renci.SshNet;
using Renci.SshNet.Security;
using Renci.SshNet.Security.Cryptography;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace ManagedLibSSH2;

/// <summary>
/// An RSA key pair stored in a Windows CSP key container.
/// </summary>
public class RsaSshKey : SshKeyEncoding, ISshKeyPair
{
    /// <summary>
    /// Offers the key with SHA-2 signatures first; plain ssh-rsa (SHA-1) is rejected by current OpenSSH servers.
    /// </summary>
    internal sealed class PrivateKeySource : IPrivateKeySource, IDisposable
    {
        private readonly RsaKey _key;

        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }

        public PrivateKeySource(RSAParameters parameters)
        {
            _key = new RsaKey(EncodePkcs1PrivateKey(parameters));
            HostKeyAlgorithms = new HostAlgorithm[3]
            {
                new KeyHostAlgorithm("rsa-sha2-512", _key, new RsaDigitalSignature(_key, HashAlgorithmName.SHA512)),
                new KeyHostAlgorithm("rsa-sha2-256", _key, new RsaDigitalSignature(_key, HashAlgorithmName.SHA256)),
                new KeyHostAlgorithm("ssh-rsa", _key)
            };
        }

        public void Dispose()
        {
            _key.Dispose();
        }
    }

    private RSACryptoServiceProvider _csp;

    private readonly int _keySize;

    public string Algorithm => "ssh-rsa";

    public string ContainerName { get; }

    public RsaSshKey(string containerName, int keySize, bool create = true)
    {
        ContainerName = containerName;
        _keySize = keySize;
        _csp = OpenContainer(ContainerName, _keySize, create);
    }

    public static RSACryptoServiceProvider OpenContainer(string containerName, int keySize, bool create = true)
    {
        CspParameters cspParameters = new CspParameters
        {
            KeyContainerName = containerName,
            ProviderType = 1
        };
        if (!create)
        {
            cspParameters.Flags |= CspProviderFlags.UseExistingKey;
        }
        try
        {
            return new RSACryptoServiceProvider(keySize, cspParameters);
        }
        catch (CryptographicException ex)
        {
            if (Marshal.GetHRForException(ex) == -2146893802 && !create)
            {
                return null;
            }
            throw new CryptoContainerCorruptException(ex);
        }
        catch
        {
            return null;
        }
    }

    public byte[] GetPublicKeyBlob()
    {
        RSAParameters parameters = _csp.ExportParameters(includePrivateParameters: false);
        byte[] name = Encoding.UTF8.GetBytes(Algorithm);
        byte[] blob = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(name.Length));
        Array.Resize(ref blob, blob.Length + name.Length);
        name.CopyTo(blob, blob.Length - name.Length);
        AppendMpInt(ref blob, parameters.Exponent);
        AppendMpInt(ref blob, parameters.Modulus);
        return blob;
    }

    public IPrivateKeySource CreatePrivateKeySource()
    {
        return new PrivateKeySource(_csp.ExportParameters(includePrivateParameters: true));
    }

    public void ExportPrivateKey(FileStream fileStream)
    {
        RSAParameters parameters = _csp.ExportParameters(includePrivateParameters: true);
        new XmlSerializer(typeof(RSAParameters)).Serialize(fileStream, parameters);
    }

    public void ImportPrivateKey(FileStream fileStream)
    {
        RSAParameters parameters = (RSAParameters)new XmlSerializer(typeof(RSAParameters)).Deserialize(fileStream);
        _csp.ImportParameters(parameters);
    }

    public void ResetContainer()
    {
        _csp.PersistKeyInCsp = false;
        _csp.Clear();
        _csp = OpenContainer(ContainerName, _keySize);
    }

    #region PKCS#1 DER encoding

    // RSAPrivateKey ::= SEQUENCE { version, modulus, publicExponent, privateExponent, prime1, prime2, exponent1, exponent2, coefficient }
    internal static byte[] EncodePkcs1PrivateKey(RSAParameters parameters)
    {
        using MemoryStream body = new MemoryStream();
        WriteDerInteger(body, new byte[1]);
        WriteDerInteger(body, parameters.Modulus);
        WriteDerInteger(body, parameters.Exponent);
        WriteDerInteger(body, parameters.D);
        WriteDerInteger(body, parameters.P);
        WriteDerInteger(body, parameters.Q);
        WriteDerInteger(body, parameters.DP);
        WriteDerInteger(body, parameters.DQ);
        WriteDerInteger(body, parameters.InverseQ);
        using MemoryStream result = new MemoryStream();
        result.WriteByte(0x30);
        WriteDerLength(result, (int)body.Length);
        body.WriteTo(result);
        return result.ToArray();
    }

    // Writes an unsigned big-endian value as a minimal DER INTEGER.
    private static void WriteDerInteger(Stream stream, byte[] value)
    {
        int start = 0;
        while (start < value.Length - 1 && value[start] == 0)
        {
            start++;
        }
        bool needsPadding = (value[start] & 0x80) != 0;
        stream.WriteByte(0x02);
        WriteDerLength(stream, value.Length - start + (needsPadding ? 1 : 0));
        if (needsPadding)
        {
            stream.WriteByte(0);
        }
        stream.Write(value, start, value.Length - start);
    }

    private static void WriteDerLength(Stream stream, int length)
    {
        if (length < 0x80)
        {
            stream.WriteByte((byte)length);
            return;
        }
        int byteCount = (length > 0xFFFF) ? 3 : ((length > 0xFF) ? 2 : 1);
        stream.WriteByte((byte)(0x80 | byteCount));
        for (int i = byteCount - 1; i >= 0; i--)
        {
            stream.WriteByte((byte)(length >> (8 * i)));
        }
    }

    #endregion
}
