using System.Diagnostics;

namespace SmarTTY.Utilities;

/// <summary>Opens a file or a folder with the program that Windows associates with it (Explorer for a folder).</summary>
internal static class ShellOpen
{
    /// <summary>ProcessStartInfo.UseShellExecute is false by default on .NET (it was true on .NET Framework), so it is set explicitly.</summary>
    public static void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
