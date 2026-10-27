using System;
using System.IO;
using System.Text.RegularExpressions;

// Regression harness for the themed folder chooser that replaced the WinForms FolderBrowserDialog in the COM/TCP
// terminal. The controls live in Controls/, which the Mono test harness compiles and which cannot reference the WPF
// windows, so they ask through the ThemedFolderBrowser facade; App wires that facade to GUI/LocalFolderBrowserWindow.
// This harness guards the wiring: the three receive paths must not open the native dialog any more, and the facade
// and the adapter that connects it to the themed browser must exist.
// Run with: dotnet run --project tests/FolderBrowserTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static string FindRepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "SmarTTY.csproj")))
        {
            root = Path.GetDirectoryName(root);
        }
        return root;
    }

    private static int Main()
    {
        try
        {
            TestReceivePathsAreMigrated();
            TestFacade();
            TestAdapterAndInstall();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine($"All {_passed} folder browser checks passed.");
        return 0;
    }

    private static void TestReceivePathsAreMigrated()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            Console.WriteLine("SKIP: repository root not found, the folder dialog call sites are not checked");
            return;
        }

        string[] receivePaths = new string[]
        {
            Path.Combine(root, "Controls", "DeviceTerminalControl.ZModem.cs"),
            Path.Combine(root, "Controls", "DeviceTerminalControl.Kermit.cs"),
            Path.Combine(root, "Controls", "DeviceTerminalControl.YModem.cs")
        };
        foreach (string path in receivePaths)
        {
            string code = File.ReadAllText(path);
            string name = Path.GetFileName(path);
            Check(!Regex.IsMatch(code, @"\bFolderBrowserDialog\b"), $"the native FolderBrowserDialog is gone from {name}");
            Check(code.Contains("ThemedFolderBrowser.Browse"), $"{name} asks the themed folder browser");
        }
    }

    private static void TestFacade()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string facadePath = Path.Combine(root, "Controls", "ThemedFolderBrowser.cs");
        Check(File.Exists(facadePath), "the ThemedFolderBrowser facade exists");
        if (!File.Exists(facadePath))
        {
            return;
        }
        string facade = File.ReadAllText(facadePath);
        Check(facade.Contains("BrowseImpl"), "the facade exposes the injected BrowseImpl hook");
        Check(facade.Contains("static string Browse"), "the facade keeps the Browse call shape of the native dialog");
        Check(facade.Contains("FolderBrowserDialog"), "the facade falls back to the native dialog when nothing is injected");
    }

    private static void TestAdapterAndInstall()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string adapterPath = Path.Combine(root, "GUI", "ThemedFolderBrowserAdapter.cs");
        Check(File.Exists(adapterPath), "the adapter that points the facade at the themed browser exists");
        if (File.Exists(adapterPath))
        {
            string adapter = File.ReadAllText(adapterPath);
            Check(adapter.Contains("ThemedFolderBrowser.BrowseImpl"),
                "the adapter installs the facade hook");
            Check(adapter.Contains("LocalFolderBrowserWindow.Browse"),
                "the adapter opens the themed local folder browser");
        }

        string app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        Check(app.Contains("ThemedFolderBrowserAdapter.Install()"),
            "App wires the folder browser adapter on startup");
    }
}
