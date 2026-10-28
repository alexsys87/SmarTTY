using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Installing and removing a public key the way a Windows host needs it (<see cref="WindowsAuthorizedKeys"/>), over SFTP against
/// the live server: which file is used, that other lines and the line ending of the file survive, that nothing is added twice,
/// and that icacls (which only a Windows host has, so it is a recorded callback here) is asked for the file of the
/// administrators and only when it was written. A directory of the test stands for %ProgramData%.
/// </summary>
internal static class WindowsAuthorizedKeysTests
{
    public static void Run(TestEnvironment environment)
    {
        Test.Section("Public key on a Windows host (key files over SFTP)");
        SshSession session = new SshSession(environment.Parameters());
        try
        {
            if (!session.Authenticate(environment.User, environment.Password))
            {
                throw new InvalidOperationException("Could not authenticate against the test server.");
            }
            using SftpChannel sftp = SftpChannel.Open(session);
            RunChecks(sftp, environment.RemoteHome + "/smartty-winkeys-" + Guid.NewGuid().ToString("N"));
        }
        finally
        {
            session.Dispose();
        }
    }

    private static byte[] Blob(string type, byte seed)
    {
        List<byte> blob = new List<byte>();
        byte[] name = Encoding.ASCII.GetBytes(type);
        blob.AddRange(new byte[] { 0, 0, 0, (byte)name.Length });
        blob.AddRange(name);
        blob.AddRange(new byte[] { 0, 0, 0, 32 });
        blob.AddRange(Enumerable.Range(0, 32).Select(index => (byte)(seed + index)));
        return blob.ToArray();
    }

    private static string Line(string type, byte seed, string comment)
    {
        return type + " " + Convert.ToBase64String(Blob(type, seed)) + " " + comment;
    }

    internal static void RunChecks(SftpChannel sftp, string work)
    {
        SftpFileSystem fileSystem = new SftpFileSystem(sftp);
        fileSystem.CreateDirectory(work);
        try
        {
            FindFiles(sftp, fileSystem, work);
            UserFile(sftp, fileSystem, work);
            AdministratorsFile(sftp, fileSystem, work);
            Removal(sftp, fileSystem, work);
        }
        finally
        {
            fileSystem.Delete(work);
            Test.Check(sftp.Lstat(work) == null, "the working directory of the key tests is deleted");
        }
    }

    private static string Text(SftpChannel sftp, string path)
    {
        byte[] content = WindowsAuthorizedKeys.ReadFile(sftp, path);
        return content == null ? null : new UTF8Encoding(false).GetString(content);
    }

    private static void Put(SftpChannel sftp, string path, string text)
    {
        using Stream stream = sftp.OpenWrite(path, 0, truncate: true);
        byte[] data = new UTF8Encoding(false).GetBytes(text);
        stream.Write(data, 0, data.Length);
    }

    private static void FindFiles(SftpChannel sftp, SftpFileSystem fileSystem, string work)
    {
        string programData = work + "/pd-find";
        string home = sftp.RealPath(".");
        List<WindowsAuthorizedKeys.KeyFile> user = WindowsAuthorizedKeys.FindKeyFiles(sftp, isAdministrator: false, programData);
        Test.Check(user.Count == 1 && user[0].Path == home + "/.ssh/authorized_keys" && !user[0].RestrictAccess, "a user's key goes to .ssh/authorized_keys of the directory the SFTP server starts in");

        List<WindowsAuthorizedKeys.KeyFile> noDirectory = WindowsAuthorizedKeys.FindKeyFiles(sftp, isAdministrator: true, programData);
        Test.Check(noDirectory.Count == 1 && noDirectory[0].Path == home + "/.ssh/authorized_keys", "an administrator on a host without the ssh directory of ProgramData uses the file of the profile");

        fileSystem.CreateDirectory(programData);
        fileSystem.CreateDirectory(programData + "/ssh");
        List<WindowsAuthorizedKeys.KeyFile> noConfig = WindowsAuthorizedKeys.FindKeyFiles(sftp, isAdministrator: true, programData);
        Test.Check(noConfig.Count == 1 && noConfig[0].Path == programData + "/ssh/administrators_authorized_keys" && noConfig[0].RestrictAccess, "an unreadable sshd_config next to an ssh directory means the default: the administrators' file, to be restricted");

        Put(sftp, programData + "/ssh/sshd_config", "# Authentication:\nAuthorizedKeysFile .ssh/authorized_keys\n\nMatch Group administrators\n       AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys\n");
        List<WindowsAuthorizedKeys.KeyFile> standard = WindowsAuthorizedKeys.FindKeyFiles(sftp, isAdministrator: true, programData);
        Test.Check(standard.Count == 1 && standard[0].Path == programData + "/ssh/administrators_authorized_keys" && standard[0].RestrictAccess, "the standard sshd_config of Windows (CRLF-free here) names the administrators' file");
        Test.Check(WindowsAuthorizedKeys.FindKeyFiles(sftp, isAdministrator: false, programData)[0].Path == home + "/.ssh/authorized_keys", "and an ordinary user still uses the file of the profile");

        Put(sftp, programData + "/ssh/sshd_config", "AuthorizedKeysFile .ssh/authorized_keys\n#Match Group administrators\n#       AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys\n");
        List<WindowsAuthorizedKeys.KeyFile> commented = WindowsAuthorizedKeys.FindKeyFiles(sftp, isAdministrator: true, programData);
        Test.Check(commented.Count == 1 && commented[0].Path == home + "/.ssh/authorized_keys" && !commented[0].RestrictAccess, "with the Match block commented out the administrators use the file of the profile");

        Put(sftp, programData + "/ssh/sshd_config", "Match Group administrators\n    AuthorizedKeysFile D:/keys/admins\nMatch User bob\n    AuthorizedKeysFile bob_keys\n");
        List<WindowsAuthorizedKeys.KeyFile> custom = WindowsAuthorizedKeys.FindKeyFiles(sftp, isAdministrator: true, programData);
        Test.Check(custom.Count == 1 && custom[0].Path == "/D:/keys/admins" && custom[0].RestrictAccess, "a custom path in the Match block is used (a drive path in SFTP form)");
    }

