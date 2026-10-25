using System;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// The message boxes of the controls that the Mono test harness compiles (<c>Controls/</c> and
/// <c>SshConnector</c>). Those cannot reference <c>GUI/</c>, so they cannot show the themed WPF dialog directly, and
/// the WinForms <c>MessageBox</c> draws a plain box that ignores the application theme. This facade keeps the
/// WinForms call shape and routes it to the themed dialog that <c>App</c> injects through <see cref="ShowImpl"/>;
/// without one it falls back to the WinForms box, so the controls still work on their own.
/// </summary>
internal static class ThemedMessageBox
{
    /// <summary>
    /// Shows the themed message box and returns the chosen button. Injected by <c>App</c>:
    /// (text, caption, buttons, icon, owner handle).
    /// </summary>
    public static Func<string, string, MessageBoxButtons, MessageBoxIcon, IntPtr, DialogResult> ShowImpl;

    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        Func<string, string, MessageBoxButtons, MessageBoxIcon, IntPtr, DialogResult> show = ShowImpl;
        return show != null
            ? show(text, caption, buttons, icon, IntPtr.Zero)
            : MessageBox.Show(text, caption, buttons, icon);
    }

    public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        Func<string, string, MessageBoxButtons, MessageBoxIcon, IntPtr, DialogResult> show = ShowImpl;
        return show != null
            ? show(text, caption, buttons, icon, owner?.Handle ?? IntPtr.Zero)
            : MessageBox.Show(owner, text, caption, buttons, icon);
    }
}
