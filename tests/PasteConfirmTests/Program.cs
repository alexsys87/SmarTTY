using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

// Regression harness for the paste confirmation (GUI/PasteConfirmWindow, which replaced the WinForms Forms/PasteConfirmForm).
// It guards two things: the rules the window is built on (Utilities/PasteConfirmRules.cs, which has no UI dependency) and the
// wiring: the paste guard (GUI/PasteGuard.cs since stage 4B) opens the WPF window itself, and the facade, its adapter and the
// WinForms form that stood in for the window are gone.
// Run with: dotnet run --project tests/PasteConfirmTests
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
            TestTitle();
            TestDangerLine();
            TestDefaultToCancel();
            TestMigratedCallSite();
            TestFacade();
            TestAdapterAndInstall();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine($"All {_passed} paste confirmation checks passed.");
        return 0;
    }

    private static void TestTitle()
    {
        Equal(PasteConfirmRules.Title(false), "Paste into the terminal", "a plain paste uses the neutral caption");
        Equal(PasteConfirmRules.Title(true), "Paste - dangerous commands", "a dangerous paste says so in the caption");
    }

    private static void TestDangerLine()
    {
        Equal(PasteConfirmRules.DangerLine(null), "", "no dangers list means no warning line");
        Equal(PasteConfirmRules.DangerLine(new List<string>()), "", "an empty dangers list means no warning line");
        Equal(PasteConfirmRules.DangerLine(new[] { "runs sudo" }), "Warning: the text runs sudo.",
            "one danger is named in the warning line");
        Equal(PasteConfirmRules.DangerLine(new[] { "runs sudo", "pipes to a shell" }),
            "Warning: the text runs sudo; pipes to a shell.", "several dangers are joined with a semicolon");
    }

    private static void TestDefaultToCancel()
    {
        Check(PasteConfirmRules.DefaultToCancel(true), "dangerous text makes Cancel the default answer");
        Check(!PasteConfirmRules.DefaultToCancel(false), "plain text makes Paste the default answer");
    }

    private static void TestMigratedCallSite()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string pasteGuard = File.ReadAllText(Path.Combine(root, "GUI", "PasteGuard.cs"));
        Check(pasteGuard.Contains("PasteConfirmWindow.Confirm"), "the paste guard opens the WPF paste confirmation");
        Check(!Regex.IsMatch(pasteGuard, @"\bThemedPasteConfirm\b"), "the paste guard no longer goes through the facade");
        Check(!Regex.IsMatch(pasteGuard, @"\bPasteConfirmForm\b"), "the paste guard no longer builds the WinForms form");
    }

    private static void TestFacade()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        // Stage 4B: the facade, its adapter and the WinForms form that stood in for the window are gone.
        foreach (string gone in new[] { "Controls/ThemedPasteConfirm.cs", "GUI/ThemedPasteConfirmAdapter.cs", "Forms/PasteConfirmForm.cs" })
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

        string windowPath = Path.Combine(root, "GUI", "PasteConfirmWindow.xaml.cs");
        Check(File.Exists(windowPath), "the themed paste confirmation window exists");
        Check(File.Exists(Path.Combine(root, "GUI", "PasteConfirmWindow.xaml")), "the themed paste confirmation markup exists");

        string app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        Check(!app.Contains("ThemedPasteConfirmAdapter"), "App installs no paste confirmation adapter");
    }
}