    private static void UserFile(SftpChannel sftp, SftpFileSystem fileSystem, string work)
    {
        string home = work + "/profile";
        fileSystem.CreateDirectory(home);
        List<WindowsAuthorizedKeys.KeyFile> files = new List<WindowsAuthorizedKeys.KeyFile> { new WindowsAuthorizedKeys.KeyFile { Path = home + "/.ssh/authorized_keys" } };
        List<string> restricted = new List<string>();
        string first = Line("ssh-ed25519", 1, "me@pc");
        byte[] firstBlob = Blob("ssh-ed25519", 1);

        WindowsAuthorizedKeys.AppendToFiles(sftp, files, first, firstBlob, restricted.Add);
        Test.Equal(first + "\n", Text(sftp, home + "/.ssh/authorized_keys"), "the .ssh directory and the file are created, with the key line");
        Test.Check(restricted.Count == 0, "the file of a user is not restricted with icacls");
        WindowsAuthorizedKeys.AppendToFiles(sftp, files, first, firstBlob, restricted.Add);
        Test.Equal(first + "\n", Text(sftp, home + "/.ssh/authorized_keys"), "the same key is not added twice");
        Test.Check(!fileSystem.List(home + "/.ssh").Any(entry => entry.Name.EndsWith(".smartty")), "no temporary file is left beside the key file");

        string second = Line("ssh-rsa", 9, "other@laptop");
        WindowsAuthorizedKeys.AppendToFiles(sftp, files, second, Blob("ssh-rsa", 9), restricted.Add);
        Test.Equal(first + "\n" + second + "\n", Text(sftp, home + "/.ssh/authorized_keys"), "another key goes below it");
        Test.Check(WindowsAuthorizedKeys.FileHoldsKey(sftp, home + "/.ssh/authorized_keys", firstBlob) && WindowsAuthorizedKeys.FileHoldsKey(sftp, home + "/.ssh/authorized_keys", Blob("ssh-rsa", 9)) && !WindowsAuthorizedKeys.FileHoldsKey(sftp, home + "/.ssh/authorized_keys", Blob("ssh-ed25519", 77)), "both keys are found and a third one is not");

        // A file written by Notepad: CRLF, comments, no newline at the end.
        string crlfPath = home + "/crlf_keys";
        string other = Line("ssh-ed25519", 5, "old");
        Put(sftp, crlfPath, "# my keys\r\n\r\n" + other);
        WindowsAuthorizedKeys.AppendToFiles(sftp, new List<WindowsAuthorizedKeys.KeyFile> { new WindowsAuthorizedKeys.KeyFile { Path = crlfPath } }, first, firstBlob, restricted.Add);
        Test.Equal("# my keys\r\n\r\n" + other + "\r\n" + first + "\r\n", Text(sftp, crlfPath), "a CRLF file keeps its line ending, its comment and its last line that had no newline");

        string emptyPath = home + "/empty_keys";
        Put(sftp, emptyPath, "");
        WindowsAuthorizedKeys.AppendToFiles(sftp, new List<WindowsAuthorizedKeys.KeyFile> { new WindowsAuthorizedKeys.KeyFile { Path = emptyPath } }, first, firstBlob, restricted.Add);
        Test.Equal(first + "\n", Text(sftp, emptyPath), "an empty file gets the key line alone");

        Test.Check(WindowsAuthorizedKeys.ReadFile(sftp, home + "/missing") == null, "a missing file reads as null");
        fileSystem.CreateFile(home + "/a-file");
        Test.Throws<RemoteOperationException>(() => WindowsAuthorizedKeys.EnsureDirectory(sftp, home + "/a-file"), "a file where the .ssh directory should be is reported");
    }

