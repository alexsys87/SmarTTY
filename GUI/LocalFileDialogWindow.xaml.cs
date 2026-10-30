using SmarTTY.Controls;
using SmarTTY.IO;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SmarTTY.GUI;

/// <summary>
/// Browses the local file system and picks a file to open or a name to save to (the WPF counterpart of the WinForms
/// <c>OpenFileDialog</c>/<c>SaveFileDialog</c>): the folders of the current path can be walked, a file is picked from
/// the list or typed into the file-name box, and the "file type" box filters what is listed. The rules of the listing
/// and the filter strings are in <see cref="LocalFileListing"/> and <see cref="FileDialogFilter"/>, which have no UI
/// dependency.
/// </summary>
public partial class LocalFileDialogWindow : Window
{
    private readonly ThemedFileDialogRequest _request;

    private readonly List<FileDialogFilterEntry> _filterEntries;

    private string _currentDirectory;

    private string _initialFileName;

    private bool _updatingDirectoryCombo;

    private bool _updatingFilterCombo;

    private bool _loaded;

    private ThemedFileDialogResult _result;

    private LocalFileDialogWindow(ThemedFileDialogRequest request)
    {
        InitializeComponent();
        _request = request;
        _filterEntries = FileDialogFilter.Parse(request.Filter);
        Title = string.IsNullOrEmpty(request.Title) ? "Select a File" : request.Title;
        AcceptButton.Content = (request.Mode == ThemedFileDialogMode.SaveFile) ? "_Save" : "_Open";
        if (request.Multiselect)
        {
            FileList.SelectionMode = SelectionMode.Extended;
        }
        Loaded += (object sender, RoutedEventArgs e) => OnLoad();
    }

    /// <summary>Shows the dialog over the given owner and returns what the user chose, or null when cancelled.</summary>
    internal static ThemedFileDialogResult Browse(ThemedFileDialogRequest request, Window owner)
    {
        LocalFileDialogWindow window = new LocalFileDialogWindow(request);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        return (window.ShowDialog() == true) ? window._result : null;
    }

    /// <summary>Asks for one existing file to open; returns the chosen path, or null when cancelled.</summary>
    internal static string OpenFile(Window owner, string title, string filter, int filterIndex = 1, string initialDirectory = null)
    {
        return Browse(new ThemedFileDialogRequest
        {
            Mode = ThemedFileDialogMode.OpenFile,
            Title = title,
            Filter = filter,
            FilterIndex = filterIndex,
            InitialDirectory = initialDirectory
        }, owner)?.FileName;
    }

    /// <summary>Asks for one or more existing files to open; returns the chosen paths, or null when cancelled.</summary>
    internal static string[] OpenFiles(Window owner, string title, string filter, int filterIndex = 1, string initialDirectory = null)
    {
        ThemedFileDialogResult result = Browse(new ThemedFileDialogRequest
        {
            Mode = ThemedFileDialogMode.OpenFile,
            Multiselect = true,
            Title = title,
            Filter = filter,
            FilterIndex = filterIndex,
            InitialDirectory = initialDirectory
        }, owner);
        return (result == null || result.FileNames.Count == 0) ? null : result.FileNames.ToArray();
    }

    /// <summary>Asks for the destination file to save to; returns the chosen path, or null when cancelled.</summary>
    internal static string SaveFile(Window owner, string title, string filter, int filterIndex = 1, string fileName = null, bool overwritePrompt = true, bool addExtension = false, string initialDirectory = null)
    {
        return Browse(new ThemedFileDialogRequest
        {
            Mode = ThemedFileDialogMode.SaveFile,
            Title = title,
            Filter = filter,
            FilterIndex = filterIndex,
            FileName = fileName,
            OverwritePrompt = overwritePrompt,
            AddExtension = addExtension,
            InitialDirectory = initialDirectory
        }, owner)?.FileName;
    }

    private void OnLoad()
    {
        _loaded = true;
        BuildFilterCombo();
        _initialFileName = _request.FileName;
        NavigateTo(InitialDirectory());
        FileList.Focus();
    }

