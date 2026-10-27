using System;
using System.Collections.Generic;

namespace SmarTTY.IO;

public class RemotePathUtils
{
    /// <summary>
    /// Quotes a value so that a POSIX shell reads it as one literal word. Inside double quotes the shell still expands
    /// $name, `command` and backslash sequences, so a file called Foo$Bar would silently become another file; inside
    /// single quotes nothing is special except the quote itself.
    /// </summary>
    public static string ShellQuote(string value)
    {
        return "'" + (value ?? "").Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// Escapes text for use inside a double quoted word of a POSIX shell. Backslash, dollar, backtick and the double
    /// quote are the characters that keep their meaning there; the backslash has to be escaped first.
    /// </summary>
    public static string EscapeForDoubleQuotes(string text)
    {
        if (text == null)
        {
            return "";
        }
        return text.Replace("\\", "\\\\").Replace("$", "\\$").Replace("`", "\\`").Replace("\"", "\\\"");
    }

    public static string GetDirectoryName(string path)
    {
        if (path == null)
        {
            return null;
        }
        int separatorIndex = path.LastIndexOfAny(new char[2] { '/', '\\' });
        if (separatorIndex <= 0)
        {
            if (path.Length == 1 || separatorIndex < 0)
            {
                return "";
            }
            return "/";
        }
        return path.Substring(0, separatorIndex);
    }

    public static string GetFileName(string path)
    {
        if (path == null)
        {
            return null;
        }
        int separatorIndex = path.LastIndexOfAny(new char[2] { '/', '\\' });
        if (separatorIndex < 0)
        {
            return path;
        }
        if (separatorIndex == path.Length - 1)
        {
            return null;
        }
        return path.Substring(separatorIndex + 1);
    }

    public static string GetExtension(string path)
    {
        string fileName = GetFileName(path);
        if (fileName == null)
        {
            // A path ending with a separator (a directory) has no file name and no extension.
            return "";
        }
        int dotIndex = fileName.LastIndexOf('.');
        if (dotIndex == -1)
        {
            return "";
        }
        string ext = fileName.Substring(dotIndex);
        if (ext.IndexOfAny(new char[2] { '\\', '/' }) != -1)
        {
            return "";
        }
        return ext;
    }

    public static string NormalizeUnix(string path)
    {
        if (string.IsNullOrEmpty(path) || path.IndexOf('.') == -1)
        {
            return path;
        }
        int leadingSlashesCount;
        for (leadingSlashesCount = 0; leadingSlashesCount < path.Length && path[leadingSlashesCount] == '/'; leadingSlashesCount++)
        {
        }
        List<string> segments = new List<string>();
        string[] rawSegments = path.Split(new char[1] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in rawSegments)
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count > 0 && segments[segments.Count - 1] != "..")
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                else if (leadingSlashesCount == 0)
                {
                    segments.Add("..");
                }
                // else: the parent of the root directory is the root directory itself, "/.." is "/".
            }
            else
            {
                segments.Add(segment);
            }
        }
        string normalizedPath = string.Join("/", segments.ToArray());
        while (leadingSlashesCount > 0)
        {
            normalizedPath = "/" + normalizedPath;
            leadingSlashesCount--;
        }
        return normalizedPath;
    }

    public static string NormalizeMixed(string path)
    {
        if (string.IsNullOrEmpty(path) || path.IndexOf('.') == -1)
        {
            return path;
        }
        int index = 0;
        List<string> segments = new List<string>();
        while (index < path.Length)
        {
            int nextSeparator = path.IndexOfAny(new char[2] { '\\', '/' }, index + 1);
            if (nextSeparator == -1)
            {
                nextSeparator = path.Length;
            }
            index = Math.Max(index, 0);
            segments.Add(path.Substring(index, nextSeparator - index));
            index = nextSeparator;
        }
        for (int i = 0; i < segments.Count; i++)
        {
            string segment = segments[i].TrimStart('\\', '/');
            if (segment == ".")
            {
                segments.RemoveAt(i--);
            }
            else if (segment == ".." && i > 0 && segments[i - 1].TrimStart('\\', '/') != "..")
            {
                segments.RemoveRange(i - 1, 2);
                i -= 2;
            }
        }
        string result = string.Join("", segments.ToArray());
        // Every segment carries its own leading separator. If the first segment of a relative path was removed, the next
        // one must not turn the path into an absolute one.
        if (path[0] != '\\' && path[0] != '/' && result.Length > 0 && (result[0] == '\\' || result[0] == '/'))
        {
            result = result.Substring(1);
        }
        return result;
    }

    public static bool IsUnderDirectory(string path, string directoryPath)
    {
        path = path.Replace('\\', '/');
        directoryPath = directoryPath.Replace('\\', '/');
        if (!path.EndsWith("/"))
        {
            path += "/";
        }
        if (!directoryPath.EndsWith("/"))
        {
            directoryPath += "/";
        }
        return path.StartsWith(directoryPath, StringComparison.InvariantCultureIgnoreCase);
    }

    public static bool IsRelative(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        if (path[0] == '.')
        {
            return true;
        }
        if (path[0] == '/' || path[0] == '\\' || (path.Length >= 2 && path[1] == ':'))
        {
            return false;
        }
        return true;
    }

    public static bool IsWindowsRooted(string path)
    {
        if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':')
        {
            if (path[2] != '\\')
            {
                return path[2] == '/';
            }
            return true;
        }
        return false;
    }

    public static string GetRelativePath(string basePath, string targetPath, bool allowAbsoluteFallback = true)
    {
        if (basePath == targetPath)
        {
            return ".";
        }
        basePath = basePath.Replace('\\', '/');
        targetPath = targetPath.Replace('\\', '/');
        if (IsRelative(targetPath))
        {
            return targetPath;
        }
        char[] separator = new char[2] { '/', '\\' };
        basePath = NormalizeUnix(basePath);
        targetPath = NormalizeUnix(targetPath);
        string[] baseSegments = basePath.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        string[] targetSegments = targetPath.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        bool ignoreCase = RemotePathUtils.IsWindowsRooted(targetPath);
        int commonCount;
        for (commonCount = 0; commonCount < Math.Min(baseSegments.Length, targetSegments.Length) && string.Compare(baseSegments[commonCount], targetSegments[commonCount], ignoreCase) == 0; commonCount++)
        {
        }
        if ((commonCount == 0) & allowAbsoluteFallback)
        {
            return targetPath;
        }
        string text = "";
        for (int i = commonCount; i < baseSegments.Length; i++)
        {
            text = Combine(text, "..");
        }
        for (int j = commonCount; j < targetSegments.Length; j++)
        {
            text = Combine(text, targetSegments[j]);
        }
        // Both paths are the same place: an empty string would be taken for "no path", for instance as an empty link target.
        return text.Length == 0 ? "." : text;
    }

    public static string Combine(string basePath, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return basePath;
        }
        if (string.IsNullOrEmpty(basePath) || !IsRelative(relativePath))
        {
            return relativePath;
        }
        if (relativePath[0] == '/' || (relativePath.Length >= 2 && relativePath[1] == ':'))
        {
            return relativePath;
        }
        return basePath.TrimEnd('/') + "/" + relativePath;
    }
}
