using System;
using System.Collections.Generic;

namespace SmarTTY.Ssh;

/// <summary>The kinds of host that "Tools > Reboot server" knows a reboot command for.</summary>
public enum RebootSystem
{
    /// <summary>Linux that runs systemd (Debian, Ubuntu, Fedora, RHEL, Arch, openSUSE, ...).</summary>
    LinuxSystemd,

    /// <summary>Linux without systemd (Alpine and other OpenRC or runit systems, Devuan, Void, BusyBox based ones).</summary>
    Linux,

    /// <summary>OpenWrt and its derivatives: BusyBox with procd, "reboot" is the way there.</summary>
    OpenWrt,

    FreeBsd,

    OpenBsd,

    NetBsd,

    DragonFly,

    /// <summary>macOS (the kernel calls itself Darwin).</summary>
    MacOs,

    /// <summary>Solaris, illumos and their distributions (OpenIndiana, SmartOS, ...).</summary>
    Solaris,

    Aix,

    /// <summary>A Unix that is none of the above (HP-UX, Haiku, ...) or did not say which: the POSIX form of the command.</summary>
    OtherUnix,

    /// <summary>A Windows host with cmd.exe or PowerShell (Win32-OpenSSH, Bitvise).</summary>
    Windows,

    /// <summary>Windows with a Unix shell (Cygwin, MSYS2, Git Bash): its "shutdown" is not the Unix one, and the Windows one is reached with different quoting.</summary>
    WindowsUnixShell
}

/// <summary>
/// What a reboot is on each kind of host, and how to tell the kind. A Windows host is known from the identification string of its SSH
/// server (see <see cref="RemoteOsDetector"/>); any other host answers <see cref="ProbeCommand"/>, which names the kernel (uname -s),
/// says whether systemd runs and whether it is OpenWrt. Pure logic without SSH, tested by tests/ApkPackageManagerTests.
/// </summary>
public static class RemoteReboot
{
    public const string KernelMarker = "SMARTTY_KERNEL=";

    public const string InitMarker = "SMARTTY_INIT=";

    public const string DistroMarker = "SMARTTY_DISTRO=";

    /// <summary>
    /// Shell command that names the kernel, tells systemd from any other init (a directory that only a running systemd creates,
    /// the check that sd_booted() makes) and recognizes OpenWrt by its release file. Every answer is a marker, so that a banner or an
    /// error line cannot be mistaken for one.
    /// </summary>
    public const string ProbeCommand =
        "echo " + KernelMarker + "$(uname -s 2>/dev/null); " +
        "if [ -d /run/systemd/system ]; then echo " + InitMarker + "systemd; else echo " + InitMarker + "other; fi; " +
        "if [ -f /etc/openwrt_release ]; then echo " + DistroMarker + "openwrt; fi";

    /// <summary>
    /// The kind of Unix host from the output of <see cref="ProbeCommand"/>; null when the host did not answer it (no line with the kernel
    /// marker at all), so that the caller can tell "unknown" from "some other Unix".
    /// </summary>
    public static RebootSystem? Detect(IEnumerable<string> lines)
    {
        if (lines == null)
        {
            return null;
        }
        string kernel = null;
        bool systemd = false;
        bool openWrt = false;
        foreach (string rawLine in lines)
        {
            string line = rawLine?.Trim();
            if (line == null)
            {
                continue;
            }
            if (line.StartsWith(KernelMarker, StringComparison.Ordinal))
            {
                kernel = line.Substring(KernelMarker.Length).Trim();
            }
            else if (line == InitMarker + "systemd")
            {
                systemd = true;
            }
            else if (line == DistroMarker + "openwrt")
            {
                openWrt = true;
            }
        }
        if (kernel == null)
        {
            return null;
        }
        if (kernel.Equals("Linux", StringComparison.OrdinalIgnoreCase))
        {
            return openWrt ? RebootSystem.OpenWrt : systemd ? RebootSystem.LinuxSystemd : RebootSystem.Linux;
        }
        if (kernel.Equals("FreeBSD", StringComparison.OrdinalIgnoreCase))
        {
            return RebootSystem.FreeBsd;
        }
        if (kernel.Equals("OpenBSD", StringComparison.OrdinalIgnoreCase))
        {
            return RebootSystem.OpenBsd;
        }
        if (kernel.Equals("NetBSD", StringComparison.OrdinalIgnoreCase))
        {
            return RebootSystem.NetBsd;
        }
        if (kernel.Equals("DragonFly", StringComparison.OrdinalIgnoreCase))
        {
            return RebootSystem.DragonFly;
        }
        if (kernel.Equals("Darwin", StringComparison.OrdinalIgnoreCase))
        {
            return RebootSystem.MacOs;
        }
        if (kernel.Equals("SunOS", StringComparison.OrdinalIgnoreCase))
        {
            return RebootSystem.Solaris;
        }
        if (kernel.Equals("AIX", StringComparison.OrdinalIgnoreCase))
        {
            return RebootSystem.Aix;
        }
        if (kernel.StartsWith("CYGWIN", StringComparison.OrdinalIgnoreCase) || kernel.StartsWith("MINGW", StringComparison.OrdinalIgnoreCase) || kernel.StartsWith("MSYS", StringComparison.OrdinalIgnoreCase))
        {
            return RebootSystem.WindowsUnixShell;
        }
        return RebootSystem.OtherUnix;
    }

