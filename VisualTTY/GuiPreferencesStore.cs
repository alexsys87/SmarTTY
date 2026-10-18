using SmarTTY.Utilities;
using System;
using System.IO;

namespace VisualTTY;

public static class GuiPreferencesStore
{
    private static CommonGUIPreferences _preferences;

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmarTTY\\GUIPreferences.xml");

    public static CommonGUIPreferences Current
    {
        get
        {
            Reload();
            return _preferences;
        }
    }

    private static CommonGUIPreferences Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                CommonGUIPreferences commonGUIPreferences = XmlStorage.LoadFromFile<CommonGUIPreferences>(FilePath, new Type[0]);
                commonGUIPreferences._Timestamp = File.GetLastWriteTime(FilePath);
                return commonGUIPreferences;
            }
        }
        catch
        {
        }
        return new CommonGUIPreferences();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            XmlStorage.SaveToFile(_preferences, FilePath);
            _preferences._Timestamp = File.GetLastWriteTime(FilePath);
        }
        catch
        {
        }
    }

    public static void Reload()
    {
        try
        {
            if (_preferences == null || (File.Exists(FilePath) && File.GetLastWriteTime(FilePath) != _preferences._Timestamp))
            {
                _preferences = Load();
            }
        }
        catch
        {
        }
    }
}
