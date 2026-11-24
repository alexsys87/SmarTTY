using System;
using System.IO;
using System.Text.RegularExpressions;

// Regression harness for the themed folder chooser that replaced the WinForms FolderBrowserDialog in the COM/TCP
// terminal. The code that cannot reference the WPF windows asks through the hooks of Controls/UserDialogs, which App
// points at GUI/LocalFolderBrowserWindow (stage 4B: the ThemedFolderBrowser facade, its adapter and its fallback to the
// native dialog are gone). This harness guards the wiring: the three receive paths must not open the native dialog, and
// the hook must be set to the themed browser.
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
            Path.Combine(root, "GUI", "DevicePanel", "WpfDeviceTerminal.ZModem.cs"),
            Path.Combine(root, "GUI", "DevicePanel", "WpfDeviceTerminal.Kermit.cs"),
            Path.Combine(root, "GUI", "DevicePanel", "WpfDeviceTerminal.YModem.cs")
        };
        foreach (string path in receivePaths)
        {
            string code = File.ReadAllText(path);
            string name = Path.GetFileName(path);
            Check(!Regex.IsMatch(code, @"\bFolderBrowserDialog\b"), $"the native FolderBrowserDialog is gone from {name}");
            Check(code.Contains("UserDialogs.Browse"), $"{name} asks the themed folder browser");
        }
    }

    private static void TestFacade()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string seam = File.ReadAllText(Path.Combine(root, "Controls", "UserDialogs.cs"));
        Check(seam.Contains("FolderImpl"), "the dialogs seam exposes the folder hook");
        Check(seam.Contains("static string Browse"), "the dialogs seam keeps the Browse call shape of the native dialog");
        Check(!Regex.IsMatch(seam, @"new\s+FolderBrowserDialog\b"), "the dialogs seam has no native fallback");
        Check(!File.Exists(Path.Combine(root, "Controls", "ThemedFolderBrowser.cs")), "the ThemedFolderBrowser facade is gone");
    }

    private static void TestAdapterAndInstall()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        Check(!File.Exists(Path.Combine(root, "GUI", "ThemedFolderBrowserAdapter.cs")), "the folder browser adapter is gone");
        string app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        Check(Regex.IsMatch(app, @"UserDialogs\.FolderImpl\s*=.*LocalFolderBrowserWindow\.Browse"),
            "App points the folder hook at the themed local folder browser");
    }
}
