using SmarTTY.GUI.Styles;
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// Turns a <see cref="SmarTTYTheme"/> into one of the colours of its palette (the palette key is the converter
/// parameter, for example "Theme.Accent"), so the theme list can show a small preview of every theme.
/// </summary>
public class ThemeSwatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SmarTTYTheme theme && parameter is string key)
        {
            return ThemeManager.GetPaletteBrush(theme, key, Brushes.Transparent);
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
