using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// The paste confirmation of the controls that the Mono test harness compiles (<c>Controls/</c>). Those cannot
/// reference <c>GUI/</c>, so they cannot show the themed WPF window (<c>GUI/PasteConfirmWindow</c>) directly, and the
/// WinForms <c>Forms/PasteConfirmForm</c> draws a plain window that ignores the application theme. This facade routes
/// the question to the themed window that <c>App</c> injects through <see cref="ConfirmImpl"/>; without one it falls
/// back to the WinForms form, so the controls still work on their own.
/// </summary>
internal static class ThemedPasteConfirm
{
    /// <summary>
    /// Shows the themed paste confirmation. Injected by <c>App</c>:
    /// (summary, preview, dangers, offerDontAsk, owner handle) -> the answer.
    /// </summary>
    public static Func<string, string, IList<string>, bool, IntPtr, ThemedPasteConfirmResult> ConfirmImpl;

    /// <summary>
    /// Asks whether a paste should really go to the terminal. Returns the answer, or null when the user cancelled.
    /// </summary>
    public static ThemedPasteConfirmResult Confirm(IWin32Window owner, string summary, string preview, IList<string> dangers, bool offerDontAsk)
    {
        Func<string, string, IList<string>, bool, IntPtr, ThemedPasteConfirmResult> confirm = ConfirmImpl;
        if (confirm != null)
        {
            return confirm(summary, preview, dangers, offerDontAsk, owner?.Handle ?? IntPtr.Zero);
        }
        return ConfirmNative(owner, summary, preview, dangers, offerDontAsk);
    }

    /// <summary>
    /// The fallback used when no themed window is installed (the Mono harness): the WinForms form.
    /// </summary>
    private static ThemedPasteConfirmResult ConfirmNative(IWin32Window owner, string summary, string preview, IList<string> dangers, bool offerDontAsk)
    {
        using SmarTTY.Forms.PasteConfirmForm form = new SmarTTY.Forms.PasteConfirmForm(summary, preview, dangers, offerDontAsk);
        System.Windows.Forms.DialogResult result = owner != null ? form.ShowDialog(owner) : form.ShowDialog();
        if (result != System.Windows.Forms.DialogResult.OK)
        {
            return null;
        }
        return new ThemedPasteConfirmResult { DontAskAgain = form.DontAskAgain };
    }
}

/// <summary>What the user answered in the paste confirmation.</summary>
internal sealed class ThemedPasteConfirmResult
{
    /// <summary>True when the user asked not to be asked about multi-line pastes again.</summary>
    public bool DontAskAgain { get; set; }
}
