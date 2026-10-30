using SmarTTY.Controls;
using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SmarTTY.GUI;

/// <summary>
/// Picks a path on a device that has only a shell: the folders are read with <c>ls</c> typed into that shell
/// (see <see cref="GzipBase64Transfer.ListDirectory"/>). A file to fetch is picked from the list, a destination to send to is
/// a folder plus an optional file name; the path can also be typed. The picker is used by the gzip + base64 transfer window.
/// </summary>
public partial class RemoteDevicePathWindow : Window
{
    private sealed class DeviceEntry
    {
        public string Name { get; set; }

        public bool IsDirectory { get; set; }
    }

    private readonly GzipBase64Transfer.DirectoryLister _list;

    private readonly bool _forReceive;

    private string _initialFolder;

    private string _initialName;

    private string _currentDirectory;

    private bool _busy;

    private string _selectedPath;

    private RemoteDevicePathWindow(GzipBase64Transfer.DirectoryLister list, bool forReceive)
    {
        InitializeComponent();
        _list = list;
        _forReceive = forReceive;
        if (forReceive)
        {
            Title = "Select a File on the Device";
            NameLabel.Text = "File name:";
        }
        else
        {
            Title = "Select a Destination on the Device";
            NameLabel.Text = "File name (optional):";
            StatusText.Text = "Leave the name empty to keep the name of the file.";
        }
        // A listing keeps the line of the device; the window stays until it is over, so that no transfer finds the line taken.
        Closing += (object sender, System.ComponentModel.CancelEventArgs e) => e.Cancel = _busy;
        Loaded += async (object sender, RoutedEventArgs e) => await OnLoadAsync();
    }

    /// <summary>
    /// Shows the picker over <paramref name="owner"/> and returns the chosen path on the device, or null when cancelled.
    /// <paramref name="currentText"/> is the path typed so far; it decides the folder to start in and the name offered.
    /// </summary>
    internal static string Browse(Window owner, GzipBase64Transfer.DirectoryLister list, bool forReceive, string currentText)
    {
        RemoteDevicePathWindow window = new RemoteDevicePathWindow(list, forReceive);
        SplitPath(currentText, out string folder, out string name);
        window._initialFolder = folder;
        window._initialName = name;
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        return window.ShowDialog() == true ? window._selectedPath : null;
    }

    /// <summary>"/etc/config/network" is the folder "/etc/config/" and the name "network"; "/tmp/" has no name; "fw.bin" has no folder.</summary>
    private static void SplitPath(string text, out string folder, out string name)
    {
        text = (text ?? string.Empty).Trim();
        int slash = text.LastIndexOf('/');
        if (slash < 0)
        {
            folder = ".";
            name = text;
            return;
        }
        folder = (slash == 0) ? "/" : text.Substring(0, slash);
        name = text.Substring(slash + 1);
    }

    private async System.Threading.Tasks.Task OnLoadAsync()
    {
        if (!await NavigateAsync(_initialFolder) && _currentDirectory == null)
        {
            // The typed folder does not exist: start from the shell's own folder instead.
            await NavigateAsync(".");
        }
        EntryList.Focus();
    }

    /// <summary>Lists <paramref name="path"/> and shows it. Returns false (and says why in the window) when it could not be read.</summary>
    private async System.Threading.Tasks.Task<bool> NavigateAsync(string path)
    {
        if (_busy)
        {
            return false;
        }
        SetBusy(true);
        try
        {
            GzipBase64Transfer.DirectoryListing listing = await _list(path);
            _currentDirectory = listing.Path;
            PathBox.Text = listing.Path;
            EntryList.Items.Clear();
            List<string> directories = new List<string>(listing.Directories);
            List<string> files = new List<string>(listing.Files);
            directories.Sort(StringComparer.Ordinal);
            files.Sort(StringComparer.Ordinal);
            foreach (string directory in directories)
            {
                EntryList.Items.Add(new DeviceEntry { Name = directory, IsDirectory = true });
            }
            foreach (string file in files)
            {
                EntryList.Items.Add(new DeviceEntry { Name = file });
            }
            if (!string.IsNullOrEmpty(_initialName))
            {
                NameBox.Text = _initialName;
                _initialName = null;
            }
            StatusText.Text = _forReceive ? string.Empty : "Leave the name empty to keep the name of the file.";
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        EntryList.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        UpButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        AcceptButton.IsEnabled = !busy;
        if (busy)
        {
            StatusText.Text = "Reading the folder from the device...";
        }
        else
        {
            EntryList.Focus();
        }
    }

    private static string Combine(string folder, string name)
    {
        if (name.StartsWith("/", StringComparison.Ordinal))
        {
            return name;
        }
        return folder.EndsWith("/", StringComparison.Ordinal) ? folder + name : folder + "/" + name;
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentDirectory) || _currentDirectory == "/")
        {
            return;
        }
        string trimmed = _currentDirectory.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        await NavigateAsync(slash <= 0 ? "/" : trimmed.Substring(0, slash));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await NavigateAsync(_currentDirectory);
    }

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            e.Handled = true;
            await NavigateAsync(PathBox.Text.Trim());
        }
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntryList.SelectedItem is DeviceEntry entry && !entry.IsDirectory)
        {
            NameBox.Text = entry.Name;
        }
    }

    private async void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await ActivateSelectionAsync();
    }

    private async void EntryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            e.Handled = true;
            await ActivateSelectionAsync();
        }
        else if (e.Key == Key.Back)
        {
            e.Handled = true;
            Up_Click(sender, e);
        }
    }

    private async System.Threading.Tasks.Task ActivateSelectionAsync()
    {
        if (EntryList.SelectedItem is DeviceEntry entry && entry.IsDirectory)
        {
            await NavigateAsync(Combine(_currentDirectory, entry.Name));
        }
        else
        {
            await AcceptAsync();
        }
    }

    private async void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            e.Handled = true;
            await AcceptAsync();
        }
    }

    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        await AcceptAsync();
    }

    private async System.Threading.Tasks.Task AcceptAsync()
    {
        if (_busy || string.IsNullOrEmpty(_currentDirectory))
        {
            return;
        }
        string name = NameBox.Text.Trim();
        // A name that is a folder of the list is opened, as in a file dialog.
        foreach (object item in EntryList.Items)
        {
            if (item is DeviceEntry entry && entry.IsDirectory && entry.Name == name)
            {
                await NavigateAsync(Combine(_currentDirectory, name));
                NameBox.Text = string.Empty;
                return;
            }
        }
        if (name.Length == 0)
        {
            if (_forReceive)
            {
                StatusText.Text = "Select a file or type its name.";
                return;
            }
            // A folder alone: the file keeps its name.
            _selectedPath = _currentDirectory.EndsWith("/", StringComparison.Ordinal) ? _currentDirectory : _currentDirectory + "/";
        }
        else
        {
            _selectedPath = Combine(_currentDirectory, name);
        }
        DialogResult = true;
    }
}
