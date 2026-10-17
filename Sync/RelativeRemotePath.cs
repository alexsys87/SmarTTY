using SmarTTY.Utilities;
using System;

namespace SmarTTY.Sync;

internal struct RelativeRemotePath(string relativePath)
{
    public readonly string Path = relativePath;

    public RelativeRemotePath Combine(string childPath)
    {
        string result = Path;
        if (result.IsNonTrivial())
        {
            result += "/";
        }
        result += childPath;
        return new RelativeRemotePath(result);
    }

    public RelativeRemotePath GetParent()
    {
        int slashIndex = Path.TrimEnd('/').LastIndexOf('/');
        if (slashIndex == -1)
        {
            throw new Exception("Cannot get parent path for " + Path);
        }
        return new RelativeRemotePath(Path.Substring(0, slashIndex));
    }

    public string PrependBase(string basePath)
    {
        if (Path.IsNonTrivial())
        {
            return basePath + "/" + Path;
        }
        return basePath;
    }

    public override string ToString()
    {
        return Path;
    }
}
