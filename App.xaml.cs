using System;
using System.Linq;
using System.Windows;
using SmarTTY.Controls;
using SmarTTY.GUI;
using SmarTTY.GUI.Styles;
using VisualTTY;
using VisualTTY.Backend;

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
        // The theme lives in Application.Resources, so it has to be there before the first window loads its XAML.
        ThemeManager.Apply(GlobalSettings.Load()?.Theme ?? SmarTTYTheme.Dark);

        EnsureCommandCompletionsFile();

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
        // The device terminal (COM/TCP/telnet) raises its dialogs from harness-compiled code as well.
        DeviceTerminalControl.LogReplayPrompt = (bytes, terminal) => LogReplayWindow.Replay(bytes, terminal);
        DeviceTerminalControl.SerialSettingsPrompt = (settings, portName) =>
            SerialPortSettingsWindow.Prompt(WindowOwnership.ActiveWindow(), settings, portName);
        DeviceTerminalControl.TransferProgressWindowFactory = (title, showErrorCount) => XModemTransferWindow.Create(title, showErrorCount);
        DeviceTerminalControl.CommandListEditor = (title, description, commands) =>
            DeviceCommandListWindow.Edit(WindowOwnership.ActiveWindow(), title, description, commands);
        DeviceTerminalControl.TftpFirmwareWindowFactory = (terminal, title) => new TftpFirmwareWindow(terminal, title);
        RemoteFileEditor.SudoConfirmationPrompt = (channel, promptText) =>
            SudoConfirmationWindow.Show(WindowOwnership.ActiveWindow(), channel, promptText);

        ConnectionSelectionWindow connectionSelectionWindow = new ConnectionSelectionWindow(showRawConnections: true, e.Args.FirstOrDefault());
        connectionSelectionWindow.Closed += OnSelectionWindowClosed;
        connectionSelectionWindow.Show();
    }

    // Creates CommandCompletions.xml next to the exe on startup if it is missing.
    // CommandCompletionDatabase.Load() writes an empty database when the file is absent
    // and never throws for I/O problems (e.g. a read-only install folder).
    private static void EnsureCommandCompletionsFile()
    {
        try
        {
            CommandCompletionDatabase.Load();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Could not prepare CommandCompletions.xml: " + ex.Message);
        }
    }

    private static void OnSelectionWindowClosed(object sender, EventArgs e)
    {
        ConnectionSelectionWindow connectionSelectionWindow = (ConnectionSelectionWindow)sender;
        if (connectionSelectionWindow.Controller.SelectedSettings != null && connectionSelectionWindow.Controller.CreatedSession != null)
        {
            MainWindow mainWindow = new MainWindow(connectionSelectionWindow.Controller.CreatedSession, connectionSelectionWindow.Controller.SelectedSettings, connectionSelectionWindow.Controller.Job?.Command, connectionSelectionWindow.Controller.Job?.EchoInitialCommand ?? false);
            mainWindow.Show();
            mainWindow.Closed += (object _, EventArgs e) =>
            {
                Application.Current.Shutdown();
            };
        }
        else if (connectionSelectionWindow.Controller.PendingRawConnectionSettings != null)
        {
            // A COM/TCP connection chosen in the start window opens in the main window, as a tab next to
            // any SSH sessions that are added later, instead of in a separate window of its own.
            MainWindow rawWindow = new MainWindow(connectionSelectionWindow.Controller.PendingRawConnectionSettings);
            rawWindow.Show();
            rawWindow.Closed += (object closedSender, EventArgs closedArgs) =>
            {
                Application.Current.Shutdown();
            };
        }
        else
        {
            Application.Current.Shutdown();
        }
    }
}
