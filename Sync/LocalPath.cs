using System;
using System.Linq;

namespace SmarTTY.Sync;

public struct LocalPath
{
    public readonly string FullPath;

    public string FileName
    {
        get
        {
            int num = FullPath.TrimEnd('\\').LastIndexOf('\\');
            if (num == -1)
            {
                return FullPath;
            }
            return FullPath.Substring(num + 1).TrimEnd('\\');
        }
    }

    public string DisplayPath => StripLongPathPrefix(FullPath);

    private LocalPath(string text)
    {
        this.FullPath = text;
    }

    public static LocalPath FromUserPath(string text)
    {
        return new LocalPath("\\\\?\\" + text.Replace('/', '\\').TrimEnd('\\'));
    }

    public static string StripLongPathPrefix(string text)
    {
        if (text != null && text.StartsWith("\\\\?\\"))
        {
            return text.Substring(4);
        }
        return text;
    }

    public LocalPath Combine(string text2)
    {
        if (text2.Contains("\\") || text2 == ".." || text2 == "." || text2.Contains('/'))
        {
            LocalPath result = this;
            string[] array = text2.Replace('/', '\\').Split('\\');
            foreach (string text in array)
            {
                switch (text)
                {
                    case "..":
                        result = result.GetParent();
                        break;
                    default:
                        result = new LocalPath(result.FullPath + "\\" + text);
                        break;
                    case "":
                    case ".":
                        break;
                }
            }
            return result;
        }
        return new LocalPath(this.FullPath + "\\" + text2.Replace('/', '\\'));
    }

    public override string ToString()
    {
        return FullPath;
    }

    public LocalPath GetParent()
    {
        int num = FullPath.TrimEnd('\\').LastIndexOf('\\');
        if (num == -1)
        {
            throw new Exception("Cannot get parent path for " + FullPath);
        }
        return new LocalPath(FullPath.Substring(0, num));
    }

    public static LocalPath FromRaw(string text)
    {
        return new LocalPath(text);
    }
}
