using System;
using System.Windows;
using System.Windows.Interop;

namespace SmarTTY.GUI;

/// <summary>
/// Places a dialog over the window the user is working in. A WPF window that is shown without an owner can end up
/// behind the window that opened it, and <see cref="WindowStartupLocation.CenterOwner"/> requires an owner.
/// </summary>
internal static class WindowOwnership
{
    /// <summary>
    /// The WPF window behind a Win32 handle (a terminal's anchor gives the handle of the window it is in), so a dialog is
    /// owned by the window that asked for it; null for no handle or one that is not a WPF window, and the dialog then
    /// takes the active window (<see cref="Attach"/>). Resolved on the UI thread, because a window belongs to the thread
    /// that created it.
    /// </summary>
    internal static Window FromHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        Application application = Application.Current;
        if (application != null && !application.Dispatcher.CheckAccess())
        {
            return application.Dispatcher.Invoke(() => HwndSource.FromHwnd(handle)?.RootVisual as Window);
        }
        return HwndSource.FromHwnd(handle)?.RootVisual as Window;
    }

    /// <summary>The WPF window of a Win32 owner (see <see cref="FromHandle"/>); null for none.</summary>
    internal static Window Of(System.Windows.Forms.IWin32Window owner)
    {
        return owner != null ? FromHandle(owner.Handle) : null;
    }

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
    /// Makes <paramref name="dialog"/> a child of <paramref name="owner"/>, centered on it. An owner that is missing, or
    /// is not on the screen (it would be refused: a window that was never shown cannot own one), is replaced by the
    /// window the user is working in, because a dialog without an owner may open behind the other windows. Only when the
    /// application has no visible window at all the dialog is centered on the screen instead.
    /// </summary>
    internal static void Attach(Window dialog, Window owner)
    {
        if (owner == null || !owner.IsVisible)
        {
            owner = ActiveWindow();
        }
        if (owner != null && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
    }
}
