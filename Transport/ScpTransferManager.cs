using SmarTTY.IO;
using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SmarTTY.Transport;

/// <summary>
/// Runs the single-file SCP transfers of a session: one queue for downloads and one for uploads, each served by its own
/// background thread, with a common pause switch. It shows no UI: the Enqueue methods throw and the outcome of a queued
/// transfer is reported through <see cref="TransferJob.State"/> and <see cref="TransferJob.ErrorMessage"/>.
/// </summary>
public class ScpTransferManager : IDisposable
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

        public bool IsDownload;

        public volatile TransferState State;

        public string RemotePath;

        public string LocalPath;

        public ScpDownloadStream PrefetchedStream;

        public object Tag;

        public volatile bool CancelRequested;

        public bool OpenFolderWhenDone;

        /// <summary>
        /// Why the transfer failed; null unless <see cref="State"/> is <see cref="TransferState.Failed"/>.
        /// </summary>
        public volatile string ErrorMessage;

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
                if (State != TransferState.InProgress || rate <= 0 || left < 0)
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

    private const int PollIntervalMs = 200;

    private const int RateIntervalMs = 500;

    private readonly LinkedList<TransferJob> _downloadQueue = new LinkedList<TransferJob>();

    private readonly Semaphore _downloadSignal = new Semaphore(0, int.MaxValue);

    private readonly LinkedList<TransferJob> _uploadQueue = new LinkedList<TransferJob>();

    private readonly Semaphore _uploadSignal = new Semaphore(0, int.MaxValue);

    private readonly ManualResetEvent _resumeEvent = new ManualResetEvent(initialState: true);

    private volatile bool _disposed;

    private volatile bool _paused;

    private volatile TransferJob _activeDownload;

    public SshSession Session;

    public ConfirmHandler ConfirmOverwrite;

    public bool IsPaused
    {
        get
        {
            return _paused;
        }
        set
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
        }
    }

    public ScpTransferManager()
    {
        // Background threads: they only wait for work, they must not keep the process alive after the window is closed.
        new Thread(() => WorkerThreadBody(_downloadQueue, _downloadSignal, RunDownload))
        {
            IsBackground = true,
            Name = "SCP downloads"
        }.Start();
        new Thread(() => WorkerThreadBody(_uploadQueue, _uploadSignal, RunUpload))
        {
            IsBackground = true,
            Name = "SCP uploads"
        }.Start();
    }

    /// <summary>
    /// Queues a download; throws if the remote file cannot be opened (missing, a directory, no permission).
    /// </summary>
    public TransferJob EnqueueDownload(string remotePath, string localPath, object tag = null, bool openFolderWhenDone = false)
    {
        // Opening the file reports a missing or unreadable file right away and tells its size.
        ScpDownloadStream scpDownloadStream = Session.OpenDownload(remotePath);
        TransferJob transferJob = new TransferJob
        {
            IsDownload = true,
            RemotePath = remotePath,
            LocalPath = localPath,
            TotalBytes = scpDownloadStream.Length,
            Tag = tag,
            OpenFolderWhenDone = openFolderWhenDone
        };
        bool keepStream;
        lock (_downloadQueue)
        {
            // The channel buffers whatever the server sends without limit, so an open stream that is not read at once
            // would collect the whole file in memory: keep it only if the download starts right away.
            keepStream = _downloadQueue.Count == 0 && _activeDownload == null && !_paused;
            if (keepStream)
            {
                transferJob.PrefetchedStream = scpDownloadStream;
            }
            _downloadQueue.AddLast(transferJob);
        }
        _downloadSignal.Release();
        if (!keepStream)
        {
            DisposeQuietly(scpDownloadStream);
        }
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
        lock (_uploadQueue)
        {
            _uploadQueue.AddLast(transferJob);
        }
        _uploadSignal.Release();
        return transferJob;
    }

    private void WorkerThreadBody(LinkedList<TransferJob> queue, Semaphore signal, Action<TransferJob> run)
    {
        while (true)
        {
            signal.WaitOne();
            // While paused, queued transfers stay in the queue (and can still be cancelled there) instead of being started.
            while (!_disposed && !_resumeEvent.WaitOne(PollIntervalMs))
            {
            }
            if (_disposed)
            {
                break;
            }
            TransferJob job;
            lock (queue)
            {
                if (queue.Count == 0)
                {
                    // The job this signal was released for has been cancelled meanwhile.
                    continue;
                }
                job = queue.First.Value;
                queue.RemoveFirst();
                if (job.IsDownload)
                {
                    _activeDownload = job;
                }
            }
            try
            {
                if (job.CancelRequested)
                {
                    throw new OperationCanceledException();
                }
                job.State = TransferState.InProgress;
                run(job);
                job.State = TransferState.Completed;
            }
            catch (Exception exception)
            {
                DisposeQuietly(job.PrefetchedStream);
                job.PrefetchedStream = null;
                if (job.CancelRequested || exception is OperationCanceledException)
                {
                    job.State = TransferState.Cancelled;
                }
                else
                {
                    job.ErrorMessage = exception.Message;
                    job.State = TransferState.Failed;
                }
            }
            finally
            {
                if (job.IsDownload)
                {
                    _activeDownload = null;
                }
            }
        }
    }

    private void RunDownload(TransferJob job)
    {
        ScpDownloadStream scpDownloadStream = job.PrefetchedStream ?? Session.OpenDownload(job.RemotePath);
        job.PrefetchedStream = null;
        // The file may have changed since it was queued: what is announced now is what has to arrive.
        job.TotalBytes = scpDownloadStream.Length;
        // The data goes to a temporary file first, so that a failed or cancelled download leaves an existing file
        // with the same name untouched instead of truncated.
        string partPath = job.LocalPath + PartialDownloadSuffix;
        bool partCreated = false;
        try
        {
            using (scpDownloadStream)
            {
                using FileStream fileStream = File.Open(partPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                partCreated = true;
                long received = CopyStream(scpDownloadStream, fileStream, job.TotalBytes, job);
                if (received != job.TotalBytes)
                {
                    throw new IOException("The transfer ended after " + received + " of " + job.TotalBytes + " bytes.");
                }
            }
            if (scpDownloadStream.AccessTime.Year > 1970)
            {
                File.SetLastAccessTimeUtc(partPath, scpDownloadStream.AccessTime);
            }
            if (scpDownloadStream.ModifyTime.Year > 1970)
            {
                File.SetLastWriteTimeUtc(partPath, scpDownloadStream.ModifyTime);
            }
            if (File.Exists(job.LocalPath))
            {
                File.Delete(job.LocalPath);
            }
            File.Move(partPath, job.LocalPath);
            partCreated = false;
        }
        finally
        {
            if (partCreated)
            {
                try
                {
                    File.Delete(partPath);
                }
                catch
                {
                }
            }
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

    private void RunUpload(TransferJob job)
    {
        // The size announced to the remote side must be the size of the file as it is now, not as it was queued.
        using FileStream fileStream = File.Open(job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        job.TotalBytes = fileStream.Length;
        RemotePathKind remoteKind = Session.GetRemotePathKind(job.RemotePath);
        if (remoteKind == RemotePathKind.Directory)
        {
            // "scp -t" would silently put the file inside that directory instead.
            throw new IOException(job.RemotePath + " is a directory");
        }
        bool remoteFileCreated = false;
        try
        {
            using ScpChannelStream scpChannelStream = Session.OpenUpload(job.RemotePath, job.TotalBytes);
            // The remote scp creates the file when it accepts the file header, i.e. before OpenUpload returns.
            remoteFileCreated = remoteKind == RemotePathKind.Missing;
            long sent = CopyStream(fileStream, scpChannelStream, job.TotalBytes, job);
            if (sent != job.TotalBytes)
            {
                throw new IOException("The file ended after " + sent + " of " + job.TotalBytes + " bytes (it was changed during the transfer).");
            }
        }
        catch when (remoteFileCreated)
        {
            // Like a failed download, a failed upload must not leave a truncated file behind; a file that existed
            // before is kept, as there is nothing better to restore it to.
            try
            {
                Session.RunCommand("rm -f " + RemotePathUtils.ShellQuote(job.RemotePath), out _);
            }
            catch
            {
            }
            throw;
        }
    }

    private long CopyStream(Stream sourceStream, Stream destinationStream, long length, TransferJob job)
    {
        long totalRead = 0L;
        byte[] buffer = new byte[262144];
        // The rate is measured over short intervals of active transfer, so that a pause does not drag it down.
        Stopwatch rateClock = Stopwatch.StartNew();
        long rateBytes = 0L;
        while (totalRead < length)
        {
            if (job.CancelRequested || _disposed)
            {
                throw new OperationCanceledException();
            }
            if (_paused)
            {
                job.BytesPerSecond = 0;
                WaitWhilePaused(job);
                rateClock.Restart();
                rateBytes = totalRead;
            }
            int bytesRead = sourceStream.Read(buffer, 0, (int)Math.Min(buffer.LongLength, length - totalRead));
            if (bytesRead <= 0)
            {
                break;
            }
            destinationStream.Write(buffer, 0, bytesRead);
            totalRead += bytesRead;
            job.TransferredBytes = totalRead;
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
        return totalRead;
    }

    private void WaitWhilePaused(TransferJob job)
    {
        // Polled, so that a paused transfer can still be cancelled (or the manager disposed) without resuming first.
        while (!_resumeEvent.WaitOne(PollIntervalMs))
        {
            if (job.CancelRequested || _disposed)
            {
                throw new OperationCanceledException();
            }
        }
    }

    /// <summary>
    /// Stops the transfer threads. Transfers that were still queued are marked as failed so that they can be retried
    /// (for example after reconnecting); a running transfer ends with the closed session.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _resumeEvent.Set();
        foreach (LinkedList<TransferJob> queue in new[] { _downloadQueue, _uploadQueue })
        {
            TransferJob[] pending;
            lock (queue)
            {
                pending = new TransferJob[queue.Count];
                queue.CopyTo(pending, 0);
                queue.Clear();
            }
            foreach (TransferJob job in pending)
            {
                DisposeQuietly(job.PrefetchedStream);
                job.PrefetchedStream = null;
                job.ErrorMessage = "The transfer did not start before the SSH session was closed.";
                job.State = TransferState.Failed;
            }
        }
        _downloadSignal.Release();
        _uploadSignal.Release();
    }

    public void Cancel(TransferJob transferJob)
    {
        bool removedFromDownloads;
        lock (_downloadQueue)
        {
            removedFromDownloads = _downloadQueue.Remove(transferJob);
        }
        if (removedFromDownloads)
        {
            // A queued download may already hold an open channel to the remote side.
            ScpDownloadStream prefetched = transferJob.PrefetchedStream;
            transferJob.PrefetchedStream = null;
            transferJob.State = TransferState.Cancelled;
            DisposeQuietly(prefetched);
            return;
        }
        lock (_uploadQueue)
        {
            if (_uploadQueue.Remove(transferJob))
            {
                transferJob.State = TransferState.Cancelled;
                return;
            }
        }
        if (transferJob.State == TransferState.Failed || transferJob.State == TransferState.Completed)
        {
            transferJob.State = TransferState.Cancelled;
        }
        transferJob.CancelRequested = true;
    }

    private static void DisposeQuietly(IDisposable disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
        }
    }
}
