using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.Forms;
using SmarTTY.IO;
using SmarTTY.Terminal;
using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using VisualTTY;

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

    public ObservableCollection<ITabContext> Tabs { get; } = new ObservableCollection<ITabContext>();

    public string Title => Settings?.HostName;

    public string TitleWithStatus
    {
        get
        {
            string text = Title;
            if (IsInactive)
            {
                text += " [inactive]";
            }
            return text;
        }
    }

    public IconType Icon
    {
        get
        {
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

    public string TitleWithUserName => Settings?.UserName + "@" + Settings?.HostName;

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

    public void AttachChannelToControl(TerminalEmulatorControl ctl, SshShellChannel chn, bool isAutoLaunchedCommand)
    {
        Utf8StreamDecoder reader = new Utf8StreamDecoder();
        chn.DataReceived += (object sender, byte[] data, int offset, int length, int value) =>
        {
            AppendText(ctl, reader.Decode(data, offset, length), data, offset, length);
        };
        if (isAutoLaunchedCommand)
        {
            chn.Finished += (object _) =>
            {
                _Controller.BeginInvoke(() =>
                {
                    if (MessageBoxes.Ask("The command has exited. Close tab?"))
                    {
                        CloseTabPageForControl(ctl);
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
                    CloseTabPageForControl(ctl, autoInvoked: true);
                });
            };
        }
        chn.PollingEnabled = true;
        chn.ResizePty(ctl.TerminalSize.Width, ctl.TerminalSize.Height);
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
        if (FirstPseudoPage == 0)
        {
            _Controller.CloseSession(this);
        }
    }

    public void Dispose()
    {
        Session?.Dispose();
        TransferManager?.Dispose();
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
