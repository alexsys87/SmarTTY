using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// File transfers to and from a Windows host: the queue must use SFTP whatever protocol is selected (the SCP side asks the host
/// for stat, mv and tail), replace an existing file through a temporary one without shell tools, tell what a path is, and refuse
/// the folder archive, which needs tar on the host. The session of the test is the Unix test server marked as a Windows host, so
/// what is checked is what SmarTTY itself does; certutil, the only command it sends, does not exist there, and the checksum is
/// reported as not verified.
/// </summary>
internal static class WindowsTransferTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public static void Run(TestEnvironment environment)
    {
        Test.Section("File transfers to a Windows host (SFTP only, no shell tools)");
        SshSession session = new SshSession(environment.Parameters());
        string localDirectory = Path.Combine(Path.GetTempPath(), "smartty-wintransfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localDirectory);
        string remoteDirectory = environment.RemoteHome + "/smartty-wintransfer-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!session.Authenticate(environment.User, environment.Password))
            {
                throw new InvalidOperationException("Could not authenticate against the test server.");
            }
            session.RemoteOs = RemoteOsKind.Windows;
            RunChecks(session, remoteDirectory, localDirectory);
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

    private static byte[] ReadRemote(SftpChannel sftp, string path)
    {
        using Stream stream = sftp.OpenRead(path, 0);
        using MemoryStream buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void WaitCompleted(FileTransferManager.TransferJob job, string name)
    {
        Test.WaitUntil(() => job.State == FileTransferManager.TransferState.Completed || job.State == FileTransferManager.TransferState.Failed, Timeout, name);
        if (job.State != FileTransferManager.TransferState.Completed)
        {
            Test.Check(false, name + ": ended as " + job.State + " (" + job.ErrorMessage + ")");
        }
    }

    internal static void RunChecks(SshSession session, string remoteDirectory, string localDirectory)
    {
        Tools(session, remoteDirectory);
        Transfers(session, remoteDirectory, localDirectory);
    }

    private static void Tools(SshSession session, string remoteDirectory)
    {
        using SftpChannel sftp = SftpChannel.Open(session);
        SftpFileSystem fileSystem = new SftpFileSystem(sftp);
        string dir = remoteDirectory + "/tools";
        fileSystem.CreateDirectory(remoteDirectory);
        fileSystem.CreateDirectory(dir);
        Put(sftp, dir + "/a.txt", "aaaa");
        Put(sftp, dir + "/b.txt", "bb");

        RemoteFileInfo missing = WindowsRemoteFileTools.GetInfo(sftp, dir + "/nothing");
        Test.Check(missing != null && !missing.Exists, "a path that does not exist gives an answer that says so, not null");
        RemoteFileInfo file = WindowsRemoteFileTools.GetInfo(sftp, dir + "/a.txt");
        Test.Check(file.Exists && !file.IsDirectory && !file.IsSymbolicLink && file.Size == 4 && file.Owner == null, "a file: exists, its size, no owner over SFTP");
        Test.Check(WindowsRemoteFileTools.GetInfo(sftp, dir).IsDirectory, "a directory");

        WindowsRemoteFileTools.Replace(sftp, dir + "/b.txt", dir + "/a.txt");
        Test.Equal("bb", Text(sftp, dir + "/a.txt"), "Replace puts the new file in place of the old one");
        Test.Check(sftp.Lstat(dir + "/b.txt") == null && sftp.Lstat(dir + "/a.txt.smartty-old") == null, "the source is gone and no backup is left");
        Test.Throws<SftpStatusException>(() => WindowsRemoteFileTools.Replace(sftp, dir + "/does-not-exist", dir + "/a.txt"), "Replace with a missing source fails");
        Test.Equal("bb", Text(sftp, dir + "/a.txt"), "and the target is still there with its old content (it was moved aside and put back)");
        Test.Check(sftp.Lstat(dir + "/a.txt.smartty-old") == null, "without a backup left over");
        Put(sftp, dir + "/c.txt", "cc");
        WindowsRemoteFileTools.Replace(sftp, dir + "/c.txt", dir + "/new.txt");
        Test.Equal("cc", Text(sftp, dir + "/new.txt"), "Replace also works when there is nothing to replace");

        WindowsRemoteFileTools.Remove(sftp, dir + "/new.txt");
        Test.Check(sftp.Lstat(dir + "/new.txt") == null, "Remove deletes a file");
        WindowsRemoteFileTools.Remove(sftp, dir + "/new.txt");
        Test.Check(true, "and a file that is not there is not an error (rm -f)");

        RemotePathKind[] kinds = SftpFileSystem.PathKinds(sftp, new[] { dir + "/a.txt", dir, dir + "/nothing", dir + "/a.txt/under-a-file" });
        Test.Equal("File,Directory,Missing,Missing", string.Join(",", kinds), "the kinds of a file, a directory, a missing path and a path below a file");
    }

    private static void Transfers(SshSession session, string remoteDirectory, string localDirectory)
    {
        using FileTransferManager manager = new FileTransferManager { Session = session, Protocol = FileTransferProtocol.Scp, ConfirmOverwrite = message => true };
        Test.Equal("SFTP", manager.EffectiveProtocolName, "SCP is selected, but the queue shows SFTP for a Windows host");

        byte[] first = new byte[2 * 1024 * 1024 + 77];
        new Random(11).NextBytes(first);
        string localFirst = Path.Combine(localDirectory, "first.bin");
        File.WriteAllBytes(localFirst, first);
        string remote = remoteDirectory + "/uploaded.bin";
        FileTransferManager.TransferJob upload = manager.EnqueueUpload(localFirst, remote);
        WaitCompleted(upload, "an upload of 2 MB completes");
        Test.Equal("SFTP", upload.Protocol, "it went over SFTP although SCP was selected");
        using (SftpChannel sftp = SftpChannel.Open(session))
        {
            Test.Check(ReadRemote(sftp, remote).SequenceEqual(first), "the uploaded file is the same, byte for byte");
        }
        Test.Check(upload.Checksum != null && upload.Checksum.StartsWith("not verified"), "the checksum is reported as not verified where certutil is not there (not as verified, and not as a failure)");

        // An existing file is replaced through a temporary file and SFTP renames, and nothing is left beside it.
        byte[] second = Encoding.UTF8.GetBytes("a shorter second version");
        string localSecond = Path.Combine(localDirectory, "second.bin");
        File.WriteAllBytes(localSecond, second);
        FileTransferManager.TransferJob replace = manager.EnqueueUpload(localSecond, remote);
        WaitCompleted(replace, "an upload over an existing file completes");
        using (SftpChannel sftp = SftpChannel.Open(session))
        {
            Test.Check(ReadRemote(sftp, remote).SequenceEqual(second), "the existing file was replaced, not appended to");
            Test.Equal("uploaded.bin", string.Join(",", new SftpFileSystem(sftp).List(remoteDirectory).Where(entry => entry.Name.StartsWith("uploaded")).Select(entry => entry.Name)), "no temporary file and no backup is left in the directory");
        }

        manager.ConfirmOverwrite = message => false;
        Test.Check(manager.EnqueueUpload(localFirst, remote) == null, "an upload over an existing file that the user does not confirm is not queued");
        manager.ConfirmOverwrite = message => true;
        using (SftpChannel sftp = SftpChannel.Open(session))
        {
            Test.Check(ReadRemote(sftp, remote).SequenceEqual(second), "and the file is as it was");
        }
        Test.Throws<IOException>(() => manager.EnqueueUpload(localFirst, remoteDirectory), "an upload to a path that is a directory is refused (the kind of the path comes from SFTP)");

        string localDownload = Path.Combine(localDirectory, "downloaded.bin");
        FileTransferManager.TransferJob download = manager.EnqueueDownload(remote, localDownload);
        WaitCompleted(download, "a download completes");
        Test.Equal("SFTP", download.Protocol, "the download went over SFTP");
        Test.Check(File.ReadAllBytes(localDownload).SequenceEqual(second), "the downloaded file is the same");

        Test.Throws<IOException>(() => manager.EnqueueDirectoryArchiveDownload(remoteDirectory, Path.Combine(localDirectory, "folder.tar.gz")), "a folder cannot be downloaded as a tar.gz archive from a Windows host, and the message says why", exception => Test.Check(exception.Message.Contains("Windows"), "the message names Windows"));
        Test.Check(session.GetRemotePathKinds(new[] { remote, remoteDirectory, remoteDirectory + "/nothing" }).SequenceEqual(new[] { RemotePathKind.File, RemotePathKind.Directory, RemotePathKind.Missing }), "the kinds of paths are found without test -d / test -e");
    }

    private static void Put(SftpChannel sftp, string path, string text)
    {
        using Stream stream = sftp.OpenWrite(path, 0, truncate: true);
        byte[] data = Encoding.UTF8.GetBytes(text);
        stream.Write(data, 0, data.Length);
    }

    private static string Text(SftpChannel sftp, string path)
    {
        return Encoding.UTF8.GetString(ReadRemote(sftp, path));
    }
}
