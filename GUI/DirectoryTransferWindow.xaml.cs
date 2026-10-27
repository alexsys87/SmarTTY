using SmarTTY.IO;
using SmarTTY.Ssh;
using SmarTTY.Sync;
using SmarTTY.Utilities;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace SmarTTY.GUI;

/// <summary>
/// Transfers a directory between the local machine and the host of <see cref="_session"/>, either as an upload or as
/// a download. The copy runs on a background thread and reports progress through <see cref="DirectoryCopier"/>.
/// </summary>
public partial class DirectoryTransferWindow : Window
{
    private const string DownloadImage = "pack://application:,,,/icons/dialog-scp-download.png";

    private const string UploadImage = "pack://application:,,,/icons/dialog-scp-upload.png";

    private readonly SshSession _session;

    private readonly bool _isUpload;

    private readonly StringBuilder _errorLog = new StringBuilder();

    private DateTime _lastProgressUpdate;

    private string _currentPath;

    private volatile bool _cancelRequested;

    private bool _closeRequested;

    /// <summary>The local directory shown in the window (the source of an upload, the target of a download).</summary>
    public string LocalDirectory
    {
        get
        {
            return _isUpload ? TopBox.Text : BottomBox.Text;
        }
        set
        {
            if (_isUpload)
            {
                TopBox.Text = value;
            }
            else
            {
                BottomBox.Text = value;
            }
        }
    }

    /// <summary>The remote directory shown in the window (the target of an upload, the source of a download).</summary>
    public string RemoteDirectory
    {
        get
        {
            return _isUpload ? BottomBox.Text : TopBox.Text;
        }
        set
        {
            if (_isUpload)
            {
                BottomBox.Text = value;
            }
            else
            {
                TopBox.Text = value;
            }
        }
    }

    public bool IsUpload => _isUpload;

