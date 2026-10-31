using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using VisualTTY;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Transport;

public class SavedConnectionStore
{
    private DateTime _loadedAt;

    private List<SSHConnectionSettings> _sshConnections = new List<SSHConnectionSettings>();

    private List<RawConnectionSettings> _rawConnections = new List<RawConnectionSettings>();

    private string _directory;

    public IEnumerable<SSHConnectionSettings> SshConnections
    {
        get
        {
            ReloadIfChanged();
            return _sshConnections;
        }
    }

    public IEnumerable<RawConnectionSettings> RawConnections
    {
        get
        {
            ReloadIfChanged();
            return _rawConnections;
        }
    }

    public SavedConnectionStore(string directory = null)
    {
        if (directory == null)
        {
            _directory = Path.Combine(GlobalSettings.DataDirectory, "SavedConnections");
        }
        else
        {
            _directory = directory;
        }
        Reload();
    }

    private void Reload()
    {
        try
        {
            _sshConnections.Clear();
            Directory.CreateDirectory(_directory);
            XmlSerializer xmlSerializer = new XmlSerializer(typeof(SSHConnectionSettings));
            XmlSerializer rawSettingsSerializer = new XmlSerializer(typeof(RawConnectionSettings));
            string[] files = Directory.GetFiles(_directory, "*.sshconnection");
            foreach (string file in files)
            {
                try
                {
                    using FileStream stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    SSHConnectionSettings sshSettings = (SSHConnectionSettings)xmlSerializer.Deserialize(stream);
                    sshSettings.FileName = file;
                    _sshConnections.Add(sshSettings);
                }
                catch
                {
                }
            }
            files = Directory.GetFiles(_directory, "*.rawconnection");
            foreach (string file in files)
            {
                try
                {
                    using FileStream item = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    RawConnectionSettings rawConnectionSettings = (RawConnectionSettings)rawSettingsSerializer.Deserialize(item);
                    rawConnectionSettings.FileName = file;
                    _rawConnections.Add(rawConnectionSettings);
                }
                catch
                {
                }
            }
            _loadedAt = DateTime.Now;
        }
        catch
        {
        }
    }

    private void ReloadIfChanged()
    {
        string path = Path.Combine(_directory, "_update_timestamp.txt");
        if (File.Exists(path))
        {
            if (File.GetLastWriteTime(path) > _loadedAt)
            {
                Reload();
            }
        }
        else
        {
            Reload();
            TouchTimestamp(_directory);
        }
    }

