using SmarTTY.IO;
using SmarTTY.Ssh;
using SmarTTY.Ssh.QuickSync;
using SmarTTY.Sync;
using SmarTTY.Utilities;
using System;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace SmarTTY.GUI;

/// <summary>
/// Synchronizes a local folder with a folder on the host of <see cref="_session"/> in one direction, using the
/// comparison engine in <see cref="QuickSyncRunner"/>. Unlike a directory download or upload, the engine first
/// builds a plan of the differences and then copies only what is missing or out of date, optionally deleting the
/// target files that the source does not have. The synchronization runs on a background thread and reports its
/// progress through <see cref="ISyncEvents"/>.
/// </summary>
public partial class SynchronizeWindow : Window
{
    private const string SyncImageUri = "pack://application:,,,/icons/MainWindow/refresh16.png";

    private readonly SshSession _session;

    private readonly StringBuilder _errorLog = new StringBuilder();

    private DateTime _lastProgressUpdate;

    private volatile bool _cancelRequested;

    private bool _closeRequested;

    public SynchronizeWindow(SshSession sshSession)
    {
        InitializeComponent();
        _session = sshSession;
        SyncImage.Source = new BitmapImage(new Uri(SyncImageUri));
    }

    /// <summary>The local folder, pre-filled by the caller (the tab's current directory or the saved default).</summary>
    public string LocalDirectory
    {
        get
        {
            return LocalBox.Text;
        }
        set
        {
            LocalBox.Text = value;
        }
    }

    /// <summary>The remote folder, pre-filled by the caller with the tab's current directory.</summary>
    public string RemoteDirectory
    {
        get
        {
            return RemoteBox.Text;
        }
        set
        {
            RemoteBox.Text = value;
        }
    }

    private bool IsUpload => UploadRadio.IsChecked == true;

