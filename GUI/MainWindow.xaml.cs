using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.GUI.Backend;
using SmarTTY.Transport;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using VisualTTY;
using VisualTTY.DeviceTerminal;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using ListView = System.Windows.Controls.ListView;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace SmarTTY.GUI;

public partial class MainWindow : Window, IComponentConnector, IStyleConnector
{
    private readonly InitialCommand _InitialCommand;

    private DispatcherTimer _ResizeTimer = new DispatcherTimer
    {
        Interval = TimeSpan.FromMilliseconds(300.0),
        IsEnabled = false
    };

    public MainWindowController Controller;

    private ToggleButton _LastActiveNewConnectionButton;

    private GridLength? _SCPHeight = new GridLength(200.0);

    private GridLength? _FilesWidth = new GridLength(200.0);

    /// <summary>Opens the window with a COM-port or raw-TCP terminal as its first session (no SSH connection).</summary>
    public MainWindow(DeviceTerminalSettings deviceSettings)
    {
        InitializeWindow();
        _InitialCommand = default(InitialCommand);
        Controller.AddDeviceSession(deviceSettings);
        _ResizeTimer.Tick += _ResizeTimer_Tick;
    }

    private void InitializeWindow()
    {
        InitializeComponent();
        CommonGUIPreferences commonGUIPreferences = GuiPreferencesStore.Current;
        try
        {
            commonGUIPreferences?.MainWindow.Apply((Window)this);
        }
        catch
        {
        }
        this.DataContext = (Controller = new MainWindowController(this));
        Controller.CloseNewConnectionPopup += () =>
        {
            NewConnectionPopup.IsOpen = false;
        };
        Controller.OnLastSessionClosed += base.Close;
    }

    public MainWindow(SshSession session, SSHConnectionSettings connectionSettings, string initialCommand = null, bool echoInitialCommand = false)
    {
        InitializeWindow();
        _InitialCommand = new InitialCommand(initialCommand, echoInitialCommand);
        SessionWrapper sessionWrapper = Controller.AddSession(session, connectionSettings, createFirstPage: false);
        VirtualTerminalMode virtualTerminalMode = Controller.Settings.VirtualTerminal.DefaultMode;
        if (_InitialCommand.Command == null && virtualTerminalMode == VirtualTerminalMode.Ask)
        {
            VirtualTerminalIntroWindow virtualTerminalIntroWindow = new VirtualTerminalIntroWindow();
            bool? dialogResult = virtualTerminalIntroWindow.ShowDialog();
            if (dialogResult.HasValue)
            {
                virtualTerminalMode = (dialogResult.Value ? VirtualTerminalMode.Enabled : VirtualTerminalMode.Disabled);
                if (((ToggleButton)virtualTerminalIntroWindow.cbRemember).IsChecked == true)
                {
                    Controller.Settings.VirtualTerminal.DefaultMode = virtualTerminalMode;
                    Controller.Settings.Save();
                }
            }
        }
        if (_InitialCommand.Command == null && virtualTerminalMode == VirtualTerminalMode.Enabled)
        {
            sessionWrapper.CreateNewTerminalPage(virtualMode: true);
        }
        else
        {
            sessionWrapper.CreateNewTerminalPage(virtualMode: false, _InitialCommand);
        }
        _ResizeTimer.Tick += _ResizeTimer_Tick;
    }

