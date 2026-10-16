using ManagedLibSSH2;
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

    public delegate bool ConfirmHandler(string text);

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

    public TransferJob EnqueueDownload(string text, string text2, object obj = null, bool flag2 = false)
    {
        try
        {
            ScpDownloadStream scpDownloadStream = Session.OpenDownload(text);
            TransferJob transferJob = new TransferJob
            {
                IsDownload = true,
                RemotePath = text,
                LocalPath = text2,
                TotalBytes = scpDownloadStream.Length,
                Tag = obj,
                OpenFolderWhenDone = flag2
            };
            bool flag = false;
            lock (_downloadQueue)
            {
                if (_downloadQueue.Count == 0)
                {
                    transferJob.PrefetchedStream = scpDownloadStream;
                }
                else
                {
                    flag = true;
                }
                _downloadQueue.AddLast(transferJob);
            }
            this._downloadSignal.Release();
            if (flag)
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

    public TransferJob EnqueueUpload(string text, string text2, object obj = null, bool flag = true)
    {
        try
        {
            if (flag)
            {
                try
                {
                    using ScpDownloadStream scpDownloadStream = Session.OpenDownload(text2);
                    if (scpDownloadStream.Length != 0L && !ConfirmOverwrite(text2 + " already exists. Overwrite it?"))
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
                RemotePath = text2,
                LocalPath = text,
                Tag = obj
            };
            using (FileStream fileStream = File.Open(text, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
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

    private long CopyStream(Stream stream, Stream stream2, long value2, TransferJob transferJob = null)
    {
        if (value2 < 0)
        {
            value2 = stream.Length;
        }
        long num = 0L;
        byte[] array = new byte[262144];
        DateTime now = DateTime.Now;
        while (num < value2)
        {
            long num2 = Math.Min(array.LongLength, value2 - num);
            int num3 = stream.Read(array, 0, (int)num2);
            if (num3 <= 0)
            {
                return num;
            }
            stream2.Write(array, 0, num3);
            num += num3;
            if (transferJob != null)
            {
                transferJob.TransferredBytes = num;
                int num4 = (int)(DateTime.Now - now).TotalMilliseconds;
                if (num4 == 0)
                {
                    num4 = 1;
                }
                transferJob.BytesPerSecond = num * 1000 / num4;
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
        return num;
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
            TransferJob value;
            lock (_downloadQueue)
            {
                if (_downloadQueue.Count < 1)
                {
                    continue;
                }
                value = _downloadQueue.First.Value;
                if (value == null)
                {
                    continue;
                }
                _downloadQueue.RemoveFirst();
            }
            try
            {
                value.State = TransferState.InProgress;
                ScpDownloadStream scpDownloadStream = value.PrefetchedStream;
                if (scpDownloadStream == null)
                {
                    scpDownloadStream = Session.OpenDownload(value.RemotePath);
                }
                value.PrefetchedStream = null;
                using (scpDownloadStream)
                {
                    using FileStream fileStream = File.Open(value.LocalPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
                    CopyStream(scpDownloadStream, fileStream, value.TotalBytes, value);
                }
                if (scpDownloadStream.ChangeTime.Year > 1970)
                {
                    File.SetCreationTimeUtc(value.LocalPath, scpDownloadStream.ChangeTime);
                }
                if (scpDownloadStream.AccessTime.Year > 1970)
                {
                    File.SetLastAccessTimeUtc(value.LocalPath, scpDownloadStream.AccessTime);
                }
                if (scpDownloadStream.ModifyTime.Year > 1970)
                {
                    File.SetLastWriteTimeUtc(value.LocalPath, scpDownloadStream.ModifyTime);
                }
                if (value.OpenFolderWhenDone)
                {
                    Process.Start(Path.GetDirectoryName(value.LocalPath));
                }
                value.State = TransferState.Completed;
            }
            catch
            {
                if (value.CancelRequested)
                {
                    try
                    {
                        File.Delete(value.LocalPath);
                    }
                    catch
                    {
                    }
                }
                value.State = TransferState.Failed;
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
            TransferJob value;
            lock (_uploadQueue)
            {
                if (_uploadQueue.Count < 1)
                {
                    continue;
                }
                value = _uploadQueue.First.Value;
                if (value == null)
                {
                    continue;
                }
                _uploadQueue.RemoveFirst();
            }
            try
            {
                value.State = TransferState.InProgress;
                ScpChannelStream scpChannelStream = Session.OpenUpload(value.RemotePath, value.TotalBytes);
                using (scpChannelStream)
                {
                    using FileStream fileStream = File.Open(value.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    CopyStream(fileStream, scpChannelStream, value.TotalBytes, value);
                }
                value.State = TransferState.Completed;
            }
            catch
            {
                value.State = TransferState.Failed;
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
