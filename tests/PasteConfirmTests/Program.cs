using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

// Regression harness for the themed paste confirmation that replaced the WinForms Forms/PasteConfirmForm. The paste
// guard lives in Controls/, which the Mono test harness compiles and which cannot reference the WPF windows, so it
// asks through the ThemedPasteConfirm facade; App wires that facade to GUI/PasteConfirmWindow. This harness guards
// two things: the rules the window is built on (Utilities/PasteConfirmRules.cs, which has no UI dependency) and the
// wiring (the migrated call site, the facade, the adapter and the install).
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

        string pasteGuard = File.ReadAllText(Path.Combine(root, "Controls", "PasteGuard.cs"));
        Check(pasteGuard.Contains("ThemedPasteConfirm.Confirm"), "the paste guard confirms through the facade");
        Check(!Regex.IsMatch(pasteGuard, @"\bPasteConfirmForm\b"), "the paste guard no longer builds the WinForms form");
        Check(!Regex.IsMatch(pasteGuard, @"using SmarTTY\.Forms"), "the paste guard no longer imports the Forms namespace");
    }

    private static void TestFacade()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string facadePath = Path.Combine(root, "Controls", "ThemedPasteConfirm.cs");
        Check(File.Exists(facadePath), "the ThemedPasteConfirm facade exists");
        if (!File.Exists(facadePath))
        {
            return;
        }
        string facade = File.ReadAllText(facadePath);
        Check(facade.Contains("ConfirmImpl"), "the facade exposes the injected ConfirmImpl hook");
        Check(facade.Contains("static ThemedPasteConfirmResult Confirm"),
            "the facade offers a Confirm entry point");
        Check(Regex.IsMatch(facade, @"\bPasteConfirmForm\b"),
            "the facade falls back to the WinForms form when nothing is injected");
    }

    private static void TestAdapterAndInstall()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string adapterPath = Path.Combine(root, "GUI", "ThemedPasteConfirmAdapter.cs");
        Check(File.Exists(adapterPath), "the adapter that points the facade at the themed window exists");
        if (File.Exists(adapterPath))
        {
            string adapter = File.ReadAllText(adapterPath);
            Check(adapter.Contains("ThemedPasteConfirm.ConfirmImpl"), "the adapter installs the facade hook");
            Check(adapter.Contains("PasteConfirmWindow.Confirm"), "the adapter opens the themed paste confirmation");
        }

        string windowPath = Path.Combine(root, "GUI", "PasteConfirmWindow.xaml.cs");
        Check(File.Exists(windowPath), "the themed paste confirmation window exists");
        Check(File.Exists(Path.Combine(root, "GUI", "PasteConfirmWindow.xaml")), "the themed paste confirmation markup exists");

        string app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        Check(app.Contains("ThemedPasteConfirmAdapter.Install()"), "App wires the paste confirmation adapter on startup");
    }
}
