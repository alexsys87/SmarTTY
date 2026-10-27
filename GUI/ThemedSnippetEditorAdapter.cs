using System;
using System.Windows;
using System.Windows.Interop;
using SmarTTY.Controls;

namespace SmarTTY.GUI;

/// <summary>
/// Routes the snippet editor of the harness-compiled controls (<see cref="ThemedSnippetEditor"/>) to the themed WPF
/// <see cref="SnippetEditWindow"/>. The controls keep the WinForms call shape; this is where the owner window is
/// resolved and the request is handed to the window.
/// </summary>
internal static class ThemedSnippetEditorAdapter
{
    /// <summary>Wires the adapter into the controls. Called once from <c>App.OnStartup</c>.</summary>
    public static void Install()
    {
        ThemedSnippetEditor.EditImpl = Edit;
    }

    private static ThemedSnippetEditorResult Edit(string name, string text, bool canDelete, IntPtr handle)
    {
        return SnippetEditWindow.Edit(FromHandle(handle), name, text, canDelete);
    }

    /// <summary>
    /// The WPF window behind a Win32 handle, so the editor is owned by the window that asked for it. A handle that
    /// does not belong to a WPF window (the WinForms controls pass their own) yields null, and the editor falls back
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
