using SmarTTY.GUI.Backend;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VisualTTY;

namespace SmarTTY.GUI;

public partial class PackageManagerWindow : Window
{
    public class ControllerImpl : INotifyPropertyChanged
    {
        private PackageManagerWindow _Window;

        private SessionWrapper _Session;

        private ILinuxPackageManagerClient[] _Clients = new ILinuxPackageManagerClient[8]
        {
            new AptPackageManagerClient(),
            new DnfPackageManagerClient(),
            new ZypperPackageManagerClient(),
            new PacmanPackageManagerClient(),
            new PortagePackageManagerClient(),
            new YumPackageManagerClient(),
            new OpkgPackageManagerClient(),
            new ApkPackageManagerClient()
        };

        private string _ProgressText;

        private ILinuxPackageManagerClient _Client;

        private bool _IsRoot;

        private LinuxPackageInfo[] _AllPackages;

        private IList<LinuxPackageInfo> _FilteredPackages = new LinuxPackageInfo[0];

        private string _Filter;

        private PackageTypeFilter _TypeFilter;

        private volatile bool _Closed;

        public string InstallText
        {
            get
            {
                int installCount = 0;
                int upgradeCount = 0;
                int removeCount = 0;
                foreach (LinuxPackageInfo linuxPackageInfo in _AllPackages ?? new LinuxPackageInfo[0])
                {
                    switch (linuxPackageInfo.Action)
                    {
                        case LinuxPackageInfo.PackageAction.Install:
                            if (linuxPackageInfo.State == LinuxPackageInfo.PackageState.Upgradeable)
                            {
                                upgradeCount++;
                            }
                            else
                            {
                                installCount++;
                            }
                            break;
                        case LinuxPackageInfo.PackageAction.Remove:
                            removeCount++;
                            break;
                    }
                }
                List<string> list = new List<string>();
                if (installCount > 0)
                {
                    list.Add(FormatCount("install", installCount));
                }
                if (upgradeCount > 0)
                {
                    list.Add(FormatCount("upgrade", upgradeCount));
                }
                if (removeCount > 0)
                {
                    list.Add(FormatCount("remove", removeCount));
                }
                string text = string.Join(", ", list.ToArray());
                if (text.Length == 0)
                {
                    return null;
                }
                return char.ToUpper(text[0]) + text.Substring(1);
            }
        }

        /// <summary>How many packages the list shows and how many there are, e.g. "120 of 3512 packages, 4 updates".</summary>
        public string StatusText
        {
            get
            {
                if (_AllPackages == null)
                {
                    return null;
                }
                string total = _AllPackages.Length + " package" + ((_AllPackages.Length == 1) ? "" : "s");
                string text = (_FilteredPackages.Count == _AllPackages.Length) ? total : (_FilteredPackages.Count + " of " + total);
                int updateCount = _AllPackages.Count((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.State == LinuxPackageInfo.PackageState.Upgradeable);
                if (updateCount > 0)
                {
                    text += ", " + updateCount + " update" + ((updateCount == 1) ? "" : "s");
                }
                return text;
            }
        }

        /// <summary>True when some of the available updates are not selected yet.</summary>
        public bool CanSelectUpdates => IsIdle && _AllPackages != null && _AllPackages.Any((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.State == LinuxPackageInfo.PackageState.Upgradeable && linuxPackageInfo.IsChecked != true);

        public string ProgressText
        {
            get
            {
                return _ProgressText;
            }
            set
            {
                _ProgressText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ProgressText"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsIdle"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CanSelectUpdates"));
            }
        }

        /// <summary>False while the package manager is being detected, queried or refreshed.</summary>
        public bool IsIdle => _ProgressText == null;

        public string Filter
        {
            get
            {
                return _Filter;
            }
            set
            {
                _Filter = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Filter"));
                ApplyFilter();
            }
        }

        /// <summary>
        /// The packages that pass the filters. The list is replaced as a whole on every change: filling an
        /// ObservableCollection item by item raises an event per package, which is slow with the tens of
        /// thousands of packages that apt lists.
        /// </summary>
        public IList<LinuxPackageInfo> FilteredPackages
        {
            get
            {
                return _FilteredPackages;
            }
            private set
            {
                _FilteredPackages = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FilteredPackages"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("StatusText"));
            }
        }

        public PackageTypeFilter TypeFilter
        {
            get
            {
                return _TypeFilter;
            }
            set
            {
                _TypeFilter = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TypeFilter"));
                ApplyFilter();
            }
        }

        public PackageCommitOptions Options { get; } = new PackageCommitOptions();

        public event PropertyChangedEventHandler PropertyChanged;

        public void OnChecked()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("InstallText"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CanSelectUpdates"));
        }

        /// <summary>Called when the window is closed: the background queries stop reporting to it.</summary>
        public void OnClosed()
        {
            _Closed = true;
        }

        public ControllerImpl(PackageManagerWindow packageManagerWindow, SessionWrapper session)
        {
            _Window = packageManagerWindow;
            _Session = session;
            ProgressText = "Detecting package manager...";
            StartBackgroundThread(ParseThreadBody);
        }

        private static void StartBackgroundThread(ThreadStart body)
        {
            // A background thread does not keep SmarTTY running if it is closed while a slow command is still running.
            new Thread(body)
            {
                IsBackground = true,
                Name = "Package manager"
            }.Start();
        }

        /// <summary>Runs <paramref name="action"/> on the UI thread unless the window has been closed in the meantime.</summary>
        private void RunOnUIThread(Action action)
        {
            ((DispatcherObject)_Window).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
            {
                if (!_Closed)
                {
                    action();
                }
            }), new object[0]);
        }

