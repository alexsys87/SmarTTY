using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.Ssh;

/// <summary>
/// The OpenSSH private key format ("-----BEGIN OPENSSH PRIVATE KEY-----", what ssh-keygen writes) for keys without a
/// passphrase. SmarTTY keeps its Ed25519 and ECDSA keys in this format: SSH.NET loads it for authentication, and it is
/// the file that is exported, so the key can be used with ssh and PuTTYgen.
/// Only writing the key and reading its public part are done here; the private key is loaded by SSH.NET.
/// </summary>
internal static class OpenSshPrivateKey
{
    public const string Ed25519Algorithm = "ssh-ed25519";

    public const string EcdsaAlgorithmPrefix = "ecdsa-sha2-";

    private const string Label = "OPENSSH PRIVATE KEY";

    // ssh-keygen wraps the base64 text at 70 characters.
    private const int LineLength = 70;

    // The private section is padded to the block size of the cipher, 8 for "none".
    private const int BlockSize = 8;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("openssh-key-v1\0");

    private static readonly Regex Block = new Regex(
        @"-----BEGIN " + Label + @"-----(?<body>.*?)-----END " + Label + "-----",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Writes an Ed25519 key from its 32-byte private seed and its 32-byte public key.</summary>
    public static string WriteEd25519(byte[] seed, byte[] publicKey, string comment)
    {
        if (seed == null || seed.Length != 32 || publicKey == null || publicKey.Length != 32)
        {
            throw new ArgumentException("An Ed25519 key has a 32-byte seed and a 32-byte public key.");
        }
        SshWriter fields = new SshWriter();
        fields.WriteString(publicKey);
        // OpenSSH stores the seed followed by the public key as the private key.
        fields.WriteString(seed.Concat(publicKey).ToArray());
        return Write(Ed25519Algorithm, Ed25519PublicKeyBlob(publicKey), fields.ToArray(), comment);
    }

    /// <summary>
    /// Writes an ECDSA key: <paramref name="curveName"/> is "nistp256", "nistp384" or "nistp521", <paramref name="q"/>
    /// the uncompressed public point (0x04 || X || Y) and <paramref name="d"/> the private value (unsigned, big-endian).
    /// </summary>
    public static string WriteEcdsa(string curveName, byte[] q, byte[] d, string comment)
    {
        if (q == null || q.Length < 3 || q[0] != 4 || d == null || d.Length == 0)
        {
            throw new ArgumentException("An ECDSA key needs an uncompressed public point and a private value.");
        }
        SshWriter fields = new SshWriter();
        fields.WriteString(curveName);
        fields.WriteString(q);
        fields.WriteMpInt(d);
        return Write(EcdsaAlgorithmPrefix + curveName, EcdsaPublicKeyBlob(curveName, q), fields.ToArray(), comment);
    }

    public static byte[] Ed25519PublicKeyBlob(byte[] publicKey)
    {
        SshWriter blob = new SshWriter();
        blob.WriteString(Ed25519Algorithm);
        blob.WriteString(publicKey);
        return blob.ToArray();
    }

    public static byte[] EcdsaPublicKeyBlob(string curveName, byte[] q)
    {
        SshWriter blob = new SshWriter();
        blob.WriteString(EcdsaAlgorithmPrefix + curveName);
        blob.WriteString(curveName);
        blob.WriteString(q);
        return blob.ToArray();
    }

    /// <summary>
    /// Reads the public key blob (as in authorized_keys) of an OpenSSH private key.
    /// Throws <see cref="InvalidDataException"/> for anything else, including a key protected by a passphrase.
    /// </summary>
    public static byte[] ReadPublicKeyBlob(string text)
    {
        Match match = Block.Match(text ?? "");
        if (!match.Success)
        {
            throw new InvalidDataException("This is not an OpenSSH private key (\"-----BEGIN " + Label + "-----\").");
        }
        byte[] data;
        try
        {
            data = Convert.FromBase64String(Regex.Replace(match.Groups["body"].Value, @"\s+", ""));
        }
        catch (FormatException)
        {
            throw new InvalidDataException("The OpenSSH private key is damaged (invalid base64 data).");
        }
        if (data.Length < Magic.Length || !data.Take(Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("The OpenSSH private key is damaged (unknown format).");
        }
        SshReader reader = new SshReader(data, Magic.Length);
        string cipher = Encoding.ASCII.GetString(reader.ReadString());
        if (cipher != "none")
        {
            throw new InvalidDataException("The key is protected by a passphrase. Remove the passphrase from a copy of the key first (ssh-keygen -p -N \"\" -f <copy>).");
        }
        reader.ReadString(); // KDF name
        reader.ReadString(); // KDF options
        if (reader.ReadUInt32() != 1)
        {
            throw new InvalidDataException("The file holds more than one key.");
        }
        return reader.ReadString();
    }

    /// <summary>The key type at the start of a public key blob ("ssh-ed25519", "ecdsa-sha2-nistp256"...).</summary>
    public static string ReadKeyType(byte[] publicKeyBlob)
    {
        return Encoding.ASCII.GetString(new SshReader(publicKeyBlob, 0).ReadString());
    }

    /// <summary>Reads the 32-byte public key from the public key blob of an Ed25519 key.</summary>
    public static byte[] ReadEd25519PublicKey(byte[] publicKeyBlob)
    {
        SshReader reader = new SshReader(publicKeyBlob, 0);
        reader.ReadString();
        return reader.ReadString();
    }

    /// <summary>Reads the curve name ("nistp256"...) and the public point from the public key blob of an ECDSA key.</summary>
    public static void ReadEcdsaPublicKey(byte[] publicKeyBlob, out string curveName, out byte[] q)
    {
        SshReader reader = new SshReader(publicKeyBlob, 0);
        reader.ReadString();
        curveName = Encoding.ASCII.GetString(reader.ReadString());
        q = reader.ReadString();
    }

    private static string Write(string keyType, byte[] publicKeyBlob, byte[] keyFields, string comment)
    {
        byte[] check = new byte[4];
        using (RandomNumberGenerator random = RandomNumberGenerator.Create())
        {
            random.GetBytes(check);
        }
        SshWriter section = new SshWriter();
        // The two equal check values tell a wrong passphrase on an encrypted key; they are written for "none" too.
        section.WriteRaw(check);
        section.WriteRaw(check);
        section.WriteString(keyType);
        section.WriteRaw(keyFields);
        section.WriteString(comment ?? "");
        for (byte pad = 1; section.Length % BlockSize != 0; pad++)
        {
            section.WriteRaw(new byte[1] { pad });
        }

        SshWriter file = new SshWriter();
        file.WriteRaw(Magic);
        file.WriteString("none");
        file.WriteString("none");
        file.WriteString(new byte[0]);
        file.WriteUInt32(1);
        file.WriteString(publicKeyBlob);
        file.WriteString(section.ToArray());

        string base64 = Convert.ToBase64String(file.ToArray());
        StringBuilder text = new StringBuilder();
        text.Append("-----BEGIN " + Label + "-----\n");
        for (int i = 0; i < base64.Length; i += LineLength)
        {
            text.Append(base64, i, Math.Min(LineLength, base64.Length - i)).Append('\n');
        }
        text.Append("-----END " + Label + "-----\n");
        return text.ToString();
    }

    private sealed class SshWriter
    {
        private readonly MemoryStream _stream = new MemoryStream();

        public long Length => _stream.Length;

        public void WriteRaw(byte[] data)
        {
            _stream.Write(data, 0, data.Length);
        }

        public void WriteUInt32(uint value)
        {
            WriteRaw(new byte[4] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        }

        public void WriteString(byte[] data)
        {
            WriteUInt32((uint)data.Length);
            WriteRaw(data);
        }

        public void WriteString(string text)
        {
            WriteString(Encoding.UTF8.GetBytes(text));
        }

        // A positive multiple precision integer: no leading zero bytes, except one that keeps the top bit clear.
        public void WriteMpInt(byte[] unsignedValue)
        {
            int start = 0;
            while (start < unsignedValue.Length && unsignedValue[start] == 0)
            {
                start++;
            }
            bool padding = start < unsignedValue.Length && (unsignedValue[start] & 0x80) != 0;
            WriteUInt32((uint)(unsignedValue.Length - start + (padding ? 1 : 0)));
            if (padding)
            {
                _stream.WriteByte(0);
            }
            _stream.Write(unsignedValue, start, unsignedValue.Length - start);
        }

        public byte[] ToArray()
        {
            return _stream.ToArray();
        }
    }

    private sealed class SshReader
    {
        private readonly byte[] _data;

        private int _offset;

        public SshReader(byte[] data, int offset)
        {
            _data = data;
            _offset = offset;
        }

        public uint ReadUInt32()
        {
            if (_data.Length - _offset < 4)
            {
                throw new InvalidDataException("The key is damaged (it ends too early).");
            }
            uint value = ((uint)_data[_offset] << 24) | ((uint)_data[_offset + 1] << 16) | ((uint)_data[_offset + 2] << 8) | _data[_offset + 3];
            _offset += 4;
            return value;
        }

        public byte[] ReadString()
        {
            uint length = ReadUInt32();
            if (length > _data.Length - _offset)
            {
                throw new InvalidDataException("The key is damaged (a field is longer than the data).");
            }
            byte[] value = new byte[length];
            Buffer.BlockCopy(_data, _offset, value, 0, (int)length);
            _offset += (int)length;
            return value;
        }
    }
}
