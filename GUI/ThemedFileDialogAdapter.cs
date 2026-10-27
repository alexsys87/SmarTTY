using SmarTTY.Controls;
using System;
using System.Windows;
using System.Windows.Interop;

namespace SmarTTY.GUI;

/// <summary>
/// Routes the file choosers of the harness-compiled controls (<see cref="ThemedFileDialog"/>) to the themed WPF
/// <see cref="LocalFileDialogWindow"/>. The controls keep the settings of the WinForms dialogs as their vocabulary;
/// this is where the owner window is resolved and the request is handed to the window.
/// </summary>
internal static class ThemedFileDialogAdapter
{
    /// <summary>Wires the adapter into the controls. Called once from <c>App.OnStartup</c>.</summary>
    public static void Install()
    {
        ThemedFileDialog.ShowImpl = Show;
    }

    private static ThemedFileDialogResult Show(ThemedFileDialogRequest request)
    {
        return LocalFileDialogWindow.Browse(request, FromHandle(request.OwnerHandle));
    }

    /// <summary>
    /// The WPF window behind a Win32 handle, so the dialog is owned by the window that asked for it. A handle that
    /// does not belong to a WPF window (the WinForms controls pass their own) yields null, and the dialog falls back
    /// to the active window. Resolved on the UI thread, because a window belongs to the thread that created it.
    /// </summary>
    private static Window FromHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        System.Windows.Application application = System.Windows.Application.Current;
        if (application != null && !application.Dispatcher.CheckAccess())
        {
            return application.Dispatcher.Invoke(() => ResolveWindow(handle));
        }
        return ResolveWindow(handle);
    }

    private static Window ResolveWindow(IntPtr handle)
    {
        return HwndSource.FromHwnd(handle)?.RootVisual as Window;
    }
}
