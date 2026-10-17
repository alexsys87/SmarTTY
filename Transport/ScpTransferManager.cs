using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SmarTTY.Transport;

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
        public bool IsDownload;

        public TransferState State;

        public string RemotePath;

        public string LocalPath;

        public long TotalBytes;

        public long TransferredBytes;

        public ScpDownloadStream PrefetchedStream;

        public object Tag;

        public long BytesPerSecond;

        public bool CancelRequested;

        public bool OpenFolderWhenDone;
    }

    public delegate bool ConfirmHandler(string message);

    private LinkedList<TransferJob> _downloadQueue = new LinkedList<TransferJob>();

    private Semaphore _downloadSignal = new Semaphore(0, int.MaxValue);

    private bool _disposed;

    private Thread _downloadThread;

    private Thread _uploadThread;

    private LinkedList<TransferJob> _uploadQueue = new LinkedList<TransferJob>();

    private Semaphore _uploadSignal = new Semaphore(0, int.MaxValue);

    public SshSession Session;

    private bool _notPaused = true;

    private ManualResetEvent _resumeEvent = new ManualResetEvent(initialState: true);

    public ConfirmHandler ConfirmOverwrite;

    public bool IsPaused
    {
        get
        {
            return !_notPaused;
        }
        set
        {
            _notPaused = !value;
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
        _downloadThread = new Thread(DownloadThreadBody);
        _downloadThread.Start();
        _uploadThread = new Thread(UploadThreadBody);
        _uploadThread.Start();
    }

    public TransferJob EnqueueDownload(string remotePath, string localPath, object tag = null, bool openFolderWhenDone = false)
    {
        try
        {
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
            bool shouldDispose = false;
            lock (_downloadQueue)
            {
                if (_downloadQueue.Count == 0)
                {
                    transferJob.PrefetchedStream = scpDownloadStream;
                }
                else
                {
                    shouldDispose = true;
                }
                _downloadQueue.AddLast(transferJob);
            }
            this._downloadSignal.Release();
            if (shouldDispose)
            {
                scpDownloadStream.Dispose();
            }
            return transferJob;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Cannot download a file: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return null;
        }
    }

    public TransferJob EnqueueUpload(string localPath, string remotePath, object tag = null, bool checkOverwrite = true)
    {
        try
        {
            if (checkOverwrite)
            {
                try
                {
                    using ScpDownloadStream scpDownloadStream = Session.OpenDownload(remotePath);
                    if (scpDownloadStream.Length != 0L && !ConfirmOverwrite(remotePath + " already exists. Overwrite it?"))
                    {
                        return null;
                    }
                }
                catch
                {
                }
            }
            TransferJob transferJob = new TransferJob
            {
                IsDownload = false,
                RemotePath = remotePath,
                LocalPath = localPath,
                Tag = tag
            };
            using (FileStream fileStream = File.Open(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                transferJob.TotalBytes = fileStream.Length;
            }
            lock (_uploadQueue)
            {
                _uploadQueue.AddLast(transferJob);
            }
            _uploadSignal.Release();
            return transferJob;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Cannot upload a file: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return null;
        }
    }

    private long CopyStream(Stream sourceStream, Stream destinationStream, long length, TransferJob transferJob = null)
    {
        if (length < 0)
        {
            length = sourceStream.Length;
        }
        long totalRead = 0L;
        byte[] buffer = new byte[262144];
        DateTime now = DateTime.Now;
        while (totalRead < length)
        {
            long bytesToRead = Math.Min(buffer.LongLength, length - totalRead);
            int bytesRead = sourceStream.Read(buffer, 0, (int)bytesToRead);
            if (bytesRead <= 0)
            {
                return totalRead;
            }
            destinationStream.Write(buffer, 0, bytesRead);
            totalRead += bytesRead;
            if (transferJob != null)
            {
                transferJob.TransferredBytes = totalRead;
                int elapsedMs = (int)(DateTime.Now - now).TotalMilliseconds;
                if (elapsedMs == 0)
                {
                    elapsedMs = 1;
                }
                transferJob.BytesPerSecond = totalRead * 1000 / elapsedMs;
                if (transferJob.CancelRequested)
                {
                    throw new OperationCanceledException();
                }
            }
            if (!_notPaused)
            {
                _resumeEvent.WaitOne();
            }
        }
        return totalRead;
    }

    private void DownloadThreadBody()
    {
        while (true)
        {
            _downloadSignal.WaitOne();
            if (_disposed)
            {
                break;
            }
            TransferJob job;
            lock (_downloadQueue)
            {
                if (_downloadQueue.Count < 1)
                {
                    continue;
                }
                job = _downloadQueue.First.Value;
                if (job == null)
                {
                    continue;
                }
                _downloadQueue.RemoveFirst();
            }
            try
            {
                job.State = TransferState.InProgress;
                ScpDownloadStream scpDownloadStream = job.PrefetchedStream;
                if (scpDownloadStream == null)
                {
                    scpDownloadStream = Session.OpenDownload(job.RemotePath);
                }
                job.PrefetchedStream = null;
                using (scpDownloadStream)
                {
                    using FileStream fileStream = File.Open(job.LocalPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
                    CopyStream(scpDownloadStream, fileStream, job.TotalBytes, job);
                }
                if (scpDownloadStream.ChangeTime.Year > 1970)
                {
                    File.SetCreationTimeUtc(job.LocalPath, scpDownloadStream.ChangeTime);
                }
                if (scpDownloadStream.AccessTime.Year > 1970)
                {
                    File.SetLastAccessTimeUtc(job.LocalPath, scpDownloadStream.AccessTime);
                }
                if (scpDownloadStream.ModifyTime.Year > 1970)
                {
                    File.SetLastWriteTimeUtc(job.LocalPath, scpDownloadStream.ModifyTime);
                }
                if (job.OpenFolderWhenDone)
                {
                    Process.Start(Path.GetDirectoryName(job.LocalPath));
                }
                job.State = TransferState.Completed;
            }
            catch
            {
                if (job.CancelRequested)
                {
                    try
                    {
                        File.Delete(job.LocalPath);
                    }
                    catch
                    {
                    }
                }
                job.State = TransferState.Failed;
            }
        }
    }

    private void UploadThreadBody()
    {
        while (true)
        {
            _uploadSignal.WaitOne();
            if (_disposed)
            {
                break;
            }
            TransferJob job;
            lock (_uploadQueue)
            {
                if (_uploadQueue.Count < 1)
                {
                    continue;
                }
                job = _uploadQueue.First.Value;
                if (job == null)
                {
                    continue;
                }
                _uploadQueue.RemoveFirst();
            }
            try
            {
                job.State = TransferState.InProgress;
                ScpChannelStream scpChannelStream = Session.OpenUpload(job.RemotePath, job.TotalBytes);
                using (scpChannelStream)
                {
                    using FileStream fileStream = File.Open(job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    CopyStream(fileStream, scpChannelStream, job.TotalBytes, job);
                }
                job.State = TransferState.Completed;
            }
            catch
            {
                job.State = TransferState.Failed;
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _resumeEvent.Set();
        _downloadSignal.Release();
        _uploadSignal.Release();
    }

    public void Cancel(TransferJob transferJob)
    {
        lock (_downloadQueue)
        {
            if (_downloadQueue.Remove(transferJob))
            {
                transferJob.State = TransferState.Cancelled;
                return;
            }
            if (transferJob.State == TransferState.Failed || transferJob.State == TransferState.Completed)
            {
                transferJob.State = TransferState.Cancelled;
            }
            transferJob.CancelRequested = true;
        }
    }
}
