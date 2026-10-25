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
        System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            RootFolder = Environment.SpecialFolder.MyComputer
        };
        try
        {
            dialog.SelectedPath = LocalDirectoryBox.Text;
        }
        catch
        {
            // A path that is no longer valid is not a reason to fail the browse.
        }
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            LocalDirectoryBox.Text = dialog.SelectedPath;
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
