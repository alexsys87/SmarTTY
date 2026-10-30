using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SmarTTY.Utilities;
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
            Check(!SmarTTYTheme.Dracula.IsLight() && !SmarTTYTheme.GruvboxDark.IsLight() &&
                !SmarTTYTheme.Monokai.IsLight() && !SmarTTYTheme.OneDark.IsLight() && !SmarTTYTheme.Forest.IsLight(),
                "Dracula, Gruvbox Dark, Monokai, One Dark and Forest are dark themes");
            Check(SmarTTYTheme.Sepia.IsLight() && SmarTTYTheme.Rose.IsLight(), "Sepia and Rose are light themes");

            // Every value names its palette folder (a distinct one, checked above) and carries a display name, so the
            // picker never shows a bare enum identifier.
            foreach (SmarTTYTheme theme in themes)
            {
                Check(!string.IsNullOrEmpty(theme.PaletteName()), $"theme {theme} names its palette folder");
                System.Reflection.FieldInfo field = typeof(SmarTTYTheme).GetField(theme.ToString());
                EnumDisplayText display = (EnumDisplayText)Attribute.GetCustomAttribute(field, typeof(EnumDisplayText));
                Check(display != null && !string.IsNullOrEmpty(display.Text),
                    $"theme {theme} has a display name for the picker");
            }

            // The settings window offers the themes in a drop-down list bound to the settings' list of available themes (the
            // built-in ones, then the custom ones), and the choice goes back through ThemeChoice.
            string settingsXaml = File.ReadAllText(Path.Combine(root, "GUI", "GlobalSettingsWindow.xaml"));
            Check(settingsXaml.Contains("ItemsSource=\"{Binding Path=AvailableThemes}\"") && settingsXaml.Contains("SelectedItem=\"{Binding ThemeChoice}\""),
                "the settings window offers the themes through AvailableThemes and ThemeChoice");

            // The enum has no explicit values, so their order is the order the picker shows; the first is the default.
            Check((int)SmarTTYTheme.Dark == 0, "Dark is the first theme, the default of a fresh settings file");

            CheckCustomThemes(root, reference);
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine($"All {_passed} checks passed.");
        return 0;
    }

    private static string Hex(uint argb)
    {
        return "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
    }

    // The custom themes: the reader of *.theme files, the themes folder and the files shipped in ThemeExamples.
    private static void CheckCustomThemes(string root, Dictionary<string, string> dark)
    {
        // Colours: #RGB, #ARGB, #RRGGBB, #AARRGGBB and Transparent; anything else is refused.
        Check(ThemeLibrary.TryParseColor("#1E1F22", out uint c) && c == 0xFF1E1F22, "#RRGGBB is an opaque colour");
        Check(ThemeLibrary.TryParseColor("#801E1F22", out c) && c == 0x801E1F22, "#AARRGGBB keeps its alpha");
        Check(ThemeLibrary.TryParseColor("#abc", out c) && c == 0xFFAABBCC, "#RGB doubles every digit");
        Check(ThemeLibrary.TryParseColor("#8abc", out c) && c == 0x88AABBCC, "#ARGB doubles every digit");
        Check(ThemeLibrary.TryParseColor("Transparent", out c) && c == 0x00FFFFFF, "Transparent is #00FFFFFF");
        foreach (string bad in new[] { "", "red", "#", "#12", "#12345", "#GG0000", "#-12345", "#+12345", "1E1F22", "#1E1F22FF00" })
        {
            Check(!ThemeLibrary.TryParseColor(bad, out _), $"\"{bad}\" is not a colour");
        }

        // The stock system colours the palettes override each follow one palette key (ThemeLibrary.SystemColorLinks): in every
        // built-in palette the system colour has the colour of one of the keys listed for it, so a custom theme that sets
        // that key can carry the system colour along.
        foreach (SmarTTYTheme theme in Enum.GetValues(typeof(SmarTTYTheme)))
        {
            string path = Path.Combine(root, "GUI", "Styles", theme.PaletteName(), "Colors.xaml");
            Dictionary<string, string> palette = LoadPalette(path);
            MatchCollection systemColors = Regex.Matches(File.ReadAllText(path), "x:Key=\"\\{x:Static SystemColors\\.(\\w+)\\}\"\\s+Color=\"([^\"]+)\"");
            Check(systemColors.Count == ThemeLibrary.SystemColorLinks.Count, $"theme {theme} overrides exactly the system colours of the table ({systemColors.Count})");
            foreach (Match match in systemColors)
            {
                string name = match.Groups[1].Value;
                bool followed = ThemeLibrary.SystemColorLinks.TryGetValue(name, out string[] candidates) &&
                    candidates.Any(key => palette.TryGetValue(key, out string value) && ThemeLibrary.TryParseColor(value, out uint a) && ThemeLibrary.TryParseColor(match.Groups[2].Value, out uint b) && a == b);
                Check(followed, $"theme {theme}: {name} has the colour of the key it follows");
            }
        }

        // One file: title, family, base theme, colours with and without the prefix, comments and what is skipped.
        ThemeChoice parsed = ThemeLibrary.Parse("my.theme",
            "\uFEFF# comment\r\n; another\r\n// and another\r\n\r\nName = Fire; Ice\r\nlight = TRUE\r\nBase = solarized light\r\n" +
            "Theme.Accent = #FF0000 ; the accent\r\ntext = #101010\r\nTHEME.border = #202020\r\n" +
            "Error = red\r\nnonsense\r\nLight2 = what\r\n= #000000\r\n");
        Check(parsed.IsCustom && parsed.FileName == "my.theme", "a parsed file is a custom theme with its file name");
        Check(parsed.Name == "Fire; Ice", "the name keeps everything after the equal sign");
        Check(parsed.IsLight, "Light = true makes a light theme (the value is case-insensitive)");
        Check(parsed.BuiltIn == SmarTTYTheme.SolarizedLight, "Base names a built-in theme by its title");
        Check(parsed.Colors.Count == 3 && parsed.Colors["Theme.Accent"] == 0xFFFF0000 && parsed.Colors["Theme.Text"] == 0xFF101010 && parsed.Colors["Theme.Border"] == 0xFF202020,
            "colours are read with or without the Theme. prefix, and a trailing comment is dropped");
        Check(parsed.Problems.Count == 4, "a bad colour, a line without equal sign, an unknown key with a bad value and an empty key are reported (" + parsed.Problems.Count + ")");
        Check(parsed.ToolTip.Contains("Skipped"), "the tool tip of a theme with problems lists them");

        ThemeChoice plain = ThemeLibrary.Parse("Plain Name.theme", "");
        Check(plain.Name == "Plain Name" && !plain.IsLight && plain.BuiltIn == SmarTTYTheme.Dark && plain.Problems.Count == 0,
            "an empty file is a copy of Dark titled by its file name");
        ThemeChoice lightOnly = ThemeLibrary.Parse("l.theme", "Light = yes");
        Check(lightOnly.IsLight && lightOnly.BuiltIn == SmarTTYTheme.Light, "Light = true without a Base extends the Light theme");
        ThemeChoice darkBase = ThemeLibrary.Parse("d.theme", "Base = Dracula");
        Check(!darkBase.IsLight && darkBase.BuiltIn == SmarTTYTheme.Dracula, "a theme is as light as its base unless it says otherwise");
        ThemeChoice badBase = ThemeLibrary.Parse("b.theme", "Base = nothing\r\nLight = maybe");
        Check(badBase.BuiltIn == SmarTTYTheme.Dark && badBase.Problems.Count == 2, "an unknown base and a bad Light value are reported, and Dark is used");
        Check(ThemeLibrary.Parse("long.theme", "Name = " + new string('x', 200)).Name.Length == 60, "a very long name is cut");

        string folderBefore = ThemeLibrary.FolderPath;
        string temp = Path.Combine(Path.GetTempPath(), "smartty-themes-" + Guid.NewGuid().ToString("N"));
        try
        {
            // No Themes folder: only the built-in themes, and a selected custom theme falls back to its built-in theme.
            ThemeLibrary.FolderPath = Path.Combine(temp, "Themes");
            ThemeLibrary.Reload();
            int builtIn = Enum.GetValues(typeof(SmarTTYTheme)).Length;
            Check(ThemeLibrary.All.Count == builtIn && ThemeLibrary.All.All(t => !t.IsCustom), "without a Themes folder there are only the built-in themes");
            Check(ThemeLibrary.All.Select(t => t.BuiltIn).SequenceEqual((SmarTTYTheme[])Enum.GetValues(typeof(SmarTTYTheme))), "the built-in themes come in the order of the enum");
            Check(ThemeLibrary.All.Select(t => t.Id).Distinct().Count() == builtIn, "every theme has its own id");
            Check(ThemeLibrary.Find(SmarTTYTheme.Nord, null).BuiltIn == SmarTTYTheme.Nord, "a built-in theme is found by its value");
            Check(ThemeLibrary.Find(SmarTTYTheme.Nord, "gone.theme").BuiltIn == SmarTTYTheme.Nord && !ThemeLibrary.Find(SmarTTYTheme.Nord, "gone.theme").IsCustom,
                "a custom theme whose file is missing falls back to the built-in theme");
            ThemeLibrary.FolderPath = null;
            ThemeLibrary.Reload();
            Check(ThemeLibrary.All.Count == builtIn, "no folder path means only the built-in themes");

            // A Themes folder: *.theme files of its top level only, the ones too big or unreadable are skipped.
            string folder = Path.Combine(temp, "Themes");
            Directory.CreateDirectory(Path.Combine(folder, "nested"));
            File.WriteAllText(Path.Combine(folder, "zebra.theme"), "Name = Zebra\r\nAccent = #112233\r\n");
            File.WriteAllText(Path.Combine(folder, "Alpha.THEME"), "Name = alpha\r\nLight = true\r\nBase = Sepia\r\n");
            File.WriteAllText(Path.Combine(folder, "broken.theme"), "this is not a theme\r\nAccent = blue\r\n");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "Name = Not a theme\r\n");
            File.WriteAllText(Path.Combine(folder, "huge.theme"), "Name = Huge\r\n" + new string('#', 70 * 1024));
            File.WriteAllText(Path.Combine(folder, "nested", "deep.theme"), "Name = Deep\r\n");
            ThemeLibrary.FolderPath = folder;
            ThemeLibrary.Reload();
            ThemeChoice[] custom = ThemeLibrary.All.Where(t => t.IsCustom).ToArray();
            Check(ThemeLibrary.All.Take(builtIn).All(t => !t.IsCustom), "the custom themes follow the built-in ones");
            Check(custom.Select(t => t.Name).SequenceEqual(new[] { "alpha", "broken", "Zebra" }),
                "the custom themes are the top-level *.theme files, sorted by name (" + string.Join(", ", custom.Select(t => t.Name)) + ")");
            Check(ThemeLibrary.Find(SmarTTYTheme.Dark, "ZEBRA.theme").Name == "Zebra", "a custom theme is found by its file name, ignoring case");
            Check(ThemeLibrary.Find(SmarTTYTheme.Dark, "alpha.theme").IsLight && ThemeLibrary.Find(SmarTTYTheme.Dark, "alpha.theme").BuiltIn == SmarTTYTheme.Sepia,
                "the base theme of a custom theme is what the settings keep as the fallback");
            Check(ThemeLibrary.Find(SmarTTYTheme.Dark, "broken.theme").Problems.Count == 2, "a broken file is still offered, with its problems listed");
            ThemeChoice same = ThemeLibrary.Find(SmarTTYTheme.Dark, "zebra.theme");
            Check(ReferenceEquals(same, ThemeLibrary.All.First(t => t.FileName == "zebra.theme")) && ReferenceEquals(same, ThemeLibrary.Find(SmarTTYTheme.Dark, "zebra.theme")),
                "the list hands out the same instance every time (the drop-down compares them)");

            // The file goes away: the settings still name it, and the built-in theme is used.
            File.Delete(Path.Combine(folder, "zebra.theme"));
            ThemeLibrary.Reload();
            Check(!ThemeLibrary.Find(SmarTTYTheme.Midnight, "zebra.theme").IsCustom && ThemeLibrary.Find(SmarTTYTheme.Midnight, "zebra.theme").BuiltIn == SmarTTYTheme.Midnight,
                "a removed theme file leaves the built-in theme of the settings");
        }
        finally
        {
            ThemeLibrary.FolderPath = folderBefore;
            ThemeLibrary.Reload();
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch
            {
            }
        }

        // The files shipped in ThemeExamples are good themes: they parse without problems and set only keys the palette has.
        string examples = Path.Combine(root, "ThemeExamples");
        foreach (string file in Directory.GetFiles(examples, "*.theme"))
        {
            string fileName = Path.GetFileName(file);
            ThemeChoice example = ThemeLibrary.Parse(fileName, File.ReadAllText(file));
            Check(example.Problems.Count == 0, $"{fileName} has no unreadable lines");
            Check(example.Colors.Keys.All(k => dark.ContainsKey(k)), $"{fileName} sets only keys that exist in the palette");
            Check(example.Colors.Count > 0, $"{fileName} sets colours");

            // The theme as the program builds it: the base palette with the colours of the file over it.
            ThemeChoice baseOf = ThemeLibrary.All.First(t => !t.IsCustom && t.BuiltIn == example.BuiltIn);
            Dictionary<string, string> palette = LoadPalette(Path.Combine(root, "GUI", "Styles", baseOf.BuiltIn.PaletteName(), "Colors.xaml"));
            foreach (KeyValuePair<string, uint> color in example.Colors)
            {
                palette[color.Key] = Hex(color.Value);
            }
            foreach ((string foreground, string background, double minimum) in ContrastRules)
            {
                double contrast = Contrast(palette["Theme." + foreground], palette["Theme." + background]);
                Check(contrast >= minimum, $"{fileName}: {foreground} on {background} is {contrast:0.00}:1 (>= {minimum})");
            }
            if (fileName == "Template.theme")
            {
                // A theme file sets the Theme.* keys; the system colours follow them (see SystemColorLinks).
                Dictionary<string, string> themeKeys = dark.Where(pair => pair.Key.StartsWith("Theme.", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => pair.Value);
                Check(example.Colors.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(themeKeys.Keys.OrderBy(k => k, StringComparer.Ordinal)), "Template.theme lists every Theme.* key of the palette");
                Check(themeKeys.All(pair => ThemeLibrary.TryParseColor(pair.Value, out uint value) && example.Colors[pair.Key] == value), "Template.theme holds the colours of Dark");
            }
        }
    }
}
