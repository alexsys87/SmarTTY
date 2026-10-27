using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

// Regression harness for the themed progress window that replaced the WinForms Forms/PasteProgressForm. The paste job
// and the terminal live in Controls/, which the Mono test harness compiles and which cannot reference the WPF windows,
// so they create the window through the ThemedPasteProgress facade; App wires that facade to GUI/PasteProgressWindow.
// This harness guards two things: the rules the window is built on (Utilities/PasteProgressRules.cs, which has no UI
// dependency) and the wiring (the migrated call sites, the facade, the adapter and the install).
// Run with: dotnet run --project tests/PasteProgressTests
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

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
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
            TestBarValue();
            TestNearLeft();
            TestNearTop();
            TestMigratedCallSites();
            TestFacade();
            TestAdapterAndInstall();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine($"All {_passed} paste progress checks passed.");
        return 0;
    }

    private static void TestBarValue()
    {
        Equal(PasteProgressRules.BarValue(0, 100), 0, "nothing sent means an empty bar");
        Equal(PasteProgressRules.BarValue(50, 100), 50, "half of the text is half the bar");
        Equal(PasteProgressRules.BarValue(100, 100), 100, "the whole text fills the bar");
        Equal(PasteProgressRules.BarValue(0, 0), 0, "an unknown total leaves the bar empty");
        Equal(PasteProgressRules.BarValue(5, -1), 0, "a negative total leaves the bar empty");
        Equal(PasteProgressRules.BarValue(200, 100), 100, "the bar does not pass the end");
        Equal(PasteProgressRules.BarValue(-5, 100), 0, "the bar does not pass the start");
        Equal(PasteProgressRules.BarValue(1, 3), 33, "the bar value is truncated, not rounded");
    }

    private static void TestNearLeft()
    {
        Equal(PasteProgressRules.NearLeft(100, 400, 200), 200, "the window is centered over the anchor");
        Equal(PasteProgressRules.NearLeft(100, 100, 200), 100, "a wide window is not left of the anchor");
        Equal(PasteProgressRules.NearLeft(0, 380, 380), 0, "an equally wide window sits at the anchor");
    }

    private static void TestNearTop()
    {
        Equal(PasteProgressRules.NearTop(100, 500, 120, 24), 356, "the window sits over the bottom edge, lifted by the margin");
        Equal(PasteProgressRules.NearTop(100, 200, 300, 24), 100, "a tall window is not above the anchor");
    }

    private static void TestMigratedCallSites()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string pasteJob = File.ReadAllText(Path.Combine(root, "Controls", "PasteJob.cs"));
        Check(pasteJob.Contains("ThemedPasteProgress.Create"), "the paste job creates the window through the facade");
        Check(!Regex.IsMatch(pasteJob, @"\bPasteProgressForm\b"), "the paste job no longer builds the WinForms form");
        Check(!Regex.IsMatch(pasteJob, @"using SmarTTY\.Forms"), "the paste job no longer imports the Forms namespace");

        string terminal = File.ReadAllText(Path.Combine(root, "Controls", "SshTerminalControl.cs"));
        Check(terminal.Contains("ThemedPasteProgress.Create"), "the line-by-line paste creates the window through the facade");
        Check(!Regex.IsMatch(terminal, @"\bPasteProgressForm\b"), "the line-by-line paste no longer builds the WinForms form");
        Check(!Regex.IsMatch(terminal, @"using SmarTTY\.Forms"), "the line-by-line paste no longer imports the Forms namespace");
    }

    private static void TestFacade()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string facadePath = Path.Combine(root, "Controls", "ThemedPasteProgress.cs");
        Check(File.Exists(facadePath), "the ThemedPasteProgress facade exists");
        if (!File.Exists(facadePath))
        {
            return;
        }
        string facade = File.ReadAllText(facadePath);
        Check(facade.Contains("CreateImpl"), "the facade exposes the injected CreateImpl hook");
        Check(facade.Contains("static IPasteProgress Create"), "the facade offers a Create entry point");
        Check(facade.Contains("interface IPasteProgress"), "the facade defines the window the controls talk to");
        Check(Regex.IsMatch(facade, @"\bPasteProgressForm\b"),
            "the facade falls back to the WinForms form when nothing is injected");
    }

    private static void TestAdapterAndInstall()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string adapterPath = Path.Combine(root, "GUI", "ThemedPasteProgressAdapter.cs");
        Check(File.Exists(adapterPath), "the adapter that points the facade at the themed window exists");
        if (File.Exists(adapterPath))
        {
            string adapter = File.ReadAllText(adapterPath);
            Check(adapter.Contains("ThemedPasteProgress.CreateImpl"), "the adapter installs the facade hook");
            Check(adapter.Contains("new PasteProgressWindow"), "the adapter creates the themed progress window");
        }

        string windowPath = Path.Combine(root, "GUI", "PasteProgressWindow.xaml.cs");
        Check(File.Exists(windowPath), "the themed progress window exists");
        Check(File.Exists(Path.Combine(root, "GUI", "PasteProgressWindow.xaml")), "the themed progress markup exists");
        if (File.Exists(windowPath))
        {
            string window = File.ReadAllText(windowPath);
            Check(window.Contains("IPasteProgress"), "the window implements the interface the controls talk to");
            Check(window.Contains("WS_EX_NOACTIVATE") || window.Contains("WsExNoActivate"),
                "the window does not take the keyboard focus");
        }

        string app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        Check(app.Contains("ThemedPasteProgressAdapter.Install()"), "App wires the progress adapter on startup");
    }
}
