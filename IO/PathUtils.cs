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
}
