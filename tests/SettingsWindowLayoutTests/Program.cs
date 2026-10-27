using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// Regression harness for the layout of GUI/GlobalSettingsWindow.xaml. The window shows five tabs whose contents
// differ a lot in height (Paste Settings has seventeen rows, Smart Terminal Settings eight). While the window sized
// itself to its content (SizeToContent="Height"), it grew and shrank every time the user switched a tab. The window
// now keeps one fixed size and each tab scrolls on its own, so switching a tab never resizes the window.
// Run with: dotnet run --project tests/SettingsWindowLayoutTests
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

    // The tabs and the element each one contains directly.
    private static (string Header, string Content)[] ReadTabs(string xaml)
    {
        var tabs = Regex.Matches(xaml, "<TabItem Header=\"([^\"]+)\">\\s*(<([A-Za-z:]+))")
            .Select(m => (Header: m.Groups[1].Value, Content: m.Groups[3].Value))
            .ToArray();
        return tabs;
    }

    private static int Main()
    {
        try
        {
            string root = FindRepositoryRoot();
            if (root == null)
            {
                Console.WriteLine("SKIP: repository root not found, the settings window layout is not checked");
                return 0;
            }
            string xaml = File.ReadAllText(Path.Combine(root, "GUI", "GlobalSettingsWindow.xaml"));

            string window = Regex.Match(xaml, "<Window\\b[^>]*>").Value;
            Check(window.Length > 0, "the settings window element is found");
            Check(!window.Contains("SizeToContent"),
                "the window does not size itself to the current tab");
            Check(Regex.IsMatch(window, "Width=\"[0-9]+\"") && Regex.IsMatch(window, "Height=\"[0-9]+\""),
                "the window has a fixed width and height");
            Check(window.Contains("ResizeMode=\"NoResize\""),
                "the window cannot be resized, so its size stays the same on every tab");

            (string Header, string Content)[] tabs = ReadTabs(xaml);
            Check(tabs.Length == 5, $"the window has its five tabs (found {tabs.Length})");
            Check(tabs.Select(t => t.Header).Distinct().Count() == tabs.Length,
                "every tab has its own header");

            // A tab whose content is taller than the window scrolls; the window itself never changes size. The
            // check reads the direct child of each TabItem, which must be that ScrollViewer.
            foreach ((string header, string content) in tabs)
            {
                Check(content == "ScrollViewer", $"the {header} tab wraps its content in a ScrollViewer");
            }
            Check(Regex.Matches(xaml, "<ScrollViewer VerticalScrollBarVisibility=\"Auto\" HorizontalScrollBarVisibility=\"Disabled\">").Count == tabs.Length,
                "every tab scrolls vertically and never horizontally");

            // A fixed height can still be taller than a small display; the window caps itself to the work area so
            // the OK/Cancel buttons stay reachable.
            string code = File.ReadAllText(Path.Combine(root, "GUI", "GlobalSettingsWindow.xaml.cs"));
            Check(code.Contains("MaxHeight = SystemParameters.WorkArea.Height"),
                "the window height is capped to the screen work area");
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine($"All {_passed} checks passed.");
        return 0;
    }
}
