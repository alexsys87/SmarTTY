using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Whole folders to and from a Windows host, where there is no tar and no find: the plan of the walk over SFTP and over the local
/// disk (<see cref="SftpFolderPlan"/>), "mkdir -p" over SFTP, and the folder going through the real transfer queue in both
/// directions, compared byte for byte.
/// </summary>
internal static class SftpFolderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public static void Run(TestEnvironment environment)
    {
        Test.Section("Folders to and from a Windows host (SFTP walk)");
        SshSession session = new SshSession(environment.Parameters());
        string localDirectory = Path.Combine(Path.GetTempPath(), "smartty-winfolder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localDirectory);
        string remoteDirectory = environment.RemoteHome + "/smartty-winfolder-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!session.Authenticate(environment.User, environment.Password))
            {
                throw new InvalidOperationException("Could not authenticate against the test server.");
            }
            session.RemoteOs = RemoteOsKind.Windows;
            RunChecks(session, remoteDirectory, localDirectory, (target, link) => session.RunCommand("ln -s '" + target + "' '" + link + "'", out _));
        }
        finally
        {
            try
            {
                using SftpChannel sftp = SftpChannel.Open(session);
                new SftpFileSystem(sftp).Delete(remoteDirectory);
            }
            catch
            {
            }
            session.Dispose();
            Directory.Delete(localDirectory, true);
        }
    }

    private static string Names(IEnumerable<string> paths, string root)
    {
        return string.Join("|", paths.Select(path => path.Substring(root.Length).Replace('\\', '/')).OrderBy(name => name, StringComparer.Ordinal));
    }

    private static void Wait(FileTransferManager.TransferJob job, string name)
    {
        Test.WaitUntil(() => job.State == FileTransferManager.TransferState.Completed || job.State == FileTransferManager.TransferState.Failed, Timeout, name);
        Test.Check(job.State == FileTransferManager.TransferState.Completed, name + ": completed (" + job.ErrorMessage + ")");
    }

    internal static void RunChecks(SshSession session, string remoteDirectory, string localDirectory, Action<string, string> makeRemoteLink)
    {
        using SftpChannel sftp = SftpChannel.Open(session);
        SftpFileSystem fileSystem = new SftpFileSystem(sftp);

        // "mkdir -p"
        fileSystem.CreateDirectories(remoteDirectory + "/a/b/c/d");
        Test.Check(sftp.Stat(remoteDirectory + "/a/b/c/d").IsDirectory, "CreateDirectories makes a directory with all the ones above it that are missing");
        fileSystem.CreateDirectories(remoteDirectory + "/a/b/c/d");
        fileSystem.CreateDirectories(remoteDirectory + "/a/b/");
        Test.Check(true, "and does nothing where it is all there (a trailing slash too)");
        Put(sftp, remoteDirectory + "/a/file", "x");
        Test.Throws<RemoteOperationException>(() => fileSystem.CreateDirectories(remoteDirectory + "/a/file"), "a file where the directory should be is reported");
        Test.Throws<RemoteOperationException>(() => fileSystem.CreateDirectories(remoteDirectory + "/a/file/below"), "and so is a file in the middle of the path");

        // The remote tree.
        string tree = remoteDirectory + "/tree";
        fileSystem.CreateDirectories(tree + "/sub/deeper");
        fileSystem.CreateDirectories(tree + "/empty");
        Put(sftp, tree + "/a.txt", "abc");
        Put(sftp, tree + "/\u0444\u0430\u0439\u043b.txt", "ru");
        byte[] big = new byte[700 * 1024 + 3];
        new Random(3).NextBytes(big);
        using (Stream stream = sftp.OpenWrite(tree + "/sub/b.bin", 0, truncate: true))
        {
            stream.Write(big, 0, big.Length);
        }
        Put(sftp, tree + "/sub/deeper/c.txt", "");
        makeRemoteLink(tree + "/a.txt", tree + "/link-file");
        makeRemoteLink(tree + "/sub", tree + "/link-dir");

        string localTarget = Path.Combine(localDirectory, "tree-copy");
        FolderPlan download = SftpFolderPlan.PlanDownload(sftp, tree, localTarget);
        Test.Equal("|/empty|/sub|/sub/deeper", Names(download.Directories, localTarget), "the plan of a download lists the folders, the empty one and the nested ones, the folder itself first");
        Test.Equal(localTarget, download.Directories[0], "the folder itself is the first to create");
        Test.Equal("/a.txt|/link-file|/sub/b.bin|/sub/deeper/c.txt|/\u0444\u0430\u0439\u043b.txt", Names(download.Files.Select(file => file.Destination), localTarget), "the files, the link to a file as a file, Cyrillic names included");
        Test.Check(download.Files.First(file => file.Destination.EndsWith("link-file")).Size == 3 && download.Files.First(file => file.Destination.EndsWith("b.bin")).Size == big.Length && download.TotalBytes == 3 + 3 + 2 + big.Length, "the sizes are those of the files (of the target for a link)");
        Test.Check(download.Skipped.Count == 1 && download.Skipped[0].Contains("link-dir") && download.Skipped[0].Contains("folder"), "a link to a folder is skipped, and the plan says so");
        Test.Check(download.Files.All(file => file.Source.StartsWith(tree + "/")), "every file is named by its path on the server");

        // The folder going through the queue.
        using (FileTransferManager manager = new FileTransferManager { Session = session, Protocol = FileTransferProtocol.Scp, ConfirmOverwrite = message => true })
        {
            foreach (string directory in download.Directories)
            {
                Directory.CreateDirectory(directory);
            }
            List<FileTransferManager.TransferJob> jobs = download.Files.Select(file => manager.EnqueueDownload(file.Source, file.Destination, null, false, file.Size)).ToList();
            foreach (FileTransferManager.TransferJob job in jobs)
            {
                Wait(job, "download of " + Path.GetFileName(job.LocalPath));
            }
            Test.Check(File.ReadAllBytes(Path.Combine(localTarget, "sub", "b.bin")).SequenceEqual(big) && Directory.Exists(Path.Combine(localTarget, "empty")) && new FileInfo(Path.Combine(localTarget, "sub", "deeper", "c.txt")).Length == 0, "the downloaded tree is the same as the one on the server (the 700 KB file, the empty folder and the empty file)");
            Test.Equal("ru", File.ReadAllText(Path.Combine(localTarget, "\u0444\u0430\u0439\u043b.txt")), "also the file with a Cyrillic name");

            // And back up again, from the local copy, to another place.
            string localSource = localTarget;
            string localLoop = null;
            try
            {
                localLoop = Path.Combine(localSource, "local-link");
                Directory.CreateSymbolicLink(localLoop, Path.Combine(localSource, "sub"));
            }
            catch
            {
                localLoop = null;
            }
            string upTarget = remoteDirectory + "/up/tree";
            FolderPlan upload = SftpFolderPlan.PlanUpload(localSource, upTarget);
            Test.Equal(upTarget, upload.Directories[0], "the folder itself is the first to create on the server");
            Test.Equal("|/empty|/sub|/sub/deeper", Names(upload.Directories, upTarget), "the plan of an upload lists the folders, the empty one included");
            Test.Equal(5, upload.Files.Count(file => !file.Source.EndsWith("local-link")) , "the files of the folder are in the plan");
            if (localLoop != null)
            {
                Test.Check(upload.Skipped.Count == 1 && upload.Skipped[0].Contains("local-link"), "a local folder that is a link is skipped and reported, so that a loop cannot make the walk endless");
            }
            foreach (string directory in upload.Directories)
            {
                fileSystem.CreateDirectories(directory);
            }
            List<FileTransferManager.TransferJob> uploads = upload.Files.Select(file => manager.EnqueueUpload(file.Source, file.Destination, null, false)).ToList();
            foreach (FileTransferManager.TransferJob job in uploads)
            {
                Wait(job, "upload of " + Path.GetFileName(job.LocalPath));
            }
            Test.Check(ReadRemote(sftp, upTarget + "/sub/b.bin").SequenceEqual(big) && sftp.Stat(upTarget + "/empty").IsDirectory && sftp.Stat(upTarget + "/sub/deeper/c.txt").Size == 0, "the uploaded tree is the same as the local one (the 700 KB file, the empty folder and the empty file)");
        }

        // A tree that is too deep is refused, with a reason that points at the likely cause.
        string deep = remoteDirectory + "/deep";
        string path = deep;
        for (int level = 0; level < SftpFolderPlan.MaxDepth + 3; level++)
        {
            path += "/d";
        }
        fileSystem.CreateDirectories(path);
        Test.Throws<RemoteOperationException>(() => SftpFolderPlan.PlanDownload(sftp, deep, Path.Combine(localDirectory, "deep")), "a folder nested deeper than the limit is refused", exception => Test.Check(exception.Message.Contains("junction"), "the message names the likely cause, a junction"));
        Test.Check(SftpFolderPlan.LocalName("a:b?c*d.txt") == "a_b_c_d.txt" || Path.DirectorySeparatorChar == '/', "characters that Windows does not allow in a file name are replaced when downloading");
    }

    private static byte[] ReadRemote(SftpChannel sftp, string path)
    {
        using Stream stream = sftp.OpenRead(path, 0);
        using MemoryStream buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Put(SftpChannel sftp, string path, string text)
    {
        using Stream stream = sftp.OpenWrite(path, 0, truncate: true);
        byte[] data = Encoding.UTF8.GetBytes(text);
        stream.Write(data, 0, data.Length);
    }
}
