using SmarTTY.GUI.Backend;
using SmarTTY.LocalRepo;
using SmarTTY.Ssh;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SmarTTY.GUI;

/// <summary>
/// Tools > Local OpenWrt repository server: serves package folders of an OpenWrt build on this computer to the device of the
/// active SSH session (see <see cref="LocalRepoHost"/>). The server keeps running after the window is closed, until it is stopped
/// or the session ends.
/// </summary>
public partial class LocalRepoWindow : Window
{
    private const int MaxLogCharacters = 200000;

    private readonly SessionWrapper _Session;

    private readonly LocalRepoHost _Host;

    /// <summary>The package manager of the device: apk on OpenWrt 25.12 and newer, otherwise opkg.</summary>
    private readonly LocalRepoPackageManager _Manager;

    private readonly LocalRepoSettings _Settings;

    private readonly ObservableCollection<string> _Folders;

    private bool _Busy;

    /// <summary>Set when the user chose "Open package manager": the caller opens it after this window is closed.</summary>
    public bool OpenPackageManagerRequested { get; private set; }

    public LocalRepoWindow(SessionWrapper session)
    {
        InitializeComponent();
        _Session = session;
        _Host = session.LocalRepo;
        _Manager = _Host.IsRunning ? _Host.Manager : (session.LocalRepoManager ?? LocalRepoPackageManager.Opkg);
        _Settings = LocalRepoSettings.Load();
        _Folders = new ObservableCollection<string>(_Settings.Folders);
        FoldersList.ItemsSource = _Folders;
        PortBox.Text = (_Host.IsRunning ? _Host.Port : _Settings.Port).ToString();
        TrustKeyBox.Visibility = _Manager == LocalRepoPackageManager.Apk ? Visibility.Visible : Visibility.Collapsed;
        TrustKeyBox.IsChecked = _Settings.TrustBuildKey;
        AllowUnsignedBox.Visibility = _Manager == LocalRepoPackageManager.Opkg ? Visibility.Visible : Visibility.Collapsed;
        AllowUnsignedBox.IsChecked = _Settings.AllowUnsignedFeeds;
        SignGeneratedBox.Visibility = _Manager == LocalRepoPackageManager.Opkg ? Visibility.Visible : Visibility.Collapsed;
        SignGeneratedBox.IsChecked = _Settings.SignGeneratedIndexes;

        StringBuilder history = new StringBuilder();
        foreach (string line in _Host.GetHistory())
        {
            history.AppendLine(line);
        }
        LogBox.Text = history.ToString();
        LogBox.ScrollToEnd();

        _Host.Log += Host_Log;
        Closed += (object sender, EventArgs e) => _Host.Log -= Host_Log;
        UpdateState();
    }

