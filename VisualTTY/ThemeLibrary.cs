using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace VisualTTY;

/// <summary>
/// The themes the program offers: the built-in ones, plus the custom themes found in the <c>Themes</c> folder next to
/// the program. Without that folder (or without <c>*.theme</c> files in it) only the built-in themes exist.
/// </summary>
/// <remarks>
/// A theme file is a plain text file of <c>name = value</c> lines; empty lines and lines starting with <c>#</c>, <c>;</c>
/// or <c>//</c> are comments. <c>Name</c> is the title in the list (the file name by default), <c>Light</c> says whether
/// the theme is a light one (true or false; by default as the base theme), and <c>Base</c> names the built-in theme that
/// supplies every colour the file leaves out (Dark by default, Light when <c>Light = true</c>). Every other line sets a
/// palette colour: the key, with or without the <c>Theme.</c> prefix, and a colour as <c>#RGB</c>, <c>#ARGB</c>,
/// <c>#RRGGBB</c>, <c>#AARRGGBB</c> or <c>Transparent</c>. The keys are the ones of GUI/Styles/Dark/Colors.xaml.
/// </remarks>
public static class ThemeLibrary
{
    /// <summary>The name of the folder (next to the program) that holds the custom themes.</summary>
    public const string FolderName = "Themes";

    /// <summary>The extension of a theme file.</summary>
    public const string FileExtension = ".theme";

    /// <summary>Longer files are not theme files; they are skipped without being read.</summary>
    private const long MaxFileSize = 64 * 1024;

    private const string KeyPrefix = "Theme.";

    private static readonly object _sync = new object();

    private static List<ThemeChoice> _builtIn;

    private static List<ThemeChoice> _all;

    /// <summary>
    /// The palettes also override the stock system colours (<c>SystemColors.WindowBrushKey</c> ...) for the parts of the
    /// stock control templates that still use them. Each of them is the colour of one palette key, which is what this table
    /// lists (the system colour by its name in SystemColors; the keys to try in order, the one with the same colour in the
    /// palette of the base theme is the one it follows). A custom theme that sets such a key sets the system colour with it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> SystemColorLinks = new Dictionary<string, string[]>
    {
        ["WindowBrushKey"] = new[] { "Theme.InputBackground" },
        ["WindowTextBrushKey"] = new[] { "Theme.Text" },
        ["ControlBrushKey"] = new[] { "Theme.SurfaceBackground", "Theme.SurfaceAltBackground" },
        ["ControlTextBrushKey"] = new[] { "Theme.Text" },
        ["GrayTextBrushKey"] = new[] { "Theme.TextDisabled" },
        ["HighlightBrushKey"] = new[] { "Theme.ItemSelectedBackground" },
        ["HighlightTextBrushKey"] = new[] { "Theme.ItemSelectedForeground" },
        ["InactiveSelectionHighlightBrushKey"] = new[] { "Theme.ItemSelectedInactiveBackground" },
        ["InactiveSelectionHighlightTextBrushKey"] = new[] { "Theme.Text" },
        ["MenuBrushKey"] = new[] { "Theme.PopupBackground" },
        ["MenuTextBrushKey"] = new[] { "Theme.Text" },
        ["InfoBrushKey"] = new[] { "Theme.SurfaceBackground" },
        ["InfoTextBrushKey"] = new[] { "Theme.Text" },
        ["ActiveBorderBrushKey"] = new[] { "Theme.Border" }
    };

    /// <summary>The folder the custom themes are read from; settable so that a test can point it somewhere else.</summary>
    public static string FolderPath { get; set; } = DefaultFolder();

    /// <summary>Every theme: the built-in ones in the order of <see cref="SmarTTYTheme"/>, then the custom ones by name.</summary>
    public static IReadOnlyList<ThemeChoice> All
    {
        get
        {
            lock (_sync)
            {
                return _all ??= Load();
            }
        }
    }

    /// <summary>Reads the themes folder again; the entries are new instances afterwards.</summary>
    public static void Reload()
    {
        lock (_sync)
        {
            _all = Load();
        }
    }

