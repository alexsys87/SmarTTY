using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SmarTTY.IO;
using SmarTTY.Transport;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Drives the transfer queue behind the "File Transfers" panel (<see cref="FileTransferManager"/>) and the SFTP client
/// against the live server, over SCP and over SFTP: what is left on both sides after a transfer completes, fails or is
/// cancelled, pausing, resuming, checksums, replacing a file through a temporary one, parallel transfers, the speed
/// limit and the saved queue.
/// </summary>
internal static class FileTransferManagerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Big enough that a transfer is still running when a test pauses it: the tests that need that also slow the
    // transfer down with the speed limit, so that it does not depend on how fast the machine is.
    private const long LargeFileSize = 16L * 1024 * 1024;

    private const long SlowRate = 4L * 1024 * 1024;

    private static TestEnvironment _environment;

    private static SshSession _session;

    private static string _localDir;

    private static FileTransferProtocol _protocol;

    public static void Run(TestEnvironment environment)
    {
        _environment = environment;
        _localDir = Path.Combine(Path.GetTempPath(), "smartty-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_localDir);
        _session = new SshSession(environment.Parameters());
        try
        {
            if (!_session.Authenticate(environment.User, environment.Password))
            {
                throw new InvalidOperationException("Could not authenticate against the test server.");
            }
            SftpClientChecks();
            SavedQueueRoundTrip();
            foreach (FileTransferProtocol protocol in new[] { FileTransferProtocol.Scp, FileTransferProtocol.Sftp })
            {
                _protocol = protocol;
                Test.Section("File transfer queue over " + protocol.ToString().ToUpperInvariant());
                using (FileTransferManager manager = NewManager())
                {
                    DownloadReplacesFile(manager);
                    CancelledDownloadKeepsFile(manager);
                    PausedQueueDoesNotStart(manager);
                    CancelledUploadRemovesNewFile(manager);
                    UploadKeepsPermissions(manager);
                    UploadReplacesOnlyWhenComplete(manager);
                    UploadOntoDirectory(manager);
                    Test.Throws<RemoteOperationException>(() => manager.EnqueueDownload(Remote("missing.bin"), Local("missing.bin")), "queueing the download of a missing file is refused");
                    ParallelTransfers(manager);
                    SpeedLimit(manager);
                }
                ResumeDownload();
                ResumeUpload();
                DisposeFailsQueuedTransfers();
            }
            RunScriptOnServer();
            RunScriptInPlace();
            ProcessListOnServer();
            Test.Section("Folder download as .tar.gz");
            using (FileTransferManager archiveManager = NewManager())
            {
                ArchiveDownload(archiveManager);
            }
            SftpFallback();
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

    private static FileTransferManager NewManager()
    {
        return new FileTransferManager { Session = _session, Protocol = _protocol };
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

    private static string RemoteHead(string path, int bytes)
    {
        return string.Join("", _session.RunCommand("head -c " + bytes + " " + RemotePathUtils.ShellQuote(path), out _) ?? new List<string>());
    }

    private static void CreateLocalFile(string path, long size)
    {
        byte[] block = new byte[65536];
        new Random(42).NextBytes(block);
        using FileStream stream = File.Create(path);
        for (long done = 0; done < size; done += block.Length)
        {
            stream.Write(block, 0, (int)Math.Min(block.Length, size - done));
        }
    }

    private static string Label(string text)
    {
        return text + " (" + _protocol.ToString().ToUpperInvariant() + ")";
    }

    private static void WaitUntilRunning(FileTransferManager.TransferJob job)
    {
        Test.WaitUntil(() => job.TransferredBytes > job.ResumedFrom || job.State != FileTransferManager.TransferState.Queued && job.State != FileTransferManager.TransferState.InProgress, Timeout, Label("the transfer starts"));
    }

    private static void WaitForState(FileTransferManager.TransferJob job, FileTransferManager.TransferState state, string name)
    {
        Test.WaitUntil(() => job.State == state || job.State == FileTransferManager.TransferState.Failed && state != FileTransferManager.TransferState.Failed, Timeout, Label(name));
        if (job.State != state)
        {
            Test.Check(false, Label(name + ": ended as " + job.State + " (" + job.ErrorMessage + ")"));
        }
    }

    private static void SftpClientChecks()
    {
        Test.Section("SFTP client");
        RunRemote("printf 'sftp data' > " + RemotePathUtils.ShellQuote(Remote("sftp.txt")) + " && chmod 640 " + RemotePathUtils.ShellQuote(Remote("sftp.txt")));
        using SftpChannel sftp = SftpChannel.Open(_session);
        Test.Check(sftp.ServerVersion >= 3, "the server speaks SFTP version 3 or later (actual: " + sftp.ServerVersion + ")");
        SftpFileAttributes attributes = sftp.Stat(Remote("sftp.txt"));
        Test.Check(attributes != null && attributes.Size == 9 && attributes.IsRegularFile && (attributes.Mode & 0xFFF) == 0x1A0, "stat reports size, type and permissions");
        Test.Check(sftp.Stat(Remote("missing")) == null, "stat of a missing file returns null");
        Test.Check(sftp.Stat(Remote("")).IsDirectory, "stat of a directory reports a directory");
        Test.Equal(_environment.RemoteHome, sftp.RealPath("."), "realpath resolves the start directory");

        byte[] payload = new byte[200000 + 123];
        new Random(7).NextBytes(payload);
        using (Stream write = sftp.OpenWrite(Remote("sftp.bin"), 0, truncate: true))
        {
            write.Write(payload, 0, 100000);
            write.Write(payload, 100000, payload.Length - 100000);
        }
        Test.Equal((long)payload.Length, sftp.Stat(Remote("sftp.bin")).Size, "a pipelined write stores every byte");
        using (Stream read = sftp.OpenRead(Remote("sftp.bin"), 0))
        {
            Test.Check(ReadAll(read).SequenceEqual(payload), "a pipelined read returns the same bytes");
        }
        using (Stream read = sftp.OpenRead(Remote("sftp.bin"), 150000))
        {
            Test.Check(ReadAll(read).SequenceEqual(payload.Skip(150000)), "a read from an offset returns the rest of the file");
        }
        using (Stream write = sftp.OpenWrite(Remote("sftp.bin"), 3, truncate: false))
        {
            write.Write(new byte[] { 1, 2 }, 0, 2);
        }
        Test.Equal((long)payload.Length, sftp.Stat(Remote("sftp.bin")).Size, "writing at an offset without truncating keeps the size");
        sftp.Rename(Remote("sftp.bin"), Remote("sftp-renamed.bin"));
        Test.Check(sftp.Stat(Remote("sftp.bin")) == null && sftp.Stat(Remote("sftp-renamed.bin")) != null, "rename moves the file");
        sftp.Remove(Remote("sftp-renamed.bin"));
        Test.Check(sftp.Stat(Remote("sftp-renamed.bin")) == null, "remove deletes the file");
        Test.Throws<SftpStatusException>(() => sftp.OpenRead(Remote("missing"), 0), "opening a missing file fails", ex => Test.Equal(SftpChannel.StatusNoSuchFile, ex.Status, "with the no-such-file status"));
        Test.Check(sftp.Stat(Remote("sftp.txt")) != null, "the channel still works after an error");
    }

    private static byte[] ReadAll(Stream stream)
    {
        using MemoryStream memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void SavedQueueRoundTrip()
    {
        Test.Section("Saved transfer queue");
        string path = TransferQueueStore.GetFilePath(Path.Combine(_localDir, "queues"), "user", "host:1/x", 22);
        Test.Check(Path.GetFileName(path) == "user@host_1_x_22.xml", "the file name is built from the connection (actual: " + Path.GetFileName(path) + ")");
        DateTime remoteTime = new DateTime(2024, 3, 4, 5, 6, 7);
        DateTime localTime = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        List<FileTransferManager.TransferJob> jobs = new List<FileTransferManager.TransferJob>
        {
            new FileTransferManager.TransferJob { IsDownload = true, RemotePath = "/r/a", LocalPath = "C:\\a", State = FileTransferManager.TransferState.InProgress, TotalBytes = 10, TransferredBytes = 4, RemoteSize = 10, RemoteModifyTime = remoteTime, OpenFolderWhenDone = true, IsArchive = true },
            new FileTransferManager.TransferJob { IsDownload = false, RemotePath = "/r/b", LocalPath = "C:\\b", State = FileTransferManager.TransferState.Failed, ErrorMessage = "boom", LocalSize = 5, LocalModifyTime = localTime, RemoteTempPath = "/r/.b.smartty-part" },
            new FileTransferManager.TransferJob { IsDownload = false, RemotePath = "/r/c", LocalPath = "C:\\c", State = FileTransferManager.TransferState.Completed }
        };
        TransferQueueStore.Save(path, jobs);
        List<FileTransferManager.TransferJob> loaded = TransferQueueStore.Load(path);
        Test.Equal(2, loaded.Count, "completed transfers are not saved");
        Test.Check(loaded[0].Id == jobs[0].Id && loaded[0].IsDownload && loaded[0].State == FileTransferManager.TransferState.Queued && loaded[0].TransferredBytes == 4 && loaded[0].RemoteModifyTime == remoteTime && loaded[0].OpenFolderWhenDone, "a running transfer comes back queued, with what it needs to resume");
        Test.Check(loaded[0].IsArchive && !loaded[1].IsArchive, "a folder archive download stays one after the queue is saved");
        Test.Check(loaded[1].State == FileTransferManager.TransferState.Failed && loaded[1].ErrorMessage == "boom" && loaded[1].LocalModifyTime == localTime && loaded[1].RemoteTempPath == "/r/.b.smartty-part", "a failed transfer comes back failed, with its temporary file");
        TransferQueueStore.Save(path, new FileTransferManager.TransferJob[0]);
        Test.Check(!File.Exists(path), "an empty queue removes the file");
        Test.Equal(0, TransferQueueStore.Load(path).Count, "loading a missing file gives an empty queue");
    }

    private static void DownloadReplacesFile(FileTransferManager manager)
    {
        RunRemote("printf hello > " + RemotePathUtils.ShellQuote(Remote("hello.txt")) + " && touch -d '2024-01-02 03:04:05 UTC' " + RemotePathUtils.ShellQuote(Remote("hello.txt")));
        File.WriteAllText(Local("hello.txt"), "an older and longer local version");
        FileTransferManager.TransferJob job = manager.EnqueueDownload(Remote("hello.txt"), Local("hello.txt"));
        WaitForState(job, FileTransferManager.TransferState.Completed, "a download completes");
        Test.Equal(_protocol == FileTransferProtocol.Sftp ? "SFTP" : "SCP", job.Protocol, Label("the selected protocol is used"));
        Test.Equal("hello", File.ReadAllText(Local("hello.txt")), Label("the downloaded file replaces the local one"));
        Test.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Local("hello.txt")), Label("the download keeps the remote modification time"));
        Test.Check(!File.Exists(Local("hello.txt") + FileTransferManager.PartialDownloadSuffix), Label("no partial file is left after a download"));
        Test.Equal(5L, job.TransferredBytes, Label("the progress reaches the file size"));
        Test.Equal("SHA-256 verified", job.Checksum, Label("the download is verified with SHA-256"));
    }

    // "Tools > Run script": the script is uploaded to /tmp owner-only and executable, run, removed, and its exit status survives.
    private static void RunScriptOnServer()
    {
        Test.Section("Run script");
        byte[] script = RemoteScript.Normalize(System.Text.Encoding.UTF8.GetBytes("\uFEFF#!/bin/sh\r\necho \"hello from $(basename \"$0\" | cut -c1-8)\"\r\necho oops >&2\r\nexit 3\r\n"));
        string path = RemoteScript.MakeRemotePath("my script.sh", "it" + Guid.NewGuid().ToString("N").Substring(0, 6));
        using (ScpChannelStream upload = _session.OpenUpload(path, script.Length, DateTime.Now, RemoteScript.FileMode))
        {
            upload.Write(script, 0, script.Length);
        }
        Test.Equal("700", RemoteStat(path).Split(' ')[0], "the script is uploaded owner-only and executable");
        List<string> output = _session.RunCommand(RemoteScript.BuildCommand(path, RemoteScript.HasShebang(script)), out int? exitStatus);
        Test.Equal(3, exitStatus ?? -1, "the exit status of the script comes back");
        Test.Check(output != null && output.Contains("hello from smartty-") && output.Contains("oops"), "its output and error output are received (got: " + string.Join(" | ", output ?? new List<string>()) + ")");
        Test.Equal(1, _session.RunCommand("test -e " + RemotePathUtils.ShellQuote(path), out int? testStatus) != null ? testStatus ?? -1 : -1, "the script is removed after it has run");
    }

    // The file list's "Run": a script that is already on the server runs from its own folder, executable or not.
    private static void RunScriptInPlace()
    {
        Test.Section("Run a script on the server in place");
        string folder = Remote("run in place");
        string script = RemoteFileOperations.Join(folder, "it's.sh");
        RunRemote("mkdir -p " + RemotePathUtils.ShellQuote(folder) + " && printf '#!/bin/sh\\necho \"in $(basename \"$(pwd)\")\"\\nexit 4\\n' > " + RemotePathUtils.ShellQuote(script) + " && chmod 644 " + RemotePathUtils.ShellQuote(script));
        List<string> probe = _session.RunCommand(RemoteScript.ProbeCommand(script), out _);
        Test.Equal("#!/bin/sh", probe.Count > 0 ? probe[0] : "", "the probe returns the shebang line of a script that is not executable");
        List<string> output = _session.RunCommand(RemoteScript.BuildRunInPlaceCommand(script, probe), out int? exitStatus);
        Test.Equal(4, exitStatus ?? -1, "a script that is not executable runs through the interpreter of its shebang; its exit status comes back");
        Test.Check(output != null && output.Contains("in run in place"), "it runs in the folder it is in (got: " + string.Join(" | ", output ?? new List<string>()) + ")");
        Test.Equal("644", RemoteStat(script).Split(' ')[0], "the file's permissions are not changed");
    }

    // "Tools > Process manager": the list is read from /proc through an exec channel, the signal commands stop a process.
    private static void ProcessListOnServer()
    {
        Test.Section("Process list");
        List<string> lines = _session.RunCommand(RemoteProcessList.ListCommand, out int? exitStatus);
        Test.Equal(0, exitStatus ?? -1, "the list command succeeds");
        ProcessSnapshot snapshot = RemoteProcessList.Parse(lines, null);
        Test.Check(snapshot.Processes.Count > 3 && snapshot.MemTotalKb > 0 && snapshot.CpuCount >= 1 && snapshot.TotalTicks > 0, "the server's processes, memory and processor time are listed (" + snapshot.Processes.Count + " processes)");
        Test.Check(snapshot.Processes.Exists(p => p.User == _environment.User && p.Command.Length > 0), "the processes of the login user are listed under the user's name");

        // A process of the login user: started by one command, ended by another.
        List<string> started = _session.RunCommand("sleep 300 >/dev/null 2>&1 & echo $!", out _);
        int pid = int.Parse(started.Find(l => l.Trim().Length > 0 && char.IsDigit(l.Trim()[0])).Trim());
        ProcessSnapshot second = RemoteProcessList.Parse(_session.RunCommand(RemoteProcessList.ListCommand, out _), snapshot);
        RemoteProcess sleeper = second.Processes.Find(p => p.Pid == pid);
        Test.Check(sleeper != null && sleeper.Command.Contains("sleep 300") && !sleeper.CpuPercent.HasValue, "a process that was just started is in the next list, without a processor share yet");
        RemoteProcess sleeperLater = RemoteProcessList.Parse(_session.RunCommand(RemoteProcessList.ListCommand, out _), second).Processes.Find(p => p.Pid == pid);
        Test.Check(sleeperLater != null && sleeperLater.CpuPercent.HasValue && sleeperLater.CpuPercent < 5, "and has one in the list after that (a sleeping process uses no processor)");
        _session.RunCommand(RemoteProcessList.SignalCommand(pid, force: false), out int? killStatus);
        Test.Equal(0, killStatus ?? -1, "the end command is accepted");
        Test.WaitUntil(() => RemoteProcessList.Parse(_session.RunCommand(RemoteProcessList.ListCommand, out _), null).Processes.TrueForAll(p => p.Pid != pid), TimeSpan.FromSeconds(5), "the process is gone from the list after it was ended");
        _session.RunCommand(RemoteProcessList.SignalCommand(pid, force: true), out int? goneStatus);
        Test.Check(goneStatus != 0, "signalling a process that is gone fails");
    }

    private static void ArchiveDownload(FileTransferManager manager)
    {
        // A name with a space and a quote, a nested folder and a file whose name starts with '-'.
        string folder = Remote("my 'folder'");
        string quoted = RemotePathUtils.ShellQuote(folder);
        RunRemote("mkdir -p " + quoted + "/sub && printf one > " + quoted + "/a.txt && printf two > " + quoted + "/sub/b.txt && printf three > " + quoted + "/-dash.txt");
        File.WriteAllText(Local("my 'folder'.tar.gz"), "an older archive");
        FileTransferManager.TransferJob job = manager.EnqueueDirectoryArchiveDownload(folder, Local("my 'folder'.tar.gz"));
        Test.Check(job.IsDownload && job.IsArchive, "a folder archive is a download job marked as an archive");
        WaitForState(job, FileTransferManager.TransferState.Completed, "a folder archive download completes");
        Test.Equal("tar", job.Protocol, "the archive is made by tar");
        Test.Check(job.TransferredBytes > 0 && job.TotalBytes == 0, "the progress counts the bytes received; the size is not known in advance");
        Test.Check(!File.Exists(Local("my 'folder'.tar.gz") + FileTransferManager.PartialDownloadSuffix), "no partial file is left after an archive download");
        string listing = RunLocal("tar", "-tzf", Local("my 'folder'.tar.gz"));
        Test.Check(listing.Contains("my 'folder'/a.txt") && listing.Contains("my 'folder'/sub/b.txt") && listing.Contains("-dash.txt"), "the archive holds the folder with its files (listing: " + listing.Replace("\n", " ") + ")");
        Test.Check(!listing.Contains(Remote("")), "the archive holds the folder by its name, not by its full path");

        Test.Throws<IOException>(() => manager.EnqueueDirectoryArchiveDownload(folder + "/a.txt", Local("file.tar.gz")), "queueing the archive of a file is refused");
        Test.Throws<IOException>(() => manager.EnqueueDirectoryArchiveDownload(Remote("missing-folder"), Local("missing.tar.gz")), "queueing the archive of a missing folder is refused");
        Test.Throws<IOException>(() => manager.EnqueueDirectoryArchiveDownload("/", Local("root.tar.gz")), "the root directory has no name to archive and is refused");

        // A cancelled archive leaves the existing file alone.
        string big = RemotePathUtils.ShellQuote(Remote("big folder"));
        RunRemote("mkdir -p " + big + " && head -c " + LargeFileSize + " /dev/urandom > " + big + "/random.bin");
        File.WriteAllText(Local("big.tar.gz"), "original");
        manager.SpeedLimitBytesPerSecond = SlowRate;
        FileTransferManager.TransferJob slow = manager.EnqueueDirectoryArchiveDownload(Remote("big folder"), Local("big.tar.gz"));
        try
        {
            WaitUntilRunning(slow);
            manager.Cancel(slow);
            Test.WaitUntil(() => slow.State == FileTransferManager.TransferState.Cancelled, TimeSpan.FromSeconds(10), "a running archive download can be cancelled");
        }
        finally
        {
            manager.SpeedLimitBytesPerSecond = 0;
        }
        Test.Equal("original", File.ReadAllText(Local("big.tar.gz")), "a cancelled archive download leaves the existing local file untouched");
        Test.Check(!File.Exists(Local("big.tar.gz") + FileTransferManager.PartialDownloadSuffix), "a cancelled archive download leaves no partial file");
    }

    private static string RunLocal(string fileName, params string[] arguments)
    {
        System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start);
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static void CancelledDownloadKeepsFile(FileTransferManager manager)
    {
        RunRemote("head -c " + LargeFileSize + " /dev/urandom > " + RemotePathUtils.ShellQuote(Remote("large.bin")));
        File.WriteAllText(Local("large.bin"), "original");
        manager.SpeedLimitBytesPerSecond = SlowRate;
        FileTransferManager.TransferJob job = manager.EnqueueDownload(Remote("large.bin"), Local("large.bin"));
        Test.Equal(LargeFileSize, job.TotalBytes, Label("the queued download knows the remote size"));
        WaitUntilRunning(job);
        manager.IsPaused = true;
        try
        {
            Test.Equal(FileTransferManager.TransferState.InProgress, job.State, Label("the download is paused while running"));
            manager.Cancel(job);
            Test.WaitUntil(() => job.State == FileTransferManager.TransferState.Cancelled, TimeSpan.FromSeconds(5), Label("a paused download can be cancelled without resuming it"));
        }
        finally
        {
            manager.IsPaused = false;
            manager.SpeedLimitBytesPerSecond = 0;
        }
        Test.Equal("original", File.ReadAllText(Local("large.bin")), Label("a cancelled download leaves the existing local file untouched"));
        Test.Check(!File.Exists(Local("large.bin") + FileTransferManager.PartialDownloadSuffix), Label("a cancelled download leaves no partial file"));
    }

    private static void PausedQueueDoesNotStart(FileTransferManager manager)
    {
        File.WriteAllText(Local("queued.txt"), "queued");
        manager.IsPaused = true;
        try
        {
            FileTransferManager.TransferJob download = manager.EnqueueDownload(Remote("hello.txt"), Local("queued-download.txt"));
            FileTransferManager.TransferJob upload = manager.EnqueueUpload(Local("queued.txt"), Remote("queued.txt"), checkOverwrite: false);
            Thread.Sleep(700);
            Test.Equal(FileTransferManager.TransferState.Queued, download.State, Label("a download queued while paused waits"));
            Test.Equal(FileTransferManager.TransferState.Queued, upload.State, Label("an upload queued while paused waits"));
            manager.Cancel(download);
            Test.Equal(FileTransferManager.TransferState.Cancelled, download.State, Label("a waiting download is cancelled at once"));
            manager.IsPaused = false;
            WaitForState(upload, FileTransferManager.TransferState.Completed, "the waiting upload runs after resuming");
            Test.Check(!File.Exists(Local("queued-download.txt")), Label("the cancelled download created no file"));
        }
        finally
        {
            manager.IsPaused = false;
        }
    }

    private static void CancelledUploadRemovesNewFile(FileTransferManager manager)
    {
        CreateLocalFile(Local("upload.bin"), LargeFileSize);
        string remote = Remote("upload.bin");
        manager.SpeedLimitBytesPerSecond = SlowRate;
        FileTransferManager.TransferJob job = manager.EnqueueUpload(Local("upload.bin"), remote);
        Test.Check(job != null, Label("an upload to a new file is queued without asking"));
        WaitUntilRunning(job);
        manager.IsPaused = true;
        try
        {
            Test.Equal(FileTransferManager.TransferState.InProgress, job.State, Label("the upload is paused while running"));
            Test.Equal(FileTransferManager.GetTemporaryUploadPath(remote), job.RemoteTempPath, Label("the upload writes a temporary file"));
            Test.Equal(RemotePathKind.Missing, _session.GetRemotePathKind(remote), Label("the target does not exist before the upload completes"));
            manager.Cancel(job);
            Test.WaitUntil(() => job.State == FileTransferManager.TransferState.Cancelled, TimeSpan.FromSeconds(5), Label("a paused upload can be cancelled without resuming it"));
        }
        finally
        {
            manager.IsPaused = false;
            manager.SpeedLimitBytesPerSecond = 0;
        }
        Test.Equal(RemotePathKind.Missing, _session.GetRemotePathKind(remote), Label("a cancelled upload creates no file"));
        Test.Equal(RemotePathKind.Missing, _session.GetRemotePathKind(FileTransferManager.GetTemporaryUploadPath(remote)), Label("a cancelled upload removes its temporary file"));
    }

    private static void UploadKeepsPermissions(FileTransferManager manager)
    {
        string remote = Remote("script.sh");
        RunRemote("printf old > " + RemotePathUtils.ShellQuote(remote) + " && chmod 754 " + RemotePathUtils.ShellQuote(remote));
        File.WriteAllText(Local("script.sh"), "#!/bin/sh\necho new\n");
        manager.ConfirmOverwrite = (string message) => false;
        Test.Check(manager.EnqueueUpload(Local("script.sh"), remote) == null, Label("declining to overwrite queues nothing"));
        string question = null;
        manager.ConfirmOverwrite = (string message) =>
        {
            question = message;
            return true;
        };
        FileTransferManager.TransferJob job = manager.EnqueueUpload(Local("script.sh"), remote);
        Test.Check(question != null && question.Contains(remote), Label("overwriting an existing remote file is confirmed first"));
        WaitForState(job, FileTransferManager.TransferState.Completed, "the upload over an existing file completes");
        Test.Equal("754 19", RemoteStat(remote), Label("an uploaded script keeps its permissions"));
        Test.Equal("SHA-256 verified", job.Checksum, Label("the upload is verified with SHA-256"));
        Test.Equal(RemotePathKind.Missing, _session.GetRemotePathKind(FileTransferManager.GetTemporaryUploadPath(remote)), Label("no temporary file is left"));
        manager.ConfirmOverwrite = null;
    }

    private static void UploadReplacesOnlyWhenComplete(FileTransferManager manager)
    {
        string remote = Remote("replace.bin");
        RunRemote("printf 'old content' > " + RemotePathUtils.ShellQuote(remote) + " && chmod 604 " + RemotePathUtils.ShellQuote(remote));
        CreateLocalFile(Local("replace.bin"), LargeFileSize);
        manager.SpeedLimitBytesPerSecond = SlowRate;
        FileTransferManager.TransferJob job = manager.EnqueueUpload(Local("replace.bin"), remote, checkOverwrite: false);
        WaitUntilRunning(job);
        manager.IsPaused = true;
        try
        {
            Test.Equal("old content", RemoteHead(remote, 11), Label("while the upload runs, the target still has its old content"));
        }
        finally
        {
            manager.IsPaused = false;
            manager.SpeedLimitBytesPerSecond = 0;
        }
        WaitForState(job, FileTransferManager.TransferState.Completed, "the replacing upload completes");
        Test.Equal("604 " + LargeFileSize, RemoteStat(remote), Label("then the target has the new content and its old permissions"));
    }

    private static void UploadOntoDirectory(FileTransferManager manager)
    {
        string remote = Remote("target");
        RunRemote("mkdir -p " + RemotePathUtils.ShellQuote(remote));
        File.WriteAllText(Local("target"), "a file named like a remote directory");
        Test.Throws<IOException>(() => manager.EnqueueUpload(Local("target"), remote), Label("an upload onto a remote directory is refused when queued"));
        FileTransferManager.TransferJob job = manager.EnqueueUpload(Local("target"), remote, checkOverwrite: false);
        WaitForState(job, FileTransferManager.TransferState.Failed, "an upload onto a remote directory fails");
        Test.Check(job.ErrorMessage != null && job.ErrorMessage.Contains("is a directory"), Label("the failure says why (actual: " + job.ErrorMessage + ")"));
        Test.Equal(RemotePathKind.Missing, _session.GetRemotePathKind(remote + "/target"), Label("nothing is written into the directory"));
    }

    private static void ParallelTransfers(FileTransferManager manager)
    {
        manager.MaxParallelTransfers = 3;
        manager.SpeedLimitBytesPerSecond = SlowRate;
        List<FileTransferManager.TransferJob> jobs = new List<FileTransferManager.TransferJob>();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                jobs.Add(manager.EnqueueDownload(Remote("large.bin"), Local("parallel" + i + ".bin")));
            }
            Test.WaitUntil(() => jobs.Count((FileTransferManager.TransferJob job) => job.State == FileTransferManager.TransferState.InProgress) == 3, Timeout, Label("three transfers run at the same time"));
            Thread.Sleep(300);
            Test.Equal(FileTransferManager.TransferState.Queued, jobs[3].State, Label("the fourth one waits for a free slot"));
            Test.Check(jobs.Sum((FileTransferManager.TransferJob job) => job.BytesPerSecond) <= SlowRate * 2, Label("the speed limit applies to all transfers together"));
        }
        finally
        {
            foreach (FileTransferManager.TransferJob job in jobs)
            {
                manager.Cancel(job);
            }
            manager.SpeedLimitBytesPerSecond = 0;
            manager.MaxParallelTransfers = 2;
        }
        Test.WaitUntil(() => jobs.All((FileTransferManager.TransferJob job) => job.State == FileTransferManager.TransferState.Cancelled), Timeout, Label("the parallel transfers are cancelled"));
    }

    private static void SpeedLimit(FileTransferManager manager)
    {
        CreateLocalFile(Local("limited.bin"), 3L * 1024 * 1024);
        manager.SpeedLimitBytesPerSecond = 1024 * 1024;
        DateTime start = DateTime.UtcNow;
        try
        {
            FileTransferManager.TransferJob job = manager.EnqueueUpload(Local("limited.bin"), Remote("limited.bin"), checkOverwrite: false);
            WaitForState(job, FileTransferManager.TransferState.Completed, "a limited upload completes");
        }
        finally
        {
            manager.SpeedLimitBytesPerSecond = 0;
        }
        double seconds = (DateTime.UtcNow - start).TotalSeconds;
        Test.Check(seconds >= 2.0 && seconds < 15.0, Label("3 MB at 1 MB/s take about 3 seconds (actual: " + seconds.ToString("0.0") + " s)"));
    }

    private static void ResumeDownload()
    {
        RunRemote("test -f " + RemotePathUtils.ShellQuote(Remote("large.bin")) + " || head -c " + LargeFileSize + " /dev/urandom > " + RemotePathUtils.ShellQuote(Remote("large.bin")));
        string local = Local("resume-download.bin");
        FileTransferManager.TransferJob job;
        using (FileTransferManager manager = NewManager())
        {
            manager.SpeedLimitBytesPerSecond = SlowRate;
            job = manager.EnqueueDownload(Remote("large.bin"), local);
            Test.WaitUntil(() => job.TransferredBytes > LargeFileSize / 4, Timeout, Label("the download gets a quarter of the way"));
            manager.IsPaused = true;
        }
        WaitForState(job, FileTransferManager.TransferState.Failed, "closing the queue interrupts a running download");
        Test.Check(job.ErrorMessage != null && job.ErrorMessage.Contains("Interrupted"), Label("and says so (actual: " + job.ErrorMessage + ")"));
        long partSize = File.Exists(local + FileTransferManager.PartialDownloadSuffix) ? new FileInfo(local + FileTransferManager.PartialDownloadSuffix).Length : 0;
        Test.Check(partSize > 0, Label("the partial file is kept for resuming (" + partSize + " bytes)"));
        using (FileTransferManager manager = NewManager())
        {
            Test.Check(manager.Requeue(job), Label("the failed download is queued again"));
            WaitForState(job, FileTransferManager.TransferState.Completed, "the resumed download completes");
        }
        Test.Check(job.ResumedFrom > 0 && job.ResumedFrom <= partSize, Label("it continued from the partial file (from " + job.ResumedFrom + ")"));
        Test.Equal(LargeFileSize, new FileInfo(local).Length, Label("the resumed file has the full size"));
        Test.Equal("SHA-256 verified", job.Checksum, Label("and the same content as the remote one"));
    }

    private static void ResumeUpload()
    {
        string local = Local("resume-upload.bin");
        CreateLocalFile(local, LargeFileSize);
        string remote = Remote("resume-upload.bin");
        _session.RunCommand("rm -f " + RemotePathUtils.ShellQuote(remote), out _);
        FileTransferManager.TransferJob job;
        using (FileTransferManager manager = NewManager())
        {
            manager.SpeedLimitBytesPerSecond = SlowRate;
            job = manager.EnqueueUpload(local, remote);
            Test.WaitUntil(() => job.TransferredBytes > LargeFileSize / 4, Timeout, Label("the upload gets a quarter of the way"));
            manager.IsPaused = true;
        }
        WaitForState(job, FileTransferManager.TransferState.Failed, "closing the queue interrupts a running upload");
        string temp = FileTransferManager.GetTemporaryUploadPath(remote);
        Test.Equal(RemotePathKind.File, _session.GetRemotePathKind(temp), Label("the temporary remote file is kept for resuming"));
        Test.Equal(RemotePathKind.Missing, _session.GetRemotePathKind(remote), Label("the target is not created by an interrupted upload"));
        using (FileTransferManager manager = NewManager())
        {
            Test.Check(manager.Requeue(job), Label("the failed upload is queued again"));
            WaitForState(job, FileTransferManager.TransferState.Completed, "the resumed upload completes");
        }
        Test.Check(job.ResumedFrom > 0, Label("it continued from the temporary file (from " + job.ResumedFrom + ")"));
        Test.Equal("644 " + LargeFileSize, RemoteStat(remote), Label("the resumed upload has the full size"));
        Test.Equal("SHA-256 verified", job.Checksum, Label("and the same content as the local file"));
        Test.Equal(RemotePathKind.Missing, _session.GetRemotePathKind(temp), Label("the temporary file is gone"));
    }

    private static void SftpFallback()
    {
        Test.Section("SFTP selected on a server without it");
        Test.Throws<RemoteOperationException>(() => SftpChannel.Open(_session, "smartty-no-such-subsystem").Dispose(), "a missing subsystem is reported as unavailable");
        File.WriteAllText(Local("fallback.txt"), "fallback");
        using FileTransferManager manager = new FileTransferManager { Session = _session, Protocol = FileTransferProtocol.Sftp, SftpSubsystemName = "smartty-no-such-subsystem" };
        Test.Equal("SFTP", manager.EffectiveProtocolName, "before the first transfer SFTP is assumed");
        FileTransferManager.TransferJob job = manager.EnqueueUpload(Local("fallback.txt"), Remote("fallback.txt"), checkOverwrite: false);
        Test.WaitUntil(() => job.State == FileTransferManager.TransferState.Completed, Timeout, "the upload completes over SCP instead");
        Test.Equal("SCP", job.Protocol, "it used SCP");
        Test.Equal("SCP (no SFTP on the server)", manager.EffectiveProtocolName, "the queue shows that SFTP is not available");
        Test.Check(manager.SftpUnavailableReason != null, "and keeps the reason (" + manager.SftpUnavailableReason + ")");
        manager.Protocol = FileTransferProtocol.Scp;
        manager.Protocol = FileTransferProtocol.Sftp;
        Test.Check(manager.SftpUnavailableReason == null, "selecting the protocol again tries SFTP again");
    }

    private static void DisposeFailsQueuedTransfers()
    {
        File.WriteAllText(Local("pending.txt"), "pending");
        FileTransferManager manager = new FileTransferManager { Session = _session, Protocol = _protocol, IsPaused = true };
        FileTransferManager.TransferJob job = manager.EnqueueUpload(Local("pending.txt"), Remote("pending.txt"), checkOverwrite: false);
        manager.Dispose();
        Test.Equal(FileTransferManager.TransferState.Failed, job.State, Label("a transfer still queued when the session goes away is marked as failed"));
        Test.Check(!string.IsNullOrEmpty(job.ErrorMessage), Label("and says why, so that it can be retried (actual: " + job.ErrorMessage + ")"));
    }
}
