using System;
using System.Collections.Generic;

namespace VisualTTY;

/// <summary>
/// The size of a window that the user changed, kept in the GUI preferences (<c>GUIPreferences.xml</c>) under the name of the window
/// class, so the window opens at that size again.
/// </summary>
public class RememberedWindowSize
{
    /// <summary>The full name of the window class.</summary>
    public string Key { get; set; }

    /// <summary>The width in device-independent pixels.</summary>
    public int Width { get; set; }

    /// <summary>The height in device-independent pixels.</summary>
    public int Height { get; set; }
}

/// <summary>The list of <see cref="RememberedWindowSize"/>: find and set an entry (UI-free, so the tests run it on plain .NET).</summary>
public static class WindowSizeList
{
    /// <summary>The most entries kept; the oldest goes first.</summary>
    public const int MaxEntries = 200;

    /// <summary>Finds the size of the window class <paramref name="key"/>.</summary>
    public static bool TryGet(List<RememberedWindowSize> list, string key, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (list == null || string.IsNullOrEmpty(key))
        {
            return false;
        }
        foreach (RememberedWindowSize entry in list)
        {
            if (entry != null && string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                if (entry.Width <= 0 || entry.Height <= 0)
                {
                    return false;
                }
                width = entry.Width;
                height = entry.Height;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Sets the size of the window class <paramref name="key"/>; the entry goes to the end of the list (the newest). Returns false
    /// when the size is nonsense and nothing was changed.
    /// </summary>
    public static bool Set(List<RememberedWindowSize> list, string key, int width, int height)
    {
        if (list == null || string.IsNullOrEmpty(key) || width <= 0 || height <= 0)
        {
            return false;
        }
        list.RemoveAll(entry => entry == null || string.Equals(entry.Key, key, StringComparison.Ordinal));
        list.Add(new RememberedWindowSize { Key = key, Width = width, Height = height });
        if (list.Count > MaxEntries)
        {
            list.RemoveRange(0, list.Count - MaxEntries);
        }
        return true;
    }
}
