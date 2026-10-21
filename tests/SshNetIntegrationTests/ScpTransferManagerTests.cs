using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SmarTTY.IO;
using SmarTTY.Transport;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Drives the SCP transfer queue behind the "SCP Transfers" panel (<see cref="ScpTransferManager"/>) against the live
/// server: what is left on both sides after a transfer completes, fails or is cancelled, pausing, and closing the queue.
/// </summary>
internal static class ScpTransferManagerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Big enough that a transfer over loopback is still running when the test pauses it.
    private const long LargeFileSize = 128L * 1024 * 1024;

    private static TestEnvironment _environment;

    private static SshSession _session;

    private static string _localDir;

    public static void Run(TestEnvironment environment)
    {
        _environment = environment;
        Test.Section("SCP transfer queue");
        _localDir = Path.Combine(Path.GetTempPath(), "smartty-scp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_localDir);
        _session = new SshSession(environment.Parameters());
        try
        {
            if (!_session.Authenticate(environment.User, environment.Password))
            {
                throw new InvalidOperationException("Could not authenticate against the test server.");
            }
            using (ScpTransferManager manager = new ScpTransferManager { Session = _session })
            {
                DownloadReplacesFile(manager);
                CancelledDownloadKeepsFile(manager);
                PausedQueueDoesNotStart(manager);
                CancelledUploadRemovesNewFile(manager);
                UploadKeepsPermissions(manager);
                UploadOntoDirectory(manager);
                Test.Throws<RemoteOperationException>(() => manager.EnqueueDownload(Remote("missing.bin"), Local("missing.bin")), "queueing the download of a missing file is refused");
            }
            DisposeFailsQueuedTransfers();
        }
        finally
        {
            _session.RunCommand("rm -rf " + RemotePathUtils.ShellQuote(Remote("")), out _);
            _session.Dispose();
            try
            {
                Directory.Delete(_localDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string Remote(string name)
    {
        return _environment.RemoteFile("smartty-queue/" + name);
    }

    private static string Local(string name)
    {
        return Path.Combine(_localDir, name);
    }

    private static void RunRemote(string command)
    {
        _session.RunCommand("mkdir -p " + RemotePathUtils.ShellQuote(Remote("")) + " && " + command, out _);
    }

    private static string RemoteStat(string path)
    {
        List<string> stat = _session.RunCommand("stat -c '%a %s' " + RemotePathUtils.ShellQuote(path), out _);
        return (stat != null && stat.Count == 1) ? stat[0] : string.Join(" ", stat ?? new List<string>());
    }

    private static void WaitUntilRunning(ScpTransferManager.TransferJob job)
    {
        Test.WaitUntil(() => job.TransferredBytes > 0 || job.State != ScpTransferManager.TransferState.Queued && job.State != ScpTransferManager.TransferState.InProgress, Timeout, "the transfer starts");
    }

    private static void DownloadReplacesFile(ScpTransferManager manager)
    {
        RunRemote("printf hello > " + RemotePathUtils.ShellQuote(Remote("hello.txt")) + " && touch -d '2024-01-02 03:04:05 UTC' " + RemotePathUtils.ShellQuote(Remote("hello.txt")));
        File.WriteAllText(Local("hello.txt"), "an older and longer local version");
        ScpTransferManager.TransferJob job = manager.EnqueueDownload(Remote("hello.txt"), Local("hello.txt"));
        Test.WaitUntil(() => job.State == ScpTransferManager.TransferState.Completed, Timeout, "a download completes");
        Test.Equal("hello", File.ReadAllText(Local("hello.txt")), "the downloaded file replaces the local one");
        Test.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Local("hello.txt")), "the download keeps the remote modification time");
        Test.Check(!File.Exists(Local("hello.txt") + ScpTransferManager.PartialDownloadSuffix), "no partial file is left after a download");
        Test.Equal(5L, job.TransferredBytes, "the progress reaches the file size");
    }

    private static void CancelledDownloadKeepsFile(ScpTransferManager manager)
    {
        RunRemote("head -c " + LargeFileSize + " /dev/zero > " + RemotePathUtils.ShellQuote(Remote("large.bin")));
        File.WriteAllText(Local("large.bin"), "original");
        ScpTransferManager.TransferJob job = manager.EnqueueDownload(Remote("large.bin"), Local("large.bin"));
        Test.Equal(LargeFileSize, job.TotalBytes, "the queued download knows the remote size");
        WaitUntilRunning(job);
        manager.IsPaused = true;
        try
        {
            Test.Equal(ScpTransferManager.TransferState.InProgress, job.State, "the download is paused while running");
            manager.Cancel(job);
            Test.WaitUntil(() => job.State == ScpTransferManager.TransferState.Cancelled, TimeSpan.FromSeconds(5), "a paused download can be cancelled without resuming it");
        }
        finally
        {
            manager.IsPaused = false;
        }
        Test.Equal("original", File.ReadAllText(Local("large.bin")), "a cancelled download leaves the existing local file untouched");
        Test.Check(!File.Exists(Local("large.bin") + ScpTransferManager.PartialDownloadSuffix), "a cancelled download leaves no partial file");
    }

    private static void PausedQueueDoesNotStart(ScpTransferManager manager)
    {
        File.WriteAllText(Local("queued.txt"), "queued");
        manager.IsPaused = true;
        try
        {
            ScpTransferManager.TransferJob download = manager.EnqueueDownload(Remote("hello.txt"), Local("queued-download.txt"));
            ScpTransferManager.TransferJob upload = manager.EnqueueUpload(Local("queued.txt"), Remote("queued.txt"), checkOverwrite: false);
            System.Threading.Thread.Sleep(700);
            Test.Equal(ScpTransferManager.TransferState.Queued, download.State, "a download queued while paused waits");
            Test.Equal(ScpTransferManager.TransferState.Queued, upload.State, "an upload queued while paused waits");
            manager.Cancel(download);
            Test.Equal(ScpTransferManager.TransferState.Cancelled, download.State, "a waiting download is cancelled at once");
            manager.IsPaused = false;
            Test.WaitUntil(() => upload.State == ScpTransferManager.TransferState.Completed, Timeout, "the waiting upload runs after resuming");
            Test.Check(!File.Exists(Local("queued-download.txt")), "the cancelled download created no file");
        }
        finally
        {
            manager.IsPaused = false;
        }
    }

    private static void CancelledUploadRemovesNewFile(ScpTransferManager manager)
    {
        using (FileStream stream = File.Create(Local("upload.bin")))
        {
            stream.SetLength(LargeFileSize);
        }
        string remote = Remote("upload.bin");
        ScpTransferManager.TransferJob job = manager.EnqueueUpload(Local("upload.bin"), remote);
        Test.Check(job != null, "an upload to a new file is queued without asking");
        WaitUntilRunning(job);
        manager.IsPaused = true;
        try
        {
            Test.Equal(ScpTransferManager.TransferState.InProgress, job.State, "the upload is paused while running");
            manager.Cancel(job);
            Test.WaitUntil(() => job.State == ScpTransferManager.TransferState.Cancelled, TimeSpan.FromSeconds(5), "a paused upload can be cancelled without resuming it");
        }
        finally
        {
            manager.IsPaused = false;
        }
        Test.Equal(RemotePathKind.Missing, _session.GetRemotePathKind(remote), "a cancelled upload removes the partial file it created");
    }

    private static void UploadKeepsPermissions(ScpTransferManager manager)
    {
        string remote = Remote("script.sh");
        RunRemote("printf old > " + RemotePathUtils.ShellQuote(remote) + " && chmod 755 " + RemotePathUtils.ShellQuote(remote));
        File.WriteAllText(Local("script.sh"), "#!/bin/sh\necho new\n");
        manager.ConfirmOverwrite = (string message) => false;
        Test.Check(manager.EnqueueUpload(Local("script.sh"), remote) == null, "declining to overwrite queues nothing");
        string question = null;
        manager.ConfirmOverwrite = (string message) =>
        {
            question = message;
            return true;
        };
        ScpTransferManager.TransferJob job = manager.EnqueueUpload(Local("script.sh"), remote);
        Test.Check(question != null && question.Contains(remote), "overwriting an existing remote file is confirmed first");
        Test.WaitUntil(() => job.State == ScpTransferManager.TransferState.Completed, Timeout, "the upload over an existing file completes");
        Test.Equal("755 19", RemoteStat(remote), "an uploaded script stays executable");
        manager.ConfirmOverwrite = null;
    }

    private static void UploadOntoDirectory(ScpTransferManager manager)
    {
        string remote = Remote("target");
        RunRemote("mkdir -p " + RemotePathUtils.ShellQuote(remote));
        File.WriteAllText(Local("target"), "a file named like a remote directory");
        Test.Throws<IOException>(() => manager.EnqueueUpload(Local("target"), remote), "an upload onto a remote directory is refused when queued");
        ScpTransferManager.TransferJob job = manager.EnqueueUpload(Local("target"), remote, checkOverwrite: false);
        Test.WaitUntil(() => job.State == ScpTransferManager.TransferState.Failed, Timeout, "an upload onto a remote directory fails");
        Test.Check(job.ErrorMessage != null && job.ErrorMessage.Contains("is a directory"), "the failure says why (actual: " + job.ErrorMessage + ")");
        Test.Equal(RemotePathKind.Missing, _session.GetRemotePathKind(remote + "/target"), "nothing is written into the directory");
    }

    private static void DisposeFailsQueuedTransfers()
    {
        File.WriteAllText(Local("pending.txt"), "pending");
        ScpTransferManager manager = new ScpTransferManager { Session = _session, IsPaused = true };
        ScpTransferManager.TransferJob job = manager.EnqueueUpload(Local("pending.txt"), Remote("pending.txt"), checkOverwrite: false);
        manager.Dispose();
        Test.Equal(ScpTransferManager.TransferState.Failed, job.State, "a transfer still queued when the session goes away is marked as failed");
        Test.Check(!string.IsNullOrEmpty(job.ErrorMessage), "and says why, so that it can be retried (actual: " + job.ErrorMessage + ")");
    }
}
