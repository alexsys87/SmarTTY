using SmarTTY.Utilities;
using System;
using System.IO;
using System.Net;

namespace SmarTTY.GUI.Backend;

/// <summary>
/// The settings of Tools > SOCKS5 proxy over SSH, kept per computer in %LOCALAPPDATA%\SmarTTY\SocksProxy.xml
/// (next to LocalRepo.xml and GUIPreferences.xml). It lives with the GUI code, not in Ssh/: the files of Ssh/ are also compiled
/// by tests/SshNetIntegrationTests, which does not have the XML storage.
/// </summary>
public class SocksProxySettings
{
    public const string DefaultBindAddress = "127.0.0.1";

    public const int DefaultPort = 1080;

    /// <summary>The interval of the keepalive messages, like "ssh -o ServerAliveInterval=30".</summary>
    public const int DefaultKeepAliveSeconds = 30;

    /// <summary>The local address the proxy listens on: the loopback address unless the user wants other computers to use it.</summary>
    public string BindAddress = DefaultBindAddress;

    public int Port = DefaultPort;

    /// <summary>Seconds between keepalive messages of the SSH connection while the proxy runs; 0 leaves the connection as it is.</summary>
    public int KeepAliveSeconds = DefaultKeepAliveSeconds;

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmarTTY\\SocksProxy.xml");

    public static SocksProxySettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                SocksProxySettings settings = XmlStorage.LoadFromFile<SocksProxySettings>(FilePath, new Type[0]);
                if (string.IsNullOrWhiteSpace(settings.BindAddress) || !IPAddress.TryParse(settings.BindAddress.Trim(), out _))
                {
                    settings.BindAddress = DefaultBindAddress;
                }
                if (settings.Port < 1 || settings.Port > 65535)
                {
                    settings.Port = DefaultPort;
                }
                if (settings.KeepAliveSeconds < 0 || settings.KeepAliveSeconds > 86400)
                {
                    settings.KeepAliveSeconds = DefaultKeepAliveSeconds;
                }
                return settings;
            }
        }
        catch
        {
        }
        return new SocksProxySettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            XmlStorage.SaveToFile(this, FilePath);
        }
        catch
        {
        }
    }
}
