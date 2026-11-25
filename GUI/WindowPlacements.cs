using System.Linq;
using System.Windows;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// The remembered place of the main window and of the connection window (<see cref="CommonGUIPreferences.WindowPlacement"/>, which the
/// preferences file holds) put on a window and read from it; it was <c>Apply</c> and <c>Save</c> of the struct, which the settings types of
/// <c>VisualTTY/</c> may not have now that they are compiled without WPF.
/// </summary>
internal static class WindowPlacements
{
    /// <summary>Puts the window where it was remembered, when the monitors are those it was remembered on.</summary>
    public static void Apply(CommonGUIPreferences.WindowPlacement placement, Window wnd)
    {
        ScreenRect[] first = SmarTTY.Native.DisplayMonitors.AllBounds();
        if (placement.ScreenRectanglesWhenWindowRectangleRemembered != null && first.SequenceEqual(placement.ScreenRectanglesWhenWindowRectangleRemembered))
        {
            wnd.WindowStartupLocation = WindowStartupLocation.Manual;
            wnd.Top = placement.LastKnownConnectionWindowRectangle.Top;
            wnd.Left = placement.LastKnownConnectionWindowRectangle.Left;
            ((FrameworkElement)wnd).Width = placement.LastKnownConnectionWindowRectangle.Width;
            ((FrameworkElement)wnd).Height = placement.LastKnownConnectionWindowRectangle.Height;
        }
    }

    /// <summary>The place of the window and the monitors it is on now, to be remembered.</summary>
    public static CommonGUIPreferences.WindowPlacement Of(Window wnd)
    {
        return new CommonGUIPreferences.WindowPlacement
        {
            LastKnownConnectionWindowRectangle = new ScreenRect((int)wnd.Left, (int)wnd.Top, (int)((FrameworkElement)wnd).Width, (int)((FrameworkElement)wnd).Height),
            ScreenRectanglesWhenWindowRectangleRemembered = SmarTTY.Native.DisplayMonitors.AllBounds()
        };
    }
}
