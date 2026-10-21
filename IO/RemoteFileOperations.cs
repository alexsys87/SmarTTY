using System;
using System.Collections.Generic;
using System.Linq;

namespace SmarTTY.IO;

/// <summary>
/// Shell commands and naming rules for the file operations of the file list (copy, move, rename, delete, new file and
/// folder). The commands use absolute, single-quoted paths and only POSIX tools (cp, mv, rm, mkdir, test), so they
/// also work with BusyBox on OpenWRT and other embedded systems.
/// </summary>
public static class RemoteFileOperations
{
    /// <summary>Returns null for a valid file or folder name, otherwise the reason why it cannot be used.</summary>
    public static string ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
        {
            return "The name cannot be empty.";
        }
        if (name.IndexOf('/') != -1)
        {
            return "The name cannot contain '/'.";
        }
        if (name.IndexOf('\0') != -1)
        {
            return "The name cannot contain a NUL character.";
        }
        if (name == "." || name == "..")
        {
            return "'" + name + "' is a reserved name.";
        }
        return null;
    }

    /// <summary>
    /// A name for a copy that does not clash with <paramref name="existingNames"/>: "report.txt" becomes
    /// "report (copy).txt", then "report (copy 2).txt" and so on. Folders and names without an extension (".bashrc",
    /// "Makefile") get the suffix at the end.
    /// </summary>
    public static string MakeCopyName(string name, bool isDirectory, ICollection<string> existingNames)
    {
        string stem = name;
        string extension = "";
        int dot = name.LastIndexOf('.');
        if (!isDirectory && dot > 0 && dot < name.Length - 1)
        {
            stem = name.Substring(0, dot);
            extension = name.Substring(dot);
        }
        for (int number = 1; ; number++)
        {
            string candidate = stem + (number == 1 ? " (copy)" : " (copy " + number + ")") + extension;
            if (!existingNames.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>True if <paramref name="path"/> is <paramref name="directory"/> itself or lies below it (case-sensitive, as on Unix).</summary>
    public static bool IsSameOrInside(string path, string directory)
    {
        path = TrimTrailingSlashes(path);
        directory = TrimTrailingSlashes(directory);
        if (path == directory)
        {
            return true;
        }
        return path.StartsWith(directory == "/" ? "/" : directory + "/", StringComparison.Ordinal);
    }

    public static string Join(string directory, string name)
    {
        return TrimTrailingSlashes(directory) == "/" ? "/" + name : TrimTrailingSlashes(directory) + "/" + name;
    }

    /// <summary>Copies a file or folder (recursively, keeping modes and times) to the exact path <paramref name="destination"/>.</summary>
    public static string CopyCommand(string source, string destination, bool replace)
    {
        return Prepare(destination, replace) + "cp -R -p " + RemotePathUtils.ShellQuote(source) + " " + RemotePathUtils.ShellQuote(destination);
    }

    /// <summary>Moves (or renames) a file or folder to the exact path <paramref name="destination"/>.</summary>
    public static string MoveCommand(string source, string destination, bool replace)
    {
        return Prepare(destination, replace) + "mv " + RemotePathUtils.ShellQuote(source) + " " + RemotePathUtils.ShellQuote(destination);
    }

    public static string DeleteCommand(IEnumerable<string> paths)
    {
        return "rm -rf " + string.Join(" ", paths.Select(RemotePathUtils.ShellQuote));
    }

    public static string CreateDirectoryCommand(string path)
    {
        return "mkdir " + RemotePathUtils.ShellQuote(path);
    }

    public static string CreateFileCommand(string path)
    {
        return FailIfExists(path) + ": > " + RemotePathUtils.ShellQuote(path);
    }

    // cp and mv do not stop at an existing destination: cp -R would copy a folder into a folder of the same name and
    // mv would silently overwrite a file. The destination is either checked or removed first.
    private static string Prepare(string destination, bool replace)
    {
        return replace ? "rm -rf " + RemotePathUtils.ShellQuote(destination) + " && " : FailIfExists(destination);
    }

    private static string FailIfExists(string path)
    {
        string quoted = RemotePathUtils.ShellQuote(path);
        // -L as well: a dangling symbolic link does not "exist" for -e but would still be overwritten.
        return "if [ -e " + quoted + " ] || [ -L " + quoted + " ]; then echo " + RemotePathUtils.ShellQuote(path + ": already exists") + " >&2; exit 1; fi; ";
    }

    private static string TrimTrailingSlashes(string path)
    {
        string trimmed = (path ?? "").TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }
}
