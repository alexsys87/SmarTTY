using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SmarTTY.Utilities;

// Regression harness for the themed colour picker that replaced the WinForms ColorDialog: the "#RRGGBB" helpers the
// picker uses (Utilities/ColorUtils.cs) and the basic colour grid it offers. It also guards the migration itself: the
// three call sites must no longer open the native dialog, or the dialog would look out of place in a light theme.
// Run with: dotnet run --project tests/ColorPickerTests
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
            TestFormat();
            TestParse();
            TestClamp();
            TestBasicColors();
            TestCallSitesAreMigrated();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine($"All {_passed} checks passed.");
        return 0;
    }

    private static void TestFormat()
    {
        Check(ColorUtils.FormatHex(0, 0, 0) == "#000000", "black is #000000");
        Check(ColorUtils.FormatHex(255, 255, 255) == "#FFFFFF", "white is #FFFFFF");
        Check(ColorUtils.FormatHex(16, 32, 48) == "#102030", "a channel below 16 keeps its leading zero");
        Check(ColorUtils.FormatHex(255, 0, 128) == "#FF0080", "the channels are written in red-green-blue order");
        Check(ColorUtils.FormatHex(-5, 300, 128) == "#00FF80", "an out of range channel is clamped, not wrapped");
    }

    private static void TestParse()
    {
        Check(ColorUtils.TryParseHex("#102030", out int red, out int green, out int blue) && red == 16 && green == 32 && blue == 48,
            "a #RRGGBB value is read back");
        Check(ColorUtils.TryParseHex("102030", out red, out green, out blue) && red == 16 && green == 32 && blue == 48,
            "the leading # is optional");
        Check(ColorUtils.TryParseHex("  #abcdef  ", out red, out green, out blue) && red == 171 && green == 205 && blue == 239,
            "surrounding spaces are ignored and lower case is accepted");
        Check(ColorUtils.TryParseHex("#abc", out red, out green, out blue) && red == 170 && green == 187 && blue == 204,
            "the #RGB shorthand doubles each digit");
        Check(!ColorUtils.TryParseHex("#12345", out red, out green, out blue), "a five-digit value is rejected");
        Check(!ColorUtils.TryParseHex("#gggggg", out red, out green, out blue), "a non-hex value is rejected");
        Check(!ColorUtils.TryParseHex("", out red, out green, out blue), "an empty value is rejected");
        Check(!ColorUtils.TryParseHex(null, out red, out green, out blue), "a null value is rejected");

        foreach (string hex in ColorUtils.BasicColors)
        {
            Check(ColorUtils.TryParseHex(hex, out red, out green, out blue) && ColorUtils.FormatHex(red, green, blue) == hex.ToUpperInvariant(),
                $"the basic colour {hex} round-trips through format and parse");
        }
    }

    private static void TestClamp()
    {
        Check(ColorUtils.Clamp(-1) == 0, "a channel below zero is clamped to zero");
        Check(ColorUtils.Clamp(0) == 0, "zero stays zero");
        Check(ColorUtils.Clamp(255) == 255, "255 stays 255");
        Check(ColorUtils.Clamp(256) == 255, "a channel above 255 is clamped to 255");
    }

    private static void TestBasicColors()
    {
        Check(ColorUtils.BasicColors.Length == 48, "the basic grid holds 48 colours");
        Check(ColorUtils.BasicColors.Length % 8 == 0, "the basic grid fills whole rows of eight");
        Check(ColorUtils.BasicColors.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ColorUtils.BasicColors.Length,
            "the basic grid has no duplicate colour");
        Check(ColorUtils.BasicColors.All(hex => Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$")),
            "every basic colour is written as #RRGGBB");
    }

    private static void TestCallSitesAreMigrated()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            Console.WriteLine("SKIP: repository root not found, the colour dialog call sites are not checked");
            return;
        }

        Check(File.Exists(Path.Combine(root, "GUI", "ColorPickerWindow.xaml")) &&
            File.Exists(Path.Combine(root, "GUI", "ColorPickerWindow.xaml.cs")),
            "the themed colour picker window exists");

        string[] callSites = new string[]
        {
            Path.Combine(root, "GUI", "PaletteEditWindow.cs"),
            Path.Combine(root, "GUI", "GlobalSettingsWindow.xaml.cs"),
            Path.Combine(root, "GUI", "RawConnectionSettingsWindow.xaml.cs")
        };
        foreach (string path in callSites)
        {
            string code = File.ReadAllText(path);
            Check(!Regex.IsMatch(code, @"\bColorDialog\b"), $"the native ColorDialog is gone from {Path.GetFileName(path)}");
            Check(!code.Contains("System.Windows.Forms"), $"the WinForms namespace is gone from {Path.GetFileName(path)}");
            Check(code.Contains("ColorPickerWindow.Pick"), $"{Path.GetFileName(path)} opens the themed colour picker");
        }
    }
}