    private void _ResizeTimer_Tick(object sender, EventArgs e)
    {
        _ResizeTimer.IsEnabled = false;
        if (Controller != null)
        {
            Controller.SuppressTerminalResizeEvents = false;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _ResizeTimer.Stop();
        _ResizeTimer.Start();
        if (Controller != null)
        {
            Controller.SuppressTerminalResizeEvents = true;
        }
    }

    private void ToggleButton_Checked(object sender, RoutedEventArgs e)
    {
        _LastActiveNewConnectionButton = sender as ToggleButton;
        if (_LastActiveNewConnectionButton != null)
        {
            Controller.LoadConnectionList();
            NewConnectionPopup.PlacementTarget = (UIElement)_LastActiveNewConnectionButton;
            ((Selector)lstConnections).SelectedItem = null;
            NewConnectionPopup.IsOpen = true;
        }
    }

    private void ToggleButton_Unchecked(object sender, RoutedEventArgs e)
    {
        NewConnectionPopup.IsOpen = false;
    }

    private void NewConnectionPopup_Closed(object sender, EventArgs e)
    {
        if (_LastActiveNewConnectionButton != null)
        {
            _LastActiveNewConnectionButton.IsChecked = false;
        }
    }

    private void NewConnectionSelected(object sender, SelectionChangedEventArgs e)
    {
        object sourceElement = ((sender is ListBox) ? sender : null);
        if (((sourceElement != null) ? ((Selector)sourceElement).SelectedItem : null) is ConnectionSelectionWindow.IConnectionSettingsWrapper wrp)
        {
            Controller.NewConnectionController.ConnectWithSettings(wrp);
        }
    }

    /// <summary>
    /// True when the window is being closed to go back to the start window (File > Back to launcher): the application then
    /// shows the launcher again instead of exiting.
    /// </summary>
    public bool ReturnToLauncher { get; private set; }

    private void BackToLauncher_Click(object sender, RoutedEventArgs e)
    {
        ReturnToLauncher = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window used to drop the unsaved changes of open remote file editors silently.
        if (!Controller.ConfirmCloseUnsavedEditors())
        {
            e.Cancel = true;
            // The user chose to stay: the window is not going back to the launcher after all.
            ReturnToLauncher = false;
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        CommonGUIPreferences commonGUIPreferences = GuiPreferencesStore.Current;
        if (commonGUIPreferences != null)
        {
            Controller.ExportPreferences(commonGUIPreferences);
            commonGUIPreferences.MainWindow.Save((Window)this);
            GuiPreferencesStore.Save();
        }
        Controller.Dispose();
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        Controller.ActiveSession?.CreateNewTerminalPage();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.OpenFile();
    }

    private void SaveFile_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.SaveFile();
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.NewSession();
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.CloseTab();
    }

    private void ReplayLog_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.ReplayLog();
    }