    private void Direction_Changed(object sender, RoutedEventArgs e)
    {
        if (LocalLabel != null)
        {
            LocalLabel.Text = IsUpload ? "Local folder (source):" : "Local folder (target):";
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        string localDir = LocalBox.Text?.Trim();
        string remoteDir = RemoteBox.Text?.Trim();
        if (string.IsNullOrEmpty(localDir) || string.IsNullOrEmpty(remoteDir))
        {
            MessageBoxes.Error("Both folders are required.");
            return;
        }
        _cancelRequested = false;
        LogBox.Clear();
        _errorLog.Length = 0;
        SyncSettings settings = BuildSettings(localDir, remoteDir);
        new Thread(() => RunSync(settings)).Start();
        SetInputEnabled(false);
        TransferProgress.Visibility = Visibility.Visible;
    }

    private SyncSettings BuildSettings(string localDir, string remoteDir)
    {
        return SyncRequest.Create(
            upload: IsUpload,
            localPath: localDir,
            remotePath: remoteDir,
            excludeMasks: ExcludeBox.Text,
            deleteExtraneous: DeleteExtraneousBox.IsChecked == true,
            skipSymlinks: SkipSymlinksBox.IsChecked == true,
            skipHardlinks: SkipHardlinksBox.IsChecked == true,
            overwriteTrigger: SelectedTrigger());
    }

    private QuickSyncOverwriteTrigger SelectedTrigger()
    {
        switch (OverwriteBox.SelectedIndex)
        {
            case 0:
                return QuickSyncOverwriteTrigger.Always;
            case 2:
                return QuickSyncOverwriteTrigger.OnlyIfMissing;
            default:
                return QuickSyncOverwriteTrigger.Stat;
        }
    }

    private void RunSync(SyncSettings settings)
    {
        // The Sysprogs helper is built from a C++ source that this build does not ship, so the engine is pinned to the
        // file-by-file one instead of letting "Auto" try to install the helper on every run.
        QuickSyncRunner runner = new QuickSyncRunner(null) { _engineKind = QuickSyncRunner.SyncEngineKind.FileByFile };
        SyncEvents events = new SyncEvents(this);
        bool completed = false;
        Exception exception = null;
        try
        {
            TransferStatistics statistics = runner.Run(_session, settings, events);
            events.Summary = statistics.Describe();
            completed = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception syncException)
        {
            exception = syncException;
        }
        try
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                StatusText.Text = events.Summary ?? "Inactive";
                SetInputEnabled(true);
                TransferProgress.Visibility = Visibility.Collapsed;
                if (exception != null)
                {
                    if (_errorLog.Length != 0)
                    {
                        TransferErrorWindow.Show(exception, _errorLog.ToString(), this);
                    }
                    else
                    {
                        MessageBoxes.Error("Cannot synchronize the folders: " + exception.Message);
                    }
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

    private void AppendLog(string text)
    {
        Dispatcher.BeginInvoke((Action)(() =>
        {
            LogBox.AppendText(text + "\r\n");
            LogBox.ScrollToEnd();
        }));
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
        // The synchronization is running: ask it to stop and wait for the background thread to close the window.
        if (!StartButton.IsEnabled)
        {
            _cancelRequested = true;
            _closeRequested = true;
            e.Cancel = true;
        }
    }

    private void LocalBrowse_Click(object sender, RoutedEventArgs e)
    {
        string selected = LocalFolderBrowserWindow.Browse(LocalBox.Text, "Select the local folder to synchronize");
        if (selected != null)
        {
            LocalBox.Text = selected;
        }
    }

    private void RemoteBrowse_Click(object sender, RoutedEventArgs e)
    {
        void StartRemoteCommand(string command, string arguments, ref IRemoteCommand remoteCommand)
        {
            SshShellChannel channel = _session.OpenShellChannel(command + " " + arguments);
            if (channel != null)
            {
                remoteCommand = new RemoteCommandJob(channel);
            }
        }
        string selected = RemoteFolderBrowserWindow.Browse(StartRemoteCommand, usePrefixedListing: false, RemoteBox.Text, _session.HostName);
        if (selected != null)
        {
            RemoteBox.Text = selected;
        }
    }

    private void SetInputEnabled(bool enabled)
    {
        StartButton.IsEnabled = enabled;
        DownloadRadio.IsEnabled = enabled;
        UploadRadio.IsEnabled = enabled;
        LocalBox.IsEnabled = enabled;
        RemoteBox.IsEnabled = enabled;
        LocalBrowseButton.IsEnabled = enabled;
        RemoteBrowseButton.IsEnabled = enabled;
        OverwriteBox.IsEnabled = enabled;
        DeleteExtraneousBox.IsEnabled = enabled;
        SkipSymlinksBox.IsEnabled = enabled;
        SkipHardlinksBox.IsEnabled = enabled;
        ExcludeBox.IsEnabled = enabled;
    }

    /// <summary>
    /// Reports the synchronization to the window: progress and warnings arrive on the background thread, so every
    /// change to the controls is marshalled to the UI thread. Throwing <see cref="OperationCanceledException"/> from
    /// a callback is how the engine is asked to stop.
    /// </summary>
    private sealed class SyncEvents : ISyncEvents
    {
        private readonly SynchronizeWindow _window;

        public string Summary;

        public SyncEvents(SynchronizeWindow window)
        {
            _window = window;
        }

        public IPermissionProvider PermissionProvider => null;

        public void OnLog(SyncLogMessage syncLogMessage)
        {
            if (_window._cancelRequested)
            {
                throw new OperationCanceledException();
            }
            string text = syncLogMessage?.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                _window.AppendLog(text);
            }
        }

        public void OnProgress(SyncProgress syncProgress)
        {
            if (_window._cancelRequested)
            {
                throw new OperationCanceledException();
            }
            // A large folder reports progress for every file; refresh the labels a few times per second.
            if ((DateTime.Now - _window._lastProgressUpdate).TotalMilliseconds < 100.0)
            {
                return;
            }
            _window._lastProgressUpdate = DateTime.Now;
            string status = syncProgress.StageDescription;
            if (!string.IsNullOrEmpty(syncProgress.CurrentFile))
            {
                status += " " + syncProgress.CurrentFile;
            }
            string stats = syncProgress.Total > 0L
                ? StringUtils.FormatSize(syncProgress.Transferred, appendUnit: true) + " of " + StringUtils.FormatSize(syncProgress.Total, appendUnit: true)
                : StringUtils.FormatSize(syncProgress.Transferred, appendUnit: true);
            bool indeterminate = syncProgress.Total <= 0L;
            double value = syncProgress.Fraction < 0.0 ? 0.0 : syncProgress.Fraction * _window.TransferProgress.Maximum;
            _window.Dispatcher.BeginInvoke((Action)(() =>
            {
                _window.StatusText.Text = status;
                _window.StatsText.Text = stats;
                _window.TransferProgress.IsIndeterminate = indeterminate;
                if (!indeterminate)
                {
                    _window.TransferProgress.Value = value;
                }
            }));
        }

        public void OnWarning(SyncWarning syncWarning)
        {
            string text = syncWarning?.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                _window._errorLog.AppendLine(text);
                _window.AppendLog(text);
            }
        }
    }
}
