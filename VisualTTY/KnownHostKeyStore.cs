using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace VisualTTY;

internal class KnownHostKeyStore
{
    private List<KnownHostKey> _keys;

    private static string FilePath => Path.Combine(GlobalSettings.DataDirectory, "KnownHosts.xml");

    public KnownHostKeyStore()
    {
        try
        {
            XmlSerializer xmlSerializer = new XmlSerializer(typeof(List<KnownHostKey>));
            using FileStream stream = File.OpenRead(FilePath);
            _keys = (List<KnownHostKey>)xmlSerializer.Deserialize(stream);
        }
        catch
        {
            _keys = new List<KnownHostKey>();
            // A file that exists but cannot be read is not the same as no file: the next Save would overwrite it and
            // every host key the user has accepted so far would be gone. Keep a copy.
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Copy(FilePath, FilePath + ".unreadable", overwrite: true);
                }
            }
            catch
            {
            }
        }
    }

    // Host names are not case sensitive: "Example.com" and "example.com" are the same host.
    private static bool SameHost(string first, string second)
    {
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    public KnownHostKey Find(string text)
    {
        foreach (KnownHostKey item in this._keys)
        {
            if (SameHost(item.HostName, text))
            {
                return item;
            }
        }
        return null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(GlobalSettings.DataDirectory);
            XmlSerializer xmlSerializer = new XmlSerializer(typeof(List<KnownHostKey>));
            // Write next to the file and swap, so that a crash in the middle does not leave a half-written store.
            string temporaryPath = FilePath + ".tmp";
            using (FileStream stream = File.Create(temporaryPath))
            {
                xmlSerializer.Serialize(stream, _keys);
            }
            if (File.Exists(FilePath))
            {
                File.Replace(temporaryPath, FilePath, null);
            }
            else
            {
                File.Move(temporaryPath, FilePath);
            }
        }
        catch
        {
        }
    }

    public void Update(string text, SshSession.HostKeyInfo hostKeyInfo)
    {
        this._keys.RemoveAll(item => SameHost(item.HostName, text));
        this._keys.Add(new KnownHostKey
        {
            HostName = text,
            KeyType = hostKeyInfo.Algorithm,
            KeyBase64 = Convert.ToBase64String(hostKeyInfo.KeyData)
        });
    }
}
