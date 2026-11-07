namespace SmarTTY.Utilities;

/// <summary>
/// What the device terminals (Serial, Telnet, TCP, Netconsole) may turn into a link to a local path. The text comes from the
/// device and is not trusted. They carry no UI dependency, so the test harness can check them without a window.
/// </summary>
public static class LocalLinkRules
{
    /// <summary>
    /// True for a path that is not on this machine's own disks: a network path (\\host\share, //host/share, also a mix of the
    /// two slashes) or a device path (\\?\, \\.\). Windows connects to the host of such a path as soon as it is asked
    /// whether the path exists, and offers the credentials of the user to it, so a name like that is never looked at.
    /// </summary>
    public static bool IsNetworkOrDevicePath(string path)
    {
        return path != null && path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);
    }

    private static bool IsSeparator(char ch)
    {
        return ch == '/' || ch == '\\';
    }
}
