using System.Collections.Generic;

namespace SmarTTY.LocalRepo;

/// <summary>
/// Tells whether an SSH session runs OpenWrt, and with which package manager (opkg or apk). The "Tools > Local OpenWrt repository server"
/// item is shown only then: the server feeds opkg or apk on OpenWrt, so on any other host it has nothing to serve.
/// Pure logic without SSH, tested by tests/LocalRepoTests.
/// </summary>
public static class OpenWrtDetector
{
    public const string OpenWrtMarker = "SMARTTY_OS=openwrt";

    public const string OpkgMarker = "SMARTTY_PKG=opkg";

    public const string ApkMarker = "SMARTTY_PKG=apk";

    /// <summary>
    /// Shell command that reports whether the host is OpenWrt (the release file, the opkg configuration directory or the
    /// custom apk repository list is present, or /etc/os-release names OpenWrt as the system or its base, as derivatives
    /// such as ImmortalWrt do with ID_LIKE="lede openwrt") and which package manager it has: apk (OpenWrt 25.12 and newer)
    /// or opkg. Both answers are printed as markers so that a missing command, a shell banner or an error line cannot be
    /// mistaken for a positive answer. When both managers are installed, apk wins, like in the package manager window.
    /// </summary>
    public const string ProbeCommand =
        "if [ -f /etc/openwrt_release ] || [ -d /etc/opkg ] || [ -f /etc/apk/repositories.d/customfeeds.list ] || " +
        "grep -qsE '^(ID|ID_LIKE)=.*(openwrt|lede)' /etc/os-release; then echo " + OpenWrtMarker + "; else echo SMARTTY_OS=other; fi; " +
        "pm=none; command -v opkg >/dev/null 2>&1 && pm=opkg; command -v apk >/dev/null 2>&1 && pm=apk; echo SMARTTY_PKG=$pm";

    /// <summary>
    /// Interprets the output of <see cref="ProbeCommand"/>: the package manager of an OpenWrt host, or null when the host
    /// is not OpenWrt or has neither opkg nor apk.
    /// </summary>
    public static LocalRepoPackageManager? Detect(IEnumerable<string> lines)
    {
        if (lines == null)
        {
            return null;
        }
        bool openWrt = false;
        bool opkg = false;
        bool apk = false;
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
            else if (line == ApkMarker)
            {
                apk = true;
            }
        }
        if (!openWrt)
        {
            return null;
        }
        if (apk)
        {
            return LocalRepoPackageManager.Apk;
        }
        return opkg ? LocalRepoPackageManager.Opkg : (LocalRepoPackageManager?)null;
    }

    /// <summary>True when <see cref="Detect"/> finds a package manager: the host is OpenWrt with opkg or apk.</summary>
    public static bool Parse(IEnumerable<string> lines)
    {
        return Detect(lines) != null;
    }
}
