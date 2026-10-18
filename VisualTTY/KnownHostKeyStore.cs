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
        }
    }

    public KnownHostKey Find(string text)
    {
        foreach (KnownHostKey item in this._keys)
        {
            if (item.HostName == text)
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
            using FileStream stream = File.Create(FilePath);
            xmlSerializer.Serialize(stream, _keys);
        }
        catch
        {
        }
    }

    public void Update(string text, SshSession.HostKeyInfo hostKeyInfo)
    {
        foreach (KnownHostKey item in this._keys)
        {
            if (item.HostName == text)
            {
                this._keys.Remove(item);
                break;
            }
        }
        this._keys.Add(new KnownHostKey
        {
            HostName = text,
            KeyType = hostKeyInfo.Algorithm,
            KeyBase64 = Convert.ToBase64String(hostKeyInfo.KeyData)
        });
    }
}
