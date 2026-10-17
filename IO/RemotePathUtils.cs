using System;
using System.Collections.Generic;

namespace SmarTTY.IO;

public class RemotePathUtils
{
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

    public static string ChangeExtension(string path, string newExtension)
    {
        int dotIndex = path.LastIndexOf('.');
        if (dotIndex == -1)
        {
            return path + newExtension;
        }
        if (path.Substring(dotIndex).IndexOfAny(new char[2] { '\\', '/' }) != -1)
        {
            return path + newExtension;
        }
        return path.Substring(0, dotIndex) + newExtension;
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
                else
                {
                    segments.Add("..");
                }
            }
            else
            {
                segments.Add(segment);
            }
        }
        string result = string.Join("/", segments.ToArray());
        while (leadingSlashesCount > 0)
        {
            result = "/" + result;
            leadingSlashesCount--;
        }
        return result;
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
            else if (segment == ".." && i > 0)
            {
                segments.RemoveRange(i - 1, 2);
                i -= 2;
            }
        }
        return string.Join("", segments.ToArray());
    }

    public static string GetNameWithoutExtension(string path)
    {
        string fileName = GetFileName(path);
        if (fileName != null)
        {
            int dotIndex = fileName.LastIndexOf('.');
            if (dotIndex != -1)
            {
                fileName = fileName.Substring(0, dotIndex);
            }
        }
        return fileName;
    }

    public static string GetNameBeforeFirstDot(string path)
    {
        string fileName = GetFileName(path);
        if (fileName != null)
        {
            int dotIndex = fileName.IndexOf('.');
            if (dotIndex != -1)
            {
                fileName = fileName.Substring(0, dotIndex);
            }
        }
        return fileName;
    }

    public static string ReplaceFileName(string path, string newFileName)
    {
        if (path == null)
        {
            return null;
        }
        if (string.IsNullOrEmpty(newFileName))
        {
            return path;
        }
        int separatorIndex = path.LastIndexOfAny(new char[2] { '\\', '/' });
        if (separatorIndex == -1)
        {
            return newFileName;
        }
        return path.Substring(0, separatorIndex + 1) + newFileName;
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
        string result = "";
        for (int i = commonCount; i < baseSegments.Length; i++)
        {
            result = Combine(result, "..");
        }
        for (int j = commonCount; j < targetSegments.Length; j++)
        {
            result = Combine(result, targetSegments[j]);
        }
        return result;
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
