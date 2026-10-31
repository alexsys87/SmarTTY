using SmarTTY.Ssh;
using SmarTTY.CodeEditing;
using SmarTTY.Completion;
using SmarTTY.Controls;
using SmarTTY.Forms;
using SmarTTY.GUI.Styles;
using SmarTTY.IO;
using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using SmarTTY.Utilities;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
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
using VisualTTY.DeviceTerminal;
using Control = System.Windows.Forms.Control;
using KeyEventArgs = System.Windows.Forms.KeyEventArgs;
using MouseEventArgs = System.Windows.Forms.MouseEventArgs;
using Point = System.Drawing.Point;

namespace SmarTTY.GUI.Backend;

public class MainWindowController : INotifyPropertyChanged
{
    public class MenuCommandHandlersImpl : INotifyPropertyChanged
    {
        private MainWindowController _Controller;

        private bool _LogInProgress;

        private FileExplorer _FileListExplorer;

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
                    MinimalisticModeWindow minimalisticModeWindow = new MinimalisticModeWindow();
                    WindowOwnership.Attach(minimalisticModeWindow, _Controller._Window);
                    minimalisticModeWindow.ShowDialog();
                    _Controller.Settings.ShowMinimalisticModeWarning = !minimalisticModeWindow.DontShowAgain;
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

