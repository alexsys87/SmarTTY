using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace ManagedLibSSH2;

public class DsaSshKey : SshKeyEncoding, ISshKeyPair, ISshSigner
{
    private DSACryptoServiceProvider _csp;

    public ISshSigner[] Signers { get; }

    public string Algorithm => "ssh-dss";

    public string ContainerName { get; }

    public DsaSshKey(string text, bool flag = true)
    {
        ContainerName = text;
        _csp = OpenContainer(ContainerName, flag);
        ISshSigner[] array = new DsaSshKey[1] { this };
        Signers = array;
    }

    private static DSACryptoServiceProvider OpenContainer(string text, bool flag = true)
    {
        CspParameters cspParameters = new CspParameters
        {
            KeyContainerName = text,
            ProviderType = 13
        };
        if (!flag)
        {
            cspParameters.Flags |= CspProviderFlags.UseExistingKey;
        }
        try
        {
            return new DSACryptoServiceProvider(cspParameters);
        }
        catch (CryptographicException ex)
        {
            if (Marshal.GetHRForException(ex) == -2146893802 && !flag)
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

    public int Sign(IntPtr handle, out IntPtr handle2, out IntPtr handle3, byte[] buffer, int value2, IntPtr handle4)
    {
        handle2 = IntPtr.Zero;
        handle3 = IntPtr.Zero;
        try
        {
            using SHA1CryptoServiceProvider sHA1CryptoServiceProvider = new SHA1CryptoServiceProvider();
            sHA1CryptoServiceProvider.Initialize();
            byte[] rgbHash = sHA1CryptoServiceProvider.ComputeHash(buffer);
            byte[] array = _csp.CreateSignature(rgbHash);
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
        DSAParameters dSAParameters = _csp.ExportParameters(includePrivateParameters: false);
        byte[] bytes = Encoding.UTF8.GetBytes(Algorithm);
        byte[] array = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(bytes.Length));
        Array.Resize(ref array, array.Length + bytes.Length);
        bytes.CopyTo(array, array.Length - bytes.Length);
        SshKeyEncoding.AppendMpInt(ref array, dSAParameters.P);
        SshKeyEncoding.AppendMpInt(ref array, dSAParameters.Q);
        SshKeyEncoding.AppendMpInt(ref array, dSAParameters.G);
        SshKeyEncoding.AppendMpInt(ref array, dSAParameters.Y);
        return array;
    }

    public void ExportPrivateKey(FileStream fileStream)
    {
        DSAParameters dSAParameters = _csp.ExportParameters(includePrivateParameters: true);
        new XmlSerializer(typeof(DSAParameters)).Serialize(fileStream, dSAParameters);
    }

    public void ImportPrivateKey(FileStream fileStream)
    {
        DSAParameters parameters = (DSAParameters)new XmlSerializer(typeof(DSAParameters)).Deserialize(fileStream);
        _csp.ImportParameters(parameters);
    }

    public void ResetContainer()
    {
        _csp.PersistKeyInCsp = false;
        _csp.Clear();
        _csp = OpenContainer(ContainerName);
    }
}
