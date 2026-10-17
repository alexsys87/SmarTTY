using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.IO;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;

namespace SmarTTY.GUI.Backend;

public class FileExplorer : INotifyPropertyChanged, ICloseableElement, IDisposable
{
    private class ListResult
    {
        public string Directory;

        public LsOutputParser.LsEntry[] Result;

        public Exception Exception;
    }

    private class ListOperation : IDisposable
    {
        public readonly SshSession Session;

        private List<byte> _AllData = new List<byte>();

        private SshShellChannel _Channel;

        private string _Directory;

        private FileExplorer _Explorer;

        public ListOperation(string directory, SshSession session, FileExplorer explorer)
        {
            Session = session;
            _Directory = directory;
            _Explorer = explorer;
        }

        public void Start()
        {
            string text = LsOutputParser.LsParser.BuildListCommand(_Directory, followSymlinks: true, recursive: false);
            SshShellChannel sshShellChannel = (_Channel = Session.OpenShellChannel(text));
            sshShellChannel.DataReceived += Channel_ChannelDataReceived;
            sshShellChannel.Finished += Channel_ChannelClosed;
            sshShellChannel.PollingEnabled = true;
        }

        private void Channel_ChannelDataReceived(object sender, byte[] data, int offset, int length, int streamID)
        {
            _AllData.AddRange(data.Skip(offset).Take(length));
        }

        private static int FileEntryComparer(LsOutputParser.LsEntry x, LsOutputParser.LsEntry y)
        {
            if (x.IsDirectory != y.IsDirectory)
            {
                if (x.IsDirectory)
                {
                    return -1;
                }
                return 1;
            }
            if ((x.Name == ".." || y.Name == "..") && x.Name != y.Name)
            {
                if (!(x.Name == ".."))
                {
                    return 1;
                }
                return -1;
            }
            bool xIsHidden = x.Name.StartsWith(".");
            bool yIsHidden = y.Name.StartsWith(".");
            if (xIsHidden != yIsHidden)
            {
                if (!xIsHidden)
                {
                    return -1;
                }
                return 1;
            }
            return StringComparer.InvariantCulture.Compare(x.Name, y.Name);
        }

        private void Channel_ChannelClosed(object sender)
        {
            new Thread(() =>
            {
                try
                {
                    _Channel.Dispose();
                }
                catch
                {
                }
                try
                {
                    List<LsOutputParser.LsEntry> list = (from entry in Encoding.UTF8.GetString(_AllData.ToArray()).Split('\n').Select(LsOutputParser.LsParser.ParseLine)
                                                         where entry.Name != null && entry.Name != "." && (_Directory != "/" || entry.Name != "..")
                                                         select entry).ToList();
                    list.Sort(FileEntryComparer);
                    _Explorer.ProcessListResult(new ListResult
                    {
                        Result = list.ToArray(),
                        Directory = _Directory
                    });
                }
                catch (Exception exception)
                {
                    _Explorer.ProcessListResult(new ListResult
                    {
                        Exception = exception,
                        Directory = _Directory
                    });
                }
            }).Start();
        }

        public void Dispose()
        {
            _Channel.Dispose();
        }
    }

    public enum ItemIcon
    {
        File,
        Folder
    }

    public class ItemWrapper
    {
        public readonly LsOutputParser.LsEntry Entry;

        public readonly string FullPath;

        public string Name => Entry.Name;

        public ItemIcon Icon
        {
            get
            {
                if (!Entry.IsDirectory)
                {
                    return ItemIcon.File;
                }
                return ItemIcon.Folder;
            }
        }

        public string Size
        {
            get
            {
                if (!Entry.IsDirectory)
                {
                    return StringUtils.FormatSize(Entry.Size);
                }
                return "<dir>";
            }
        }

        public ItemWrapper(LsOutputParser.LsEntry item, string baseDir)
        {
            Entry = item;
            FullPath = RemotePathUtils.Combine(baseDir, Entry.Name);
        }

        public override string ToString()
        {
            return Entry.Name;
        }
    }

    private SshTerminalControl _Control;

    private ItemWrapper[] _FilteredItems;

    private string _Filter;

    private string _ProgressText;

    private ListOperation _PendingList;

    private object _PendingListLock = new object();

    private string _WorkingDirectory;

    private bool _IsVisible;

    private bool _IsActive;

    private ListResult _LastListResult;

    public string Title => "File List";

    public ItemWrapper[] FilteredItems
    {
        get
        {
            return _FilteredItems;
        }
        set
        {
            _FilteredItems = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FilteredItems"));
        }
    }

    public string Filter
    {
        get
        {
            return _Filter;
        }
        set
        {
            _Filter = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Filter"));
            UpdateFilteredItems();
        }
    }

    public string ProgressText
    {
        get
        {
            return _ProgressText;
        }
        set
        {
            _ProgressText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ProgressText"));
        }
    }

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

    public Visibility MenuCommandVisibility => Visibility.Visible;

    public bool IsActive
    {
        get
        {
            return _IsActive;
        }
        set
        {
            _IsActive = value;
            ProvideFileList();
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public FileExplorer(SshTerminalControl vtc)
    {
        _Control = vtc;
        _Control.DirectoryChanged += _Control_WorkingDirectoryChanged;
        _WorkingDirectory = vtc.CurrentDirectory;
    }

    public void GoToDirectory(string name)
    {
        try
        {
            _Control.ChangeDirectory(name);
            Filter = "";
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void ProcessListResult(ListResult listResult)
    {
        bool isCurrentDirectory;
        lock (_PendingListLock)
        {
            _PendingList = null;
            isCurrentDirectory = listResult?.Directory == _WorkingDirectory;
            _LastListResult = listResult;
        }
        if (isCurrentDirectory)
        {
            ProgressText = null;
            UpdateFilteredItems();
        }
        else
        {
            ProvideFileList();
        }
        ProgressText = null;
    }

    internal void Refresh()
    {
        ProvideFileList(forceRefresh: true);
    }

    private void UpdateFilteredItems()
    {
        FilteredItems = _LastListResult?.Result?.Where((LsOutputParser.LsEntry entry) => string.IsNullOrEmpty(_Filter) || entry.Name.IndexOf(_Filter, StringComparison.InvariantCultureIgnoreCase) != -1)?.Select((LsOutputParser.LsEntry item) => new ItemWrapper(item, _WorkingDirectory))?.ToArray();
    }

    private void ProvideFileList(bool forceRefresh = false)
    {
        try
        {
            ListOperation listOperation;
            lock (_PendingListLock)
            {
                if ((!forceRefresh && (_LastListResult?.Directory == _WorkingDirectory || !_IsActive)) || (_PendingList != null && _PendingList?.Session == _Control.Session))
                {
                    return;
                }
                listOperation = (_PendingList = new ListOperation(_WorkingDirectory, _Control.Session, this));
            }
            ProgressText = "Loading...";
            listOperation.Start();
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void _Control_WorkingDirectoryChanged(string dir)
    {
        _WorkingDirectory = dir;
        ProvideFileList();
    }

    internal void Show()
    {
        IsVisible = true;
    }

    public void Close()
    {
        IsVisible = false;
    }

    public void Dispose()
    {
        _PendingList?.Dispose();
    }
}
