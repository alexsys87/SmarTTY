using System.Collections.Generic;
using System.Text;

namespace SmarTTY.Ssh;

/// <summary>
/// What the remote host has for X11: the xauth program (the SSH server needs it to accept X11 forwarding), the X11 client
/// library (needed by graphical programs) and the X11Forwarding line of the OpenSSH server configuration. Gathered by one
/// POSIX shell command that also works with BusyBox.
/// </summary>
public sealed class X11RemoteStatus
{
    internal const string ProbeCommand =
        "for p in xauth /usr/bin/xauth /usr/X11R6/bin/xauth /usr/X11R7/bin/xauth /usr/openwin/bin/xauth /opt/X11/bin/xauth /usr/local/bin/xauth; do " +
        "if command -v \"$p\" >/dev/null 2>&1; then echo \"xauth=$(command -v \"$p\")\"; break; fi; done; " +
        "for f in /usr/lib/libX11.so* /usr/lib64/libX11.so* /usr/lib/*/libX11.so* /lib/*/libX11.so* /usr/local/lib/libX11.so* /usr/X11R6/lib/libX11.so* /usr/X11R7/lib/libX11.so* /usr/pkg/lib/libX11.so*; do " +
        "if [ -e \"$f\" ]; then echo \"libX11=$f\"; break; fi; done; " +
        "grep -i -h '^[[:space:]]*X11Forwarding[[:space:]]' /etc/ssh/sshd_config.d/*.conf /etc/ssh/sshd_config 2>/dev/null | head -n 1 | sed 's/^[[:space:]]*/sshd=/'; " +
        "echo \"display=$DISPLAY\"; true";

    /// <summary>False if the probe command could not be run at all; the other fields are then unknown.</summary>
    public bool ProbeSucceeded;

    /// <summary>Software version of the SSH server (e.g. "OpenSSH_9.6p1", "dropbear_2022.83").</summary>
    public string ServerVersion;

    public string XauthPath;

    public string LibX11Path;

    /// <summary>The X11Forwarding line of sshd_config, if it could be read (e.g. "X11Forwarding no").</summary>
    public string SshdSetting;

    /// <summary>DISPLAY of a command run without X11 forwarding (normally empty).</summary>
    public string Display;

    public bool HasXauth => !string.IsNullOrEmpty(XauthPath);

    public bool HasLibX11 => !string.IsNullOrEmpty(LibX11Path);

    public bool IsDropbear => ServerVersion != null && ServerVersion.IndexOf("dropbear", System.StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>True when sshd_config explicitly disables X11 forwarding.</summary>
    public bool SshdDisablesX11 => SshdSetting != null && SshdSetting.Trim().EndsWith("no", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs the probe on the remote host (a helper command without a PTY).</summary>
    public static X11RemoteStatus Check(SshSession session)
    {
        List<string> output = session.RunCommand(ProbeCommand, out int? _);
        X11RemoteStatus status = Parse(output);
        status.ServerVersion = session.ServerVersion;
        return status;
    }

    internal static X11RemoteStatus Parse(IEnumerable<string> output)
    {
        X11RemoteStatus status = new X11RemoteStatus { ProbeSucceeded = output != null };
        if (output == null)
        {
            return status;
        }
        foreach (string rawLine in output)
        {
            string line = rawLine.TrimEnd('\r');
            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            string value = line.Substring(separator + 1).Trim();
            switch (line.Substring(0, separator))
            {
                case "xauth":
                    status.XauthPath = value;
                    break;
                case "libX11":
                    status.LibX11Path = value;
                    break;
                case "sshd":
                    status.SshdSetting = value;
                    break;
                case "display":
                    status.Display = value;
                    break;
            }
        }
        return status;
    }

    /// <summary>Explains why the server refused X11 forwarding and what to change on it.</summary>
    public string DescribeRefusal(string hostName)
    {
        StringBuilder text = new StringBuilder();
        text.Append("The SSH server ").Append(hostName).Append(" refused X11 forwarding, so graphical programs started there cannot open windows on this computer.");
        text.AppendLine().AppendLine();
        if (!ProbeSucceeded)
        {
            text.Append("SmarTTY could not check the remote host for X11 support. Make sure that the xauth program is installed there and that the SSH server allows X11 forwarding (\"X11Forwarding yes\" in /etc/ssh/sshd_config).");
            return text.ToString();
        }
        bool reasonFound = false;
        if (!HasXauth)
        {
            text.Append("- The xauth program was not found on the remote host; the SSH server needs it for X11 forwarding. Install it (package \"xauth\" on Debian/Ubuntu/Arch/Alpine, \"xorg-x11-xauth\" on RHEL/Fedora/CentOS).").AppendLine();
            reasonFound = true;
        }
        if (SshdDisablesX11)
        {
            text.Append("- sshd_config contains \"").Append(SshdSetting).Append("\". Change it to \"X11Forwarding yes\" and restart the SSH server.").AppendLine();
            reasonFound = true;
        }
        if (IsDropbear)
        {
            text.Append("- The server is Dropbear (").Append(ServerVersion).Append("). Builds for OpenWrt and other embedded systems usually leave X11 forwarding out; it needs a Dropbear built with X11 forwarding and the xauth program.").AppendLine();
            reasonFound = true;
        }
        if (!reasonFound)
        {
            text.Append("- xauth is installed (").Append(XauthPath).Append("). Check that the SSH server allows X11 forwarding (\"X11Forwarding yes\" in /etc/ssh/sshd_config, also inside \"Match\" blocks) and restart it after changing the setting.").AppendLine();
        }
        if (!HasLibX11)
        {
            text.Append("- No X11 client library (libX11) was found on the remote host: no graphical X11 programs seem to be installed there.").AppendLine();
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>A summary of the remote side for the X11 diagnostics window.</summary>
    public string Describe()
    {
        if (!ProbeSucceeded)
        {
            return "The remote host could not be checked.";
        }
        StringBuilder text = new StringBuilder();
        text.Append("SSH server: ").Append(string.IsNullOrEmpty(ServerVersion) ? "unknown" : ServerVersion).AppendLine();
        text.Append("xauth: ").Append(HasXauth ? XauthPath : "not found (the SSH server needs it for X11 forwarding)").AppendLine();
        text.Append("X11 client library: ").Append(HasLibX11 ? LibX11Path : "not found (no graphical programs installed?)").AppendLine();
        if (SshdSetting != null)
        {
            text.Append("sshd_config: ").Append(SshdSetting).AppendLine();
        }
        return text.ToString().TrimEnd();
    }
}
