using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using VisualTTY;

namespace SmarTTY.GUI.Styles;

/// <summary>
/// Applies one of the themes to the whole application.
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

    /// <summary>The theme that is applied now; null before the first theme is applied.</summary>
    public static ThemeChoice Current { get; private set; }

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

    // The palettes built for the theme list previews; an entry goes away with its ThemeChoice (the list is reloaded).
    private static readonly ConditionalWeakTable<ThemeChoice, ResourceDictionary> _Previews = new ConditionalWeakTable<ThemeChoice, ResourceDictionary>();

    /// <summary>
    /// A brush of the palette of <paramref name="theme"/> (a "Theme.*" key) without applying that theme: the theme list
    /// of the settings shows what each theme looks like. Returns <paramref name="fallback"/> when the palette or the key
    /// cannot be read.
    /// </summary>
    public static Brush GetPaletteBrush(ThemeChoice theme, string key, Brush fallback)
    {
        try
        {
            if (theme != null)
            {
                ResourceDictionary palette = _Previews.GetValue(theme, BuildPalette);
                if (palette.Contains(key) && palette[key] is Brush brush)
                {
                    return brush;
                }
            }
        }
        catch
        {
        }
        return fallback;
    }

    /// <summary>Applies the theme the settings select (a custom theme when its file is there, else the built-in one).</summary>
    public static void Apply(GlobalSettings settings)
    {
        Apply(settings?.ThemeChoice ?? ThemeLibrary.Find(SmarTTYTheme.Dark, null));
    }

    public static void Apply(SmarTTYTheme theme)
    {
        Apply(ThemeLibrary.Find(theme, null));
    }

    public static void Apply(ThemeChoice theme)
    {
        Application application = Application.Current;
        if (application == null || theme == null || ReferenceEquals(Current, theme))
        {
            return;
        }
        var mergedDictionaries = application.Resources.MergedDictionaries;
        // App.xaml already holds a palette and the styles: take them over instead of adding second copies.
        _Palette ??= FindDictionary(mergedDictionaries, "/Colors.xaml");
        _Controls ??= FindDictionary(mergedDictionaries, "/Controls.xaml");
        ResourceDictionary palette = BuildPalette(theme);
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

    /// <summary>
    /// The palette of a theme: the dictionary of a built-in theme as it is; for a custom theme a copy of the palette of its
    /// base theme with the colours of the theme file put over it. A file can set only the keys the palette has (the others
    /// are ignored), so a typo cannot leave a DynamicResource unresolved, and whatever the file leaves out keeps the colour of
    /// the base theme.
    /// </summary>
    private static ResourceDictionary BuildPalette(ThemeChoice theme)
    {
        ResourceDictionary basePalette = new ResourceDictionary { Source = PaletteUri(theme.BuiltIn) };
        if (!theme.IsCustom || theme.Colors.Count == 0)
        {
            return basePalette;
        }
        try
        {
            // Every entry of the base palette is kept, the SystemColors ones included (their keys are not strings).
            ResourceDictionary palette = new ResourceDictionary();
            Dictionary<string, string> keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in basePalette)
            {
                palette.Add(entry.Key, entry.Value);
                if (entry.Key is string name)
                {
                    keys[name] = name;
                }
            }
            Dictionary<string, Brush> changed = new Dictionary<string, Brush>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, uint> color in theme.Colors)
            {
                if (keys.TryGetValue(color.Key, out string key))
                {
                    uint argb = color.Value;
                    SolidColorBrush brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
                    brush.Freeze();
                    palette[key] = brush;
                    changed[key] = brush;
                }
            }
            // A system colour is the colour of one palette key in the base theme (ThemeLibrary.SystemColorLinks): when the
            // file sets that key, the system colour follows it, so the stock controls match the rest of the theme.
            foreach (KeyValuePair<string, string[]> link in ThemeLibrary.SystemColorLinks)
            {
                ResourceKey systemKey = typeof(SystemColors).GetProperty(link.Key)?.GetValue(null, null) as ResourceKey;
                if (systemKey == null || !(basePalette[systemKey] is SolidColorBrush current))
                {
                    continue;
                }
                foreach (string candidate in link.Value)
                {
                    if (basePalette[candidate] is SolidColorBrush source && source.Color == current.Color)
                    {
                        if (changed.TryGetValue(candidate, out Brush replacement))
                        {
                            palette[systemKey] = replacement;
                        }
                        break;
                    }
                }
            }
            return palette;
        }
        catch
        {
            // A theme that cannot be built is shown as its base theme.
            return basePalette;
        }
    }

    private static ResourceDictionary FindDictionary(Collection<ResourceDictionary> dictionaries, string fileName)
    {
        foreach (ResourceDictionary dictionary in dictionaries)
        {
            if (dictionary.Source != null && dictionary.Source.OriginalString.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return dictionary;
            }
        }
        return null;
    }

    private static Uri PaletteUri(SmarTTYTheme theme)
    {
        return new Uri($"pack://application:,,,/GUI/Styles/{theme.PaletteName()}/Colors.xaml");
    }
}
