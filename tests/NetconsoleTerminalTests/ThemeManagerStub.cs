using System;

namespace SmarTTY.GUI.Styles;

/// <summary>
/// Stands in for the WPF theme manager, which Mono cannot load: the controls get the fallback colours they ask for, as
/// before a theme is applied.
/// </summary>
public static class ThemeManager
{
    public static event EventHandler ThemeChanged
    {
        add { }
        remove { }
    }

    public static System.Drawing.Color GetColor(string key, System.Drawing.Color fallback)
    {
        return fallback;
    }
}
