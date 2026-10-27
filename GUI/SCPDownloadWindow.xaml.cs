using SmarTTY.IO;
using System;
using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// SCP -&gt; Download a file: picks the remote file and the local folder to download it into.
/// </summary>
public partial class SCPDownloadWindow : Window
{
    private readonly RemoteCommandStarter _callback;

    private readonly string _initialDirectory;

    public string RemoteFile
    {
        get
        {
            return RemoteFileBox.Text;
        }
        set
        {
            RemoteFileBox.Text = value;
        }
    }

    public string LocalDirectory
    {
        get
        {
            return LocalDirectoryBox.Text;
        }
        set
        {
            LocalDirectoryBox.Text = value;
        }
    }

    public bool OpenLocalDirAfterDownload
    {
        get
        {
            return OpenLocalDirBox.IsChecked == true;
        }
        set
        {
            OpenLocalDirBox.IsChecked = value;
        }
    }

    public SCPDownloadWindow(RemoteCommandStarter callback, string initialDirectory, bool showCheckBox)
    {
        InitializeComponent();
        _callback = callback;
        _initialDirectory = initialDirectory;
        OpenLocalDirBox.Visibility = showCheckBox ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LocalBrowse_Click(object sender, RoutedEventArgs e)
    {
        string selected = LocalFolderBrowserWindow.Browse(LocalDirectoryBox.Text, "Select the local folder");
        if (selected != null)
        {
            LocalDirectoryBox.Text = selected;
        }
    }

    private void RemoteBrowse_Click(object sender, RoutedEventArgs e)
    {
        string dirPath = (RemoteFileBox.Text == "" && _initialDirectory != null) ? _initialDirectory : null;
        string selected = RemoteOpenFileWindow.Browse(_callback, RemoteFileBox.Text, dirPath, allowNewFile: false);
        if (selected != null)
        {
            RemoteFileBox.Text = selected;
        }
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