    private static void AdministratorsFile(SftpChannel sftp, SftpFileSystem fileSystem, string work)
    {
        string programData = work + "/pd-admin";
        fileSystem.CreateDirectory(programData);
        fileSystem.CreateDirectory(programData + "/ssh");
        string path = programData + "/ssh/administrators_authorized_keys";
        List<WindowsAuthorizedKeys.KeyFile> files = new List<WindowsAuthorizedKeys.KeyFile> { new WindowsAuthorizedKeys.KeyFile { Path = path, RestrictAccess = true } };
        List<string> restricted = new List<string>();
        string key = Line("ssh-ed25519", 3, "admin@pc");
        byte[] blob = Blob("ssh-ed25519", 3);

        WindowsAuthorizedKeys.AppendToFiles(sftp, files, key, blob, restricted.Add);
        Test.Equal(key + "\n", Text(sftp, path), "the key is written to the file of the administrators");
        Test.Check(restricted.SequenceEqual(new[] { path }), "and the access to that file is restricted after it was written");
        WindowsAuthorizedKeys.AppendToFiles(sftp, files, key, blob, restricted.Add);
        Test.Check(restricted.Count == 1, "a key that is already there writes nothing and restricts nothing again");
        Test.Throws<SftpStatusException>(() => WindowsAuthorizedKeys.AppendToFiles(sftp, new List<WindowsAuthorizedKeys.KeyFile> { new WindowsAuthorizedKeys.KeyFile { Path = programData + "/no-ssh-dir/sub/keys" } }, key, blob, restricted.Add), "a file in a directory that cannot be created is an error, not a silent success");
    }

    private static void Removal(SftpChannel sftp, SftpFileSystem fileSystem, string work)
    {
        string path = work + "/removal";
        fileSystem.CreateDirectory(path);
        string keys = path + "/keys";
        string mine = Line("ssh-ed25519", 1, "me@pc");
        string theirs = Line("ssh-rsa", 2, "them@pc");
        byte[] mineBlob = Blob("ssh-ed25519", 1);
        List<WindowsAuthorizedKeys.KeyFile> files = new List<WindowsAuthorizedKeys.KeyFile> { new WindowsAuthorizedKeys.KeyFile { Path = keys, RestrictAccess = true } };
        List<string> restricted = new List<string>();

        Put(sftp, keys, "# keys\r\n" + mine + "\r\n" + theirs + "\r\n\r\n");
        WindowsAuthorizedKeys.RemoveFromFiles(sftp, files, mineBlob, restricted.Add);
        Test.Equal("# keys\r\n" + theirs + "\r\n\r\n", Text(sftp, keys), "only the lines of that key are removed; comments, other keys and blank lines stay, and so does CRLF");
        Test.Check(restricted.SequenceEqual(new[] { keys }), "the access is restricted again after the file was rewritten");
        WindowsAuthorizedKeys.RemoveFromFiles(sftp, files, mineBlob, restricted.Add);
        Test.Check(restricted.Count == 1 && Text(sftp, keys) == "# keys\r\n" + theirs + "\r\n\r\n", "removing a key that is not there changes nothing and restricts nothing");
        WindowsAuthorizedKeys.RemoveFromFiles(sftp, new List<WindowsAuthorizedKeys.KeyFile> { new WindowsAuthorizedKeys.KeyFile { Path = path + "/never-existed" } }, mineBlob, restricted.Add);
        Test.Check(WindowsAuthorizedKeys.ReadFile(sftp, path + "/never-existed") == null, "removing from a missing file creates nothing");
        Put(sftp, keys, mine + "\n");
        WindowsAuthorizedKeys.RemoveFromFiles(sftp, files, mineBlob, restricted.Add);
        Test.Equal("", Text(sftp, keys), "a file that held only that key is left empty");

        // Many keys: the whole file is rewritten, and nothing of the others is lost.
        StringBuilder many = new StringBuilder();
        for (int index = 0; index < 3000; index++)
        {
            many.Append(Line("ssh-ed25519", (byte)(index % 200 + 20), "user" + index)).Append('\n');
        }
        Put(sftp, keys, many.ToString() + mine + "\n");
        WindowsAuthorizedKeys.RemoveFromFiles(sftp, files, mineBlob, restricted.Add);
        Test.Equal(many.ToString(), Text(sftp, keys), "a file of 3000 keys loses exactly one line");
    }
}
