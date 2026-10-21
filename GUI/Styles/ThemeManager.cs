using System;
using System.Windows;
using System.Windows.Media;
using VisualTTY;

namespace SmarTTY.GUI.Styles;

/// <summary>
/// Applies the dark or the light theme to the whole application.
/// </summary>
/// <remarks>
/// Application.Resources holds two dictionaries: the control styles (Controls.xaml, loaded once) and the palette of the
/// current theme. The styles take every colour from the palette through DynamicResource, so replacing the palette
/// restyles all open windows, popups, context menus and tooltips at once.
/// </remarks>
public static class ThemeManager
{
    private static readonly Uri ControlsUri = new Uri("pack://application:,,,/GUI/Styles/Controls.xaml");

    private static ResourceDictionary _Controls;

    private static ResourceDictionary _Palette;

    public static SmarTTYTheme? Current { get; private set; }

    /// <summary>Raised after a new palette is in place; WinForms controls repaint themselves with <see cref="GetColor"/>.</summary>
    public static event EventHandler ThemeChanged;

    /// <summary>
    /// A palette colour (a "Theme.*" key) for WinForms controls, which cannot use the WPF resources directly.
    /// Returns <paramref name="fallback"/> before a theme is applied.
    /// </summary>
    public static System.Drawing.Color GetColor(string key, System.Drawing.Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            Color color = brush.Color;
            return System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
        }
        return fallback;
    }

    public static void Apply(SmarTTYTheme theme)
    {
        Application application = Application.Current;
        if (application == null || Current == theme)
        {
            return;
        }
        var mergedDictionaries = application.Resources.MergedDictionaries;
        ResourceDictionary palette = new ResourceDictionary { Source = PaletteUri(theme) };
        if (_Palette != null)
        {
            mergedDictionaries[mergedDictionaries.IndexOf(_Palette)] = palette;
        }
        else
        {
            mergedDictionaries.Add(palette);
        }
        _Palette = palette;
        if (_Controls == null)
        {
            _Controls = new ResourceDictionary { Source = ControlsUri };
            mergedDictionaries.Add(_Controls);
        }
        Current = theme;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static Uri PaletteUri(SmarTTYTheme theme)
    {
        string name = theme == SmarTTYTheme.Light ? "Light" : "Dark";
        return new Uri($"pack://application:,,,/GUI/Styles/{name}/Colors.xaml");
    }
}
