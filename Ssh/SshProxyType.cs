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
    Socks5
}
