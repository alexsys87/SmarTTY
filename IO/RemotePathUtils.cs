using System;
using System.Collections.Generic;

namespace SmarTTY.IO;

public class RemotePathUtils
{
    public static string GetDirectoryName(string text)
    {
        if (text == null)
        {
            return null;
        }
        int num = text.LastIndexOfAny(new char[2] { '/', '\\' });
        if (num <= 0)
        {
            if (text.Length == 1 || num < 0)
            {
                return "";
            }
            return "/";
        }
        return text.Substring(0, num);
    }

    public static string GetFileName(string text)
    {
        if (text == null)
        {
            return null;
        }
        int num = text.LastIndexOfAny(new char[2] { '/', '\\' });
        if (num < 0)
        {
            return text;
        }
        if (num == text.Length - 1)
        {
            return null;
        }
        return text.Substring(num + 1);
    }

    public static string GetExtension(string text3)
    {
        string text = GetFileName(text3);
        int num = text.LastIndexOf('.');
        if (num == -1)
        {
            return "";
        }
        string text2 = text.Substring(num);
        if (text2.IndexOfAny(new char[2] { '\\', '/' }) != -1)
        {
            return "";
        }
        return text2;
    }

    public static string ChangeExtension(string text, string text2)
    {
        int num = text.LastIndexOf('.');
        if (num == -1)
        {
            return text + text2;
        }
        if (text.Substring(num).IndexOfAny(new char[2] { '\\', '/' }) != -1)
        {
            return text + text2;
        }
        return text.Substring(0, num) + text2;
    }

    public static string NormalizeUnix(string text3)
    {
        if (string.IsNullOrEmpty(text3) || text3.IndexOf('.') == -1)
        {
            return text3;
        }
        int num;
        for (num = 0; num < text3.Length && text3[num] == '/'; num++)
        {
        }
        List<string> list = new List<string>();
        string[] array = text3.Split(new char[1] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string text in array)
        {
            if (text == ".")
            {
                continue;
            }
            if (text == "..")
            {
                if (list.Count > 0 && list[list.Count - 1] != "..")
                {
                    list.RemoveAt(list.Count - 1);
                }
                else
                {
                    list.Add("..");
                }
            }
            else
            {
                list.Add(text);
            }
        }
        string text2 = string.Join("/", list.ToArray());
        while (num > 0)
        {
            text2 = "/" + text2;
            num--;
        }
        return text2;
    }

    public static string NormalizeMixed(string text2)
    {
        if (string.IsNullOrEmpty(text2) || text2.IndexOf('.') == -1)
        {
            return text2;
        }
        int num = 0;
        List<string> list = new List<string>();
        while (num < text2.Length)
        {
            int num2 = text2.IndexOfAny(new char[2] { '\\', '/' }, num + 1);
            if (num2 == -1)
            {
                num2 = text2.Length;
            }
            num = Math.Max(num, 0);
            list.Add(text2.Substring(num, num2 - num));
            num = num2;
        }
        for (int num3 = 0; num3 < list.Count; num3++)
        {
            string text = list[num3].TrimStart('\\', '/');
            if (text == ".")
            {
                list.RemoveAt(num3--);
            }
            else if (text == ".." && num3 > 0)
            {
                list.RemoveRange(num3 - 1, 2);
                num3 -= 2;
            }
        }
        return string.Join("", list.ToArray());
    }

    public static string GetNameWithoutExtension(string text2)
    {
        string text = GetFileName(text2);
        if (text != null)
        {
            int num = text.LastIndexOf('.');
            if (num != -1)
            {
                text = text.Substring(0, num);
            }
        }
        return text;
    }

    public static string GetNameBeforeFirstDot(string text2)
    {
        string text = GetFileName(text2);
        if (text != null)
        {
            int num = text.IndexOf('.');
            if (num != -1)
            {
                text = text.Substring(0, num);
            }
        }
        return text;
    }

    public static string ReplaceFileName(string text, string text2)
    {
        if (text == null)
        {
            return null;
        }
        if (string.IsNullOrEmpty(text2))
        {
            return text;
        }
        int num = text.LastIndexOfAny(new char[2] { '\\', '/' });
        if (num == -1)
        {
            return text2;
        }
        return text.Substring(0, num + 1) + text2;
    }

    public static bool IsUnderDirectory(string text, string text2)
    {
        text = text.Replace('\\', '/');
        text2 = text2.Replace('\\', '/');
        if (!text.EndsWith("/"))
        {
            text += "/";
        }
        if (!text2.EndsWith("/"))
        {
            text2 += "/";
        }
        return text.StartsWith(text2, StringComparison.InvariantCultureIgnoreCase);
    }

    public static bool IsRelative(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        if (text[0] == '.')
        {
            return true;
        }
        if (text[0] == '/' || text[0] == '\\' || (text.Length >= 2 && text[1] == ':'))
        {
            return false;
        }
        return true;
    }

    public static bool IsWindowsRooted(string text)
    {
        if (text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':')
        {
            if (text[2] != '\\')
            {
                return text[2] == '/';
            }
            return true;
        }
        return false;
    }

    public static string GetRelativePath(string text, string text2, bool flag = true)
    {
        if (text == text2)
        {
            return ".";
        }
        text = text.Replace('\\', '/');
        text2 = text2.Replace('\\', '/');
        if (IsRelative(text2))
        {
            return text2;
        }
        char[] separator = new char[2] { '/', '\\' };
        text = NormalizeUnix(text);
        text2 = NormalizeUnix(text2);
        string[] array = text.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        string[] array2 = text2.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        bool ignoreCase = RemotePathUtils.IsWindowsRooted(text2);
        int num;
        for (num = 0; num < Math.Min(array.Length, array2.Length) && string.Compare(array[num], array2[num], ignoreCase) == 0; num++)
        {
        }
        if ((num == 0) & flag)
        {
            return text2;
        }
        string result = "";
        for (int num2 = num; num2 < array.Length; num2++)
        {
            result = Combine(result, "..");
        }
        for (int num3 = num; num3 < array2.Length; num3++)
        {
            result = Combine(result, array2[num3]);
        }
        return result;
    }

    public static string Combine(string text, string text2)
    {
        if (string.IsNullOrEmpty(text2))
        {
            return text;
        }
        if (string.IsNullOrEmpty(text) || !IsRelative(text2))
        {
            return text2;
        }
        if (text2[0] == '/' || (text2.Length >= 2 && text2[1] == ':'))
        {
            return text2;
        }
        return text.TrimEnd('/') + "/" + text2;
    }
}
