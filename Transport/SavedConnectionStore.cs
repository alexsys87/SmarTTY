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

    public SavedConnectionStore(string text = null)
    {
        if (text == null)
        {
            _directory = Path.Combine(GlobalSettings.DataDirectory, "SavedConnections");
        }
        else
        {
            _directory = text;
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
            foreach (string text in files)
            {
                try
                {
                    using FileStream stream = File.Open(text, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    SSHConnectionSettings sSHConnectionSettings = (SSHConnectionSettings)xmlSerializer.Deserialize(stream);
                    sSHConnectionSettings.FileName = text;
                    _sshConnections.Add(sSHConnectionSettings);
                }
                catch
                {
                }
            }
            files = Directory.GetFiles(_directory, "*.rawconnection");
            foreach (string text2 in files)
            {
                try
                {
                    using FileStream stream2 = File.Open(text2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    RawConnectionSettings rawConnectionSettings = (RawConnectionSettings)xmlSerializer2.Deserialize(stream2);
                    rawConnectionSettings.FileName = text2;
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

    internal static void TouchTimestamp(string text)
    {
        File.WriteAllText(Path.Combine(text, "_update_timestamp.txt"), "The timestamp of this file indicates the last time some of the settings files here were changed");
    }

    public static string SanitizeFileName(string text2)
    {
        if (text2 == null)
        {
            return "";
        }
        char[] array = text2.ToCharArray();
        string text = "\\/<>:| ;&=";
        for (int num = 0; num < array.Length; num++)
        {
            if (text.IndexOf(array[num]) != -1)
            {
                array[num] = '_';
            }
        }
        return new string(array);
    }

    public void SaveSshConnection(SSHConnectionSettings sshConnectionSettings, Action<string> action = null)
    {
        action?.Invoke("Saving connection settings...");
        sshConnectionSettings.FileName = Path.Combine(_directory, $"{SanitizeFileName(sshConnectionSettings.UserName)}@{SanitizeFileName(sshConnectionSettings.HostName)}.sshconnection");
        action?.Invoke("Settings file name:" + sshConnectionSettings.FileName);
        for (int num = 0; num < _sshConnections.Count; num++)
        {
            if (StringComparer.InvariantCultureIgnoreCase.Compare(_sshConnections[num].FileName, sshConnectionSettings.FileName) == 0)
            {
                action?.Invoke($"Found the same settings at position {num}. Removing...");
                _sshConnections.RemoveAt(num--);
            }
        }
        _sshConnections.Add(sshConnectionSettings);
        action?.Invoke("Saving connection index to " + sshConnectionSettings.FileName + "...");
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

    public SSHConnectionSettings FindSshConnection(string text, string text2)
    {
        try
        {
            ReloadIfChanged();
            foreach (SSHConnectionSettings item in _sshConnections)
            {
                if (StringComparer.InvariantCultureIgnoreCase.Compare(item.HostName, text) == 0 && StringComparer.InvariantCultureIgnoreCase.Compare(item.UserName, text2) == 0)
                {
                    return item;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public List<SSHConnectionSettings> FindByHost(string text)
    {
        ReloadIfChanged();
        List<SSHConnectionSettings> list = new List<SSHConnectionSettings>();
        if (text == null)
        {
            return list;
        }
        foreach (SSHConnectionSettings item in _sshConnections)
        {
            if (SSHConnectionSettings.HostNamesEqual(item.HostName, text))
            {
                list.Add(item);
            }
        }
        return list;
    }

    public void SaveRawConnection(RawConnectionSettings rawConnectionSettings)
    {
        for (int num = 0; num < _rawConnections.Count; num++)
        {
            if (_rawConnections[num].Settings != null && _rawConnections[num].Settings.Connection.FileNameBase == rawConnectionSettings.Settings.Connection.FileNameBase)
            {
                _rawConnections.RemoveAt(num--);
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
