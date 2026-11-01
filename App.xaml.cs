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
        System.Windows.Forms.Application.EnableVisualStyles();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashLog.Install(this);
        // The theme lives in Application.Resources, so it has to be there before the first window loads its XAML.
        ThemeManager.Apply(GlobalSettings.Load());
        // The device terminals (WinForms) show the very same "connection closed" popup as the SSH sessions.
        DeviceTerminalControl.ConnectionPromptHostFactory = prompt => new System.Windows.Forms.Integration.ElementHost
        {
            AutoSize = true,
            Child = new ConnectionClosedPopup { DataContext = prompt }
        };

        // The message boxes of the harness-compiled controls become the themed WPF dialogs.
        ThemedMessageBoxAdapter.Install();

        // The folder choosers of the harness-compiled controls become the themed WPF local folder browser.
        ThemedFolderBrowserAdapter.Install();

        // The file choosers of the harness-compiled controls become the themed WPF local file dialog.
        ThemedFileDialogAdapter.Install();

        // The snippet editor of the harness-compiled paste menu becomes the themed WPF window.
        ThemedSnippetEditorAdapter.Install();

        // The paste confirmation of the harness-compiled paste guard becomes the themed WPF window.
        ThemedPasteConfirmAdapter.Install();

        // The progress window of a long paste becomes the themed WPF window.
        ThemedPasteProgressAdapter.Install();

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
        // The device terminal (COM/TCP/telnet) raises its dialogs from harness-compiled code as well.
        DeviceTerminalControl.LogReplayPrompt = (bytes, terminal) => LogReplayWindow.Replay(bytes, terminal);
        DeviceTerminalControl.SerialSettingsPrompt = (settings, portName) =>
            SerialPortSettingsWindow.Prompt(WindowOwnership.ActiveWindow(), settings, portName);
        DeviceTerminalControl.TransferProgressWindowFactory = (title, showErrorCount) => XModemTransferWindow.Create(title, showErrorCount);
        DeviceTerminalControl.GzipBase64RequestPrompt = sending => sending
            ? GzipBase64TransferWindow.ShowSend(WindowOwnership.ActiveWindow())
            : GzipBase64TransferWindow.ShowReceive(WindowOwnership.ActiveWindow());
        DeviceTerminalControl.CommandListEditor = (title, description, commands) =>
            DeviceCommandListWindow.Edit(WindowOwnership.ActiveWindow(), title, description, commands);
        DeviceTerminalControl.TftpFirmwareWindowFactory = (terminal, title) => new TftpFirmwareWindow(terminal, title);
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
