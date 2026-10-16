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

    public static bool IsPathRooted(string text)
    {
        try
        {
            return Path.IsPathRooted(text);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("Invalid path: " + text);
        }
    }

    public static bool? TryIsPathRooted(string text)
    {
        try
        {
            return Path.IsPathRooted(text);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string StripBasePath(string text, string text2)
    {
        if (!text2.StartsWith(text, StringComparison.InvariantCultureIgnoreCase))
        {
            throw new Exception(text2 + " does not start with " + text);
        }
        return text2.Substring(text.Length).TrimStart('\\', '/');
    }

    public static string GetRelativePath(string text2, string text3, bool flag = false)
    {
        try
        {
            if (text3.Length == 2 && text3.EndsWith(":"))
            {
                text3 += "\\";
            }
            if (!Path.IsPathRooted(text3))
            {
                return text3;
            }
            char[] separator = new char[2] { '/', '\\' };
            text2 = Path.GetFullPath(text2);
            text3 = Path.GetFullPath(text3);
            if (flag)
            {
                text2 = GetShortPath(text2);
                text3 = GetShortPath(text3);
            }
            string[] array = text2.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            string[] array2 = text3.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            int num;
            for (num = 0; num < Math.Min(array.Length, array2.Length) && string.Compare(array[num], array2[num], ignoreCase: true) == 0; num++)
            {
            }
            if (num == 0)
            {
                return Path.GetFullPath(text3);
            }
            string text = "";
            for (int num2 = num; num2 < array.Length; num2++)
            {
                text = Path.Combine(text, "..");
            }
            for (int num3 = num; num3 < array2.Length; num3++)
            {
                text = Path.Combine(text, array2[num3]);
            }
            if (text == "")
            {
                text = ".";
            }
            return text;
        }
        catch (Exception ex)
        {
            throw new TargetInvocationException("GetRelativePath('" + text2 + "', '" + text3 + "') failed - " + ex.Message, ex);
        }
    }

    public static string SanitizeFileName(string text2, FileNameSanitizeMode fileNameSanitizeMode = FileNameSanitizeMode.Default)
    {
        if (text2 == null)
        {
            return "";
        }
        char[] array = text2.ToCharArray();
        if (fileNameSanitizeMode == FileNameSanitizeMode.AlphanumericOnly)
        {
            for (int num = 0; num < array.Length; num++)
            {
                if ((array[num] < 'a' || array[num] > 'z') && (array[num] < 'A' || array[num] > 'Z') && (array[num] < '0' || array[num] > '9'))
                {
                    array[num] = '_';
                }
            }
        }
        else
        {
            string text = "<>:| ;&=";
            if ((fileNameSanitizeMode & FileNameSanitizeMode.PathSeparators) == FileNameSanitizeMode.PathSeparators)
            {
                text += "\\/";
            }
            if ((fileNameSanitizeMode & FileNameSanitizeMode.Parentheses) == FileNameSanitizeMode.Parentheses)
            {
                text += "()";
            }
            if ((fileNameSanitizeMode & FileNameSanitizeMode.Space) == FileNameSanitizeMode.Space)
            {
                text += " ";
            }
            for (int num2 = 0; num2 < array.Length; num2++)
            {
                if (text.IndexOf(array[num2]) != -1)
                {
                    array[num2] = '_';
                }
                else if (array[num2] < ' ' || array[num2] >= '\u007f')
                {
                    array[num2] = '_';
                }
            }
        }
        return new string(array);
    }

    public static string GetFullPathOrNull(string text)
    {
        try
        {
            if (text == null)
            {
                return null;
            }
            return Path.GetFullPath(text);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsUnderDirectory(string text, string text2)
    {
        if (text == null || text2 == null)
        {
            return false;
        }
        text = text.Replace('/', '\\');
        text2 = text2.Replace('/', '\\');
        if (!text.EndsWith("\\"))
        {
            text += "\\";
        }
        if (!text2.EndsWith("\\"))
        {
            text2 += "\\";
        }
        return text.StartsWith(text2, StringComparison.InvariantCultureIgnoreCase);
    }

    public static string GetFullPathChecked(string text)
    {
        try
        {
            return Path.GetFullPath(text);
        }
        catch (NotSupportedException)
        {
            throw new NotSupportedException("Unsupported path format: " + text);
        }
    }

    public static string GetExactCasePath(string text)
    {
        try
        {
            if (!File.Exists(text) && !Directory.Exists(text))
            {
                return text;
            }
            DirectoryInfo directoryInfo = new DirectoryInfo(text);
            if (directoryInfo.Parent != null)
            {
                return Path.Combine(GetExactCasePath(directoryInfo.Parent.FullName), directoryInfo.Parent.GetFileSystemInfos(directoryInfo.Name)[0].Name);
            }
            return directoryInfo.Name.ToUpper();
        }
        catch
        {
            return text;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    protected static extern uint GetShortPathName([MarshalAs(UnmanagedType.LPTStr)] string text, [MarshalAs(UnmanagedType.LPTStr)] StringBuilder stringBuilder, int value2);

    public static string GetShortPath(string text)
    {
        if (text == null)
        {
            return null;
        }
        if (text.IndexOf(' ') == -1)
        {
            return text;
        }
        try
        {
            StringBuilder stringBuilder = new StringBuilder(512);
            if (GetShortPathName(text, stringBuilder, stringBuilder.Capacity) != 0)
            {
                return stringBuilder.ToString();
            }
        }
        catch
        {
        }
        return text;
    }

    public static bool IsAbsolutePath(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            return Path.IsPathRooted(text);
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

    public static string GetFileName(string text)
    {
        if (text == null)
        {
            return null;
        }
        int num = text.LastIndexOfAny(new char[2] { '\\', '/' });
        if (num == -1)
        {
            return text;
        }
        return text.Substring(num + 1);
    }

    public static string GetDirectoryName(string text)
    {
        if (text == null)
        {
            return null;
        }
        int num = text.LastIndexOfAny(new char[2] { '\\', '/' });
        if (num == -1)
        {
            return "";
        }
        return text.Substring(0, num).TrimEnd('\\', '/');
    }

    public static bool PathsEqual(string text, string text2)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(text2))
        {
            return string.IsNullOrEmpty(text) == string.IsNullOrEmpty(text2);
        }
        try
        {
            return StringComparer.InvariantCultureIgnoreCase.Compare(GetFullPathChecked(text), GetFullPathChecked(text2)) == 0;
        }
        catch
        {
            return StringComparer.InvariantCultureIgnoreCase.Compare(text, text2) == 0;
        }
    }

    public static string GetCommonDirectory(IEnumerable<string> items)
    {
        string text = null;
        foreach (string item in items)
        {
            if (!item.IsNullOrEmpty())
            {
                if (text == null)
                {
                    text = item;
                }
                int num = StringUtils.CommonPrefixLength(text, item);
                if (num < 0)
                {
                    return null;
                }
                text = text.Substring(0, num);
            }
        }
        if (!string.IsNullOrEmpty(text))
        {
            int num2 = text.LastIndexOfAny(new char[2] { '/', '\\' });
            if (num2 != -1)
            {
                return text.Substring(0, num2);
            }
        }
        return null;
    }

    public static string CombineWithDirectoryOf(string text, string text2)
    {
        return Path.Combine(Path.GetDirectoryName(text), text2);
    }
}