        public bool FileListVisible
        {
            get
            {
                return _Controller.ActiveSession?.ActiveTab?.FileExplorer?.IsVisible
                    ?? _Controller.Settings.VirtualTerminal.ShowFileExplorer;
            }
            set
            {
                VirtualTerminalSettings virtualTerminalSettings = _Controller.Settings.VirtualTerminal;
                if (virtualTerminalSettings.ShowFileExplorer != value)
                {
                    virtualTerminalSettings.ShowFileExplorer = value;
                    _Controller.Settings.Save();
                }
                foreach (SessionWrapper allSession in _Controller.AllSessions)
                {
                    foreach (ITabContext tab in allSession.Tabs)
                    {
                        FileExplorer fileExplorer = tab.FileExplorer;
                        if (fileExplorer != null && fileExplorer.IsVisible != value)
                        {
                            fileExplorer.IsVisible = value;
                        }
                    }
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FileListVisible"));
            }
        }

        internal void OnActiveTabChanged(ITabContext activeTab)
        {
            FileExplorer fileExplorer = activeTab?.FileExplorer;
            if (_FileListExplorer != fileExplorer)
            {
                if (_FileListExplorer != null)
                {
                    _FileListExplorer.PropertyChanged -= FileListExplorer_PropertyChanged;
                }
                _FileListExplorer = fileExplorer;
                if (_FileListExplorer != null)
                {
                    _FileListExplorer.PropertyChanged += FileListExplorer_PropertyChanged;
                }
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FileListVisible"));
        }

        private void FileListExplorer_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == "IsVisible")
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FileListVisible"));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public MenuCommandHandlersImpl(MainWindowController mainWindowController)
        {
            _Controller = mainWindowController;
        }

        /// <summary>Returns the active SSH session, or throws a readable error when a COM/TCP tab (or nothing) is active.</summary>
        private SessionWrapper RequireSshSession(string feature)
        {
            SessionWrapper activeSession = _Controller.ActiveSession;
            if (activeSession == null || activeSession.IsPlaceholder)
            {
                throw new Exception("No session active");
            }
            if (activeSession.IsDevice)
            {
                throw new Exception(feature + " is available only in SSH sessions.");
            }
            return activeSession;
        }

        internal void SaveFile()
        {
            try
            {
                // In the background: a slow connection must not freeze the window. Closing a tab or the
                // window still saves synchronously (TrySave).
                ((_Controller.ActiveSession?.ActiveTab as EditorTabContext) ?? throw new Exception("No active editor tab")).Editor.SaveInBackground();
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        internal void NewSession()
        {
            ConnectionSelectionWindow connectionSelectionWindow = new ConnectionSelectionWindow(showRawConnections: true, null);
            bool? dialogResult = connectionSelectionWindow.ShowDialog();
            DeviceTerminalSettings pendingDevice = connectionSelectionWindow.Controller.TakePendingRawConnectionSettings();
            if (pendingDevice != null)
            {
                _Controller.AddDeviceSession(pendingDevice);
            }
            else if (dialogResult == true && connectionSelectionWindow.Controller.CreatedSession != null)
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
                ITabContext activeTab = _Controller.ActiveSession?.ActiveTab;
                string chosen = LocalFileDialogWindow.OpenFile(_Controller._Window, "Play back a recorded log of received data",
                    "All files (*.*)|*.*|Text log (*.log)|*.log|Hex dump (*.txt)|*.txt|Raw bytes (*.bin)|*.bin");
                if (chosen == null)
                {
                    return;
                }
                byte[] bytes;
                using (FileStream fileStream = File.OpenRead(chosen))
                {
                    using (MemoryStream memoryStream = new MemoryStream())
                    {
                        fileStream.CopyTo(memoryStream);
                        bytes = memoryStream.ToArray();
                    }
                }
                // Device terminals record raw bytes, SSH terminals record UTF-8 text.
                if (activeTab is DeviceTabContext deviceTab)
                {
                    LogReplayWindow.Replay(bytes, deviceTab.Terminal);
                    return;
                }
                TerminalEmulatorControl terminalEmulatorControl = (activeTab as NormalTabContext)?.Terminal;
                if (terminalEmulatorControl == null)
                {
                    throw new Exception("No active terminal");
                }
                LogReplayWindow.Replay(Encoding.UTF8.GetString(bytes), terminalEmulatorControl);
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
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
                if (_LogInProgress)
                {
                    terminalEmulatorControl.OpenLogFile(null);
                }
                else
                {
                    string logFile = LocalFileDialogWindow.SaveFile(_Controller._Window, "Save the terminal log to a file",
                        "Text log (*.log)|*.log|All files (*.*)|*.*");
                    if (logFile == null)
                    {
                        return;
                    }
                    terminalEmulatorControl.OpenLogFile(logFile);
                }
                _LogInProgress = !_LogInProgress;
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        internal void OpenFile(ITabContext tabWithCurrentDir = null)
        {
            try
            {
                SessionWrapper activeSession = RequireSshSession("Opening remote files");
                tabWithCurrentDir = tabWithCurrentDir ?? activeSession.ActiveTab;
                RemoteOpenFileWindow remoteOpenFileWindow = activeSession.CreateRemoteOpenFileDialog();
                // A name that does not exist yet opens an empty editor that creates the file on save.
                remoteOpenFileWindow.AllowNewFile = true;
                string text = activeSession.TryExtractCurrentDir(tabWithCurrentDir) ?? activeSession.LastDownloadDir;
                if ((tabWithCurrentDir as NormalTabContext)?.Terminal is SshTerminalControl sshTerminalControl)
                {
                    remoteOpenFileWindow.InitialDirectory = sshTerminalControl.CurrentDirectory;
                }
                else if (text == activeSession.CurrentDirDuringLastOpen && !string.IsNullOrEmpty(activeSession.LastUsedOpenDir))
                {
                    remoteOpenFileWindow.InitialDirectory = activeSession.LastUsedOpenDir;
                }
                else
                {
                    remoteOpenFileWindow.InitialDirectory = text;
                }
                string selectedPath = remoteOpenFileWindow.PickPath();
                if (selectedPath != null)
                {
                    activeSession.LastUsedOpenDir = RemotePathUtils.GetDirectoryName(selectedPath);
                    activeSession.CurrentDirDuringLastOpen = text;
                    CreateNewEditorTab(activeSession, selectedPath);
                }
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
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
            editor.LoadFailed += (object sender, EventArgs e) =>
            {
                session.CloseFirstMatchingPage((ITabContext tabContext) => tabContext.EditorControl == editor, closeSessionIfEmpty: false);
            };
            editor.SavingChanged += (object sender, EventArgs e) => UpdateEditorTitle(editor);
            editor.SyntaxChoiceChanged += (object sender, EventArgs e) => _Controller.ApplySyntaxHighlighting(editor);
            editor.LoadCompleted += (object sender, EventArgs e) =>
            {
                UpdateEditorTitle(editor);
                // A file whose name tells nothing (e.g. a script without an extension) may still be
                // recognized by its "#!" line now that the content is known. The completion language follows
                // the detection even when the highlighting is off, so this runs in that case too.
                if (editor.SyntaxOverride == null && SyntaxHighlighting.DetectLanguage(editor.RemotePath) == SyntaxHighlighting.Language.Plain)
                {
                    _Controller.ApplySyntaxHighlighting(editor);
                }
            };
            _Controller.ApplyEditorSettings(editor);
            editor.ZoomChanged += (object sender, EventArgs e) => _Controller.ApplyEditorZoom(editor.FontZoom);
            EditorTabContext newTab = session.AddNewFileEditorOrThrow(editor, fn);
            editor.Tag = newTab;
            // The tool bar and the context menu of the editor.
            newTab.EditorPanel.SaveRequested += (object sender, EventArgs e) => SaveFile();
            newTab.EditorPanel.GoToLineRequested += (object sender, EventArgs e) => GoToLine(editor);
            newTab.EditorPanel.ShowChangesRequested += (object sender, EventArgs e) => ShowChanges(editor);
            newTab.EditorPanel.RunRequested += (object sender, EventArgs e) => RunInTerminal(editor, session);
            newTab.EditorPanel.FollowLogRequested += (object sender, EventArgs e) => FollowLog(editor.RemotePath);
            newTab.EditorPanel.ShowAllCharactersChanged += (object sender, EventArgs e) =>
            {
                // The switch on the tool bar and in the context menu is the same setting as in the settings window.
                _Controller.Settings.Editor.ShowAllCharacters = editor.ShowAllCharacters;
                _Controller.Settings.Save();
            };
            newTab.EditorPanel.WordWrapChanged += (object sender, EventArgs e) =>
            {
                _Controller.Settings.Editor.WordWrap = editor.WordWrap;
                _Controller.Settings.Save();
            };
        }

        internal void Search()
        {
            ITabContext activeTab = _Controller.ActiveSession?.ActiveTab;
            if (activeTab is EditorTabContext editorTab && editorTab.Editor.IsHexMode)
            {
                editorTab.ShowHexFind();
                return;
            }
            TextSearcher searcher = activeTab?.Searcher;
            if (searcher != null)
            {
                searcher.IsReplaceVisible = false;
                searcher.Activate();
            }
        }

        private void GoToLine(RemoteFileEditor editor)
        {
            if (editor == null)
            {
                return;
            }
            if (editor.IsHexMode)
            {
                GoToOffsetWindow offsetWindow = new GoToOffsetWindow(editor.HexEditor.CaretOffset, editor.HexEditor.Length)
                {
                    Owner = _Controller._Window
                };
                if (offsetWindow.ShowDialog() == true)
                {
                    editor.HexEditor.GoToOffset(offsetWindow.Offset);
                }
                editor.FocusActiveView();
                return;
            }
            GoToLineWindow window = new GoToLineWindow(editor.CaretPosition.Line + 1, Math.Max(1, editor.LineCount))
            {
                Owner = _Controller._Window
            };
            if (window.ShowDialog() == true)
            {
                editor.GoToPosition(window.Line - 1, Math.Max(0, window.Column - 1));
            }
            editor.FocusActiveView();
        }

        /// <summary>
        /// Runs the file of the editor in the terminal (F5): from its folder, in the terminal tab used last (a new one when
        /// there is none). Unsaved changes, and a file that does not exist yet, are saved first after a question.
        /// </summary>
        private void RunInTerminal(RemoteFileEditor editor, SessionWrapper session)
        {
            if (editor == null || session == null || !editor.CanRunInTerminal || editor.IsSaving)
            {
                return;
            }
            if (!editor.IsModified && !editor.IsNewFile)
            {
                session.RunRemoteScript(editor.RemotePath, editor.RunInterpreter);
                return;
            }
            if (!MessageBoxes.Ask("Save " + RemotePathUtils.GetFileName(editor.RemotePath) + " and run it?"))
            {
                return;
            }
            // The save runs in the background: the file is run once it is on the server. When it ends without having saved
            // (a conflict, a refused write, a problem the check found) nothing is run; F5 starts again.
            EventHandler runWhenSaved = null;
            runWhenSaved = (object sender, EventArgs e) =>
            {
                if (editor.IsSaving)
                {
                    return;
                }
                editor.SavingChanged -= runWhenSaved;
                if (!editor.IsModified && !editor.IsNewFile)
                {
                    session.RunRemoteScript(editor.RemotePath, editor.RunInterpreter);
                }
            };
            editor.SavingChanged += runWhenSaved;
            editor.SaveInBackground();
        }

        /// <summary>Shows what has been changed in the editor against the file on the server (Show Changes).</summary>
        private void ShowChanges(RemoteFileEditor editor)
        {
            if (editor == null || editor.IsHexMode)
            {
                return;
            }
            ChangesWindow window = new ChangesWindow(RemotePathUtils.GetFileName(editor.RemotePath), editor.GetChanges())
            {
                Owner = _Controller._Window
            };
            window.ShowDialog();
            editor.FocusActiveView();
        }

        private void editor_UnsavedChanged(EditableTextBrowser editor, bool newValue)
        {
            UpdateEditorTitle(editor as RemoteFileEditor);
        }

        /// <summary>The tab title: the file name, "(new)" until the file exists, "(saving)" and "*" for unsaved changes.</summary>
        private static void UpdateEditorTitle(RemoteFileEditor editor)
        {
            if (editor?.Tag is EditorTabContext editorTabContext)
            {
                string title = RemotePathUtils.GetFileName(editorTabContext.FilePath);
                if (editor.IsNewFile)
                {
                    title += " (new)";
                }
                if (editor.IsPreview)
                {
                    title += " (preview, read-only)";
                }
                else if (editor.IsReadOnlyMode)
                {
                    title += " (read-only)";
                }
                if (editor.IsSaving)
                {
                    title += " (saving)";
                }
                if (editor.IsModified)
                {
                    title += " *";
                }
                editorTabContext.Title = title;
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
                    case Keys.H:
                        // Ctrl+H is also Backspace as a character: keep it from reaching the editor.
                        e.SuppressKeyPress = true;
                        if ((sender as RemoteFileEditor)?.IsHexMode != true)
                        {
                            _Controller.ActiveSession?.ActiveTab?.Searcher?.ActivateReplace();
                        }
                        break;
                    case Keys.G:
                        e.SuppressKeyPress = true;
                        GoToLine(sender as RemoteFileEditor);
                        break;
                }
            }
            else if (!e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.F5)
            {
                if (sender is RemoteFileEditor runnable && runnable.CanRunInTerminal && runnable.Tag is EditorTabContext runnableTab)
                {
                    e.SuppressKeyPress = true;
                    runnableTab.EditorPanel.RunCommand(EditorToolbarCommand.RunFile);
                }
            }
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.F3)
            {
                if (sender is RemoteFileEditor hexEditor && hexEditor.IsHexMode)
                {
                    e.SuppressKeyPress = true;
                    hexEditor.HexEditor.FindNext(!e.Shift);
                }
                else if ((sender as ReadonlyTextBrowser)?.Tag is EditorTabContext editorTabContext)
                {
                    editorTabContext?.Searcher?.Search(e.Shift);
                }
            }
        }

        internal void Copy()
        {
            ITabContext activeTab = _Controller.ActiveSession?.ActiveTab;
            if (activeTab is DeviceTabContext deviceTab)
            {
                deviceTab.Terminal.CopySelection();
                return;
            }
            activeTab?.EditorControl?.CopySelection();
        }

        internal void Paste()
        {
            ITabContext activeTab = _Controller.ActiveSession?.ActiveTab;
            if (activeTab is DeviceTabContext deviceTab)
            {
                deviceTab.Terminal.Paste();
                return;
            }
            activeTab?.EditorControl?.Paste();
        }

        internal void CopyAll()
        {
            _Controller.CopyAll();
        }

        internal void SCPTransfers()
        {
            if (_Controller.ActiveSession?.IsDevice == true)
            {
                return;
            }
            _Controller.ActiveSession?.SCPController?.Show();
        }

        internal void UploadFile()
        {
            try
            {
                SessionWrapper activeSession = RequireSshSession("File transfer");
                SCPUploadWindow scpUploadWindow = new SCPUploadWindow(activeSession.ExecuteAsyncCommand)
                {
                    RemoteDirectory = _Controller.Settings.DefaultUploadDirectory
                };
                WindowOwnership.Attach(scpUploadWindow, _Controller._Window);
                if (scpUploadWindow.ShowDialog() == true)
                {
                    _Controller.Settings.DefaultUploadDirectory = scpUploadWindow.RemoteDirectory.Trim();
                    string dir = _Controller.Settings.DefaultUploadDirectory;
                    activeSession.ExpandHomeDirIfNeeded(ref dir);
                    _Controller.Settings.DefaultUploadDirectory = dir;
                    _Controller.Settings.Save();
                    activeSession.UploadFilesToDirectory(scpUploadWindow.LocalFiles, _Controller.Settings.DefaultUploadDirectory);
                }
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
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
            foreach (SessionWrapper session in _Controller.AllSessions)
            {
                session.ApplyTransferSettings();
                session.SaveTransferQueue();
            }
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
                    else if (tab is DeviceTabContext deviceTab)
                    {
                        deviceTab.ApplyCommonSettings();
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
            try
            {
                SessionWrapper activeSession = RequireSshSession("Public key authentication");
                PublicKeyManagerWindow.Show(_Controller._Window, activeSession.Session, activeSession.Settings);
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        internal void About()
        {
            AboutWindow aboutWindow = new AboutWindow();
            WindowOwnership.Attach(aboutWindow, _Controller._Window);
            aboutWindow.ShowDialog();
        }

        internal void DownloadDir()
        {
            try
            {
                DirectoryTransferWindow window = new DirectoryTransferWindow(RequireSshSession("Directory transfer").Session, isUpload: false)
                {
                    LocalDirectory = _Controller.Settings.DefaultDownloadDirectory
                };
                window.Closed += Frm_FormClosed;
                WindowOwnership.Attach(window, _Controller._Window);
                window.Show();
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        private void Frm_FormClosed(object sender, EventArgs e)
        {
            DirectoryTransferWindow window = (DirectoryTransferWindow)sender;
            if (window.IsUpload)
            {
                _Controller.Settings.DefaultUploadDirectory = window.RemoteDirectory;
            }
            else
            {
                _Controller.Settings.DefaultDownloadDirectory = window.LocalDirectory;
            }
            _Controller.Settings.Save();
        }

        /// <summary>
        /// Copies a directory from one open SSH host to another. The copy needs two SSH sessions at once, so the
        /// window offers every open SSH session and the user picks the source and the target there.
        /// </summary>
        internal void CopyDirBetweenHosts()
        {
            try
            {
                SessionWrapper activeSession = RequireSshSession("Directory transfer");
                HostToHostCopyWindow window = new HostToHostCopyWindow(
                    activeSession,
                    _Controller.AllSessions.ToList(),
                    activeSession.TryExtractCurrentDir() ?? activeSession.LastDownloadDir);
                WindowOwnership.Attach(window, _Controller._Window);
                window.Show();
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        /// Opens the folder synchronization window for the active SSH session. Unlike a directory transfer, this
        /// compares both sides first and copies only the differences, with an optional deletion of the extra files.
        /// </summary>
        internal void Synchronize()
        {
            try
            {
                SessionWrapper activeSession = RequireSshSession("Folder synchronization");
                SynchronizeWindow window = new SynchronizeWindow(activeSession.Session)
                {
                    RemoteDirectory = activeSession.TryExtractCurrentDir() ?? activeSession.LastDownloadDir,
                    LocalDirectory = _Controller.Settings.DefaultDownloadDirectory
                };
                WindowOwnership.Attach(window, _Controller._Window);
                window.Show();
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        internal void UploadDir()
        {
            try
            {
                DirectoryTransferWindow window = new DirectoryTransferWindow(RequireSshSession("Directory transfer").Session, isUpload: true)
                {
                    RemoteDirectory = _Controller.Settings.DefaultUploadDirectory
                };
                window.Closed += Frm_FormClosed;
                WindowOwnership.Attach(window, _Controller._Window);
                window.Show();
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        /// <summary>
        /// Downloads several remote files, or folders as .tar.gz archives, into the default download directory. The
        /// directory is asked for once (when it is not set), not once per item.
        /// </summary>
        public void QuickDownloadMany(string[] remotePaths, bool archives)
        {
            try
            {
                if (remotePaths == null || remotePaths.Length == 0)
                {
                    return;
                }
                SessionWrapper activeSession = RequireSshSession("File transfer");
                string text = _Controller.Settings.DefaultDownloadDirectory;
                if (string.IsNullOrEmpty(text) || !Directory.Exists(text))
                {
                    text = LocalFolderBrowserWindow.Browse(text, "Select download directory");
                    if (text == null)
                    {
                        return;
                    }
                }
                for (int index = 0; index < remotePaths.Length; index++)
                {
                    // The download directory is opened once, for the last item, not once per item.
                    bool openWhenDone = _Controller.Settings.OpenLocalDirAfterDownload && index == remotePaths.Length - 1;
                    if (archives)
                    {
                        activeSession.DoDownloadArchive(remotePaths[index], text, openWhenDone);
                    }
                    else
                    {
                        activeSession.DoDownloadFile(remotePaths[index], text, openWhenDone);
                    }
                }
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        public void QuickDownload(string fn = null)
        {
            try
            {
                SessionWrapper activeSession = RequireSshSession("File transfer");
                if (fn == null)
                {
                    fn = activeSession.CreateRemoteOpenFileDialog().PickPath();
                    if (fn == null)
                    {
                        return;
                    }
                }
                string text = _Controller.Settings.DefaultDownloadDirectory;
                if (string.IsNullOrEmpty(text) || !Directory.Exists(text))
                {
                    text = LocalFolderBrowserWindow.Browse(text, "Select download directory");
                    if (text == null)
                    {
                        return;
                    }
                }
                DoDownloadFile(fn, text, _Controller.Settings.OpenLocalDirAfterDownload);
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        /// <summary>
        /// Downloads a remote folder as a .tar.gz archive into the default download directory (asked for when it is not set).
        /// </summary>
        public void QuickDownloadArchive(string remoteDirectory)
        {
            try
            {
                SessionWrapper activeSession = RequireSshSession("File transfer");
                string text = _Controller.Settings.DefaultDownloadDirectory;
                if (string.IsNullOrEmpty(text) || !Directory.Exists(text))
                {
                    text = LocalFolderBrowserWindow.Browse(text, "Select download directory");
                    if (text == null)
                    {
                        return;
                    }
                }
                activeSession.DoDownloadArchive(remoteDirectory, text, _Controller.Settings.OpenLocalDirAfterDownload);
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        /// <summary>
        /// Asks how to follow a log file on the server (the last lines, a filter) and follows it like tail -f in a new
        /// terminal tab.
        /// </summary>
        public void FollowLog(string remotePath)
        {
            try
            {
                SessionWrapper session = RequireSshSession("Following a log");
                FollowLogWindow window = new FollowLogWindow(RemotePathUtils.GetFileName(remotePath))
                {
                    Owner = _Controller._Window
                };
                if (window.ShowDialog() == true)
                {
                    session.FollowLog(remotePath, window.Lines, window.Filter, window.FilterIsRegex, window.IgnoreCase);
                }
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        /// <summary>Runs a shell script that is on the server, from the folder it is in, in the active terminal.</summary>
        public void RunRemoteScript(string remotePath, string defaultInterpreter = "sh")
        {
            try
            {
                RequireSshSession("Running scripts").RunRemoteScript(remotePath, defaultInterpreter);
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        /// <summary>Installs .ipk or .apk packages that are on the OpenWrt device, with its package manager, in the active terminal.</summary>
        public void InstallRemotePackages(string[] remotePaths)
        {
            try
            {
                RequireSshSession("Installing packages").InstallRemotePackages(remotePaths);
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        /// <summary>Asks for a local shell script and runs it on the server in the active terminal.</summary>
        internal void RunScript()
        {
            try
            {
                SessionWrapper activeSession = RequireSshSession("Running scripts");
                string script = LocalFileDialogWindow.OpenFile(_Controller._Window, "Select a shell script to run on the server",
                    "Shell scripts (*.sh;*.ash;*.bash)|*.sh;*.ash;*.bash|All files (*.*)|*.*");
                if (script != null)
                {
                    activeSession.RunScript(script);
                }
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        internal void DownloadFile()
        {
            try
            {
                SessionWrapper activeSession = RequireSshSession("File transfer");
                string text = activeSession.TryExtractCurrentDir() ?? activeSession.LastDownloadDir;
                SCPDownloadWindow scpDownloadWindow = new SCPDownloadWindow(activeSession.ExecuteAsyncCommand, text, showCheckBox: true)
                {
                    OpenLocalDirAfterDownload = _Controller.Settings.OpenLocalDirAfterDownload,
                    LocalDirectory = _Controller.Settings.DefaultDownloadDirectory
                };
                WindowOwnership.Attach(scpDownloadWindow, _Controller._Window);
                if (scpDownloadWindow.ShowDialog() == true)
                {
                    _Controller.Settings.DefaultDownloadDirectory = scpDownloadWindow.LocalDirectory;
                    _Controller.Settings.Save();
                    DoDownloadFile(scpDownloadWindow.RemoteFile, scpDownloadWindow.LocalDirectory, scpDownloadWindow.OpenLocalDirAfterDownload);
                }
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
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
            _Timer.Tick += (object sender, EventArgs e) =>
            {
                RefreshAndSendKeepalives();
            };
        }

        private static string TransferStatusPrefix(SessionWrapper session)
        {
            // "SCP" or "SFTP"; the panel title tells why SCP is used when SFTP was selected.
            return (session.TransferManager?.EffectiveProtocolName ?? "SCP").Split(' ')[0] + ": ";
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
                if (activeSession.IsDevice)
                {
                    Status1 = activeSession.DeviceStatusText;
                    Status2 = activeSession.DeviceTrafficText;
                    ProgressVisibility = Visibility.Collapsed;
                    return;
                }
                if (activeSession.IsPlaceholder)
                {
                    return;
                }
                SCPController.SCPStatistics valueOrDefault = (_Controller.ActiveSession?.SCPController?.SendUpdateEventsAndGetStatistics()).GetValueOrDefault();
                if (valueOrDefault.TotalActiveOrQueuedTransfers == 0)
                {
                    Status1 = TransferStatusPrefix(activeSession) + "No transfers";
                    ProgressVisibility = Visibility.Collapsed;
                }
                else
                {
                    ProgressVisibility = Visibility.Visible;
                    if (activeSession.TransferManager.IsPaused)
                    {
                        Status1 = TransferStatusPrefix(activeSession) + string.Format("paused ({1}/{2})", valueOrDefault.TotalTransfers, StringUtils.FormatSize(valueOrDefault.TotalDone), StringUtils.FormatSize(valueOrDefault.TotalSize));
                    }
                    else
                    {
                        Status1 = TransferStatusPrefix(activeSession) + $"{valueOrDefault.TotalTransfers} transfers ({StringUtils.FormatSize(valueOrDefault.TotalDone)}/{StringUtils.FormatSize(valueOrDefault.TotalSize)})";
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
            catch (Exception exception)
            {
                Status1 = exception.Message;
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

    // How close to an edge of the terminal the mouse must be for that edge to count as "near". The same margin
    // is used for the auto-hidden scrollbars and for revealing the menu and the tab headers in minimalistic mode.
    private const int EdgeMargin = 30;

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

    // The standard API symbols (ApiSymbols.xml). Loaded at most once, on the first editor that needs them,
    // and only while the setting is on, so a switched-off feature never reads the file.
    private ApiSymbolDatabase _ApiSymbols;

    private ApiSymbolDatabase ApiSymbols
    {
        get
        {
            if (_ApiSymbols == null && Settings.Editor.ApiSymbols)
            {
                _ApiSymbols = ApiSymbolDatabase.Load();
            }
            return _ApiSymbols;
        }
    }

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
            if (activeSession != null && activeSession.IsPlaceholder)
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

    private int FirstPseudoPage => Enumerable.Range(0, AllSessions.Count).FirstOrDefault((int index) => AllSessions[index].IsPlaceholder);

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

    /// <summary>
    /// Visibility of the "_Replay log" menu command. Replay works for SSH terminals and for device (COM / raw TCP /
    /// telnet) terminals, so it is shown whenever the active tab is a terminal of either kind — unlike the
    /// debug-only commands next to it.
    /// </summary>
    public Visibility ReplayVisibility
    {
        get
        {
            switch (ActiveSession?.ActiveTab)
            {
                case NormalTabContext normalTab:
                    return normalTab.Terminal == null ? Visibility.Collapsed : Visibility.Visible;
                case DeviceTabContext:
                    return Visibility.Visible;
                default:
                    return Visibility.Collapsed;
            }
        }
    }

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
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    /// <summary>
    /// Asks whether to save every editor with unsaved changes before the main window closes.
    /// Returns false when the user cancelled or a save failed: the window must then stay open.
    /// </summary>
    public bool ConfirmCloseUnsavedEditors()
    {
        foreach (SessionWrapper session in AllSessions.ToList())
        {
            foreach (EditorTabContext editorTab in session.Tabs.OfType<EditorTabContext>().ToList())
            {
                if (!editorTab.Editor.IsModified)
                {
                    continue;
                }
                switch (MessageBoxes.YesNoCancel("Save " + editorTab.FilePath + "?"))
                {
                    case System.Windows.MessageBoxResult.Yes:
                        if (!editorTab.Editor.TrySave())
                        {
                            return false;
                        }
                        break;
                    case System.Windows.MessageBoxResult.No:
                        // The changes are given up on purpose: no draft of them is kept.
                        editorTab.Editor.DiscardDraft();
                        break;
                    case System.Windows.MessageBoxResult.Cancel:
                        return false;
                }
            }
        }
        return true;
    }

    public void ExportPreferences(CommonGUIPreferences prefs)
    {
        prefs.FullScreen = FullScreenMode;
        prefs.Minimalistic = MinimalisticMode;
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
        GlobalSettings.EditorColors effectiveColors = Settings.Editor.GetEffectiveColors(Settings.IsLightTheme);
        edit.BackColor = effectiveColors.BackColor;
        edit.ForeColor = effectiveColors.TextColor;
        edit.Brush = new SolidBrush(effectiveColors.LineNumberBackground);
        edit.LineNumberForegroundBrush = new SolidBrush(effectiveColors.LineNumberForeground);
        edit.TabSize = Settings.Editor.TabSize;
        edit.InsertSpaces = Settings.Editor.InsertSpaces;
        edit.HideMargins = !Settings.Editor.ShowLineNumbers;
        edit.ShowTabs = Settings.Editor.ShowTabs;
        edit.ShowAllCharacters = Settings.Editor.ShowAllCharacters;
        edit.WordWrap = Settings.Editor.WordWrap;
        edit.FontZoom = Settings.Editor.FontZoom > 0f ? Settings.Editor.FontZoom : 1f;
        edit.CompletionEnabled = Settings.Editor.Autocompletion;
        edit.CompletionOnTyping = Settings.Editor.AutocompletionOnTyping;
        edit.ApiSymbols = Settings.Editor.ApiSymbols ? ApiSymbols : null;
        ApplySyntaxHighlighting(edit);
        // The inline messages of a check follow the editor theme.
        edit.RefreshMessageColors();
        // The settings window can switch the character marks too, so the tool bar and the menu show the new state.
        if (edit.Tag is EditorTabContext editorTab)
        {
            editorTab.EditorPanel.RefreshState();
        }
    }

    /// <summary>Gives every open file editor the zoom just chosen in one of them.</summary>
    private void ApplyEditorZoom(float zoom)
    {
        Settings.Editor.FontZoom = zoom;
        Settings.Save();
        foreach (SessionWrapper session in AllSessions)
        {
            foreach (ITabContext tab in session.Tabs)
            {
                if (tab is EditorTabContext editorTab)
                {
                    editorTab.Editor.FontZoom = zoom;
                }
            }
        }
    }

    private void ApplySyntaxHighlighting(RemoteFileEditor edit)
    {
        GlobalSettings.EditorColors effectiveColors = Settings.Editor.GetEffectiveColors(Settings.IsLightTheme);
        TokenCache.LineTokenizer tokenizer = null;
        SyntaxHighlighting.Language language = SyntaxHighlighting.Language.Plain;
        // A syntax chosen by hand works even when the automatic highlighting is switched off in the settings.
        if (Settings.Editor.SyntaxHighlighting || edit.SyntaxOverride != null)
        {
            // The first line is only known once the file is loaded (see the LoadCompleted handler): it lets
            // scripts without an extension be recognized by their "#!" line.
            language = edit.DetectSyntax();
            tokenizer = SyntaxHighlighting.GetTokenizer(language);
        }

        edit.ReportLanguage(language);
        edit.SetTokenizer(tokenizer);
        if (tokenizer != null)
        {
            edit.TokenBrushes = new SolidBrush[]
            {
                new SolidBrush(effectiveColors.TextColor),
                new SolidBrush(effectiveColors.KeywordColor),
                new SolidBrush(effectiveColors.StringColor),
                new SolidBrush(effectiveColors.CommentColor),
                new SolidBrush(effectiveColors.NumberColor),
                new SolidBrush(effectiveColors.VariableColor),
                new SolidBrush(effectiveColors.SectionColor),
                new SolidBrush(effectiveColors.PreprocessorColor)
            };
        }
        else
        {
            edit.TokenBrushes = null;
        }
    }

    public MainWindowController(MainWindow wnd)
    {
        _Window = wnd;
        Border popupBorder = new Border
        {
            Child = (UIElement)new VirtualTerminalPopup(this)
        };
        // The popup is not part of the window, so the theme frame (background, text colour) is set explicitly.
        popupBorder.SetResourceReference(FrameworkElement.StyleProperty, "PopupBorder");
        _VirtualTerminalPopup = new Popup
        {
            Child = (UIElement)popupBorder,
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false
        };
        Settings = GlobalSettings.Load() ?? new GlobalSettings();
        MenuCommandHandlers = new MenuCommandHandlersImpl(this);
        ToolStrip = new ToolStripImpl(this, ((DispatcherObject)wnd).Dispatcher);
        Dispatcher = ((DispatcherObject)wnd).Dispatcher;
        _TerminalContextMenu = new ContextMenuStrip();
        _TerminalContextMenu.Items.Add("Copy", Icons.Load("copy"), (object sender, EventArgs e) =>
        {
            ActiveSession?.ActiveTab?.EditorControl?.CopySelection();
        });
        _TerminalContextMenu.Items.Add("Paste", Icons.Load("paste"), (object _, EventArgs e) =>
        {
            ActiveSession?.ActiveTab?.EditorControl?.Paste();
        });
        ToolStripMenuItem pasteSpecialMenu = new ToolStripMenuItem("Paste special");
        pasteSpecialMenu.DropDownItems.Add("Paste as one line (lines joined with a space)", null, (object _, EventArgs e) => PasteSpecial(PasteMode.OneLineSpaces));
        pasteSpecialMenu.DropDownItems.Add("Paste as one line (lines joined with &&)", null, (object _, EventArgs e) => PasteSpecial(PasteMode.OneLineAnd));
        ToolStripItem pasteLineByLineItem = pasteSpecialMenu.DropDownItems.Add("Paste line by line (wait for each command)", null, (object _, EventArgs e) => PasteSpecial(PasteMode.LineByLine));
        pasteLineByLineItem.ToolTipText = "Smart Terminal: runs the pasted commands one after another, each when the previous one has ended; stops at a failed command.";
        _TerminalContextMenu.Items.Add(pasteSpecialMenu);
        _TerminalContextMenu.Opening += (object _, System.ComponentModel.CancelEventArgs e) =>
        {
            // Only the Smart Terminal knows when a command has ended.
            pasteLineByLineItem.Enabled = ((_TerminalContextMenu.SourceControl as TerminalEmulatorControl) ?? (ActiveSession?.ActiveTab as NormalTabContext)?.Terminal) is SshTerminalControl;
        };
        // Painted when it opens, so that it follows the theme chosen in the settings like the other menus.
        _TerminalContextMenu.Opening += (object _, System.ComponentModel.CancelEventArgs e) => ThemedMenu.Apply(_TerminalContextMenu);
        _TerminalContextMenu.Items.Add("Copy All", Icons.Load("copy"), (object _, EventArgs e) =>
        {
            CopyAll();
        });
        _TerminalContextMenu.Items.Add("Clear", Icons.Load("clear"), (object _, EventArgs e) =>
        {
            ((_TerminalContextMenu.SourceControl as TerminalEmulatorControl) ?? (ActiveSession?.ActiveTab as NormalTabContext)?.Terminal)?.ClearOutput();
        });
        _TerminalContextMenu.Items.Add("New Tab", Icons.Load("add"), (object _, EventArgs e) =>
        {
            ActiveSession?.CreateNewTerminalPage();
        });
        _TerminalContextMenu.Items.Add("Close Tab", Icons.Load("delete"), (object _, EventArgs e) =>
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
        ThemeManager.Apply(Settings);
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
        ITabContext activeTab = ActiveSession?.ActiveTab;
        if (activeTab is DeviceTabContext deviceTab)
        {
            deviceTab.Terminal.CopyAll();
            return;
        }
        (activeTab?.EditorControl)?.CopyAll();
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
        HookSessionCollection();
        ActiveSessionIndex = firstPseudoPage;
        return sessionWrapper;
    }

    private bool _SessionCollectionHooked;

    private void HookSessionCollection()
    {
        if (!_SessionCollectionHooked)
        {
            _SessionCollectionHooked = true;
            AllSessions.CollectionChanged += AllSessions_CollectionChanged;
        }
    }

    /// <summary>
    /// Opens a COM-port or raw-TCP terminal as a session next to the SSH sessions.
    /// Different ports can be open at the same time. A port that is already open is not opened a second time:
    /// its existing session is activated instead (and reconnected if it was closed), because a COM port has one owner.
    /// </summary>
    internal SessionWrapper AddDeviceSession(DeviceTerminalSettings settings)
    {
        if (settings?.Connection == null || !settings.Connection.IsValid())
        {
            throw new ArgumentException("The device connection is not specified.", nameof(settings));
        }
        string exclusiveKey = DeviceTabContext.MakeExclusiveKey(settings.Connection);
        if (exclusiveKey != null)
        {
            SessionWrapper existing = AllSessions.FirstOrDefault((SessionWrapper candidate) => candidate.DeviceExclusiveKey == exclusiveKey);
            if (existing != null)
            {
                ActiveSessionIndex = AllSessions.IndexOf(existing);
                existing.DeviceTab?.Terminal.ReconnectIfClosed();
                return existing;
            }
        }
        int firstPseudoPage = FirstPseudoPage;
        DeviceTerminalSettings sessionSettings = CloneForSession(settings);
        string title = MakeUniqueDeviceTitle(DeviceTerminalControl.GetConnectionTitle(sessionSettings.Connection));
        SessionWrapper sessionWrapper = SessionWrapper.CreateDeviceSession(this, sessionSettings, title);
        sessionWrapper.DeviceTab.Terminal.CompletionPopupRequested += MainWindowController_VirtualTerminalPopupRequested;
        sessionWrapper.DeviceTab.Terminal.OpenFileRequested += DeviceTab_OpenFileRequested;
        SubscribeMinimalisticModeMouseTracking(sessionWrapper.DeviceTab.Terminal);
        sessionWrapper.ShowNewSessionTab = AllSessions.Count == 1;
        AllSessions.Insert(firstPseudoPage, sessionWrapper);
        HookSessionCollection();
        ActiveSessionIndex = firstPseudoPage;
        return sessionWrapper;
    }

    // Every tab works on its own copy: changing the baud rate or the encoding in one tab must not leak into the
    // saved connection list item it was opened from, or into another tab opened from the same item.
    private static DeviceTerminalSettings CloneForSession(DeviceTerminalSettings source)
    {
        DeviceTerminalSettings clone = source.Clone();
        if (source.Connection is DeviceTerminalSettings.ConnectionSettings.Serial serial)
        {
            clone.Connection = serial.Clone();
        }
        else if (source.Connection is DeviceTerminalSettings.ConnectionSettings.Tcp tcp)
        {
            // Clone() keeps the kind of the endpoint: a telnet connection must stay a telnet connection.
            clone.Connection = tcp.Clone();
        }
        return clone;
    }

    private string MakeUniqueDeviceTitle(string baseTitle)
    {
        string title = baseTitle;
        int suffix = 2;
        while (AllSessions.Any((SessionWrapper candidate) => candidate.IsDevice && string.Equals(candidate.Title, title, StringComparison.OrdinalIgnoreCase)))
        {
            title = baseTitle + " #" + suffix++;
        }
        return title;
    }

    private void AllSessions_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HideHeaders"));
        if (AllSessions.Count == 2 && !AllSessions[0].IsPlaceholder)
        {
            AllSessions[0].ShowNewSessionTab = true;
        }
        else if (AllSessions.Count > 2 && !AllSessions[0].IsPlaceholder)
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
            // The session the tab is created for, which is not necessarily the active one yet (a new connection).
            SshTerminalControl vt = new SshTerminalControl(session, initialDirForVirtualMode)
            {
                Tag = new TerminalTag
                {
                    Session = session
                }
            };
            vt.EndOfInputRequested += (object sender, EventArgs e) =>
            {
                session.CloseTabPageForControl(vt);
            };
            vt.CompletionPopupRequested += MainWindowController_VirtualTerminalPopupRequested;
            vt.OpenFileRequested += Vt_FileOpenRequested;
            vt.ByteOrientedLineEditing = session.UsesByteOrientedLineEditing(smartTerminal: true);
            terminalEmulatorControl = vt;
        }
        else
        {
            terminalEmulatorControl = new TerminalEmulatorControl();
            terminalEmulatorControl.HighlightLogLevels = initialCommand.HighlightLogLevels;
            // Decided before the shell starts: the PTY of a byte-counting shell is opened without IUTF8.
            terminalEmulatorControl.ByteOrientedLineEditing = session.UsesByteOrientedLineEditing(smartTerminal: false);
            SshShellChannel sshShellChannel = session.Session.OpenShellChannel(initialCommand.Command, "xterm", ExtendedDataMode.Normal, utf8Tty: !terminalEmulatorControl.ByteOrientedLineEditing);
            sshShellChannel.ExtendedDataMode = ExtendedDataMode.Merge;
            terminalEmulatorControl.DataEntered += terminalEmulatorControl1_HandleTerminalInput;
            terminalEmulatorControl.TerminalSizeChanged += terminalEmulatorControl1_TerminalSizeChanged;
            terminalEmulatorControl.Tag = new TerminalTag
            {
                Channel = sshShellChannel,
                Session = session
            };
            session.AttachChannelToControl(terminalEmulatorControl, sshShellChannel, initialCommand.Command != null, initialCommand.TabKeptAfterExit);
        }
        // A very large paste can be sent as a file instead of being typed into the terminal.
        terminalEmulatorControl.LargePasteUploadRequested += (TerminalEmulatorControl control, string text) => session.UploadPastedText(text, control);
        terminalEmulatorControl.CaretWhenUnfocused = false;
        // Pasted line breaks reach the remote program as Enter (CR), as in other terminal emulators; a bare LF is
        // Ctrl+J, which editors such as nano or Emacs treat as a command of its own.
        terminalEmulatorControl.PasteLineBreak = "\r";
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
            Font configuredFont = Settings.ConfiguredFont;
            if (configuredFont != null)
            {
                terminalEmulatorControl.Font = configuredFont;
            }
        }
        catch
        {
        }
        return terminalEmulatorControl;
    }

    private void PasteSpecial(PasteMode mode)
    {
        TerminalEmulatorControl terminal = (_TerminalContextMenu.SourceControl as TerminalEmulatorControl) ?? (ActiveSession?.ActiveTab as NormalTabContext)?.Terminal;
        terminal?.PasteSpecial(mode);
    }

    private void Vt_FileOpenRequested(object sender, OpenFileEventArgs e)
    {
        try
        {
            MenuCommandHandlers.CreateNewEditorTab(ActiveSession, e.Path, new TextDocument.TextPosition(e.Column.GetValueOrDefault(), e.Line.GetValueOrDefault()));
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void DeviceTab_OpenFileRequested(object sender, OpenFileEventArgs e)
    {
        try
        {
            Process.Start(e.Path);
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void MainWindowController_VirtualTerminalPopupRequested(object sender, CompletionPopupRequest e)
    {
        _VirtualTerminalPopup.Placement = PlacementMode.Custom;
        _VirtualTerminalPopup.PlacementTarget = (UIElement)_Window;
        // The popup window is shared: the list it showed before belongs to a completion that is over now.
        (((FrameworkElement)_VirtualTerminalPopup).DataContext as SuggestionPopupController)?.Detach();
        SuggestionPopupController suggestionPopupController = new SuggestionPopupController(this, e, _VirtualTerminalPopup);
        ((FrameworkElement)_VirtualTerminalPopup).DataContext = suggestionPopupController;
        _VirtualTerminalPopup.CustomPopupPlacementCallback = suggestionPopupController.PlacePopup;
        _VirtualTerminalPopup.IsOpen = true;
        suggestionPopupController.ItemSelected += (CompletionItem ba2, Keys isEnter) =>
        {
            suggestionPopupController.Close();
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
        if (AllSessions.Count((SessionWrapper sessionWrapper) => !sessionWrapper.IsPlaceholder) == 0)
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
            SshTerminalControl sshTerminal = terminalEmulatorControl as SshTerminalControl;
            if ((sshTerminal == null || !((TextEditorControl)sshTerminal).CanEdit) && e.Control && !e.Alt && !e.Shift)
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
        TerminalTag tag = sender.Tag as TerminalTag;
        if (tag?.Channel == null || tag.ShellOpenFailed)
        {
            return;
        }
        try
        {
            tag.Channel.ResizePty(newWidth, newHeight);
        }
        catch (Exception exception) when (IsConnectionGoneError(tag, exception))
        {
            // The channel is gone with a lost connection: the reconnect prompt deals with it, a resize must not crash the window.
        }
    }

    /// <summary>
    /// Whether an exception from writing to the terminal channel only says that the connection is gone. SSH.NET throws
    /// SshConnectionException (not an IOException) in the short window between the real break and the moment the
    /// channels are marked closed, so the channel flags cannot be relied on alone.
    /// </summary>
    private static bool IsConnectionGoneError(TerminalTag tag, Exception exception)
    {
        return IsConnectionGoneError(tag, tag.Channel, exception);
    }

    /// <summary>
    /// The same for a write that was held back and went to <paramref name="channel"/>, which may no longer be the terminal's
    /// channel by now (a reconnect replaces it).
    /// </summary>
    private static bool IsConnectionGoneError(TerminalTag tag, SshShellChannel channel, Exception exception)
    {
        if (exception is Renci.SshNet.Common.SshConnectionException || exception is ObjectDisposedException)
        {
            return true;
        }
        return channel.IsDisposed || channel.ClosedByConnectionLoss || channel.IsEof || (tag.Session?.Session?.IsConnectionClosed ?? false);
    }

    private void terminalEmulatorControl1_HandleTerminalInput(object sender, byte[] input, TerminalEmulatorControl.InputKind type)
    {
        TerminalEmulatorControl terminal = (TerminalEmulatorControl)sender;
        TerminalTag tag = (TerminalTag)terminal.Tag;
        if (tag.ShellOpenFailed)
        {
            // The shell could not be reopened after a reconnect: this key press is the retry.
            tag.Session?.RetryShell(terminal);
            return;
        }
        if (tag.Writes.IsBusy)
        {
            // A command that the program typed in (Run, Install ...) is still on its way into the channel: this key goes behind
            // it, so the keys and the command arrive in the order they were given. That write runs on a thread of the pool, so
            // it reports its own errors.
            SshShellChannel heldChannel = tag.Channel;
            tag.Writes.Post(delegate
            {
                try
                {
                    heldChannel.Input.Write(input, 0, input.Length);
                }
                catch (Exception exception)
                {
                    if (!IsConnectionGoneError(tag, heldChannel, exception))
                    {
                        BeginInvoke(() => MessageBoxes.Exception(exception));
                    }
                }
            });
            return;
        }
        try
        {
            tag.Channel.Input.Write(input, 0, input.Length);
        }
        catch (Exception exception) when (IsConnectionGoneError(tag, exception))
        {
            // Typing into a tab whose connection is gone (the reconnect prompt is showing): the input is dropped.
        }
    }

    private void anyctl_MouseMove(object sender, MouseEventArgs e)
    {
        ScreenEdgeUtils.EdgeFlags edgeFlags = ScreenEdgeUtils.GetNearEdges(sender as Control, e.Location, EdgeMargin);
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
        UpdateMinimalisticModeVisibility(edgeFlags);
    }

    /// <summary>
    /// Hides the menu and the tab headers while the mouse is away from the window edges, and brings them back
    /// when it is near the edge they are docked to. The same rules drive SSH and device (COM/TCP) terminals;
    /// only the source of the mouse events is different, because a WinForms control hosted in a
    /// WindowsFormsHost does not pass its MouseMove events up to the WPF window.
    /// </summary>
    private void UpdateMinimalisticModeVisibility(ScreenEdgeUtils.EdgeFlags edgeFlags)
    {
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

    /// <summary>
    /// Subscribes <see cref="UpdateMinimalisticModeVisibility"/> to the mouse events of a device (COM/TCP)
    /// terminal. The terminal itself and every child that can appear under the pointer are handled, because a
    /// child control does not pass its MouseMove events to its parent. Coordinates are converted to the
    /// terminal's client area, so a control that does not fill the window (a label of the "connecting" panel,
    /// for example) cannot make a window edge look nearer than it is.
    /// </summary>
    private void SubscribeMinimalisticModeMouseTracking(Control terminal)
    {
        SubscribeMinimalisticModeMouseTracking(terminal, terminal);
    }

    private void SubscribeMinimalisticModeMouseTracking(Control control, Control terminal)
    {
        control.MouseMove += (sender, e) =>
        {
            if (!(sender is Control source) || terminal.IsDisposed || source.IsDisposed)
            {
                return;
            }
            Point location = terminal.PointToClient(source.PointToScreen(e.Location));
            UpdateMinimalisticModeVisibility(ScreenEdgeUtils.GetNearEdges(terminal, location, EdgeMargin));
        };
        foreach (Control child in control.Controls)
        {
            SubscribeMinimalisticModeMouseTracking(child, terminal);
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
            // COM ports and saved raw TCP endpoints are offered here as well (showRawConnections), so they open as
            // tabs of this window; the update thread is what discovers the COM ports that are currently present.
            NewConnectionController = new ConnectionSelectionWindow.ControllerImpl(Dispatcher, showRawConnections: true, null, new CommonGUIPreferences
            {
                ProbeConnections = commonGUIPreferences.ProbeConnections,
                SortMode = commonGUIPreferences.SortMode,
                ShownConnections = new ShownConnectionTypes
                {
                    SSH = true,
                    RawTcp = true,
                    Serial = true
                }
            }, suppressUpdateThread: false);
            NewConnectionController.SessionCreated += NewConnectionController_SessionCreated;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("NewConnectionController"));
        }
    }

    private void NewConnectionController_SessionCreated()
    {
        BeginInvoke(() =>
        {
            DeviceTerminalSettings pendingDevice = NewConnectionController.TakePendingRawConnectionSettings();
            try
            {
                if (pendingDevice != null)
                {
                    AddDeviceSession(pendingDevice);
                }
                else
                {
                    AddSession(NewConnectionController.CreatedSession, NewConnectionController.SelectedSettings, createFirstPage: true);
                }
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
            CloseNewConnectionPopup?.Invoke();
        });
    }

    public void OnActiveTabChanged(SessionWrapper session, ITabContext activeTab)
    {
        if (session == ActiveSession && _ActiveTab != activeTab)
        {
            TerminalEmulatorControl previousTerminal = (_ActiveTab as NormalTabContext)?.Terminal;
            TerminalEmulatorControl newTerminal = (activeTab as NormalTabContext)?.Terminal;
            _ActiveTab = activeTab;
            if (previousTerminal != null)
            {
                previousTerminal.CaretWhenUnfocused = false;
                previousTerminal.LayoutSuspended = false;
                previousTerminal.LostFocus -= ActiveTerminal_LostFocus;
            }
            if (newTerminal != null)
            {
                newTerminal.CaretWhenUnfocused = true;
                newTerminal.VerticalScrollBarVisible = Settings.VerticalScrollbarMode == GlobalSettings.ScrollbarMode.Show;
                newTerminal.HorizontalScrollBarVisible = Settings.HorizontalScrollbarMode == GlobalSettings.ScrollbarMode.Show;
                newTerminal.LayoutSuspended = SuppressTerminalResizeEvents;
                newTerminal.LostFocus += ActiveTerminal_LostFocus;
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("RemoteFileSaveHeader"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SaveCommandVisibility"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ReplayVisibility"));
            MenuCommandHandlers?.OnActiveTabChanged(activeTab);
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
        SessionWrapper[] items = AllSessions.ToArray();
        foreach (SessionWrapper sessionWrapper in items)
        {
            if (!sessionWrapper.IsPlaceholder)
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
