using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;

namespace SmarTTY.LocalRepo;

/// <summary>
/// The folders served by Tools > Local OpenWrt repository server and the TCP port. They are kept per computer in
/// %LOCALAPPDATA%\SmarTTY\LocalRepo.xml (next to GUIPreferences.xml), because they point into the local build tree.
/// </summary>
public class LocalRepoSettings
{
    public const int DefaultPort = 8765;

    public List<string> Folders = new List<string>();

    public int Port = DefaultPort;

    /// <summary>For apk devices: also make the device trust the public key of the build (public-key.pem of the OpenWrt tree).</summary>
    public bool TrustBuildKey;

    /// <summary>For opkg devices: turn the signature check of opkg off while the server runs, for feeds without a trusted Packages.sig.</summary>
    public bool AllowUnsignedFeeds;

    /// <summary>
    /// For opkg devices with the signature check on: sign the indexes that SmarTTY generates with a temporary key of the device
    /// (trusted until the server stops), so that the check passes for them. Ignored when unsigned feeds are allowed.
    /// </summary>
    public bool SignGeneratedIndexes = true;

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmarTTY\\LocalRepo.xml");

    public static LocalRepoSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                LocalRepoSettings settings = XmlStorage.LoadFromFile<LocalRepoSettings>(FilePath, new Type[0]);
                if (settings.Folders == null)
                {
                    settings.Folders = new List<string>();
                }
                if (settings.Port < 1 || settings.Port > 65535)
                {
                    settings.Port = DefaultPort;
                }
                return settings;
            }
        }
        catch
        {
        }
        return new LocalRepoSettings();
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
