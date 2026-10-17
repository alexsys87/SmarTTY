using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SmarTTY.IO;

public class PathUtils
{
    public enum FileNameSanitizeMode
    {
        Basic = 1,
        Parentheses = 2,
        PathSeparators = 4,
        Space = 8,
        Default = 5,
        Extended = 7,
        AlphanumericOnly = 65535
    }

    public static bool IsPathRooted(string path)
    {
        try
        {
            return Path.IsPathRooted(path);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("Invalid path: " + path);
        }
    }

    public static bool? TryIsPathRooted(string path)
    {
        try
        {
            return Path.IsPathRooted(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string StripBasePath(string basePath, string fullPath)
    {
        if (!fullPath.StartsWith(basePath, StringComparison.InvariantCultureIgnoreCase))
        {
            throw new Exception(fullPath + " does not start with " + basePath);
        }
        return fullPath.Substring(basePath.Length).TrimStart('\\', '/');
    }

    public static string GetRelativePath(string basePath, string targetPath, bool useShortPaths = false)
    {
        try
        {
            if (targetPath.Length == 2 && targetPath.EndsWith(":"))
            {
                targetPath += "\\";
            }
            if (!Path.IsPathRooted(targetPath))
            {
                return targetPath;
            }
            char[] separator = new char[2] { '/', '\\' };
            basePath = Path.GetFullPath(basePath);
            targetPath = Path.GetFullPath(targetPath);
            if (useShortPaths)
            {
                basePath = GetShortPath(basePath);
                targetPath = GetShortPath(targetPath);
            }
            string[] baseSegments = basePath.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            string[] targetSegments = targetPath.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            int commonCount;
            for (commonCount = 0; commonCount < Math.Min(baseSegments.Length, targetSegments.Length) && string.Compare(baseSegments[commonCount], targetSegments[commonCount], ignoreCase: true) == 0; commonCount++)
            {
            }
            if (commonCount == 0)
            {
                return Path.GetFullPath(targetPath);
            }
            string text = "";
            for (int i = commonCount; i < baseSegments.Length; i++)
            {
                text = Path.Combine(text, "..");
            }
            for (int j = commonCount; j < targetSegments.Length; j++)
            {
                text = Path.Combine(text, targetSegments[j]);
            }
            if (text == "")
            {
                text = ".";
            }
            return text;
        }
        catch (Exception exception)
        {
            throw new TargetInvocationException("GetRelativePath('" + basePath + "', '" + targetPath + "') failed - " + exception.Message, exception);
        }
    }

    public static string SanitizeFileName(string fileName, FileNameSanitizeMode fileNameSanitizeMode = FileNameSanitizeMode.Default)
    {
        if (fileName == null)
        {
            return "";
        }
        char[] charArray = fileName.ToCharArray();
        if (fileNameSanitizeMode == FileNameSanitizeMode.AlphanumericOnly)
        {
            for (int i = 0; i < charArray.Length; i++)
            {
                if ((charArray[i] < 'a' || charArray[i] > 'z') && (charArray[i] < 'A' || charArray[i] > 'Z') && (charArray[i] < '0' || charArray[i] > '9'))
                {
                    charArray[i] = '_';
                }
            }
        }
        else
        {
            string invalidChars = "<>:| ;&=";
            if ((fileNameSanitizeMode & FileNameSanitizeMode.PathSeparators) == FileNameSanitizeMode.PathSeparators)
            {
                invalidChars += "\\/";
            }
            if ((fileNameSanitizeMode & FileNameSanitizeMode.Parentheses) == FileNameSanitizeMode.Parentheses)
            {
                invalidChars += "()";
            }
            if ((fileNameSanitizeMode & FileNameSanitizeMode.Space) == FileNameSanitizeMode.Space)
            {
                invalidChars += " ";
            }
            for (int i = 0; i < charArray.Length; i++)
            {
                if (invalidChars.IndexOf(charArray[i]) != -1)
                {
                    charArray[i] = '_';
                }
                else if (charArray[i] < ' ' || charArray[i] >= '\u007f')
                {
                    charArray[i] = '_';
                }
            }
        }
        return new string(charArray);
    }

    public static string GetFullPathOrNull(string path)
    {
        try
        {
            if (path == null)
            {
                return null;
            }
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsUnderDirectory(string path, string directoryPath)
    {
        if (path == null || directoryPath == null)
        {
            return false;
        }
        path = path.Replace('/', '\\');
        directoryPath = directoryPath.Replace('/', '\\');
        if (!path.EndsWith("\\"))
        {
            path += "\\";
        }
        if (!directoryPath.EndsWith("\\"))
        {
            directoryPath += "\\";
        }
        return path.StartsWith(directoryPath, StringComparison.InvariantCultureIgnoreCase);
    }

    public static string GetFullPathChecked(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (NotSupportedException)
        {
            throw new NotSupportedException("Unsupported path format: " + path);
        }
    }

    public static string GetExactCasePath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }
            DirectoryInfo directoryInfo = new DirectoryInfo(path);
            if (directoryInfo.Parent != null)
            {
                return Path.Combine(GetExactCasePath(directoryInfo.Parent.FullName), directoryInfo.Parent.GetFileSystemInfos(directoryInfo.Name)[0].Name);
            }
            return directoryInfo.Name.ToUpper();
        }
        catch
        {
            return path;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    protected static extern uint GetShortPathName([MarshalAs(UnmanagedType.LPTStr)] string lpszLongPath, [MarshalAs(UnmanagedType.LPTStr)] StringBuilder lpszShortPath, int cchBuffer);

    public static string GetShortPath(string path)
    {
        if (path == null)
        {
            return null;
        }
        if (path.IndexOf(' ') == -1)
        {
            return path;
        }
        try
        {
            StringBuilder stringBuilder = new StringBuilder(512);
            if (GetShortPathName(path, stringBuilder, stringBuilder.Capacity) != 0)
            {
                return stringBuilder.ToString();
            }
        }
        catch
        {
        }
        return path;
    }

    public static bool IsAbsolutePath(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            return Path.IsPathRooted(path);
        }
        catch
        {
            return false;
        }
    }

    public static string CombineMany(params string[] items)
    {
        if (items.Length == 0)
        {
            throw new ArgumentException("No path components provided");
        }
        string text = items[0];
        foreach (string item in items.Skip(1))
        {
            if (!item.IsNullOrEmpty())
            {
                text = Path.Combine(text, item);
            }
        }
        return text;
    }

    public static string GetFileName(string path)
    {
        if (path == null)
        {
            return null;
        }
        int separatorIndex = path.LastIndexOfAny(new char[2] { '\\', '/' });
        if (separatorIndex == -1)
        {
            return path;
        }
        return path.Substring(separatorIndex + 1);
    }

    public static string GetDirectoryName(string path)
    {
        if (path == null)
        {
            return null;
        }
        int separatorIndex = path.LastIndexOfAny(new char[2] { '\\', '/' });
        if (separatorIndex == -1)
        {
            return "";
        }
        return path.Substring(0, separatorIndex).TrimEnd('\\', '/');
    }

    public static bool PathsEqual(string firstPath, string secondPath)
    {
        if (string.IsNullOrEmpty(firstPath) || string.IsNullOrEmpty(secondPath))
        {
            return string.IsNullOrEmpty(firstPath) == string.IsNullOrEmpty(secondPath);
        }
        try
        {
            return StringComparer.InvariantCultureIgnoreCase.Compare(GetFullPathChecked(firstPath), GetFullPathChecked(secondPath)) == 0;
        }
        catch
        {
            return StringComparer.InvariantCultureIgnoreCase.Compare(firstPath, secondPath) == 0;
        }
    }

    public static string GetCommonDirectory(IEnumerable<string> paths)
    {
        string commonPrefix = null;
        foreach (string path in paths)
        {
            if (!path.IsNullOrEmpty())
            {
                if (commonPrefix == null)
                {
                    commonPrefix = path;
                }
                int commonLen = StringUtils.CommonPrefixLength(commonPrefix, path);
                if (commonLen < 0)
                {
                    return null;
                }
                commonPrefix = commonPrefix.Substring(0, commonLen);
            }
        }
        if (!string.IsNullOrEmpty(commonPrefix))
        {
            int separatorIndex = commonPrefix.LastIndexOfAny(new char[2] { '/', '\\' });
            if (separatorIndex != -1)
            {
                return commonPrefix.Substring(0, separatorIndex);
            }
        }
        return null;
    }

    public static string CombineWithDirectoryOf(string sourceFilePath, string relativeFileName)
    {
        return Path.Combine(Path.GetDirectoryName(sourceFilePath), relativeFileName);
    }
}
