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

    public string ProxyAuth { get; set; }

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

    public bool IsEquivalentToExceptPassword(SSHConnectionSettings settings)
    {
        if (settings.HostName != HostName)
        {
            return false;
        }
        if (settings.UserName != UserName)
        {
            return false;
        }
        if (settings.AuthenticationType != AuthenticationType)
        {
            return false;
        }
        if (settings.ExplicitKeyFile != ExplicitKeyFile)
        {
            return false;
        }
        if (settings.ProxyAuth != ProxyAuth)
        {
            return false;
        }
        if (settings.ProxyServer != ProxyServer)
        {
            return false;
        }
        if (settings.ProxyPort != ProxyPort)
        {
            return false;
        }
        if (settings.EnableCompression != EnableCompression)
        {
            return false;
        }
        if (settings.EnableOnTheFlyTAR != EnableOnTheFlyTAR)
        {
            return false;
        }
        if (settings.EnableX11Forwarding != EnableX11Forwarding)
        {
            return false;
        }
        if (settings.AutoStartSocksProxy != AutoStartSocksProxy)
        {
            return false;
        }
        if (settings.ConnectionAlias != ConnectionAlias)
        {
            return false;
        }
        if (settings.SuppressKeyMismatchWarning != SuppressKeyMismatchWarning)
        {
            return false;
        }
        if (settings.KeepaliveInterval != KeepaliveInterval)
        {
            return false;
        }
        return true;
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

    public static string StripPortNumber(string hostWithPort)
    {
        if (hostWithPort == null)
        {
            return null;
        }
        int index = hostWithPort.IndexOf(':');
        if (index == -1)
        {
            return hostWithPort;
        }
        return hostWithPort.Substring(0, index);
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
