using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using SmarTTY.Controls;

namespace SmarTTY.GUI;

/// <summary>
/// Routes the paste confirmation of the harness-compiled controls (<see cref="ThemedPasteConfirm"/>) to the themed WPF
/// <see cref="PasteConfirmWindow"/>. The controls keep the WinForms call shape; this is where the owner window is
/// resolved and the request is handed to the window.
/// </summary>
internal static class ThemedPasteConfirmAdapter
{
    /// <summary>Wires the adapter into the controls. Called once from <c>App.OnStartup</c>.</summary>
    public static void Install()
    {
        ThemedPasteConfirm.ConfirmImpl = Confirm;
    }

    private static ThemedPasteConfirmResult Confirm(string summary, string preview, IList<string> dangers, bool offerDontAsk, IntPtr handle)
    {
        return PasteConfirmWindow.Confirm(FromHandle(handle), summary, preview, dangers, offerDontAsk);
    }

    /// <summary>
    /// The WPF window behind a Win32 handle, so the confirmation is owned by the window that asked for it. A handle
    /// that does not belong to a WPF window (the WinForms controls pass their own) yields null, and the window falls
    /// back to the active window. Resolved on the UI thread, because a window belongs to the thread that created it.
    /// </summary>
    private static Window FromHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        Application application = Application.Current;
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
