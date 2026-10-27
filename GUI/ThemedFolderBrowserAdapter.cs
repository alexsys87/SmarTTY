using SmarTTY.Controls;

namespace SmarTTY.GUI;

/// <summary>
/// Routes the folder choosers of the harness-compiled controls (<see cref="ThemedFolderBrowser"/>) to the themed WPF
/// <see cref="LocalFolderBrowserWindow"/>. The controls keep the call shape of the native dialog; this is where it is
/// pointed at the themed browser. The controls raise the request on the UI thread, so showing a modal window is safe.
/// </summary>
internal static class ThemedFolderBrowserAdapter
{
    /// <summary>Wires the adapter into the controls. Called once from <c>App.OnStartup</c>.</summary>
    public static void Install()
    {
        ThemedFolderBrowser.BrowseImpl = (description, initialPath) =>
            LocalFolderBrowserWindow.Browse(initialPath, title: null, description: description);
    }
}
