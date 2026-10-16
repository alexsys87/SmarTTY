using SmarTTY.Ssh;
using Microsoft.Win32;
using SmarTTY.Controls;
using SmarTTY.Forms;
using SmarTTY.IO;
using SmarTTY.Properties;
using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using SmarTTY.Utilities;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Threading;
using VisualTTY;
using Control = System.Windows.Forms.Control;
using KeyEventArgs = System.Windows.Forms.KeyEventArgs;
using MouseEventArgs = System.Windows.Forms.MouseEventArgs;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using Point = System.Drawing.Point;
using SaveFileDialog = System.Windows.Forms.SaveFileDialog;

namespace SmarTTY.GUI.Backend;

public class MainWindowController : INotifyPropertyChanged
{
    public class MenuCommandHandlersImpl : INotifyPropertyChanged
    {
        private MainWindowController _Controller;

        private bool _LogInProgress;

        public bool AutoCloseTabs
        {
            get
            {
                return _Controller.Settings.AutoCloseTabs;
            }
            set
            {
                _Controller.Settings.AutoCloseTabs = value;
                _Controller.Settings.Save();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("AutoCloseTabs"));
            }
        }

        public bool AutoWrapLines
        {
            get
            {
                return _Controller.Settings.AutoWrapLines;
            }
            set
            {
                _Controller.Settings.AutoWrapLines = value;
                _Controller.Settings.Save();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("AutoWrapLines"));
                foreach (SessionWrapper allSession in _Controller.AllSessions)
                {
                    foreach (NormalTabContext item in allSession.Tabs.OfType<NormalTabContext>())
                    {
                        if (item.Terminal != null)
                        {
                            item.Terminal.AutoWrap = value;
                        }
                    }
                }
            }
        }

        public bool MinimalisticMode
        {
            get
            {
                return _Controller.MinimalisticMode;
            }
            set
            {
                _Controller.MinimalisticMode = value;
                if (value && _Controller.Settings.ShowMinimalisticModeWarning)
                {
                    using MinimalisticModeNotificationForm minimalisticModeNotificationForm = new MinimalisticModeNotificationForm();
                    minimalisticModeNotificationForm.ShowModal();
                    _Controller.Settings.ShowMinimalisticModeWarning = !minimalisticModeNotificationForm.DontShowAgain;
                    _Controller.Settings.Save();
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("MinimalisticMode"));
            }
        }

        public bool FullScreenMode
        {
            get
            {
                return _Controller.FullScreenMode;
            }
            set
            {
                _Controller.FullScreenMode = value;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public MenuCommandHandlersImpl(MainWindowController mainWindowController)
        {
            _Controller = mainWindowController;
        }

        internal void SaveFile()
        {
            try
            {
                ((_Controller.ActiveSession?.ActiveTab as EditorTabContext) ?? throw new Exception("No active editor tab")).Editor.Save();
            }
            catch (Exception ex)
            {
                MessageBoxes.Exception(ex);
            }
        }

        internal void NewSession()
        {
            ConnectionSelectionWindow connectionSelectionWindow = new ConnectionSelectionWindow(showRawConnections: false, null);
            if (connectionSelectionWindow.ShowDialog() == true && connectionSelectionWindow.Controller.CreatedSession != null)
            {
                _Controller.AddSession(connectionSelectionWindow.Controller.CreatedSession, connectionSelectionWindow.Controller.SelectedSettings, createFirstPage: true);
            }
        }

        internal void CloseTab()
        {
            _Controller.CloseActivePage();
        }

        internal void ReplayLog()
        {
            try
            {
                TerminalEmulatorControl terminalEmulatorControl = (_Controller.ActiveSession?.ActiveTab as NormalTabContext)?.Terminal;
                if (terminalEmulatorControl == null)
                {
                    throw new Exception("No active terminal");
                }
                OpenFileDialog openFileDialog = new OpenFileDialog();
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    using (FileStream fileStream = File.OpenRead(openFileDialog.FileName))
                    {
                        byte[] array = new byte[fileStream.Length];
                        fileStream.Read(array, 0, array.Length);
                        new LogReplayForm(Encoding.UTF8.GetString(array), terminalEmulatorControl).Show();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBoxes.Exception(ex);
            }
        }

        internal void MakeLog()
        {
            try
            {
                TerminalEmulatorControl terminalEmulatorControl = (_Controller.ActiveSession?.ActiveTab as NormalTabContext)?.Terminal;
                if (terminalEmulatorControl == null)
                {
                    throw new Exception("No active terminal");
                }
                SaveFileDialog saveFileDialog = new SaveFileDialog();
                if (_LogInProgress)
                {
                    terminalEmulatorControl.OpenLogFile(null);
                }
                else
                {
                    if (saveFileDialog.ShowDialog() != DialogResult.OK)
                    {
                        return;
                    }
                    terminalEmulatorControl.OpenLogFile(saveFileDialog.FileName);
                }
                _LogInProgress = !_LogInProgress;
            }
            catch (Exception ex)
            {
                MessageBoxes.Exception(ex);
            }
        }

        internal void OpenFile(ITabContext tabWithCurrentDir = null)
        {
            try
            {
                SessionWrapper activeSession = _Controller.ActiveSession;
                if (activeSession == null)
                {
                    throw new Exception("No session is active");
                }
                tabWithCurrentDir = tabWithCurrentDir ?? activeSession.ActiveTab;
                RemoteOpenFileDialog remoteOpenFileDialog = activeSession.CreateRemoteOpenFileDialog();
                string text = activeSession.TryExtractCurrentDir(tabWithCurrentDir) ?? activeSession.LastDownloadDir;
                if ((tabWithCurrentDir as NormalTabContext)?.Terminal is SshTerminalControl sshTerminalControl)
                {
                    remoteOpenFileDialog.InitialDirectory = sshTerminalControl.CurrentDirectory;
                }
                else if (text == activeSession.CurrentDirDuringLastOpen && !string.IsNullOrEmpty(activeSession.LastUsedOpenDir))
                {
                    remoteOpenFileDialog.InitialDirectory = activeSession.LastUsedOpenDir;
                }
                else
                {
                    remoteOpenFileDialog.InitialDirectory = text;
                }
                string text2 = remoteOpenFileDialog.ShowDialog();
                if (text2 != null)
                {
                    activeSession.LastUsedOpenDir = RemotePathUtils.GetDirectoryName(text2);
                    activeSession.CurrentDirDuringLastOpen = text;
                    CreateNewEditorTab(activeSession, text2);
                }
            }
            catch (Exception ex)
            {
                MessageBoxes.Exception(ex);
            }
        }

        public void CreateNewEditorTab(SessionWrapper session, string fn, TextDocument.TextPosition initialLoc = default(TextDocument.TextPosition))
        {
            EditorTabContext editorTabContext = session.TryActivateEditorForFile(fn);
            if (editorTabContext != null)
            {
                try
                {
                    editorTabContext.Editor.SetCaret(initialLoc);
                    return;
                }
                catch
                {
                    return;
                }
            }
            RemoteFileEditor editor = new RemoteFileEditor(session, fn, _Controller.Settings, initialLoc, MessageBoxes.Exception)
            {
                NativeScrollSemantics = false
            };
            editor.CtrlTabPressed += _Controller.anyctl_CtrlTabPressed;
            editor.KeyUp += _Controller.anyctl_KeyUp;
            editor.KeyDown += editor_KeyDown;
            editor.MouseMove += _Controller.anyctl_MouseMove;
            ((EditableTextBrowser)editor).ModifiedChanged += editor_UnsavedChanged;
            editor.LoadFailed += (object obj2, EventArgs e10) =>
            {
                session.CloseFirstMatchingPage((ITabContext tabContext) => tabContext.EditorControl == editor, closeSessionIfEmpty: false);
            };
            _Controller.ApplyEditorSettings(editor);
            editor.Tag = session.AddNewFileEditorOrThrow(editor, fn);
        }

        internal void Search()
        {
            _Controller.ActiveSession?.ActiveTab?.Searcher?.Activate();
        }

        private void editor_UnsavedChanged(EditableTextBrowser editor, bool newValue)
        {
            if (editor.Tag is EditorTabContext { Title: var title } editorTabContext)
            {
                string text = title.TrimEnd(' ', '*');
                if (editor.IsModified)
                {
                    editorTabContext.Title = text + " *";
                }
                else
                {
                    editorTabContext.Title = text;
                }
            }
        }

        private void editor_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && !e.Alt && !e.Shift)
            {
                switch (e.KeyCode)
                {
                    case Keys.S:
                        SaveFile();
                        break;
                    case Keys.O:
                        OpenFile();
                        break;
                    case Keys.W:
                        CloseTab();
                        break;
                    case Keys.F:
                        Search();
                        break;
                }
            }
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.F3 && (sender as ReadonlyTextBrowser)?.Tag is EditorTabContext editorTabContext)
            {
                editorTabContext?.Searcher?.Search(e.Shift);
            }
        }

        internal void Copy()
        {
            _Controller.ActiveSession?.ActiveTab?.EditorControl?.CopySelection();
        }

        internal void Paste()
        {
            _Controller.ActiveSession?.ActiveTab?.EditorControl?.Paste();
        }

        internal void CopyAll()
        {
            _Controller.CopyAll();
        }

        internal void SCPTransfers()
        {
            _Controller.ActiveSession?.SCPController?.Show();
        }

        internal void UploadFile()
        {
            try
            {
                SessionWrapper activeSession = _Controller.ActiveSession;
                if (activeSession == null)
                {
                    throw new Exception("No session active");
                }
                SCPUploadForm scpUploadForm = new SCPUploadForm(activeSession.ExecuteAsyncCommand);
                scpUploadForm.RemoteDirectory = _Controller.Settings.DefaultUploadDirectory;
                if (scpUploadForm.ShowModal() == DialogResult.OK)
                {
                    _Controller.Settings.DefaultUploadDirectory = scpUploadForm.RemoteDirectory;
                    string dir = _Controller.Settings.DefaultUploadDirectory;
                    activeSession.ExpandHomeDirIfNeeded(ref dir);
                    _Controller.Settings.DefaultUploadDirectory = dir;
                    _Controller.Settings.Save();
                    activeSession.DoUploadFile(scpUploadForm.LocalFile, _Controller.Settings.DefaultUploadDirectory);
                }
            }
            catch (Exception ex)
            {
                MessageBoxes.Exception(ex);
            }
        }

        internal void Settings()
        {
            GlobalSettingsWindow globalSettingsWindow = new GlobalSettingsWindow(_Controller.Settings);
            globalSettingsWindow.Owner = (Window)_Controller._Window;
            if (globalSettingsWindow.ShowDialog() != true)
            {
                return;
            }
            _Controller.Settings.Save();
            Font font = null;
            try
            {
                font = _Controller.Settings.EffectiveFont;
            }
            catch
            {
            }
            foreach (SessionWrapper allSession in _Controller.AllSessions)
            {
                foreach (ITabContext tab in allSession.Tabs)
                {
                    if (tab is NormalTabContext { Terminal: var terminal })
                    {
                        terminal.Font = font;
                        _Controller.ApplyTerminalSettings(terminal);
                        terminal.UpdateLayout();
                        terminal.Redraw();
                    }
                    else if (tab is EditorTabContext editorTabContext)
                    {
                        _Controller.ApplyEditorSettings(editorTabContext.Editor);
                    }
                }
            }
            _Controller.ApplyGlobalVisualSettings();
        }

        internal void SetupPublicKey()
        {
            SessionWrapper activeSession = _Controller.ActiveSession;
            if (activeSession != null)
            {
                new PublicKeyManagerForm(activeSession.Session, activeSession.Settings).ShowModal();
            }
        }

        internal void About()
        {
            new AboutForm().ShowModal();
        }

        internal void DownloadDir()
        {
            try
            {
                DirectoryTransferForm directoryTransferForm = new DirectoryTransferForm((_Controller.ActiveSession ?? throw new Exception("No session active")).Session, flag: false);
                directoryTransferForm.LocalDirectory = _Controller.Settings.DefaultDownloadDirectory;
                directoryTransferForm.FormClosed += Frm_FormClosed;
                directoryTransferForm.Show();
            }
            catch (Exception ex)
            {
                MessageBoxes.Exception(ex);
            }
        }

        private void Frm_FormClosed(object sender, FormClosedEventArgs e)
        {
            DirectoryTransferForm directoryTransferForm = (DirectoryTransferForm)sender;
            if (directoryTransferForm.IsUpload)
            {
                _Controller.Settings.DefaultUploadDirectory = directoryTransferForm.RemoteDirectory;
            }
            else
            {
                _Controller.Settings.DefaultDownloadDirectory = directoryTransferForm.LocalDirectory;
            }
            _Controller.Settings.Save();
        }

        internal void UploadDir()
        {
            try
            {
                DirectoryTransferForm directoryTransferForm = new DirectoryTransferForm((_Controller.ActiveSession ?? throw new Exception("No session active")).Session, flag: true);
                directoryTransferForm.RemoteDirectory = _Controller.Settings.DefaultUploadDirectory;
                directoryTransferForm.FormClosed += Frm_FormClosed;
                directoryTransferForm.Show();
            }
            catch (Exception ex)
            {
                MessageBoxes.Exception(ex);
            }
        }

        public void QuickDownload(string fn = null)
        {
            try
            {
                SessionWrapper activeSession = _Controller.ActiveSession;
                if (activeSession == null)
                {
                    throw new Exception("No session active");
                }
                if (fn == null)
                {
                    fn = activeSession.CreateRemoteOpenFileDialog().ShowDialog();
                    if (fn == null)
                    {
                        return;
                    }
                }
                string text = _Controller.Settings.DefaultDownloadDirectory;
                if (string.IsNullOrEmpty(text) || !Directory.Exists(text))
                {
                    FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog
                    {
                        Description = "Select download directory"
                    };
                    if (folderBrowserDialog.ShowDialog() != DialogResult.OK)
                    {
                        return;
                    }
                    text = folderBrowserDialog.SelectedPath;
                }
                DoDownloadFile(fn, text, _Controller.Settings.OpenLocalDirAfterDownload);
            }
            catch (Exception ex)
            {
                MessageBoxes.Exception(ex);
            }
        }

        internal void DownloadFile()
        {
            try
            {
                SessionWrapper activeSession = _Controller.ActiveSession;
                if (activeSession == null)
                {
                    throw new Exception("No session active");
                }
                string text = activeSession.TryExtractCurrentDir() ?? activeSession.LastDownloadDir;
                SCPDownloadForm scpDownloadForm = new SCPDownloadForm(activeSession.ExecuteAsyncCommand, text, flag: true)
                {
                    OpenLocalDirAfterDownload = _Controller.Settings.OpenLocalDirAfterDownload
                };
                scpDownloadForm.LocalDirectory = _Controller.Settings.DefaultDownloadDirectory;
                if (scpDownloadForm.ShowModal() == DialogResult.OK)
                {
                    _Controller.Settings.DefaultDownloadDirectory = scpDownloadForm.LocalDirectory;
                    _Controller.Settings.Save();
                    DoDownloadFile(scpDownloadForm.RemoteFile, scpDownloadForm.LocalDirectory, scpDownloadForm.OpenLocalDirAfterDownload);
                }
            }
            catch (Exception ex)
            {
                MessageBoxes.Exception(ex);
            }
        }

        private void DoDownloadFile(string file, string localDir, bool openWhenDone)
        {
            _Controller.ActiveSession?.DoDownloadFile(file, localDir, openWhenDone);
        }
    }

    public class ToolStripImpl : INotifyPropertyChanged, IDisposable
    {
        private string _Status1;

        private string _Status2;

        private Visibility _ProgressVisibility;

        private double _Progress;

        private readonly MainWindowController _Controller;

        private readonly DispatcherTimer _Timer;

        private Visibility _StatusBarVisibility;

        public string Status1
        {
            get
            {
                return _Status1;
            }
            set
            {
                _Status1 = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Status1"));
            }
        }

        public string Status2
        {
            get
            {
                return _Status2;
            }
            set
            {
                _Status2 = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Status2"));
            }
        }

        public Visibility ProgressVisibility
        {
            get
            {
                return _ProgressVisibility;
            }
            set
            {
                _ProgressVisibility = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ProgressVisibility"));
            }
        }

        public Visibility StatusBarVisibility
        {
            get
            {
                return _StatusBarVisibility;
            }
            set
            {
                _StatusBarVisibility = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("StatusBarVisibility"));
            }
        }

        public double Progress
        {
            get
            {
                return _Progress;
            }
            set
            {
                _Progress = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Progress"));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ToolStripImpl(MainWindowController controller, Dispatcher dispatcher)
        {
            _Controller = controller;
            _Timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250.0),
                IsEnabled = true
            };
            _Timer.Tick += (object obj, EventArgs e10) =>
            {
                RefreshAndSendKeepalives();
            };
        }

        public void RefreshAndSendKeepalives()
        {
            try
            {
                _Controller._Window.UpdateContextMenu();
                SessionWrapper activeSession = _Controller.ActiveSession;
                if (activeSession == null)
                {
                    Status1 = "No active session";
                    Status2 = null;
                    ProgressVisibility = Visibility.Collapsed;
                    return;
                }
                SCPController.SCPStatistics valueOrDefault = (_Controller.ActiveSession?.SCPController?.SendUpdateEventsAndGetStatistics()).GetValueOrDefault();
                if (valueOrDefault.TotalActiveOrQueuedTransfers == 0)
                {
                    Status1 = "SCP: No transfers";
                    ProgressVisibility = Visibility.Collapsed;
                }
                else
                {
                    ProgressVisibility = Visibility.Visible;
                    if (activeSession.TransferManager.IsPaused)
                    {
                        Status1 = string.Format("SCP: paused ({1}/{2})", valueOrDefault.TotalTransfers, StringUtils.FormatSize(valueOrDefault.TotalDone), StringUtils.FormatSize(valueOrDefault.TotalSize));
                    }
                    else
                    {
                        Status1 = $"SCP: {valueOrDefault.TotalTransfers} transfers ({StringUtils.FormatSize(valueOrDefault.TotalDone)}/{StringUtils.FormatSize(valueOrDefault.TotalSize)})";
                        if (valueOrDefault.TotalSize == 0L)
                        {
                            valueOrDefault.TotalSize = 1L;
                        }
                        if (valueOrDefault.TotalDone > valueOrDefault.TotalSize)
                        {
                            valueOrDefault.TotalDone = 0L;
                        }
                    }
                    Progress = (double)valueOrDefault.TotalDone * 1.0 / (double)valueOrDefault.TotalSize;
                }
                SshSession.SessionStats sessionStats = activeSession.Session.Statistics;
                Status2 = $"{StringUtils.FormatSize(sessionStats.BytesSent)}B sent, {StringUtils.FormatSize(sessionStats.BytesReceived)}B received";
            }
            catch (Exception ex)
            {
                Status1 = ex.Message;
                Status2 = null;
                ProgressVisibility = Visibility.Collapsed;
            }
        }

        public void Dispose()
        {
            _Timer.IsEnabled = false;
        }
    }

    private TerminalEventDispatcher _Worker = new TerminalEventDispatcher();

    private readonly MainWindow _Window;

    public readonly Dispatcher Dispatcher;

    private ContextMenuStrip _TerminalContextMenu;

    private Dock _SessionHeaderLocation;

    private Dock _TabHeaderLocation;

    private bool _HideHeadersAnyway;

    private int _ActiveSessionIndex;

    private Popup _VirtualTerminalPopup;

    private bool _HideTabHeaders;

    private Visibility _MainMenuVisibility;

    private bool _FullScreenMode;

    private Rect _PreFullScreenRect;

    private bool _MinimalisticMode;

    private bool _SuppressTerminalResizeEvents;

    private ITabContext _ActiveTab;

    public GlobalSettings Settings { get; private set; }

    public MenuCommandHandlersImpl MenuCommandHandlers { get; }

    public ToolStripImpl ToolStrip { get; }

    public Dock SessionHeaderLocation
    {
        get
        {
            return _SessionHeaderLocation;
        }
        set
        {
            _SessionHeaderLocation = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SessionHeaderLocation"));
        }
    }

    public Dock TabHeaderLocation
    {
        get
        {
            return _TabHeaderLocation;
        }
        set
        {
            _TabHeaderLocation = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TabHeaderLocation"));
        }
    }

    public bool HideHeaders
    {
        get
        {
            if (AllSessions.Count >= 3)
            {
                return _HideHeadersAnyway;
            }
            return true;
        }
    }

    public bool HideHeadersAnyway
    {
        set
        {
            _HideHeadersAnyway = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HideHeaders"));
        }
    }

    public int ActiveSessionIndex
    {
        get
        {
            return _ActiveSessionIndex;
        }
        set
        {
            int oldValue = Math.Max(Math.Min(_ActiveSessionIndex, FirstPseudoPage - 1), 0);
            _ActiveSessionIndex = value;
            SessionWrapper activeSession = ActiveSession;
            if (activeSession != null && activeSession.Session == null)
            {
                Dispatcher.BeginInvoke((Delegate)(Action)(() =>
                {
                    ActiveSessionIndex = oldValue;
                }), DispatcherPriority.ContextIdle, new object[0]);
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ActiveSessionIndex"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ActiveSession"));
            OnActiveTabChanged(ActiveSession, ActiveSession?.ActiveTab);
            ActiveSession?.SCPController?.SendUpdateEventsAndGetStatistics();
            ToolStrip?.RefreshAndSendKeepalives();
        }
    }

    public SessionWrapper ActiveSession
    {
        get
        {
            if (_ActiveSessionIndex < 0 || _ActiveSessionIndex >= AllSessions.Count)
            {
                return null;
            }
            return AllSessions[_ActiveSessionIndex];
        }
    }

    public ObservableCollection<SessionWrapper> AllSessions { get; } = new ObservableCollection<SessionWrapper>();

    private int FirstPseudoPage => Enumerable.Range(0, AllSessions.Count).FirstOrDefault((int index) => AllSessions[index].Session == null);

    public bool HideTabHeaders
    {
        get
        {
            return _HideTabHeaders;
        }
        set
        {
            if (_HideTabHeaders != value)
            {
                _HideTabHeaders = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HideTabHeaders"));
                ActiveSession?.RaiseHideHeadersChanged();
            }
        }
    }

    public Visibility MainMenuVisibility
    {
        get
        {
            return _MainMenuVisibility;
        }
        set
        {
            _MainMenuVisibility = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("MainMenuVisibility"));
        }
    }

    public ConnectionSelectionWindow.ControllerImpl NewConnectionController { get; private set; }

    public Visibility DebugVisibility => Visibility.Collapsed;

    public string RemoteFileSaveHeader
    {
        get
        {
            if (!(ActiveSession?.ActiveTab is EditorTabContext editorTabContext))
            {
                return null;
            }
            return "Save " + RemotePathUtils.GetFileName(editorTabContext.FilePath);
        }
    }

    public Visibility SaveCommandVisibility
    {
        get
        {
            if (string.IsNullOrEmpty(RemoteFileSaveHeader))
            {
                return Visibility.Collapsed;
            }
            return Visibility.Visible;
        }
    }

    public Rect Bounds
    {
        get
        {
            return new Rect(_Window.Left, _Window.Top, _Window.Width, _Window.Height);
        }
        set
        {
            _Window.Left = value.Left;
            _Window.Top = value.Top;
            _Window.Width = value.Width;
            _Window.Height = value.Height;
        }
    }

    public bool FullScreenMode
    {
        get
        {
            return _FullScreenMode;
        }
        set
        {
            _FullScreenMode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FullScreenMode"));
            if (value)
            {
                _PreFullScreenRect = Bounds;
                Rectangle bounds = Screen.FromPoint(new Point((int)_Window.Left, (int)_Window.Top)).Bounds;
                _Window.WindowState = WindowState.Normal;
                _Window.WindowStyle = WindowStyle.None;
                Bounds = new Rect((double)bounds.Left, (double)bounds.Top, (double)bounds.Width, (double)bounds.Height);
            }
            else
            {
                _Window.WindowStyle = WindowStyle.SingleBorderWindow;
                if (_PreFullScreenRect.Width > 0.0 && _PreFullScreenRect.Height > 0.0)
                {
                    Bounds = _PreFullScreenRect;
                }
            }
            UpdateResizeMode();
        }
    }

    public bool MinimalisticMode
    {
        get
        {
            return _MinimalisticMode;
        }
        set
        {
            _MinimalisticMode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("MinimalisticMode"));
            ToolStrip.StatusBarVisibility = BoolToVisibility(!value);
            UpdateResizeMode();
        }
    }

    public bool SuppressTerminalResizeEvents
    {
        get
        {
            return _SuppressTerminalResizeEvents;
        }
        set
        {
            _SuppressTerminalResizeEvents = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SuppressTerminalResizeEvents"));
            TerminalEmulatorControl terminalEmulatorControl = (ActiveSession?.ActiveTab as NormalTabContext)?.Terminal;
            if (terminalEmulatorControl != null)
            {
                terminalEmulatorControl.LayoutSuspended = value;
            }
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public event Action OnLastSessionClosed;

    public event Action CloseNewConnectionPopup;

    public void HandleShortcut(VirtualTerminalPopupSummary.FileOrDirectoryInfo fdi)
    {
        try
        {
            if (fdi.IsDirectory)
            {
                ActiveSession?.CreateNewTerminalPage(virtualMode: true, default, fdi.FullPath);
            }
            else
            {
                MenuCommandHandlers.CreateNewEditorTab(ActiveSession, fdi.FullPath);
            }
        }
        catch (Exception ex)
        {
            MessageBoxes.Exception(ex);
        }
    }

    public void ExportPreferences(CommonGUIPreferences prefs)
    {
        OpenSettingsKey();
        prefs.FullScreen = FullScreenMode;
        prefs.Minimalistic = MinimalisticMode;
    }

    public static RegistryKey OpenSettingsKey()
    {
        return Registry.CurrentUser.CreateSubKey("SOFTWARE\\Sysprogs\\SmarTTY\\Settings");
    }

    private void UpdateResizeMode()
    {
        if (FullScreenMode)
        {
            _Window.ResizeMode = ResizeMode.NoResize;
        }
        else
        {
            _Window.ResizeMode = (MenuCommandHandlers.MinimalisticMode ? ResizeMode.CanResize : ResizeMode.CanResizeWithGrip);
        }
    }

    private void ApplyEditorSettings(RemoteFileEditor edit)
    {
        GlobalSettings.EditorColors effectiveColors = Settings.Editor.GetEffectiveColors(Settings.Theme);
        edit.BackColor = effectiveColors.BackColor;
        edit.ForeColor = effectiveColors.TextColor;
        edit.Brush = new SolidBrush(effectiveColors.LineNumberBackground);
        edit.Brush2 = new SolidBrush(effectiveColors.LineNumberForeground);
        edit.TabSize = Settings.Editor.TabSize;
        edit.HideMargins = !Settings.Editor.ShowLineNumbers;
        edit.TabSize = Settings.Editor.TabSize;
    }

    public MainWindowController(MainWindow wnd)
    {
        _Window = wnd;
        _VirtualTerminalPopup = new Popup
        {
            Child = (UIElement)new Border
            {
                Child = (UIElement)new VirtualTerminalPopup(this)
            },
            StaysOpen = true,
            Focusable = false
        };
        Settings = GlobalSettings.Load() ?? new GlobalSettings();
        MenuCommandHandlers = new MenuCommandHandlersImpl(this);
        ToolStrip = new ToolStripImpl(this, ((DispatcherObject)wnd).Dispatcher);
        Dispatcher = ((DispatcherObject)wnd).Dispatcher;
        _TerminalContextMenu = new ContextMenuStrip();
        _TerminalContextMenu.Items.Add("Copy", Resources.CopyHS, (object obj, EventArgs e10) =>
        {
            ActiveSession?.ActiveTab?.EditorControl?.CopySelection();
        });
        _TerminalContextMenu.Items.Add("Paste", Resources.PasteHS, (object obj, EventArgs e10) =>
        {
            ActiveSession?.ActiveTab?.EditorControl?.Paste();
        });
        _TerminalContextMenu.Items.Add("Copy All", null, (object obj, EventArgs e10) =>
        {
            CopyAll();
        });
        _TerminalContextMenu.Items.Add("New Tab", Resources.add16, (object obj, EventArgs e10) =>
        {
            ActiveSession?.CreateNewTerminalPage();
        });
        _TerminalContextMenu.Items.Add("Close Tab", Resources.delete16, (object obj, EventArgs e10) =>
        {
            CloseActivePage();
        });
        AllSessions.Add(new SessionWrapper(this, null, null));
        CommonGUIPreferences commonGUIPreferences = GuiPreferencesStore.Current;
        if (commonGUIPreferences != null)
        {
            MinimalisticMode = commonGUIPreferences.Minimalistic;
            FullScreenMode = commonGUIPreferences.FullScreen;
        }
        ApplyGlobalVisualSettings();
    }

    private static Dock HeaderOrientationToDock(HeaderOrientation orient)
    {
        return orient switch
        {
            HeaderOrientation.Top => Dock.Top,
            HeaderOrientation.Left => Dock.Left,
            HeaderOrientation.Right => Dock.Right,
            HeaderOrientation.Bottom => Dock.Bottom,
            _ => Dock.Bottom,
        };
    }

    private void ApplyGlobalVisualSettings()
    {
        TabHeaderLocation = HeaderOrientationToDock(Settings.TabHeaderOrientation);
        SessionHeaderLocation = HeaderOrientationToDock(Settings.SessionHeaderOrientation);
        MainWindow.ApplyTheme((Window)_Window, Settings.Theme, ref _Window._ThemeDictionary);
    }

    private void CloseActivePage()
    {
        SessionWrapper activeSession = ActiveSession;
        if (activeSession != null)
        {
            (activeSession.ActiveTab as ICloseableElement)?.Close();
        }
    }

    private void CopyAll()
    {
        (ActiveSession?.ActiveTab?.EditorControl)?.CopyAll();
    }

    internal TcpClient CreateX11Connection()
    {
        return XServerLaunchWindow.CreateX11ConnectionFromArbitraryThread(Dispatcher, Settings, (Window)_Window);
    }

    internal SessionWrapper AddSession(SshSession session, SSHConnectionSettings connectionSettings, bool createFirstPage)
    {
        int firstPseudoPage = FirstPseudoPage;
        SessionWrapper sessionWrapper = new SessionWrapper(this, session, connectionSettings);
        if (createFirstPage)
        {
            sessionWrapper.CreateNewTerminalPage();
        }
        sessionWrapper.ShowNewSessionTab = AllSessions.Count == 1;
        AllSessions.Insert(firstPseudoPage, sessionWrapper);
        AllSessions.CollectionChanged += AllSessions_CollectionChanged;
        ActiveSessionIndex = firstPseudoPage;
        return sessionWrapper;
    }

    private void AllSessions_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HideHeaders"));
        if (AllSessions.Count == 2 && AllSessions[0].Session != null)
        {
            AllSessions[0].ShowNewSessionTab = true;
        }
        else if (AllSessions.Count > 2 && AllSessions[0].Session != null)
        {
            AllSessions[0].ShowNewSessionTab = false;
        }
    }

    public void BeginInvoke(Action a)
    {
        Dispatcher.BeginInvoke((Delegate)a, new object[0]);
    }

    public TerminalEmulatorControl CreateNewTerminal(SessionWrapper session, InitialCommand initialCommand, bool virtualMode, string initialDirForVirtualMode = null)
    {
        TerminalEmulatorControl terminalEmulatorControl;
        if (virtualMode)
        {
            SshTerminalControl vt = new SshTerminalControl(ActiveSession, initialDirForVirtualMode)
            {
                Tag = new TerminalTag
                {
                    Session = session
                }
            };
            vt.EndOfInputRequested += (object obj2, EventArgs e10) =>
            {
                session.CloseTabPageForControl(vt);
            };
            vt.CompletionPopupRequested += MainWindowController_VirtualTerminalPopupRequested;
            vt.OpenFileRequested += Vt_FileOpenRequested;
            terminalEmulatorControl = vt;
        }
        else
        {
            terminalEmulatorControl = new TerminalEmulatorControl();
            SshShellChannel sshShellChannel = session.Session.OpenShellChannel(initialCommand.Command, "xterm");
            sshShellChannel.ExtendedDataMode = ExtendedDataMode.Merge;
            terminalEmulatorControl.DataEntered += terminalEmulatorControl1_HandleTerminalInput;
            terminalEmulatorControl.TerminalSizeChanged += terminalEmulatorControl1_TerminalSizeChanged;
            terminalEmulatorControl.Tag = new TerminalTag
            {
                Channel = sshShellChannel,
                Session = session
            };
            session.AttachChannelToControl(terminalEmulatorControl, sshShellChannel, initialCommand.Command != null);
        }
        terminalEmulatorControl.CaretWhenUnfocused = false;
        terminalEmulatorControl.MouseMove += anyctl_MouseMove;
        terminalEmulatorControl.Dispatcher = _Worker;
        terminalEmulatorControl.ContextMenuStrip = _TerminalContextMenu;
        terminalEmulatorControl.AppSettings = Settings;
        terminalEmulatorControl.KeyDown += ctl_KeyDown;
        terminalEmulatorControl.CtrlTabPressed += anyctl_CtrlTabPressed;
        terminalEmulatorControl.KeyUp += anyctl_KeyUp;
        if (initialCommand.Echo && !string.IsNullOrEmpty(initialCommand.Command))
        {
            terminalEmulatorControl.ProcessOutput(initialCommand.Command + "\r\n");
        }
        terminalEmulatorControl.AutoWrap = Settings.AutoWrapLines;
        ApplyTerminalSettings(terminalEmulatorControl);
        try
        {
            if (Settings.FontName != null && Settings.FontSize != 0f)
            {
                terminalEmulatorControl.Font = new Font(Settings.FontName, Settings.FontSize);
            }
        }
        catch
        {
        }
        return terminalEmulatorControl;
    }

    private void Vt_FileOpenRequested(object sender, SshTerminalControl.OpenFileEventArgs e)
    {
        try
        {
            MenuCommandHandlers.CreateNewEditorTab(ActiveSession, e.Path, new TextDocument.TextPosition(e.Column.GetValueOrDefault(), e.Line.GetValueOrDefault()));
        }
        catch (Exception ex)
        {
            MessageBoxes.Exception(ex);
        }
    }

    private void MainWindowController_VirtualTerminalPopupRequested(object sender, CompletionPopupRequest e)
    {
        _VirtualTerminalPopup.Placement = PlacementMode.Custom;
        _VirtualTerminalPopup.PlacementTarget = (UIElement)_Window;
        SuggestionPopupController suggestionPopupController = new SuggestionPopupController(this, e, _VirtualTerminalPopup);
        ((FrameworkElement)_VirtualTerminalPopup).DataContext = suggestionPopupController;
        _VirtualTerminalPopup.CustomPopupPlacementCallback = suggestionPopupController.PlacePopup;
        _VirtualTerminalPopup.IsOpen = true;
        suggestionPopupController.ItemSelected += (CompletionItem ba2, Keys isEnter) =>
        {
            _VirtualTerminalPopup.IsOpen = false;
            FocusMainControlDelayed(null);
        };
        e.Popup = suggestionPopupController;
    }

    private void ApplyTerminalSettings(TerminalEmulatorControl trm)
    {
        trm.ShowUnrecognizedAsHex = Settings.ShowUnrecognizedCharactersAsHex;
        trm.MaxChunkLength = Settings.LargeBlockCutoffPoint;
        trm.BellMode = Settings.BeepMode;
        trm.BellSoundFile = Settings.BeepFileName;
        ((TerminalEmulatorBase)trm).CopyOnSelect = Settings.CopyOnSelection;
        if (Settings.DefaultTextColor.Alpha != 0)
        {
            trm.ForeColor = Settings.DefaultTextColor;
        }
        if (Settings.PaletteOverride == null)
        {
            trm.SetPalette(ConsolePalette.PuTTY);
        }
        else
        {
            trm.SetPalette(Settings.PaletteOverride);
        }
    }

    public void CloseSession(SessionWrapper session)
    {
        AllSessions.Remove(session);
        if (AllSessions.Count((SessionWrapper sessionWrapper) => sessionWrapper.Session != null) == 0)
        {
            OnLastSessionClosed?.Invoke();
        }
        else
        {
            Dispatcher.BeginInvoke((Delegate)(Action)(() =>
            {
                int firstPseudoPage = FirstPseudoPage;
                if (ActiveSessionIndex >= firstPseudoPage || ActiveSessionIndex < 0)
                {
                    ActiveSessionIndex = Math.Max(0, firstPseudoPage - 1);
                }
            }), DispatcherPriority.ContextIdle, new object[0]);
        }
        session.Dispose();
    }

    private void anyctl_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.ControlKey)
        {
            ActiveSession?.OnCtrlReleased();
        }
    }

    private void anyctl_CtrlTabPressed(ReadonlyTextBrowser browser, bool bShift)
    {
        ActiveSession?.OnCtrlTabPressed(bShift);
    }

    private void ctl_KeyDown(object sender, KeyEventArgs e)
    {
        TerminalEmulatorControl terminalEmulatorControl = sender as TerminalEmulatorControl;
        if (Settings.UseCtrlShortcuts)
        {
            if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.W)
            {
                ActiveSession?.CloseTabPageForControl(terminalEmulatorControl);
            }
            else if (e.Control && !e.Alt && e.Shift && (e.KeyCode == Keys.T || e.KeyCode == Keys.N))
            {
                ActiveSession?.CreateNewTerminalPage();
            }
        }
        if (Settings.InterceptCtrlClipboardShortcuts)
        {
            SshTerminalControl obj = terminalEmulatorControl as SshTerminalControl;
            if ((obj == null || !((TextEditorControl)obj).CanEdit) && e.Control && !e.Alt && !e.Shift)
            {
                switch (e.KeyCode)
                {
                    case Keys.C:
                        terminalEmulatorControl.CopySelection();
                        e.SuppressKeyPress = true;
                        break;
                    case Keys.X:
                        terminalEmulatorControl.Cut();
                        e.SuppressKeyPress = true;
                        break;
                    case Keys.V:
                        terminalEmulatorControl.Paste();
                        e.SuppressKeyPress = true;
                        break;
                }
            }
        }
        if (!e.Control && e.Alt && !e.Shift && e.KeyCode == Keys.Return)
        {
            FullScreenMode = !FullScreenMode;
        }
    }

    private void terminalEmulatorControl1_TerminalSizeChanged(TerminalEmulatorControl sender, int newWidth, int newHeight)
    {
        (sender.Tag as TerminalTag).Channel.ResizePty(newWidth, newHeight);
    }

    private void terminalEmulatorControl1_HandleTerminalInput(object sender, byte[] input, TerminalEmulatorControl.InputKind type)
    {
        ((TerminalTag)(sender as TerminalEmulatorControl).Tag).Channel.Input.Write(input, 0, input.Length);
    }

    private void anyctl_MouseMove(object sender, MouseEventArgs e)
    {
        ScreenEdgeUtils.EdgeFlags edgeFlags = ScreenEdgeUtils.GetNearEdges(sender as Control, e.Location, 30);
        if (sender is TerminalEmulatorControl terminalEmulatorControl)
        {
            switch (Settings.VerticalScrollbarMode)
            {
                case GlobalSettings.ScrollbarMode.Show:
                    terminalEmulatorControl.VerticalScrollBarVisible = true;
                    break;
                case GlobalSettings.ScrollbarMode.Hide:
                    terminalEmulatorControl.VerticalScrollBarVisible = false;
                    break;
                case GlobalSettings.ScrollbarMode.Auto:
                    terminalEmulatorControl.VerticalScrollBarVisible = (edgeFlags & ScreenEdgeUtils.EdgeFlags.Right) != 0;
                    break;
            }
            switch (Settings.HorizontalScrollbarMode)
            {
                case GlobalSettings.ScrollbarMode.Show:
                    terminalEmulatorControl.HorizontalScrollBarVisible = true;
                    break;
                case GlobalSettings.ScrollbarMode.Hide:
                    terminalEmulatorControl.HorizontalScrollBarVisible = false;
                    break;
                case GlobalSettings.ScrollbarMode.Auto:
                    terminalEmulatorControl.HorizontalScrollBarVisible = (edgeFlags & ScreenEdgeUtils.EdgeFlags.Bottom) != 0;
                    break;
            }
        }
        if (MinimalisticMode)
        {
            MainMenuVisibility = BoolToVisibility((edgeFlags & ScreenEdgeUtils.EdgeFlags.Top) != 0);
            HideTabHeaders = !IsNearRelatedEdge(Settings.TabHeaderOrientation, edgeFlags);
            HideHeadersAnyway = !IsNearRelatedEdge(Settings.SessionHeaderOrientation, edgeFlags);
        }
        else
        {
            MainMenuVisibility = BoolToVisibility(v: true);
            HideTabHeaders = false;
            HideHeadersAnyway = false;
        }
    }

    private static Visibility BoolToVisibility(bool v)
    {
        if (v)
        {
            return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    private bool IsNearRelatedEdge(HeaderOrientation orientation, ScreenEdgeUtils.EdgeFlags edges)
    {
        return orientation switch
        {
            HeaderOrientation.Top => (edges & ScreenEdgeUtils.EdgeFlags.Top) != 0,
            HeaderOrientation.Left => (edges & ScreenEdgeUtils.EdgeFlags.Left) != 0,
            HeaderOrientation.Right => (edges & ScreenEdgeUtils.EdgeFlags.Right) != 0,
            HeaderOrientation.Bottom => (edges & ScreenEdgeUtils.EdgeFlags.Bottom) != 0,
            _ => false,
        };
    }

    public void LoadConnectionList()
    {
        if (NewConnectionController == null)
        {
            CommonGUIPreferences commonGUIPreferences = GuiPreferencesStore.Current;
            NewConnectionController = new ConnectionSelectionWindow.ControllerImpl(Dispatcher, showRawConnections: false, null, new CommonGUIPreferences
            {
                ProbeConnections = commonGUIPreferences.ProbeConnections,
                SortMode = commonGUIPreferences.SortMode,
                ShownConnections = new ShownConnectionTypes
                {
                    SSH = true
                }
            }, suppressUpdateThread: true);
            NewConnectionController.SessionCreated += NewConnectionController_SessionCreated;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("NewConnectionController"));
        }
    }

    private void NewConnectionController_SessionCreated()
    {
        BeginInvoke(() =>
        {
            AddSession(NewConnectionController.CreatedSession, NewConnectionController.SelectedSettings, createFirstPage: true);
            CloseNewConnectionPopup?.Invoke();
        });
    }

    public void OnActiveTabChanged(SessionWrapper session, ITabContext activeTab)
    {
        if (session == ActiveSession && _ActiveTab != activeTab)
        {
            TerminalEmulatorControl terminalEmulatorControl = (_ActiveTab as NormalTabContext)?.Terminal;
            TerminalEmulatorControl terminalEmulatorControl2 = (activeTab as NormalTabContext)?.Terminal;
            _ActiveTab = activeTab;
            if (terminalEmulatorControl != null)
            {
                terminalEmulatorControl.CaretWhenUnfocused = false;
                terminalEmulatorControl.LayoutSuspended = false;
                terminalEmulatorControl.LostFocus -= ActiveTerminal_LostFocus;
            }
            if (terminalEmulatorControl2 != null)
            {
                terminalEmulatorControl2.CaretWhenUnfocused = true;
                terminalEmulatorControl2.VerticalScrollBarVisible = Settings.VerticalScrollbarMode == GlobalSettings.ScrollbarMode.Show;
                terminalEmulatorControl2.HorizontalScrollBarVisible = Settings.HorizontalScrollbarMode == GlobalSettings.ScrollbarMode.Show;
                terminalEmulatorControl2.LayoutSuspended = SuppressTerminalResizeEvents;
                terminalEmulatorControl2.LostFocus += ActiveTerminal_LostFocus;
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("RemoteFileSaveHeader"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SaveCommandVisibility"));
            _VirtualTerminalPopup.IsOpen = false;
            FocusMainControlDelayed(activeTab);
        }
    }

    private void FocusMainControlDelayed(ITabContext activeTab)
    {
        if (activeTab == null)
        {
            activeTab = ActiveSession?.ActiveTab;
        }
        if (activeTab == null)
        {
            return;
        }
        Dispatcher.BeginInvoke((Delegate)(Action)(() =>
        {
            if (activeTab?.Control != null)
            {
                FocusManager.GetFocusedElement((DependencyObject)_Window);
                FocusManager.SetFocusedElement((DependencyObject)_Window, (IInputElement)activeTab.Control);
            }
            activeTab?.FocusMainControl();
            try
            {
                (activeTab.EditorControl as SshTerminalControl)?.Start();
            }
            catch
            {
            }
        }), DispatcherPriority.ContextIdle, new object[0]);
    }

    private void ActiveTerminal_LostFocus(object sender, EventArgs e)
    {
        _VirtualTerminalPopup.IsOpen = false;
    }

    public void Dispose()
    {
        SessionWrapper[] array = AllSessions.ToArray();
        foreach (SessionWrapper sessionWrapper in array)
        {
            if (sessionWrapper.Session != null)
            {
                sessionWrapper.Dispose();
            }
        }
        _Worker.Dispose();
        NewConnectionController?.Dispose();
        ToolStrip?.Dispose();
    }

    public System.Windows.Point PointFromScreen(System.Windows.Point point)
    {
        return _Window.PointFromScreen(point);
    }
}
