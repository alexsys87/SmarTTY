using System;
using System.Collections.Generic;

namespace SmarTTY.Utilities;

/// <summary>
/// The rules of the icon paths (stage 5A), without WPF so that the tests can check every path the code names: a path is the one the PNG
/// file had under <c>icons/</c> (<c>"MainWindow/refresh16.png"</c>, <c>"../icons/net16.png"</c>,
/// <c>"pack://application:,,,/icons/keys/locked.png"</c>, the extension optional); its folder names the resource dictionary of
/// <c>GUI/Icons/</c> the vector image is in, and its name the key of the image (<c>refresh16DrawingImage</c>: a character that is not
/// a letter, a digit or an underscore is an underscore). <c>GUI/IconImages</c> loads the images.
/// </summary>
internal static class IconPaths
{
    // The dictionary of each folder of the old icons/ (the folder names in lower case).
    private static readonly Dictionary<string, string> _dictionaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "", "Root" },
        { "mainwindow", "MainWindow" },
        { "editor", "Editor" },
        { "menu", "Menu" },
        { "keys", "Keys" },
        { "transfers", "Transfers" },
        { "filetypes", "FileTypes" }
    };

    // The size in device-independent pixels at which WPF showed a PNG that was not 16 x 16 (its pixels at the DPI it declared:
    // a 16-pixel icon of 72 DPI was 21.33 wide, one that declared 11.3 DPI 136.51). The rest were 16 x 16.
    private static readonly Dictionary<string, double> _sizes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        { "documenths", 21.33 },
        { "fonths", 21.33 },
        { "playhs", 21.33 },
        { "serialport", 48.01 },
        { "about", 128 },
        { "access-point", 48 },
        { "chip", 48 },
        { "cloud", 48 },
        { "color16", 17.07 },
        { "computer-laptop", 34.13 },
        { "computer", 68.28 },
        { "container", 48 },
        { "database", 48 },
        { "dialog-browse-folder", 21.38 },
        { "dialog-credentials", 96 },
        { "dialog-host-key-continue", 32 },
        { "dialog-host-key-warning", 96 },
        { "dialog-host-key", 72 },
        { "dialog-public-key", 128 },
        { "dialog-scp-download", 64.15 },
        { "dialog-scp-upload", 64.15 },
        { "edit16", 21.38 },
        { "firewall", 48 },
        { "gear16", 136.51 },
        { "ip-camera", 48 },
        { "lock16", 136.51 },
        { "nas", 48 },
        { "net16", 16.03 },
        { "open16", 21.38 },
        { "phone", 48 },
        { "router", 48 },
        { "server", 48 },
        { "single-board", 48 },
        { "sort16", 21.33 },
        { "switch", 48 },
        { "term16", 136.51 },
        { "term16v", 136.82 },
        { "terminal", 48 },
        { "unlock16", 136.51 },
        { "unlocked32", 32 },
        { "user48", 25.6 },
        { "vds", 48 },
        { "mainwindow/findhs", 21.33 },
        { "mainwindow/add16", 136.51 },
        { "mainwindow/archive16", 17.07 },
        { "mainwindow/busy", 17.07 },
        { "mainwindow/close8", 8.01 },
        { "mainwindow/close8red", 8.01 },
        { "mainwindow/copy", 21.38 },
        { "mainwindow/download", 21.38 },
        { "mainwindow/editor", 136.51 },
        { "mainwindow/err9", 9 },
        { "mainwindow/getdir", 16.03 },
        { "mainwindow/go-down-search", 136.51 },
        { "mainwindow/go-up-search", 136.51 },
        { "mainwindow/new16", 17.07 },
        { "mainwindow/open16", 21.38 },
        { "mainwindow/paste", 21.38 },
        { "mainwindow/pubkey", 16.03 },
        { "mainwindow/qdownload", 21.38 },
        { "mainwindow/refresh16", 21.33 },
        { "mainwindow/save16", 21.33 },
        { "mainwindow/updir", 16.03 },
        { "mainwindow/upload", 21.38 },
        { "keys/locked", 128 },
        { "keys/unlocked", 128 },
        { "transfers/check", 21.33 },
        { "transfers/clock9", 9 },
        { "transfers/down", 21.33 },
        { "transfers/pause16", 16.01 },
        { "transfers/pause9", 9 },
        { "transfers/removecomp", 21.38 },
        { "transfers/up", 21.33 },
    };

    /// <summary>The file of the resource dictionary (in <c>GUI/Icons/</c>, without <c>.xaml</c>) of a folder <see cref="TryParse"/> gave.</summary>
    public static string DictionaryOf(string folder)
    {
        return _dictionaries[folder];
    }

    /// <summary>The folders of the old <c>icons/</c> (in lower case, empty for <c>icons/</c> itself) and their dictionaries.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Dictionaries => _dictionaries;

    /// <summary>
    /// Splits an icon path into its folder under the old <c>icons/</c> (empty for <c>icons/</c> itself, in lower case) and the name of
    /// the file without <c>.png</c>. False for a path that names no folder of the icons.
    /// </summary>
    public static bool TryParse(string path, out string folder, out string name)
    {
        folder = null;
        name = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        string rest = path.Trim().Replace('\\', '/');
        int icons = rest.LastIndexOf("icons/", StringComparison.OrdinalIgnoreCase);
        if (icons >= 0 && (icons == 0 || rest[icons - 1] == '/'))
        {
            rest = rest.Substring(icons + "icons/".Length);
        }
        rest = rest.TrimStart('/');
        if (rest.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest.Substring(0, rest.Length - 4);
        }
        int slash = rest.LastIndexOf('/');
        string folderPart = slash >= 0 ? rest.Substring(0, slash) : "";
        string namePart = slash >= 0 ? rest.Substring(slash + 1) : rest;
        if (namePart.Length == 0 || !_dictionaries.ContainsKey(folderPart))
        {
            return false;
        }
        folder = folderPart.ToLowerInvariant();
        name = namePart;
        return true;
    }

    /// <summary>The key of an icon in its dictionary: the name with what is not a letter, a digit or an underscore as an underscore.</summary>
    public static string KeyOf(string name)
    {
        char[] key = name.ToCharArray();
        for (int i = 0; i < key.Length; i++)
        {
            char ch = key[i];
            bool keep = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_';
            if (!keep)
            {
                key[i] = '_';
            }
        }
        return new string(key) + "DrawingImage";
    }

    /// <summary>The size at which the icon is shown (that of its PNG), in device-independent pixels.</summary>
    public static double SizeOf(string folder, string name)
    {
        return _sizes.TryGetValue(folder.Length == 0 ? name : folder + "/" + name, out double size) ? size : 16;
    }

}
