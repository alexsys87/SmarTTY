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
            int slashIndex = FullPath.TrimEnd('\\').LastIndexOf('\\');
            if (slashIndex == -1)
            {
                return FullPath;
            }
            return FullPath.Substring(slashIndex + 1).TrimEnd('\\');
        }
    }

    public string DisplayPath => StripLongPathPrefix(FullPath);

    private LocalPath(string path)
    {
        this.FullPath = path;
    }

    public static LocalPath FromUserPath(string path)
    {
        return new LocalPath("\\\\?\\" + path.Replace('/', '\\').TrimEnd('\\'));
    }

    public static string StripLongPathPrefix(string path)
    {
        if (path != null && path.StartsWith("\\\\?\\"))
        {
            return path.Substring(4);
        }
        return path;
    }

    public LocalPath Combine(string relativePath)
    {
        if (relativePath.Contains("\\") || relativePath == ".." || relativePath == "." || relativePath.Contains('/'))
        {
            LocalPath normalizedPath = this;
            string[] segments = relativePath.Replace('/', '\\').Split('\\');
            foreach (string part in segments)
            {
                switch (part)
                {
                    case "..":
                        normalizedPath = normalizedPath.GetParent();
                        break;
                    default:
                        normalizedPath = new LocalPath(normalizedPath.FullPath + "\\" + part);
                        break;
                    case "":
                    case ".":
                        break;
                }
            }
            return normalizedPath;
        }
        return new LocalPath(this.FullPath + "\\" + relativePath.Replace('/', '\\'));
    }

    public override string ToString()
    {
        return FullPath;
    }

    public LocalPath GetParent()
    {
        int slashIndex = FullPath.TrimEnd('\\').LastIndexOf('\\');
        if (slashIndex == -1)
        {
            throw new Exception("Cannot get parent path for " + FullPath);
        }
        return new LocalPath(FullPath.Substring(0, slashIndex));
    }

    public static LocalPath FromRaw(string rawPath)
    {
        return new LocalPath(rawPath);
    }
}
