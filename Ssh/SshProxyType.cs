using SmarTTY.Utilities;

namespace SmarTTY.Ssh;

/// <summary>
/// The kind of proxy an SSH connection goes through. The names are stored in the connection profiles; profiles of earlier
/// versions have none and get <see cref="Http"/>, the only proxy they knew.
/// </summary>
public enum SshProxyType
{
    [EnumDisplayText("HTTP CONNECT")]
    Http,
    /// <summary>
    /// A SOCKS5 proxy, such as the local inbound of xray, v2ray or sing-box. The host name of the SSH server is passed to
    /// the proxy and resolved there (as socks5h://), so it also works for names only the far side of the proxy knows.
    /// </summary>
    [EnumDisplayText("SOCKS5")]
    Socks5,
    /// <summary>
    /// SOCKS4, or SOCKS4a for a host name (the proxy resolves it). The user name is sent as the SOCKS4 user ID; there is no
    /// password. Older proxies and SSH tunnels (ssh -D) speak it.
    /// </summary>
    [EnumDisplayText("SOCKS4/4a")]
    Socks4,
    /// <summary>
    /// HTTP CONNECT over a TLS connection to the proxy (an "HTTPS proxy", as browsers, Squid with https_port or
    /// NaiveProxy offer it), so that the proxy credentials do not cross the network in clear text. The certificate of
    /// the proxy is checked against the trusted certificates of Windows.
    /// </summary>
    [EnumDisplayText("HTTPS (TLS)")]
    Https
}
