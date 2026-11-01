namespace SmarTTY.Ssh;

/// <summary>The commands of the Tools menu that depend on what the host of the active session is.</summary>
public enum SessionTool
{
    /// <summary>Tools > Manage Linux Packages.</summary>
    PackageManager,

    /// <summary>Tools > Process manager: reads /proc, so it needs Linux.</summary>
    ProcessManager,

    /// <summary>Tools > Local OpenWrt repository server.</summary>
    LocalRepoServer,

    /// <summary>Tools > Port forwarding: a feature of the SSH protocol, works with any server.</summary>
    PortForwarding,

    /// <summary>Tools > SOCKS5 proxy over SSH: a feature of the SSH protocol, works with any server.</summary>
    SocksProxy,

    /// <summary>Tools > Public key authentication: authorized_keys of a Unix host or the key files of a Windows one.</summary>
    PublicKeyAuthentication,

    /// <summary>Tools > Run script: uploads a shell script to /tmp, makes it executable and runs it with sh.</summary>
    RunScript,

    /// <summary>Tools > Check X11 forwarding: looks for xauth and libX11 on the host.</summary>
    X11Check,

    /// <summary>Tools > TFTP server / U-Boot upload: for a COM or TCP device session.</summary>
    TftpFirmware,

    /// <summary>Tools > Backup to tar.gz: runs tar on a Unix host and streams the archive through the SSH channel.</summary>
    Backup
}

/// <summary>What is known about the host of a session when the Tools menu is shown.</summary>
public readonly struct RemoteHostFacts
{
    /// <summary>A COM or TCP terminal: there is no SSH connection and no shell of a server.</summary>
    public bool IsDevice { get; }

    /// <summary>Windows, Unix or not known (the banner of the server said nothing and the probe has not answered).</summary>
    public RemoteOsKind Os { get; }

    /// <summary>Linux, another Unix kernel or not known yet (the probe of the host has not answered).</summary>
    public RemoteKernelKind Kernel { get; }

    /// <summary>The probe in the background found a package manager that SmarTTY supports.</summary>
    public bool HasPackageManager { get; }

    /// <summary>The probe in the background found OpenWrt with opkg or apk.</summary>
    public bool HasLocalRepoManager { get; }

    /// <summary>The probe in the background found tar on the host (false until it has answered).</summary>
    public bool HasTar { get; }

    public RemoteHostFacts(bool isDevice, RemoteOsKind os, RemoteKernelKind kernel, bool hasPackageManager, bool hasLocalRepoManager, bool hasTar = false)
    {
        IsDevice = isDevice;
        Os = os;
        Kernel = kernel;
        HasPackageManager = hasPackageManager;
        HasLocalRepoManager = hasLocalRepoManager;
        HasTar = hasTar;
    }
}

/// <summary>
/// The one place that says which commands of the Tools menu are offered for which host: a command that cannot work there is not
/// in the menu at all. The rules use only what the detection of the session already knows (the device or SSH kind, the Windows or
/// Unix banner, the kernel and the package managers found by the probes in the background), so a host that has not answered a
/// probe yet gets the commands that do not depend on it. Pure logic without WPF or SSH, tested by tests/LocalRepoTests.
/// </summary>
public static class ToolAvailability
{
    public static bool IsAvailable(SessionTool tool, RemoteHostFacts host)
    {
        switch (tool)
        {
            case SessionTool.TftpFirmware:
                return host.IsDevice;
            case SessionTool.PackageManager:
                // The answer of the probe: it covers Windows (winget) as well as the Unix package managers.
                return !host.IsDevice && host.HasPackageManager;
            case SessionTool.LocalRepoServer:
                return !host.IsDevice && host.HasLocalRepoManager;
        }
        if (host.IsDevice)
        {
            return false;
        }
        switch (tool)
        {
            case SessionTool.PortForwarding:
            case SessionTool.SocksProxy:
            case SessionTool.PublicKeyAuthentication:
                return true;
            case SessionTool.ProcessManager:
                // /proc/stat, /proc/meminfo and /proc/N/stat: only Linux has them in this form.
                return host.Os != RemoteOsKind.Windows && host.Kernel != RemoteKernelKind.Other;
            case SessionTool.RunScript:
            case SessionTool.X11Check:
                // sh, chmod, /tmp and xauth are tools of a Unix host.
                return host.Os != RemoteOsKind.Windows;
            case SessionTool.Backup:
                // /bin/sh and tar: offered once the probe has found tar, so a host without it (or a Windows one) never sees the item.
                return host.Os != RemoteOsKind.Windows && host.HasTar;
            default:
                return true;
        }
    }
}
