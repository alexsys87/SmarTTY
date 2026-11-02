using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// Places a dialog over the window the user is working in. A WPF window that is shown without an owner can end up
/// behind the window that opened it, and <see cref="WindowStartupLocation.CenterOwner"/> requires an owner.
/// </summary>
internal static class WindowOwnership
{
    /// <summary>
    /// The window the user is working in: the active visible one. While none of the windows is active (the user has
    /// switched to another program, which is likely during a connection that takes a while to fail) it is the
    /// top-most visible window of the application, so that a dialog still has an owner. Null only when no window is visible.
    /// </summary>
    internal static Window ActiveWindow()
    {
        WindowCollection windows = Application.Current?.Windows;
        if (windows == null)
        {
            return null;
        }
        foreach (Window window in windows)
        {
            if (window.IsActive && window.IsVisible)
            {
                return window;
            }
        }
        return TopVisibleWindow(windows);
    }

    /// <summary>
    /// Without an owner a modal dialog that opens while another program is in front is not in the taskbar and stays
    /// behind that program, while every window of this application is disabled by it: the application seems to ignore
    /// all clicks. Owned by the window the user left last, the dialog stays on top of it and is brought forward with it.
    /// </summary>
    private static Window TopVisibleWindow(WindowCollection windows)
    {
        Window current = Application.Current.MainWindow;
        if (current == null || !current.IsVisible)
        {
            current = null;
            foreach (Window window in windows)
            {
                if (window.IsVisible && window.Owner == null)
                {
                    current = window;
                    break;
                }
            }
        }
        // The dialog the user was in is the deepest of the windows that own each other.
        while (current != null)
        {
            Window deeper = null;
            foreach (Window owned in current.OwnedWindows)
            {
                if (owned.IsVisible)
                {
                    deeper = owned;
                }
            }
            if (deeper == null)
            {
                break;
            }
            current = deeper;
        }
        return current;
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
