using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.Forms;
using SmarTTY.IO;
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
using VisualTTY;
using VisualTTY.DeviceTerminal;

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
            if (_ReconnectionController?.CreatedSession != null)
            {
                _Session._Controller.BeginInvoke(() =>
                {
                    _Session.AttachNewSession(_ReconnectionController.CreatedSession);
                    _Session.Fixer = null;
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

    public System.Windows.Visibility SshOnlyVisibility => IsDevice ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

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

    public SshSession Session { get; private set; }

    public ScpTransferManager TransferManager { get; private set; }

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
            if (_ActiveTabIndex >= 0 && _ActiveTabIndex <= Tabs.Count)
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

    public event PropertyChangedEventHandler PropertyChanged;

    public void RaiseHideHeadersChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HideHeaders"));
    }

    public SessionWrapper(MainWindowController controllerImpl, SshSession session, SSHConnectionSettings connectionSettings)
    {
        SCPController = new SCPController(this);
        _Controller = controllerImpl;
        if (session != null)
        {
            AttachNewSession(session);
        }
        Settings = connectionSettings;
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
        Session?.Dispose();
        Session = session;
        TransferManager?.Dispose();
        TransferManager = new ScpTransferManager
        {
            Session = session,
            ConfirmOverwrite = MessageBoxes.AskWarn
        };
        session.ConnectionLost += (object sender, EventArgs e) =>
        {
            _Controller.BeginInvoke(() =>
            {
                Fixer = new SessionFixer(this);
            });
        };
        foreach (ITabContext tab in Tabs)
        {
            tab.OnSessionReconnected(session);
        }
        if (_Analyzer != null)
        {
            lock (_Sync)
            {
                _Analyzer = new ShellCompletionAnalyzer(this, session);
            }
        }
        IsInactive = false;
    }

    public void AttachChannelToControl(TerminalEmulatorControl ctl, SshShellChannel chn, bool isAutoLaunchedCommand, Action tabKeptAfterExit = null)
    {
        Utf8StreamDecoder reader = new Utf8StreamDecoder();
        chn.DataReceived += (object sender, byte[] data, int offset, int length, int value) =>
        {
            if (IsChannelAttached(ctl, chn))
            {
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
                        CloseTabPageForControl(ctl, autoInvoked: true);
                    }
                });
            };
        }
        chn.PollingEnabled = true;
        chn.ResizePty(ctl.TerminalSize.Width, ctl.TerminalSize.Height);
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

    public void ExpandHomeDirIfNeeded(ref string dir)
    {
        if (!(dir == "~"))
        {
            return;
        }
        try
        {
            using SshShellChannel sshShellChannel = Session.OpenShellChannel("pwd");
            using StreamReader streamReader = new StreamReader(sshShellChannel.Output);
            dir = streamReader.ReadLine();
        }
        catch
        {
        }
    }

    public void DoUploadFile(string file, string remoteDir)
    {
        LastDownloadDir = RemotePathUtils.GetDirectoryName(file);
        string fileName = Path.GetFileName(file);
        string text = remoteDir.TrimEnd('/') + "/" + fileName;
        ScpTransferManager.TransferJob transferJob = TransferManager.EnqueueUpload(file, text);
        if (transferJob != null)
        {
            SCPController.AddTransfer(transferJob);
        }
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

    public void UploadFiles(string[] files)
    {
        if (IsDevice)
        {
            return;
        }
        string dir = _Controller.Settings.DefaultUploadDirectory;
        ExpandHomeDirIfNeeded(ref dir);
        _Controller.Settings.DefaultUploadDirectory = dir;
        string text = TryExtractCurrentDir();
        if (text == null)
        {
            text = _Controller.Settings.DefaultUploadDirectory;
        }
        foreach (string file in files)
        {
            DoUploadFile(file, text);
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

    internal RemoteOpenFileDialog CreateRemoteOpenFileDialog()
    {
        string text = TryExtractCurrentDir() ?? LastDownloadDir;
        RemoteOpenFileDialog remoteOpenFileDialog = new RemoteOpenFileDialog();
        remoteOpenFileDialog.InitialDirectory = text;
        remoteOpenFileDialog.CommandRequested += ExecuteAsyncCommand;
        return remoteOpenFileDialog;
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

    public void DoDownloadFile(string file, string localDir, bool openWhenDone)
    {
        LastDownloadDir = RemotePathUtils.GetDirectoryName(file);
        string fileName = Path.GetFileName(file);
        string text = Path.Combine(localDir, fileName);
        if (!File.Exists(text) || MessageBoxes.AskWarn(text + " already exists. Overwrite it?"))
        {
            ScpTransferManager.TransferJob transferJob = TransferManager.EnqueueDownload(file, text, null, openWhenDone);
            if (transferJob != null)
            {
                SCPController.AddTransfer(transferJob);
            }
        }
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
        Session?.Dispose();
        TransferManager?.Dispose();
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

    public string[] RunCommand(string command)
    {
        return Session.RunCommandChecked(command, requireSuccess: true)?.ToArray();
    }
}
