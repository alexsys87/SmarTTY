using SmarTTY.Transport;
using SmarTTY.Utilities;
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
        // The same way as the SSH profiles: never a half-written file (the profile is saved at every connection).
        XmlStorage.WriteAtomically(FileName, stream => xmlSerializer.Serialize(stream, this), FileName + ".bak");
        try
        {
            // Other windows and instances notice the change by this file (see SavedConnectionStore).
            SavedConnectionStore.TouchTimestamp(Path.GetDirectoryName(FileName));
        }
        catch
        {
        }
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
