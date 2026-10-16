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
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using ListView = System.Windows.Controls.ListView;
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

    public readonly MainWindowController Controller;

    private ToggleButton _LastActiveNewConnectionButton;

    internal ResourceDictionary _ThemeDictionary;

    private GridLength? _SCPHeight = new GridLength(200.0);

    private GridLength? _FilesWidth = new GridLength(200.0);

    public MainWindow(SshSession session, SSHConnectionSettings connectionSettings, string initialCommand = null, bool echoInitialCommand = false)
    {
        ApplyTheme((Window)this, SmarTTYTheme.Dark, ref _ThemeDictionary);
        InitializeComponent();
        CommonGUIPreferences commonGUIPreferences = GuiPreferencesStore.Current;
        try
        {
            commonGUIPreferences?.MainWindow.Apply((Window)this);
        }
        catch
        {
        }
        _InitialCommand = new InitialCommand(initialCommand, echoInitialCommand);
        this.DataContext = (Controller = new MainWindowController(this));
        Controller.CloseNewConnectionPopup += () =>
        {
            NewConnectionPopup.IsOpen = false;
        };
        Controller.OnLastSessionClosed += base.Close;
        SessionWrapper sessionWrapper = Controller.AddSession(session, connectionSettings, createFirstPage: false);
        VirtualTerminalMode virtualTerminalMode = Controller.Settings.VirtualTerminal.DefaultMode;
        if (_InitialCommand.Command == null && virtualTerminalMode == VirtualTerminalMode.Ask)
        {
            VirtualTerminalIntroWindow virtualTerminalIntroWindow = new VirtualTerminalIntroWindow(Controller.Settings);
            bool? flag = virtualTerminalIntroWindow.ShowDialog();
            if (flag.HasValue)
            {
                virtualTerminalMode = (flag.Value ? VirtualTerminalMode.Enabled : VirtualTerminalMode.Disabled);
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
        object obj = ((sender is ListBox) ? sender : null);
        if (((obj != null) ? ((Selector)obj).SelectedItem : null) is ConnectionSelectionWindow.IConnectionSettingsWrapper wrp)
        {
            Controller.NewConnectionController.ConnectWithSettings(wrp);
        }
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

    private void PublicKey_Click(object sender, RoutedEventArgs e)
    {
        Controller.MenuCommandHandlers.SetupPublicKey();
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
        object obj = ((sender is FrameworkElement) ? sender : null);
        (((obj != null) ? ((FrameworkElement)obj).DataContext : null) as TextSearcher)?.Search(up: true);
    }

    private void SearchDown_Click(object sender, RoutedEventArgs e)
    {
        object obj = ((sender is FrameworkElement) ? sender : null);
        (((obj != null) ? ((FrameworkElement)obj).DataContext : null) as TextSearcher)?.Search(up: false);
    }

    private void Border_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Border border = sender as Border;
        bool flag = false;
        int num;
        if (border != null)
        {
            object newValue = e.NewValue;
            if (newValue is bool)
            {
                flag = (bool)newValue;
                num = 1;
            }
            else
            {
                num = 0;
            }
        }
        else
        {
            num = 0;
        }
        if (((uint)num & (flag ? 1u : 0u)) == 0)
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
        object obj = ((sender is FrameworkElement) ? sender : null);
        (((obj != null) ? ((FrameworkElement)obj).DataContext : null) as TextSearcher)?.Hide();
    }

    private void txtSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if ((int)e.Key == 6)
        {
            object obj = ((sender is FrameworkElement) ? sender : null);
            (((obj != null) ? ((FrameworkElement)obj).DataContext : null) as TextSearcher)?.Search(up: false);
        }
        else if ((int)e.Key == 13)
        {
            object obj2 = ((sender is FrameworkElement) ? sender : null);
            (((obj2 != null) ? ((FrameworkElement)obj2).DataContext : null) as TextSearcher)?.Hide();
        }
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            Controller.ActiveSession?.UploadFiles(files);
        }
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
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
            object obj = ((sender is FrameworkElement) ? sender : null);
            (((obj != null) ? ((FrameworkElement)obj).DataContext : null) as ICloseableElement)?.Close();
        }
        catch (Exception ex)
        {
            MessageBoxes.Exception(ex);
        }
    }

    public static void ApplyTheme(Window wnd, SmarTTYTheme theme, ref ResourceDictionary themeDictionary)
    {
        ResourceDictionary resourceDictionary = new ResourceDictionary();
        switch (theme)
        {
            case SmarTTYTheme.Dark:
                resourceDictionary.Source = new Uri("pack://application:,,,/GUI/Styles/Dark/Style.xaml");
                break;
            case SmarTTYTheme.Light:
                resourceDictionary.Source = new Uri("pack://application:,,,/GUI/Styles/Light/Style.xaml");
                break;
        }
        if (themeDictionary != null)
        {
            ((FrameworkElement)wnd).Resources.MergedDictionaries.Remove(themeDictionary);
        }
        themeDictionary = resourceDictionary;
        ((FrameworkElement)wnd).Resources.MergedDictionaries.Add(resourceDictionary);
    }

    private void SCPVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        object newValue = e.NewValue;
        bool flag = false;
        int num;
        if (newValue is bool)
        {
            flag = (bool)newValue;
            num = 1;
        }
        else
        {
            num = 0;
        }
        if (((uint)num & (flag ? 1u : 0u)) != 0)
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
        bool flag = false;
        int num;
        if (newValue is bool)
        {
            flag = (bool)newValue;
            num = 1;
        }
        else
        {
            num = 0;
        }
        if (((uint)num & (flag ? 1u : 0u)) != 0)
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
        object obj = ((sender is FrameworkElement) ? sender : null);
        (((obj != null) ? ((FrameworkElement)obj).DataContext : null) as SCPController.TransferObject)?.Cancel();
    }

    private void ListView_KeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if ((int)e.Key == 32)
            {
                SCPController.TransferObject[] array = ((ListBox)((sender is ListView) ? sender : null)).SelectedItems.OfType<SCPController.TransferObject>().ToArray();
                for (int num = 0; num < array.Length; num++)
                {
                    array[num].Cancel();
                }
            }
        }
        catch (Exception ex)
        {
            MessageBoxes.Exception(ex);
        }
    }

    private void RemoveCompleted_Click(object sender, RoutedEventArgs e)
    {
        object obj = ((sender is FrameworkElement) ? sender : null);
        (((obj != null) ? ((FrameworkElement)obj).DataContext : null) as SCPController)?.RemoveCompleted();
    }

    private void RunForSelecterXfers(Action<SCPController.TransferObject> action, bool onlyFirst = false)
    {
        SCPController.TransferObject[] array = ((ListBox)TransferList).SelectedItems.OfType<SCPController.TransferObject>().ToArray();
        foreach (SCPController.TransferObject obj in array)
        {
            action(obj);
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
        SCPController.TransferObject[] array = ((ListBox)TransferList).SelectedItems.OfType<SCPController.TransferObject>().ToArray();
        if (array.Length == 0)
        {
            xferMenu.Visibility = Visibility.Collapsed;
            return;
        }
        xferMenu.Visibility = Visibility.Visible;
        cmdCancel.Visibility = ((array.FirstOrDefault((SCPController.TransferObject transferObject) => transferObject.Status == ScpTransferManager.TransferState.InProgress || transferObject.Status == ScpTransferManager.TransferState.Queued) == null) ? Visibility.Collapsed : Visibility.Visible);
        cmdRetry.Visibility = ((array.FirstOrDefault((SCPController.TransferObject transferObject) => transferObject.Status == ScpTransferManager.TransferState.Failed) == null) ? Visibility.Collapsed : Visibility.Visible);
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
                    Controller.MenuCommandHandlers.QuickDownload(e.FullPath);
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBoxes.Exception(ex);
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
        if (activeSession == null)
        {
            return;
        }
        try
        {
            PackageManagerWindow packageManagerWindow = new PackageManagerWindow(activeSession);
            packageManagerWindow.Owner = (Window)this;
            PackageManagerWindow packageManagerWindow2 = packageManagerWindow;
            ApplyTheme((Window)packageManagerWindow2, Controller.Settings.Theme, ref packageManagerWindow2._ThemeDictionary);
            if (packageManagerWindow2.ShowDialog() == true && packageManagerWindow2.CommandLine != null && (!(activeSession.ActiveTab.EditorControl is SshTerminalControl sshTerminalControl) || !sshTerminalControl.Submit(packageManagerWindow2.CommandLine)))
            {
                activeSession.CreateNewTerminalPage(virtualMode: false, new InitialCommand(packageManagerWindow2.CommandLine, echoInitialCommand: true));
            }
        }
        catch (Exception ex)
        {
            MessageBoxes.Exception(ex);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Controller.ActiveSession?.ActiveTab?.EditorControl is TerminalEmulatorControl terminalEmulatorControl)
        {
            if ((int)e.Key == 156 && (int)e.SystemKey == 99)
            {
                ((RoutedEventArgs)e).Handled = true;
                terminalEmulatorControl?.SendKey(new System.Windows.Forms.KeyEventArgs(Keys.F10));
            }
            if ((int)e.Key == 156 && (int)e.SystemKey != 120 && (int)e.SystemKey != 116)
            {
                char ch = '\0';
                if ((int)e.SystemKey >= 44 && (int)e.SystemKey <= 69)
                {
                    int num = (int)e.SystemKey - 44;
                    ch = (char)(97 + num);
                }
                else if ((int)e.Key >= 34 && (int)e.Key <= 43)
                {
                    int num2 = (int)e.SystemKey - 34;
                    ch = (char)(48 + num2);
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

    internal static void ApplyTheme(VirtualTerminalIntroWindow virtualTerminalIntroWindow, SmarTTYTheme theme, ref object themeDictionary)
    {
        throw new NotImplementedException();
    }
}
