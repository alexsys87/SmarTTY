using SmarTTY.GUI.Backend;
using SmarTTY.LocalRepo;
using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
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

    /// <summary>The entry of the list that is being edited in the box (null: the box adds a new folder).</summary>
    private string _Editing;

    /// <summary>The folders the running server was started with; null when it is not running.</summary>
    private string[] _AppliedFolders;

    /// <summary>How long a typed folder is looked for: an unreachable network path can take much longer to give up.</summary>
    private static readonly TimeSpan FolderCheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Set when the user chose "Open package manager": the caller opens it after this window is closed.</summary>
    public bool OpenPackageManagerRequested { get; private set; }

    /// <summary>Set when the user chose "Edit feeds file": the path on the device that the caller opens in an editor tab after this window is closed.</summary>
    public string FileToEdit { get; private set; }

    public LocalRepoWindow(SessionWrapper session)
    {
        InitializeComponent();
        _Session = session;
        _Host = session.LocalRepo;
        _Manager = _Host.IsRunning ? _Host.Manager : (session.LocalRepoManager ?? LocalRepoPackageManager.Opkg);
        _Settings = LocalRepoSettings.Load();
        _Folders = new ObservableCollection<string>(_Settings.Folders);
        FoldersList.ItemsSource = _Folders;
        // The folders a server that is already running (this window was closed meanwhile) serves are the ones saved at its start.
        _AppliedFolders = _Host.IsRunning ? _Settings.Folders.ToArray() : null;
        _Folders.CollectionChanged += (object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateState();
        PortBox.Text = (_Host.IsRunning ? _Host.Port : _Settings.Port).ToString();
        TrustKeyBox.Visibility = _Manager == LocalRepoPackageManager.Apk ? Visibility.Visible : Visibility.Collapsed;
        TrustKeyBox.IsChecked = _Settings.TrustBuildKey;
        AllowUnsignedBox.Visibility = _Manager == LocalRepoPackageManager.Opkg ? Visibility.Visible : Visibility.Collapsed;
        AllowUnsignedBox.IsChecked = _Settings.AllowUnsignedFeeds;
        TargetPackagesBox.IsChecked = _Settings.AddTargetPackages;
        DisableOfficialBox.IsChecked = _Settings.DisableOfficialFeeds;
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
        // The list can be changed while the server runs; the change is served after "Restart with these folders".
        FolderBox.IsEnabled = idle;
        BrowseButton.IsEnabled = idle;
        AddButton.IsEnabled = idle;
        EditButton.IsEnabled = idle && FoldersList.SelectedItems.Count == 1;
        RemoveButton.IsEnabled = idle;
        FoldersList.IsEnabled = idle;
        bool foldersChanged = running && _AppliedFolders != null && !_Folders.SequenceEqual(_AppliedFolders, StringComparer.OrdinalIgnoreCase);
        ApplyButton.Visibility = foldersChanged ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = idle;
        PortBox.IsEnabled = editable;
        TrustKeyBox.IsEnabled = editable;
        AllowUnsignedBox.IsEnabled = editable;
        SignGeneratedBox.IsEnabled = editable;
        TargetPackagesBox.IsEnabled = editable;
        DisableOfficialBox.IsEnabled = editable;
        StartButton.IsEnabled = editable;
        StopButton.IsEnabled = running && idle;
        CleanButton.IsEnabled = editable;
        EditFeedsButton.IsEnabled = idle;
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
            if (foldersChanged)
            {
                text.AppendLine().Append("The list of folders was changed: press \"Restart with these folders\" to serve it.");
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

    /// <summary>
    /// Adds the folder that was typed (or picked), or puts it in the place of the entry that is being edited. The folder is looked
    /// for in the background, in its spelling as typed and, for WSL, in the other one; a folder that cannot be found is described
    /// (how much of the path exists, what is there) and can still be added, for example while the WSL distribution is not started.
    /// Returns false when nothing was added.
    /// </summary>
    private async Task<bool> AddFolderAsync(string text)
    {
        string folder = LocalRepoFolders.Clean(text);
        if (folder.Length == 0)
        {
            return false;
        }
        SetBusy(true);
        string found;
        string description = null;
        try
        {
            Task<string> look = Task.Run(() => LocalRepoFolders.FindExisting(folder, System.IO.Directory.Exists));
            if (await Task.WhenAny(look, Task.Delay(FolderCheckTimeout)) == look)
            {
                found = look.Result;
                if (found == null)
                {
                    description = await Task.Run(() => LocalRepoFolders.DescribeMissing(folder, System.IO.Directory.Exists, ListFolders));
                }
            }
            else
            {
                found = null;
                description = "The folder could not be reached in " + (int)FolderCheckTimeout.TotalSeconds + " seconds: " + folder;
            }
        }
        finally
        {
            SetBusy(false);
        }
        if (found == null)
        {
            if (!MessageBoxes.Ask(description + "\n\nAdd it to the list anyway? It has to be reachable when the server starts."))
            {
                return false;
            }
            found = folder;
        }
        PutFolder(found);
        FolderBox.Clear();
        return true;
    }

    /// <summary>
    /// The folders to serve: the listed ones and, when "Add kernel modules of the build" is on, the target package folders
    /// (bin/targets/.../packages) of the builds that the listed folders belong to, if they are not listed already. Says in the log what was added.
    /// </summary>
    private string[] WithTargetPackages(string[] listed)
    {
        if (TargetPackagesBox.IsChecked != true)
        {
            return listed;
        }
        List<string> all = listed.ToList();
        List<string> added = new List<string>();
        foreach (string folder in listed)
        {
            IList<string> extra = LocalRepoFolders.FindTargetPackageFolders(folder, System.IO.Directory.Exists, ListFolders, LocalRepoFolders.ReadArchitecture);
            foreach (string target in extra)
            {
                if (!all.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    all.Add(target);
                    added.Add(target);
                }
            }
        }
        if (added.Count > 0)
        {
            _Host.Report("Kernel modules of the build are served too: " + string.Join(", ", added.ToArray()));
        }
        return all.ToArray();
    }

    private static IList<string> ListFolders(string directory)
    {
        return System.IO.Directory.GetDirectories(directory).Select(System.IO.Path.GetFileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Adds the folder to the list, or replaces the entry that is being edited (keeping its place).</summary>
    private void PutFolder(string folder)
    {
        int index = _Editing == null ? -1 : _Folders.IndexOf(_Editing);
        EndEdit();
        bool present = _Folders.Contains(folder, StringComparer.OrdinalIgnoreCase);
        if (index >= 0 && index < _Folders.Count)
        {
            if (present && !string.Equals(_Folders[index], folder, StringComparison.OrdinalIgnoreCase))
            {
                // The edited entry became the same as another one: one of them is enough.
                _Folders.RemoveAt(index);
            }
            else
            {
                _Folders[index] = folder;
            }
        }
        else if (!present)
        {
            _Folders.Add(folder);
        }
    }

    private void BeginEdit()
    {
        if (FoldersList.SelectedItems.Count != 1)
        {
            return;
        }
        _Editing = (string)FoldersList.SelectedItem;
        AddButton.Content = "Update";
        FolderBox.Text = _Editing;
        FolderBox.Focus();
        FolderBox.SelectAll();
    }

    private void EndEdit()
    {
        _Editing = null;
        AddButton.Content = "Add";
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        string picked;
        using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog())
        {
            dialog.Description = "Select a folder of the OpenWrt build with packages (a feed, or a folder that contains feeds). A WSL or network folder that is not in this tree can be typed into the box of the window instead.";
            dialog.ShowNewFolderButton = false;
            string start = LocalRepoFolders.Clean(FolderBox.Text);
            if (start.Length > 0 && System.IO.Directory.Exists(start))
            {
                dialog.SelectedPath = start;
            }
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            {
                return;
            }
            picked = dialog.SelectedPath;
        }
        await AddFolderAsync(picked);
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        await AddFolderAsync(FolderBox.Text);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        BeginEdit();
    }

    private void FoldersList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FoldersList.IsEnabled && e.OriginalSource is FrameworkElement element && element.DataContext is string)
        {
            BeginEdit();
        }
    }

    private void FoldersList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            BeginEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            Remove_Click(sender, e);
            e.Handled = true;
        }
    }

    private void FoldersList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateState();
    }

    private async void FolderBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            e.Handled = true;
            await AddFolderAsync(FolderBox.Text);
        }
        else if (e.Key == Key.Escape && _Editing != null)
        {
            EndEdit();
            FolderBox.Clear();
            e.Handled = true;
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (string folder in FoldersList.SelectedItems.Cast<string>().ToList())
        {
            if (folder == _Editing)
            {
                EndEdit();
                FolderBox.Clear();
            }
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
        // A folder typed into the box but not added yet is taken too; if it is not (the user declined), nothing is started.
        if (!string.IsNullOrWhiteSpace(FolderBox.Text) && !await AddFolderAsync(FolderBox.Text))
        {
            return;
        }
        string[] listed = _Folders.ToArray();
        string[] folders = WithTargetPackages(listed);
        _Settings.Folders = listed.ToList();
        _Settings.AddTargetPackages = TargetPackagesBox.IsChecked == true;
        _Settings.Port = port;
        bool trustKey = TrustKeyBox.IsChecked == true && _Manager == LocalRepoPackageManager.Apk;
        _Settings.TrustBuildKey = TrustKeyBox.IsChecked == true;
        bool allowUnsigned = AllowUnsignedBox.IsChecked == true && _Manager == LocalRepoPackageManager.Opkg;
        _Settings.AllowUnsignedFeeds = AllowUnsignedBox.IsChecked == true;
        bool signGenerated = SignGeneratedBox.IsChecked == true && _Manager == LocalRepoPackageManager.Opkg;
        _Settings.SignGeneratedIndexes = SignGeneratedBox.IsChecked == true;
        bool disableOfficial = DisableOfficialBox.IsChecked == true;
        _Settings.DisableOfficialFeeds = disableOfficial;
        _Settings.Save();
        LocalRepoPackageManager manager = _Manager;
        SetBusy(true);
        try
        {
            await Task.Run(() => _Host.Start(ssh, folders, port, manager, trustKey, allowUnsigned, signGenerated, disableOfficial));
            _AppliedFolders = listed;
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

    /// <summary>Stops the running server and starts it again with the list of folders as it is now (same port and options).</summary>
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        SshSession ssh = _Session.Session;
        if (ssh == null)
        {
            MessageBoxes.Error("There is no SSH connection.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(FolderBox.Text) && !await AddFolderAsync(FolderBox.Text))
        {
            return;
        }
        string[] listed = _Folders.ToArray();
        string[] folders = WithTargetPackages(listed);
        _Settings.AddTargetPackages = TargetPackagesBox.IsChecked == true;
        int port = _Host.Port;
        LocalRepoPackageManager manager = _Manager;
        bool trustKey = TrustKeyBox.IsChecked == true && manager == LocalRepoPackageManager.Apk;
        bool allowUnsigned = AllowUnsignedBox.IsChecked == true && manager == LocalRepoPackageManager.Opkg;
        bool signGenerated = SignGeneratedBox.IsChecked == true && manager == LocalRepoPackageManager.Opkg;
        bool disableOfficial = DisableOfficialBox.IsChecked == true;
        _Settings.DisableOfficialFeeds = disableOfficial;
        _Settings.Folders = listed.ToList();
        _Settings.Save();
        SetBusy(true);
        try
        {
            _Host.Report("Restarting the server with " + folders.Length + (folders.Length == 1 ? " folder." : " folders."));
            await Task.Run(() =>
            {
                _Host.Stop(ssh);
                _Host.Start(ssh, folders, port, manager, trustKey, allowUnsigned, signGenerated, disableOfficial);
            });
            _AppliedFolders = listed;
        }
        catch (Exception exception)
        {
            _Host.Report("Restart failed: " + exception.Message.Replace('\n', ' '));
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

    private void EditFeeds_Click(object sender, RoutedEventArgs e)
    {
        FileToEdit = _Manager == LocalRepoPackageManager.Apk ? ApkFeedConfig.DefaultConfigFile : OpkgFeedConfig.DefaultConfigFile;
        Close();
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
