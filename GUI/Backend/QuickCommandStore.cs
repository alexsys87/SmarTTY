using SmarTTY.Terminal;
using System;
using System.IO;
using VisualTTY;

namespace SmarTTY.GUI.Backend;

/// <summary>
/// The quick commands of Tools > Quick commands, in QuickCommands.xml next to Settings.xml (%APPDATA%\SmarTTY, or PortableSettings).
/// The file is read again when it has changed on disk (an import of settings replaces it), so the menu of the terminal always
/// shows what is saved.
/// </summary>
internal static class QuickCommandStore
{
    private static QuickCommandList _Cached;

    private static DateTime _CachedStamp;

    private static string FilePath => Path.Combine(GlobalSettings.DataDirectory, QuickCommandList.FileName);

    /// <summary>The saved list; an empty one when there is none. A damaged file throws (the window says so).</summary>
    public static QuickCommandList Load()
    {
        string path = FilePath;
        if (!File.Exists(path))
        {
            _Cached = null;
            return new QuickCommandList();
        }
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        if (_Cached == null || stamp != _CachedStamp)
        {
            _Cached = QuickCommandList.Load(GlobalSettings.DataDirectory);
            _CachedStamp = stamp;
        }
        return _Cached;
    }

    /// <summary>The saved list for the menu of the terminal: an empty one when the file cannot be read.</summary>
    public static QuickCommandList LoadForMenu()
    {
        try
        {
            return Load();
        }
        catch (Exception)
        {
            return new QuickCommandList();
        }
    }

    public static void Save(QuickCommandList list)
    {
        list.Save(GlobalSettings.DataDirectory);
        _Cached = list;
        _CachedStamp = File.GetLastWriteTimeUtc(FilePath);
    }
}
