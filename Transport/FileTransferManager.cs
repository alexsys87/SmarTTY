using SmarTTY.Ssh;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace SmarTTY.Transport;

public enum FileTransferProtocol
{
    [EnumDisplayText("SCP")]
    Scp,
    [EnumDisplayText("SFTP (SCP if the server has none)")]
    Sftp
}

/// <summary>
/// Runs the single-file transfers of a session over SCP or SFTP: one queue served by up to
/// <see cref="MaxParallelTransfers"/> transfers at a time, with a common pause switch and speed limit.
/// Interrupted transfers can be resumed, finished ones are checked by comparing checksums, and uploads replace an
/// existing file only once the new content is complete (temporary file + mv).
/// It shows no UI: the Enqueue methods throw and the outcome of a queued transfer is reported through
/// <see cref="TransferJob.State"/>, <see cref="TransferJob.ErrorMessage"/> and <see cref="JobFinished"/>.
/// </summary>
public class FileTransferManager : IDisposable
{
    public enum TransferState
    {
        Queued,
        InProgress,
        Completed,
        Failed,
        Cancelled
    }

    public class TransferJob
    {
        // The counters are written by a transfer thread and read by the UI; on x86 a plain long can be read half-written.
        private long _totalBytes;

        private long _transferredBytes;

        private long _bytesPerSecond;

        public Guid Id = Guid.NewGuid();

        public bool IsDownload;

        public volatile TransferState State;

        public string RemotePath;

        public string LocalPath;

        public object Tag;

        public volatile bool CancelRequested;

        public bool OpenFolderWhenDone;

        /// <summary>
        /// A download of a remote directory as a .tar.gz archive made by "tar" on the server: <see cref="RemotePath"/>
        /// is the directory, <see cref="LocalPath"/> the archive, and <see cref="TotalBytes"/> stays 0 (the size of the
        /// archive is not known in advance). It is neither resumed nor verified.
        /// </summary>
        public bool IsArchive;

        /// <summary>
        /// Why the transfer failed; null unless <see cref="State"/> is <see cref="TransferState.Failed"/>.
        /// </summary>
        public volatile string ErrorMessage;

        /// <summary>
        /// What a running transfer is doing besides moving data ("Verifying", ...); null while it copies.
        /// </summary>
        public volatile string Phase;

        /// <summary>
        /// The protocol the last attempt used ("SCP" or "SFTP").
        /// </summary>
        public volatile string Protocol;

        /// <summary>
        /// The result of the integrity check of the last attempt, e.g. "SHA-256 verified"; null if none was made.
        /// </summary>
        public volatile string Checksum;

        /// <summary>
        /// True when the last attempt failed because the SSH connection went away, not because of the transfer itself.
        /// Such a job is taken up again after reconnecting. Set before <see cref="State"/> becomes Failed.
        /// </summary>
        public volatile bool InterruptedByConnectionLoss;

        // What the source looked like when the data in the partial file was written; a resumed transfer continues
        // only if it still looks the same.
        public long RemoteSize = -1;

        public DateTime RemoteModifyTime;

        public long LocalSize = -1;

        public DateTime LocalModifyTime;

        /// <summary>
        /// The temporary remote file an upload writes before it replaces the target; null for a direct upload.
        /// </summary>
        public string RemoteTempPath;

        public long ResumedFrom;

        public long TotalBytes
        {
            get => Interlocked.Read(ref _totalBytes);
            set => Interlocked.Exchange(ref _totalBytes, value);
        }

        public long TransferredBytes
        {
            get => Interlocked.Read(ref _transferredBytes);
            set => Interlocked.Exchange(ref _transferredBytes, value);
        }

        public long BytesPerSecond
        {
            get => Interlocked.Read(ref _bytesPerSecond);
            set => Interlocked.Exchange(ref _bytesPerSecond, value);
        }

        /// <summary>
        /// The estimated time left, or null when it cannot be estimated yet.
        /// </summary>
        public TimeSpan? Remaining
        {
            get
            {
                long rate = BytesPerSecond;
                long left = TotalBytes - TransferredBytes;
                if (State != TransferState.InProgress || Phase != null || rate <= 0 || left < 0)
                {
                    return null;
                }
                return TimeSpan.FromSeconds(Math.Ceiling((double)left / rate));
            }
        }
    }

    public delegate bool ConfirmHandler(string message);

