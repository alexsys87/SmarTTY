using System;
using System.IO;
using System.Text;
using SmarTTY.Ssh;

// Checks that an encrypted private key (the passphrase is what is needed) is told from a file that cannot be loaded for
// another reason (damaged, not a private key), so that only the first one is answered with a passphrase prompt.
// The keys are built here: only the headers matter, not the key material.
// Run with: dotnet run --project tests/PrivateKeyFileFormatTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void AddString(MemoryStream stream, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(new byte[] { (byte)(bytes.Length >> 24), (byte)(bytes.Length >> 16), (byte)(bytes.Length >> 8), (byte)bytes.Length }, 0, 4);
        stream.Write(bytes, 0, bytes.Length);
    }

    // The start of an OpenSSH key file: magic, cipher name, KDF name and options, number of keys, then some filler.
    private static byte[] OpenSshData(string cipher, bool magic = true)
    {
        MemoryStream stream = new MemoryStream();
        byte[] start = Encoding.ASCII.GetBytes(magic ? "openssh-key-v1\0" : "openssh-key-v2\0");
        stream.Write(start, 0, start.Length);
        AddString(stream, cipher);
        AddString(stream, cipher == "none" ? "none" : "bcrypt");
        AddString(stream, cipher == "none" ? "" : "0123456789abcdef");
        stream.Write(new byte[] { 0, 0, 0, 1 }, 0, 4);
        byte[] filler = new byte[120];
        stream.Write(filler, 0, filler.Length);
        return stream.ToArray();
    }

    private static string Pem(string label, string body, string lineBreak = "\n", string endLabel = null)
    {
        return "-----BEGIN " + label + "-----" + lineBreak + body + lineBreak + "-----END " + (endLabel ?? label) + "-----" + lineBreak;
    }

    private static string Wrapped(byte[] data, string lineBreak = "\n")
    {
        string base64 = Convert.ToBase64String(data);
        StringBuilder text = new StringBuilder();
        for (int i = 0; i < base64.Length; i += 70)
        {
            text.Append(base64, i, Math.Min(70, base64.Length - i)).Append(lineBreak);
        }
        return text.ToString().TrimEnd('\r', '\n');
    }

    private static PrivateKeyFileKind Kind(string text)
    {
        return PrivateKeyFileFormat.Classify(text);
    }

    private static int Main()
    {
        string plainOpenSsh = Pem("OPENSSH PRIVATE KEY", Wrapped(OpenSshData("none")));
        string encryptedOpenSsh = Pem("OPENSSH PRIVATE KEY", Wrapped(OpenSshData("aes256-ctr")));
        Check(Kind(plainOpenSsh) == PrivateKeyFileKind.Plain, "openssh: cipher none is a key without a passphrase");
        Check(Kind(encryptedOpenSsh) == PrivateKeyFileKind.Encrypted, "openssh: a cipher means a passphrase");
        Check(Kind(Pem("OPENSSH PRIVATE KEY", Wrapped(OpenSshData("aes256-gcm@openssh.com")))) == PrivateKeyFileKind.Encrypted, "openssh: aes256-gcm is encrypted too");
        Check(Kind(Pem("OPENSSH PRIVATE KEY", Wrapped(OpenSshData("aes256-ctr"), "\r\n"), "\r\n")) == PrivateKeyFileKind.Encrypted, "openssh: Windows line breaks");
        Check(Kind("\uFEFF" + plainOpenSsh) == PrivateKeyFileKind.Plain, "openssh: a byte order mark before the block");
        Check(Kind("# my key\n" + encryptedOpenSsh + "\n") == PrivateKeyFileKind.Encrypted, "openssh: text around the block");

        // Damaged OpenSSH keys must not read as "encrypted".
        Check(Kind(Pem("OPENSSH PRIVATE KEY", Wrapped(OpenSshData("aes256-ctr", magic: false)))) == PrivateKeyFileKind.NotAPrivateKey, "openssh: wrong magic is not a key");
        Check(Kind(Pem("OPENSSH PRIVATE KEY", Wrapped(Encoding.ASCII.GetBytes("openssh-key-v1\0")))) == PrivateKeyFileKind.NotAPrivateKey, "openssh: cut off after the magic");
        Check(Kind(Pem("OPENSSH PRIVATE KEY", "!!! not base64 !!!")) == PrivateKeyFileKind.NotAPrivateKey, "openssh: not base64");
        Check(Kind(Pem("OPENSSH PRIVATE KEY", "")) == PrivateKeyFileKind.NotAPrivateKey, "openssh: empty block");
        byte[] hugeName = new byte[40];
        Encoding.ASCII.GetBytes("openssh-key-v1\0").CopyTo(hugeName, 0);
        hugeName[15] = 0xFF;
        Check(Kind(Pem("OPENSSH PRIVATE KEY", Wrapped(hugeName))) == PrivateKeyFileKind.NotAPrivateKey, "openssh: impossible cipher name length");

        // Traditional PEM keys: encryption is announced in a header.
        string legacyBody = "MIIBOgIBAAJBAKj34GkxFhD90vcNLYLInFEX6Ppy1tPf9Cnzj4p4WGeKLs1Pt8Qu\nKUpRKfFLfRYC9AIKjbJTWit+CqvjWYzvQwECAwEAAQ==";
        Check(Kind(Pem("RSA PRIVATE KEY", "Proc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,0123456789ABCDEF0123456789ABCDEF\n\n" + legacyBody)) == PrivateKeyFileKind.Encrypted, "pem: Proc-Type ENCRYPTED");
        Check(Kind(Pem("EC PRIVATE KEY", "Proc-Type: 4, ENCRYPTED\r\nDEK-Info: AES-256-CBC,00\r\n\r\n" + legacyBody, "\r\n")) == PrivateKeyFileKind.Encrypted, "pem: EC key, Windows line breaks");
        Check(Kind(Pem("RSA PRIVATE KEY", legacyBody)) == PrivateKeyFileKind.Plain, "pem: no header means no passphrase");
        Check(Kind(Pem("PRIVATE KEY", legacyBody)) == PrivateKeyFileKind.Plain, "pkcs8: plain");
        Check(Kind(Pem("ENCRYPTED PRIVATE KEY", legacyBody)) == PrivateKeyFileKind.Encrypted, "pkcs8: encrypted");
        Check(Kind(Pem("RSA PRIVATE KEY", "this text mentions ENCRYPTED but is not a header\n" + legacyBody)) == PrivateKeyFileKind.Plain, "pem: the word alone is not the header");

        // Not a private key at all.
        Check(Kind("") == PrivateKeyFileKind.NotAPrivateKey && Kind(null) == PrivateKeyFileKind.NotAPrivateKey && Kind("  \n") == PrivateKeyFileKind.NotAPrivateKey, "empty file");
        Check(Kind("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGq3 me@host\n") == PrivateKeyFileKind.NotAPrivateKey, "a public key line");
        Check(Kind(Pem("PUBLIC KEY", legacyBody)) == PrivateKeyFileKind.NotAPrivateKey, "a PEM public key");
        Check(Kind("-----BEGIN RSA PRIVATE KEY-----\n" + legacyBody) == PrivateKeyFileKind.NotAPrivateKey, "a key without its end line (cut off file)");
        Check(Kind(Pem("RSA PRIVATE KEY", legacyBody, "\n", "EC PRIVATE KEY")) == PrivateKeyFileKind.NotAPrivateKey, "begin and end of different kinds");
        Check(Kind("\0\0\0binary\u0001\u0002 data") == PrivateKeyFileKind.NotAPrivateKey, "binary data");

        // PuTTY keys, which SSH.NET reads: the Encryption header tells whether the passphrase is needed.
        Check(Kind("PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: aes256-cbc\nComment: me\n") == PrivateKeyFileKind.Encrypted, "putty ppk v3 with a passphrase");
        Check(Kind("PuTTY-User-Key-File-3: ecdsa-sha2-nistp256\r\nEncryption: aes256-cbc\r\nComment: me\r\n") == PrivateKeyFileKind.Encrypted, "putty ppk with CRLF line ends");
        Check(Kind("\uFEFFPuTTY-User-Key-File-2: ssh-rsa\nEncryption: none\n") == PrivateKeyFileKind.Plain, "putty ppk v2 without a passphrase, with a byte order mark");
        Check(Kind("PuTTY-User-Key-File-3: ssh-ed25519\nComment: me\n") == PrivateKeyFileKind.NotAPrivateKey, "putty ppk without the Encryption header");

        // From a file.
        string directory = Path.Combine(Path.GetTempPath(), "smartty-key-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "id_ed25519");
            File.WriteAllText(path, encryptedOpenSsh);
            Check(PrivateKeyFileFormat.Inspect(path) == PrivateKeyFileKind.Encrypted, "file: encrypted key");
            File.WriteAllText(path, plainOpenSsh);
            Check(PrivateKeyFileFormat.Inspect(path) == PrivateKeyFileKind.Plain, "file: plain key");
            File.WriteAllBytes(path, new byte[2 * 1024 * 1024]);
            Check(PrivateKeyFileFormat.Inspect(path) == PrivateKeyFileKind.NotAPrivateKey, "file: too big to be a key");
            File.WriteAllText(path, "");
            Check(PrivateKeyFileFormat.Inspect(path) == PrivateKeyFileKind.NotAPrivateKey, "file: empty");
            bool missingIsIoError = false;
            try
            {
                PrivateKeyFileFormat.Inspect(Path.Combine(directory, "missing"));
            }
            catch (IOException)
            {
                missingIsIoError = true;
            }
            Check(missingIsIoError, "file: a file that cannot be read is an IOException, not a kind");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