        private static string FormatCount(string verb, int count)
        {
            return verb + " " + count + " package" + ((count == 1) ? "" : "s");
        }

        private void ParseThreadBody()
        {
            try
            {
                ILinuxPackageManagerClient bestClient = null;
                int bestScore = 0;
                foreach (ILinuxPackageManagerClient client in _Clients)
                {
                    if (_Closed)
                    {
                        return;
                    }
                    int score = -1;
                    try
                    {
                        score = client.Probe(_Session);
                    }
                    catch
                    {
                    }
                    if (score > bestScore)
                    {
                        bestClient = client;
                        bestScore = score;
                    }
                }
                if (bestClient == null)
                {
                    // Every probe failed because the connection went away: say so instead of "no package manager".
                    _Session.Session?.ThrowIfConnectionLost();
                    throw new Exception("Could not detect the package manager on the remote host");
                }
                _Client = bestClient;
                _IsRoot = PackageIndexUpdater.IsRoot(_Session);
                LoadPackages(null);
            }
            catch (Exception exception)
            {
                RunOnUIThread(() =>
                {
                    MessageBoxes.Exception(exception);
                    _Window.Close();
                });
            }
        }

        /// <summary>Queries the package list and replaces the current one; <paramref name="selection"/> restores pending marks.</summary>
        private void LoadPackages(Dictionary<string, LinuxPackageInfo> selection)
        {
            ProgressText = "Querying package list...";
            LinuxPackageInfo[] allPackages = _Client.QueryAllPackages(_Session);
            if (allPackages == null)
            {
                throw new Exception("Failed to query package list");
            }
            foreach (LinuxPackageInfo linuxPackageInfo in allPackages)
            {
                if (linuxPackageInfo.State == LinuxPackageInfo.PackageState.Installed)
                {
                    linuxPackageInfo.IsChecked = true;
                }
                else if (linuxPackageInfo.State == LinuxPackageInfo.PackageState.Available)
                {
                    linuxPackageInfo.IsChecked = false;
                }
                if (selection != null && selection.TryGetValue(linuxPackageInfo.ID, out LinuxPackageInfo previous) && previous.State == linuxPackageInfo.State)
                {
                    linuxPackageInfo.IsChecked = previous.IsChecked;
                }
            }
            RunOnUIThread(() =>
            {
                // The list is replaced on the UI thread, which is the one that reads it.
                _AllPackages = allPackages;
                ProgressText = null;
                ApplyFilter();
                OnChecked();
            });
        }

        /// <summary>Updates the package lists from the repositories with the detected package manager and reloads the list.</summary>
        public void Refresh()
        {
            if (_Client == null || !IsIdle)
            {
                return;
            }
            Dictionary<string, LinuxPackageInfo> selection = new Dictionary<string, LinuxPackageInfo>();
            foreach (LinuxPackageInfo linuxPackageInfo in _AllPackages ?? new LinuxPackageInfo[0])
            {
                if (linuxPackageInfo.Action != LinuxPackageInfo.PackageAction.None)
                {
                    selection[linuxPackageInfo.ID] = linuxPackageInfo;
                }
            }
            ProgressText = "Updating package lists (" + _Client.GetCommandLineForIndexUpdate() + ")...";
            // Empty the list so that the progress text does not overlap the old rows; it is refilled after the reload.
            FilteredPackages = new LinuxPackageInfo[0];
            StartBackgroundThread(() => RefreshThreadBody(selection));
        }

        private void RefreshThreadBody(Dictionary<string, LinuxPackageInfo> selection)
        {
            try
            {
                PackageIndexUpdater.Run(_Session, _Client, _IsRoot);
                LoadPackages(selection);
            }
            catch (Exception exception)
            {
                RunOnUIThread(() =>
                {
                    ProgressText = null;
                    // Bring back the previous list, the refresh did not replace it.
                    ApplyFilter();
                    MessageBoxes.Exception(exception);
                });
            }
        }

        /// <summary>Marks every available update for installation.</summary>
        public void SelectAllUpdates()
        {
            if (_AllPackages == null || !IsIdle)
            {
                return;
            }
            foreach (LinuxPackageInfo linuxPackageInfo in _AllPackages)
            {
                if (linuxPackageInfo.State == LinuxPackageInfo.PackageState.Upgradeable)
                {
                    linuxPackageInfo.IsChecked = true;
                }
            }
            OnChecked();
        }

