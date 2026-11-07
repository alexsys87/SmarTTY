using System;
using System.Collections.Generic;
using System.IO;

namespace SmarTTY.Utilities;

public enum LocalLinkAction
{
    /// <summary>Nothing to open: no path, or nothing of that name on this machine.</summary>
    Ignore,

    /// <summary>The shell opens it: an address in the browser, a folder in Explorer, a document in the program it belongs to.</summary>
    OpenWithShell,

    /// <summary>Shown as text in Notepad, which is started by its own name and never runs what it shows.</summary>
    OpenInNotepad
}

/// <summary>
/// What a click on a path in the text of a device terminal (a COM or TCP session) does. The path comes from what the device printed,
/// and the files of this machine that it can point at are anything the user has: the shell runs a file by its extension (a .js in
/// the Windows Script Host, a .py with the Python launcher, a .bat, a .exe, a .lnk, a .msi), so that is left to the few kinds
/// of file that are shown and not run. Everything else is opened as text.
/// </summary>
public static class LocalLinkPolicy
{
    // Documents, data and pictures whose program shows them. Not here: anything that a script host, an interpreter or the system runs,
    // and the pages and drawings that a browser runs scripts of (.html, .htm, .svg) - those are text to look at.
    private static readonly HashSet<string> ShownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".csv", ".tsv", ".json", ".xml", ".ini", ".cfg", ".conf", ".cnf", ".properties",
        ".yml", ".yaml", ".toml", ".diff", ".patch", ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".bmp"
    };

    public static bool IsUrl(string path)
    {
        return path != null && (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="isDirectory">The path is a folder on this machine.</param>
    /// <param name="isFile">The path is a file on this machine.</param>
    public static LocalLinkAction Decide(string path, bool isDirectory, bool isFile)
    {
        if (string.IsNullOrEmpty(path))
        {
            return LocalLinkAction.Ignore;
        }
        if (IsUrl(path) || isDirectory)
        {
            return LocalLinkAction.OpenWithShell;
        }
        if (!isFile)
        {
            return LocalLinkAction.Ignore;
        }
        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return LocalLinkAction.OpenInNotepad;
        }
        return ShownExtensions.Contains(extension) ? LocalLinkAction.OpenWithShell : LocalLinkAction.OpenInNotepad;
    }
}
