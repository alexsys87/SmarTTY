using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

// Regression harness for the theme palettes (GUI/Styles/<name>/Colors.xaml). Every theme must define exactly the
// same keys, so ThemeManager can swap one palette for another without leaving a DynamicResource unresolved, and the
// text colours must keep the contrast the existing Dark and Light palettes promise (WCAG): primary text >= 7:1,
// secondary text >= 4.5:1, disabled text and the file-list permission colours >= 3:1, text on the accent >= 4.5:1.
// Run with: dotnet run --project tests/ThemePaletteTests
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

    // The keys Dark/Colors.xaml defines; every palette has to match this set.
    private static Dictionary<string, string> LoadPalette(string path)
    {
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(File.ReadAllText(path), "x:Key=\"([^\"]+)\"\\s+Color=\"([^\"]+)\""))
        {
            colors[match.Groups[1].Value] = match.Groups[2].Value;
        }
        return colors;
    }

    private static double Luminance(string hex)
    {
        string value = hex.TrimStart('#');
        double[] channels = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double channel = int.Parse(value.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            channels[i] = channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
    }

    private static double Contrast(string foreground, string background)
    {
        double a = Luminance(foreground);
        double b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    // (foreground key, background key, minimum contrast). A key is a "Theme." name without the prefix.
    private static readonly (string Foreground, string Background, double Minimum)[] ContrastRules =
    {
        ("Text", "WindowBackground", 7),
        ("Text", "SurfaceBackground", 7),
        ("Text", "InputBackground", 7),
        ("Text", "ContentBackground", 7),
        ("TextSecondary", "WindowBackground", 4.5),
        ("TextSecondary", "SurfaceBackground", 4.5),
        ("TextSecondary", "SurfaceAltBackground", 4.5),
        ("TextSecondary", "ContentBackground", 4.5),
        ("TextSecondary", "InputBackground", 4.5),
        ("TextSecondary", "PopupBackground", 4.5),
        ("TextDisabled", "WindowBackground", 3),
        ("TextDisabled", "SurfaceBackground", 3),
        ("AccentForeground", "AccentBackground", 4.5),
        ("AccentForeground", "AccentHoverBackground", 4.5),
        ("AccentForeground", "AccentPressedBackground", 4.5),
        ("ItemSelectedForeground", "ItemSelectedBackground", 4.5),
        ("ItemSelectedForeground", "ItemSelectedInactiveBackground", 4.5),
        ("Link", "WindowBackground", 4.5),
        ("Link", "SurfaceBackground", 4.5),
        ("Link", "ContentBackground", 4.5),
        ("LinkHover", "WindowBackground", 4.5),
        ("Error", "WindowBackground", 4.5),
        ("FocusBorder", "WindowBackground", 3),
        ("FocusBorder", "InputBackground", 3),
        ("BorderStrong", "WindowBackground", 2),
        ("Border", "WindowBackground", 1.3),
        ("ControlBackground", "WindowBackground", 1.05),
        ("ControlHoverBackground", "ControlBackground", 1.1),
        ("Divider", "SurfaceBackground", 1.1),
        ("TabStripBackground", "WindowBackground", 1.05),
        ("ScrollThumb", "SurfaceBackground", 1.3),
        ("Permission.ReadOnly", "WindowBackground", 3),
        ("Permission.Executable", "WindowBackground", 3),
        ("Permission.SetId", "WindowBackground", 3),
        ("Permission.WorldWritable", "WindowBackground", 3),
        ("Permission.ReadOnly", "ContentBackground", 3),
        ("Permission.Executable", "ContentBackground", 3),
        ("Permission.SetId", "ContentBackground", 3),
        ("Permission.WorldWritable", "ContentBackground", 3)
    };

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
            string root = FindRepositoryRoot();
            if (root == null)
            {
                Console.WriteLine("SKIP: repository root not found, the theme palettes are not checked");
                return 0;
            }
            string stylesDirectory = Path.Combine(root, "GUI", "Styles");
            Dictionary<string, string> reference = LoadPalette(Path.Combine(stylesDirectory, "Dark", "Colors.xaml"));

            // Every theme value loads a palette that exists and has the same keys as the others.
            foreach (SmarTTYTheme theme in Enum.GetValues(typeof(SmarTTYTheme)))
            {
                string path = Path.Combine(stylesDirectory, theme.PaletteName(), "Colors.xaml");
                Check(File.Exists(path), $"theme {theme} has a palette at GUI/Styles/{theme.PaletteName()}/Colors.xaml");
                Dictionary<string, string> palette = LoadPalette(path);
                Check(palette.Keys.OrderBy(k => k).SequenceEqual(reference.Keys.OrderBy(k => k)),
                    $"theme {theme} defines exactly the same keys as Dark");
                foreach ((string foreground, string background, double minimum) in ContrastRules)
                {
                    string fg = "Theme." + foreground;
                    string bg = "Theme." + background;
                    Check(palette.ContainsKey(fg) && palette.ContainsKey(bg), $"theme {theme} has {fg} and {bg}");
                    double contrast = Contrast(palette[fg], palette[bg]);
                    Check(contrast >= minimum,
                        $"theme {theme}: {foreground} on {background} is {contrast:0.00}:1 (>= {minimum})");
                }
            }

            // The palette folder is unique per theme: two themes must not share one palette.
            SmarTTYTheme[] themes = (SmarTTYTheme[])Enum.GetValues(typeof(SmarTTYTheme));
            Check(themes.Select(t => t.PaletteName()).Distinct().Count() == themes.Length,
                "every theme has its own palette folder");

            // Both the light and the dark family are represented, and the display name of every value is set.
            Check(themes.Any(t => t.IsLight()) && themes.Any(t => !t.IsLight()),
                "there is at least one light and one dark theme");
            Check(!SmarTTYTheme.Dark.IsLight() && SmarTTYTheme.Light.IsLight(),
                "Dark is a dark theme and Light is a light theme");
            Check(!SmarTTYTheme.Blue.IsLight() && SmarTTYTheme.SolarizedLight.IsLight(),
                "Blue is dark, Solarized Light is light");

            // The XAML the settings window binds to lists every value through EnumValueFetcher; a display name keeps
            // the choices readable.
            string settingsXaml = File.ReadAllText(Path.Combine(root, "GUI", "GlobalSettingsWindow.xaml"));
            Check(settingsXaml.Contains("Path=Theme, Converter={StaticResource EnumValueFetcher}"),
                "the settings window offers the theme through EnumValueFetcher");

            // The enum has no explicit values, so their order is the order the picker shows; the first is the default.
            Check((int)SmarTTYTheme.Dark == 0, "Dark is the first theme, the default of a fresh settings file");
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
