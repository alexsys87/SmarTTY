using SmarTTY.Utilities;
using System;

namespace SmarTTY.Sync;

internal struct RelativeRemotePath(string relativePath)
{
    public readonly string Path = relativePath;

    public RelativeRemotePath Combine(string childPath)
    {
        string text = Path;
        if (text.IsNonTrivial())
        {
            text += "/";
        }
        text += childPath;
        return new RelativeRemotePath(text);
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
