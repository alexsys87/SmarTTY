using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.IO;
using SmarTTY.LocalRepo;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

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

        private string _Directory;

        private FileExplorer _Explorer;

        public ListOperation(string directory, SshSession session, FileExplorer explorer)
        {
            Session = session;
            _Directory = directory;
            _Explorer = explorer;
        }

        // The listing runs as a helper command (RunCommand) rather than a channel of its own: helper commands do not
        // overlap with setting up a PTY channel, which Dropbear on OpenWRT does not survive (see Ssh/PtySetupGate.cs).
        public void Start()
        {
            string text = LsOutputParser.LsParser.BuildListCommand(_Directory, followSymlinks: true, recursive: false);
            new Thread(() =>
            {
                try
                {
                    List<string> lines = Session.RunCommand(text, out _);
                    if (lines == null)
                    {
                        throw new Exception("Failed to list " + _Directory);
                    }
                    List<LsOutputParser.LsEntry> list = (from entry in lines.Select(LsOutputParser.LsParser.ParseLine)
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
            })
            {
                IsBackground = true
            }.Start();
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

        public void Dispose()
        {
        }
    }

    // Files and folders copied or cut in a file list; shared by the file lists of all tabs of a session.
    private class RemoteClipboard
    {
        public SshSession Session;

        public ItemWrapper[] Items;

        public bool Cut;
    }

    private static RemoteClipboard _Clipboard;

    public class ItemWrapper
    {
        public readonly LsOutputParser.LsEntry Entry;

        public readonly string FullPath;

        public string Name => Entry.Name;

        public ItemIcon Icon
        {
            get
            {
                return Entry.IsDirectory ? ItemIcon.Folder : RemoteFileTypes.IconFor(Entry.Name);
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

    public string WorkingDirectory => _WorkingDirectory;

    /// <summary>
    /// Tells the package manager of the host when it is OpenWrt (null otherwise or while the probe has not answered yet): the
    /// "Install package" items are offered only then. Set by the tab, which knows the session.
    /// </summary>
    public Func<LocalRepoPackageManager?> PackageManagerProvider { get; set; }

    /// <summary>The package manager of the OpenWrt host (null if the host is not OpenWrt or has not answered the probe yet).</summary>
    public LocalRepoPackageManager? PackageManager => PackageManagerProvider?.Invoke();

    /// <summary>True if files copied or cut in this session can be pasted here.</summary>
    public bool CanPaste => _Clipboard != null && _Clipboard.Session == _Control.Session;

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

    // Names in the current directory as last listed (for name clashes and copy names).
    private HashSet<string> CurrentNames()
    {
        ListResult listResult = _LastListResult;
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
        if (listResult?.Result != null && listResult.Directory == _WorkingDirectory)
        {
            foreach (LsOutputParser.LsEntry entry in listResult.Result)
            {
                names.Add(entry.Name);
            }
        }
        return names;
    }

    public void CopyToClipboard(IEnumerable<ItemWrapper> items, bool cut)
    {
        ItemWrapper[] selected = items.Where((ItemWrapper item) => item.Name != "..").ToArray();
        if (selected.Length == 0)
        {
            return;
        }
        _Clipboard = new RemoteClipboard
        {
            Session = _Control.Session,
            Items = selected,
            Cut = cut
        };
    }

    /// <summary>
    /// Pastes the copied or cut files and folders into the current directory. A copy into the folder it came from
    /// gets a "(copy)" name; for other name clashes the user decides whether to replace the existing entry.
    /// </summary>
    public void Paste()
    {
        RemoteClipboard clipboard = _Clipboard;
        if (clipboard == null)
        {
            return;
        }
        if (clipboard.Session != _Control.Session)
        {
            MessageBoxes.Info("The files were copied in another SSH session. Copy them again in this session's file list.");
            return;
        }
        string targetDirectory = _WorkingDirectory;
        HashSet<string> names = CurrentNames();
        List<string> commands = new List<string>();
        foreach (ItemWrapper item in clipboard.Items)
        {
            string sourceDirectory = RemotePathUtils.GetDirectoryName(item.FullPath);
            bool sameDirectory = RemoteFileOperations.IsSameOrInside(sourceDirectory, targetDirectory) && RemoteFileOperations.IsSameOrInside(targetDirectory, sourceDirectory);
            if (item.Entry.IsDirectory && RemoteFileOperations.IsSameOrInside(targetDirectory, item.FullPath))
            {
                MessageBoxes.Error("'" + item.Name + "' cannot be " + (clipboard.Cut ? "moved" : "copied") + " into itself.");
                return;
            }
            if (sameDirectory && clipboard.Cut)
            {
                continue;
            }
            string name = item.Name;
            bool replace = false;
            if (sameDirectory)
            {
                name = RemoteFileOperations.MakeCopyName(item.Name, item.Entry.IsDirectory, names);
            }
            else if (names.Contains(name))
            {
                System.Windows.Forms.DialogResult answer = MessageBoxes.YesNoCancel("'" + name + "' already exists in " + targetDirectory + ".\n\nReplace it" + (item.Entry.IsDirectory ? " (the existing folder and everything in it is deleted)" : "") + "?\n\nYes: replace, No: skip this item, Cancel: stop.");
                if (answer == System.Windows.Forms.DialogResult.Cancel)
                {
                    return;
                }
                if (answer == System.Windows.Forms.DialogResult.No)
                {
                    continue;
                }
                replace = true;
            }
            names.Add(name);
            string destination = RemoteFileOperations.Join(targetDirectory, name);
            commands.Add(clipboard.Cut ? RemoteFileOperations.MoveCommand(item.FullPath, destination, replace) : RemoteFileOperations.CopyCommand(item.FullPath, destination, replace));
        }
        if (commands.Count == 0)
        {
            return;
        }
        RunOperations((clipboard.Cut ? "Moving " : "Copying ") + Describe(commands.Count) + "...", commands, () =>
        {
            // Cut files are pasted once; copied ones can be pasted again.
            if (clipboard.Cut && _Clipboard == clipboard)
            {
                _Clipboard = null;
            }
        });
    }

    public void Delete(IEnumerable<ItemWrapper> items)
    {
        ItemWrapper[] selected = items.Where((ItemWrapper item) => item.Name != "..").ToArray();
        if (selected.Length == 0)
        {
            return;
        }
        string list = string.Join("\n", selected.Take(10).Select((ItemWrapper item) => item.Name + (item.Entry.IsDirectory ? "/" : "")));
        if (selected.Length > 10)
        {
            list += "\n... and " + (selected.Length - 10) + " more";
        }
        bool hasFolders = selected.Any((ItemWrapper item) => item.Entry.IsDirectory);
        if (!MessageBoxes.AskWarn("Delete " + Describe(selected.Length) + " from " + _WorkingDirectory + "?\n\n" + list + "\n\n" + (hasFolders ? "Folders are deleted with everything in them. " : "") + "This cannot be undone."))
        {
            return;
        }
        RunOperations("Deleting " + Describe(selected.Length) + "...", new List<string> { RemoteFileOperations.DeleteCommand(selected.Select((ItemWrapper item) => item.FullPath)) }, null);
    }

    /// <summary>
    /// Unpacks the selected .tar.gz, .tgz and .tar archives into the current directory (the one the list shows).
    /// </summary>
    public void Extract(IEnumerable<ItemWrapper> items)
    {
        ItemWrapper[] archives = items.Where((ItemWrapper item) => !item.Entry.IsDirectory && RemoteFileOperations.IsExtractableArchive(item.Name)).ToArray();
        if (archives.Length == 0)
        {
            return;
        }
        List<string> commands = archives.Select((ItemWrapper item) => RemoteFileOperations.ExtractCommand(item.FullPath, _WorkingDirectory)).ToList();
        RunOperations(archives.Length == 1 ? "Extracting " + archives[0].Name + "..." : "Extracting " + archives.Length + " archives...", commands, null);
    }

    public void Rename(ItemWrapper item, string newName)
    {
        if (item == null || item.Name == ".." || newName == item.Name)
        {
            return;
        }
        string error = RemoteFileOperations.ValidateName(newName);
        if (error != null)
        {
            MessageBoxes.Error(error);
            return;
        }
        string destination = RemoteFileOperations.Join(RemotePathUtils.GetDirectoryName(item.FullPath), newName);
        RunOperations("Renaming...", new List<string> { RemoteFileOperations.MoveCommand(item.FullPath, destination, replace: false) }, null);
    }

    public void CreateEntry(string name, bool folder)
    {
        string error = RemoteFileOperations.ValidateName(name);
        if (error != null)
        {
            MessageBoxes.Error(error);
            return;
        }
        string path = RemoteFileOperations.Join(_WorkingDirectory, name);
        RunOperations(folder ? "Creating the folder..." : "Creating the file...", new List<string> { folder ? RemoteFileOperations.CreateDirectoryCommand(path) : RemoteFileOperations.CreateFileCommand(path) }, null);
    }

    /// <summary>A name for a new file or folder that does not exist yet in the current directory.</summary>
    public string SuggestNewName(string baseName, bool folder)
    {
        HashSet<string> names = CurrentNames();
        if (!names.Contains(baseName))
        {
            return baseName;
        }
        string stem = baseName;
        string extension = "";
        int dot = baseName.LastIndexOf('.');
        if (!folder && dot > 0)
        {
            stem = baseName.Substring(0, dot);
            extension = baseName.Substring(dot);
        }
        for (int number = 2; ; number++)
        {
            string candidate = stem + " (" + number + ")" + extension;
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Describe(int count)
    {
        return count == 1 ? "1 item" : count + " items";
    }

    /// <summary>
    /// Runs the commands one after another on a background thread (as helper commands of the session), then refreshes
    /// the list and reports the commands that failed.
    /// </summary>
    private void RunOperations(string progressText, List<string> commands, Action onSuccess)
    {
        SshSession session = _Control.Session;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        ProgressText = progressText;
        new Thread(() =>
        {
            List<string> errors = new List<string>();
            foreach (string command in commands)
            {
                List<string> output = session.RunCommand(command, out int? exitCode);
                if (exitCode != 0)
                {
                    string message = string.Join("\n", (output ?? new List<string>()).Where((string line) => line.Trim() != ""));
                    errors.Add(message != "" ? message : (exitCode.HasValue ? "The command failed with exit code " + exitCode : "The connection is not available"));
                }
            }
            dispatcher.BeginInvoke((Action)(() =>
            {
                ProgressText = null;
                Refresh();
                if (errors.Count != 0)
                {
                    MessageBoxes.Error(string.Join("\n", errors.Take(10)) + (errors.Count > 10 ? "\n..." : ""));
                }
                else
                {
                    onSuccess?.Invoke();
                }
            }));
        })
        {
            IsBackground = true
        }.Start();
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
