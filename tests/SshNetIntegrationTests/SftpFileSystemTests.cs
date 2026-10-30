using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// The SFTP operations behind the file list of a Windows host (<see cref="SftpFileSystem"/>): listing, creating, moving,
/// copying and deleting, run against the live server. The server is a Unix one, but SFTP is the same protocol the
/// Win32-OpenSSH server speaks, so what is checked here is the client's side of it: a listing that takes many READDIR
/// requests, a recursive delete that must not follow links, a copy of a big file on one channel, names that are not ASCII.
/// </summary>
internal static class SftpFileSystemTests
{
    public static void Run(TestEnvironment environment)
    {
        Test.Section("SFTP file system (file list of a Windows host)");
        SshSession session = new SshSession(environment.Parameters());
        try
        {
            if (!session.Authenticate(environment.User, environment.Password))
            {
                throw new InvalidOperationException("Could not authenticate against the test server.");
            }
            using SftpChannel sftp = SftpChannel.Open(session);
            string workDirectory = environment.RemoteHome + "/smartty-sftpfs-" + Guid.NewGuid().ToString("N");
            RunChecks(sftp, workDirectory, (target, link) => session.RunCommand("ln -s " + RemotePathUtilsQuote(target) + " " + RemotePathUtilsQuote(link), out _));
        }
        finally
        {
            session.Dispose();
        }
    }

    private static string RemotePathUtilsQuote(string path)
    {
        return "'" + path.Replace("'", "'\\''") + "'";
    }

    /// <summary>The checks themselves; <paramref name="makeLink"/> creates a symbolic link (target, link) or is null to skip the checks with links.</summary>
    internal static void RunChecks(SftpChannel sftp, string workDirectory, Action<string, string> makeLink)
    {
        SftpFileSystem fileSystem = new SftpFileSystem(sftp);
        Test.Check(fileSystem.HomeDirectory().StartsWith("/"), "the start directory of the server is an absolute path");
        Test.Equal(0, fileSystem.ListDrives().Count, "a Unix server has no drives, and asking for 24 of them is no error");
        fileSystem.CreateDirectory(workDirectory);
        try
        {
            ListAndCreate(sftp, fileSystem, workDirectory);
            MoveChecks(sftp, fileSystem, workDirectory);
            CopyChecks(sftp, fileSystem, workDirectory);
            DeleteChecks(sftp, fileSystem, workDirectory);
            BigListing(sftp, fileSystem, workDirectory);
            if (makeLink != null)
            {
                LinkChecks(sftp, fileSystem, workDirectory, makeLink);
            }
        }
        finally
        {
            fileSystem.Delete(workDirectory);
            Test.Check(sftp.Lstat(workDirectory) == null, "the working directory of the test is deleted with everything in it");
        }
    }

