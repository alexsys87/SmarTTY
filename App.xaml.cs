using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SmarTTY.GUI;
using SmarTTY.GUI.Styles;
using VisualTTY;

namespace SmarTTY;

public partial class App : Application
{
    public App()
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        // Windows load their XAML (and its DynamicResource lookups) before MainWindow.ApplyTheme merges the
        // theme dictionary, which logged "Resource not found" warnings for the theme brushes. These application-level
        // defaults only act as a fallback: a theme dictionary merged into a window always takes precedence.
        Resources[StyleKeys.MainWindowText] = SystemColors.WindowTextBrush;
        Resources[StyleKeys.ActiveTabBackground] = Brushes.Transparent;
        // WPFScrollBar references this style in its constructor, before it is attached to a window that has the
        // theme dictionary (the reference is re-resolved on attach). The light theme does not define it at all.
        Resources["MainWindowScrollBar"] = new Style(typeof(ScrollBar));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        EnsureCommandCompletionsFile();

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
