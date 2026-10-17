using SmarTTY.IO;
using SmarTTY.Transport;
using SmarTTY.Utilities;
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

        private ScpTransferManager.TransferJob _Context;

        public string LocalFile => _Context.LocalPath;

        public string RemoteFile => _Context.RemotePath;

        public string Size => StringUtils.FormatSize(_Context.TotalBytes);

        public string TransferRate => StringUtils.FormatSize(_Context.BytesPerSecond) + "B/s";

        public ImagePair Images => _Context.State switch
        {
            ScpTransferManager.TransferState.InProgress => new ImagePair((!_Context.IsDownload) ? MainImageType.Up : MainImageType.Down, _Controller._Session.TransferManager.IsPaused ? OverlayImageType.Pause : OverlayImageType.None),
            ScpTransferManager.TransferState.Queued => new ImagePair((!_Context.IsDownload) ? MainImageType.Up : MainImageType.Down, OverlayImageType.Clock),
            ScpTransferManager.TransferState.Completed => new ImagePair(MainImageType.Done),
            _ => new ImagePair(MainImageType.Error),
        };

        public double Progress
        {
            get
            {
                if (_Context.State != ScpTransferManager.TransferState.InProgress)
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
                if (_Context.State != ScpTransferManager.TransferState.InProgress && _Context.State != ScpTransferManager.TransferState.Queued)
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
                if (_Context.State == ScpTransferManager.TransferState.InProgress)
                {
                    return Visibility.Visible;
                }
                return Visibility.Hidden;
            }
        }

        public ScpTransferManager.TransferState Status => _Context.State;

        public event PropertyChangedEventHandler PropertyChanged;

        public TransferObject(SCPController controller, ScpTransferManager.TransferJob ctx)
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
            stats.TotalTransfers++;
            if (Status == ScpTransferManager.TransferState.InProgress || Status == ScpTransferManager.TransferState.Queued)
            {
                stats.TotalActiveOrQueuedTransfers++;
            }
            stats.TotalSize += _Context.TotalBytes;
            stats.TotalDone += _Context.TransferredBytes;
            if (_Context.State == ScpTransferManager.TransferState.Cancelled)
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
        }

        internal void Retry()
        {
            if (_Context.State == ScpTransferManager.TransferState.Failed)
            {
                _Controller._Session.TransferManager.Cancel(_Context);
                _Context.State = ScpTransferManager.TransferState.Cancelled;
                if (_Context.IsDownload)
                {
                    _Controller._Session.DoDownloadFile(_Context.RemotePath, Path.GetDirectoryName(_Context.LocalPath), _Context.OpenFolderWhenDone);
                }
                else
                {
                    _Controller._Session.DoUploadFile(_Context.LocalPath, RemotePathUtils.GetDirectoryName(_Context.RemotePath));
                }
            }
        }

        internal void OpenLocal()
        {
            if (_Context?.LocalPath != null)
            {
                Process.Start(_Context?.LocalPath);
            }
        }

        internal void OpenFolder()
        {
            if (_Context?.LocalPath != null)
            {
                Process.Start(Path.GetDirectoryName(_Context?.LocalPath));
            }
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

    public string Title => "SCP Transfers";

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

    public void AddTransfer(ScpTransferManager.TransferJob ctx)
    {
        ActiveTransfers.Add(new TransferObject(this, ctx));
        Show();
    }

    public SCPStatistics SendUpdateEventsAndGetStatistics()
    {
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
            if (transferObject.Status == ScpTransferManager.TransferState.Completed)
            {
                ActiveTransfers.Remove(transferObject);
            }
        }
    }
}
