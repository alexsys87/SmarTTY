using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SmarTTY.GUI;

/// <summary>
/// Browses the file system of a remote host: the directories of the current path can be walked, and a file is either
/// picked from the list or typed into the file-name box.
/// </summary>
public partial class RemoteOpenFileWindow : Window
{
    /// <summary>What the window does with the path the user picks.</summary>
    public enum DialogMode
    {
        Open,
        Save
    }

    private const string FolderIcon = "pack://application:,,,/icons/MainWindow/folder_flat.png";

    private const string FileIcon = "pack://application:,,,/icons/MainWindow/document_flat.png";

    private const string ExecutableIcon = "pack://application:,,,/icons/menu/executable.png";

    private const string OtherIcon = "pack://application:,,,/icons/gear16.png";

    private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };

    private RemoteCommandStarter _runCommand;

    private IRemoteCommand _pendingJob;

    private string _initialDirectory;

    private string _initialFileName;

    private string _currentDirectory;

    private string _requestedDirectory;

    private string _enteredPath;

    private DialogMode _mode;

    private bool _usePrefixedListing;

    // Open mode: a typed name that does not exist may be accepted to create a new file.
    private bool _allowNewFile;

    private bool _newFileDeclined;

    private bool _updatingDirCombo;

    private bool _loaded;

    /// <summary>The path to return: the typed one, or the selected list entry under the current directory.</summary>
    public string SelectedPath
    {
        get
        {
            if (_enteredPath != null)
            {
                return _enteredPath.StartsWith("/") ? _enteredPath : _currentDirectory.TrimEnd('/') + "/" + _enteredPath;
            }
            if (!(SelectedEntry() is FileEntry entry))
            {
                return null;
            }
            return (_currentDirectory == "/") ? entry.Name : _currentDirectory + "/" + entry.Name;
        }
        set
        {
            if (value == null)
            {
                return;
            }
            string dir = RemotePathUtils.GetDirectoryName(value);
            if (dir != null)
            {
                _initialDirectory = dir;
                string fileName = RemotePathUtils.GetFileName(value);
                if (fileName != null)
                {
                    _initialFileName = fileName;
                }
            }
        }
    }

    public DialogMode Mode
    {
        get
        {
            return _mode;
        }
        set
        {
            _mode = value;
            OpenButton.Content = (_mode == DialogMode.Save) ? "_Save" : "_Open";
        }
    }

    private bool IsBusy
    {
        set
        {
            if ((BusyPanel.Visibility == Visibility.Visible) != value)
            {
                BusyPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                FileList.IsEnabled = !value;
                FileNameBox.IsEnabled = !value;
                OpenButton.IsEnabled = !value;
                CancelButton.IsEnabled = !value;
                DirectoryCombo.IsEnabled = !value;
                UpButton.IsEnabled = !value;
            }
        }
    }

    /// <summary>The directory the browser opens in; null lists the working directory of the session.</summary>
    public string InitialDirectory
    {
        get
        {
            return _initialDirectory;
        }
        set
        {
            _initialDirectory = value;
        }
    }

    /// <summary>Accept a typed file name that does not exist (after asking), to create a new file.</summary>
    public bool AllowNewFile
    {
        get
        {
            return _allowNewFile;
        }
        set
        {
            _allowNewFile = value;
        }
    }

    private RemoteOpenFileWindow(RemoteCommandStarter callback)
    {
        InitializeComponent();
        _runCommand = callback;
        _timer.Tick += OnPollTimer;
        Loaded += (object sender, RoutedEventArgs e) => OnLoad();
    }

    /// <summary>Builds a window over <paramref name="callback"/>; the caller sets its properties and calls <see cref="PickPath"/>.</summary>
    internal static RemoteOpenFileWindow Create(RemoteCommandStarter callback)
    {
        return new RemoteOpenFileWindow(callback);
    }

    /// <summary>Shows the window over the active one and returns the picked path, or null when it was cancelled.</summary>
    internal string PickPath()
    {
        WindowOwnership.Attach(this, WindowOwnership.ActiveWindow());
        return (ShowDialog() == true) ? SelectedPath : null;
    }

    /// <summary>Asks for a file on the remote host; returns null when the window was cancelled.</summary>
    public static string Browse(RemoteCommandStarter callback, string filePath = null, string dirPath = null, string hostDisplayName = null, DialogMode dialogMode = DialogMode.Open, bool usePrefixedListing = false, bool allowNewFile = false)
    {
        if (dirPath == null && filePath != null)
        {
            dirPath = RemotePathUtils.GetDirectoryName(filePath);
        }
        if (string.IsNullOrEmpty(dirPath))
        {
            dirPath = RemoteListing.GetWorkingDirectory(callback);
        }
        RemoteOpenFileWindow window = new RemoteOpenFileWindow(callback)
        {
            _initialDirectory = dirPath,
            Title = "Browse File System on " + (hostDisplayName ?? "Remote Host"),
            Mode = dialogMode,
            _initialFileName = RemotePathUtils.GetFileName(filePath),
            _usePrefixedListing = usePrefixedListing,
            _allowNewFile = allowNewFile
        };
        WindowOwnership.Attach(window, WindowOwnership.ActiveWindow());
        if (window.ShowDialog() != true)
        {
            return null;
        }
        return window.SelectedPath;
    }

    private void OnLoad()
    {
        _loaded = true;
        NavigateTo();
        FileList.Focus();
    }

    private bool TryStatPath(string path, out bool isDirectory)
    {
        isDirectory = false;
        if (!path.StartsWith("/"))
        {
            path = _currentDirectory + "/" + path;
        }
        IRemoteCommand remoteCommand = null;
        _runCommand("ls", "-1dLF \"" + path + "\"", ref remoteCommand);
        if (remoteCommand == null)
        {
            return false;
        }
        using (remoteCommand)
        {
            remoteCommand.WaitForExit(-1);
            if (remoteCommand.ExitCode != 0)
            {
                return false;
            }
            List<string> output = remoteCommand.OutputLines;
            if (output == null || output.Count != 1)
            {
                return false;
            }
            isDirectory = output[0].EndsWith("/");
            return true;
        }
    }

    private void UpdateDirectoryCombo(string dirPath)
    {
        DirectoryCombo.Items.Clear();
        string current = dirPath;
        while (dirPath != null && dirPath.Length > 1)
        {
            DirectoryCombo.Items.Add(dirPath);
            dirPath = RemotePathUtils.GetDirectoryName(dirPath);
        }
        DirectoryCombo.Items.Add("/");
        _updatingDirCombo = true;
        DirectoryCombo.Text = current;
    }

    private void NavigateTo(string dirPath = null)
    {
        if (_runCommand == null)
        {
            return;
        }
        string targetDir = (dirPath != null) ? dirPath : (_initialDirectory ?? "/");
        if (!targetDir.StartsWith("/"))
        {
            targetDir = _currentDirectory + "/" + targetDir;
        }
        _requestedDirectory = targetDir;
        try
        {
            IRemoteCommand remoteCommand = null;
            try
            {
                _runCommand("ls", (_usePrefixedListing ? "-F" : "-1ALF") + " \"" + targetDir + "\"", ref remoteCommand);
                if (remoteCommand == null)
                {
                    return;
                }
            }
            catch
            {
                return;
            }
            if (remoteCommand.WaitForExit(300))
            {
                using (remoteCommand)
                {
                    if (remoteCommand.ExitCode == 0 || (remoteCommand.OutputLines != null && remoteCommand.OutputLines.Count != 0))
                    {
                        PopulateList(remoteCommand.OutputLines, remoteCommand.ErrorText);
                    }
                    else
                    {
                        IsBusy = false;
                        MessageBoxes.Error(RemoteListing.DescribeFailure(remoteCommand.ExitCode));
                        _pendingJob = null;
                        _requestedDirectory = null;
                        if (_currentDirectory == null)
                        {
                            NavigateTo("/");
                        }
                    }
                    return;
                }
            }
            IsBusy = true;
            _pendingJob?.Dispose();
            _pendingJob = remoteCommand;
            _timer.Start();
        }
        catch (Exception exception)
        {
            MessageBoxes.Error(exception.Message);
            IsBusy = false;
            _pendingJob = null;
            _timer.Stop();
            _requestedDirectory = null;
        }
    }

    private void OnPollTimer(object sender, EventArgs e)
    {
        if (_pendingJob == null)
        {
            _timer.Stop();
            _requestedDirectory = null;
            IsBusy = false;
        }
        else if (_pendingJob.WaitForExit(0))
        {
            IsBusy = false;
            if (_pendingJob.ExitCode == 0 || _pendingJob.OutputLines.Count != 0)
            {
                _timer.Stop();
                PopulateList(_pendingJob.OutputLines, _pendingJob.ErrorText);
                _requestedDirectory = null;
            }
            else
            {
                _timer.Stop();
                MessageBoxes.Error(RemoteListing.DescribeFailure(_pendingJob.ExitCode));
                _requestedDirectory = null;
            }
            _pendingJob.Dispose();
            _pendingJob = null;
        }
    }

    private void PopulateList(List<string> lines, string errorText)
    {
        if (string.IsNullOrEmpty(errorText))
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ErrorText.Visibility = Visibility.Visible;
            ErrorText.Text = errorText;
        }
        UpdateDirectoryCombo(_requestedDirectory);
        _currentDirectory = _requestedDirectory;
        FileList.Items.Clear();
        if (lines == null)
        {
            return;
        }
        Regex statFailure = new Regex("stat '(.*)' failed:.*");
        foreach (string item in RemoteListing.SortEntries(lines, _usePrefixedListing))
        {
            if (string.IsNullOrEmpty(item))
            {
                continue;
            }
            string entryName = item;
            string icon = FileIcon;
            if (!_usePrefixedListing)
            {
                switch (item[item.Length - 1])
                {
                    case '/':
                        icon = FolderIcon;
                        break;
                    case '*':
                        icon = ExecutableIcon;
                        break;
                    case '=':
                    case '@':
                    case '|':
                        icon = OtherIcon;
                        break;
                }
                entryName = item.TrimEnd('/', '*', '@', '|', '=');
            }
            else
            {
                switch (item[0])
                {
                    case '-':
                        if (item.StartsWith("- "))
                        {
                            entryName = item.Substring(2);
                        }
                        break;
                    case 'd':
                        if (item.StartsWith("d "))
                        {
                            entryName = item.Substring(2);
                            icon = FolderIcon;
                        }
                        break;
                    case 'l':
                        if (item.StartsWith("ld "))
                        {
                            entryName = item.Substring(3);
                            icon = FolderIcon;
                        }
                        else if (item.StartsWith("l- ") || item.StartsWith("l? "))
                        {
                            entryName = item.Substring(3);
                        }
                        break;
                    case 's':
                        if (statFailure.IsMatch(item))
                        {
                            continue;
                        }
                        break;
                }
            }
            FileList.Items.Add(CreateItem(new FileEntry(entryName, icon)));
        }
        if (_initialFileName != null)
        {
            SelectItem(_initialFileName);
            _initialFileName = null;
        }
        else if (FileList.Items.Count > 0)
        {
            // Focus the first entry without selecting it: the file-name box stays empty until the user picks one.
            ((ListBoxItem)FileList.Items[0]).Focus();
        }
    }

    private FileEntry SelectedEntry()
    {
        return (FileList.SelectedItem as ListBoxItem)?.Tag as FileEntry;
    }

    private static ListBoxItem CreateItem(FileEntry entry)
    {
        StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Image { Source = new BitmapImage(new Uri(entry.IconPath)), Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = entry.Name });
        return new ListBoxItem { Content = content, Tag = entry };
    }

    private void SelectItem(string itemName)
    {
        if (itemName == null)
        {
            return;
        }
        foreach (object item in FileList.Items)
        {
            if (item is ListBoxItem row && row.Tag is FileEntry entry && entry.Name == itemName)
            {
                FileList.SelectedItem = row;
                FileList.ScrollIntoView(row);
                break;
            }
        }
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDirectory != null && _currentDirectory.Length > 1)
        {
            int slashIndex = _currentDirectory.LastIndexOf('/');
            string parentPath = _currentDirectory.Substring(0, slashIndex).TrimEnd('/');
            if (slashIndex == 0)
            {
                parentPath = "/";
            }
            _initialFileName = _currentDirectory.Substring(slashIndex + 1);
            NavigateTo(parentPath);
        }
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ActivateSelection();
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            ActivateSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            Up_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ActivateSelection()
    {
        FileEntry entry = SelectedEntry();
        if (entry == null)
        {
            Open_Click(this, null);
            return;
        }
        if (entry.IconPath != FolderIcon)
        {
            Open_Click(this, null);
        }
        else if (_currentDirectory != null)
        {
            string childPath = (_currentDirectory == "/") ? "/" + entry.Name : _currentDirectory + "/" + entry.Name;
            NavigateTo(childPath.TrimEnd('/'));
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FileEntry entry = SelectedEntry();
        if (entry != null && entry.IconPath != FolderIcon)
        {
            FileNameBox.Text = entry.Name;
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        FileEntry entry = SelectedEntry();
        if (!(FileNameBox.Text != "" && TryAcceptEnteredPath()) && entry != null)
        {
            if (entry.IconPath != FolderIcon)
            {
                DialogResult = true;
                return;
            }
            NavigateTo((_currentDirectory == "/") ? "/" + entry.Name : _currentDirectory + "/" + entry.Name);
        }
    }

    private bool TryAcceptEnteredPath()
    {
        if (!TryStatPath(FileNameBox.Text, out bool isDirectory))
        {
            if (_mode == DialogMode.Save)
            {
                _enteredPath = FileNameBox.Text;
                DialogResult = true;
                return true;
            }
            if (_allowNewFile && FileNameBox.Text.Trim().Length != 0 && !FileNameBox.Text.EndsWith("/"))
            {
                if (MessageBoxes.Ask(FileNameBox.Text + " does not exist. Create a new file?"))
                {
                    _enteredPath = FileNameBox.Text;
                    DialogResult = true;
                }
                else
                {
                    _newFileDeclined = true;
                }
                // Handled either way: no "does not exist" error on top of the question.
                return true;
            }
            return false;
        }
        if (isDirectory)
        {
            NavigateTo(FileNameBox.Text);
            FileNameBox.Text = "";
        }
        else
        {
            _enteredPath = FileNameBox.Text;
            DialogResult = true;
        }
        return true;
    }

    private void DirectoryCombo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            if (DirectoryCombo.Text.Length > 1 && DirectoryCombo.Text.EndsWith("/"))
            {
                DirectoryCombo.Text = DirectoryCombo.Text.TrimEnd('/');
            }
            NavigateTo(DirectoryCombo.Text);
            e.Handled = true;
        }
    }

    private void FileNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return)
        {
            return;
        }
        _newFileDeclined = false;
        if (!TryAcceptEnteredPath())
        {
            MessageBoxes.Error(FileNameBox.Text + " does not exist");
        }
        else if (DialogResult == true || _newFileDeclined)
        {
            // A new file was accepted or declined: there is no directory to list.
            return;
        }
        NavigateTo(FileNameBox.Text);
        e.Handled = true;
    }

    private void DirectoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingDirCombo)
        {
            _updatingDirCombo = false;
        }
        else if (_loaded && DirectoryCombo.SelectedItem != null)
        {
            NavigateTo(DirectoryCombo.SelectedItem.ToString());
        }
    }

    private void BusyCancel_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        IsBusy = false;
        _requestedDirectory = null;
        _pendingJob = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _pendingJob?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>One entry of the list: the file name and the icon chosen for its type.</summary>
    private sealed class FileEntry
    {
        public FileEntry(string name, string iconPath)
        {
            Name = name;
            IconPath = iconPath;
        }

        public string Name { get; }

        public string IconPath { get; }
    }
}