    private void Host_Log(string line)
    {
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (LogBox.Text.Length > MaxLogCharacters)
            {
                // Whole lines are dropped, so that the log does not start in the middle of one.
                string text = LogBox.Text;
                int cut = text.IndexOf('\n', text.Length - MaxLogCharacters / 2);
                LogBox.Text = cut < 0 ? "" : text.Substring(cut + 1);
            }
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
            // The host also reports what happens without this window: the tunnel restored after a reconnect, the server
            // stopped when the session ended. The status and the buttons follow it.
            UpdateState();
        }));
    }

    private void UpdateState()
    {
        bool running = _Host.IsRunning;
        bool idle = !_Busy;
        bool editable = !running && idle;
        FolderBox.IsEnabled = editable;
        BrowseButton.IsEnabled = editable;
        AddButton.IsEnabled = editable;
        RemoveButton.IsEnabled = editable;
        FoldersList.IsEnabled = editable;
        PortBox.IsEnabled = editable;
        TrustKeyBox.IsEnabled = editable;
        AllowUnsignedBox.IsEnabled = editable;
        SignGeneratedBox.IsEnabled = editable;
        StartButton.IsEnabled = editable;
        StopButton.IsEnabled = running && idle;
        CleanButton.IsEnabled = editable;
        PackagesButton.IsEnabled = running && idle;

        if (!running)
        {
            StatusText.Text = _Busy ? "Working..." : "The server is stopped.";
        }
        else if (!_Host.IsTunnelCurrent(_Session.Session))
        {
            StatusText.Text = "The server is running, but the SSH connection has been replaced and the tunnel is not restored yet. If it does not recover (see the log), stop the server and start it again.";
        }
        else
        {
            StringBuilder text = new StringBuilder("Running on port " + _Host.Port + ". Feeds added on the device:");
            foreach (LocalRepoFeed feed in _Host.Feeds)
            {
                text.AppendLine().Append("  ").Append(_Host.DescribeFeed(feed));
            }
            StatusText.Text = text.ToString();
        }
    }

    private void SetBusy(bool busy)
    {
        _Busy = busy;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        UpdateState();
    }

    private bool TryGetPort(out int port)
    {
        if (int.TryParse(PortBox.Text.Trim(), out port) && port >= 1 && port <= 65535)
        {
            return true;
        }
        MessageBoxes.Error("The TCP port must be a number from 1 to 65535.");
        return false;
    }

    private void AddFolder(string folder)
    {
        folder = folder?.Trim().Trim('"');
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }
        if (!System.IO.Directory.Exists(folder))
        {
            MessageBoxes.Error("The folder was not found: " + folder);
            return;
        }
        if (!_Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            _Folders.Add(folder);
        }
        FolderBox.Clear();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog())
        {
            dialog.Description = "Select a folder of the OpenWrt build with packages (a feed, or a folder that contains feeds)";
            dialog.ShowNewFolderButton = false;
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                AddFolder(dialog.SelectedPath);
            }
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        AddFolder(FolderBox.Text);
    }

    private void FolderBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            AddFolder(FolderBox.Text);
            e.Handled = true;
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (string folder in FoldersList.SelectedItems.Cast<string>().ToList())
        {
            _Folders.Remove(folder);
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetPort(out int port))
        {
            return;
        }
        SshSession ssh = _Session.Session;
        if (ssh == null)
        {
            MessageBoxes.Error("There is no SSH connection.");
            return;
        }
        // A folder typed into the box but not added yet is taken too.
        if (!string.IsNullOrWhiteSpace(FolderBox.Text))
        {
            AddFolder(FolderBox.Text);
        }
        string[] folders = _Folders.ToArray();
        _Settings.Folders = folders.ToList();
        _Settings.Port = port;
        bool trustKey = TrustKeyBox.IsChecked == true && _Manager == LocalRepoPackageManager.Apk;
        _Settings.TrustBuildKey = TrustKeyBox.IsChecked == true;
        bool allowUnsigned = AllowUnsignedBox.IsChecked == true && _Manager == LocalRepoPackageManager.Opkg;
        _Settings.AllowUnsignedFeeds = AllowUnsignedBox.IsChecked == true;
        bool signGenerated = SignGeneratedBox.IsChecked == true && _Manager == LocalRepoPackageManager.Opkg;
        _Settings.SignGeneratedIndexes = SignGeneratedBox.IsChecked == true;
        _Settings.Save();
        LocalRepoPackageManager manager = _Manager;
        SetBusy(true);
        try
        {
            await Task.Run(() => _Host.Start(ssh, folders, port, manager, trustKey, allowUnsigned, signGenerated));
        }
        catch (Exception exception)
        {
            _Host.Report("Start failed: " + exception.Message.Replace('\n', ' '));
            MessageBoxes.Exception(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        SshSession ssh = _Session.Session;
        SetBusy(true);
        try
        {
            await Task.Run(() => _Host.Stop(ssh));
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        SshSession ssh = _Session.Session;
        SetBusy(true);
        try
        {
            LocalRepoPackageManager manager = _Manager;
            string problem = await Task.Run(() => _Host.RemoveFeeds(ssh, manager));
            _Host.Report(problem ?? "The feeds of this server were removed from " + (manager == LocalRepoPackageManager.Apk ? ApkFeedConfig.DefaultConfigFile : OpkgFeedConfig.DefaultConfigFile) + " on the device.");
            if (problem != null)
            {
                MessageBoxes.Error(problem);
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Packages_Click(object sender, RoutedEventArgs e)
    {
        OpenPackageManagerRequested = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_Busy)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