    /// <summary>
    /// Appended to the name of a file while it is being downloaded; it replaces the target only once it is complete.
    /// </summary>
    public const string PartialDownloadSuffix = ".smartty-part";

    public const int MaxWorkers = 8;

    private const int PollIntervalMs = 200;

    private const int RateIntervalMs = 500;

    private const int BufferSize = 262144;

    private readonly LinkedList<TransferJob> _queue = new LinkedList<TransferJob>();

    private readonly ManualResetEvent _resumeEvent = new ManualResetEvent(initialState: true);

    private readonly RateLimiter _limiter = new RateLimiter();

    private volatile bool _disposed;

    private volatile bool _paused;

    private int _running;

    private int _maxParallel = 2;

    private volatile FileTransferProtocol _protocol;

    private volatile string _sftpUnavailableReason;

    public SshSession Session;

    public ConfirmHandler ConfirmOverwrite;

    /// <summary>
    /// The SSH subsystem that provides SFTP; only changed by tests, to check the fallback to SCP.
    /// </summary>
    public string SftpSubsystemName { get; set; } = "sftp";

    public bool VerifyChecksums { get; set; } = true;

    public bool ResumeInterrupted { get; set; } = true;

    public bool AtomicUploads { get; set; } = true;

    /// <summary>
    /// Raised on a transfer thread when a transfer has completed, failed or been cancelled.
    /// </summary>
    public event Action<TransferJob> JobFinished;

    public FileTransferProtocol Protocol
    {
        get => _protocol;
        set
        {
            if (_protocol != value)
            {
                _protocol = value;
                _sftpUnavailableReason = null;
            }
        }
    }

    /// <summary>
    /// Why SFTP was not used although it was selected; null if it was not tried yet or works.
    /// </summary>
    public string SftpUnavailableReason => _sftpUnavailableReason;

    /// <summary>
    /// The protocol transfers use now, for display: "SCP", "SFTP" or "SCP (no SFTP on the server)".
    /// </summary>
    public string EffectiveProtocolName
    {
        get
        {
            if (_protocol != FileTransferProtocol.Sftp)
            {
                return "SCP";
            }
            return (_sftpUnavailableReason == null) ? "SFTP" : "SCP (no SFTP on the server)";
        }
    }

    public int MaxParallelTransfers
    {
        get => _maxParallel;
        set
        {
            lock (_queue)
            {
                _maxParallel = Math.Max(1, Math.Min(MaxWorkers, value));
                Monitor.PulseAll(_queue);
            }
        }
    }

    /// <summary>
    /// The limit for all transfers together in bytes per second; 0 means unlimited.
    /// </summary>
    public long SpeedLimitBytesPerSecond
    {
        get => _limiter.BytesPerSecond;
        set => _limiter.BytesPerSecond = value;
    }

    public bool IsPaused
    {
        get
        {
            return _paused;
        }
        set
        {
            lock (_queue)
            {
                _paused = value;
                if (value)
                {
                    _resumeEvent.Reset();
                }
                else
                {
                    _resumeEvent.Set();
                }
                Monitor.PulseAll(_queue);
            }
        }
    }

    public FileTransferManager()
    {
        // Background threads: they only wait for work, they must not keep the process alive after the window is closed.
        for (int i = 0; i < MaxWorkers; i++)
        {
            new Thread(WorkerThreadBody)
            {
                IsBackground = true,
                Name = "File transfer " + (i + 1)
            }.Start();
        }
    }

    /// <summary>
    /// Queues a download; throws if the remote file cannot be read (missing, a directory, no permission).
    /// </summary>
    public TransferJob EnqueueDownload(string remotePath, string localPath, object tag = null, bool openFolderWhenDone = false)
    {
        long size;
        using (ITransferBackend backend = CreateBackend())
        {
            // Reports a missing or unreadable file right away and tells its size.
            backend.Probe(remotePath, out size, out _);
        }
        TransferJob transferJob = new TransferJob
        {
            IsDownload = true,
            RemotePath = remotePath,
            LocalPath = localPath,
            TotalBytes = size,
            Tag = tag,
            OpenFolderWhenDone = openFolderWhenDone
        };
        Enqueue(transferJob);
        return transferJob;
    }

