using System;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// The folder choosers of the controls that the Mono test harness compiles (<c>Controls/</c>). Those cannot reference
/// <c>GUI/</c>, so they cannot show the themed WPF browser (<c>GUI/LocalFolderBrowserWindow</c>) directly, and the
/// WinForms <c>FolderBrowserDialog</c> draws a plain tree that ignores the application theme. This facade keeps the
/// call shape of the native dialog and routes it to the themed browser that <c>App</c> injects through
/// <see cref="BrowseImpl"/>; without one it falls back to the WinForms dialog, so the controls still work on their own.
/// </summary>
internal static class ThemedFolderBrowser
{
    /// <summary>
    /// Asks for a folder. Injected by <c>App</c>: (description, initial path) -> the selected path, or null when the
    /// user cancelled.
    /// </summary>
    public static Func<string, string, string> BrowseImpl;

    /// <summary>
    /// Asks the user for a folder, starting at <paramref name="initialPath"/> when it is given. Returns the selected
    /// path, or null when the user cancelled.
    /// </summary>
    public static string Browse(IWin32Window owner, string description, string initialPath = null)
    {
        Func<string, string, string> browse = BrowseImpl;
        if (browse != null)
        {
            return browse(description, initialPath);
        }
        using FolderBrowserDialog dialog = new FolderBrowserDialog
        {
            Description = description,
            ShowNewFolderButton = true
        };
        if (!string.IsNullOrEmpty(initialPath))
        {
            dialog.SelectedPath = initialPath;
        }
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
    }
}
