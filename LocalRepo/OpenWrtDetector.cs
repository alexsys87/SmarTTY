using System.Collections.Generic;

namespace SmarTTY.LocalRepo;

/// <summary>
/// Tells whether an SSH session runs OpenWrt with the opkg package manager. The "Tools > Local OpenWrt repository server"
/// item is shown only then: the server feeds opkg on OpenWrt, so on any other host it has nothing to serve.
/// Pure logic without SSH, tested by tests/LocalRepoTests.
/// </summary>
public static class OpenWrtDetector
{
    public const string OpenWrtMarker = "SMARTTY_OS=openwrt";

    public const string OpkgMarker = "SMARTTY_PKG=opkg";

    /// <summary>
    /// Shell command that reports whether the host is OpenWrt (the release file or the opkg configuration directory is
    /// present) and whether opkg is installed. Both answers are printed as markers so that a missing command, a shell
    /// banner or an error line cannot be mistaken for a positive answer.
    /// </summary>
    public const string ProbeCommand =
        "if [ -f /etc/openwrt_release ] || [ -d /etc/opkg ]; then echo " + OpenWrtMarker + "; else echo SMARTTY_OS=other; fi; " +
        "if command -v opkg >/dev/null 2>&1; then echo " + OpkgMarker + "; else echo SMARTTY_PKG=none; fi";

    /// <summary>Interprets the output of <see cref="ProbeCommand"/>: true only when both markers are present.</summary>
    public static bool Parse(IEnumerable<string> lines)
    {
        if (lines == null)
        {
            return false;
        }
        bool openWrt = false;
        bool opkg = false;
        foreach (string rawLine in lines)
        {
            string line = rawLine?.Trim();
            if (line == OpenWrtMarker)
            {
                openWrt = true;
            }
            else if (line == OpkgMarker)
            {
                opkg = true;
            }
        }
        return openWrt && opkg;
    }
}
