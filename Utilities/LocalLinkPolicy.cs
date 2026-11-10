using System;

namespace SmarTTY.Utilities;

/// <summary>
/// What a device terminal (COM, Telnet, Raw TCP, Netconsole) makes a link of. Everything in it is what the device on the other end
/// printed, or a log of that, so a path in it is a path of that device, never a file or folder of this computer: "/tmp" of a Linux
/// host would be C:\tmp here (opened in Explorer by a click), "C:\Users" of a Windows host would open the folder of this computer,
/// and a network path would make Windows connect to its host and offer the credentials of the user. Only an address is a link; a
/// click opens it in the browser. Tested by tests/LocalLinkPolicyTests.
/// </summary>
public static class LocalLinkPolicy
{
    /// <summary>True for an http, https or ftp address: the only text of a device terminal that a click opens.</summary>
    public static bool IsUrl(string text)
    {
        return text != null && (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase));
    }
}