    internal static void TouchTimestamp(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "_update_timestamp.txt"), "The timestamp of this file indicates the last time some of the settings files here were changed");
    }

    public static string SanitizeFileName(string fileName)
    {
        if (fileName == null)
        {
            return "";
        }
        char[] charArray = fileName.ToCharArray();
        string invalidChars = "\\/<>:| ;&=";
        for (int i = 0; i < charArray.Length; i++)
        {
            if (invalidChars.IndexOf(charArray[i]) != -1)
            {
                charArray[i] = '_';
            }
        }
        return new string(charArray);
    }

    public void SaveSshConnection(SSHConnectionSettings sshConnectionSettings, Action<string> progressCallback = null)
    {
        progressCallback?.Invoke("Saving connection settings...");
        sshConnectionSettings.FileName = Path.Combine(_directory, $"{SanitizeFileName(sshConnectionSettings.UserName)}@{SanitizeFileName(sshConnectionSettings.HostName)}.sshconnection");
        progressCallback?.Invoke("Settings file name:" + sshConnectionSettings.FileName);
        for (int i = 0; i < _sshConnections.Count; i++)
        {
            if (StringComparer.InvariantCultureIgnoreCase.Compare(_sshConnections[i].FileName, sshConnectionSettings.FileName) == 0)
            {
                progressCallback?.Invoke($"Found the same settings at position {i}. Removing...");
                _sshConnections.RemoveAt(i--);
            }
        }
        _sshConnections.Add(sshConnectionSettings);
        progressCallback?.Invoke("Saving connection index to " + sshConnectionSettings.FileName + "...");
        sshConnectionSettings.Save();
    }

    public void DeleteSshConnection(SSHConnectionSettings sshConnectionSettings)
    {
        _sshConnections.Remove(sshConnectionSettings);
        if (sshConnectionSettings.FileName != null)
        {
            try
            {
                File.Delete(sshConnectionSettings.FileName);
                TouchTimestamp(_directory);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Splits a <c>user@host</c> command-line argument, the form <c>ssh</c> accepts. Returns false when the argument
    /// has no <c>@</c> or one of the two sides is empty, so the caller keeps looking the argument up as a plain name.
    /// </summary>
    public static bool TrySplitUserHost(string argument, out string userName, out string hostName)
    {
        userName = null;
        hostName = null;
        if (argument == null)
        {
            return false;
        }
        int separator = argument.IndexOf('@');
        if (separator <= 0 || separator == argument.Length - 1)
        {
            return false;
        }
        userName = argument.Substring(0, separator);
        hostName = argument.Substring(separator + 1);
        return true;
    }

    /// <summary>
    /// Resolves a <c>user@host</c> argument to a saved connection: the exact user and host first, then any connection
    /// to that host (which is what <c>ssh host</c> does when the user name is not stored). Returns null when the
    /// argument is not a <c>user@host</c> pair or nothing matches.
    /// </summary>
    public SSHConnectionSettings FindSshConnectionForArgument(string argument)
    {
        if (!TrySplitUserHost(argument, out string userName, out string hostName))
        {
            return null;
        }
        return FindSshConnection(hostName, userName) ?? FindByHost(hostName).FirstOrDefault();
    }

    public SSHConnectionSettings FindSshConnection(string hostName, string userName)
    {
        try
        {
            ReloadIfChanged();
            foreach (SSHConnectionSettings connection in _sshConnections)
            {
                if (StringComparer.InvariantCultureIgnoreCase.Compare(connection.HostName, hostName) == 0 && StringComparer.InvariantCultureIgnoreCase.Compare(connection.UserName, userName) == 0)
                {
                    return connection;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public List<SSHConnectionSettings> FindByHost(string hostName)
    {
        ReloadIfChanged();
        List<SSHConnectionSettings> matches = new List<SSHConnectionSettings>();
        if (hostName == null)
        {
            return matches;
        }
        foreach (SSHConnectionSettings connection in _sshConnections)
        {
            if (SSHConnectionSettings.HostNamesEqual(connection.HostName, hostName))
            {
                matches.Add(connection);
            }
        }
        return matches;
    }

    public void SaveRawConnection(RawConnectionSettings rawConnectionSettings)
    {
        List<RawConnectionSettings> replaced = new List<RawConnectionSettings>();
        for (int i = 0; i < _rawConnections.Count; i++)
        {
            // Host names are case-insensitive and so are file names on NTFS: "Device.local:23" and "device.local:23" are one profile.
            if (_rawConnections[i].Settings != null && string.Equals(_rawConnections[i].Settings.Connection.FileNameBase, rawConnectionSettings.Settings.Connection.FileNameBase, StringComparison.OrdinalIgnoreCase))
            {
                replaced.Add(_rawConnections[i]);
                _rawConnections.RemoveAt(i--);
            }
        }
        _rawConnections.Add(rawConnectionSettings);
        string newFileName = Path.Combine(_directory, ToFileName(rawConnectionSettings.Settings.Connection.FileNameBase) + ".rawconnection");
        rawConnectionSettings.FileName = newFileName;
        foreach (RawConnectionSettings old in replaced)
        {
            // The same connection saved under another name earlier (a telnet over TLS profile was once named like the plain
            // one): its old file would come back as a second entry at the next start.
            if (!ReferenceEquals(old, rawConnectionSettings) && !string.IsNullOrEmpty(old.FileName)
                && !string.Equals(old.FileName, newFileName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(old.FileName);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        rawConnectionSettings.Save();
    }

    /// <summary>
    /// The connection key as a file name: an IPv6 host ("::1_23") or a serial device description may contain characters
    /// that Windows does not allow in a file name, and saving such a connection used to fail.
    /// </summary>
    internal static string ToFileName(string fileNameBase)
    {
        string name = string.IsNullOrWhiteSpace(fileNameBase) ? "connection" : fileNameBase;
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }
        return name;
    }

    public void DeleteRawConnection(RawConnectionSettings rawConnectionSettings)
    {
        _rawConnections.Remove(rawConnectionSettings);
        if (rawConnectionSettings.FileName != null)
        {
            try
            {
                File.Delete(rawConnectionSettings.FileName);
            }
            catch
            {
            }
        }
    }
}