    public DirectoryTransferWindow(SshSession sshSession, bool isUpload)
    {
        InitializeComponent();
        _session = sshSession;
        _isUpload = isUpload;
        if (isUpload)
        {
            // The upload window asks for the local directory first: the rows swap.
            TransferImage.Source = new BitmapImage(new Uri(UploadImage));
            TopLabel.Text = "Local directory:";
            BottomLabel.Text = "Remote directory:";
            StartButton.Content = "_Upload";
            Title = "Upload a directory";
        }
        else
        {
            TransferImage.Source = new BitmapImage(new Uri(DownloadImage));
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        string remoteDir = RemoteDirectory;
        string localDir = LocalDirectory;
        bool useTar = TarRadio.IsChecked == true;
        if (string.IsNullOrEmpty(localDir))
        {
            MessageBoxes.Error("Local directory cannot be empty");
            return;
        }
        if (string.IsNullOrEmpty(remoteDir))
        {
            MessageBoxes.Error("Remote directory cannot be empty");
            return;
        }
        if (_isUpload && !Directory.Exists(localDir))
        {
            MessageBoxes.Error(localDir + " is not a valid directory");
            return;
        }
        _cancelRequested = false;
        new Thread(() => RunTransfer(remoteDir, localDir, useTar)).Start();
        SetInputEnabled(false);
        TransferProgress.Visibility = Visibility.Visible;
    }

    private void RunTransfer(string remoteDir, string localDir, bool useTar)
    {
        DirectoryCopier directoryCopier = new DirectoryCopier(_session) { UseTar = useTar };
        directoryCopier.Progress += OnProgress;
        directoryCopier.Log += OnLog;
        bool completed = false;
        _errorLog.Length = 0;
        Exception exception = null;
        try
        {
            if (_isUpload)
            {
                directoryCopier.Upload(localDir, remoteDir);
            }
            else
            {
                directoryCopier.Download(remoteDir, localDir);
            }
            completed = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception transferException)
        {
            exception = transferException;
        }
        try
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                StatusText.Text = "Inactive";
                StatsText.Text = "";
                SetInputEnabled(true);
                TransferProgress.Visibility = Visibility.Collapsed;
                if (_errorLog.Length != 0)
                {
                    TransferErrorWindow.Show(exception, _errorLog.ToString(), this);
                }
                else if (exception != null)
                {
                    MessageBoxes.Error("Cannot transfer a directory: " + exception.Message);
                }
                if (completed || _closeRequested)
                {
                    Close();
                }
            }));
        }
        catch
        {
        }
    }

    private void OnLog(object sender, string logMessage)
    {
        _errorLog.AppendLine(logMessage);
    }

    private void OnProgress(DirectoryCopier.CopyProgress copyProgress)
    {
        if (_cancelRequested)
        {
            throw new OperationCanceledException();
        }
        if (copyProgress.Path != null)
        {
            _currentPath = copyProgress.Path;
        }
        // File-by-file copying reports every chunk; only refresh the labels a few times per second.
        if (copyProgress.Stage != DirectoryCopier.CopyStage.CopyingFile || !((DateTime.Now - _lastProgressUpdate).TotalMilliseconds < 100.0))
        {
            _lastProgressUpdate = DateTime.Now;
            DirectoryCopier.CopyStage stage = copyProgress.Stage;
            string currentPath = _currentPath;
            int filesDone = copyProgress.FilesDone;
            int filesTotal = copyProgress.FilesTotal;
            long bytesDone = copyProgress.BytesDone;
            long bytesTotal = copyProgress.BytesTotal;
            Dispatcher.BeginInvoke((Action)(() => UpdateProgress(stage, currentPath, filesDone, filesTotal, bytesDone, bytesTotal)));
        }
    }

    private void UpdateProgress(DirectoryCopier.CopyStage copyStage, string currentPath, int filesDone, int filesTotal, long bytesDone, long bytesTotal)
    {
        if (bytesDone > bytesTotal)
        {
            bytesTotal = bytesDone;
        }
        if (bytesTotal == 0L)
        {
            bytesTotal = 1L;
        }
        switch (copyStage)
        {
            case DirectoryCopier.CopyStage.Counting:
                StatusText.Text = (currentPath == null) ? "Determining directory size..." : "Scanning " + currentPath + "...";
                TransferProgress.IsIndeterminate = true;
                StatsText.Text = StringUtils.FormatSize(bytesDone) + "B found" + FormatFileCount(filesDone, filesTotal);
                break;
            case DirectoryCopier.CopyStage.CopyingFile:
                StatusText.Text = (_isUpload ? "Uploading " : "Downloading ") + currentPath + "...";
                TransferProgress.IsIndeterminate = false;
                TransferProgress.Value = bytesDone * TransferProgress.Maximum / bytesTotal;
                StatsText.Text = StringUtils.FormatSize(bytesDone) + "B done" + FormatFileCount(filesDone, filesTotal);
                break;
            case DirectoryCopier.CopyStage.CopyingTarEntry:
                StatusText.Text = "Linking " + currentPath + "...";
                TransferProgress.IsIndeterminate = true;
                StatsText.Text = FormatFileCount(filesDone, filesTotal).TrimStart(' ');
                break;
            case DirectoryCopier.CopyStage.StreamingTar:
                StatusText.Text = "Downloading external symbolic link targets...";
                TransferProgress.IsIndeterminate = true;
                StatsText.Text = StringUtils.FormatSize(bytesDone) + "B done" + FormatFileCount(filesDone, filesTotal);
                break;
        }
    }

    /// <summary>
    /// " - 12 of 300 files" once the total is known, otherwise " - 12 files" (the count of the scan so far). The
    /// labels of the tar stages show the file count alone, so the leading separator is trimmed there.
    /// </summary>
    private static string FormatFileCount(int filesDone, int filesTotal)
    {
        if (filesTotal > 0)
        {
            return " - " + filesDone + " of " + filesTotal + " files";
        }
        if (filesDone > 0)
        {
            return " - " + filesDone + " files";
        }
        return "";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancelRequested = true;
        if (StartButton.IsEnabled)
        {
            Close();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        // The transfer is running: cancel it and wait for the background thread to close the window.
        if (!StartButton.IsEnabled)
        {
            _cancelRequested = true;
            _closeRequested = true;
            e.Cancel = true;
        }
    }

    private void TopBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpload)
        {
            BrowseLocal(TopBox);
        }
        else
        {
            BrowseRemote(TopBox);
        }
    }

    private void BottomBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpload)
        {
            BrowseRemote(BottomBox);
        }
        else
        {
            BrowseLocal(BottomBox);
        }
    }

    private void BrowseRemote(TextBox box)
    {
        string selected = RemoteFolderBrowserWindow.Browse(StartRemoteCommand, usePrefixedListing: false, box.Text, _session.HostName);
        if (selected != null)
        {
            box.Text = selected;
        }
    }

    private static void BrowseLocal(TextBox box)
    {
        string selected = LocalFolderBrowserWindow.Browse(box.Text);
        if (selected != null)
        {
            box.Text = selected;
        }
    }

    private void StartRemoteCommand(string command, string arguments, ref IRemoteCommand remoteCommand)
    {
        SshShellChannel sshShellChannel = _session.OpenShellChannel(command + " " + arguments);
        if (sshShellChannel != null)
        {
            remoteCommand = new RemoteCommandJob(sshShellChannel);
        }
    }

    private void SetInputEnabled(bool enabled)
    {
        TarRadio.IsEnabled = enabled;
        ScpRadio.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
        TopBox.IsEnabled = enabled;
        BottomBox.IsEnabled = enabled;
    }
}
