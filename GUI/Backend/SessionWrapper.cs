using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.Forms;
using SmarTTY.IO;
using SmarTTY.LocalRepo;
using SmarTTY.Terminal;
using SmarTTY.Transport;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualTTY;
using VisualTTY.DeviceTerminal;
using SmarTTY.Completion;

namespace SmarTTY.GUI.Backend;

public class SessionWrapper : INotifyPropertyChanged, IDisposable, ICloseableElement, ITerminalSessionContext, ISSHCommandRunner
{
    public class SessionFixer : IDisposable, INotifyPropertyChanged
    {
        private SessionWrapper _Session;

        private ConnectionSelectionWindow.ControllerImpl _ReconnectionController;

        public string HostName => _Session.Title;

        public ConnectionSelectionWindow.ControllerImpl Reconnector => _ReconnectionController;

        public event PropertyChangedEventHandler PropertyChanged;

        public SessionFixer(SessionWrapper sessionWrapper)
        {
            _Session = sessionWrapper;
            sessionWrapper.IsInactive = true;
        }

        public void Close()
        {
            _Session._Controller.CloseSession(_Session);
        }

        public void Ignore()
        {
            _Session.Fixer = null;
        }

        public void Reconnect()
        {
            if (_ReconnectionController == null)
            {
                _ReconnectionController = new ConnectionSelectionWindow.ControllerImpl(_Session._Controller.Dispatcher, showRawConnections: false, null, new CommonGUIPreferences
                {
                    ProbeConnections = false
                }, suppressUpdateThread: true);
                _ReconnectionController.SessionCreated += _MainController_SessionCreated;
            }
            _ReconnectionController.BeginSSHConnection(_Session.Settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Reconnector"));
        }

        private void _MainController_SessionCreated()
        {
            SshSession created = _ReconnectionController?.CreatedSession;
            if (created != null)
            {
                _Session._Controller.BeginInvoke(() =>
                {
                    try
                    {
                        _Session.AttachNewSession(created);
                    }
                    catch (Exception exception)
                    {
                        // This runs straight on the dispatcher, where nothing catches it and the application would go down.
                        MessageBoxes.Exception(exception);
                    }
                    // Whether the new connection took over decides what is left to fix: if it did, the prompt is done
                    // (even when something after the switch failed); if not, the prompt stays so that the user can try again.
                    if (ReferenceEquals(_Session.Session, created))
                    {
                        _Session.IsInactive = false;
                        _Session.Fixer = null;
                    }
                });
            }
        }

        public void Dispose()
        {
            _ReconnectionController?.Dispose();
        }
    }

    private readonly MainWindowController _Controller;

    public SSHConnectionSettings Settings;

    private int _ActiveTabIndex;

    private int _TabUpdateInProgress;

    private PseudoTabContext[] _NewTabShortcuts;

    private bool _IsInactive;

    public string LastUsedOpenDir;

    public string CurrentDirDuringLastOpen;

    public string LastDownloadDir;

    private string _CachedHomeDir;

    private SessionFixer _Fixer;

    private bool _ShowNewSessionTab;

    private bool m_bCtrlTabActive;

    private int m_CtrlTabScrollIndex;

    private List<ITabContext> _PagesInLastUsedOrder = new List<ITabContext>();

    private ShellCompletionAnalyzer _Analyzer;

    // Whether the login shell edits its command line in UTF-8 bytes (BusyBox without Unicode support, as on OpenWrt):
    // null until the probe started by the first classic terminal tab has answered.
    private bool? _ByteOrientedShell;

    // The running or finished probe of the current connection; its result is null when the probe command could not be
    // run (the connection went away), which must not be remembered as an answer.
    private Task<bool?> _ByteOrientedShellProbe;

    private static readonly TimeSpan ByteProbeTimeout = TimeSpan.FromSeconds(3);

    // The package manager of the host of this session if it is OpenWrt (opkg, or apk on OpenWrt 25.12 and newer), so that
    // "Tools > Local OpenWrt repository server" is offered: null until the probe has answered. A host that is not OpenWrt
    // (or has neither opkg nor apk) never sees the item.
    private LocalRepoPackageManager? _LocalRepoManager;

    /// <summary>The package manager client found on this connection (null until it is found); forgotten on a reconnect.</summary>
    internal VisualTTY.ILinuxPackageManagerClient PackageManagerClient;

    // Whether the host of this session has a package manager SmarTTY supports: false until the probe in the background has found one.
    // A failed probe leaves the previous answer alone, so a reconnect to the same host does not hide the item for good.
    private bool _HasPackageManager;

    /// <summary>
    /// Visibility of the "Tools > Manage Linux Packages" item: offered only for an SSH session whose host answered the probe with a
    /// package manager that SmarTTY supports. Until the probe answers, and on a host without such a manager, it is not in the menu at all.
    /// </summary>
    public System.Windows.Visibility PackageManagerMenuVisibility => (!IsDevice && _HasPackageManager) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    private RemoteOsKind _RemoteOs;

    /// <summary>What the SSH server runs on: from its identification string at connect, confirmed by a command when that said nothing.</summary>
    public RemoteOsKind RemoteOs => _RemoteOs;

    /// <summary>True for a Windows host (Win32-OpenSSH, Bitvise): the Unix probes, df and the package managers of Linux do not apply to it.</summary>
    public bool IsWindowsHost => _RemoteOs == RemoteOsKind.Windows;

