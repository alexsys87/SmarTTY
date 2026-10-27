using System;
using System.Collections.Generic;
using System.IO;

namespace SmarTTY.IO;

/// <summary>
/// The rules of the local folder browser (GUI/LocalFolderBrowserWindow): which roots it shows, which subdirectories a
/// node has, how a path is normalized and in which order the entries appear. It touches the file system but has no UI
/// dependency, so the test harness can check it without a window.
/// </summary>
public static class LocalFolderTree
{
    /// <summary>
    /// The roots of the tree: the ready drives and the user's shell folders, each with the name shown in the tree.
    /// A shell folder that is empty or missing is left out. The order is This PC, then the shell folders, then the drives.
    /// A folder that two mounts (or a shell folder and a drive) name the same way is listed once.
    /// </summary>
    public static List<LocalFolderEntry> GetRoots()
    {
        List<LocalFolderEntry> roots = new List<LocalFolderEntry>();
        HashSet<string> seen = new HashSet<string>(PathComparer);
        roots.Add(new LocalFolderEntry("This PC", null));

        AddShellFolder(roots, seen, "Desktop", Environment.SpecialFolder.Desktop);
        AddShellFolder(roots, seen, "Documents", Environment.SpecialFolder.MyDocuments);
        AddShellFolder(roots, seen, "Downloads", null);

        foreach (DriveInfo drive in GetReadyDrives())
        {
            // A stacked mount (the same filesystem mounted twice on one point) yields the same drive name twice.
            if (seen.Add(drive.RootDirectory.FullName))
            {
                roots.Add(new LocalFolderEntry(DriveDisplayName(drive), drive.RootDirectory.FullName));
            }
        }
        return roots;
    }

    /// <summary>
    /// The ready (mounted, formatted) drives of the machine. A mount point the platform reports more than once
    /// is listed once: on Linux <see cref="DriveInfo.GetDrives"/> can repeat a mount (the same device mounted
    /// twice), and the tree must not show the same folder as two nodes.
    /// </summary>
    public static List<DriveInfo> GetReadyDrives()
    {
        List<DriveInfo> drives = new List<DriveInfo>();
        HashSet<string> seen = new HashSet<string>(PathComparer);
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    string name = drive.Name;
                    if (string.IsNullOrEmpty(name) || !drive.IsReady || !seen.Add(name))
                    {
                        continue;
                    }
                    drives.Add(drive);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return drives;
    }

    /// <summary>Paths are compared case-insensitively on Windows and case-sensitively elsewhere.</summary>
    private static StringComparer PathComparer =>
        (Path.DirectorySeparatorChar == '\\') ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The label of a drive: "C:\ (System)", or just the volume name when there is one.</summary>
    public static string DriveDisplayName(DriveInfo drive)
    {
        string name = drive.Name;
        try
        {
            string label = drive.VolumeLabel;
            if (!string.IsNullOrEmpty(label))
            {
                return name + " (" + label + ")";
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return name;
    }

    /// <summary>The subdirectories of <paramref name="path"/>, named without the directory part, sorted case-insensitively.</summary>
    public static List<string> GetChildDirectories(string path)
    {
        List<string> names = new List<string>();
        if (string.IsNullOrEmpty(path))
        {
            return names;
        }
        try
        {
            foreach (string directory in Directory.GetDirectories(path))
            {
                string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                                          exception is ArgumentException || exception is NotSupportedException)
        {
            // A folder that cannot be listed (removed, no rights, a bad name) simply has no children.
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>The full path of <paramref name="childName"/> under <paramref name="parentPath"/>, or null when either is unusable.</summary>
    public static string GetChildPath(string parentPath, string childName)
    {
        if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(childName))
        {
            return null;
        }
        try
        {
            return Path.Combine(parentPath, childName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Normalizes a typed path: trims it, expands a leading "~" to the user profile, makes it absolute against
    /// <paramref name="basePath"/> and removes the trailing separator. Returns null for empty or invalid input.
    /// </summary>
    public static string NormalizePath(string path, string basePath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        string text = path.Trim();
        if (text == "~")
        {
            text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (text.StartsWith("~" + Path.DirectorySeparatorChar) || text.StartsWith("~/"))
        {
            text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text.Substring(2));
        }
        try
        {
            if (!Path.IsPathRooted(text) && !string.IsNullOrEmpty(basePath))
            {
                text = Path.Combine(basePath, text);
            }
            string full = Path.GetFullPath(text);
            // Keep a drive root ("C:\") as it is; a trailing separator is dropped from every other folder.
            if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException ||
                                          exception is PathTooLongException || exception is System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>The parts of <paramref name="path"/> (the drive root included) for building the tree down to a folder.</summary>
    public static List<string> GetPathSegments(string path)
    {
        List<string> segments = new List<string>();
        if (string.IsNullOrEmpty(path))
        {
            return segments;
        }
        try
        {
            string current = path;
            while (!string.IsNullOrEmpty(current))
            {
                segments.Insert(0, current);
                string parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }
        }
        catch (ArgumentException)
        {
        }
        return segments;
    }

    /// <summary>The last part of a path ("project" for "C:\work\project"), used as the node's caption.</summary>
    public static string GetDisplayName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static void AddShellFolder(List<LocalFolderEntry> roots, HashSet<string> seen, string name, Environment.SpecialFolder? folder)
    {
        string path = (folder.HasValue)
            ? Environment.GetFolderPath(folder.Value)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && seen.Add(path))
        {
            roots.Add(new LocalFolderEntry(name, path));
        }
    }
}

/// <summary>One root of the local folder tree: the caption shown in the tree and the path it stands for.</summary>
public struct LocalFolderEntry
{
    /// <summary>The caption ("This PC", "Documents", "C:\ (System)").</summary>
    public string Name { get; }

    /// <summary>The folder the entry opens; null for the "This PC" node, which lists the drives.</summary>
    public string Path { get; }

    public LocalFolderEntry(string name, string path)
    {
        Name = name;
        Path = path;
    }

    public override string ToString()
    {
        return Name;
    }
}
