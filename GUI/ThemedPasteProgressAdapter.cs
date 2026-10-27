using SmarTTY.Controls;

namespace SmarTTY.GUI;

/// <summary>
/// Routes the progress window of a paste that takes time (<see cref="ThemedPasteProgress"/>) to the themed WPF
/// <see cref="PasteProgressWindow"/>. The controls create the window through the facade and talk to it through
/// <see cref="IPasteProgress"/>.
/// </summary>
internal static class ThemedPasteProgressAdapter
{
    /// <summary>Wires the adapter into the controls. Called once from <c>App.OnStartup</c>.</summary>
    public static void Install()
    {
        ThemedPasteProgress.CreateImpl = title => new PasteProgressWindow(title);
    }
}
