using System.Collections.Generic;
using System.Linq;

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
    /// Shell command that reports whether the host is OpenWrt (the release file, the custom opkg feed list or the
    /// custom apk repository list is present - not just the /etc/opkg directory, which Yocto-based systems have too, or /etc/os-release names OpenWrt as the system or its base, as derivatives
    /// such as ImmortalWrt do with ID_LIKE="lede openwrt"; the name has to be a whole word there, so that an ID such as
    /// "prolede" or "fedora-lede" is not taken for it) and which package manager it has: apk (OpenWrt 25.12 and newer)
    /// or opkg. Both answers are printed as markers so that a missing command, a shell banner or an error line cannot be
    /// mistaken for a positive answer. When both managers are installed, apk wins, like in the package manager window.
    /// The same round trip reports the kernel of the host (<see cref="SmarTTY.Ssh.RemoteOsDetector.KernelProbeCommand"/>), which
    /// decides whether the tools that need Linux are offered, and whether the host has tar
    /// (<see cref="SmarTTY.Transport.RemoteBackup.TarProbeCommand"/>), which decides whether the backup to tar.gz is offered.
    /// </summary>
    public const string ProbeCommand =
        "if [ -f /etc/openwrt_release ] || [ -f /etc/opkg/customfeeds.conf ] || [ -f /etc/apk/repositories.d/customfeeds.list ] || " +
        "grep -qsE '^(ID|ID_LIKE)=([^[:alnum:]]*|.*[^[:alnum:]_-])(openwrt|lede)([^[:alnum:]_-]|$)' /etc/os-release; then echo " + OpenWrtMarker + "; else echo SMARTTY_OS=other; fi; " +
        "pm=none; command -v opkg >/dev/null 2>&1 && pm=opkg; command -v apk >/dev/null 2>&1 && pm=apk; echo SMARTTY_PKG=$pm; " +
        SmarTTY.Transport.RemoteBackup.TarProbeCommand + "; " +
        SmarTTY.Ssh.RemoteOsDetector.KernelProbeCommand;

    /// <summary>
    /// True when the host answered the probe at all (it printed "SMARTTY_OS=openwrt" or "SMARTTY_OS=other"). A host that did
    /// not has no /bin/sh, which is what a Windows host looks like: see <c>RemoteOsDetector</c>.
    /// </summary>
    public static bool IsAnswer(IEnumerable<string> lines)
    {
        return lines != null && lines.Any(line => line != null && line.TrimStart().StartsWith("SMARTTY_OS=", System.StringComparison.Ordinal));
    }

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
