using SmarTTY.Forms;
using SmarTTY.GUI;
using System;
using System.Linq;
using System.Windows;
using VisualTTY;
using Application = System.Windows.Application;

namespace SmarTTY;

internal class Program
{

    [STAThread]
    private static void Main(string[] args)
    {
        EnsureCommandCompletionsFile();

        ConnectionSelectionWindow connectionSelectionWindow = new ConnectionSelectionWindow(showRawConnections: true, args.FirstOrDefault());
        App app = new App();
        ((Application)app).ShutdownMode = ShutdownMode.OnExplicitShutdown;
        connectionSelectionWindow.Closed += OnSelectionWindowClosed;
        ((Application)app).Run((Window)connectionSelectionWindow);
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
            System.Windows.Forms.Application.Run(new DeviceTerminalHostForm(connectionSelectionWindow.Controller.PendingRawConnectionSettings));
            Application.Current.Shutdown();
        }
        else
        {
            Application.Current.Shutdown();
        }
    }
}