    /// <summary>
    /// Queues the download of a remote directory as a .tar.gz archive; throws if the path is not a directory.
    /// </summary>
    public TransferJob EnqueueDirectoryArchiveDownload(string remoteDirectory, string localPath, object tag = null, bool openFolderWhenDone = false)
    {
        SplitArchivePath(remoteDirectory);
        RemotePathKind kind = Session.GetRemotePathKind(remoteDirectory);
        if (kind == RemotePathKind.File)
        {
            throw new IOException(remoteDirectory + " is not a directory");
        }
        if (kind == RemotePathKind.Missing)
        {
            throw new IOException(remoteDirectory + " does not exist");
        }
        TransferJob transferJob = new TransferJob
        {
            IsDownload = true,
            IsArchive = true,
            RemotePath = remoteDirectory,
            LocalPath = localPath,
            Tag = tag,
            OpenFolderWhenDone = openFolderWhenDone
        };
        Enqueue(transferJob);
        return transferJob;
    }

    /// <summary>
    /// Queues an upload; throws if the local file cannot be read or the remote path is a directory.
    /// Returns null if <paramref name="checkOverwrite"/> is set and the user declined to overwrite an existing remote file.
    /// </summary>
    public TransferJob EnqueueUpload(string localPath, string remotePath, object tag = null, bool checkOverwrite = true)
    {
        long length;
        using (FileStream fileStream = File.Open(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            length = fileStream.Length;
        }
        if (checkOverwrite)
        {
            switch (Session.GetRemotePathKind(remotePath))
            {
                case RemotePathKind.Directory:
                    throw new IOException(remotePath + " is a directory");
                case RemotePathKind.File:
                    if (ConfirmOverwrite != null && !ConfirmOverwrite(remotePath + " already exists. Overwrite it?"))
                    {
                        return null;
                    }
                    break;
            }
        }
        TransferJob transferJob = new TransferJob
        {
            IsDownload = false,
            RemotePath = remotePath,
            LocalPath = localPath,
            TotalBytes = length,
            Tag = tag
        };
        Enqueue(transferJob);
        return transferJob;
    }

    /// <summary>
    /// Queues a job again: a failed one resumes where it stopped (if that is still possible), a completed one is
    /// transferred again. Returns false if the job is still queued or running.
    /// </summary>
    public bool Requeue(TransferJob job)
    {
        lock (_queue)
        {
            if (job.State == TransferState.Queued && !_queue.Contains(job) && !job.CancelRequested)
            {
                // A job restored from a saved queue, or one left behind by a previous manager of this session.
                _queue.AddLast(job);
                Monitor.PulseAll(_queue);
                return true;
            }
            if (job.State == TransferState.Queued || job.State == TransferState.InProgress)
            {
                return false;
            }
            job.State = TransferState.Queued;
            job.CancelRequested = false;
            job.ErrorMessage = null;
            job.InterruptedByConnectionLoss = false;
            job.Phase = null;
            job.Checksum = null;
            job.BytesPerSecond = 0;
            job.TransferredBytes = 0;
            _queue.AddLast(job);
            Monitor.PulseAll(_queue);
            return true;
        }
    }

    private void Enqueue(TransferJob job)
    {
        lock (_queue)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FileTransferManager));
            }
            job.State = TransferState.Queued;
            _queue.AddLast(job);
            Monitor.PulseAll(_queue);
        }
    }

    private void WorkerThreadBody()
    {
        while (true)
        {
            TransferJob job;
            lock (_queue)
            {
                // While paused, queued transfers stay in the queue (and can still be cancelled there) instead of being started.
                while (!_disposed && (_paused || _queue.Count == 0 || _running >= _maxParallel))
                {
                    Monitor.Wait(_queue);
                }
                if (_disposed)
                {
                    return;
                }
                job = _queue.First.Value;
                _queue.RemoveFirst();
                // Marked under the lock, so that Requeue cannot take it for a job that is waiting to be queued.
                job.State = TransferState.InProgress;
                _running++;
            }
            try
            {
                RunJob(job);
            }
            finally
            {
                lock (_queue)
                {
                    _running--;
                    Monitor.PulseAll(_queue);
                }
            }
            try
            {
                JobFinished?.Invoke(job);
            }
            catch
            {
            }
        }
    }

    private void RunJob(TransferJob job)
    {
        job.ErrorMessage = null;
        job.InterruptedByConnectionLoss = false;
        job.Phase = null;
        job.Checksum = null;
        job.ResumedFrom = 0;
        job.BytesPerSecond = 0;
        try
        {
            if (job.CancelRequested)
            {
                throw new OperationCanceledException();
            }
            job.State = TransferState.InProgress;
            if (job.IsArchive)
            {
                job.Protocol = "tar";
                RunArchiveDownload(job);
            }
            else
            {
                using ITransferBackend backend = CreateBackend();
                job.Protocol = backend.Name;
                if (job.IsDownload)
                {
                    RunDownload(job, backend);
                }
                else
                {
                    RunUpload(job, backend);
                }
            }
            job.Phase = null;
            job.State = TransferState.Completed;
        }
        catch (Exception exception)
        {
            job.Phase = null;
            if (exception is TransferInterruptedException)
            {
                job.ErrorMessage = exception.Message;
                job.State = TransferState.Failed;
            }
            else if (job.CancelRequested || exception is OperationCanceledException)
            {
                job.State = TransferState.Cancelled;
            }
            else
            {
                job.ErrorMessage = exception.Message;
                // The channels are closed before a transfer thread sees the short read, so the session already knows
                // about the loss here. Say so (instead of "The transfer ended after N of M bytes") and mark the job,
                // so that it is resumed after reconnecting even though it has failed by the time the loss is reported.
                if (Session != null && Session.IsConnectionClosed)
                {
                    job.ErrorMessage = "The connection to the server was lost. " + exception.Message;
                    job.InterruptedByConnectionLoss = true;
                }
                job.State = TransferState.Failed;
            }
        }
    }

    private ITransferBackend CreateBackend()
    {
        if (_protocol == FileTransferProtocol.Sftp && _sftpUnavailableReason == null)
        {
            try
            {
                return new SftpBackend(SftpChannel.Open(Session, SftpSubsystemName));
            }
            catch (RemoteOperationException exception)
            {
                // No SFTP on this server (OpenWrt without openssh-sftp-server): SCP does the job instead.
                _sftpUnavailableReason = exception.Message;
            }
        }
        return new ScpBackend(Session);
    }

    #region Downloads

    private void RunDownload(TransferJob job, ITransferBackend backend)
    {
        backend.Probe(job.RemotePath, out long size, out DateTime modifyTime);
        // The data goes to a temporary file first, so that a failed or cancelled download leaves an existing file
        // with the same name untouched instead of truncated; a failed one keeps it to resume from.
        string partPath = job.LocalPath + PartialDownloadSuffix;
        long offset = 0;
        if (ResumeInterrupted && File.Exists(partPath) && job.RemoteSize == size && job.RemoteModifyTime == modifyTime)
        {
            offset = new FileInfo(partPath).Length;
            if (offset > size)
            {
                offset = 0;
            }
        }
        job.RemoteSize = size;
        job.RemoteModifyTime = modifyTime;
        job.TotalBytes = size;
        job.ResumedFrom = offset;
        job.TransferredBytes = offset;
        bool keepPart = false;
        try
        {
            using (FileStream fileStream = File.Open(partPath, (offset > 0) ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                fileStream.SetLength(offset);
                fileStream.Seek(offset, SeekOrigin.Begin);
                if (offset < size)
                {
                    using Stream remote = backend.OpenRead(job.RemotePath, offset);
                    // From here on a failure leaves data worth resuming from.
                    keepPart = ResumeInterrupted;
                    CopyStream(remote, fileStream, size - offset, job, offset);
                }
            }
            keepPart = false;
            Verify(job, job.RemotePath, partPath);
            keepPart = ResumeInterrupted;
            if (modifyTime.Year > 1970)
            {
                File.SetLastWriteTimeUtc(partPath, modifyTime);
            }
            if (File.Exists(job.LocalPath))
            {
                File.Delete(job.LocalPath);
            }
            File.Move(partPath, job.LocalPath);
            keepPart = true;
        }
        catch (Exception exception)
        {
            if (!keepPart || job.CancelRequested || exception is OperationCanceledException)
            {
                TryDeleteLocal(partPath);
            }
            throw;
        }
        if (job.OpenFolderWhenDone)
        {
            try
            {
                Process.Start(Path.GetDirectoryName(job.LocalPath));
            }
            catch
            {
            }
        }
    }

    private static void TryDeleteLocal(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    /// <summary>
    /// The directory that holds <paramref name="remoteDirectory"/> and its name, for "tar -C parent name".
    /// Throws for the root directory, which has no name to archive.
    /// </summary>
    internal static void SplitArchivePath(string remoteDirectory, out string parent, out string name)
    {
        string path = (remoteDirectory ?? "").TrimEnd('/');
        int slash = path.LastIndexOf('/');
        name = path.Substring(slash + 1);
        if (name.Length == 0 || name == "." || name == "..")
        {
            throw new IOException("Cannot make an archive of " + remoteDirectory + ": select a folder by its name.");
        }
        parent = slash < 0 ? "." : slash == 0 ? "/" : path.Substring(0, slash);
    }

    private static void SplitArchivePath(string remoteDirectory)
    {
        SplitArchivePath(remoteDirectory, out _, out _);
    }

    private void RunArchiveDownload(TransferJob job)
    {
        SplitArchivePath(job.RemotePath, out string parent, out string name);
        // A name that starts with '-' must not be read as an option of tar.
        string member = RemoteFileTools.Quote(name.StartsWith("-", StringComparison.Ordinal) ? "./" + name : name);
        string TarCommand(string compression) => "tar -c" + compression + "f - -C " + RemoteFileTools.Quote(parent) + " " + member;
        job.TotalBytes = 0;
        job.TransferredBytes = 0;
        string partPath = job.LocalPath + PartialDownloadSuffix;
        try
        {
            string failure = DownloadArchive(job, TarCommand("z"), partPath, compressLocally: false, out long received);
            if (failure != null && received == 0)
            {
                // A tar without gzip support (a small BusyBox build): the archive is made plain and compressed here.
                string plainFailure = DownloadArchive(job, TarCommand(""), partPath, compressLocally: true, out _);
                failure = plainFailure == null ? null : failure;
            }
            if (failure != null)
            {
                throw new IOException(failure);
            }
            if (File.Exists(job.LocalPath))
            {
                File.Delete(job.LocalPath);
            }
            File.Move(partPath, job.LocalPath);
        }
        catch
        {
            TryDeleteLocal(partPath);
            throw;
        }
        if (job.OpenFolderWhenDone)
        {
            try
            {
                Process.Start(Path.GetDirectoryName(job.LocalPath));
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="command"/> and writes its output to <paramref name="partPath"/> (through gzip when
    /// <paramref name="compressLocally"/> is set). Returns null on success, otherwise what tar said.
    /// </summary>
    private string DownloadArchive(TransferJob job, string command, string partPath, bool compressLocally, out long received)
    {
        received = 0;
        job.TransferredBytes = 0;
        Session.PtyGate.BeginPlainHelper();
        try
        {
            using SshShellChannel channel = Session.OpenShellChannel(command, null, ExtendedDataMode.Normal);
            using (FileStream fileStream = File.Open(partPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                Stream destination = fileStream;
                GZipStream gzip = null;
                try
                {
                    if (compressLocally)
                    {
                        destination = gzip = new GZipStream(fileStream, CompressionMode.Compress, leaveOpen: true);
                    }
                    try
                    {
                        CopyStream(channel.Output, destination, -1, job, 0);
                    }
                    finally
                    {
                        received = job.TransferredBytes;
                    }
                }
                finally
                {
                    gzip?.Dispose();
                }
            }
            channel.WaitClosed();
            int exitStatus = channel.ExitStatus;
            // 1 is GNU tar's "some files changed while they were read": the archive is complete enough to keep.
            if (exitStatus == 0 || exitStatus == 1)
            {
                if (exitStatus == 1)
                {
                    job.Checksum = "some files changed while they were read";
                }
                return null;
            }
            string error = new StreamReader(channel.Error).ReadToEnd().Trim();
            return string.IsNullOrEmpty(error) ? "tar failed on the server (exit status " + exitStatus + ")." : error;
        }
        finally
        {
            Session.PtyGate.EndPlainHelper();
        }
    }

    #endregion

    #region Uploads

    /// <summary>
    /// The temporary file an upload to <paramref name="remotePath"/> writes first: hidden, in the same directory,
    /// so that the final rename stays within one file system.
    /// </summary>
    public static string GetTemporaryUploadPath(string remotePath)
    {
        int slash = remotePath.LastIndexOf('/');
        string directory = (slash >= 0) ? remotePath.Substring(0, slash + 1) : "";
        return directory + "." + remotePath.Substring(slash + 1) + PartialDownloadSuffix;
    }

    private void RunUpload(TransferJob job, ITransferBackend backend)
    {
        // The size announced to the remote side must be the size of the file as it is now, not as it was queued.
        using FileStream fileStream = File.Open(job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long size = fileStream.Length;
        DateTime localModifyTime = File.GetLastWriteTimeUtc(job.LocalPath);
        string target = job.RemotePath;
        RemoteFileInfo targetInfo = RemoteFileTools.GetInfo(Session, target);
        if (targetInfo != null && targetInfo.IsDirectory)
        {
            // "scp -t" would silently put the file inside that directory instead.
            throw new IOException(target + " is a directory");
        }
        // A symbolic link is written through (replacing it would turn it into a regular file); if the target cannot
        // be examined at all, the upload goes straight to it as well.
        bool useTemp = AtomicUploads && targetInfo != null && !targetInfo.IsSymbolicLink;
        string tempPath = useTemp ? GetTemporaryUploadPath(target) : null;
        long offset = 0;
        if (useTemp && ResumeInterrupted && job.RemoteTempPath == tempPath && job.LocalSize == size && job.LocalModifyTime == localModifyTime)
        {
            long? done = backend.GetSize(tempPath);
            if (done.HasValue && done.Value > 0 && done.Value <= size)
            {
                offset = done.Value;
            }
        }
        job.LocalSize = size;
        job.LocalModifyTime = localModifyTime;
        job.TotalBytes = size;
        job.RemoteTempPath = tempPath;
        job.ResumedFrom = offset;
        job.TransferredBytes = offset;
        string writePath = useTemp ? tempPath : target;
        bool remoteFileCreated = false;
        bool keepRemote = false;
        try
        {
            Stream remote;
            try
            {
                remote = backend.OpenWrite(writePath, offset, size);
            }
            catch (Exception exception) when (useTemp && offset == 0 && (exception is RemoteOperationException || exception is RemotePathException))
            {
                // The directory does not let us create a file next to the target (but the target itself may be
                // writable, e.g. a file in /etc owned by the user): write the target directly.
                useTemp = false;
                job.RemoteTempPath = null;
                writePath = target;
                remote = backend.OpenWrite(writePath, 0, size);
            }
            // The file exists from here on: SCP creates it when it accepts the header, SFTP when it opens it.
            remoteFileCreated = useTemp || (targetInfo != null && !targetInfo.Exists);
            using (remote)
            {
                if (offset < size)
                {
                    fileStream.Seek(offset, SeekOrigin.Begin);
                    keepRemote = useTemp && ResumeInterrupted;
                    CopyStream(fileStream, remote, size - offset, job, offset);
                }
            }
            keepRemote = false;
            Verify(job, writePath, job.LocalPath);
            if (useTemp)
            {
                job.Phase = "Replacing";
                if (targetInfo.Exists)
                {
                    RemoteFileTools.CopyAttributes(Session, targetInfo, tempPath);
                }
                RemoteFileTools.Replace(Session, tempPath, target);
                job.RemoteTempPath = null;
            }
        }
        catch (Exception exception)
        {
            // Like a failed download, a failed upload must not leave a truncated file behind, unless it is the
            // temporary file a retry can continue; a target that existed before is never removed.
            if (remoteFileCreated && (!keepRemote || job.CancelRequested || exception is OperationCanceledException))
            {
                try
                {
                    Session.RunCommand("rm -f " + RemoteFileTools.Quote(writePath), out _);
                }
                catch
                {
                }
            }
            throw;
        }
    }

    #endregion

    private void Verify(TransferJob job, string remotePath, string localPath)
    {
        if (!VerifyChecksums)
        {
            return;
        }
        job.Phase = "Verifying";
        FileChecksum remote = RemoteFileTools.ComputeRemoteChecksum(Session, remotePath);
        if (remote == null)
        {
            job.Checksum = "not verified: the server has neither sha256sum nor md5sum";
            return;
        }
        FileChecksum local = RemoteFileTools.ComputeLocalChecksum(localPath, remote.Algorithm, () => CheckCancelled(job));
        if (local.Value != remote.Value)
        {
            throw new IOException("The " + remote.Algorithm + " checksums differ after the transfer (local " + local.Value + ", remote " + remote.Value + "): the file was changed during the transfer or the data was damaged.");
        }
        job.Checksum = remote.Algorithm + " verified";
        job.Phase = null;
    }

    private void CopyStream(Stream sourceStream, Stream destinationStream, long length, TransferJob job, long startOffset)
    {
        long totalRead = 0L;
        byte[] buffer = new byte[BufferSize];
        // The rate is measured over short intervals of active transfer, so that a pause does not drag it down.
        Stopwatch rateClock = Stopwatch.StartNew();
        long rateBytes = 0L;
        // A negative length is a stream of unknown size, read to its end.
        bool untilEnd = length < 0;
        while (untilEnd || totalRead < length)
        {
            CheckCancelled(job);
            if (_paused)
            {
                job.BytesPerSecond = 0;
                WaitWhilePaused(job);
                rateClock.Restart();
                rateBytes = totalRead;
            }
            int chunk = untilEnd ? _limiter.ChunkSize(buffer.Length) : (int)Math.Min(_limiter.ChunkSize(buffer.Length), length - totalRead);
            int bytesRead = sourceStream.Read(buffer, 0, chunk);
            if (bytesRead <= 0)
            {
                break;
            }
            _limiter.Consume(bytesRead, () => CheckCancelled(job));
            destinationStream.Write(buffer, 0, bytesRead);
            totalRead += bytesRead;
            job.TransferredBytes = startOffset + totalRead;
            long elapsedMs = rateClock.ElapsedMilliseconds;
            if (elapsedMs >= RateIntervalMs)
            {
                long current = (totalRead - rateBytes) * 1000 / elapsedMs;
                long previous = job.BytesPerSecond;
                job.BytesPerSecond = (previous == 0) ? current : (previous + current) / 2;
                rateClock.Restart();
                rateBytes = totalRead;
            }
        }
        if (job.BytesPerSecond == 0 && totalRead > 0)
        {
            // A transfer shorter than one measuring interval.
            job.BytesPerSecond = totalRead * 1000 / Math.Max(1L, rateClock.ElapsedMilliseconds);
        }
        if (!untilEnd && totalRead != length)
        {
            throw new IOException(job.IsDownload
                ? "The transfer ended after " + (startOffset + totalRead) + " of " + (startOffset + length) + " bytes."
                : "The file ended after " + (startOffset + totalRead) + " of " + (startOffset + length) + " bytes (it was changed during the transfer).");
        }
        // SFTP writes are confirmed asynchronously: a write error must surface here, not when the stream is closed.
        destinationStream.Flush();
    }

    private void CheckCancelled(TransferJob job)
    {
        if (job.CancelRequested)
        {
            throw new OperationCanceledException();
        }
        if (_disposed)
        {
            throw new TransferInterruptedException();
        }
    }

    private void WaitWhilePaused(TransferJob job)
    {
        // Polled, so that a paused transfer can still be cancelled (or the manager disposed) without resuming first.
        while (!_resumeEvent.WaitOne(PollIntervalMs))
        {
            CheckCancelled(job);
        }
        CheckCancelled(job);
    }

    /// <summary>
    /// Stops the transfers. Queued ones and running ones are marked as failed (the running ones keep what they
    /// transferred so far), so that they can be resumed later, for example after reconnecting.
    /// </summary>
    public void Dispose()
    {
        TransferJob[] pending;
        lock (_queue)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            pending = new TransferJob[_queue.Count];
            _queue.CopyTo(pending, 0);
            _queue.Clear();
            _resumeEvent.Set();
            Monitor.PulseAll(_queue);
        }
        foreach (TransferJob job in pending)
        {
            job.ErrorMessage = "The transfer did not start before the SSH session was closed.";
            job.State = TransferState.Failed;
        }
    }

    public void Cancel(TransferJob transferJob)
    {
        lock (_queue)
        {
            if (_queue.Remove(transferJob))
            {
                transferJob.State = TransferState.Cancelled;
                return;
            }
        }
        if (transferJob.State == TransferState.Failed || transferJob.State == TransferState.Completed)
        {
            bool failed = transferJob.State == TransferState.Failed;
            transferJob.State = TransferState.Cancelled;
            if (failed && transferJob.IsDownload)
            {
                // A failed download kept its partial file for resuming; nobody will resume it now.
                TryDeleteLocal(transferJob.LocalPath + PartialDownloadSuffix);
            }
            return;
        }
        transferJob.CancelRequested = true;
    }

    /// <summary>
    /// Deletes the remote file of a job: the uploaded file, or the source of a download.
    /// </summary>
    public void DeleteRemoteFile(TransferJob job)
    {
        RemoteFileTools.Remove(Session, job.RemotePath);
    }

    private sealed class TransferInterruptedException : Exception
    {
        public TransferInterruptedException()
            : base("Interrupted: the SSH session was closed. Retry to continue where it stopped.")
        {
        }
    }

    #region Protocol back ends

    private interface ITransferBackend : IDisposable
    {
        string Name { get; }

        /// <summary>
        /// Checks that <paramref name="path"/> is a readable regular file and returns its size and modification time.
        /// </summary>
        void Probe(string path, out long size, out DateTime modifyTime);

        Stream OpenRead(string path, long offset);

        /// <summary>
        /// Opens <paramref name="path"/> for writing <paramref name="length"/> bytes in total: from scratch if
        /// <paramref name="offset"/> is 0, otherwise appending after the first <paramref name="offset"/> bytes.
        /// </summary>
        Stream OpenWrite(string path, long offset, long length);

        /// <summary>
        /// The size of a regular file, or null if there is none.
        /// </summary>
        long? GetSize(string path);
    }

    private sealed class ScpBackend : ITransferBackend
    {
        private readonly SshSession _session;

        private ScpDownloadStream _probed;

        private string _probedPath;

        public ScpBackend(SshSession session)
        {
            _session = session;
        }

        public string Name => "SCP";

        public void Probe(string path, out long size, out DateTime modifyTime)
        {
            DropProbe();
            // The SCP header is the only way SCP tells the size and the time; the open stream is kept, so that
            // a download from the start can use it instead of asking for the file a second time.
            _probed = _session.OpenDownload(path);
            _probedPath = path;
            size = _probed.Length;
            modifyTime = _probed.ModifyTime;
        }

        public Stream OpenRead(string path, long offset)
        {
            if (offset == 0)
            {
                ScpDownloadStream stream = (_probed != null && _probedPath == path) ? _probed : _session.OpenDownload(path);
                _probed = null;
                return stream;
            }
            if (_probed == null || _probedPath != path)
            {
                Probe(path, out _, out _);
            }
            long size = _probed.Length;
            DropProbe();
            return RemoteFileTools.OpenReadFrom(_session, path, offset, size - offset);
        }

        public Stream OpenWrite(string path, long offset, long length)
        {
            if (offset == 0)
            {
                // Without a mode an existing file keeps its permissions; a new one gets 0644 minus the umask.
                return _session.OpenUpload(path, length);
            }
            return RemoteFileTools.OpenAppend(_session, path, length - offset);
        }

        public long? GetSize(string path)
        {
            RemoteFileInfo info = RemoteFileTools.GetInfo(_session, path);
            return (info != null && info.Exists && !info.IsDirectory) ? info.Size : null;
        }

        private void DropProbe()
        {
            try
            {
                _probed?.Dispose();
            }
            catch
            {
            }
            _probed = null;
        }

        public void Dispose()
        {
            DropProbe();
        }
    }

    private sealed class SftpBackend : ITransferBackend
    {
        private readonly SftpChannel _sftp;

        public SftpBackend(SftpChannel sftp)
        {
            _sftp = sftp;
        }

        public string Name => "SFTP";

        public void Probe(string path, out long size, out DateTime modifyTime)
        {
            SftpFileAttributes attributes = _sftp.Stat(path) ?? throw new RemoteOperationException(path + ": No such file or directory");
            if (attributes.IsDirectory)
            {
                throw new RemoteOperationException(path + " is a directory");
            }
            size = attributes.Size ?? throw new RemoteOperationException("The SFTP server did not report the size of " + path);
            modifyTime = attributes.ModifyTime ?? SshSession.UnixEpoch;
        }

        public Stream OpenRead(string path, long offset)
        {
            return _sftp.OpenRead(path, offset);
        }

        public Stream OpenWrite(string path, long offset, long length)
        {
            return _sftp.OpenWrite(path, offset, truncate: offset == 0);
        }

        public long? GetSize(string path)
        {
            SftpFileAttributes attributes = _sftp.Stat(path);
            return (attributes != null && !attributes.IsDirectory) ? attributes.Size : null;
        }

        public void Dispose()
        {
            _sftp.Dispose();
        }
    }

    #endregion
}
