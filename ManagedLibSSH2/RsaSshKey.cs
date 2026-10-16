using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace ManagedLibSSH2;

public class RsaSshKey : SshKeyEncoding, ISshKeyPair
{
    private class RsaSigner : ISshSigner
    {
        private RsaSshKey _owner;

        private string _algorithmName;

        private string _hashAlgorithm;

        public RsaSigner(RsaSshKey rsaSshKey, string text, string text2)
        {
            this._owner = rsaSshKey;
            this._algorithmName = text;
            this._hashAlgorithm = text2;
        }

        public int Sign(IntPtr handle, out IntPtr handle2, out IntPtr handle3, byte[] buffer, int value2, IntPtr handle4)
        {
            handle2 = IntPtr.Zero;
            handle3 = IntPtr.Zero;
            try
            {
                using HashAlgorithm hashAlgorithm = HashAlgorithm.Create(this._hashAlgorithm);
                using RSA rSA = RSA.Create();
                byte[] rgbHash = hashAlgorithm.ComputeHash(buffer);
                rSA.ImportParameters(this._owner._csp.ExportParameters(includePrivateParameters: true));
                RSAPKCS1SignatureFormatter rSAPKCS1SignatureFormatter = new RSAPKCS1SignatureFormatter(rSA);
                rSAPKCS1SignatureFormatter.SetHashAlgorithm(this._hashAlgorithm);
                byte[] array = rSAPKCS1SignatureFormatter.CreateSignature(rgbHash);
                handle3 = (IntPtr)array.Length;
                handle2 = LibSsh2.Alloc(handle, handle3);
                Marshal.Copy(array, 0, handle2, (int)handle3);
                return 0;
            }
            catch
            {
                return -1;
            }
        }

        public byte[] GetPublicKeyBlob()
        {
            return _owner.GetPublicKeyBlob(_algorithmName);
        }
    }

    private RSACryptoServiceProvider _csp;

    private readonly int _keySize;

    public ISshSigner[] Signers { get; }

    public string Algorithm => "ssh-rsa";

    public string ContainerName { get; }

    public RsaSshKey(string text, int value2, bool flag = true)
    {
        ContainerName = text;
        _keySize = value2;
        this._csp = OpenContainer(ContainerName, _keySize, flag);
        ISshSigner[] array = new RsaSigner[3]
        {
            new RsaSigner(this, "rsa-sha2-512", "SHA512"),
            new RsaSigner(this, "rsa-sha2-256", "SHA256"),
            new RsaSigner(this, Algorithm, "SHA1")
        };
        this.Signers = array;
    }

    public static RSACryptoServiceProvider OpenContainer(string text, int value2, bool flag = true)
    {
        CspParameters cspParameters = new CspParameters
        {
            KeyContainerName = text,
            ProviderType = 1
        };
        if (!flag)
        {
            cspParameters.Flags |= CspProviderFlags.UseExistingKey;
        }
        try
        {
            return new RSACryptoServiceProvider(value2, cspParameters);
        }
        catch (CryptographicException ex)
        {
            if (Marshal.GetHRForException(ex) == -2146893802 && !flag)
            {
                return null;
            }
            throw new Exception("Cryptographic container is corrupt (" + ex.Message + "). Consider resetting containers by emptying %APPDATA%\\Microsoft\\Crypto.", ex);
        }
        catch
        {
            return null;
        }
    }

    public byte[] GetPublicKeyBlob()
    {
        return GetPublicKeyBlob(Algorithm);
    }

    private byte[] GetPublicKeyBlob(string text)
    {
        RSAParameters rSAParameters = this._csp.ExportParameters(includePrivateParameters: false);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        byte[] array = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(bytes.Length));
        Array.Resize(ref array, array.Length + bytes.Length);
        bytes.CopyTo(array, array.Length - bytes.Length);
        SshKeyEncoding.AppendMpInt(ref array, rSAParameters.Exponent);
        SshKeyEncoding.AppendMpInt(ref array, rSAParameters.Modulus);
        return array;
    }

    public void ExportPrivateKey(FileStream fileStream)
    {
        RSAParameters rSAParameters = this._csp.ExportParameters(includePrivateParameters: true);
        new XmlSerializer(typeof(RSAParameters)).Serialize(fileStream, rSAParameters);
    }

    public void ImportPrivateKey(FileStream fileStream)
    {
        RSAParameters parameters = (RSAParameters)new XmlSerializer(typeof(RSAParameters)).Deserialize(fileStream);
        this._csp.ImportParameters(parameters);
    }

    public void ResetContainer()
    {
        _csp.PersistKeyInCsp = false;
        _csp.Clear();
        _csp = OpenContainer(ContainerName, _keySize);
    }
}
