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

    /// <summary>
    /// True for a path that names a file or folder of this machine: one with a drive letter (C:\x\file.log, C:/x/file.log). The other
    /// paths in the text of a device are the device's own: "/tmp" printed by a Linux host behind a telnet or serial line is its /tmp,
    /// and Windows would read it as C:\tmp of this machine (opened in Explorer by a click when such a folder exists here). A name
    /// without a folder ("notes.txt") or one rooted without a drive ("\x") is relative to a directory of the device, not to the
    /// working directory of SmarTTY. Network and device paths are excluded (<see cref="IsNetworkOrDevicePath"/>).
    /// </summary>
    public static bool IsLocalPath(string path)
    {
        return path != null && path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]) && !IsNetworkOrDevicePath(path.Substring(2));
    }

    private static bool IsAsciiLetter(char ch)
    {
        return (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');
    }

    private static bool IsSeparator(char ch)
    {
        return ch == '/' || ch == '\\';
    }
}
