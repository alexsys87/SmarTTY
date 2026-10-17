using SmarTTY.Forms;
using SmarTTY.GUI;
using System;
using System.Linq;
using System.Windows;
using Application = System.Windows.Application;

namespace SmarTTY;

internal class Program
{

    [STAThread]
    private static void Main(string[] args)
    {
        ConnectionSelectionWindow connectionSelectionWindow = new ConnectionSelectionWindow(showRawConnections: true, args.FirstOrDefault());
        App app = new App();
        ((Application)app).ShutdownMode = ShutdownMode.OnExplicitShutdown;
        connectionSelectionWindow.Closed += OnSelectionWindowClosed;
        ((Application)app).Run((Window)connectionSelectionWindow);
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
