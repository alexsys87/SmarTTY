using SmarTTY.GUI.Backend;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using VisualTTY;

namespace SmarTTY.GUI;

public partial class PackageManagerWindow : Window, IComponentConnector, IStyleConnector
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

        private string _Filter;

        private PackageTypeFilter _TypeFilter;

        public string InstallText
        {
            get
            {
                int itemIndex = 0;
                int removedPackageCount = 0;
                if (_AllPackages != null)
                {
                    LinuxPackageInfo[] allPackages = _AllPackages;
                    for (int allPackageIndex = 0; allPackageIndex < allPackages.Length; allPackageIndex++)
                    {
                        switch (allPackages[allPackageIndex].Action)
                        {
                            case LinuxPackageInfo.PackageAction.Install:
                                itemIndex++;
                                break;
                            case LinuxPackageInfo.PackageAction.Remove:
                                removedPackageCount++;
                                break;
                        }
                    }
                }
                List<string> list = new List<string>();
                if (itemIndex > 0)
                {
                    list.Add($"install {itemIndex} package" + ((itemIndex == 1) ? "" : "s"));
                }
                if (removedPackageCount > 0)
                {
                    list.Add($"remove {removedPackageCount} package" + ((removedPackageCount == 1) ? "" : "s"));
                }
                string text = string.Join(", ", list.ToArray());
                if (text.Length == 0)
                {
                    return null;
                }
                return char.ToUpper(text[0]) + text.Substring(1);
            }
        }

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

        public ObservableCollection<LinuxPackageInfo> FilteredPackages { get; } = new ObservableCollection<LinuxPackageInfo>();

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
        }

        public ControllerImpl(PackageManagerWindow packageManagerWindow, SessionWrapper session)
        {
            _Window = packageManagerWindow;
            _Session = session;
            ProgressText = "Detecting package manager...";
            new Thread(ParseThreadBody).Start();
        }

        private void ParseThreadBody()
        {
            try
            {
                ILinuxPackageManagerClient bestClient = null;
                int bestScore = 0;
                foreach (ILinuxPackageManagerClient client in _Clients)
                {
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
                    throw new Exception("Could not detect the package manager on the remote host");
                }
                _Client = bestClient;
                _IsRoot = PackageIndexUpdater.IsRoot(_Session);
                LoadPackages(null);
            }
            catch (Exception exception)
            {
                ((DispatcherObject)_Window).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
                {
                    MessageBoxes.Exception(exception);
                    _Window.Close();
                }), new object[0]);
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
            _AllPackages = allPackages;
            ProgressText = null;
            ((DispatcherObject)_Window).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
            {
                ApplyFilter();
                OnChecked();
            }), new object[0]);
        }

        /// <summary>Updates the package lists from the repositories with the detected package manager and reloads the list.</summary>
        public void Refresh()
        {
            if (_Client == null || !IsIdle)
            {
                return;
            }
            ProgressText = "Updating package lists...";
            // Empty the list so that the progress text does not overlap the old rows; it is refilled after the reload.
            FilteredPackages.Clear();
            new Thread(RefreshThreadBody).Start();
        }

        private void RefreshThreadBody()
        {
            try
            {
                Dictionary<string, LinuxPackageInfo> selection = new Dictionary<string, LinuxPackageInfo>();
                foreach (LinuxPackageInfo linuxPackageInfo in _AllPackages ?? new LinuxPackageInfo[0])
                {
                    if (linuxPackageInfo.Action != LinuxPackageInfo.PackageAction.None)
                    {
                        selection[linuxPackageInfo.ID] = linuxPackageInfo;
                    }
                }
                ProgressText = "Updating package lists (" + _Client.GetCommandLineForIndexUpdate() + ")...";
                PackageIndexUpdater.Run(_Session, _Client, _IsRoot);
                LoadPackages(selection);
            }
            catch (Exception exception)
            {
                ProgressText = null;
                ((DispatcherObject)_Window).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
                {
                    // Bring back the previous list, the refresh did not replace it.
                    ApplyFilter();
                    MessageBoxes.Exception(exception);
                }), new object[0]);
            }
        }

        private void ApplyFilter()
        {
            FilteredPackages.Clear();
            string filter = _Filter;
            Regex regex = null;
            if (filter != null && filter.IndexOfAny(new char[4] { '$', '*', '^', '.' }) != -1)
            {
                try
                {
                    regex = new Regex(filter, RegexOptions.IgnoreCase);
                }
                catch
                {
                }
            }
            LinuxPackageInfo[] linuxPackageInfos = _AllPackages ?? new LinuxPackageInfo[0];
            foreach (LinuxPackageInfo linuxPackageInfo in linuxPackageInfos)
            {
                string name = linuxPackageInfo.Name;
                if (name == null)
                {
                    continue;
                }
                switch (_TypeFilter)
                {
                    case PackageTypeFilter.Installed:
                        if (linuxPackageInfo.State != LinuxPackageInfo.PackageState.Installed)
                        {
                            continue;
                        }
                        break;
                    case PackageTypeFilter.Updates:
                        if (linuxPackageInfo.State != LinuxPackageInfo.PackageState.Upgradeable)
                        {
                            continue;
                        }
                        break;
                }
                if (!string.IsNullOrEmpty(_Filter))
                {
                    if (regex != null)
                    {
                        if (!regex.IsMatch(name))
                        {
                            continue;
                        }
                    }
                    else if (name.IndexOf(_Filter, StringComparison.InvariantCultureIgnoreCase) == -1)
                    {
                        continue;
                    }
                }
                FilteredPackages.Add(linuxPackageInfo);
            }
        }

        public string BuildCommandLine()
        {
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
            string text = string.Join("&&", list.Where((string value) => !string.IsNullOrEmpty(value)).ToArray());
            if (text.Length == 0)
            {
                // Nothing was selected. The caller sends the command line to the terminal unless it is null: an empty
                // text would end up there as "sudo su -c \"\"".
                return null;
            }
            return PackageIndexUpdater.WrapForPrivileges(text, _IsRoot);
        }
    }

    internal ResourceDictionary _ThemeDictionary;

    public string CommandLine { get; private set; }

    public PackageManagerWindow(SessionWrapper session)
    {
        InitializeComponent();
        this.DataContext = new ControllerImpl(this, session);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        (this.DataContext as ControllerImpl)?.Refresh();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        CommandLine = (this.DataContext as ControllerImpl)?.BuildCommandLine();
        this.DialogResult = true;
    }

    private void CheckBox_CheckChanged(object sender, RoutedEventArgs e)
    {
        (this.DataContext as ControllerImpl)?.OnChecked();
    }

    private void ListView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((int)e.Key != 18)
        {
            return;
        }
        foreach (LinuxPackageInfo item in ((ListBox)((sender is ListView) ? sender : null)).SelectedItems.OfType<LinuxPackageInfo>())
        {
            item.ToggleSelection();
        }
        ((RoutedEventArgs)e).Handled = true;
    }
}
