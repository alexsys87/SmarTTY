using System;
using System.IO;
using System.Xml.Serialization;

namespace VisualTTY.DeviceTerminal;

public class RawConnectionSettings
{
    public DeviceTerminalSettings Settings;

    [XmlIgnore]
    public string FileName;

    public string Icon;

    public void Save()
    {
        XmlSerializer xmlSerializer = new XmlSerializer(typeof(RawConnectionSettings));
        using FileStream stream = File.Create(FileName);
        xmlSerializer.Serialize(stream, this);
    }

    public void UpdateLastUsedTime()
    {
        if (!string.IsNullOrEmpty(FileName))
        {
            Settings.LastConnectionTime = DateTime.Now.ToFileTime();
            Save();
        }
    }
}
