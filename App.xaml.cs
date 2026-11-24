using System;
using System.Linq;
using System.Windows;
using SmarTTY.Controls;
using SmarTTY.GUI;
using SmarTTY.GUI.Styles;
using VisualTTY;
using VisualTTY.Backend;
using SmarTTY.Ssh;

namespace SmarTTY;

public partial class App : Application
{
    public App()
    {
        // The default font of WinForms is Segoe UI 9pt since .NET Core 3.0; the layouts of the designer-built controls
        // were made for Microsoft Sans Serif 8.25pt (the .NET Framework default), so keep it.
        System.Windows.Forms.Application.SetDefaultFont(new System.Drawing.Font("Microsoft Sans Serif", 8.25f));
        System.Windows.Forms.Application.EnableVisualStyles();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Legacy code pages (cp866, koi8-r, windows-1251) are not built into .NET; the terminal encodings need them from the start.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        base.OnStartup(e);
        CrashLog.Install(this);
        // The theme lives in Application.Resources, so it has to be there before the first window loads its XAML.
        ThemeManager.Apply(GlobalSettings.Load());

        // The message boxes, file choosers and folder chooser of the code that cannot reference GUI/ (and of the rest) are the WPF windows.
        UserDialogs.MessageImpl = MessageDialog.Ask;
        UserDialogs.FileImpl = LocalFileDialogWindow.Show;
        // The code asks on the UI thread, so showing a modal window is safe.
        UserDialogs.FolderImpl = (description, initialPath) => LocalFolderBrowserWindow.Browse(initialPath, title: null, description: description);

        // The proxy prompt is raised from the harness-compiled connection controller, which cannot reference the WPF
        // dialog; the window is wired in here instead.
        NewConnectionController.ProxyCredentialsPrompt = (string realm, string credentials) =>
        {
            string entered = credentials;
            return ProxyAuthWindow.ShowPrompt(WindowOwnership.ActiveWindow(), realm, ref entered) ? entered : null;
        };
        // The host-key prompts are raised from SshConnector, which is compiled by the Mono test harness too.
        SshConnector.NewHostKeyPrompt = (settings, fingerprint) => NewHostKeyWindow.Confirm(settings, fingerprint);
        SshConnector.HostKeyMismatchPrompt = (settings, reason) => HostKeyMismatchWindow.Confirm(settings, reason);
        SshConnector.KeyboardInteractivePrompt = (settings, name, instruction, prompts) =>
            KeyboardInteractiveWindow.Prompt(WindowOwnership.ActiveWindow(), settings, name, instruction, prompts);
        SshConnector.KnownHostsProblemNotice = message => MessageBoxes.Warning(message);
        // A settings or profile file that could not be read or saved (see XmlStorage); one that was met before this point is shown now.
        // Saves can come from any thread.
        SmarTTY.Utilities.XmlStorage.ProblemNotice = message => Dispatcher.BeginInvoke((Action)(() => MessageBoxes.Warning(message)));
        // The device terminal (COM/TCP/telnet) raises its dialogs from harness-compiled code as well.
        SmarTTY.GUI.DevicePanel.WpfDeviceTerminal.LogReplayPrompt = (bytes, terminal) => LogReplayWindow.Replay(bytes, terminal);
        SmarTTY.GUI.DevicePanel.WpfDeviceTerminal.SerialSettingsPrompt = (settings, portName) =>
            SerialPortSettingsWindow.Prompt(WindowOwnership.ActiveWindow(), settings, portName);
        SmarTTY.GUI.DevicePanel.WpfDeviceTerminal.TransferProgressWindowFactory = (title, showErrorCount) => XModemTransferWindow.Create(title, showErrorCount);
        SmarTTY.GUI.DevicePanel.WpfDeviceTerminal.GzipBase64RequestPrompt = sending => sending
            ? GzipBase64TransferWindow.ShowSend(WindowOwnership.ActiveWindow())
            : GzipBase64TransferWindow.ShowReceive(WindowOwnership.ActiveWindow());
        SmarTTY.GUI.DevicePanel.WpfDeviceTerminal.FileDropProtocolPrompt = files => FileTransferProtocolWindow.Ask(WindowOwnership.ActiveWindow(), files);
        SmarTTY.GUI.DevicePanel.WpfDeviceTerminal.GzipBase64DropPrompt = file => GzipBase64TransferWindow.ShowSend(WindowOwnership.ActiveWindow(), file);
        SmarTTY.GUI.DevicePanel.WpfDeviceTerminal.GzipCatRequestPrompt = sending => sending
            ? GzipBase64TransferWindow.ShowSend(WindowOwnership.ActiveWindow(), raw: true)
            : GzipBase64TransferWindow.ShowReceive(WindowOwnership.ActiveWindow(), raw: true);
        SmarTTY.GUI.DevicePanel.WpfDeviceTerminal.GzipCatDropPrompt = file => GzipBase64TransferWindow.ShowSend(WindowOwnership.ActiveWindow(), file, raw: true);
        SmarTTY.GUI.DevicePanel.WpfDeviceTerminal.CommandListEditor = (title, description, commands) =>
            DeviceCommandListWindow.Edit(WindowOwnership.ActiveWindow(), title, description, commands);
        // "Command history..." of the terminal menus (SSH tabs and COM/TCP terminals).
        SmarTTY.Terminal.CommandHistorySource.ShowWindow = source => CommandHistoryWindow.Open(WindowOwnership.ActiveWindow(), source);
        SmarTTY.GUI.DevicePanel.WpfDeviceTerminal.TftpFirmwareWindowFactory = (terminal, title) => new TftpFirmwareWindow(terminal, title);
        RemoteFileEditor.SudoConfirmationPrompt = (channel, promptText) =>
            SudoConfirmationWindow.Show(WindowOwnership.ActiveWindow(), channel, promptText);

        ShowLauncher(e.Args.FirstOrDefault());
    }

