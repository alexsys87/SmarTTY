using System;
using System.Linq;
using SmarTTY.Ssh;

// Regression harness for "Tools > Reboot server": which system the probe output names and the reboot command of each system.
internal static class RebootTests
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static RebootSystem? Probe(string kernel, bool systemd = false, bool openWrt = false, params string[] noise)
    {
        return RemoteReboot.Detect(noise.Concat(new[] { "SMARTTY_KERNEL=" + kernel, "SMARTTY_INIT=" + (systemd ? "systemd" : "other") }).Concat(openWrt ? new[] { "SMARTTY_DISTRO=openwrt" } : new string[0]));
    }

    public static int Run()
    {
        Check(Probe("Linux", systemd: true) == RebootSystem.LinuxSystemd, "detect: Linux with systemd");
        Check(Probe("Linux") == RebootSystem.Linux, "detect: Linux without systemd (Alpine, Void, Devuan)");
        Check(Probe("Linux", openWrt: true) == RebootSystem.OpenWrt, "detect: OpenWrt");
        Check(Probe("Linux", systemd: true, openWrt: true) == RebootSystem.OpenWrt, "detect: the release file of OpenWrt wins");
        Check(Probe("FreeBSD") == RebootSystem.FreeBsd && Probe("OpenBSD") == RebootSystem.OpenBsd && Probe("NetBSD") == RebootSystem.NetBsd && Probe("DragonFly") == RebootSystem.DragonFly, "detect: the BSDs");
        Check(Probe("Darwin") == RebootSystem.MacOs && Probe("SunOS") == RebootSystem.Solaris && Probe("AIX") == RebootSystem.Aix, "detect: macOS, Solaris, AIX");
        Check(Probe("HP-UX") == RebootSystem.OtherUnix && Probe("Haiku") == RebootSystem.OtherUnix && Probe("") == RebootSystem.OtherUnix, "detect: any other kernel, or one that printed nothing, is a plain Unix");
        Check(Probe("CYGWIN_NT-10.0-22631") == RebootSystem.WindowsUnixShell && Probe("MINGW64_NT-10.0-19045") == RebootSystem.WindowsUnixShell && Probe("MSYS_NT-10.0") == RebootSystem.WindowsUnixShell, "detect: Cygwin, MSYS2 and Git Bash");
        Check(Probe("linux", systemd: true) == RebootSystem.LinuxSystemd && Probe("  Darwin  ") == RebootSystem.MacOs, "detect: case and blanks do not matter");
        Check(Probe("Linux", systemd: true, openWrt: false, "Last login: Mon Nov  2 10:00:00 2026", "Welcome to Debian") == RebootSystem.LinuxSystemd, "detect: a banner around the markers is ignored");
        Check(RemoteReboot.Detect(null) == null && RemoteReboot.Detect(new string[0]) == null && RemoteReboot.Detect(new[] { "Last login: today", "sh: uname: not found" }) == null && RemoteReboot.Detect(new[] { "SMARTTY_INIT=systemd" }) == null, "detect: no kernel marker -> unknown, not 'some Unix'");
        Check(RemoteReboot.Detect(new[] { "SMARTTY_INIT=systemd", "SMARTTY_DISTRO=openwrt", null, "SMARTTY_KERNEL=Linux" }) == RebootSystem.OpenWrt, "detect: the order of the lines and a null line do not matter");

        Check(RemoteReboot.GetCommand(RebootSystem.LinuxSystemd) == "systemctl reboot", "command: systemd");
        Check(RemoteReboot.GetCommand(RebootSystem.Linux) == "reboot" && RemoteReboot.GetCommand(RebootSystem.OpenWrt) == "reboot", "command: Linux without systemd, OpenWrt");
        Check(new[] { RebootSystem.FreeBsd, RebootSystem.OpenBsd, RebootSystem.NetBsd, RebootSystem.DragonFly, RebootSystem.MacOs, RebootSystem.OtherUnix }.All(system => RemoteReboot.GetCommand(system) == "shutdown -r now"), "command: the BSDs, macOS and other Unix");
        Check(RemoteReboot.GetCommand(RebootSystem.Solaris) == "shutdown -y -i6 -g0" && RemoteReboot.GetCommand(RebootSystem.Aix) == "shutdown -Fr", "command: Solaris, AIX");
        Check(RemoteReboot.GetCommand(RebootSystem.Windows) == "shutdown /r /t 0", "command: Windows");
        Check(RemoteReboot.GetCommand(RebootSystem.WindowsUnixShell) == null, "command: none for Windows with a Unix shell");
        Check(Enum.GetValues(typeof(RebootSystem)).Cast<RebootSystem>().All(system => !string.IsNullOrWhiteSpace(RemoteReboot.GetSystemName(system))), "every system has a name");
        Check(RemoteReboot.GetSystemName(RebootSystem.LinuxSystemd) == "Linux (systemd)" && RemoteReboot.GetSystemName(RebootSystem.MacOs) == "macOS", "names for the question");

        Check(RemoteReboot.BuildCommandLine(RebootSystem.LinuxSystemd, isRoot: true) == "systemctl reboot", "line: root runs it as it is");
        Check(RemoteReboot.BuildCommandLine(RebootSystem.LinuxSystemd, isRoot: false) == "sudo su -c \"systemctl reboot\"", "line: a user goes through sudo");
        Check(RemoteReboot.BuildCommandLine(RebootSystem.Solaris, isRoot: false) == "sudo su -c \"shutdown -y -i6 -g0\"", "line: Solaris through sudo");
        Check(RemoteReboot.BuildCommandLine(RebootSystem.Windows, isRoot: false) == "shutdown /r /t 0" && RemoteReboot.BuildCommandLine(RebootSystem.Windows, isRoot: true) == "shutdown /r /t 0", "line: Windows has no sudo");
        Check(RemoteReboot.BuildCommandLine(RebootSystem.WindowsUnixShell, isRoot: true) == null, "line: nothing to type for Windows with a Unix shell");

        Check(RemoteReboot.ProbeCommand.Contains("uname -s") && RemoteReboot.ProbeCommand.Contains("/run/systemd/system") && RemoteReboot.ProbeCommand.Contains("/etc/openwrt_release"), "probe: asks for the kernel, systemd and the OpenWrt release file");
        Check(!RemoteReboot.ProbeCommand.Contains("\n") && !RemoteReboot.ProbeCommand.Contains("\""), "probe: one line without double quotes (it is wrapped for sh)");
        return _passed;
    }
}
