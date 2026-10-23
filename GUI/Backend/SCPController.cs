using SmarTTY.IO;
using SmarTTY.Transport;
using SmarTTY.Utilities;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace SmarTTY.GUI.Backend;

public class SCPController : INotifyPropertyChanged, ICloseableElement
{
    public enum MainImageType
    {
        None,
        Up,
        Down,
        Done,
        Error
    }

    public enum OverlayImageType
    {
        None,
        Clock,
        Pause
    }

    public struct ImagePair
    {
        public MainImageType Main { get; }

        public OverlayImageType Overlay { get; }

        public ImagePair(MainImageType main, OverlayImageType overlay = OverlayImageType.None)
        {
            Main = main;
            Overlay = overlay;
        }
    }

    public class TransferObject : INotifyPropertyChanged
    {
        private readonly SCPController _Controller;

        private FileTransferManager.TransferJob _Context;

        public string LocalFile => _Context.LocalPath;

        public string RemoteFile => _Context.RemotePath;

        public string Size => StringUtils.FormatSize(_Context.TotalBytes);

        public string TransferRate => (_Context.State == FileTransferManager.TransferState.InProgress && !_Controller.IsPaused) ? StringUtils.FormatSize(_Context.BytesPerSecond) + "B/s" : "";

        /// <summary>
        /// The state in words: progress and time left while running, the reason when failed.
        /// </summary>
        public string StatusText
        {
            get
            {
                switch (_Context.State)
                {
                    case FileTransferManager.TransferState.Queued:
                        return "Queued";
                    case FileTransferManager.TransferState.InProgress:
                        {
                            string phase = _Context.Phase;
                            if (phase != null)
                            {
                                return phase + "...";
                            }
                            if (_Controller.IsPaused)
                            {
                                return "Paused";
                            }
                            long total = _Context.TotalBytes;
                            // An archive made on the fly has no size known in advance: what has arrived is shown instead.
                            string text = (total > 0) ? $"{_Context.TransferredBytes * 100 / total}%" : (_Context.IsArchive && _Context.TransferredBytes > 0) ? StringUtils.FormatSize(_Context.TransferredBytes) + "B received" : "In progress";
                            TimeSpan? remaining = _Context.Remaining;
                            if (remaining.HasValue)
                            {
                                text += ", " + FormatDuration(remaining.Value) + " left";
                            }
                            if (_Context.ResumedFrom > 0)
                            {
                                text += " (resumed)";
                            }
                            return text;
                        }
                    case FileTransferManager.TransferState.Completed:
                        return (_Context.Checksum != null) ? "Completed, " + _Context.Checksum : "Completed";
                    case FileTransferManager.TransferState.Cancelled:
                        return "Cancelled";
                    default:
                        return string.IsNullOrEmpty(_Context.ErrorMessage) ? "Failed" : "Failed: " + _Context.ErrorMessage;
                }
            }
        }

        public string ToolTip => (_Context.IsDownload ? "Download " + _Context.RemotePath + " to " + _Context.LocalPath : "Upload " + _Context.LocalPath + " to " + _Context.RemotePath)
            + ((_Context.Protocol != null) ? " (" + _Context.Protocol + ")" : "") + "\n" + StatusText;

        public bool IsDownload => _Context.IsDownload;

        internal FileTransferManager.TransferJob Job => _Context;

        public ImagePair Images => _Context.State switch
        {
            FileTransferManager.TransferState.InProgress => new ImagePair((!_Context.IsDownload) ? MainImageType.Up : MainImageType.Down, _Controller._Session.TransferManager.IsPaused ? OverlayImageType.Pause : OverlayImageType.None),
            FileTransferManager.TransferState.Queued => new ImagePair((!_Context.IsDownload) ? MainImageType.Up : MainImageType.Down, OverlayImageType.Clock),
            FileTransferManager.TransferState.Completed => new ImagePair(MainImageType.Done),
            _ => new ImagePair(MainImageType.Error),
        };

        public double Progress
        {
            get
            {
                // An empty file has nothing to divide by: 0 / 0 would be NaN, which a ProgressBar cannot take.
                if (_Context.State != FileTransferManager.TransferState.InProgress || _Context.TotalBytes <= 0)
                {
                    return 0.0;
                }
                return (double)_Context.TransferredBytes / (double)_Context.TotalBytes;
            }
        }

