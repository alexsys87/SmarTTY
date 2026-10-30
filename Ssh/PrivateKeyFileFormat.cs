using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.Ssh;

/// <summary>What a private key file holds, as far as can be told without loading the key.</summary>
internal enum PrivateKeyFileKind
{
    /// <summary>No PEM, OpenSSH or PuTTY private key (an empty or damaged file, a public key, some other format).</summary>
    NotAPrivateKey,

    /// <summary>A key that needs no passphrase.</summary>
    Plain,

    /// <summary>A key protected by a passphrase.</summary>
    Encrypted
}

/// <summary>
/// Tells an encrypted private key from one that cannot be loaded for another reason. A failed load of the key says
/// nothing about that by itself: a passphrase prompt is only the right answer for an encrypted key.
/// </summary>
internal static class PrivateKeyFileFormat
{
    // Private keys are a few kilobytes; anything bigger is not one.
    private const long MaxKeyFileBytes = 1024 * 1024;

    private static readonly Regex Block = new Regex(
        @"-----BEGIN ([A-Z0-9 ]*PRIVATE KEY)-----(?<body>.*?)-----END \1-----",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyEncryptionHeader = new Regex(
        @"^\s*Proc-Type:\s*4\s*,\s*ENCRYPTED",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The header of a PuTTY .ppk file (versions 2 and 3) that names its cipher ("none" for a key without a passphrase).
    private static readonly Regex PuttyEncryptionHeader = new Regex(
        @"^Encryption:[ \t]*(?<cipher>\S+)[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly byte[] OpenSshMagic = Encoding.ASCII.GetBytes("openssh-key-v1\0");

    public static PrivateKeyFileKind Inspect(string path)
    {
        if (new FileInfo(path).Length > MaxKeyFileBytes)
        {
            return PrivateKeyFileKind.NotAPrivateKey;
        }
        return Classify(File.ReadAllText(path));
    }

    public static PrivateKeyFileKind Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return PrivateKeyFileKind.NotAPrivateKey;
        }
        if (text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal))
        {
            Match encryption = PuttyEncryptionHeader.Match(text);
            if (!encryption.Success)
            {
                return PrivateKeyFileKind.NotAPrivateKey;
            }
            return encryption.Groups["cipher"].Value == "none" ? PrivateKeyFileKind.Plain : PrivateKeyFileKind.Encrypted;
        }
        Match match = Block.Match(text);
        if (!match.Success)
        {
            return PrivateKeyFileKind.NotAPrivateKey;
        }
        string label = match.Groups[1].Value;
        string body = match.Groups["body"].Value;
        if (label == "ENCRYPTED PRIVATE KEY")
        {
            return PrivateKeyFileKind.Encrypted;
        }
        if (label == "OPENSSH PRIVATE KEY")
        {
            return ClassifyOpenSsh(body);
        }
        // Traditional PEM keys (RSA, EC, DSA) say in a header before the base64 data that they are encrypted.
        return LegacyEncryptionHeader.IsMatch(body) ? PrivateKeyFileKind.Encrypted : PrivateKeyFileKind.Plain;
    }

    // The OpenSSH format starts with a magic string and the name of the cipher ("none" for a key without a passphrase).
    private static PrivateKeyFileKind ClassifyOpenSsh(string body)
    {
        byte[] data;
        try
        {
            data = Convert.FromBase64String(Regex.Replace(body, @"\s+", ""));
        }
        catch (FormatException)
        {
            return PrivateKeyFileKind.NotAPrivateKey;
        }
        int lengthOffset = OpenSshMagic.Length;
        if (data.Length < lengthOffset + 4 || !data.Take(lengthOffset).SequenceEqual(OpenSshMagic))
        {
            return PrivateKeyFileKind.NotAPrivateKey;
        }
        uint nameLength = ((uint)data[lengthOffset] << 24) | ((uint)data[lengthOffset + 1] << 16) | ((uint)data[lengthOffset + 2] << 8) | data[lengthOffset + 3];
        if (nameLength == 0 || nameLength > 64 || data.Length < lengthOffset + 4 + nameLength)
        {
            return PrivateKeyFileKind.NotAPrivateKey;
        }
        string cipher = Encoding.ASCII.GetString(data, lengthOffset + 4, (int)nameLength);
        return cipher == "none" ? PrivateKeyFileKind.Plain : PrivateKeyFileKind.Encrypted;
    }
}
