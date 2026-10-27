using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;

namespace SmarTTY.IO;

/// <summary>
/// The rules of the local file dialog (GUI/LocalFileDialogWindow): which directories a path walks to, which entries a
/// folder holds (the subdirectories and, when the dialog is not a folder-only one, the files), how they are ordered and
/// which of them a filter keeps. It touches the file system but has no UI dependency, so the test harness can check it
/// without a window.
/// </summary>
public static class LocalFileListing
{
    /// <summary>
    /// The entries of <paramref name="directory"/>: its subdirectories, and its files when
    /// <paramref name="includeFiles"/> is set. <paramref name="entry"/> is the selected filter entry; when it is null
    /// or has no patterns, every file is kept. The result is ordered directories first, then files, each by name.
    /// </summary>
    public static List<LocalFileEntry> List(string directory, FileDialogFilterEntry entry, bool includeFiles)
    {
        List<LocalFileEntry> directories = new List<LocalFileEntry>();
        List<LocalFileEntry> files = new List<LocalFileEntry>();
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                foreach (string child in Directory.GetDirectories(directory))
                {
                    string name = NameOf(child);
                    if (name != null)
                    {
                        directories.Add(new LocalFileEntry(name, isDirectory: true, size: 0, modified: null));
                    }
                }
                if (includeFiles)
                {
                    foreach (string child in Directory.GetFiles(directory))
                    {
                        string name = NameOf(child);
                        if (name == null || !FileDialogFilter.MatchesEntry(entry, name))
                        {
                            continue;
                        }
                        long size = 0;
                        DateTime? modified = null;
                        try
                        {
                            FileInfo info = new FileInfo(child);
                            size = info.Length;
                            modified = info.LastWriteTime;
                        }
                        catch (Exception exception) when (IsFileSystemError(exception))
                        {
                            // A file that vanished between the listing and the stat still shows, with an empty size.
                        }
                        files.Add(new LocalFileEntry(name, isDirectory: false, size: size, modified: modified));
                    }
                }
            }
            catch (Exception exception) when (IsFileSystemError(exception))
            {
                // A folder that cannot be listed (removed, no rights, a bad name) simply has no entries.
            }
        }
        directories.Sort(ByName);
        files.Sort(ByName);
        directories.AddRange(files);
        return directories;
    }

    /// <summary>
    /// True when <paramref name="directory"/> is an existing directory. A null or empty path is not one.
    /// </summary>
    public static bool DirectoryExists(string directory)
    {
        return !string.IsNullOrEmpty(directory) && Directory.Exists(directory);
    }

    /// <summary>
    /// The path the dialog returns for a chosen entry: the file name is combined with the current directory. A null or
    /// empty name yields null.
    /// </summary>
    public static string Combine(string directory, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        if (string.IsNullOrEmpty(directory))
        {
            return name;
        }
        return LocalFolderTree.GetChildPath(directory, name) ?? name;
    }

    private static int ByName(LocalFileEntry left, LocalFileEntry right)
    {
        return StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
    }

    private static string NameOf(string path)
    {
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static bool IsFileSystemError(Exception exception)
    {
        return exception is IOException || exception is UnauthorizedAccessException ||
               exception is ArgumentException || exception is NotSupportedException;
    }
}

/// <summary>One entry of the local file dialog: a folder or a file, with what the list shows for it.</summary>
public sealed class LocalFileEntry
{
    /// <summary>The name shown in the list.</summary>
    public string Name { get; }

    /// <summary>True for a folder, false for a file.</summary>
    public bool IsDirectory { get; }

    /// <summary>The size in bytes of a file; 0 for a folder.</summary>
    public long Size { get; }

    /// <summary>The last write time of a file, or null for a folder or an unreadable file.</summary>
    public DateTime? Modified { get; }

    public LocalFileEntry(string name, bool isDirectory, long size, DateTime? modified)
    {
        Name = name;
        IsDirectory = isDirectory;
        Size = size;
        Modified = modified;
    }

    /// <summary>The size column: empty for a folder, otherwise the human-readable size.</summary>
    public string SizeText => IsDirectory ? "" : StringUtils.FormatSize(Size, appendUnit: true);

    /// <summary>The "modified" column: empty for a folder or an unreadable file.</summary>
    public string ModifiedText => Modified.HasValue ? Modified.Value.ToString("yyyy-MM-dd HH:mm") : "";

    public override string ToString()
    {
        return Name;
    }
}
