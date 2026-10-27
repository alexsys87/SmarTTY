
using SmarTTY.Utilities;

namespace VisualTTY;

/// <summary>
/// The application themes. Each value names a palette under <c>GUI/Styles/&lt;name&gt;/Colors.xaml</c>; the control
/// styles take every colour from the palette, so choosing a theme only swaps that dictionary (see ThemeManager).
/// </summary>
public enum SmarTTYTheme
{
    [EnumDisplayText("Dark")]
    Dark,

    [EnumDisplayText("Light")]
    Light,

    [EnumDisplayText("Blue")]
    Blue,

    [EnumDisplayText("Midnight")]
    Midnight,

    [EnumDisplayText("Nord")]
    Nord,

    [EnumDisplayText("Solarized Dark")]
    SolarizedDark,

    [EnumDisplayText("Solarized Light")]
    SolarizedLight,

    [EnumDisplayText("High Contrast")]
    HighContrast,

    [EnumDisplayText("Dracula")]
    Dracula,

    [EnumDisplayText("Gruvbox Dark")]
    GruvboxDark,

    [EnumDisplayText("Monokai")]
    Monokai,

    [EnumDisplayText("One Dark")]
    OneDark,

    [EnumDisplayText("Sepia")]
    Sepia
}

/// <summary>Theme metadata: the palette folder and whether the theme is a light one.</summary>
public static class SmarTTYThemes
{
    /// <summary>The folder of the palette under <c>GUI/Styles</c> that <paramref name="theme"/> loads.</summary>
    public static string PaletteName(this SmarTTYTheme theme)
    {
        return theme switch
        {
            SmarTTYTheme.Light => "Light",
            SmarTTYTheme.Blue => "Blue",
            SmarTTYTheme.Midnight => "Midnight",
            SmarTTYTheme.Nord => "Nord",
            SmarTTYTheme.SolarizedDark => "SolarizedDark",
            SmarTTYTheme.SolarizedLight => "SolarizedLight",
            SmarTTYTheme.HighContrast => "HighContrast",
            SmarTTYTheme.Dracula => "Dracula",
            SmarTTYTheme.GruvboxDark => "GruvboxDark",
            SmarTTYTheme.Monokai => "Monokai",
            SmarTTYTheme.OneDark => "OneDark",
            SmarTTYTheme.Sepia => "Sepia",
            _ => "Dark"
        };
    }

    /// <summary>
    /// Whether the theme draws dark text on light surfaces. The icons that exist in a dark and a light variant, the
    /// terminal popup and the editor colour family all pick their light or dark form with this.
    /// </summary>
    public static bool IsLight(this SmarTTYTheme theme)
    {
        return theme is SmarTTYTheme.Light or SmarTTYTheme.SolarizedLight or SmarTTYTheme.Sepia;
    }
}