        public Visibility CancelVisibility
        {
            get
            {
                if (_Context.State != FileTransferManager.TransferState.InProgress && _Context.State != FileTransferManager.TransferState.Queued)
                {
                    return Visibility.Hidden;
                }
                return Visibility.Visible;
            }
        }

        public Visibility ProgressVisibility
        {
            get
            {
                if (_Context.State == FileTransferManager.TransferState.InProgress)
                {
                    return Visibility.Visible;
                }
                return Visibility.Hidden;
            }
        }

        public FileTransferManager.TransferState Status => _Context.State;

        public event PropertyChangedEventHandler PropertyChanged;

        public TransferObject(SCPController controller, FileTransferManager.TransferJob ctx)
        {
            _Controller = controller;
            _Context = ctx;
        }

        public void SendUpdateEvents(ref SCPStatistics stats)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("LocalFile"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("RemoteFile"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Size"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TransferRate"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Progress"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CancelVisibility"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ProgressVisibility"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Images"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("StatusText"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ToolTip"));
            stats.TotalTransfers++;
            if (Status == FileTransferManager.TransferState.InProgress || Status == FileTransferManager.TransferState.Queued)
            {
                stats.TotalActiveOrQueuedTransfers++;
            }
            stats.TotalSize += _Context.TotalBytes;
            stats.TotalDone += _Context.TransferredBytes;
            if (_Context.State == FileTransferManager.TransferState.Cancelled)
            {
                _Controller.ActiveTransfers.Remove(this);
            }
        }

        public void SendUpdateEvents()
        {
            SCPStatistics stats = default;
            SendUpdateEvents(ref stats);
        }

        public void Cancel()
        {
            _Controller._Session.TransferManager.Cancel(_Context);
            SendUpdateEvents();
            _Controller._Session.SaveTransferQueue();
        }

        /// <summary>
        /// Queues the transfer again: a failed one continues where it stopped if possible; with
        /// <paramref name="evenIfCompleted"/> a completed one is transferred once more. The overwrite question was
        /// answered when the transfer was first queued.
        /// </summary>
        internal void Retry(bool evenIfCompleted = false)
        {
            FileTransferManager manager = _Controller._Session.TransferManager;
            if (manager == null)
            {
                return;
            }
            FileTransferManager.TransferState state = _Context.State;
            if (state == FileTransferManager.TransferState.Failed || (evenIfCompleted && state == FileTransferManager.TransferState.Completed))
            {
                manager.Requeue(_Context);
                _Controller._Session.SaveTransferQueue();
                SendUpdateEvents();
            }
        }

        internal void CopyRemotePath()
        {
            try
            {
                Clipboard.SetText(_Context.RemotePath);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Deletes the file an upload created on the server (after asking) and removes the entry.
        /// </summary>
        internal bool DeleteUploadedFile(bool ask = true)
        {
            if (_Context.IsDownload || _Context.State != FileTransferManager.TransferState.Completed)
            {
                return false;
            }
            if (ask && !MessageBoxes.AskWarn("Delete " + _Context.RemotePath + " on the server?\n\nThis cannot be undone."))
            {
                return false;
            }
            try
            {
                _Controller._Session.TransferManager.DeleteRemoteFile(_Context);
            }
            catch (Exception exception)
            {
                MessageBoxes.Error("Cannot delete " + _Context.RemotePath + ": " + exception.Message);
                return false;
            }
            _Controller.ActiveTransfers.Remove(this);
            _Controller._Session.RefreshFileExplorers(RemotePathUtils.GetDirectoryName(_Context.RemotePath));
            return true;
        }

        internal void OpenLocal()
        {
            string localPath = _Context?.LocalPath;
            if (localPath == null)
            {
                return;
            }
            if (!File.Exists(localPath))
            {
                MessageBoxes.Error(localPath + " does not exist" + ((_Context.IsDownload && _Context.State != FileTransferManager.TransferState.Completed) ? " yet: the download has not completed." : "."));
                return;
            }
            try
            {
                Process.Start(localPath);
            }
            catch (Exception exception)
            {
                MessageBoxes.Error("Cannot open " + localPath + ": " + exception.Message);
            }
        }

        internal void OpenFolder()
        {
            string localPath = _Context?.LocalPath;
            if (localPath == null)
            {
                return;
            }
            try
            {
                if (File.Exists(localPath))
                {
                    // Opens the folder with the file selected.
                    Process.Start("explorer.exe", "/select,\"" + localPath + "\"");
                }
                else
                {
                    Process.Start(Path.GetDirectoryName(localPath));
                }
            }
            catch (Exception exception)
            {
                MessageBoxes.Error("Cannot open the folder of " + localPath + ": " + exception.Message);
            }
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1.0)
            {
                return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
            }
            return $"{duration.Minutes}:{duration.Seconds:00}";
        }

        internal void CopyLocalPath()
        {
            try
            {
                Clipboard.SetText(_Context.LocalPath);
            }
            catch
            {
            }
        }
    }

    public struct SCPStatistics
    {
        public long TotalSize;

        public long TotalDone;

        public int TotalTransfers;

        public int TotalActiveOrQueuedTransfers;
    }

    private SessionWrapper _Session;

    private bool _IsVisible;

    public string Title => "File Transfers: " + (_Session?.TransferManager?.EffectiveProtocolName ?? "SCP");

    /// <summary>
    /// The jobs listed in the panel, e.g. for saving the queue.
    /// </summary>
    public FileTransferManager.TransferJob[] Jobs => ActiveTransfers.Select((TransferObject transferObject) => transferObject.Job).ToArray();

    public ObservableCollection<TransferObject> ActiveTransfers { get; } = new ObservableCollection<TransferObject>();

    public bool IsVisible
    {
        get
        {
            return _IsVisible;
        }
        set
        {
            _IsVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsVisible"));
        }
    }

    public bool IsPaused
    {
        get
        {
            SessionWrapper session = _Session;
            if (session == null)
            {
                return false;
            }
            return session.TransferManager?.IsPaused == true;
        }
        set
        {
            _Session.TransferManager.IsPaused = value;
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public SCPController(SessionWrapper sessionWrapper)
    {
        _Session = sessionWrapper;
    }

    internal void Show()
    {
        IsVisible = true;
    }

    public void AddTransfer(FileTransferManager.TransferJob ctx, bool save = true)
    {
        ActiveTransfers.Add(new TransferObject(this, ctx));
        Show();
        if (save)
        {
            _Session.SaveTransferQueue();
        }
    }

    public SCPStatistics SendUpdateEventsAndGetStatistics()
    {
        // The protocol in the title changes when SFTP turns out to be unavailable.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Title"));
        SCPStatistics stats = default;
        TransferObject[] items = ActiveTransfers.ToArray();
        for (int arrayIndex = 0; arrayIndex < items.Length; arrayIndex++)
        {
            items[arrayIndex].SendUpdateEvents(ref stats);
        }
        return stats;
    }

    public void Close()
    {
        IsVisible = false;
    }

    public void RemoveCompleted()
    {
        TransferObject[] items = ActiveTransfers.ToArray();
        foreach (TransferObject transferObject in items)
        {
            if (transferObject.Status == FileTransferManager.TransferState.Completed)
            {
                ActiveTransfers.Remove(transferObject);
            }
        }
    }

    /// <summary>
    /// Queues all failed transfers again; with <paramref name="evenIfCompleted"/> also the completed ones.
    /// </summary>
    public void RetryFailed(bool evenIfCompleted = false)
    {
        foreach (TransferObject transferObject in ActiveTransfers.ToArray())
        {
            transferObject.Retry(evenIfCompleted);
        }
    }

    /// <summary>
    /// Cancels every queued and running transfer and empties the list.
    /// </summary>
    public void RemoveAll()
    {
        FileTransferManager manager = _Session?.TransferManager;
        foreach (TransferObject transferObject in ActiveTransfers.ToArray())
        {
            manager?.Cancel(transferObject.Job);
        }
        ActiveTransfers.Clear();
        _Session?.SaveTransferQueue();
    }
}
