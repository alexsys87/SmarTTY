using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SmarTTY.GUI;

/// <summary>
/// Writes the exceptions nobody caught to %LOCALAPPDATA%\SmarTTY\crash.log, so that a crash that happens only now and then
/// (a race between the threads of a login) leaves its stack trace behind. An exception of the UI thread is shown and the
/// program goes on; one of a background thread cannot be stopped by .NET, but it is written down before the process ends.
/// </summary>
internal static class CrashLog
{
    private static readonly object Sync = new object();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmarTTY", "crash.log");

    public static void Install(Application application)
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            Write(e.IsTerminating ? "Unhandled exception (the program ends)" : "Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Write("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Write("Unhandled exception of the UI thread", e.Exception);
        e.Handled = true;
        try
        {
            MessageBoxes.Error("An unexpected error occurred, the program goes on:\n\n" + e.Exception.Message
                + "\n\nThe details are in " + FilePath);
        }
        catch
        {
        }
    }

    public static void Write(string title, Exception exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                StringBuilder text = new StringBuilder();
                text.AppendLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + title);
                text.AppendLine((exception?.ToString() ?? "(no exception object)"));
                text.AppendLine();
                File.AppendAllText(FilePath, text.ToString());
            }
        }
        catch
        {
            // Writing the log must never be the reason for another failure.
        }
    }
}
