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
