using SmarTTY.Ssh;
using SmarTTY.Transport;
using SmarTTY.Utilities;
using System;
using System.IO;
using System.Xml.Serialization;

namespace VisualTTY;

public class SSHConnectionSettings
{
    public bool EmulateSCPWithCat;

    public byte[] EncryptedSavedPassword;

    public bool AutoConfirmSudo;

    public int IconIndex;

    public string Icon;

    public PublicKeyType KeyType;

    [XmlIgnore]
    public string FileName;

    [XmlIgnore]
    public bool BypassHostKeyCheck;

    public bool IsShared;

    public long LastConnectionTime;

    public string HostName { get; set; }

    public string UserName { get; set; }

    public AuthType AuthenticationType { get; set; }

    public string ExplicitKeyFile { get; set; }

    /// <summary>
    /// The credentials of the HTTP proxy ("user:password") encrypted the same way as the saved password. Only this form
    /// is written to the profile file.
    /// </summary>
    public byte[] EncryptedProxyAuth;

    /// <summary>
    /// "user:password" of the HTTP proxy, null when it has none. The profile file keeps it only encrypted
    /// (<see cref="EncryptedProxyAuth"/>); reading gives null when it cannot be decrypted (a profile copied from another
    /// computer, a replaced key), and the proxy then asks for the credentials when it answers with 407. Writing throws
    /// when the text is longer than <see cref="PasswordProtector"/> can encrypt.
    /// </summary>
    [XmlIgnore]
    public string ProxyAuth
    {
        get
        {
            if (EncryptedProxyAuth == null)
            {
                return null;
            }
            try
            {
                return PasswordProtector.Decrypt(EncryptedProxyAuth, AppEnvironment.PasswordContainerName);
            }
            catch
            {
                return null;
            }
        }
        set
        {
            EncryptedProxyAuth = string.IsNullOrEmpty(value) ? null : PasswordProtector.Encrypt(value, AppEnvironment.PasswordContainerName);
        }
    }

    /// <summary>
    /// Reads the plain-text &lt;ProxyAuth&gt; element that profiles saved by earlier versions hold and encrypts it into
    /// <see cref="EncryptedProxyAuth"/>, so that the next save replaces the plain text. It is never written (the getter
    /// returns null): the profile file only gets <see cref="EncryptedProxyAuth"/>.
    /// </summary>
    [XmlElement("ProxyAuth")]
    public string LegacyProxyAuth
    {
        get
        {
            return null;
        }
        set
        {
            if (string.IsNullOrEmpty(value) || EncryptedProxyAuth != null)
            {
                return;
            }
            try
            {
                ProxyAuth = value;
            }
            catch
            {
                // The key container cannot be used: the proxy asks for its credentials when it needs them.
            }
        }
    }

    public string ProxyServer { get; set; }

    public int ProxyPort { get; set; }

    public bool EnableCompression { get; set; }

    /// <summary>Requests X11 forwarding, so that graphical programs started on the server open their windows here (VcXsrv).</summary>
    public bool EnableX11Forwarding { get; set; }

    /// <summary>Starts the SOCKS5 proxy through the SSH connection as soon as it is established (see Tools > SOCKS5 proxy over SSH for its address and port).</summary>
    public bool AutoStartSocksProxy { get; set; }

    public bool EnableOnTheFlyTAR { get; set; } = true;

    public string ConnectionAlias { get; set; }

    public bool SuppressKeyMismatchWarning { get; set; }

    /// <summary>Keep-alive default of a connection whose profile does not say (0), in seconds.</summary>
    public const int DefaultKeepaliveSeconds = 15;

    /// <summary>The value of <see cref="KeepaliveInterval"/> that was chosen by switching the keep-alive off.</summary>
    public const int KeepaliveOff = -1;

    /// <summary>
    /// Seconds between keep-alive packets. 0 is "not set" (profiles saved before the option could be told apart, new
    /// profiles): <see cref="DefaultKeepaliveSeconds"/> applies, because without periodic traffic a pulled cable is only
    /// noticed on the next key press. A negative value (<see cref="KeepaliveOff"/>) is an explicit "off".
    /// </summary>
    public int KeepaliveInterval { get; set; }

    public bool SuppressAutoProbing { get; set; }