    /// <summary>
    /// Shows the start window with the list of connections. What it does when it is closed depends on what was chosen:
    /// the main window opens, or the application exits when nothing was chosen.
    /// </summary>
    private static void ShowLauncher(string defaultItemNameOrCmdLine)
    {
        ConnectionSelectionWindow connectionSelectionWindow = new ConnectionSelectionWindow(showRawConnections: true, defaultItemNameOrCmdLine);
        connectionSelectionWindow.Closed += OnSelectionWindowClosed;
        connectionSelectionWindow.Show();
    }

    // The application ends with the main window, unless it was closed by File > Back to launcher.
    private static void ExitWithMainWindow(MainWindow mainWindow)
    {
        mainWindow.Closed += (object sender, EventArgs e) =>
        {
            if (!mainWindow.ReturnToLauncher)
            {
                Application.Current.Shutdown();
                return;
            }
            // Later, not from the Closed event: the window is still being disposed and its preferences saved there.
            Application.Current.Dispatcher.BeginInvoke((Action)(() => ShowLauncher(null)));
        };
    }

    /// <summary>
    /// The main window of a new SSH connection could not be opened: the error is written to the crash log and shown, the
    /// connection is closed and the launcher comes back, so that the user is not left without any window.
    /// </summary>
    private static void OnMainWindowFailed(MainWindow mainWindow, SshSession session, Exception exception)
    {
        CrashLog.Write("Cannot open the main window of the SSH connection", exception);
        // The launcher first: closing the half-built main window must not leave the application without windows, which
        // would shut it down.
        ShowLauncher(null);
        try
        {
            mainWindow?.Close();
        }
        catch
        {
        }
        try
        {
            session?.Dispose();
        }
        catch
        {
        }
        MessageBoxes.Error("Cannot open the terminal window of the connection:\n\n" + exception.GetType().Name + ": " + exception.Message
            + "\n\nThe details are in " + CrashLog.FilePath);
    }

    private static void OnSelectionWindowClosed(object sender, EventArgs e)
    {
        ConnectionSelectionWindow connectionSelectionWindow = (ConnectionSelectionWindow)sender;
        if (connectionSelectionWindow.Controller.SelectedSettings != null && connectionSelectionWindow.Controller.CreatedSession != null)
        {
            SshSession session = connectionSelectionWindow.Controller.CreatedSession;
            MainWindow mainWindow = null;
            try
            {
                mainWindow = new MainWindow(session, connectionSelectionWindow.Controller.SelectedSettings, connectionSelectionWindow.Controller.Job?.Command, connectionSelectionWindow.Controller.Job?.EchoInitialCommand ?? false);
                mainWindow.Show();
                ExitWithMainWindow(mainWindow);
            }
            catch (Exception exception)
            {
                // The launcher is already gone: an exception here used to end the program (or leave it running without
                // any window), so that the connection seemed to vanish. Say what went wrong and offer the list again.
                OnMainWindowFailed(mainWindow, session, exception);
            }
        }
        else if (connectionSelectionWindow.Controller.PendingRawConnectionSettings != null)
        {
            // A COM/TCP connection chosen in the start window opens in the main window, as a tab next to
            // any SSH sessions that are added later, instead of in a separate window of its own.
            MainWindow rawWindow = new MainWindow(connectionSelectionWindow.Controller.PendingRawConnectionSettings);
            rawWindow.Show();
            ExitWithMainWindow(rawWindow);
        }
        else
        {
            Application.Current.Shutdown();
        }
    }
}