    private void MakeLog_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.MakeLog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.Search();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.Copy();
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.Paste();
    }

    private void SCPTransfers_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.SCPTransfers();
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.CopyAll();
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.DownloadFile();
    }

    private void QuickDownload_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.QuickDownload();
    }

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.UploadFile();
    }

    private void DownloadDir_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.DownloadDir();
    }

    private void UploadDir_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.UploadDir();
    }

    private void CopyDirBetweenHosts_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.CopyDirBetweenHosts();
    }

    private void Synchronize_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.Synchronize();
    }

    private void PublicKey_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.SetupPublicKey();
    }

    private void RunScript_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.RunScript();
    }

    private void CheckX11_Click(object sender, RoutedEventArgs e)
    {
        SessionWrapper activeSession = Controller.ActiveSession;
        if (activeSession == null || activeSession.IsDevice || activeSession.Session == null)
        {
            return;
        }
        // The check runs commands on the server.
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string report;
            bool offerInstall = false;
            try
            {
                report = activeSession.DescribeX11Forwarding();
                // Forwarded programs need an X server on this computer: offer to get it when it is not there.
                offerInstall = VcxsrvSetup.FindExecutable() == null;
            }
            catch (Exception exception)
            {
                report = "The X11 check failed: " + exception.Message;
            }
            Controller.BeginInvoke(() =>
            {
                if (offerInstall && MessageBoxes.Ask(report + "\n\nVcXsrv is not installed. Download and install the latest version now? The download runs in the background; the installer then asks for administrator rights."))
                {
                    InstallVcxsrvInBackground();
                }
                else
                {
                    MessageBoxes.Info(report);
                }
            });
        });
    }

    private void InstallVcxsrvInBackground()
    {
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string result;
            bool failed = false;
            try
            {
                result = VcxsrvSetup.DescribeInstallation(VcxsrvSetup.DownloadAndInstall(step => { }, System.Threading.CancellationToken.None));
            }
            catch (OperationCanceledException exception)
            {
                result = exception.Message;
                failed = true;
            }
            catch (Exception exception)
            {
                result = exception.Message + " You can download VcXsrv manually from " + VcxsrvSetup.ReleasesPage + ".";
                failed = true;
            }
            Controller.BeginInvoke(() =>
            {
                if (failed)
                {
                    MessageBoxes.Error(result);
                }
                else
                {
                    MessageBoxes.Info(result);
                }
            });
        });
    }

    private void TftpFirmware_Click(object sender, RoutedEventArgs e)
    {
        DeviceTerminalControl terminal = Controller.ActiveSession?.DeviceTab?.Terminal;
        if (terminal == null)
        {
            return;
        }
        try
        {
            terminal.ShowTftpFirmwareWindow();
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void CommandCompletionsEditor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommandCompletionsEditorWindow.Open(this);
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.Settings();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.About();
    }

    private void SearchUp_Click(object sender, RoutedEventArgs e)
    {
        object sourceElement = ((sender is FrameworkElement) ? sender : null);
        (((sourceElement != null) ? ((FrameworkElement)sourceElement).DataContext : null) as TextSearcher)?.Search(up: true);
    }

    private void SearchDown_Click(object sender, RoutedEventArgs e)
    {
        object sourceElement = ((sender is FrameworkElement) ? sender : null);
        (((sourceElement != null) ? ((FrameworkElement)sourceElement).DataContext : null) as TextSearcher)?.Search(up: false);
    }

    private void Border_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Border border = sender as Border;
        bool isVisible = false;
        int value;
        if (border != null)
        {
            object newValue = e.NewValue;
            if (newValue is bool)
            {
                isVisible = (bool)newValue;
                value = 1;
            }
            else
            {
                value = 0;
            }
        }
        else
        {
            value = 0;
        }
        if (((uint)value & (isVisible ? 1u : 0u)) == 0)
        {
            return;
        }
        IInputElement focusedElement = FocusManager.GetFocusedElement((DependencyObject)border);
        TextBox edit = focusedElement as TextBox;
        if (edit != null)
        {
            ((TextBoxBase)edit).SelectAll();
            this.Dispatcher.BeginInvoke((Delegate)(Action)(() =>
            {
                FocusManager.SetFocusedElement((DependencyObject)this, (IInputElement)edit);
            }), DispatcherPriority.ContextIdle, new object[0]);
        }
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e)
    {
        object sourceElement = ((sender is FrameworkElement) ? sender : null);
        (((sourceElement != null) ? ((FrameworkElement)sourceElement).DataContext : null) as TextSearcher)?.Hide();
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        ((sender as FrameworkElement)?.DataContext as TextSearcher)?.Replace();
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        ((sender as FrameworkElement)?.DataContext as TextSearcher)?.ReplaceAll();
    }

    private void txtReplace_KeyDown(object sender, KeyEventArgs e)
    {
        TextSearcher searcher = (sender as FrameworkElement)?.DataContext as TextSearcher;
        if (e.Key == Key.Enter)
        {
            searcher?.Replace();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            searcher?.Hide();
            e.Handled = true;
        }
    }

    private void txtSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if ((int)e.Key == 6)
        {
            object sourceElement = ((sender is FrameworkElement) ? sender : null);
            (((sourceElement != null) ? ((FrameworkElement)sourceElement).DataContext : null) as TextSearcher)?.Search(up: false);
        }
        else if ((int)e.Key == 13)
        {
            object hiddenSourceElement = ((sender is FrameworkElement) ? sender : null);
            (((hiddenSourceElement != null) ? ((FrameworkElement)hiddenSourceElement).DataContext : null) as TextSearcher)?.Hide();
        }
        else if ((int)e.Key == 114)
        {
            // F3 searches down (forward), Shift+F3 searches up (backward), exactly like the two arrow buttons.
            ModifierKeys f3Modifiers = (e.KeyboardDevice != null) ? e.KeyboardDevice.Modifiers : ModifierKeys.None;
            bool searchUp = (f3Modifiers & ModifierKeys.Shift) != 0;
            object sourceElement = ((sender is FrameworkElement) ? sender : null);
            (((sourceElement != null) ? ((FrameworkElement)sourceElement).DataContext : null) as TextSearcher)?.Search(up: searchUp);
        }
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (!FileListControl.IsDraggingOut && e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length != 0)
        {
            SessionWrapper dropSession = Controller.ActiveSession;
            if (dropSession != null && !dropSession.IsDevice)
            {
                // As for drops on the terminal: let the drop finish first, so that Explorer is not kept waiting while the
                // remote directory is determined and the overwrite question is shown.
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        dropSession.UploadFiles(files);
                    }
                    catch (Exception exception)
                    {
                        MessageBoxes.Exception(exception);
                    }
                }));
            }
        }
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (!FileListControl.IsDraggingOut && e.Data.GetDataPresent(DataFormats.FileDrop) && Controller.ActiveSession?.IsDevice != true)
        {
            e.Effects = (DragDropEffects)1;
        }
        else
        {
            e.Effects = (DragDropEffects)0;
        }
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            object sourceElement = ((sender is FrameworkElement) ? sender : null);
            (((sourceElement != null) ? ((FrameworkElement)sourceElement).DataContext : null) as ICloseableElement)?.Close();
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    /// <summary>Shows the tab menu only for real tabs and enables what applies to the clicked one.</summary>
    private void TabHeader_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        try
        {
            FrameworkElement header = sender as FrameworkElement;
            ITabContext tab = header?.DataContext as ITabContext;
            ContextMenu menu = header?.ContextMenu;
            SessionWrapper session = Controller.ActiveSession;
            if (tab == null || tab is PseudoTabContext || menu == null)
            {
                e.Handled = true;
                return;
            }
            // The menu is outside the visual tree, so its data context is set explicitly.
            menu.DataContext = tab;
            foreach (MenuItem item in menu.Items.OfType<MenuItem>())
            {
                switch (item.Tag as string)
                {
                    case "CopyPath":
                        item.IsEnabled = tab is EditorTabContext;
                        break;
                    case "CloseOthers":
                        item.IsEnabled = tab is ICloseableElement && session != null && session.Tabs.Any(other => other != tab && other is ICloseableElement);
                        break;
                    case "SaveAll":
                        item.IsEnabled = session != null && session.Tabs.OfType<EditorTabContext>().Any(editorTab => editorTab.Editor.IsModified || editorTab.Editor.IsNewFile);
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            e.Handled = true;
            MessageBoxes.Exception(exception);
        }
    }

    private void TabContextMenu_CopyPath(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is EditorTabContext editorTab && !string.IsNullOrEmpty(editorTab.FilePath))
            {
                System.Windows.Clipboard.SetText(editorTab.FilePath);
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void TabContextMenu_CloseOthers(object sender, RoutedEventArgs e)
    {
        try
        {
            SessionWrapper session = Controller.ActiveSession;
            ITabContext tab = (sender as FrameworkElement)?.DataContext as ITabContext;
            if (session == null || tab == null || !session.Tabs.Contains(tab))
            {
                return;
            }
            // Keep the clicked tab active while the others ask their questions and go away.
            foreach (ITabContext other in session.Tabs.ToList())
            {
                if (other == tab || !(other is ICloseableElement closeable))
                {
                    continue;
                }
                session.ActiveTabIndex = session.Tabs.IndexOf(tab);
                closeable.Close();
                if (session.Tabs.Contains(other))
                {
                    // The user cancelled the close, or the file could not be saved.
                    break;
                }
            }
            int index = session.Tabs.IndexOf(tab);
            if (index >= 0)
            {
                session.ActiveTabIndex = index;
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void TabContextMenu_SaveAll(object sender, RoutedEventArgs e)
    {
        try
        {
            SessionWrapper session = Controller.ActiveSession;
            if (session == null)
            {
                return;
            }
            foreach (EditorTabContext editorTab in session.Tabs.OfType<EditorTabContext>().ToList())
            {
                if (editorTab.Editor.IsModified || editorTab.Editor.IsNewFile)
                {
                    editorTab.Editor.SaveInBackground();
                }
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    // Minimizes the main application window when the "collapse" button on a device (COM/raw TCP)
    // session tab header is pressed. The button is only shown on device sessions (see TabMinimizeButton).
    private void MinimizeDeviceTabWindow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement element)
            {
                Window.GetWindow(element).WindowState = WindowState.Minimized;
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void SCPVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        object newValue = e.NewValue;
        bool isVisible = false;
        int value;
        if (newValue is bool)
        {
            isVisible = (bool)newValue;
            value = 1;
        }
        else
        {
            value = 0;
        }
        if (((uint)value & (isVisible ? 1u : 0u)) != 0)
        {
            if (_SCPHeight.HasValue)
            {
                SCPRow.Height = _SCPHeight.Value;
            }
        }
        else
        {
            _SCPHeight = SCPRow.Height;
            SCPRow.Height = new GridLength(0.0);
        }
    }

    private void FilesVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        object newValue = e.NewValue;
        bool isVisible = false;
        int value;
        if (newValue is bool)
        {
            isVisible = (bool)newValue;
            value = 1;
        }
        else
        {
            value = 0;
        }
        if (((uint)value & (isVisible ? 1u : 0u)) != 0)
        {
            if (_FilesWidth.HasValue)
            {
                FilesColumn.Width = _FilesWidth.Value;
            }
        }
        else
        {
            _FilesWidth = FilesColumn.Width;
            FilesColumn.Width = new GridLength(0.0);
        }
    }

    private void CancelTransfer(object sender, RoutedEventArgs e)
    {
        object sourceElement = ((sender is FrameworkElement) ? sender : null);
        (((sourceElement != null) ? ((FrameworkElement)sourceElement).DataContext : null) as SCPController.TransferObject)?.Cancel();
    }

    private void ListView_KeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if ((int)e.Key == 32)
            {
                SCPController.TransferObject[] items = ((ListBox)((sender is ListView) ? sender : null)).SelectedItems.OfType<SCPController.TransferObject>().ToArray();
                for (int arrayIndex = 0; arrayIndex < items.Length; arrayIndex++)
                {
                    items[arrayIndex].Cancel();
                }
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void RemoveCompleted_Click(object sender, RoutedEventArgs e)
    {
        object sourceElement = ((sender is FrameworkElement) ? sender : null);
        (((sourceElement != null) ? ((FrameworkElement)sourceElement).DataContext : null) as SCPController)?.RemoveCompleted();
    }

    private void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        Controller.ActiveSession?.SCPController?.RetryFailed();
    }

    private void RetryAll_Click(object sender, RoutedEventArgs e)
    {
        Controller.ActiveSession?.SCPController?.RetryFailed(evenIfCompleted: true);
    }

    private void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        SCPController controller = Controller.ActiveSession?.SCPController;
        if (controller == null || controller.ActiveTransfers.Count == 0)
        {
            return;
        }
        bool unfinished = controller.ActiveTransfers.Any((SCPController.TransferObject transferObject) => transferObject.Status == FileTransferManager.TransferState.InProgress || transferObject.Status == FileTransferManager.TransferState.Queued);
        if (unfinished && !MessageBoxes.AskWarn("Some transfers are not finished yet. Cancel them and clear the list?"))
        {
            return;
        }
        controller.RemoveAll();
    }

    private void SelectedXfers_RetryAgain(object sender, RoutedEventArgs e)
    {
        RunForSelecterXfers((SCPController.TransferObject xf) =>
        {
            xf.Retry(evenIfCompleted: true);
        });
    }

    private void SelectedXfers_CopyRemotePath(object sender, RoutedEventArgs e)
    {
        RunForSelecterXfers((SCPController.TransferObject xf) =>
        {
            xf.CopyRemotePath();
        }, onlyFirst: true);
    }

    private void SelectedXfers_DeleteUploaded(object sender, RoutedEventArgs e)
    {
        SCPController.TransferObject[] uploads = ((ListBox)TransferList).SelectedItems.OfType<SCPController.TransferObject>().Where((SCPController.TransferObject xf) => !xf.IsDownload && xf.Status == FileTransferManager.TransferState.Completed).ToArray();
        if (uploads.Length == 0)
        {
            return;
        }
        string question = (uploads.Length == 1) ? "Delete " + uploads[0].RemoteFile + " on the server?" : "Delete these " + uploads.Length + " files on the server?\n\n" + string.Join("\n", uploads.Take(15).Select((SCPController.TransferObject xf) => xf.RemoteFile)) + ((uploads.Length > 15) ? "\n..." : "");
        if (!MessageBoxes.AskWarn(question + "\n\nThis cannot be undone."))
        {
            return;
        }
        foreach (SCPController.TransferObject upload in uploads)
        {
            if (!upload.DeleteUploadedFile(ask: false))
            {
                break;
            }
        }
    }

    private void SelectedXfers_DeleteDownloaded(object sender, RoutedEventArgs e)
    {
        SCPController.TransferObject[] downloads = ((ListBox)TransferList).SelectedItems.OfType<SCPController.TransferObject>().Where((SCPController.TransferObject xf) => xf.IsDownload && xf.Status == FileTransferManager.TransferState.Completed).ToArray();
        if (downloads.Length == 0)
        {
            return;
        }
        string question = (downloads.Length == 1) ? "Delete " + downloads[0].LocalFile + " on this computer?" : "Delete these " + downloads.Length + " files on this computer?\n\n" + string.Join("\n", downloads.Take(15).Select((SCPController.TransferObject xf) => xf.LocalFile)) + ((downloads.Length > 15) ? "\n..." : "");
        if (!MessageBoxes.AskWarn(question + "\n\nThis cannot be undone."))
        {
            return;
        }
        foreach (SCPController.TransferObject download in downloads)
        {
            if (!download.DeleteLocalFile(ask: false))
            {
                break;
            }
        }
    }

    private void RunForSelecterXfers(Action<SCPController.TransferObject> action, bool onlyFirst = false)
    {
        SCPController.TransferObject[] items = ((ListBox)TransferList).SelectedItems.OfType<SCPController.TransferObject>().ToArray();
        foreach (SCPController.TransferObject sender in items)
        {
            action(sender);
            if (onlyFirst)
            {
                break;
            }
        }
    }

    private void SelectedXfers_Cancel(object sender, RoutedEventArgs e)
    {
        RunForSelecterXfers((SCPController.TransferObject xf) =>
        {
            xf.Cancel();
        });
    }

    private void SelectedXfers_Retry(object sender, RoutedEventArgs e)
    {
        RunForSelecterXfers((SCPController.TransferObject xf) =>
        {
            xf.Retry();
        });
    }

    private void SelectedXfers_OpenLocal(object sender, RoutedEventArgs e)
    {
        RunForSelecterXfers((SCPController.TransferObject xf) =>
        {
            xf.OpenLocal();
        }, onlyFirst: true);
    }

    private void SelectedXfers_OpenFolder(object sender, RoutedEventArgs e)
    {
        RunForSelecterXfers((SCPController.TransferObject xf) =>
        {
            xf.OpenFolder();
        }, onlyFirst: true);
    }

    private void SelectedXfers_CopyLocalPath(object sender, RoutedEventArgs e)
    {
        RunForSelecterXfers((SCPController.TransferObject xf) =>
        {
            xf.CopyLocalPath();
        }, onlyFirst: true);
    }

    private void TransferList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SCPController.TransferObject[] items = ((ListBox)TransferList).SelectedItems.OfType<SCPController.TransferObject>().ToArray();
        if (items.Length == 0)
        {
            xferMenu.Visibility = Visibility.Collapsed;
            return;
        }
        xferMenu.Visibility = Visibility.Visible;
        cmdCancel.Visibility = ((items.FirstOrDefault((SCPController.TransferObject transferObject) => transferObject.Status == FileTransferManager.TransferState.InProgress || transferObject.Status == FileTransferManager.TransferState.Queued) == null) ? Visibility.Collapsed : Visibility.Visible);
        cmdRetry.Visibility = ((items.FirstOrDefault((SCPController.TransferObject transferObject) => transferObject.Status == FileTransferManager.TransferState.Failed) == null) ? Visibility.Collapsed : Visibility.Visible);
        cmdRetryAgain.Visibility = items.Any((SCPController.TransferObject transferObject) => transferObject.Status == FileTransferManager.TransferState.Completed) ? Visibility.Visible : Visibility.Collapsed;
        Visibility deleteVisibility = items.Any((SCPController.TransferObject transferObject) => !transferObject.IsDownload && transferObject.Status == FileTransferManager.TransferState.Completed) ? Visibility.Visible : Visibility.Collapsed;
        cmdDeleteUploaded.Visibility = deleteVisibility;
        sepDeleteUploaded.Visibility = deleteVisibility;
        cmdDeleteDownloaded.Visibility = items.Any((SCPController.TransferObject transferObject) => transferObject.IsDownload && transferObject.Status == FileTransferManager.TransferState.Completed) ? Visibility.Visible : Visibility.Collapsed;
    }

    public void UpdateContextMenu()
    {
        TransferList_SelectionChanged(null, null);
    }

    private void FileListControl_FileActionRequested(object sender, FileListControl.FileEventArgs e)
    {
        try
        {
            switch (e.Event)
            {
                case FileListControl.FileEvent.Open:
                    Controller.MenuCommandHandlers.CreateNewEditorTab(Controller.ActiveSession, e.FullPath);
                    break;
                case FileListControl.FileEvent.Download:
                    Controller.MenuCommandHandlers.QuickDownloadMany(e.FullPaths, archives: false);
                    break;
                case FileListControl.FileEvent.Run:
                    Controller.MenuCommandHandlers.RunRemoteScript(e.FullPath);
                    break;
                case FileListControl.FileEvent.DownloadArchive:
                    Controller.MenuCommandHandlers.QuickDownloadMany(e.FullPaths, archives: true);
                    break;
                case FileListControl.FileEvent.InstallPackage:
                    Controller.MenuCommandHandlers.InstallRemotePackages(e.FullPaths);
                    break;
                case FileListControl.FileEvent.Upload:
                    {
                        SessionWrapper dropSession = Controller.ActiveSession;
                        string[] files = e.LocalFiles;
                        string remoteDir = e.FullPath;
                        // Like other drops: let the drop finish first, so that Explorer is not kept waiting.
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            try
                            {
                                dropSession?.UploadFiles(files, remoteDir: remoteDir);
                            }
                            catch (Exception exception)
                            {
                                MessageBoxes.Exception(exception);
                            }
                        }));
                        break;
                    }
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void FileListControl_FocusRequested(object sender, EventArgs e)
    {
        if (sender is FileListControl fileListControl)
        {
            FocusManager.SetFocusedElement((DependencyObject)this, (IInputElement)fileListControl);
        }
    }

    private void ManagePackages_Click(object sender, RoutedEventArgs e)
    {
        SessionWrapper activeSession = Controller.ActiveSession;
        if (activeSession == null || activeSession.IsDevice)
        {
            return;
        }
        ShowPackageManager(activeSession);
    }

    private void ProcessManager_Click(object sender, RoutedEventArgs e)
    {
        SessionWrapper activeSession = Controller.ActiveSession;
        if (activeSession == null || activeSession.IsDevice || activeSession.Session == null)
        {
            return;
        }
        try
        {
            ProcessManagerWindow.ShowFor(activeSession, this);
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void SocksProxy_Click(object sender, RoutedEventArgs e)
    {
        SessionWrapper activeSession = Controller.ActiveSession;
        if (activeSession == null || activeSession.IsDevice || activeSession.Session == null)
        {
            return;
        }
        try
        {
            new SocksProxyWindow(activeSession)
            {
                Owner = this
            }.ShowDialog();
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void LocalRepo_Click(object sender, RoutedEventArgs e)
    {
        SessionWrapper activeSession = Controller.ActiveSession;
        if (activeSession == null || activeSession.IsDevice || activeSession.Session == null)
        {
            return;
        }
        try
        {
            LocalRepoWindow localRepoWindow = new LocalRepoWindow(activeSession)
            {
                Owner = this
            };
            localRepoWindow.ShowDialog();
            if (localRepoWindow.OpenPackageManagerRequested)
            {
                ShowPackageManager(activeSession);
            }
            else if (localRepoWindow.FileToEdit != null)
            {
                Controller.MenuCommandHandlers.CreateNewEditorTab(activeSession, localRepoWindow.FileToEdit);
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void ShowPackageManager(SessionWrapper activeSession)
    {
        try
        {
            PackageManagerWindow packageManagerWindow = new PackageManagerWindow(activeSession);
            packageManagerWindow.Owner = (Window)this;
            PackageManagerWindow packageManagerWindow2 = packageManagerWindow;
            if (packageManagerWindow2.ShowDialog() == true && packageManagerWindow2.CommandLine != null && (!(activeSession.ActiveTab.EditorControl is SshTerminalControl sshTerminalControl) || !sshTerminalControl.Submit(packageManagerWindow2.CommandLine)))
            {
                activeSession.CreateNewTerminalPage(virtualMode: false, new InitialCommand(packageManagerWindow2.CommandLine, echoInitialCommand: true, tabKeptAfterExit: () => ShowPackageManager(activeSession)));
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Ctrl+F opens the shared search bar for any active tab. It is intercepted here, before the terminal sees
        // the key, so Ctrl+F is never sent to the remote side (SSH shell, COM port or raw TCP connection).
        if ((int)e.Key == 70)
        {
            ModifierKeys modifiers = (e.KeyboardDevice != null) ? e.KeyboardDevice.Modifiers : ModifierKeys.None;
            if ((modifiers & ModifierKeys.Control) != 0 && (modifiers & ModifierKeys.Alt) == 0)
            {
                Controller.MenuCommandHandlers.Search();
                ((RoutedEventArgs)e).Handled = true;
                return;
            }
        }
        // COM/TCP tabs follow the same F10 and Alt+letter rules as SSH terminals (the shared "Action for Alt+letter" option).
        ITabContext activeTab = Controller.ActiveSession?.ActiveTab;
        if ((activeTab?.EditorControl as TerminalEmulatorControl ?? (activeTab as DeviceTabContext)?.Terminal.FocusedTextTerminal) is TerminalEmulatorControl terminalEmulatorControl)
        {
            if ((int)e.Key == 156 && (int)e.SystemKey == 99)
            {
                ((RoutedEventArgs)e).Handled = true;
                terminalEmulatorControl?.SendKey(new System.Windows.Forms.KeyEventArgs(Keys.F10));
            }
            if ((int)e.Key == 156 && (int)e.SystemKey != 120 && (int)e.SystemKey != 116)
            {
                char ch = '\0';
                bool shift = e.KeyboardDevice != null && (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0;
                if ((int)e.SystemKey >= 44 && (int)e.SystemKey <= 69)
                {
                    // Alt+Shift+letter sends ESC and the capital letter (Meta-B is not Meta-b in readline).
                    int value = (int)e.SystemKey - 44;
                    ch = (char)((shift ? 65 : 97) + value);
                }
                else if ((int)e.SystemKey >= 34 && (int)e.SystemKey <= 43 && !shift)
                {
                    // Alt+digit (Key.D0..Key.D9). With Alt held the key arrives as Key.System, so the digit must be
                    // read from SystemKey; checking Key never matched and Alt+digit was never sent.
                    int currentValue = (int)e.SystemKey - 34;
                    ch = (char)(48 + currentValue);
                }
                if (ch != '\0' && terminalEmulatorControl != null && terminalEmulatorControl.SendAltChar(ch))
                {
                    ((RoutedEventArgs)e).Handled = true;
                    return;
                }
            }
        }
        base.OnPreviewKeyDown(e);
    }
}
