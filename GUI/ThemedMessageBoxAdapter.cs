using SmarTTY.Controls;
using System;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace SmarTTY.GUI;

/// <summary>
/// Routes the message boxes of the harness-compiled controls (<see cref="ThemedMessageBox"/>) to the themed WPF
/// <see cref="MessageDialog"/>. The controls keep the WinForms button set and <see cref="DialogResult"/> as their
/// vocabulary; this is where they are translated to the dialog's icon and button set, and back.
/// </summary>
internal static class ThemedMessageBoxAdapter
{
    /// <summary>Wires the adapter into the controls. Called once from <c>App.OnStartup</c>.</summary>
    public static void Install()
    {
        ThemedMessageBox.ShowImpl = Show;
    }

    private static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, IntPtr owner)
    {
        MessageBoxResult result = MessageDialog.Show(
            text,
            string.IsNullOrEmpty(caption) ? "SmarTTY" : caption,
            ToIcon(icon),
            ToButtons(buttons),
            defaultResult: null,
            owner: FromHandle(owner));
        return ToDialogResult(result);
    }

    private static MessageDialogIcon ToIcon(MessageBoxIcon icon)
    {
        // Hand/Stop/Error share one value, as do Warning/Exclamation, so each appears once.
        switch (icon)
        {
            case MessageBoxIcon.Hand:
                return MessageDialogIcon.Error;
            case MessageBoxIcon.Question:
                return MessageDialogIcon.Question;
            case MessageBoxIcon.Warning:
                return MessageDialogIcon.Warning;
            default:
                return MessageDialogIcon.Information;
        }
    }

    private static MessageDialogButtons ToButtons(MessageBoxButtons buttons)
    {
        switch (buttons)
        {
            case MessageBoxButtons.OKCancel:
                return MessageDialogButtons.OkCancel;
            case MessageBoxButtons.YesNo:
                return MessageDialogButtons.YesNo;
            case MessageBoxButtons.YesNoCancel:
                return MessageDialogButtons.YesNoCancel;
            default:
                return MessageDialogButtons.Ok;
        }
    }

    private static DialogResult ToDialogResult(MessageBoxResult result)
    {
        switch (result)
        {
            case MessageBoxResult.Yes:
                return DialogResult.Yes;
            case MessageBoxResult.No:
                return DialogResult.No;
            case MessageBoxResult.OK:
                return DialogResult.OK;
            default:
                return DialogResult.Cancel;
        }
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