    /// <summary>
    /// The entry the settings select: the custom theme in <paramref name="customFileName"/> when that file exists, otherwise the
    /// built-in <paramref name="theme"/> (which is also what a custom theme falls back to after its file is removed).
    /// </summary>
    public static ThemeChoice Find(SmarTTYTheme theme, string customFileName)
    {
        IReadOnlyList<ThemeChoice> all = All;
        if (!string.IsNullOrWhiteSpace(customFileName))
        {
            foreach (ThemeChoice choice in all)
            {
                if (choice.IsCustom && string.Equals(choice.FileName, customFileName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return choice;
                }
            }
        }
        foreach (ThemeChoice choice in all)
        {
            if (!choice.IsCustom && choice.BuiltIn == theme)
            {
                return choice;
            }
        }
        return all[0];
    }

    /// <summary>Reads one theme file. The file never fails as a whole: what cannot be understood is skipped and listed in <see cref="ThemeChoice.Problems"/>.</summary>
    /// <param name="fileName">The name of the file (it gives the default title).</param>
    /// <param name="text">The content of the file.</param>
    /// <param name="filePath">The full path, shown in the tool tip.</param>
    public static ThemeChoice Parse(string fileName, string text, string filePath = null)
    {
        List<string> problems = new List<string>();
        Dictionary<string, uint> colors = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        string name = null;
        SmarTTYTheme? baseTheme = null;
        bool? light = null;
        using (StringReader reader = new StringReader((text ?? "").TrimStart('\uFEFF')))
        {
            int number = 0;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                number++;
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                int equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    problems.Add($"line {number}: expected \"name = value\"");
                    continue;
                }
                string key = line.Substring(0, equals).Trim();
                string rawValue = line.Substring(equals + 1).Trim();
                if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    name = rawValue;
                    continue;
                }
                string value = StripComment(rawValue);
                if (key.Equals("Base", StringComparison.OrdinalIgnoreCase))
                {
                    SmarTTYTheme? found = FindBuiltIn(value);
                    if (found == null)
                    {
                        problems.Add($"line {number}: unknown base theme \"{value}\"");
                    }
                    else
                    {
                        baseTheme = found;
                    }
                }
                else if (key.Equals("Light", StringComparison.OrdinalIgnoreCase))
                {
                    bool? flag = ParseFlag(value);
                    if (flag == null)
                    {
                        problems.Add($"line {number}: Light must be true or false");
                    }
                    else
                    {
                        light = flag;
                    }
                }
                else if (TryParseColor(value, out uint argb))
                {
                    colors[key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase) ? key : KeyPrefix + key] = argb;
                }
                else
                {
                    problems.Add($"line {number}: \"{value}\" is not a colour (use #RRGGBB or #AARRGGBB)");
                }
            }
        }
        // Without a Base the file extends the built-in theme of its family.
        SmarTTYTheme resolvedBase = baseTheme ?? (light == true ? SmarTTYTheme.Light : SmarTTYTheme.Dark);
        bool isLight = light ?? resolvedBase.IsLight();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileNameWithoutExtension(fileName ?? "");
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Custom theme";
        }
        if (name.Length > 60)
        {
            name = name.Substring(0, 60);
        }
        return new ThemeChoice(name, isLight, resolvedBase, fileName, filePath ?? fileName, colors, problems);
    }

    /// <summary>
    /// Reads a colour: <c>#RGB</c>, <c>#ARGB</c>, <c>#RRGGBB</c>, <c>#AARRGGBB</c> or <c>Transparent</c>. The result is ARGB;
    /// a colour without an alpha part is opaque.
    /// </summary>
    public static bool TryParseColor(string text, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        if (text.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            argb = 0x00FFFFFF;
            return true;
        }
        if (text[0] != '#')
        {
            return false;
        }
        string digits = text.Substring(1);
        if (digits.Length == 3 || digits.Length == 4)
        {
            StringBuilder doubled = new StringBuilder();
            foreach (char digit in digits)
            {
                doubled.Append(digit).Append(digit);
            }
            digits = doubled.ToString();
        }
        if (digits.Length != 6 && digits.Length != 8)
        {
            return false;
        }
        if (!uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
        {
            return false;
        }
        argb = digits.Length == 6 ? 0xFF000000u | value : value;
        return true;
    }

    private static string StripComment(string value)
    {
        int comment = value.IndexOf(';');
        return (comment >= 0 ? value.Substring(0, comment) : value).Trim();
    }

    private static bool? ParseFlag(string value)
    {
        switch (value.ToLowerInvariant())
        {
            case "true":
            case "yes":
            case "on":
            case "1":
                return true;
            case "false":
            case "no":
            case "off":
            case "0":
                return false;
            default:
                return null;
        }
    }

    // A built-in theme by its enum name or its title, ignoring case, spaces, dashes and underscores.
    private static SmarTTYTheme? FindBuiltIn(string text)
    {
        string wanted = Normalize(text);
        foreach (ThemeChoice choice in BuiltIn())
        {
            if (Normalize(choice.BuiltIn.ToString()) == wanted || Normalize(choice.Name) == wanted)
            {
                return choice.BuiltIn;
            }
        }
        return null;
    }

    private static string Normalize(string text)
    {
        StringBuilder result = new StringBuilder();
        foreach (char c in text ?? "")
        {
            if (char.IsLetterOrDigit(c))
            {
                result.Append(char.ToLowerInvariant(c));
            }
        }
        return result.ToString();
    }

    private static List<ThemeChoice> BuiltIn()
    {
        lock (_sync)
        {
            if (_builtIn == null)
            {
                List<ThemeChoice> list = new List<ThemeChoice>();
                foreach (SmarTTYTheme theme in Enum.GetValues(typeof(SmarTTYTheme)))
                {
                    FieldInfo field = typeof(SmarTTYTheme).GetField(theme.ToString());
                    EnumDisplayText display = field == null ? null : (EnumDisplayText)Attribute.GetCustomAttribute(field, typeof(EnumDisplayText));
                    list.Add(new ThemeChoice(display?.Text ?? theme.ToString(), theme.IsLight(), theme));
                }
                _builtIn = list;
            }
            return _builtIn;
        }
    }

    private static List<ThemeChoice> Load()
    {
        List<ThemeChoice> all = new List<ThemeChoice>(BuiltIn());
        List<ThemeChoice> custom = new List<ThemeChoice>();
        string folder = FolderPath;
        string[] files = new string[0];
        try
        {
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                // Every file is listed and the extension is checked below: the pattern of GetFiles is case-sensitive on some systems.
                files = Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly);
            }
        }
        catch
        {
            // An unreadable folder is the same as no folder.
        }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            try
            {
                if (!file.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase) || new FileInfo(file).Length > MaxFileSize)
                {
                    continue;
                }
                custom.Add(Parse(Path.GetFileName(file), File.ReadAllText(file, Encoding.UTF8), file));
            }
            catch
            {
                // A file that cannot be read is not offered.
            }
        }
        custom.Sort((a, b) =>
        {
            int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
        });
        all.AddRange(custom);
        return all;
    }

    private static string DefaultFolder()
    {
        try
        {
            string location = Assembly.GetExecutingAssembly().Location;
            string directory = string.IsNullOrEmpty(location) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(location);
            return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, FolderName);
        }
        catch
        {
            return null;
        }
    }
}
