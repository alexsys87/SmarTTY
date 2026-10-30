using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// The Ed25519 and ECDSA keys of SmarTTY (<see cref="OpenSshKeyPair"/>): generation, the OpenSSH private key that is
/// stored and exported (checked with ssh-keygen when it is installed), the import of keys made by ssh-keygen and PuTTYgen,
/// and, against the live server, installing the key in authorized_keys and logging in with it.
/// The key store is kept in memory here; the application keeps it in a DPAPI-protected file.
/// </summary>
internal static class SmarTTYKeyTests
{
    private static readonly string[] Algorithms = { "ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521" };

    private sealed class MemoryKeyStore : IPrivateKeyStore
    {
        public byte[] Data;

        public string Location => "memory";

        public byte[] Read()
        {
            return Data;
        }

        public void Write(byte[] data)
        {
            Data = data;
        }
    }

    public static void Run()
    {
        Test.Section("SmarTTY Ed25519 and ECDSA keys");
        foreach (PublicKeyType keyType in new[] { PublicKeyType.Ed25519, PublicKeyType.ECDSA256, PublicKeyType.ECDSA384, PublicKeyType.ECDSA521 })
        {
            Test.Check(SshKeyFactory.FromKeyName(SshKeyFactory.GetKeyName(keyType)) == keyType, keyType + " has a key name that maps back to it (" + SshKeyFactory.GetKeyName(keyType) + ")");
        }
        Test.Equal("rsa", SshKeyFactory.GetKeyName(PublicKeyType.RSA), "the RSA key keeps the container name of earlier versions");

        string directory = Path.Combine(Path.GetTempPath(), "smartty-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string algorithm in Algorithms)
            {
                GeneratedKey(algorithm, directory);
            }
            Import(directory);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static void GeneratedKey(string algorithm, string directory)
    {
        MemoryKeyStore store = new MemoryKeyStore();
        OpenSshKeyPair key = new OpenSshKeyPair(algorithm, store);
        Test.Check(store.Data != null, algorithm + ": a key is generated and stored when there is none");
        Test.Equal(algorithm, OpenSshPrivateKey.ReadKeyType(key.GetPublicKeyBlob()), algorithm + ": the public key has the requested type");
        string line = key.ToAuthorizedKeysLine("test@smartty");
        Test.Check(line.StartsWith(algorithm + " ", StringComparison.Ordinal), algorithm + ": the authorized_keys line starts with the key type");

        using (IDisposable source = (IDisposable)key.CreatePrivateKeySource())
        {
            Test.Check(((Renci.SshNet.IPrivateKeySource)source).HostKeyAlgorithms.Any(a => a.Name == algorithm), algorithm + ": SSH.NET loads the stored key for authentication");
        }

        OpenSshKeyPair reopened = new OpenSshKeyPair(algorithm, store, create: false);
        Test.Check(reopened.GetPublicKeyBlob().SequenceEqual(key.GetPublicKeyBlob()), algorithm + ": the stored key is loaded again, not replaced");
        byte[] before = key.GetPublicKeyBlob();
        key.ResetContainer();
        Test.Check(!key.GetPublicKeyBlob().SequenceEqual(before), algorithm + ": Reset key generates a new key");

        string exported = Path.Combine(directory, "export-" + algorithm);
        using (FileStream stream = File.Create(exported))
        {
            key.ExportPrivateKey(stream);
        }
        string publicKey = PublicKeyOf(exported);
        if (publicKey != null)
        {
            Test.Equal(key.ToAuthorizedKeysLine("").TrimEnd(), publicKey, algorithm + ": ssh-keygen reads the exported key and finds the same public key");
        }

        MemoryKeyStore otherStore = new MemoryKeyStore();
        OpenSshKeyPair other = new OpenSshKeyPair(algorithm, otherStore);
        using (FileStream stream = File.OpenRead(exported))
        {
            other.ImportPrivateKey(stream);
        }
        Test.Check(other.GetPublicKeyBlob().SequenceEqual(key.GetPublicKeyBlob()), algorithm + ": an exported key is imported back");

        Test.Throws<InvalidDataException>(() => new OpenSshKeyPair(algorithm, new MemoryKeyStore { Data = Encoding.ASCII.GetBytes("damaged") }), algorithm + ": a damaged stored key is reported, not silently replaced");
        Test.Throws<FileNotFoundException>(() => new OpenSshKeyPair(algorithm, new MemoryKeyStore(), create: false), algorithm + ": without create, a missing key is not generated");
    }

    private static void Import(string directory)
    {
        Test.Throws<InvalidDataException>(() => OpenSshKeyPair.ConvertPrivateKey("not a key", "ssh-ed25519"), "import: a file that is not a key is rejected");
        string ed25519 = OpenSshKeyPair.Generate("ssh-ed25519", "me@host");
        Test.Throws<InvalidDataException>(() => OpenSshKeyPair.ConvertPrivateKey(ed25519, "ecdsa-sha2-nistp256"), "import: a key of another type is rejected", exception =>
            Test.Check(exception.Message.Contains("ssh-ed25519"), "import: the message names the type of the file"));
        Test.Check(OpenSshKeyPair.ConvertPrivateKey(ed25519.Replace("\n", "\r\n"), "ssh-ed25519").Contains("OPENSSH PRIVATE KEY"), "import: a key with CRLF line ends is read");

        if (!Run("ssh-keygen", "-q -t ed25519 -N \"\" -f \"" + Path.Combine(directory, "probe") + "\"", out _))
        {
            Console.WriteLine("SKIP: ssh-keygen is not installed, the import of its keys is not checked.");
            return;
        }
        var keygen = new List<(string Arguments, string Algorithm)>
        {
            ("-t ed25519", "ssh-ed25519"),
            ("-t ecdsa -b 256", "ecdsa-sha2-nistp256"),
            ("-t ecdsa -b 384 -m PEM", "ecdsa-sha2-nistp384"),
            ("-t ecdsa -b 521 -m PKCS8", "ecdsa-sha2-nistp521"),
        };
        foreach ((string arguments, string algorithm) in keygen)
        {
            string file = Path.Combine(directory, "keygen-" + algorithm);
            Run("ssh-keygen", "-q " + arguments + " -N \"\" -C imported@host -f \"" + file + "\"", out _);
            MemoryKeyStore store = new MemoryKeyStore();
            OpenSshKeyPair key = new OpenSshKeyPair(algorithm, store);
            using (FileStream stream = File.OpenRead(file))
            {
                key.ImportPrivateKey(stream);
            }
            string expected = string.Join(" ", File.ReadAllText(file + ".pub").Trim().Split(' ').Take(2));
            Test.Equal(expected, key.ToAuthorizedKeysLine("").TrimEnd(), "import: the ssh-keygen key (" + arguments + ") keeps its public key");
            Test.Check(Encoding.ASCII.GetString(store.Data).Contains("-----BEGIN OPENSSH PRIVATE KEY-----"), "import: the key (" + arguments + ") is stored as an OpenSSH private key");
            File.WriteAllBytes(file + ".stored", store.Data);
            Test.Equal(expected, PublicKeyOf(file + ".stored"), "import: ssh-keygen reads the converted key (" + arguments + ")");
        }

        string encrypted = Path.Combine(directory, "encrypted");
        Run("ssh-keygen", "-q -t ed25519 -N secret-passphrase -f \"" + encrypted + "\"", out _);
        Test.Throws<InvalidDataException>(() => OpenSshKeyPair.ConvertPrivateKey(File.ReadAllText(encrypted), "ssh-ed25519"), "import: a key with a passphrase is rejected", exception =>
            Test.Check(exception.Message.Contains("passphrase"), "import: the message says that the passphrase is the problem"));

        string rsa = Path.Combine(directory, "rsa");
        Run("ssh-keygen", "-q -t rsa -b 2048 -N \"\" -f \"" + rsa + "\"", out _);
        Test.Throws<InvalidDataException>(() => OpenSshKeyPair.ConvertPrivateKey(File.ReadAllText(rsa), "ssh-ed25519"), "import: an RSA key is not taken as an Ed25519 key");

        string ppk = Path.Combine(directory, "putty.ppk");
        if (Run("puttygen", "-t ecdsa -b 384 -C putty@host -o \"" + ppk + "\" --new-passphrase /dev/null -q", out _))
        {
            Run("puttygen", "\"" + ppk + "\" -O public-openssh -o \"" + ppk + ".pub\"", out _);
            string converted = OpenSshKeyPair.ConvertPrivateKey(File.ReadAllText(ppk), "ecdsa-sha2-nistp384");
            string expected = string.Join(" ", File.ReadAllText(ppk + ".pub").Trim().Split(' ').Take(2));
            Test.Equal(expected, OpenSshPrivateKey.ReadKeyType(OpenSshPrivateKey.ReadPublicKeyBlob(converted)) + " " + Convert.ToBase64String(OpenSshPrivateKey.ReadPublicKeyBlob(converted)), "import: a PuTTY .ppk key is converted with its public key");
        }
        else
        {
            Console.WriteLine("SKIP: puttygen is not installed, the import of a .ppk key is not checked.");
        }
    }

    /// <summary>Logs in with each key type after installing it the way SmarTTY does (Setup public key authentication).</summary>
    public static void RunLive(TestEnvironment environment)
    {
        Test.Section("Login with SmarTTY Ed25519 and ECDSA keys");
        foreach (string algorithm in Algorithms)
        {
            OpenSshKeyPair key = new OpenSshKeyPair(algorithm, new MemoryKeyStore());
            using (SshSession session = new SshSession(environment.Parameters()))
            {
                Test.Check(!session.AuthenticateWithPublicKey(environment.User, key), algorithm + ": the key is refused before it is installed");
            }
            using (SshSession session = new SshSession(environment.Parameters()))
            {
                if (!session.Authenticate(environment.User, environment.Password))
                {
                    throw new InvalidOperationException("Could not authenticate against the test server.");
                }
                AuthorizedKeysManager.AppendAuthorizedKey(session, key.ToAuthorizedKeysLine("smartty-test"));
                Test.Check(AuthorizedKeysManager.IsKeyAuthorized(session, key.GetPublicKeyBlob()), algorithm + ": the key is installed in authorized_keys");
            }
            try
            {
                using SshSession session = new SshSession(environment.Parameters());
                Test.Check(session.AuthenticateWithPublicKey(environment.User, key), algorithm + ": the server accepts the key");
                Test.Check(session.IsAuthenticated, algorithm + ": the session is authenticated with the key");
            }
            finally
            {
                using SshSession session = new SshSession(environment.Parameters());
                if (session.Authenticate(environment.User, environment.Password))
                {
                    AuthorizedKeysManager.RemoveAuthorizedKey(session, key.GetPublicKeyBlob());
                }
            }
        }
    }

    /// <summary>A PuTTY .ppk key with a passphrase logs in through the key file path, asking for the passphrase first.</summary>
    public static void RunLivePutty(TestEnvironment environment)
    {
        Test.Section("Login with a PuTTY key file");
        string directory = Path.Combine(Path.GetTempPath(), "smartty-ppk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string ppk = Path.Combine(directory, "key.ppk");
            string passphraseFile = Path.Combine(directory, "passphrase");
            File.WriteAllText(passphraseFile, "ppk-passphrase\n");
            if (!Run("puttygen", "-t ed25519 -o \"" + ppk + "\" --new-passphrase \"" + passphraseFile + "\" -q", out _) ||
                !Run("puttygen", "\"" + ppk + "\" --old-passphrase \"" + passphraseFile + "\" -O public-openssh -o \"" + ppk + ".pub\"", out _))
            {
                Console.WriteLine("SKIP: puttygen is not installed, the login with a .ppk key is not checked.");
                return;
            }
            string publicKeyLine = File.ReadAllText(ppk + ".pub").Trim();
            byte[] publicKeyBlob = Convert.FromBase64String(publicKeyLine.Split(' ')[1]);
            using (SshSession session = new SshSession(environment.Parameters()))
            {
                if (!session.Authenticate(environment.User, environment.Password))
                {
                    throw new InvalidOperationException("Could not authenticate against the test server.");
                }
                AuthorizedKeysManager.AppendAuthorizedKey(session, publicKeyLine);
            }
            try
            {
                using (SshSession session = new SshSession(environment.Parameters()))
                {
                    Test.Check(!session.AuthenticateWithKeyFile(environment.User, out bool passphraseRequired, ppk), "ppk: a key with a passphrase does not log in without it");
                    Test.Check(passphraseRequired, "ppk: the missing passphrase is reported, so SmarTTY asks for it");
                }
                using (SshSession session = new SshSession(environment.Parameters()))
                {
                    Test.Check(session.AuthenticateWithKeyFile(environment.User, out bool passphraseRequired, ppk, "ppk-passphrase"), "ppk: the key logs in with its passphrase");
                    Test.Check(!passphraseRequired, "ppk: a correct passphrase is not reported as missing");
                }
            }
            finally
            {
                using SshSession session = new SshSession(environment.Parameters());
                if (session.Authenticate(environment.User, environment.Password))
                {
                    AuthorizedKeysManager.RemoveAuthorizedKey(session, publicKeyBlob);
                }
            }
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    // The public key line ("type base64") that ssh-keygen reads from a private key file, or null without ssh-keygen.
    private static string PublicKeyOf(string privateKeyFile)
    {
        if (!OperatingSystem.IsWindows())
        {
            // ssh-keygen refuses a private key that others can read.
            File.SetUnixFileMode(privateKeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        if (!Run("ssh-keygen", "-y -f \"" + privateKeyFile + "\"", out string output))
        {
            return null;
        }
        return string.Join(" ", output.Trim().Split(' ').Take(2));
    }

    private static bool Run(string program, string arguments, out string output)
    {
        output = null;
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(program, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false
            };
            using Process process = Process.Start(startInfo);
            process.StandardInput.Close();
            output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
