using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

// Regression harness for the snippet editor (GUI/SnippetEditWindow, which replaced the WinForms Forms/SnippetEditForm). It
// guards the rules the window is built on (Utilities/SnippetRules.cs, which has no UI dependency) and the wiring: the paste
// menu (GUI/PasteMenu.cs, WPF since stage 4A) opens the WPF window itself, and the facade, its adapter and the WinForms form
// that stood in for the window are gone (stage 4B).
// Run with: dotnet run --project tests/SnippetEditorTests
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
            TestLineBreakNormalisation();
            TestNameTrimming();
            TestAcceptance();
            TestMigratedCallSite();
            TestFacade();
            TestAdapterAndInstall();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine($"All {_passed} snippet editor checks passed.");
        return 0;
    }

    private static void TestLineBreakNormalisation()
    {
        Equal(SnippetRules.NormalizeText(null), "", "a null text becomes empty");
        Equal(SnippetRules.NormalizeText(""), "", "an empty text stays empty");
        Equal(SnippetRules.NormalizeText("one line"), "one line", "a text without line breaks is unchanged");
        Equal(SnippetRules.NormalizeText("a\nb"), "a\r\nb", "a bare LF becomes CRLF");
        Equal(SnippetRules.NormalizeText("a\r\nb"), "a\r\nb", "an existing CRLF stays CRLF");
        // A bare CR is left as is, exactly as the WinForms form did (the clipboard hands out CRLF, so this is
        // the rare case of a text built by hand).
        Equal(SnippetRules.NormalizeText("a\rb"), "a\rb", "a bare CR is left as is, as the WinForms form did");
        Equal(SnippetRules.NormalizeText("a\n\nb"), "a\r\n\r\nb", "a blank line survives the normalisation");
        // No LF may be left alone (the box would then show one line break in two ways).
        Check(!SnippetRules.NormalizeText("a\nb").Replace("\r\n", "").Contains("\n"),
            "no bare LF is left after the normalisation");
    }

    private static void TestNameTrimming()
    {
        Equal(SnippetRules.TrimName(null), "", "a null name becomes empty");
        Equal(SnippetRules.TrimName("   "), "", "a blank name becomes empty (the menu shows the text instead)");
        Equal(SnippetRules.TrimName("  name  "), "name", "the name is trimmed");
        Equal(SnippetRules.TrimName("name"), "name", "a plain name is unchanged");
    }

    private static void TestAcceptance()
    {
        Check(!SnippetRules.IsAcceptable(""), "an empty text is refused");
        Check(!SnippetRules.IsAcceptable(null), "a null text is refused");
        Check(SnippetRules.IsAcceptable("x"), "a text with one character is accepted");
        Check(SnippetRules.IsAcceptable("\r\n"), "a text of only a line break is accepted");
    }

    private static void TestMigratedCallSite()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string pasteMenu = File.ReadAllText(Path.Combine(root, "GUI", "PasteMenu.cs"));
        Check(pasteMenu.Contains("SnippetEditWindow.Edit"), "the paste menu edits snippets in the WPF window");
        Check(!Regex.IsMatch(pasteMenu, @"\bThemedSnippetEditor\b"), "the paste menu does not go through the facade");
        Check(!Regex.IsMatch(pasteMenu, @"\bSnippetEditForm\b"), "the paste menu no longer builds the WinForms form");
    }

    private static void TestFacade()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        // Stage 4B: the facade, its adapter and the WinForms form that stood in for the window are gone.
        foreach (string gone in new[] { "Controls/ThemedSnippetEditor.cs", "GUI/ThemedSnippetEditorAdapter.cs", "Forms/SnippetEditForm.cs" })
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

        string windowPath = Path.Combine(root, "GUI", "SnippetEditWindow.xaml.cs");
        Check(File.Exists(windowPath), "the themed snippet editor window exists");
        Check(File.Exists(Path.Combine(root, "GUI", "SnippetEditWindow.xaml")), "the themed snippet editor markup exists");

        string app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        Check(!app.Contains("ThemedSnippetEditorAdapter"), "App installs no snippet editor adapter");
    }
}