    /// <summary>The name of the system for the question to the user.</summary>
    public static string GetSystemName(RebootSystem system)
    {
        switch (system)
        {
            case RebootSystem.LinuxSystemd:
                return "Linux (systemd)";
            case RebootSystem.Linux:
                return "Linux (without systemd)";
            case RebootSystem.OpenWrt:
                return "OpenWrt";
            case RebootSystem.FreeBsd:
                return "FreeBSD";
            case RebootSystem.OpenBsd:
                return "OpenBSD";
            case RebootSystem.NetBsd:
                return "NetBSD";
            case RebootSystem.DragonFly:
                return "DragonFly BSD";
            case RebootSystem.MacOs:
                return "macOS";
            case RebootSystem.Solaris:
                return "Solaris / illumos";
            case RebootSystem.Aix:
                return "AIX";
            case RebootSystem.Windows:
                return "Windows";
            case RebootSystem.WindowsUnixShell:
                return "Windows with a Unix shell (Cygwin, MSYS2, Git Bash)";
            default:
                return "Unix";
        }
    }

    /// <summary>
    /// The command that reboots the host at once, without the rights it needs; null when there is none (Windows with a Unix shell).
    /// systemd has its own command, which also waits for the services to stop; the others have "reboot" or the form of "shutdown"
    /// that their system knows: "-r now" on Linux and the BSDs, "-y -i6 -g0" on Solaris, "-Fr" on AIX, "/r /t 0" on Windows.
    /// </summary>
    public static string GetCommand(RebootSystem system)
    {
        switch (system)
        {
            case RebootSystem.LinuxSystemd:
                return "systemctl reboot";
            case RebootSystem.Linux:
            case RebootSystem.OpenWrt:
                return "reboot";
            case RebootSystem.FreeBsd:
            case RebootSystem.OpenBsd:
            case RebootSystem.NetBsd:
            case RebootSystem.DragonFly:
            case RebootSystem.MacOs:
            case RebootSystem.OtherUnix:
                return "shutdown -r now";
            case RebootSystem.Solaris:
                return "shutdown -y -i6 -g0";
            case RebootSystem.Aix:
                return "shutdown -Fr";
            case RebootSystem.Windows:
                return "shutdown /r /t 0";
            default:
                return null;
        }
    }

    /// <summary>
    /// The line that is typed in the terminal: the command as it is for Windows (it has no sudo, and rejects the rights itself) and for root,
    /// through "sudo su -c" for any other user of a Unix host, like the package manager does. Null when the system has no command.
    /// </summary>
    public static string BuildCommandLine(RebootSystem system, bool isRoot)
    {
        string command = GetCommand(system);
        if (command == null)
        {
            return null;
        }
        // Windows has no sudo (it refuses the rights itself); any other host goes through "sudo su -c" for a user other than
        // root, the way the package manager wraps its command, so that the password is asked for in the terminal. Wrapped here
        // and not by PackageIndexUpdater.WrapForPrivileges, because tests/SshNetIntegrationTests compiles all of Ssh/ on its own
        // and that layer does not include PackageManagers.
        return system == RebootSystem.Windows || isRoot ? command : "sudo su -c \"" + command + "\"";
    }
}
