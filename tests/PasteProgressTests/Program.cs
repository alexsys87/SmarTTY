using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

// Regression harness for the progress window of a paste (GUI/PasteProgressWindow, which replaced the WinForms
// Forms/PasteProgressForm). It guards the rules the window is built on (Utilities/PasteProgressRules.cs, which has no UI
// dependency) and the wiring: the paste job (GUI/PasteJob.cs since stage 4B) and the paste line by line of the Smart
// Terminal create the WPF window themselves, and the facade, its adapter and the WinForms form are gone.
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

        string pasteJob = File.ReadAllText(Path.Combine(root, "GUI", "PasteJob.cs"));
        Check(pasteJob.Contains("new PasteProgressWindow"), "the paste job creates the WPF progress window");
        Check(!Regex.IsMatch(pasteJob, @"\bThemedPasteProgress\b"), "the paste job no longer goes through the facade");
        Check(!Regex.IsMatch(pasteJob, @"\bPasteProgressForm\b"), "the paste job no longer builds the WinForms form");

        string terminal = File.ReadAllText(Path.Combine(root, "GUI", "TerminalView", "WpfSmartTerminal.cs"));
        Check(terminal.Contains("new PasteProgressWindow"), "the line-by-line paste creates the WPF progress window");
        Check(!Regex.IsMatch(terminal, @"\bThemedPasteProgress\b"), "the line-by-line paste no longer goes through the facade");
    }

    private static void TestFacade()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        // Stage 4B: the facade, its adapter and the WinForms form that stood in for the window are gone.
        foreach (string gone in new[] { "Controls/ThemedPasteProgress.cs", "GUI/ThemedPasteProgressAdapter.cs", "Forms/PasteProgressForm.cs" })
        {
            Check(!File.Exists(Path.Combine(root, gone)), gone + " is gone");
        }
        Check(!Directory.Exists(Path.Combine(root, "Forms")), "the Forms folder is gone");
    }

    private static void TestAdapterAndInstall()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string windowPath = Path.Combine(root, "GUI", "PasteProgressWindow.xaml.cs");
        Check(File.Exists(windowPath), "the themed progress window exists");
        Check(File.Exists(Path.Combine(root, "GUI", "PasteProgressWindow.xaml")), "the themed progress markup exists");
        if (File.Exists(windowPath))
        {
            string window = File.ReadAllText(windowPath);
            Check(window.Contains("interface IPasteProgress"), "the window file defines the interface the paste talks to");
            Check(window.Contains("WS_EX_NOACTIVATE") || window.Contains("WsExNoActivate"),
                "the window does not take the keyboard focus");
        }

        string app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        Check(!app.Contains("ThemedPasteProgressAdapter"), "App installs no progress adapter");
    }
}