        private void ApplyFilter()
        {
            string filter = _Filter;
            Regex regex = null;
            if (filter != null && filter.IndexOfAny(new char[4] { '$', '*', '^', '.' }) != -1)
            {
                try
                {
                    // The timeout keeps a pathological pattern from freezing the window on a long list.
                    regex = new Regex(filter, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                }
                catch
                {
                }
            }
            List<LinuxPackageInfo> filteredPackages = new List<LinuxPackageInfo>();
            LinuxPackageInfo[] linuxPackageInfos = _AllPackages ?? new LinuxPackageInfo[0];
            foreach (LinuxPackageInfo linuxPackageInfo in linuxPackageInfos)
            {
                string name = linuxPackageInfo.Name;
                if (name == null || !MatchesTypeFilter(linuxPackageInfo))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(filter))
                {
                    if (regex != null)
                    {
                        try
                        {
                            if (!regex.IsMatch(name))
                            {
                                continue;
                            }
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            // Fall back to the plain text search for the rest of the list.
                            regex = null;
                            if (name.IndexOf(filter, StringComparison.InvariantCultureIgnoreCase) == -1)
                            {
                                continue;
                            }
                        }
                    }
                    else if (name.IndexOf(filter, StringComparison.InvariantCultureIgnoreCase) == -1)
                    {
                        continue;
                    }
                }
                filteredPackages.Add(linuxPackageInfo);
            }
            FilteredPackages = filteredPackages;
        }

        private bool MatchesTypeFilter(LinuxPackageInfo linuxPackageInfo)
        {
            switch (_TypeFilter)
            {
                case PackageTypeFilter.Installed:
                    // A package with an update is installed too.
                    return linuxPackageInfo.State != LinuxPackageInfo.PackageState.Available;
                case PackageTypeFilter.NotInstalled:
                    return linuxPackageInfo.State == LinuxPackageInfo.PackageState.Available;
                case PackageTypeFilter.Updates:
                    return linuxPackageInfo.State == LinuxPackageInfo.PackageState.Upgradeable;
                case PackageTypeFilter.Pending:
                    return linuxPackageInfo.Action != LinuxPackageInfo.PackageAction.None;
                default:
                    return true;
            }
        }

        public string BuildCommandLine()
        {
            if (_AllPackages == null || _Client == null)
            {
                return null;
            }
            List<string> list = new List<string>();
            LinuxPackageInfo[] items = _AllPackages.Where((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.Action == LinuxPackageInfo.PackageAction.Install).ToArray();
            LinuxPackageInfo[] entries = _AllPackages.Where((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.Action == LinuxPackageInfo.PackageAction.Remove).ToArray();
            if (items.Length != 0)
            {
                list.Add(_Client.GetCommandLineForPackageOperation(items, LinuxPackageInfo.PackageAction.Install, Options));
            }
            if (entries.Length != 0)
            {
                list.Add(_Client.GetCommandLineForPackageOperation(entries, LinuxPackageInfo.PackageAction.Remove, Options));
            }
            string text = string.Join(" && ", list.Where((string value) => !string.IsNullOrEmpty(value)).ToArray());
            if (text.Length == 0)
            {
                // Nothing was selected. The caller sends the command line to the terminal unless it is null: an empty
                // text would end up there as "sudo su -c \"\"".
                return null;
            }
            return PackageIndexUpdater.WrapForPrivileges(text, _IsRoot);
        }
    }


    public string CommandLine { get; private set; }

    public PackageManagerWindow(SessionWrapper session)
    {
        InitializeComponent();
        this.DataContext = new ControllerImpl(this, session);
    }

    protected override void OnClosed(EventArgs e)
    {
        (this.DataContext as ControllerImpl)?.OnClosed();
        base.OnClosed(e);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        (this.DataContext as ControllerImpl)?.Refresh();
    }

    private void SelectAllUpdates_Click(object sender, RoutedEventArgs e)
    {
        (this.DataContext as ControllerImpl)?.SelectAllUpdates();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        CommandLine = (this.DataContext as ControllerImpl)?.BuildCommandLine();
        if (CommandLine == null)
        {
            // Enter in the filter box reaches the default button even when nothing is selected: keep the window open.
            return;
        }
        this.DialogResult = true;
    }

    private void CheckBox_CheckChanged(object sender, RoutedEventArgs e)
    {
        (this.DataContext as ControllerImpl)?.OnChecked();
    }

    private void ListView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !(sender is ListView listView))
        {
            return;
        }
        foreach (LinuxPackageInfo item in listView.SelectedItems.OfType<LinuxPackageInfo>())
        {
            item.ToggleSelection();
        }
        // The rows that are scrolled out of view have no check box to report the change, so update the summary here.
        (this.DataContext as ControllerImpl)?.OnChecked();
        ((RoutedEventArgs)e).Handled = true;
    }
}