    private void SetRemoteOs(RemoteOsKind kind)
    {
        if (_RemoteOs == kind)
        {
            return;
        }
        _RemoteOs = kind;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemoteOs)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWindowsHost)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnixSshVisibility)));
        if (kind == RemoteOsKind.Windows)
        {
            // df is a Unix command: the bar would only collect failed readings there.
            DiskSpace.Attach(null);
        }
    }

    private object _Sync = new object();

    private string _DeviceTitle;

    public ObservableCollection<ITabContext> Tabs { get; } = new ObservableCollection<ITabContext>();

    /// <summary>
    /// Settings of a COM/TCP terminal when this wrapper is a device session; null for SSH sessions and for the
    /// "new connection" placeholder. A device session owns exactly one <see cref="DeviceTabContext"/> and has no
    /// SSH session, SCP transfers or "new terminal tab" shortcuts.
    /// </summary>
    public DeviceTerminalSettings DeviceSettings { get; }

    public bool IsDevice => DeviceSettings != null;

    /// <summary>True for the trailing "connect to another target" pseudo session (neither SSH nor device).</summary>
    public bool IsPlaceholder => Session == null && DeviceSettings == null;

    /// <summary>Visible for COM/TCP sessions only (the TFTP / U-Boot upload item of the Tools menu).</summary>
    public System.Windows.Visibility DeviceOnlyVisibility => IsDevice ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public System.Windows.Visibility SshOnlyVisibility => IsDevice ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>
    /// Visible for SSH sessions to a Unix host only: the tools that rely on ps, sh, /tmp or xauth of the remote host
    /// (process manager, run script, X11 check) are hidden for devices and for Windows hosts.
    /// </summary>
    public System.Windows.Visibility UnixSshVisibility => (IsDevice || IsWindowsHost) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>
    /// Visibility of the "View -> File List" item: the file list only makes sense for SSH sessions, but the item
    /// must stay available while an editor or a classic terminal tab of such a session is active.
    /// </summary>
    public System.Windows.Visibility FileListMenuVisibility => (IsDevice || IsPlaceholder) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public DeviceTabContext DeviceTab => Tabs.OfType<DeviceTabContext>().FirstOrDefault();

    /// <summary>Non-null for a COM port: the key that must be unique among open sessions.</summary>
    public string DeviceExclusiveKey => IsDevice ? DeviceTabContext.MakeExclusiveKey(DeviceSettings.Connection) : null;

    /// <summary>Connection state and transferred bytes of a device session, for the status bar.</summary>
    public string DeviceStatusText => DeviceTab?.Terminal.StatusText;

    public string DeviceTrafficText
    {
        get
        {
            DeviceTerminalControl terminal = DeviceTab?.Terminal;
            if (terminal == null)
            {
                return null;
            }
            terminal.GetTransferRates(out double sendRate, out double receiveRate);
            return $"{StringUtils.FormatSize(terminal.BytesSent)}B sent ({TransferRateMeter.FormatRate(sendRate)}), "
                + $"{StringUtils.FormatSize(terminal.BytesReceived)}B received ({TransferRateMeter.FormatRate(receiveRate)})";
        }
    }

    public string Title => IsDevice ? _DeviceTitle : Settings?.HostName;

    public string TitleWithStatus
    {
        get
        {
            string text = Title;
            if (IsInactive)
            {
                text += " [inactive]";
            }
            else if (IsDevice && DeviceTab != null && (DeviceTab.Terminal.ConnectionState == ConnectionStateEnum.Disconnected || DeviceTab.Terminal.ConnectionState == ConnectionStateEnum.Failed))
            {
                text += " [disconnected]";
            }
            return text;
        }
    }

    public IconType Icon
    {
        get
        {
            if (IsDevice)
            {
                return (DeviceSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial) ? IconType.Serial : IconType.Network;
            }
            if (Session == null)
            {
                return IconType.Add;
            }
            return IconType.Terminal;
        }
    }

    /// <summary>
    /// Visibility of the "Tools > Local OpenWrt repository server" item: the server feeds opkg or apk on OpenWrt, so the item
    /// is offered only for an SSH session whose host answered the probe as OpenWrt with opkg or apk installed. Until the probe
    /// answers it stays hidden, so that it does not appear on the hosts that cannot use it.
    /// </summary>
    public System.Windows.Visibility LocalRepoMenuVisibility => (_LocalRepoManager != null) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>The package manager of the OpenWrt host (null if the host is not OpenWrt or has not answered the probe yet).</summary>
    public LocalRepoPackageManager? LocalRepoManager => _LocalRepoManager;

    public SshSession Session { get; private set; }

    /// <summary>The local OpenWrt repository served to this session's device (Tools > Local OpenWrt repository server).</summary>
    public LocalRepoHost LocalRepo { get; } = new LocalRepoHost();

    /// <summary>The SOCKS5 proxy through this session's SSH connection (Tools > SOCKS5 proxy over SSH).</summary>
    public SocksProxyHost SocksProxy { get; } = new SocksProxyHost();

    /// <summary>The local and remote port forwardings of this session (Tools > Port forwarding), loaded from the profile.</summary>
    public PortForwardHost PortForwards { get; } = new PortForwardHost();

    /// <summary>The free disk space of this session's SSH host (<c>df -h</c> every 10 seconds), shown below the terminal.</summary>
    public DiskSpaceMonitor DiskSpace { get; } = new DiskSpaceMonitor();

    public FileTransferManager TransferManager { get; private set; }

    public bool HideHeaders => _Controller.HideTabHeaders;

    public int ActiveTabIndex
    {
        get
        {
            return _ActiveTabIndex;
        }
        set
        {
            ITabContext sender = null;
            if (_ActiveTabIndex >= 0 && _ActiveTabIndex < Tabs.Count)
            {
                sender = Tabs[_ActiveTabIndex];
            }
            _ActiveTabIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ActiveTabIndex"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ActiveTab"));
            if (_TabUpdateInProgress == 0)
            {
                (ActiveTab as PseudoTabContext)?.ActivationHandler?.Invoke(sender);
            }
            _Controller.OnActiveTabChanged(this, ActiveTab);
        }
    }

    public ITabContext ActiveTab
    {
        get
        {
            if (_ActiveTabIndex < 0 || _ActiveTabIndex >= Tabs.Count)
            {
                return null;
            }
            return Tabs[_ActiveTabIndex];
        }
    }

    public SCPController SCPController { get; }

    public bool IsInactive
    {
        get
        {
            return _IsInactive;
        }
        set
        {
            if (_IsInactive == value)
            {
                return;
            }
            _IsInactive = value;
            if (!value)
            {
                PseudoTabContext[] newTabShortcuts = _NewTabShortcuts;
                foreach (PseudoTabContext item in newTabShortcuts)
                {
                    Tabs.Add(item);
                }
            }
            else
            {
                PseudoTabContext[] newTabShortcuts = _NewTabShortcuts;
                foreach (PseudoTabContext entry in newTabShortcuts)
                {
                    Tabs.Remove(entry);
                }
            }
            foreach (ITabContext tab in Tabs)
            {
                tab.OnSessionInactiveChanged(value);
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsInactive"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TitleWithStatus"));
        }
    }

    public SessionFixer Fixer
    {
        get
        {
            return _Fixer;
        }
        set
        {
            _Fixer?.Dispose();
            _Fixer = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Fixer"));
        }
    }

    private int FirstPseudoPage => Enumerable.Range(0, Tabs.Count).FirstOrDefault((int index) => Tabs[index] is PseudoTabContext);

    public bool ShowNewSessionTab
    {
        get
        {
            return _ShowNewSessionTab;
        }
        set
        {
            if (_ShowNewSessionTab == value)
            {
                return;
            }
            _ShowNewSessionTab = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ShowNewSessionTab"));
            if (value)
            {
                Tabs.Add(new PseudoTabContext
                {
                    IsNewSessionShortcut = true,
                    Icon = IconType.New,
                    ToolTip = "Connect to another target"
                });
            }
            else
            {
                CloseFirstMatchingPage((ITabContext tabContext) => tabContext is PseudoTabContext pseudoTabContext && pseudoTabContext.IsNewSessionShortcut, closeSessionIfEmpty: false);
            }
        }
    }

    public string TitleWithUserName => IsDevice ? _DeviceTitle : Settings?.UserName + "@" + Settings?.HostName;

    public VirtualTerminalSettings VTSettings => _Controller.Settings.VirtualTerminal;

    public bool IsRoot => Settings.UserName == "root";

    public bool AutoConfirmSudo => Settings?.AutoConfirmSudo ?? false;

    public event PropertyChangedEventHandler PropertyChanged;

    public void RaiseHideHeadersChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HideHeaders"));
    }

    public SessionWrapper(MainWindowController controllerImpl, SshSession session, SSHConnectionSettings connectionSettings)
    {
        SCPController = new SCPController(this);
        _Controller = controllerImpl;
        // Before attaching the session: the connection settings decide whether it forwards X11.
        Settings = connectionSettings;
        if (session != null)
        {
            AttachNewSession(session);
        }
        Tabs.CollectionChanged += Tabs_CollectionChanged;
        _NewTabShortcuts = new PseudoTabContext[3]
        {
            new PseudoTabContext
            {
                Icon = IconType.Add,
                ActivationHandler = (ITabContext tabContext) =>
                {
                    CreateNewTerminalPage();
                },
                ToolTip = "Open another terminal tab within session"
            },
            new PseudoTabContext
            {
                Icon = IconType.AddVirtual,
                ActivationHandler = (ITabContext tabContext) =>
                {
                    CreateNewTerminalPage(virtualMode: true);
                },
                ToolTip = "Open another Smart Terminal tab"
            },
            new PseudoTabContext
            {
                Icon = IconType.Open,
                ActivationHandler = (ITabContext tabWithCurrentDir) =>
                {
                    _Controller.MenuCommandHandlers.OpenFile(tabWithCurrentDir);
                },
                ToolTip = "Edit a file on the remote machine"
            }
        };
        PseudoTabContext[] newTabShortcuts = _NewTabShortcuts;
        foreach (PseudoTabContext item in newTabShortcuts)
        {
            Tabs.Add(item);
        }
    }

    private SessionWrapper(MainWindowController controllerImpl, DeviceTerminalSettings deviceSettings)
    {
        SCPController = new SCPController(this);
        _Controller = controllerImpl;
        DeviceSettings = deviceSettings;
        Tabs.CollectionChanged += Tabs_CollectionChanged;
        _NewTabShortcuts = new PseudoTabContext[0];
    }

    /// <summary>Creates a session for a COM/TCP terminal with its single terminal tab.</summary>
    internal static SessionWrapper CreateDeviceSession(MainWindowController controller, DeviceTerminalSettings settings, string title)
    {
        SessionWrapper session = new SessionWrapper(controller, settings);
        session._DeviceTitle = title;
        session.AddDeviceTab();
        return session;
    }

    private void AddDeviceTab()
    {
        try
        {
            _TabUpdateInProgress++;
            Tabs.Insert(0, new DeviceTabContext(this, DeviceSettings));
        }
        finally
        {
            _TabUpdateInProgress--;
        }
    }

    /// <summary>Settings shared by all tabs (font, palette, key behavior), for device terminals.</summary>
    internal GlobalSettings CommonSettings => _Controller.Settings;

    /// <summary>Toggles the main window full screen mode (Alt+Enter in a device terminal).</summary>
    internal void ToggleFullScreenFromDevice()
    {
        _Controller.BeginInvoke(() => _Controller.FullScreenMode = !_Controller.FullScreenMode);
    }

    /// <summary>Closes a device tab after the key handler that asked for it has returned (Ctrl+W).</summary>
    internal void CloseDeviceTabFromKeyboard(DeviceTabContext tab)
    {
        _Controller.BeginInvoke(() => tab.Close());
    }

    internal void OnDeviceStatusChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TitleWithStatus"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("DeviceStatusText"));
    }

    /// <summary>Closes the terminal of a device session, which ends the session itself.</summary>
    internal void CloseDeviceTab(DeviceTabContext tab)
    {
        int tabIndex = Tabs.IndexOf(tab);
        if (tabIndex < 0)
        {
            return;
        }
        if (ActiveTabIndex == tabIndex)
        {
            ActiveTabIndex = Math.Max(0, ActiveTabIndex - 1);
        }
        try
        {
            _TabUpdateInProgress++;
            Tabs.RemoveAt(tabIndex);
        }
        finally
        {
            _TabUpdateInProgress--;
        }
        tab.Dispose();
        _Controller.CloseSession(this);
    }

    private void AttachNewSession(SshSession session)
    {
        if (session == Session)
        {
            return;
        }
        bool firstConnection = Session == null;
        SshSession oldSession = Session;
        Session = session;
        if (oldSession != null)
        {
            if (SocksProxy.IsRunning || PortForwards.HasActive)
            {
                // Closing a connection that carries the SOCKS proxy or forwarded ports waits (up to the connection timeout, 30 s)
                // for the programs that still use them to close their connections, so it must not block the UI thread. The
                // listeners are released at once, and SocksProxy.Reattach / PortForwards.OnConnected below retry until the new
                // connection can take their ports.
                ThreadPool.QueueUserWorkItem((object state) =>
                {
                    try
                    {
                        ((SshSession)state).Dispose();
                    }
                    catch
                    {
                    }
                }, oldSession);
            }
            else
            {
                oldSession.Dispose();
            }
        }
        // A reconnect may land on another kind of host: forget what was known (a Windows host found by the probe) and read the banner again.
        SetRemoteOs(RemoteOsKind.Unknown);
        SetRemoteOs(RemoteOsDetector.FromServerVersion(session.ServerVersion));
        if (!IsWindowsHost)
        {
            DiskSpace.Attach(session);
        }
        EnableX11Forwarding(session);
        // Not only on the first connection: when the start failed because the connection was lost, it is repeated after reconnecting.
        AutoStartSocksProxy(session);
        if (!firstConnection && LocalRepo.IsRunning)
        {
            // The reverse tunnel belonged to the old connection: restore it (and the feeds) on the new one, off the UI thread.
            ThreadPool.QueueUserWorkItem((object state) => LocalRepo.Reattach((SshSession)state), session);
        }
        if (!firstConnection && SocksProxy.IsRunning)
        {
            // The listener belonged to the old connection: open it again on the new one, off the UI thread.
            ThreadPool.QueueUserWorkItem((object state) => SocksProxy.Reattach((SshSession)state), session);
        }
        if (firstConnection)
        {
            PortForwards.Load(Settings?.PortForwards);
        }
        // Restores the forwardings that ran on the old connection and starts those marked "start on connect", off the UI thread.
        ThreadPool.QueueUserWorkItem((object state) => PortForwards.OnConnected((SshSession)state), session);
        TransferManager?.Dispose();
        TransferManager = new FileTransferManager
        {
            Session = session,
            ConfirmOverwrite = MessageBoxes.AskWarn
        };
        ApplyTransferSettings();
        TransferManager.JobFinished += OnTransferFinished;
        // A loss that happened before this line (the session is replaced step by step) is reported at once: the event is latched.
        session.ConnectionLost += (object sender, EventArgs e) =>
        {
            _Controller.BeginInvoke(() =>
            {
                if (!ReferenceEquals(Session, session))
                {
                    // A connection that has been replaced meanwhile needs no reconnect prompt.
                    return;
                }
                // The transfers that were under way continue after reconnecting, from where they stopped. Their threads
                // usually see the loss (a short read) before this runs and have already failed, so a transfer that failed
                // because of the loss counts as well.
                List<FileTransferManager.TransferJob> interrupted = SCPController.Jobs.Where((FileTransferManager.TransferJob job) => job.State == FileTransferManager.TransferState.Queued || job.State == FileTransferManager.TransferState.InProgress || (job.State == FileTransferManager.TransferState.Failed && job.InterruptedByConnectionLoss)).ToList();
                // What an earlier loss collected and no reconnect has taken up yet stays in the list.
                _TransfersToResume = (_TransfersToResume == null) ? interrupted : _TransfersToResume.Union(interrupted).ToList();
                Fixer = new SessionFixer(this);
            });
        };
        if (firstConnection)
        {
            RestoreTransferQueue();
        }
        else if (_TransfersToResume != null)
        {
            List<FileTransferManager.TransferJob> resume = _TransfersToResume;
            _TransfersToResume = null;
            _Controller.BeginInvoke(() =>
            {
                foreach (FileTransferManager.TransferJob job in resume)
                {
                    TransferManager.Requeue(job);
                }
            });
        }
        // From here on the new connection is in place: a failure of one step must not leave the others undone (the session
        // would stay inactive with no way to retry), so the failures are collected and reported at the end.
        Exception deferredError = null;
        if (!_ByteOrientedShell.HasValue)
        {
            // A probe that started on the old connection (and may have been cut off) says nothing about the new one.
            _ByteOrientedShellProbe = null;
        }
        foreach (ITabContext tab in Tabs.ToList())
        {
            try
            {
                tab.OnSessionReconnected(session);
            }
            catch (Exception exception)
            {
                deferredError = deferredError ?? exception;
            }
        }
        if (_Analyzer != null)
        {
            try
            {
                ShellCompletionAnalyzer previousAnalyzer;
                lock (_Sync)
                {
                    previousAnalyzer = _Analyzer;
                    _Analyzer = new ShellCompletionAnalyzer(this, session);
                }
                // Its history channel belongs to the lost connection.
                previousAnalyzer.Dispose();
            }
            catch (Exception exception)
            {
                deferredError = deferredError ?? exception;
            }
        }
        IsInactive = false;
        PackageManagerClient = null;
        StartOpenWrtProbe();
        StartPackageManagerProbe();
        if (deferredError != null)
        {
            MessageBoxes.Exception(deferredError);
        }
    }

    /// <summary>
    /// Asks the host once per connection whether it is OpenWrt with opkg or apk (in the background, the session works meanwhile)
    /// and raises the visibility of the "Tools > Local OpenWrt repository server" item when it is. On any other host the
    /// item stays hidden. A failed probe leaves the previous answer alone, so a reconnect to the same kind of host does not
    /// hide the item for good.
    /// </summary>
    private void StartOpenWrtProbe()
    {
        SshSession session = Session;
        if (session == null)
        {
            return;
        }
        RemoteOsKind knownOs = _RemoteOs;
        if (knownOs == RemoteOsKind.Windows)
        {
            // The server said so in its identification string: there is nothing to ask a Windows host about OpenWrt.
            _LocalRepoManager = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("LocalRepoMenuVisibility"));
            return;
        }
        ThreadPool.QueueUserWorkItem(delegate
        {
            List<string> output = null;
            try
            {
                output = session.RunCommand(PosixShell.Wrap(OpenWrtDetector.ProbeCommand, addAdminDirectoriesToPath: true), out _, timeout: TimeSpan.FromSeconds(20));
            }
            catch
            {
            }
            if (output == null)
            {
                // The command could not be run (the connection went away): keep the previous answer.
                return;
            }
            bool isWindows = false;
            if (knownOs != RemoteOsKind.Unix && !OpenWrtDetector.IsAnswer(output))
            {
                // Not even "SMARTTY_OS=other": the host has no /bin/sh, and its banner did not say what it is. Ask in its own words.
                try
                {
                    isWindows = RemoteOsDetector.FromWindowsProbe(session.RunCommand(RemoteOsDetector.WindowsProbeCommand, out _, timeout: TimeSpan.FromSeconds(20))) == RemoteOsKind.Windows;
                }
                catch
                {
                }
            }
            LocalRepoPackageManager? manager = isWindows ? null : OpenWrtDetector.Detect(output);
            _Controller.BeginInvoke(() =>
            {
                // A reconnect may have replaced the connection while the probe ran: only the current one counts.
                if (!ReferenceEquals(Session, session))
                {
                    return;
                }
                if (isWindows)
                {
                    session.RemoteOs = RemoteOsKind.Windows;
                    SetRemoteOs(RemoteOsKind.Windows);
                }
                _LocalRepoManager = manager;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("LocalRepoMenuVisibility"));
            });
        });
    }

    /// <summary>
    /// Finds the package manager of the host (the one the package manager window then uses) and remembers it for the connection:
    /// null when the host has none that is supported. A Windows host is asked for winget only, and any other host for the package
    /// managers of <see cref="PackageManagerCatalog"/>; a host whose banner said nothing and that answered none of them is checked
    /// for being Windows. <paramref name="isCancelled"/> stops the probes that are not run yet.
    /// </summary>
    internal VisualTTY.ILinuxPackageManagerClient DetectPackageManager(Func<bool> isCancelled = null)
    {
        SshSession session = Session;
        VisualTTY.ILinuxPackageManagerClient client = PackageManagerClient;
        if (client != null || session == null)
        {
            return client;
        }
        if (!IsWindowsHost)
        {
            client = PackageManagerSelector.Pick(PackageManagerCatalog.CreateUnixClients(), this, isCancelled);
            if (client == null && !(isCancelled?.Invoke() ?? false) && _RemoteOs == RemoteOsKind.Unknown && DetectWindowsHost())
            {
                // The banner of the server did not say that it is Windows, and no package manager of Linux answered.
                client = PickWinget(isCancelled);
            }
        }
        else
        {
            // A Windows host has winget only: none of the package managers of Unix exists there.
            client = PickWinget(isCancelled);
        }
        if (client != null && ReferenceEquals(Session, session))
        {
            // A reconnect may have replaced the connection while the probes ran: the answer is for the old one then.
            PackageManagerClient = client;
        }
        return client;
    }

    private VisualTTY.ILinuxPackageManagerClient PickWinget(Func<bool> isCancelled)
    {
        return PackageManagerSelector.Pick(new VisualTTY.ILinuxPackageManagerClient[] { new WingetPackageManagerClient() }, this, isCancelled);
    }

    /// <summary>
    /// Looks for the package manager of the host once per connection, in the background (the session works meanwhile), and raises the
    /// visibility of "Tools > Manage Linux Packages" when one is found. A host without a supported manager never sees the item.
    /// </summary>
    private void StartPackageManagerProbe()
    {
        SshSession session = Session;
        if (session == null)
        {
            return;
        }
        ThreadPool.QueueUserWorkItem(delegate
        {
            VisualTTY.ILinuxPackageManagerClient client = null;
            try
            {
                client = DetectPackageManager(() => !ReferenceEquals(Session, session));
                if (client == null)
                {
                    // Every probe failed because the connection went away: that says nothing about the host, keep the previous answer.
                    session.ThrowIfConnectionLost();
                }
            }
            catch
            {
                return;
            }
            bool found = client != null;
            _Controller.BeginInvoke(() =>
            {
                if (!ReferenceEquals(Session, session) || _HasPackageManager == found)
                {
                    return;
                }
                _HasPackageManager = found;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PackageManagerMenuVisibility)));
            });
        });
    }

    /// <summary>
    /// Tries again to open the shell of a classic terminal tab whose shell could not be reopened after a reconnect.
    /// Called on the UI thread; does nothing while there is no working connection (the reconnect prompt owns that case).
    /// </summary>
    internal void RetryShell(TerminalEmulatorControl ctl)
    {
        SshSession session = Session;
        if (session == null || IsInactive || session.IsConnectionClosed)
        {
            return;
        }
        foreach (ITabContext tab in Tabs.ToList())
        {
            if (tab is NormalTabContext normalTab && ReferenceEquals(normalTab.Terminal, ctl))
            {
                if (normalTab.IsOpeningShell)
                {
                    // The shell is already being opened: every further key press would start another one.
                    return;
                }
                normalTab.OnSessionReconnected(session);
                return;
            }
        }
    }

    public void AttachChannelToControl(TerminalEmulatorControl ctl, SshShellChannel chn, bool isAutoLaunchedCommand, Action tabKeptAfterExit = null)
    {
        Utf8StreamDecoder reader = new Utf8StreamDecoder();
        chn.DataReceived += (object sender, byte[] data, int offset, int length, int value) =>
        {
            if (IsChannelAttached(ctl, chn))
            {
                // In byte mode the terminal needs to see which stray byte arrived (see Utf8StreamDecoder).
                reader.EscapeInvalidBytes = ctl.ByteOrientedLineEditing;
                AppendText(ctl, reader.Decode(data, offset, length), data, offset, length);
            }
        };
        if (isAutoLaunchedCommand)
        {
            chn.Finished += (object _) =>
            {
                _Controller.BeginInvoke(() =>
                {
                    if (!IsRemoteExit(ctl, chn))
                    {
                        return;
                    }
                    ReportChannelFailure(chn);
                    if (MessageBoxes.Ask("The command has exited. Close tab?"))
                    {
                        CloseTabPageForControl(ctl);
                    }
                    else
                    {
                        tabKeptAfterExit?.Invoke();
                    }
                });
            };
        }
        else
        {
            chn.Finished += (object _) =>
            {
                _Controller.BeginInvoke(() =>
                {
                    if (IsRemoteExit(ctl, chn))
                    {
                        ReportChannelFailure(chn);
                        CloseTabPageForControl(ctl, autoInvoked: true);
                    }
                });
            };
        }
        chn.PollingEnabled = true;
        chn.ResizePty(ctl.TerminalSize.Width, ctl.TerminalSize.Height);
    }

    /// <summary>
    /// Whether a new terminal tab of this session works in byte mode: its login shell edits the command line in
    /// UTF-8 bytes (BusyBox without Unicode support, OpenWrt), so the terminal repeats Backspace, Delete and the arrows
    /// once per byte and the tab's PTY is opened without IUTF8 (see SshSession.OpenShellChannel). Classic and Smart
    /// Terminal tabs have a setting each; in the automatic mode the shell is probed once per session. The answer is
    /// needed before the PTY is opened, so the probe is waited for, but never longer than <see cref="ByteProbeTimeout"/>:
    /// a slow server keeps the normal mode for that tab instead of freezing the window, and later tabs get the answer.
    /// </summary>
    public bool UsesByteOrientedLineEditing(bool smartTerminal)
    {
        GlobalSettings.ByteOrientedLineEditingMode mode = smartTerminal ? _Controller.Settings.VirtualTerminal.ByteOrientedLineEditing : _Controller.Settings.ByteOrientedLineEditing;
        switch (mode)
        {
            case GlobalSettings.ByteOrientedLineEditingMode.Always:
                return true;
            case GlobalSettings.ByteOrientedLineEditingMode.Never:
                return false;
        }
        if (_ByteOrientedShell.HasValue)
        {
            return _ByteOrientedShell.Value;
        }
        SshSession session = Session;
        if (session == null)
        {
            return false;
        }
        Task<bool?> probe = _ByteOrientedShellProbe;
        if (probe == null)
        {
            // A probe that could not run (the connection went away: RunCommand gives null) has no answer, which is not the
            // same as "not a byte-oriented shell": such a result is not remembered, the next tab asks again.
            probe = Task.Run(() =>
            {
                List<string> output = session.RunCommand(ByteColumns.ProbeCommand, out _);
                return (output == null) ? null : (bool?)ByteColumns.ParseProbe(output);
            });
            _ByteOrientedShellProbe = probe;
        }
        try
        {
            if (probe.Wait(ByteProbeTimeout))
            {
                bool? answer = probe.Result;
                if (answer.HasValue)
                {
                    _ByteOrientedShell = answer;
                    return answer.Value;
                }
                ForgetByteOrientedShellProbe(probe);
            }
        }
        catch
        {
            ForgetByteOrientedShellProbe(probe);
        }
        return false;
    }

    private void ForgetByteOrientedShellProbe(Task<bool?> probe)
    {
        if (ReferenceEquals(_ByteOrientedShellProbe, probe))
        {
            _ByteOrientedShellProbe = null;
        }
    }

    private static bool IsChannelAttached(TerminalEmulatorControl ctl, SshShellChannel chn)
    {
        return !ctl.IsDisposed && (ctl.Tag as TerminalTag)?.Channel == chn;
    }

    /// <summary>
    /// Tells a real end of the remote shell (or command) from the other reasons a channel finishes: the tab was
    /// closed (the channel is disposed together with it), the tab got a new channel after a reconnect, or the SSH
    /// connection went away. Only a real exit may close the tab or ask about it; a lost connection is handled by
    /// the reconnect prompt (<see cref="SessionFixer"/>), and closing the tabs here would close the session with it.
    /// </summary>
    /// <summary>A channel that failed while the connection is alive is not a shell exit: say why the tab closes.</summary>
    private static void ReportChannelFailure(SshShellChannel chn)
    {
        Exception failure = chn.Failure;
        if (failure != null)
        {
            MessageBoxes.Error("The terminal channel failed: " + failure.Message);
        }
    }

    private static bool IsRemoteExit(TerminalEmulatorControl ctl, SshShellChannel chn)
    {
        return IsChannelAttached(ctl, chn) && !chn.IsDisposed && !chn.ClosedByConnectionLoss;
    }

    public EditorTabContext TryActivateEditorForFile(string fn)
    {
        fn = RemotePathUtils.NormalizeUnix(fn);
        for (int tabIndex = 0; tabIndex < Tabs.Count; tabIndex++)
        {
            if (Tabs[tabIndex] is EditorTabContext editorTabContext && editorTabContext.FilePath == fn)
            {
                ActiveTabIndex = tabIndex;
                return editorTabContext;
            }
        }
        return null;
    }

    private void AppendText(TerminalEmulatorControl ctl, string text, byte[] data, int offset, int length)
    {
        try
        {
            ctl.EnqueueOutput(text);
            ctl.WriteLog(data, offset, length);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Replaces a leading "~" ("~" or "~/dir") with the home directory: SCP takes the path literally, so a "~" in it
    /// would name a directory called "~".
    /// </summary>
    public void ExpandHomeDirIfNeeded(ref string dir)
    {
        if (dir == null || !(dir == "~" || dir.StartsWith("~/")))
        {
            return;
        }
        try
        {
            string home;
            using (SshShellChannel sshShellChannel = Session.OpenShellChannel("pwd"))
            using (StreamReader streamReader = new StreamReader(sshShellChannel.Output))
            {
                home = streamReader.ReadLine();
            }
            if (!string.IsNullOrEmpty(home))
            {
                dir = home.TrimEnd('/') + dir.Substring(1);
                if (dir.Length == 0)
                {
                    dir = "/";
                }
            }
        }
        catch
        {
        }
    }

    #region File transfer queue

    private List<FileTransferManager.TransferJob> _TransfersToResume;

    // The refusal of X11 forwarding is explained once per session, not after every reconnection.
    private int _X11RefusalReported;

    private readonly object _SocksAutoStartLock = new object();

    // True once the profile's automatic start of the SOCKS5 proxy has worked; it is not repeated then, so that a proxy stopped by hand stays stopped.
    private volatile bool _SocksAutoStartDone;

    private volatile bool _SocksAutoStartErrorShown;

    /// <summary>
    /// Starts the SOCKS5 proxy of a connection whose profile asks for it (address, port and keepalive are the ones saved by
    /// Tools > SOCKS5 proxy over SSH). Runs off the UI thread; a failure (usually a port that another connection uses) is
    /// reported and does not affect the connection. It is tried on every new connection until it has worked once (a start that
    /// failed because the connection was lost is repeated after reconnecting); after that <see cref="SocksProxyHost.Reattach"/>
    /// restores a running proxy after a reconnect, and a proxy that the user stopped stays stopped.
    /// </summary>
    private void AutoStartSocksProxy(SshSession session)
    {
        if (Settings == null || !Settings.AutoStartSocksProxy || _SocksAutoStartDone || SocksProxy.IsRunning)
        {
            return;
        }
        ThreadPool.QueueUserWorkItem(delegate
        {
            lock (_SocksAutoStartLock)
            {
                // Rechecked: another start (an earlier connection's, or one by hand) may have finished while this one waited.
                if (_SocksAutoStartDone || SocksProxy.IsRunning)
                {
                    return;
                }
                try
                {
                    SocksProxySettings proxySettings = SocksProxySettings.Load();
                    SocksProxy.Start(session, proxySettings.BindAddress, proxySettings.Port, proxySettings.KeepAliveSeconds);
                    _SocksAutoStartDone = true;
                }
                catch (Exception exception)
                {
                    string message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
                    bool connectionLost = session.IsConnectionClosed || !ReferenceEquals(Session, session);
                    SocksProxy.Report("Automatic start failed: " + message + (connectionLost ? " The connection was lost, the start is repeated after reconnecting." : ""));
                    if (!connectionLost && !_SocksAutoStartErrorShown)
                    {
                        // Shown once; the start is still repeated after a reconnect, but only logged then.
                        _SocksAutoStartErrorShown = true;
                        _Controller.BeginInvoke(() => MessageBoxes.Error("The SOCKS5 proxy could not be started automatically: " + message + "\r\n\r\nThe connection works as usual. Change the port in Tools > SOCKS5 proxy over SSH (it is used by every connection that starts the proxy) or turn the option off in the connection settings."));
                    }
                    return;
                }
            }
            if (!ReferenceEquals(Session, session))
            {
                // The connection was replaced while the proxy was starting on it: move the proxy to the current one.
                SocksProxy.Reattach(Session);
            }
        });
    }

    /// <summary>
    /// Requests X11 forwarding on the terminal channels of a connection whose settings ask for it. Remote programs then
    /// reach the local X server, which is started (or installed) on their first connection, see XServerLaunchWindow.
    /// </summary>
    private void EnableX11Forwarding(SshSession session)
    {
        if (Settings == null || !Settings.EnableX11Forwarding)
        {
            return;
        }
        try
        {
            X11Forwarding x11 = session.EnableX11Forwarding(() => _Controller.CreateX11Connection()?.Client);
            x11.Refused += (object sender, EventArgs e) => OnX11Refused(session);
        }
        catch (Exception exception)
        {
            _Controller.BeginInvoke(() => MessageBoxes.Error("X11 forwarding could not be enabled: " + exception.Message));
        }
    }

    // Runs on a worker thread: the remote check tells the user what the server lacks.
    private void OnX11Refused(SshSession session)
    {
        if (Interlocked.Exchange(ref _X11RefusalReported, 1) != 0)
        {
            return;
        }
        X11RemoteStatus status = X11RemoteStatus.Check(session);
        string text = status.DescribeRefusal(session.HostName) + "\r\n\r\nTerminal tabs work as usual. X11 forwarding can be turned off in the connection settings.";
        _Controller.BeginInvoke(() => MessageBoxes.Warning(text));
    }

    /// <summary>
    /// A report of X11 forwarding for this session: the connection setting, the server's answer, the remote host's X11
    /// support and the local X server. Runs commands on the server, so it is called off the UI thread.
    /// </summary>
    internal string DescribeX11Forwarding()
    {
        SshSession session = Session;
        System.Text.StringBuilder text = new System.Text.StringBuilder();
        X11Forwarding x11 = session?.X11;
        if (Settings == null || !Settings.EnableX11Forwarding)
        {
            text.AppendLine("X11 forwarding is off for this connection. Turn on \"Forward X11\" in the connection settings and reconnect.");
        }
        else if (x11 == null)
        {
            text.AppendLine("X11 forwarding is on in the connection settings but is not active for this connection; reconnect to enable it.");
        }
        else if (x11.IsRefused)
        {
            text.AppendLine("X11 forwarding: the server refused it.");
        }
        else if (x11.IsAccepted)
        {
            text.AppendLine("X11 forwarding: active. Programs started in terminal tabs get DISPLAY; " + x11.ConnectionCount + " X11 connection(s) so far, " + x11.ActiveConnectionCount + " open.");
        }
        else
        {
            text.AppendLine("X11 forwarding: not requested yet; it is requested when a terminal tab or a Smart Terminal command starts.");
        }
        text.AppendLine();
        text.AppendLine("Remote host " + session?.HostName + ":");
        X11RemoteStatus status = (session != null) ? X11RemoteStatus.Check(session) : new X11RemoteStatus();
        text.AppendLine(status.Describe());
        if (x11 != null && x11.IsRefused)
        {
            text.AppendLine();
            text.AppendLine(status.DescribeRefusal(session.HostName));
        }
        text.AppendLine();
        text.AppendLine("This computer:");
        text.AppendLine(VcxsrvSetup.DescribeInstallation(VcxsrvSetup.FindExecutable()));
        text.AppendLine("X server type in Settings: " + DescribeXServerSetting(_Controller.Settings));
        text.Append("X server on localhost:6000 (display :0): ").Append(IsLocalXServerRunning() ? "running" : "not running (it is started when a remote program connects)");
        return text.ToString();
    }

    // The window "X Server Required" appears only for "Ask on first use": it is the explanation the check gives for it.
    private static string DescribeXServerSetting(GlobalSettings settings)
    {
        switch (settings.XServer)
        {
            case XServerType.Vcxsrv:
                return "VcXsrv, started automatically when a remote program connects";
            case XServerType.Custom:
                return "custom (" + settings.CustomXServerPath + "), started automatically when a remote program connects";
            default:
                return "Ask on first use: the \"X Server Required\" window appears when a remote program needs an X server and nothing listens on localhost:6000";
        }
    }

    private static bool IsLocalXServerRunning()
    {
        try
        {
            using System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient();
            return client.ConnectAsync("localhost", 6000).Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private string TransferQueueFile => (Session == null) ? null : TransferQueueStore.GetFilePath(Path.Combine(GlobalSettings.DataDirectory, "TransferQueues"), Session.UserName, Session.HostName, Session.Port);

    /// <summary>
    /// Applies the file transfer settings (protocol, parallel transfers, speed limit, checks) to the queue.
    /// </summary>
    public void ApplyTransferSettings()
    {
        FileTransferManager manager = TransferManager;
        if (manager == null)
        {
            return;
        }
        GlobalSettings settings = _Controller.Settings;
        manager.Protocol = settings.TransferProtocol;
        manager.MaxParallelTransfers = settings.MaxParallelTransfers;
        manager.SpeedLimitBytesPerSecond = Math.Max(0, settings.TransferSpeedLimitKBps) * 1024L;
        manager.VerifyChecksums = settings.VerifyTransfers;
        manager.ResumeInterrupted = settings.ResumeTransfers;
        manager.AtomicUploads = settings.AtomicUploads;
    }

    /// <summary>
    /// Saves the unfinished transfers, so that they come back the next time SmarTTY connects to this server.
    /// </summary>
    /// <summary>
    /// Saves the list of Tools > Port forwarding with the profile, so that the next connection has it. A quick connection
    /// without a profile file keeps the list only while the session lives.
    /// </summary>
    public void SavePortForwards()
    {
        if (Settings == null)
        {
            return;
        }
        Settings.PortForwards = PortForwards.Rules;
        if (!string.IsNullOrEmpty(Settings.FileName))
        {
            Settings.Save();
        }
    }

    public void SaveTransferQueue()
    {
        string file = TransferQueueFile;
        if (IsDevice || file == null)
        {
            return;
        }
        try
        {
            // With the option off, an old saved queue is removed so that it does not come back later.
            TransferQueueStore.Save(file, _Controller.Settings.SaveTransferQueue ? SCPController.Jobs : new FileTransferManager.TransferJob[0]);
        }
        catch
        {
        }
    }

    private void RestoreTransferQueue()
    {
        string file = TransferQueueFile;
        if (file == null || !_Controller.Settings.SaveTransferQueue)
        {
            return;
        }
        List<FileTransferManager.TransferJob> jobs = TransferQueueStore.Load(file);
        if (jobs.Count == 0)
        {
            return;
        }
        _Controller.BeginInvoke(() =>
        {
            HashSet<Guid> listed = new HashSet<Guid>(SCPController.Jobs.Select((FileTransferManager.TransferJob job) => job.Id));
            foreach (FileTransferManager.TransferJob job in jobs.Where((FileTransferManager.TransferJob job) => !listed.Contains(job.Id)))
            {
                SCPController.AddTransfer(job, save: false);
                if (job.State == FileTransferManager.TransferState.Queued)
                {
                    TransferManager.Requeue(job);
                }
            }
        });
    }

    // Runs on a transfer thread.
    private void OnTransferFinished(FileTransferManager.TransferJob job)
    {
        _Controller.BeginInvoke(() =>
        {
            SaveTransferQueue();
            if (!job.IsDownload && job.State == FileTransferManager.TransferState.Completed)
            {
                RefreshFileExplorers(RemotePathUtils.GetDirectoryName(job.RemotePath));
            }
            if (!job.IsDownload)
            {
                OnPackageUploadFinished(job);
                OnScriptUploadFinished(job);
            }
        });
    }

    // Dropped package files whose installation is offered when all of them have been uploaded (one offer per drop).
    private class PackageInstallOffer
    {
        public ITabContext Origin;

        public int Pending;

        public readonly List<string> Uploaded = new List<string>();
    }

    // Upload transfers of dropped packages, by job id; used on the UI thread only.
    private readonly Dictionary<Guid, PackageInstallOffer> _PackageInstallOffers = new Dictionary<Guid, PackageInstallOffer>();

    /// <summary>
    /// Remembers the uploads of the dropped files that the OpenWrt package manager installs (.ipk for opkg, .apk for apk), so
    /// that their installation is offered when the whole drop has been uploaded. Does nothing on a host that is not OpenWrt.
    /// </summary>
    private void RegisterPackageInstallOffer(IEnumerable<FileTransferManager.TransferJob> jobs, ITabContext origin)
    {
        LocalRepoPackageManager? manager = _LocalRepoManager;
        if (manager == null || jobs == null)
        {
            return;
        }
        FileTransferManager.TransferJob[] packages = jobs.Where((FileTransferManager.TransferJob job) => job != null && RemotePackageInstall.CanInstall(RemotePathUtils.GetFileName(job.RemotePath), manager)).ToArray();
        if (packages.Length == 0)
        {
            return;
        }
        PackageInstallOffer offer = new PackageInstallOffer
        {
            Origin = origin ?? ActiveTab,
            Pending = packages.Length
        };
        foreach (FileTransferManager.TransferJob job in packages)
        {
            _PackageInstallOffers[job.Id] = offer;
        }
    }

    /// <summary>
    /// A transfer has ended (UI thread): when it was the last upload of a dropped set of packages and at least one arrived, asks
    /// whether to install them. Failed and cancelled uploads are left out.
    /// </summary>
    private void OnPackageUploadFinished(FileTransferManager.TransferJob job)
    {
        if (!_PackageInstallOffers.TryGetValue(job.Id, out PackageInstallOffer offer))
        {
            return;
        }
        // An upload cut short by the loss of the connection is resumed after reconnecting (the same job, the same id) and
        // reports again when it ends. Until then it is neither done nor failed: counting it as finished would offer the
        // installation of an incomplete set now, and drop the offer for good before the resumed upload completes. By the
        // time this runs on the UI thread the job may already have been requeued, hence the other two states.
        if (job.State == FileTransferManager.TransferState.Queued || job.State == FileTransferManager.TransferState.InProgress || (job.State == FileTransferManager.TransferState.Failed && job.InterruptedByConnectionLoss))
        {
            return;
        }
        _PackageInstallOffers.Remove(job.Id);
        if (job.State == FileTransferManager.TransferState.Completed)
        {
            offer.Uploaded.Add(job.RemotePath);
        }
        offer.Pending--;
        if (offer.Pending > 0 || offer.Uploaded.Count == 0)
        {
            return;
        }
        // The tab the files were dropped on may have been closed meanwhile: the active terminal is used then.
        ITabContext origin = offer.Origin != null && Tabs.Contains(offer.Origin) ? offer.Origin : null;
        InstallRemotePackages(offer.Uploaded.ToArray(), origin, afterUpload: true);
    }

    /// <summary>
    /// Reloads the Smart Terminal file panels of this session that show <paramref name="remoteDir"/>, so that new or
    /// deleted files appear at once.
    /// </summary>
    public void RefreshFileExplorers(string remoteDir)
    {
        if (string.IsNullOrEmpty(remoteDir))
        {
            return;
        }
        string wanted = remoteDir.TrimEnd('/');
        foreach (ITabContext tab in Tabs.ToList())
        {
            FileExplorer explorer = tab.FileExplorer;
            string shown = explorer?.WorkingDirectory?.TrimEnd('/');
            if (explorer != null && shown != null && shown == wanted)
            {
                explorer.Refresh();
            }
        }
    }

    #endregion

    /// <summary>Queues the upload of a file; returns the queued transfer, or null if nothing was queued.</summary>
    public FileTransferManager.TransferJob DoUploadFile(string file, string remoteDir, bool checkOverwrite = true)
    {
        // This directory is the remote one offered next time a remote file is picked, so it must not be the local one.
        LastDownloadDir = remoteDir;
        string fileName = Path.GetFileName(file);
        string text = remoteDir.TrimEnd('/') + "/" + fileName;
        FileTransferManager.TransferJob transferJob = null;
        try
        {
            transferJob = TransferManager.EnqueueUpload(file, text, null, checkOverwrite);
            if (transferJob != null)
            {
                SCPController.AddTransfer(transferJob);
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Error("Cannot upload " + file + ": " + exception.Message);
        }
        return transferJob;
    }

    public void ExecuteAsyncCommand(string command, string args, ref IRemoteCommand job)
    {
        SshShellChannel sshShellChannel = Session.OpenShellChannel(command + " " + args);
        if (sshShellChannel != null)
        {
            job = new RemoteCommandJob(sshShellChannel);
        }
    }

    public void CloseTabPageForControl(TerminalEmulatorControl ctl, bool autoInvoked = false)
    {
        if (!autoInvoked || _Controller.Settings.AutoCloseTabs)
        {
            CloseFirstMatchingPage((ITabContext tab) => tab is NormalTabContext normalTabContext && normalTabContext.Terminal == ctl, closeSessionIfEmpty: true);
        }
    }

    /// <summary>
    /// Uploads dropped files and folders into <paramref name="remoteDir"/> if given (a drop on the file panel), else into
    /// the current directory of <paramref name="targetTab"/> (or of the active tab), falling back to the default upload
    /// directory. Files go to the transfer queue after a single overwrite
    /// question for all of them; each folder opens the directory upload window and starts uploading at once. For dropped .sh files the user
    /// is asked whether to convert their line endings to Unix and make them executable (chmod +x) once uploaded.
    /// </summary>
    public void UploadFiles(string[] files, ITabContext targetTab = null, string remoteDir = null)
    {
        if (IsDevice || files == null)
        {
            return;
        }
        string[] regularFiles = files.Where(File.Exists).ToArray();
        string[] folders = files.Where(Directory.Exists).ToArray();
        if (regularFiles.Length == 0 && folders.Length == 0)
        {
            return;
        }
        remoteDir ??= TryExtractCurrentDir(targetTab);
        if (remoteDir == null)
        {
            string dir = _Controller.Settings.DefaultUploadDirectory;
            ExpandHomeDirIfNeeded(ref dir);
            _Controller.Settings.DefaultUploadDirectory = dir;
            remoteDir = dir;
        }
        if (string.IsNullOrEmpty(remoteDir))
        {
            MessageBoxes.Error("Cannot determine the remote directory to upload to. Set the default upload directory in the SCP upload window.");
            return;
        }
        if (regularFiles.Length != 0)
        {
            // chmod +x and Unix line ends are for a Unix host; a Windows host gets the file as it is.
            string[] scripts = Session?.RemoteOs == RemoteOsKind.Windows ? new string[0] : regularFiles.Where(IsDroppedShellScript).ToArray();
            if (scripts.Length != 0)
            {
                switch (MessageBoxes.YesNoCancel(BuildShellScriptQuestion(scripts, remoteDir)))
                {
                    case System.Windows.MessageBoxResult.Yes:
                        break;
                    case System.Windows.MessageBoxResult.No:
                        // Uploaded as they are, like any other file.
                        scripts = new string[0];
                        break;
                    default:
                        regularFiles = regularFiles.Except(scripts).ToArray();
                        scripts = new string[0];
                        break;
                }
            }
            if (regularFiles.Length != 0)
            {
                // A dropped .ipk or .apk is offered for installation when its upload is complete (OpenWrt only).
                RegisterPackageInstallOffer(UploadFilesAndPrepareScripts(regularFiles, scripts, remoteDir), targetTab);
            }
        }
        foreach (string folder in folders)
        {
            // A dropped drive root (D:\) has no name of its own: its contents go straight into the directory.
            string folderName = Path.GetFileName(folder.TrimEnd('\\', '/'));
            string remoteFolder = string.IsNullOrEmpty(folderName) ? remoteDir : remoteDir.TrimEnd('/') + "/" + folderName;
            if (Session?.RemoteOs == RemoteOsKind.Windows)
            {
                // The directory upload window works with tar and stat on the host: a Windows host is walked over SFTP instead.
                UploadFolderOverSftp(folder, remoteFolder);
            }
            else
            {
                ShowDirectoryUploadForm(folder, remoteFolder, startNow: true);
            }
        }
    }

    /// <summary>
    /// A paste that is too large to be typed into the terminal, sent as a file instead: the text is saved in a temporary
    /// file and uploaded to the current directory of the terminal like a dropped file.
    /// </summary>
    public void UploadPastedText(string text, TerminalEmulatorControl terminal)
    {
        try
        {
            NormalTabContext tab = Tabs.OfType<NormalTabContext>().FirstOrDefault((NormalTabContext candidate) => candidate.Terminal == terminal);
            string directory = Path.Combine(Path.GetTempPath(), "SmarTTY-paste");
            Directory.CreateDirectory(directory);
            foreach (string old in Directory.GetFiles(directory, "paste-*.txt"))
            {
                try
                {
                    if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-1))
                    {
                        File.Delete(old);
                    }
                }
                catch
                {
                }
            }
            string file = Path.Combine(directory, "paste-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            File.WriteAllText(file, text, new System.Text.UTF8Encoding(false));
            UploadFiles(new[] { file }, tab);
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    // Above this size a dropped script is uploaded unchanged: it is not a script written by hand, and it is read into memory to fix it.
    private const long MaxScriptFixSize = 32L * 1024 * 1024;

    private static bool IsDroppedShellScript(string file)
    {
        return file.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildShellScriptQuestion(string[] scripts, string remoteDir)
    {
        string what = scripts.Length == 1
            ? "Upload the shell script " + Path.GetFileName(scripts[0]) + " to " + remoteDir + "?"
            : "Upload these shell scripts to " + remoteDir + "?\n\n" + FormatFileList(scripts.Select((string script) => Path.GetFileName(script)).ToList());
        return what + "\n\nMake " + (scripts.Length == 1 ? "it" : "them") + " executable (chmod +x) and convert the line endings to Unix (LF)?"
            + "\n\nYes: do both. No: upload as is. Cancel: do not upload " + (scripts.Length == 1 ? "it." : "them.");
    }

    // Uploads of dropped shell scripts that are made executable once they have arrived, by job id; used on the UI thread only.
    private class ScriptUpload
    {
        // The folder with the copy that has Unix line endings (null when the file needed no change); removed when the upload ends.
        public string TempDirectory;
    }

    private readonly Dictionary<Guid, ScriptUpload> _ScriptUploads = new Dictionary<Guid, ScriptUpload>();

    /// <summary>
    /// Queues the upload of <paramref name="files"/>; the ones also in <paramref name="scripts"/> are sent with Unix line ends
    /// (and without a UTF-8 byte order mark, which hides the "#!" from the system) and get chmod +x when their upload is complete.
    /// The dropped file itself is never changed: a script with Windows line ends is sent from a corrected copy of it.
    /// </summary>
    private List<FileTransferManager.TransferJob> UploadFilesAndPrepareScripts(string[] files, string[] scripts, string remoteDir)
    {
        HashSet<string> scriptSet = new HashSet<string>(scripts.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        // Full path of what is uploaded -> its folder when it is a corrected copy.
        Dictionary<string, string> scriptPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<string> toUpload = new List<string>();
        foreach (string file in files)
        {
            if (!scriptSet.Contains(Path.GetFullPath(file)))
            {
                toUpload.Add(file);
                continue;
            }
            string upload = file;
            string tempDirectory = null;
            try
            {
                if (new FileInfo(file).Length <= MaxScriptFixSize)
                {
                    byte[] original = File.ReadAllBytes(file);
                    byte[] fixedContent = RemoteScript.Normalize(original);
                    // Normalize only ever removes bytes (CR of CR LF, the byte order mark).
                    if (fixedContent.Length != original.Length)
                    {
                        tempDirectory = Path.Combine(Path.GetTempPath(), "SmarTTY-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempDirectory);
                        upload = Path.Combine(tempDirectory, Path.GetFileName(file));
                        File.WriteAllBytes(upload, fixedContent);
                    }
                }
            }
            catch (Exception exception)
            {
                DeleteTempDirectory(tempDirectory);
                tempDirectory = null;
                upload = file;
                MessageBoxes.Warning("Cannot convert the line endings of " + file + ": " + exception.Message + "\n\nIt is uploaded as it is.");
            }
            toUpload.Add(upload);
            scriptPaths[Path.GetFullPath(upload)] = tempDirectory;
        }
        List<FileTransferManager.TransferJob> jobs = UploadFilesToDirectory(toUpload, remoteDir);
        HashSet<string> queuedPaths = new HashSet<string>(jobs.Select((FileTransferManager.TransferJob job) => Path.GetFullPath(job.LocalPath)), StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> script in scriptPaths)
        {
            if (!queuedPaths.Contains(script.Key))
            {
                // Skipped (not overwritten) or cancelled: nothing will read the copy.
                DeleteTempDirectory(script.Value);
            }
        }
        foreach (FileTransferManager.TransferJob job in jobs)
        {
            if (scriptPaths.TryGetValue(Path.GetFullPath(job.LocalPath), out string directory))
            {
                _ScriptUploads[job.Id] = new ScriptUpload { TempDirectory = directory };
            }
        }
        return jobs;
    }

    private static void DeleteTempDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // A leftover in the temp folder is harmless.
        }
    }

    /// <summary>
    /// A transfer has ended (UI thread): when it was the upload of a dropped shell script, removes the corrected copy and, if the
    /// script arrived, makes it executable (chmod +x, within the umask) on a worker thread.
    /// </summary>
    private void OnScriptUploadFinished(FileTransferManager.TransferJob job)
    {
        if (!_ScriptUploads.TryGetValue(job.Id, out ScriptUpload upload))
        {
            return;
        }
        // An upload cut short by the loss of the connection is resumed after reconnecting (the same job): it is not over yet,
        // and the copy it reads from must stay (see OnPackageUploadFinished).
        if (job.State == FileTransferManager.TransferState.Queued || job.State == FileTransferManager.TransferState.InProgress || (job.State == FileTransferManager.TransferState.Failed && job.InterruptedByConnectionLoss))
        {
            return;
        }
        _ScriptUploads.Remove(job.Id);
        DeleteTempDirectory(upload.TempDirectory);
        if (job.State != FileTransferManager.TransferState.Completed)
        {
            return;
        }
        string remotePath = job.RemotePath;
        SshSession session = Session;
        ThreadPool.QueueUserWorkItem((object state) =>
        {
            string error = null;
            try
            {
                string command = RemoteFileOperations.ChmodCommands(new[] { remotePath }, "+x", ChmodScope.Selected)[0];
                if (session.RunCommandChecked(command, requireSuccess: true) == null)
                {
                    session.ThrowIfConnectionLost();
                    error = "the chmod command failed";
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
            _Controller.BeginInvoke(() =>
            {
                if (error != null)
                {
                    MessageBoxes.Warning("The script " + remotePath + " was uploaded, but it could not be made executable: " + error + ".");
                }
                RefreshFileExplorers(RemotePathUtils.GetDirectoryName(remotePath));
            });
        });
    }

    /// <summary>
    /// Queues several uploads into one remote directory, asking once about all the files that already exist there. Returns the
    /// queued transfers (none when the user cancelled or the directory is not usable).
    /// </summary>
    public List<FileTransferManager.TransferJob> UploadFilesToDirectory(IList<string> files, string remoteDir)
    {
        List<FileTransferManager.TransferJob> queued = new List<FileTransferManager.TransferJob>();
        string[] remotePaths = files.Select((string file) => remoteDir.TrimEnd('/') + "/" + Path.GetFileName(file)).ToArray();
        RemotePathKind[] kinds;
        try
        {
            // The target directory is checked in the same round trip as the files.
            kinds = Session.GetRemotePathKinds(remotePaths.Concat(new[] { remoteDir }).ToList());
            switch (kinds[files.Count])
            {
                case RemotePathKind.File:
                    MessageBoxes.Error("Cannot upload files to " + remoteDir + ": it is a file, not a directory.");
                    return queued;
                case RemotePathKind.Missing:
                    if (!MessageBoxes.Ask(remoteDir + " does not exist on the server. Create it?"))
                    {
                        return queued;
                    }
                    if (Session.RemoteOs == RemoteOsKind.Windows)
                    {
                        using SftpChannel sftp = SftpChannel.Open(Session);
                        new SftpFileSystem(sftp).CreateDirectories(remoteDir);
                    }
                    else if (Session.RunCommandChecked("mkdir -p " + RemotePathUtils.ShellQuote(remoteDir), requireSuccess: true) == null)
                    {
                        // The outer catch reports a lost connection as such instead of "cannot create".
                        Session.ThrowIfConnectionLost();
                        MessageBoxes.Error("Cannot create " + remoteDir + ".");
                        return queued;
                    }
                    break;
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Error("Cannot upload files: " + exception.Message);
            return queued;
        }
        List<string> directories = Enumerable.Range(0, files.Count).Where((int i) => kinds[i] == RemotePathKind.Directory).Select((int i) => remotePaths[i]).ToList();
        if (directories.Count != 0)
        {
            MessageBoxes.Error("These files cannot be uploaded because a directory with the same name exists on the server:\n\n" + FormatFileList(directories));
        }
        List<int> existing = Enumerable.Range(0, files.Count).Where((int i) => kinds[i] == RemotePathKind.File).ToList();
        bool overwrite = true;
        if (existing.Count == 1 && files.Count == 1)
        {
            if (!MessageBoxes.AskWarn(remotePaths[0] + " already exists. Overwrite it?"))
            {
                return queued;
            }
        }
        else if (existing.Count != 0)
        {
            string question = "These files already exist in " + remoteDir + ":\n\n" + FormatFileList(existing.Select((int i) => Path.GetFileName(files[i])).ToList())
                + "\n\nYes: overwrite them. No: skip them and upload the rest. Cancel: upload nothing.";
            switch (MessageBoxes.YesNoCancel(question))
            {
                case System.Windows.MessageBoxResult.Yes:
                    break;
                case System.Windows.MessageBoxResult.No:
                    overwrite = false;
                    break;
                default:
                    return queued;
            }
        }
        for (int i = 0; i < files.Count; i++)
        {
            if (kinds[i] == RemotePathKind.Directory || (kinds[i] == RemotePathKind.File && !overwrite))
            {
                continue;
            }
            // Unknown (the check could not run) is left to the transfer itself, which reports what goes wrong.
            FileTransferManager.TransferJob job = DoUploadFile(files[i], remoteDir, checkOverwrite: false);
            if (job != null)
            {
                queued.Add(job);
            }
        }
        return queued;
    }

    private static string FormatFileList(IList<string> names)
    {
        const int MaxShown = 15;
        string list = string.Join("\n", names.Take(MaxShown));
        return (names.Count > MaxShown) ? list + $"\n... and {names.Count - MaxShown} more" : list;
    }

    /// <summary>
    /// Opens the directory upload window for <paramref name="localDir"/>; the transfer starts when the user confirms it there.
    /// </summary>
    public void ShowDirectoryUploadForm(string localDir, string remoteDir, bool startNow = false)
    {
        DirectoryTransferWindow window = new DirectoryTransferWindow(Session, isUpload: true)
        {
            LocalDirectory = localDir,
            RemoteDirectory = remoteDir
        };
        // The new folder shows up in the file panels once the upload window is closed.
        window.Closed += (object sender, EventArgs e) =>
        {
            RefreshFileExplorers(RemotePathUtils.GetDirectoryName(remoteDir.TrimEnd('/')));
            RefreshFileExplorers(remoteDir);
        };
        WindowOwnership.Attach(window, WindowOwnership.ActiveWindow());
        window.Show();
        if (startNow)
        {
            window.StartTransfer();
        }
    }

    public string TryExtractCurrentDir(ITabContext activeTab = null)
    {
        TerminalEmulatorControl terminalEmulatorControl = ((activeTab ?? ActiveTab) as NormalTabContext)?.Terminal;
        if (terminalEmulatorControl == null)
        {
            return null;
        }
        return AppEnvironment.GetCurrentDirectory(terminalEmulatorControl, Session, ref _CachedHomeDir);
    }

    private void Tabs_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Remove:
                {
                    foreach (ITabContext item in e.OldItems.OfType<ITabContext>())
                    {
                        _PagesInLastUsedOrder.Remove(item);
                    }
                    break;
                }
            case NotifyCollectionChangedAction.Add:
                {
                    foreach (ITabContext entry in from tabContext in e.NewItems.OfType<ITabContext>()
                                                  where !(tabContext is PseudoTabContext)
                                                  select tabContext)
                    {
                        _PagesInLastUsedOrder.Insert(0, entry);
                    }
                    break;
                }
        }
    }

    internal RemoteOpenFileWindow CreateRemoteOpenFileDialog()
    {
        RemoteOpenFileWindow window = RemoteOpenFileWindow.Create(ExecuteAsyncCommand);
        window.InitialDirectory = TryExtractCurrentDir() ?? LastDownloadDir;
        return window;
    }

    public void CreateNewTerminalPage(bool virtualMode = false, InitialCommand command = default(InitialCommand), string initialDirForVirtualMode = null)
    {
        if (IsDevice)
        {
            // One owner per device: a second terminal on the same COM port (or a clone of the connection) is not offered.
            MessageBoxes.Info("A COM/TCP session has a single terminal. Open another port from the 'Connect to another target' list, or close this tab first.");
            return;
        }
        try
        {
            _TabUpdateInProgress++;
            TerminalEmulatorControl term = _Controller.CreateNewTerminal(this, command, virtualMode, initialDirForVirtualMode);
            NormalTabContext item = new NormalTabContext(this, term);
            int firstPseudoPage = FirstPseudoPage;
            Tabs.Insert(firstPseudoPage, item);
            ActiveTabIndex = firstPseudoPage;
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
        finally
        {
            _TabUpdateInProgress--;
        }
    }

    public EditorTabContext AddNewFileEditorOrThrow(RemoteFileEditor editor, string filePath)
    {
        try
        {
            _TabUpdateInProgress++;
            EditorTabContext editorTabContext = new EditorTabContext(this, editor, filePath);
            int firstPseudoPage = FirstPseudoPage;
            Tabs.Insert(firstPseudoPage, editorTabContext);
            ActiveTabIndex = firstPseudoPage;
            return editorTabContext;
        }
        finally
        {
            _TabUpdateInProgress--;
        }
    }

    public void DoDownloadFile(string file, string localDir, bool openWhenDone, bool confirmOverwrite = true)
    {
        LastDownloadDir = RemotePathUtils.GetDirectoryName(file);
        // The remote name may contain characters that Windows does not allow in file names (':', '?', '*', ...).
        string fileName = RemotePathUtils.GetFileName(file);
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }
        string text = Path.Combine(localDir, fileName);
        if (Directory.Exists(text))
        {
            MessageBoxes.Error("Cannot download " + file + ": " + text + " is a directory.");
            return;
        }
        if (confirmOverwrite && File.Exists(text) && !MessageBoxes.AskWarn(text + " already exists. Overwrite it?"))
        {
            return;
        }
        try
        {
            FileTransferManager.TransferJob transferJob = TransferManager.EnqueueDownload(file, text, null, openWhenDone);
            SCPController.AddTransfer(transferJob);
        }
        catch (Exception exception)
        {
            MessageBoxes.Error("Cannot download a file: " + exception.Message);
        }
    }

    /// <summary>
    /// Downloads the remote directory <paramref name="remoteDir"/> as "&lt;name&gt;.tar.gz" in <paramref name="localDir"/>.
    /// </summary>
    public void DoDownloadArchive(string remoteDir, string localDir, bool openWhenDone, bool confirmOverwrite = true)
    {
        if (Session.RemoteOs == RemoteOsKind.Windows)
        {
            // No tar on the host: the folder is walked over SFTP and its files go through the queue one by one.
            DownloadFolderOverSftp(remoteDir, localDir, openWhenDone, confirmOverwrite);
            return;
        }
        // The remote name may contain characters that Windows does not allow in file names (':', '?', '*', ...).
        string fileName = RemotePathUtils.GetFileName(remoteDir.TrimEnd('/'));
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }
        string text = Path.Combine(localDir, fileName + ".tar.gz");
        if (Directory.Exists(text))
        {
            MessageBoxes.Error("Cannot download " + remoteDir + ": " + text + " is a directory.");
            return;
        }
        if (confirmOverwrite && File.Exists(text) && !MessageBoxes.AskWarn(text + " already exists. Overwrite it?"))
        {
            return;
        }
        try
        {
            FileTransferManager.TransferJob transferJob = TransferManager.EnqueueDirectoryArchiveDownload(remoteDir, text, null, openWhenDone);
            SCPController.AddTransfer(transferJob);
        }
        catch (Exception exception)
        {
            MessageBoxes.Error("Cannot download a folder: " + exception.Message);
        }
    }

    /// <summary>
    /// Downloads a folder of a Windows host as a folder: the tree is walked over SFTP in the background, the folders are made
    /// on the local disk, and the files are queued like any others (the queue shows their progress, and can stop them).
    /// </summary>
    private void DownloadFolderOverSftp(string remoteDir, string localDir, bool openWhenDone, bool confirmOverwrite)
    {
        string name = SftpFolderPlan.LocalName(RemotePathUtils.GetFileName(remoteDir.TrimEnd('/')));
        if (name.Length == 0)
        {
            name = "files";
        }
        string target = Path.Combine(localDir, name);
        if (File.Exists(target))
        {
            MessageBoxes.Error("Cannot download " + remoteDir + ": " + target + " is a file.");
            return;
        }
        if (confirmOverwrite && Directory.Exists(target) && !MessageBoxes.AskWarn(target + " already exists. Download into it, replacing the files that have the same names?"))
        {
            return;
        }
        SshSession session = Session;
        ThreadPool.QueueUserWorkItem((object state) =>
        {
            FolderPlan plan = null;
            string error = null;
            try
            {
                using SftpChannel sftp = SftpChannel.Open(session);
                plan = SftpFolderPlan.PlanDownload(sftp, remoteDir, target);
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
            _Controller.BeginInvoke(() => QueueFolderDownload(plan, error, remoteDir, openWhenDone));
        });
    }

    private void QueueFolderDownload(FolderPlan plan, string error, string remoteDir, bool openWhenDone)
    {
        if (plan == null)
        {
            MessageBoxes.Error("Cannot download " + remoteDir + ": " + error);
            return;
        }
        try
        {
            foreach (string directory in plan.Directories)
            {
                Directory.CreateDirectory(directory);
            }
            for (int index = 0; index < plan.Files.Count; index++)
            {
                FolderFile file = plan.Files[index];
                // The local folder is opened once, when the last file is queued; the size is known from the walk.
                FileTransferManager.TransferJob job = TransferManager.EnqueueDownload(file.Source, file.Destination, null, openWhenDone && index == plan.Files.Count - 1, file.Size);
                SCPController.AddTransfer(job);
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Error("Cannot download a folder: " + exception.Message);
            return;
        }
        if (plan.Skipped.Count != 0)
        {
            MessageBoxes.Warning("These were not downloaded:\n\n" + FormatFileList(plan.Skipped));
        }
    }

    /// <summary>
    /// Uploads a local folder to a Windows host: the folders are made over SFTP, and the files are queued like any others. Asks
    /// first when the folder is already on the server.
    /// </summary>
    private void UploadFolderOverSftp(string localFolder, string remoteFolder)
    {
        SshSession session = Session;
        ThreadPool.QueueUserWorkItem((object state) =>
        {
            FolderPlan plan = null;
            bool exists = false;
            string error = null;
            try
            {
                plan = SftpFolderPlan.PlanUpload(localFolder, remoteFolder);
                using SftpChannel sftp = SftpChannel.Open(session);
                SftpFileAttributes attributes = sftp.Stat(WindowsSftpPath.ForServer(remoteFolder));
                if (attributes != null && !attributes.IsDirectory)
                {
                    throw new RemoteOperationException(remoteFolder + " is a file on the server, not a folder.");
                }
                exists = attributes != null;
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
            _Controller.BeginInvoke(() =>
            {
                if (plan == null)
                {
                    MessageBoxes.Error("Cannot upload " + localFolder + ": " + error);
                    return;
                }
                if (exists && !MessageBoxes.AskWarn(remoteFolder + " already exists on the server. Upload into it, replacing the files that have the same names?"))
                {
                    return;
                }
                ThreadPool.QueueUserWorkItem((object innerState) =>
                {
                    string createError = null;
                    try
                    {
                        using SftpChannel sftp = SftpChannel.Open(session);
                        SftpFileSystem fileSystem = new SftpFileSystem(sftp);
                        foreach (string directory in plan.Directories)
                        {
                            fileSystem.CreateDirectories(directory);
                        }
                    }
                    catch (Exception exception)
                    {
                        createError = exception.Message;
                    }
                    _Controller.BeginInvoke(() => QueueFolderUpload(plan, createError, localFolder, remoteFolder));
                });
            });
        });
    }

    private void QueueFolderUpload(FolderPlan plan, string error, string localFolder, string remoteFolder)
    {
        if (error != null)
        {
            MessageBoxes.Error("Cannot create the folders of " + remoteFolder + ": " + error);
            return;
        }
        try
        {
            foreach (FolderFile file in plan.Files)
            {
                FileTransferManager.TransferJob job = TransferManager.EnqueueUpload(file.Source, file.Destination, null, checkOverwrite: false);
                if (job != null)
                {
                    SCPController.AddTransfer(job);
                }
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Error("Cannot upload " + localFolder + ": " + exception.Message);
        }
        // The new folder shows up in the file panels now: the files follow as the queue finishes them.
        RefreshFileExplorers(RemotePathUtils.GetDirectoryName(remoteFolder.TrimEnd('/')));
        RefreshFileExplorers(remoteFolder);
        if (plan.Skipped.Count != 0)
        {
            MessageBoxes.Warning("These were not uploaded:\n\n" + FormatFileList(plan.Skipped));
        }
    }

    /// <summary>
    /// Runs a shell script that is on the server from the folder it is in (see <see cref="RemoteScript.BuildRunInPlaceCommand"/>),
    /// in the terminal tab that is active now: the command is typed there and everything the script prints and asks is in
    /// that terminal.
    /// </summary>
    public void RunRemoteScript(string remotePath)
    {
        ITabContext origin = ActiveTab;
        SshSession session = Session;
        ThreadPool.QueueUserWorkItem(delegate
        {
            // Whether the file can be executed and what its shebang says; without an answer sh is used.
            List<string> probe = null;
            try
            {
                probe = session.RunCommand(RemoteScript.ProbeCommand(remotePath), out _);
            }
            catch (Exception)
            {
            }
            string command = RemoteScript.BuildRunInPlaceCommand(remotePath, probe);
            _Controller.BeginInvoke(() => RunInTerminal(command, origin));
        });
    }

    /// <summary>
    /// Installs package files that are on the OpenWrt device (the file list's "Install package" items): .ipk files with opkg,
    /// .apk files with apk, after the user has confirmed. Only an OpenWrt host (see <see cref="LocalRepoManager"/>) is
    /// accepted, and only the files its package manager installs. The command is typed in the terminal tab that is active now,
    /// so that its output, questions and errors (a user that is not root, missing dependencies) are there to read. After an
    /// upload (a package dropped on the terminal) it is typed in the tab the file was dropped on, <paramref name="origin"/>.
    /// </summary>
    public void InstallRemotePackages(string[] remotePaths, ITabContext origin = null, bool afterUpload = false)
    {
        LocalRepoPackageManager? manager = _LocalRepoManager;
        if (manager == null)
        {
            MessageBoxes.Error("Packages can be installed only on an OpenWrt device with opkg or apk.");
            return;
        }
        string[] packages = (remotePaths ?? new string[0]).Where((string path) => RemotePackageInstall.CanInstall(RemotePathUtils.GetFileName(path), manager)).ToArray();
        if (packages.Length == 0)
        {
            MessageBoxes.Error("This device uses " + RemotePackageInstall.ToolName(manager.Value) + ", which installs " + (manager == LocalRepoPackageManager.Apk ? ".apk" : ".ipk") + " packages.");
            return;
        }
        string list = string.Join("\n", packages.Take(10).Select((string path) => RemotePathUtils.GetFileName(path)));
        if (packages.Length > 10)
        {
            list += "\n... and " + (packages.Length - 10) + " more";
        }
        string note = manager == LocalRepoPackageManager.Apk ? "\n\nThe packages are installed without a signature check (apk add --allow-untrusted)." : "";
        if (!MessageBoxes.Ask((afterUpload ? "The upload is complete.\n\n" : "") + "Install " + (packages.Length == 1 ? "this package" : packages.Length + " packages") + " with " + RemotePackageInstall.ToolName(manager.Value) + "?\n\n" + list + note))
        {
            return;
        }
        RunInTerminal(RemotePackageInstall.BuildInstallCommand(manager.Value, packages), origin ?? ActiveTab);
    }

    /// <summary>
    /// Types <paramref name="commandLine"/> into the terminal of <paramref name="origin"/> (the active tab when it is not
    /// a terminal: the terminal used last) and shows that tab. A Smart Terminal runs it as the next command; a classic
    /// terminal gets it as typed text for its shell. The line starts with a blank, so that shells that skip such lines
    /// do not keep it in their history. Without a terminal tab a new one is opened for the command.
    /// </summary>
    private void RunInTerminal(string commandLine, ITabContext origin)
    {
        try
        {
            NormalTabContext tab = origin as NormalTabContext ?? ActiveTab as NormalTabContext ?? _PagesInLastUsedOrder.OfType<NormalTabContext>().FirstOrDefault();
            if (tab == null)
            {
                CreateNewTerminalPage(virtualMode: false, new InitialCommand(commandLine, echoInitialCommand: false));
                return;
            }
            int index = Tabs.IndexOf(tab);
            if (index >= 0 && ActiveTabIndex != index)
            {
                ActiveTabIndex = index;
            }
            if (tab.Terminal is SshTerminalControl smartTerminal)
            {
                if (!smartTerminal.Submit(" " + commandLine))
                {
                    MessageBoxes.Info("The terminal is busy with another command. Wait until it ends and try again.");
                }
                return;
            }
            TerminalTag terminalTag = tab.Terminal.Tag as TerminalTag;
            SshShellChannel channel = terminalTag?.Channel;
            if (channel == null || channel.IsDisposed)
            {
                MessageBoxes.Error("The terminal is not connected.");
                return;
            }
            byte[] input = System.Text.Encoding.UTF8.GetBytes(" " + commandLine + "\r");
            // Writing may wait for the server (the window of the channel is full, the connection is slow), which must not
            // block the UI thread. The terminal's queue writes on a thread of the pool and keeps this command in order with the
            // commands given before it and with the keys pressed after it.
            terminalTag.Writes.Post(delegate
            {
                try
                {
                    channel.Input.Write(input, 0, input.Length);
                }
                catch (Exception exception)
                {
                    _Controller.BeginInvoke(() => MessageBoxes.Exception(exception));
                }
            });
            tab.Terminal.Focus();
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    /// <summary>
    /// Runs a local shell script on the server: it is uploaded to /tmp (owner-only, executable), run in the terminal tab
    /// that is active now, so that everything it prints (and what it asks for) is in that terminal, and removed again when it
    /// has ended. A script with a shebang uses the interpreter it names (bash, ash, ...), one without runs under sh. It runs in
    /// the directory the terminal is in.
    /// </summary>
    public void RunScript(string localPath)
    {
        byte[] content;
        try
        {
            content = File.ReadAllBytes(localPath);
        }
        catch (Exception exception)
        {
            MessageBoxes.Error("Cannot read " + localPath + ": " + exception.Message);
            return;
        }
        string problem = RemoteScript.Validate(content);
        if (problem != null)
        {
            MessageBoxes.Error(Path.GetFileName(localPath) + ": " + problem);
            return;
        }
        byte[] data = RemoteScript.Normalize(content);
        string remotePath = RemoteScript.MakeRemotePath(Path.GetFileName(localPath), Guid.NewGuid().ToString("N").Substring(0, 8));
        string command = RemoteScript.BuildCommand(remotePath, RemoteScript.HasShebang(data));
        ITabContext origin = ActiveTab;
        SshSession session = Session;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                using ScpChannelStream upload = session.OpenUpload(remotePath, data.Length, DateTime.Now, RemoteScript.FileMode);
                upload.Write(data, 0, data.Length);
            }
            catch (Exception exception)
            {
                _Controller.BeginInvoke(() => MessageBoxes.Error("Cannot upload the script to " + remotePath + ": " + exception.Message));
                return;
            }
            _Controller.BeginInvoke(() => RunInTerminal(command, origin));
        });
    }

    public void CloseFirstMatchingPage(Predicate<ITabContext> pred, bool closeSessionIfEmpty)
    {
        for (int tabIndex = 0; tabIndex < Tabs.Count; tabIndex++)
        {
            ITabContext tabContext = Tabs[tabIndex];
            if (pred(tabContext))
            {
                if (ActiveTabIndex == tabIndex)
                {
                    ActiveTabIndex = Math.Max(0, ActiveTabIndex - 1);
                }
                try
                {
                    _TabUpdateInProgress++;
                    Tabs.RemoveAt(tabIndex);
                }
                finally
                {
                    _TabUpdateInProgress--;
                }
                tabContext.Dispose();
                break;
            }
        }
        // A device session keeps no pseudo tabs of its own, so "no pseudo page" must not be read as "no tabs left".
        if (!IsDevice && FirstPseudoPage == 0)
        {
            _Controller.CloseSession(this);
        }
    }

    public void Dispose()
    {
        // Saved before the queue is stopped, so that the transfers still waiting come back as waiting, not as failed.
        SaveTransferQueue();
        TransferManager?.Dispose();
        try
        {
            // Takes the feeds out of the opkg configuration of the device while the connection is still open.
            LocalRepo.Stop(Session);
        }
        catch
        {
        }
        SshSession sessionToClose = Session;
        DiskSpace.Dispose();
        if (SocksProxy.IsRunning || PortForwards.HasActive)
        {
            // Stopping the proxy and the forwardings waits (up to 30 s) for the programs that still use them to close their
            // connections: not on the UI thread.
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    SocksProxy.Stop();
                }
                catch
                {
                }
                try
                {
                    PortForwards.StopAll();
                }
                catch
                {
                }
                try
                {
                    sessionToClose?.Dispose();
                }
                catch
                {
                }
            });
        }
        else
        {
            sessionToClose?.Dispose();
        }
        foreach (DeviceTabContext deviceTab in Tabs.OfType<DeviceTabContext>().ToList())
        {
            deviceTab.Dispose();
        }
    }

    internal void OnCtrlTabPressed(bool bShift)
    {
        if (_PagesInLastUsedOrder.Count == 0)
        {
            return;
        }
        if (!bShift)
        {
            if (++m_CtrlTabScrollIndex >= _PagesInLastUsedOrder.Count)
            {
                m_CtrlTabScrollIndex = 0;
            }
        }
        else if (--m_CtrlTabScrollIndex < 0)
        {
            m_CtrlTabScrollIndex = _PagesInLastUsedOrder.Count - 1;
        }
        m_bCtrlTabActive = true;
        int index = Tabs.IndexOf(_PagesInLastUsedOrder[m_CtrlTabScrollIndex]);
        if (index != -1)
        {
            ActiveTabIndex = index;
        }
    }

    internal void OnCtrlReleased()
    {
        if (m_bCtrlTabActive)
        {
            if (_PagesInLastUsedOrder.Count > 0 && m_CtrlTabScrollIndex >= 0 && m_CtrlTabScrollIndex < _PagesInLastUsedOrder.Count)
            {
                ITabContext item = _PagesInLastUsedOrder[m_CtrlTabScrollIndex];
                _PagesInLastUsedOrder.RemoveAt(m_CtrlTabScrollIndex);
                _PagesInLastUsedOrder.Insert(0, item);
            }
            m_CtrlTabScrollIndex = 0;
            m_bCtrlTabActive = false;
        }
    }

    public void Close()
    {
        _Controller.CloseSession(this);
        _Analyzer?.Dispose();
    }

    public ShellCompletionAnalyzer ProvideAnalyzer()
    {
        lock (_Sync)
        {
            if (_Analyzer == null)
            {
                _Analyzer = new ShellCompletionAnalyzer(this, Session);
            }
        }
        return _Analyzer;
    }

    /// <summary>
    /// Asks a host whose banner did not say what it is whether it runs Windows, and remembers the answer. The package manager
    /// window calls it when no package manager of Linux answered, so that it does not depend on the OpenWrt probe being finished.
    /// </summary>
    internal bool DetectWindowsHost()
    {
        SshSession session = Session;
        if (session == null)
        {
            return false;
        }
        if (session.RemoteOs == RemoteOsKind.Windows)
        {
            return true;
        }
        List<string> output = session.RunCommand(RemoteOsDetector.WindowsProbeCommand, out _, timeout: TimeSpan.FromSeconds(20));
        if (RemoteOsDetector.FromWindowsProbe(output) != RemoteOsKind.Windows)
        {
            return false;
        }
        // The session knows at once, so the commands that follow are not wrapped in sh; the rest follows on the UI thread.
        session.RemoteOs = RemoteOsKind.Windows;
        _Controller.BeginInvoke(() =>
        {
            if (ReferenceEquals(Session, session))
            {
                SetRemoteOs(RemoteOsKind.Windows);
            }
        });
        return true;
    }

    public string[] RunCommand(string command)
    {
        // The package manager probes and commands of Linux are POSIX shell: run them in sh whatever the login shell is.
        // A Windows host has no sh: its commands (winget) go to its own shell as they are.
        string line = Session.RemoteOs == RemoteOsKind.Windows ? command : PosixShell.Wrap(command, addAdminDirectoriesToPath: true);
        string[] output = Session.RunCommandChecked(line, requireSuccess: true)?.ToArray();
        if (output == null)
        {
            // null means "the command failed" for the callers (a package manager that is not there); not so for a dropped connection.
            Session.ThrowIfConnectionLost();
        }
        return output;
    }
}
