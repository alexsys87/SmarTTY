using SmarTTY.GUI.Backend;
using SmarTTY.IO;
using SmarTTY.Ssh;
using SmarTTY.Sync;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace SmarTTY.GUI;

/// <summary>
/// Copies a directory from one SSH host to another without going through the local machine: the source
/// <c>tar</c> stream is written straight into the target <c>tar -x</c> (see
/// <see cref="DirectoryCopier.CopyBetweenHosts"/>). The copy runs on a background thread and reports its progress
/// through the same <see cref="DirectoryCopier.CopyProgress"/> as a directory download.
/// </summary>
public partial class HostToHostCopyWindow : Window
{
    private const string TransferImageUri = "pack://application:,,,/icons/dialog-scp-download.png";

    private readonly StringBuilder _errorLog = new StringBuilder();

    private DateTime _lastProgressUpdate;

    private volatile bool _cancelRequested;

    private bool _closeRequested;

    /// <summary>A host offered as the source or the target: the session and the name shown in the list.</summary>
    private sealed class HostEntry
    {
        public readonly SessionWrapper Session;

        public readonly string Name;

        public HostEntry(SessionWrapper session, string name)
        {
            Session = session;
            Name = name;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// Opens the window. <paramref name="sessions"/> are the open SSH sessions that can be used; a host that is
    /// offline is listed but cannot be picked.
    /// </summary>
    public HostToHostCopyWindow(SessionWrapper activeSession, IEnumerable<SessionWrapper> sessions, string initialSourcePath)
    {
        InitializeComponent();
        TransferImage.Source = new BitmapImage(new Uri(TransferImageUri));
        ObservableCollection<HostEntry> entries = new ObservableCollection<HostEntry>();
        foreach (SessionWrapper session in sessions)
        {
            if (session != null && !session.IsPlaceholder && !session.IsDevice && session.Session != null)
            {
                entries.Add(new HostEntry(session, session.Title + " - " + session.Session.HostName));
            }
        }
        SourceHostBox.ItemsSource = entries;
        DestinationHostBox.ItemsSource = entries;
        int activeIndex = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Session == activeSession)
            {
                activeIndex = i;
                break;
            }
        }
        SourceHostBox.SelectedIndex = activeIndex;
        if (!string.IsNullOrEmpty(initialSourcePath))
        {
            SourcePathBox.Text = initialSourcePath;
        }
        // The target is another host by default, so the copy is not from a host onto itself.
        DestinationHostBox.SelectedIndex = entries.Count > 1 ? (activeIndex == 0 ? 1 : 0) : activeIndex;
    }

    private SessionWrapper SelectedSource => (SourceHostBox.SelectedItem as HostEntry)?.Session;

    private SessionWrapper SelectedDestination => (DestinationHostBox.SelectedItem as HostEntry)?.Session;

    private void SourceHost_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SessionWrapper session = SelectedSource;
        if (session != null && string.IsNullOrEmpty(SourcePathBox.Text))
        {
            SourcePathBox.Text = session.TryExtractCurrentDir() ?? "~";
        }
    }

    private void DestinationHost_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SessionWrapper session = SelectedDestination;
        if (session != null && string.IsNullOrEmpty(DestinationPathBox.Text))
        {
            DestinationPathBox.Text = session.TryExtractCurrentDir() ?? "~";
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        SessionWrapper source = SelectedSource;
        SessionWrapper destination = SelectedDestination;
        string sourcePath = SourcePathBox.Text?.Trim();
        string destinationPath = DestinationPathBox.Text?.Trim();
        if (source == null || destination == null)
        {
            MessageBoxes.Error("Choose the host to copy from and the host to copy to.");
            return;
        }
        if (source == destination)
        {
            MessageBoxes.Error("The source and the target are the same host. Use \"Copy a directory\" for a local copy.");
            return;
        }
        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(destinationPath))
        {
            MessageBoxes.Error("Both folders are required.");
            return;
        }
        _cancelRequested = false;
        LogBox.Clear();
        _errorLog.Length = 0;
        new Thread(() => RunTransfer(source, sourcePath, destination, destinationPath)).Start();
        SetInputEnabled(false);
        TransferProgress.Visibility = Visibility.Visible;
    }

    private void RunTransfer(SessionWrapper source, string sourcePath, SessionWrapper destination, string destinationPath)
    {
        bool completed = false;
        Exception exception = null;
        try
        {
            DirectoryCopier.CopyBetweenHosts(source.Session, sourcePath, destination.Session, destinationPath, OnLog);
            completed = true;
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
                        MessageBoxes.Error("Cannot copy the directory: " + exception.Message);
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

    private void OnLog(object sender, string logLine)
    {
        if (_cancelRequested)
        {
            throw new OperationCanceledException();
        }
        if (string.IsNullOrEmpty(logLine))
        {
            return;
        }
        // The tar output is mostly file names; show a few lines a second so a large directory does not flood the box.
        if ((DateTime.Now - _lastProgressUpdate).TotalMilliseconds >= 100.0)
        {
            _lastProgressUpdate = DateTime.Now;
            string line = logLine;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                StatusText.Text = "Copying " + line;
                LogBox.AppendText(line + "\r\n");
                LogBox.ScrollToEnd();
            }));
        }
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
        // The copy is running: ask it to stop and wait for the background thread to close the window.
        if (!StartButton.IsEnabled)
        {
            _cancelRequested = true;
            _closeRequested = true;
            e.Cancel = true;
        }
    }

    private void SourceBrowse_Click(object sender, RoutedEventArgs e)
    {
        SessionWrapper session = SelectedSource;
        if (session != null)
        {
            Browse(session, SourcePathBox);
        }
    }

    private void DestinationBrowse_Click(object sender, RoutedEventArgs e)
    {
        SessionWrapper session = SelectedDestination;
        if (session != null)
        {
            Browse(session, DestinationPathBox);
        }
    }

    private static void Browse(SessionWrapper session, TextBox box)
    {
        void StartRemoteCommand(string command, string arguments, ref IRemoteCommand remoteCommand)
        {
            SshShellChannel channel = session.Session.OpenShellChannel(command + " " + arguments);
            if (channel != null)
            {
                remoteCommand = new RemoteCommandJob(channel);
            }
        }
        string selected = RemoteFolderBrowserWindow.Browse(StartRemoteCommand, usePrefixedListing: false, box.Text, session.Session.HostName);
        if (selected != null)
        {
            box.Text = selected;
        }
    }

    private void SetInputEnabled(bool enabled)
    {
        StartButton.IsEnabled = enabled;
        SourceHostBox.IsEnabled = enabled;
        DestinationHostBox.IsEnabled = enabled;
        SourcePathBox.IsEnabled = enabled;
        DestinationPathBox.IsEnabled = enabled;
        SourceBrowseButton.IsEnabled = enabled;
        DestinationBrowseButton.IsEnabled = enabled;
    }
}
