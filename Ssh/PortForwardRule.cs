using SmarTTY.Utilities;
using System;
using System.Globalization;
using System.Net;
using System.Xml.Serialization;

namespace SmarTTY.Ssh;

public enum PortForwardKind
{
    /// <summary>"ssh -L": a port of this computer leads to a host and port as the SSH server sees them.</summary>
    [EnumDisplayText("Local (-L)")]
    Local,
    /// <summary>"ssh -R": a port of the SSH server leads to a host and port as this computer sees them.</summary>
    [EnumDisplayText("Remote (-R)")]
    Remote
}

/// <summary>
/// One port forwarding of an SSH profile (Tools > Port forwarding), saved with the profile like LocalForward / RemoteForward
/// in ssh_config.
/// </summary>
public class PortForwardRule
{
    public PortForwardKind Kind;

    /// <summary>The address the listening side binds: of this computer for <see cref="PortForwardKind.Local"/>, of the server for <see cref="PortForwardKind.Remote"/>.</summary>
    public string BindAddress = "127.0.0.1";

    /// <summary>The listening port; 0 lets this computer choose a free one (local forwarding only).</summary>
    public int BindPort;

    /// <summary>Where the connections go: resolved by the server for local forwarding, by this computer for remote forwarding.</summary>
    public string TargetHost = "localhost";

    public int TargetPort;

    /// <summary>Started as soon as the profile connects.</summary>
    public bool StartOnConnect;

    /// <summary>A free text that tells the forwardings apart in the list (optional).</summary>
    public string Description;

    [XmlIgnore]
    public string ListenText => FormatEndpoint(BindAddress, BindPort) + (Kind == PortForwardKind.Remote ? " (server)" : "");

    [XmlIgnore]
    public string TargetText => FormatEndpoint(TargetHost, TargetPort) + (Kind == PortForwardKind.Remote ? " (from here)" : " (from the server)");

    /// <summary>The same forwarding as an ssh option: "-L 127.0.0.1:8080:db:5432".</summary>
    [XmlIgnore]
    public string SshOption => (Kind == PortForwardKind.Local ? "-L " : "-R ") + Bracket(BindAddress) + ":" + BindPort.ToString(CultureInfo.InvariantCulture) + ":" + Bracket(TargetHost) + ":" + TargetPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>What is wrong with the rule, or null when it can be started.</summary>
    public string Validate()
    {
        if (string.IsNullOrWhiteSpace(BindAddress))
        {
            return "Enter the address to listen on (127.0.0.1 for this computer only).";
        }
        if (Kind == PortForwardKind.Local && !IPAddress.TryParse(BindAddress.Trim(), out _) && !string.Equals(BindAddress.Trim(), "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "The local listening address must be an IP address of this computer, for example 127.0.0.1 or 0.0.0.0.";
        }
        if (BindPort < (Kind == PortForwardKind.Local ? 0 : 1) || BindPort > 65535)
        {
            return Kind == PortForwardKind.Local
                ? "The listening port must be a number from 0 to 65535 (0: any free port)."
                : "The listening port on the server must be a number from 1 to 65535.";
        }
        if (string.IsNullOrWhiteSpace(TargetHost) || TargetHost.Trim().IndexOfAny(new[] { ' ', '\t', '/' }) >= 0)
        {
            return "Enter the destination host (a name or an address).";
        }
        if (TargetPort < 1 || TargetPort > 65535)
        {
            return "The destination port must be a number from 1 to 65535.";
        }
        return null;
    }

    public PortForwardRule Clone()
    {
        return (PortForwardRule)MemberwiseClone();
    }

    public static string FormatEndpoint(string host, int port)
    {
        return Bracket(host) + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    private static string Bracket(string host)
    {
        host = host ?? "";
        return host.IndexOf(':') >= 0 ? "[" + host + "]" : host;
    }
}