    private static void ListAndCreate(SftpChannel sftp, SftpFileSystem fileSystem, string work)
    {
        string dir = work + "/list";
        fileSystem.CreateDirectory(dir);
        fileSystem.CreateDirectory(dir + "/sub");
        fileSystem.CreateFile(dir + "/a.txt");
        WriteText(sftp, dir + "/b.txt", "hello");
        WriteText(sftp, dir + "/\u0444\u0430\u0439\u043b \u0441 \u043f\u0440\u043e\u0431\u0435\u043b\u043e\u043c.txt", "x");
        List<SftpDirectoryEntry> entries = fileSystem.List(dir);
        Test.Equal("a.txt|b.txt|sub|\u0444\u0430\u0439\u043b \u0441 \u043f\u0440\u043e\u0431\u0435\u043b\u043e\u043c.txt", string.Join("|", entries.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal)), "the listing has the files and the folder, and neither \".\" nor \"..\" (names with spaces and Cyrillic included)");
        Test.Check(entries.First(entry => entry.Name == "sub").Attributes.IsDirectory, "a folder is a directory in the listing");
        SftpFileAttributes b = entries.First(entry => entry.Name == "b.txt").Attributes;
        Test.Check(b.IsRegularFile && b.Size == 5, "a file has its size and type in the listing");
        Test.Equal(0L, entries.First(entry => entry.Name == "a.txt").Attributes.Size ?? -1, "\"Create file\" makes an empty file");
        Test.Throws<SftpStatusException>(() => fileSystem.CreateFile(dir + "/b.txt"), "\"Create file\" does not overwrite an existing file");
        Test.Check(ReadText(sftp, dir + "/b.txt") == "hello", "and the existing file is untouched");
        Test.Throws<SftpStatusException>(() => fileSystem.CreateDirectory(dir + "/sub"), "\"Create folder\" fails where the name is taken");
        Test.Throws<SftpStatusException>(() => fileSystem.List(dir + "/missing"), "listing a missing directory fails with the status of the server", exception => Test.Equal(SftpChannel.StatusNoSuchFile, exception.Status, "the status is \"no such file\""));
        Test.Throws<SftpStatusException>(() => sftp.RemoveDirectory(dir), "rmdir does not remove a folder with files in it");
        sftp.MakeDirectory(dir + "/private", Convert.ToInt32("700", 8));
        Test.Equal(Convert.ToInt32("700", 8), sftp.Stat(dir + "/private").Mode.Value & 0xFFF, "a folder is created with the permissions that were asked for");
        Test.Equal(1, fileSystem.List(dir).Count(entry => entry.Name == "private"), "and it is in the listing");
    }

    private static void MoveChecks(SftpChannel sftp, SftpFileSystem fileSystem, string work)
    {
        string dir = work + "/move";
        fileSystem.CreateDirectory(dir);
        WriteText(sftp, dir + "/one.txt", "1");
        WriteText(sftp, dir + "/two.txt", "2");
        fileSystem.Move(dir + "/one.txt", dir + "/renamed.txt", replace: false);
        Test.Check(sftp.Stat(dir + "/one.txt") == null && ReadText(sftp, dir + "/renamed.txt") == "1", "a file is renamed");
        Test.Throws<SftpStatusException>(() => fileSystem.Move(dir + "/renamed.txt", dir + "/two.txt", replace: false), "a move onto an existing file fails without \"replace\"");
        Test.Check(ReadText(sftp, dir + "/two.txt") == "2" && ReadText(sftp, dir + "/renamed.txt") == "1", "and both files are still there");
        fileSystem.Move(dir + "/renamed.txt", dir + "/two.txt", replace: true);
        Test.Check(ReadText(sftp, dir + "/two.txt") == "1" && sftp.Stat(dir + "/renamed.txt") == null, "with \"replace\" the target is replaced");
        fileSystem.CreateDirectory(dir + "/folder");
        WriteText(sftp, dir + "/folder/inside.txt", "in");
        fileSystem.CreateDirectory(dir + "/other");
        fileSystem.Move(dir + "/folder", dir + "/other/moved", replace: false);
        Test.Check(ReadText(sftp, dir + "/other/moved/inside.txt") == "in" && sftp.Stat(dir + "/folder") == null, "a folder is moved with what is in it");
    }

    private static void CopyChecks(SftpChannel sftp, SftpFileSystem fileSystem, string work)
    {
        string dir = work + "/copy";
        fileSystem.CreateDirectory(dir);
        byte[] big = new byte[3 * 1024 * 1024 + 123];
        new Random(7).NextBytes(big);
        WriteBytes(sftp, dir + "/big.bin", big);
        fileSystem.Copy(dir + "/big.bin", dir + "/big (copy).bin", replace: false);
        Test.Check(ReadBytes(sftp, dir + "/big (copy).bin").SequenceEqual(big), "a file of 3 MB is copied byte for byte over one channel");
        Test.Check(ReadBytes(sftp, dir + "/big.bin").SequenceEqual(big), "and the original is intact");
        WriteText(sftp, dir + "/small.txt", "small");
        Test.Throws<SftpStatusException>(() => fileSystem.Copy(dir + "/small.txt", dir + "/big.bin", replace: false), "a copy onto an existing file fails without \"replace\" and leaves it alone");
        Test.Check(ReadBytes(sftp, dir + "/big.bin").SequenceEqual(big), "the existing file was not touched by the failed copy");
        fileSystem.Copy(dir + "/small.txt", dir + "/big.bin", replace: true);
        Test.Check(ReadText(sftp, dir + "/big.bin") == "small", "with \"replace\" the existing file is replaced");
        // A folder tree with an empty file, an empty folder and a script that must stay executable.
        fileSystem.CreateDirectory(dir + "/tree");
        fileSystem.CreateDirectory(dir + "/tree/empty");
        fileSystem.CreateDirectory(dir + "/tree/deep");
        fileSystem.CreateDirectory(dir + "/tree/deep/deeper");
        fileSystem.CreateFile(dir + "/tree/deep/zero.txt");
        WriteText(sftp, dir + "/tree/deep/deeper/script.sh", "#!/bin/sh\n", Convert.ToInt32("755", 8));
        fileSystem.Copy(dir + "/tree", dir + "/tree2", replace: false);
        Test.Equal("deep|empty", string.Join("|", fileSystem.List(dir + "/tree2").Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal)), "a folder is copied with its subfolders");
        Test.Check(ReadText(sftp, dir + "/tree2/deep/deeper/script.sh") == "#!/bin/sh\n", "files deep in the tree are copied");
        Test.Equal(0L, sftp.Stat(dir + "/tree2/deep/zero.txt").Size ?? -1, "an empty file is copied as an empty file");
        Test.Equal(Convert.ToInt32("755", 8), sftp.Stat(dir + "/tree2/deep/deeper/script.sh").Mode.Value & 0xFFF, "the permissions of a copied file are kept");
        WriteText(sftp, dir + "/tree2/stale.txt", "stale");
        fileSystem.Copy(dir + "/tree", dir + "/tree2", replace: true);
        Test.Check(sftp.Stat(dir + "/tree2/stale.txt") == null && sftp.Stat(dir + "/tree2/deep/zero.txt") != null, "copying a folder over a folder with \"replace\" deletes the old one first");
    }

    private static void DeleteChecks(SftpChannel sftp, SftpFileSystem fileSystem, string work)
    {
        string dir = work + "/delete";
        fileSystem.CreateDirectory(dir);
        WriteText(sftp, dir + "/file.txt", "x");
        fileSystem.Delete(dir + "/file.txt");
        Test.Check(sftp.Stat(dir + "/file.txt") == null, "a file is deleted");
        fileSystem.Delete(dir + "/never-existed");
        Test.Check(true, "deleting what is already gone is not an error");
        fileSystem.CreateDirectory(dir + "/empty");
        fileSystem.Delete(dir + "/empty");
        Test.Check(sftp.Stat(dir + "/empty") == null, "an empty folder is deleted");
        fileSystem.CreateDirectory(dir + "/tree");
        for (int level = 0; level < 4; level++)
        {
            string path = dir + "/tree" + string.Concat(Enumerable.Repeat("/d", level + 1));
            fileSystem.CreateDirectory(path);
            WriteText(sftp, path + "/f1.txt", "1");
            WriteText(sftp, path + "/f2.txt", "2");
        }
        fileSystem.Delete(dir + "/tree");
        Test.Check(sftp.Stat(dir + "/tree") == null, "a folder with 4 levels and 8 files is deleted with everything in it");
    }

    private static void BigListing(SftpChannel sftp, SftpFileSystem fileSystem, string work)
    {
        string dir = work + "/big";
        fileSystem.CreateDirectory(dir);
        const int count = 1500;
        for (int index = 0; index < count; index++)
        {
            fileSystem.CreateFile(dir + "/file-" + index.ToString("D5") + ".txt");
        }
        List<SftpDirectoryEntry> entries = fileSystem.List(dir);
        Test.Equal(count, entries.Count, "a directory of " + count + " files is listed completely (many READDIR requests)");
        Test.Equal(count, entries.Select(entry => entry.Name).Distinct().Count(), "and no entry comes twice");
        fileSystem.Delete(dir);
        Test.Check(sftp.Stat(dir) == null, "and it is deleted");
    }

    private static void LinkChecks(SftpChannel sftp, SftpFileSystem fileSystem, string work, Action<string, string> makeLink)
    {
        string dir = work + "/links";
        fileSystem.CreateDirectory(dir);
        fileSystem.CreateDirectory(dir + "/target");
        WriteText(sftp, dir + "/target/precious.txt", "keep me");
        WriteText(sftp, dir + "/plain.txt", "plain");
        makeLink(dir + "/target", dir + "/dirlink");
        makeLink(dir + "/plain.txt", dir + "/filelink");
        Test.Check(sftp.Lstat(dir + "/dirlink").IsSymbolicLink && sftp.Stat(dir + "/dirlink").IsDirectory, "Lstat describes a link itself, Stat what it points to");
        Test.Check(fileSystem.List(dir).First(entry => entry.Name == "dirlink").Attributes.IsSymbolicLink, "the listing reports a link as a link");
        Test.Throws<RemoteOperationException>(() => fileSystem.Copy(dir + "/dirlink", dir + "/dirlink-copy", replace: false), "a link to a folder is not copied");
        fileSystem.Copy(dir + "/filelink", dir + "/filelink-copy", replace: false);
        Test.Check(ReadText(sftp, dir + "/filelink-copy") == "plain" && sftp.Lstat(dir + "/filelink-copy").IsRegularFile, "a link to a file is copied as the file it points to");
        fileSystem.Delete(dir + "/dirlink");
        Test.Check(sftp.Lstat(dir + "/dirlink") == null && ReadText(sftp, dir + "/target/precious.txt") == "keep me", "deleting a link to a folder removes the link and leaves what it points to");
        makeLink(dir + "/target", dir + "/target/loop");
        fileSystem.Delete(dir);
        Test.Check(sftp.Lstat(dir) == null, "a folder with a link to a folder inside (even one that loops) is deleted without following the link");
    }

    private static void WriteText(SftpChannel sftp, string path, string text, int mode = 0x1A4)
    {
        WriteBytes(sftp, path, new UTF8Encoding(false).GetBytes(text), mode);
    }

    private static void WriteBytes(SftpChannel sftp, string path, byte[] data, int mode = 0x1A4)
    {
        using Stream stream = sftp.OpenWrite(path, 0, truncate: true, mode: mode);
        stream.Write(data, 0, data.Length);
    }

    private static string ReadText(SftpChannel sftp, string path)
    {
        return new UTF8Encoding(false).GetString(ReadBytes(sftp, path));
    }

    private static byte[] ReadBytes(SftpChannel sftp, string path)
    {
        using Stream stream = sftp.OpenRead(path, 0);
        using MemoryStream buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
