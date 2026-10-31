using System;

namespace SmarTTY.Ssh;

/// <summary>
/// Everything needed to establish an SSH connection before authentication starts.
/// </summary>
public class SshConnectionParameters
{
    /// <summary>
    /// Host name or address to connect to (without the port).
    /// </summary>
    public string Host;

    public int Port = 22;

    /// <summary>
    /// Host name as shown to the user and used for the known host key lookup (may include ":port").
    /// </summary>
    public string DisplayHostName;

    /// <summary>
    /// User name for all authentication attempts on this connection (SSH servers do not allow changing it).
    /// </summary>
    public string UserName;

    public bool EnableCompression;

    public bool EnableOnTheFlyTar;

    /// <summary>
    /// Proxy used for the connection (see <see cref="ProxyType"/>); null for a direct connection.
    /// </summary>
    public string ProxyHost;

    public SshProxyType ProxyType;

    /// <summary>
    /// Checks the certificate of an HTTPS proxy; null for the usual check against the trusted certificates of Windows.
    /// </summary>
    public System.Net.Security.RemoteCertificateValidationCallback ProxyCertificateValidation;

    public int ProxyPort;

    public string ProxyUserName;

    public string ProxyPassword;

    /// <summary>
    /// Host key type stored for this host (e.g. "ssh-rsa"). Matching algorithms are preferred during the key exchange,
    /// so that a server offering several host keys keeps presenting the one the user already trusted.
    /// </summary>
    public string PreferredHostKeyType;

    public TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A copy to connect with again (the fields are plain values and delegates).</summary>
    public SshConnectionParameters Clone()
    {
        return (SshConnectionParameters)MemberwiseClone();
    }
}