    private string InitialDirectory()
    {
        if (!string.IsNullOrEmpty(_request.InitialDirectory) && Directory.Exists(_request.InitialDirectory))
        {
            return _request.InitialDirectory;
        }
        if (!string.IsNullOrEmpty(_request.FileName))
        {
            string directory = Path.GetDirectoryName(_request.FileName);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                return directory;
            }
        }
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents) && Directory.Exists(documents))
        {
            return documents;
        }
        List<DriveInfo> drives = LocalFolderTree.GetReadyDrives();
        return (drives.Count > 0) ? drives[0].RootDirectory.FullName : null;
    }

    private void BuildFilterCombo()
    {
        _updatingFilterCombo = true;
        FilterCombo.Items.Clear();
        foreach (FileDialogFilterEntry entry in _filterEntries)
        {
            FilterCombo.Items.Add(entry);
        }
        int index = _request.FilterIndex - 1;
        if (index < 0 || index >= FilterCombo.Items.Count)
        {
            index = 0;
        }
        FilterCombo.SelectedIndex = (FilterCombo.Items.Count > 0) ? index : -1;
        _updatingFilterCombo = false;
    }

    private FileDialogFilterEntry SelectedFilter()
    {
        return FilterCombo.SelectedItem as FileDialogFilterEntry;
    }

    private void NavigateTo(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            ShowError("The folder does not exist: " + (directory ?? ""));
            return;
        }
        _currentDirectory = directory;
        UpdateDirectoryCombo(directory);
        FileList.Items.Clear();
        foreach (LocalFileEntry entry in LocalFileListing.List(directory, SelectedFilter(), includeFiles: true))
        {
            FileList.Items.Add(entry);
        }
        ClearError();
        if (!string.IsNullOrEmpty(_initialFileName))
        {
            SelectItem(_initialFileName);
            _initialFileName = null;
        }
        else
        {
            FileNameBox.Text = "";
            if (FileList.Items.Count > 0)
            {
                FileList.ScrollIntoView(FileList.Items[0]);
            }
        }
    }

    private void UpdateDirectoryCombo(string directory)
    {
        _updatingDirectoryCombo = true;
        DirectoryCombo.Items.Clear();
        string current = directory;
        while (current != null && current.Length > 1)
        {
            DirectoryCombo.Items.Add(current);
            current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        // The chain above only reaches the root of the current drive, so list the other drives to switch to them.
        foreach (DriveInfo drive in LocalFolderTree.GetReadyDrives())
        {
            string root = drive.RootDirectory.FullName;
            if (!DirectoryCombo.Items.Contains(root))
            {
                DirectoryCombo.Items.Add(root);
            }
        }
        DirectoryCombo.Text = directory;
        _updatingDirectoryCombo = false;
    }

    private void SelectItem(string fileName)
    {
        foreach (object item in FileList.Items)
        {
            if (item is LocalFileEntry entry && string.Equals(entry.Name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                FileList.SelectedItem = item;
                FileList.ScrollIntoView(item);
                FileNameBox.Text = entry.Name;
                return;
            }
        }
        FileNameBox.Text = fileName;
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentDirectory))
        {
            return;
        }
        string parent = Path.GetDirectoryName(_currentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent) && !string.Equals(parent, _currentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _initialFileName = Path.GetFileName(_currentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            NavigateTo(parent);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo(_currentDirectory);
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
        if (FileList.SelectedItem is LocalFileEntry entry && entry.IsDirectory)
        {
            NavigateTo(LocalFileListing.Combine(_currentDirectory, entry.Name));
        }
        else
        {
            Accept_Click(this, null);
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is LocalFileEntry entry && !entry.IsDirectory)
        {
            FileNameBox.Text = entry.Name;
        }
    }

    private void DirectoryCombo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            NavigateTo(LocalFolderTree.NormalizePath(DirectoryCombo.Text, _currentDirectory) ?? DirectoryCombo.Text);
            e.Handled = true;
        }
    }

    private void DirectoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingDirectoryCombo)
        {
            return;
        }
        if (_loaded && DirectoryCombo.SelectedItem is string directory)
        {
            NavigateTo(directory);
        }
    }

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFilterCombo && _loaded)
        {
            // Re-list so the files that the new filter hides or shows appear.
            NavigateTo(_currentDirectory);
        }
    }

    private void FileNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            Accept_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (_request.Mode == ThemedFileDialogMode.SaveFile)
        {
            AcceptSave();
        }
        else
        {
            AcceptOpen();
        }
    }

    private void AcceptOpen()
    {
        string typed = FileNameBox.Text.Trim();
        if (typed.Length != 0)
        {
            string full = LocalFileListing.Combine(_currentDirectory, typed);
            if (Directory.Exists(full))
            {
                NavigateTo(full);
                return;
            }
            if (File.Exists(full))
            {
                Finish(new List<string> { full });
                return;
            }
            ShowError("The file does not exist: " + typed);
            return;
        }
        List<string> selected = SelectedFilePaths();
        if (selected.Count == 0)
        {
            ShowError("Select a file or type its name.");
            return;
        }
        Finish(selected);
    }

    private void AcceptSave()
    {
        string typed = FileNameBox.Text.Trim();
        if (typed.Length == 0)
        {
            ShowError("Type a file name.");
            return;
        }
        string name = AddExtension(typed);
        string full = LocalFileListing.Combine(_currentDirectory, name);
        if (Directory.Exists(full))
        {
            ShowError("A folder with this name already exists: " + name);
            return;
        }
        if (File.Exists(full) && _request.OverwritePrompt && !MessageBoxes.Ask(name + " already exists. Replace it?"))
        {
            return;
        }
        Finish(new List<string> { full });
    }

    private string AddExtension(string name)
    {
        if (!_request.AddExtension || Path.HasExtension(name))
        {
            return name;
        }
        FileDialogFilterEntry entry = SelectedFilter();
        if (entry != null && entry.Patterns.Count > 0)
        {
            string extension = Path.GetExtension(entry.Patterns[0]);
            if (!string.IsNullOrEmpty(extension) && extension != ".*")
            {
                return name + extension;
            }
        }
        return name;
    }

    private List<string> SelectedFilePaths()
    {
        List<string> paths = new List<string>();
        foreach (object item in FileList.SelectedItems)
        {
            if (item is LocalFileEntry entry && !entry.IsDirectory)
            {
                string path = LocalFileListing.Combine(_currentDirectory, entry.Name);
                if (path != null)
                {
                    paths.Add(path);
                }
            }
        }
        return paths;
    }

    private void Finish(List<string> fileNames)
    {
        ThemedFileDialogResult result = new ThemedFileDialogResult { FilterIndex = FilterCombo.SelectedIndex + 1 };
        if (_request.Multiselect)
        {
            result.FileNames.AddRange(fileNames);
        }
        else
        {
            result.FileName = fileNames[0];
        }
        _result = result;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
    }
}
