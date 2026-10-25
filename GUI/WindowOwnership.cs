using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// Places a dialog over the window the user is working in. A WPF window that is shown without an owner can end up
/// behind the window that opened it, and <see cref="WindowStartupLocation.CenterOwner"/> requires an owner.
/// </summary>
internal static class WindowOwnership
{
    /// <summary>The active visible window, or null when the application has none.</summary>
    internal static Window ActiveWindow()
    {
        WindowCollection windows = Application.Current?.Windows;
        if (windows != null)
        {
            foreach (Window window in windows)
            {
                if (window.IsActive && window.IsVisible)
                {
                    return window;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Makes <paramref name="dialog"/> a child of <paramref name="owner"/>, centered on it. Without an owner the
    /// dialog is centered on the screen instead.
    /// </summary>
    internal static void Attach(Window dialog, Window owner)
    {
        if (owner != null)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
    }
}
