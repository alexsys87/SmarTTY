using System.Collections.Generic;

namespace VisualTTY;

/// <summary>
/// One entry of the theme list: a built-in theme, or a custom theme read from a <c>*.theme</c> file in the
/// <c>Themes</c> folder next to the program (see <see cref="ThemeLibrary"/>). The instances are created by
/// <see cref="ThemeLibrary"/> only, and the list hands out the same instance for the same theme until it is reloaded.
/// </summary>
public sealed class ThemeChoice
{
    private static readonly IReadOnlyDictionary<string, uint> NoColors = new Dictionary<string, uint>();

    private static readonly IReadOnlyList<string> NoProblems = new string[0];

    /// <summary>The name shown in the theme list.</summary>
    public string Name { get; }

    /// <summary>Whether the theme draws dark text on light surfaces (it selects the light editor colours and icons).</summary>
    public bool IsLight { get; }

    /// <summary>
    /// The built-in theme whose palette this entry loads. For a custom theme it is the theme that supplies every
    /// colour the file does not set, and the theme the program falls back to when the file is gone.
    /// </summary>
    public SmarTTYTheme BuiltIn { get; }

    public bool IsCustom { get; }

    /// <summary>The name of the theme file inside the themes folder; null for a built-in theme.</summary>
    public string FileName { get; }

    /// <summary>The full path of the theme file; null for a built-in theme.</summary>
    public string FilePath { get; }

    /// <summary>The colours a custom theme sets: palette key (always with the "Theme." prefix) to ARGB.</summary>
    public IReadOnlyDictionary<string, uint> Colors { get; }

    /// <summary>The lines of the theme file that were skipped, each with the reason; empty for a good file.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>A stable identifier: the enum name of a built-in theme, "custom:" and the file name for a custom one.</summary>
    public string Id => IsCustom ? "custom:" + FileName : BuiltIn.ToString();

    /// <summary>The tool tip of the entry: where a custom theme comes from and what was wrong in its file.</summary>
    public string ToolTip
    {
        get
        {
            if (!IsCustom)
            {
                return null;
            }
            string text = "Custom theme: " + FilePath;
            if (Problems.Count > 0)
            {
                text += "\r\nSkipped: " + string.Join("; ", Problems);
            }
            return text;
        }
    }

    internal ThemeChoice(string name, bool isLight, SmarTTYTheme builtIn)
    {
        Name = name;
        IsLight = isLight;
        BuiltIn = builtIn;
        Colors = NoColors;
        Problems = NoProblems;
    }

    internal ThemeChoice(string name, bool isLight, SmarTTYTheme baseTheme, string fileName, string filePath, IReadOnlyDictionary<string, uint> colors, IReadOnlyList<string> problems)
    {
        Name = name;
        IsLight = isLight;
        BuiltIn = baseTheme;
        IsCustom = true;
        FileName = fileName;
        FilePath = filePath;
        Colors = colors;
        Problems = problems;
    }

    public override string ToString()
    {
        return Name;
    }
}
