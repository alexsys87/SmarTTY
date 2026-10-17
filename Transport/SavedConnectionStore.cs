using System;
using System.Collections.Generic;
using System.IO;
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
            XmlSerializer xmlSerializer2 = new XmlSerializer(typeof(RawConnectionSettings));
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
                    using FileStream stream2 = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    RawConnectionSettings rawConnectionSettings = (RawConnectionSettings)xmlSerializer2.Deserialize(stream2);
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
        for (int i = 0; i < _rawConnections.Count; i++)
        {
            if (_rawConnections[i].Settings != null && _rawConnections[i].Settings.Connection.FileNameBase == rawConnectionSettings.Settings.Connection.FileNameBase)
            {
                _rawConnections.RemoveAt(i--);
            }
        }
        _rawConnections.Add(rawConnectionSettings);
        rawConnectionSettings.FileName = Path.Combine(_directory, $"{rawConnectionSettings.Settings.Connection.FileNameBase}.rawconnection");
        rawConnectionSettings.Save();
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