    [XmlIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(ConnectionAlias))
            {
                return ConnectionAlias;
            }
            return UserName + "@" + HostName;
        }
    }

    public override string ToString()
    {
        return UserName + "@" + HostName;
    }

    public void Save()
    {
        XmlSerializer xmlSerializer = new XmlSerializer(typeof(SSHConnectionSettings));
        using (FileStream stream = File.Create(FileName))
        {
            xmlSerializer.Serialize(stream, this);
        }
        try
        {
            SavedConnectionStore.TouchTimestamp(Path.GetDirectoryName(FileName));
        }
        catch
        {
        }
    }

    /// <summary>
    /// Splits the host field of a connection: "host", "host:port", "[ipv6]" or "[ipv6]:port". Returns the host without
    /// brackets and the port, or null when there is none. An IPv6 address without brackets has colons of its own, so
    /// it is a host without a port. Anything that only looks like a port (letters, 0, more than 65535) is not split
    /// off: the text is returned whole, so that a typo in the port fails when connecting instead of connecting to the
    /// default port.
    /// </summary>
    public static string SplitHostPort(string hostWithPort, out int? port)
    {
        port = null;
        if (string.IsNullOrEmpty(hostWithPort))
        {
            return hostWithPort;
        }
        if (hostWithPort[0] == '[')
        {
            int end = hostWithPort.IndexOf(']');
            if (end < 2)
            {
                return hostWithPort;
            }
            string address = hostWithPort.Substring(1, end - 1);
            if (end == hostWithPort.Length - 1)
            {
                return address;
            }
            if (hostWithPort[end + 1] == ':' && TryParsePort(hostWithPort.Substring(end + 2), out int bracketedPort))
            {
                port = bracketedPort;
                return address;
            }
            return hostWithPort;
        }
        int colon = hostWithPort.IndexOf(':');
        if (colon == -1 || colon != hostWithPort.LastIndexOf(':'))
        {
            // No port, or an IPv6 address (two or more colons).
            return hostWithPort;
        }
        if (colon > 0 && TryParsePort(hostWithPort.Substring(colon + 1), out int hostPort))
        {
            port = hostPort;
            return hostWithPort.Substring(0, colon);
        }
        return hostWithPort;
    }

    private static bool TryParsePort(string text, out int port)
    {
        // Digits only: int.TryParse alone would take a sign or spaces.
        port = 0;
        if (text.Length == 0 || text.Length > 5)
        {
            return false;
        }
        foreach (char digit in text)
        {
            if (digit < '0' || digit > '9')
            {
                return false;
            }
        }
        port = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return port >= 1 && port <= 65535;
    }

    /// <summary>
    /// Parses the field of the HTTP proxy: "[user[:password]@]host:port", the host being a name, an IPv4 address or an
    /// IPv6 address in brackets. The credentials end at the last '@', so a password may contain one. False when there is
    /// no valid port: a proxy without a port is not guessed, and silently connecting without it would bypass the proxy.
    /// <paramref name="credentials"/> is null when the text has none.
    /// </summary>
    public static bool TryParseProxy(string text, out string credentials, out string host, out int port)
    {
        credentials = null;
        host = null;
        port = 0;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        string endpoint = text;
        string userAndPassword = null;
        int at = text.LastIndexOf('@');
        if (at != -1)
        {
            userAndPassword = at > 0 ? text.Substring(0, at) : null;
            endpoint = text.Substring(at + 1);
        }
        string proxyHost = SplitHostPort(endpoint, out int? proxyPort);
        if (proxyPort == null || string.IsNullOrEmpty(proxyHost))
        {
            return false;
        }
        credentials = userAndPassword;
        host = proxyHost;
        port = proxyPort.Value;
        return true;
    }

    /// <summary>
    /// The text of the proxy field for the saved proxy settings: the inverse of <see cref="TryParseProxy"/> ("user:password@"
    /// when there are credentials, an IPv6 address in brackets). Null when no proxy is set. Without a valid port the host
    /// alone is returned, so that the user sees what is saved and is asked for the port when connecting.
    /// </summary>
    public static string FormatProxy(string credentials, string host, int port)
    {
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }
        string endpoint = host.IndexOf(':') != -1 ? "[" + host + "]" : host;
        if (port >= 1 && port <= 65535)
        {
            endpoint += ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return string.IsNullOrEmpty(credentials) ? endpoint : credentials + "@" + endpoint;
    }

    public static string StripPortNumber(string hostWithPort)
    {
        return SplitHostPort(hostWithPort, out _);
    }

    public static bool HostNamesEqual(string firstHost, string secondHost)
    {
        return StringComparer.InvariantCultureIgnoreCase.Compare(StripPortNumber(firstHost), StripPortNumber(secondHost)) == 0;
    }

    public void UpdateLastConnectionTime()
    {
        LastConnectionTime = DateTime.Now.ToFileTime();
        try
        {
            if (!string.IsNullOrEmpty(FileName))
            {
                Save();
            }
        }
        catch
        {
        }
    }

    public SSHConnectionSettings CloneSlow()
    {
        return XmlStorage.Deserialize<SSHConnectionSettings>(XmlStorage.Serialize(this), new Type[0]);
    }
}
