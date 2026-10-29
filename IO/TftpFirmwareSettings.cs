using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;

namespace SmarTTY.IO;

/// <summary>
/// What Tools &gt; TFTP server / U-Boot upload remembers between runs: the files the server hands out, the addresses of
/// this computer and of the board, the U-Boot prompt and the commands that run after the download. It is kept per
/// computer in %LOCALAPPDATA%\SmarTTY\TftpFirmware.xml, next to LocalRepo.xml, SocksProxy.xml and GUIPreferences.xml.
///
/// The class has no platform-specific code, so it can be tested anywhere. Older versions kept these values in the
/// registry (HKCU\SOFTWARE\Sysprogs\SmarTTY\Settings\Tftp); the window imports that key once when the file is absent.
/// </summary>
public class TftpFirmwareSettings
{
    public const string DefaultLoadAddress = "${loadaddr}";

    /// <summary>A file the server hands out: the name it answers to and where it is on disk.</summary>
    public class ServedFileEntry
    {
        public string Name;

        public string FullPath;
    }

    /// <summary>The files the server hands out, in the order the user added them.</summary>
    public List<ServedFileEntry> Files = new List<ServedFileEntry>();

    /// <summary>The address of this computer as the board sees it; also the address the server listens on.</summary>
    public string ServerIp;

    /// <summary>The address of the board; empty leaves ipaddr as it is in the U-Boot environment.</summary>
    public string DeviceIp = string.Empty;

    /// <summary>The address tftpboot loads to; ${loadaddr} is the board's own variable.</summary>
    public string LoadAddress = DefaultLoadAddress;

    /// <summary>The end of the U-Boot prompt; empty means SmarTTY recognizes the common prompts itself.</summary>
    public string Prompt = string.Empty;

    /// <summary>The commands to run after the download, one per line.</summary>
    public string After = string.Empty;

    /// <summary>Answer only the board's address (the flag does nothing while the board address is empty).</summary>
    public bool RestrictClient = true;

    /// <summary>The file chosen in "Download this one".</summary>
    public string FileToSend;

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmarTTY\\TftpFirmware.xml");

    /// <summary>True when the settings were saved before (the window imports the legacy registry key only when this is false).</summary>
    public static bool Exists()
    {
        try
        {
            return File.Exists(FilePath);
        }
        catch
        {
            return false;
        }
    }

    public static TftpFirmwareSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                TftpFirmwareSettings settings = XmlStorage.LoadFromFile<TftpFirmwareSettings>(FilePath, new Type[0]);
                if (settings.Files == null)
                {
                    settings.Files = new List<ServedFileEntry>();
                }
                return settings;
            }
        }
        catch
        {
        }
        return new TftpFirmwareSettings();
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
