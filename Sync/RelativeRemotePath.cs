using SmarTTY.Utilities;
using System;

namespace SmarTTY.Sync;

internal struct RelativeRemotePath(string text2)
{
    public readonly string Path = text2;

    public RelativeRemotePath Combine(string text2)
    {
        string text = Path;
        if (text.IsNonTrivial())
        {
            text += "/";
        }
        text += text2;
        return new RelativeRemotePath(text);
    }

    public RelativeRemotePath GetParent()
    {
        int num = Path.TrimEnd('/').LastIndexOf('/');
        if (num == -1)
        {
            throw new Exception("Cannot get parent path for " + Path);
        }
        return new RelativeRemotePath(Path.Substring(0, num));
    }

    public string PrependBase(string text)
    {
        if (Path.IsNonTrivial())
        {
            return text + "/" + Path;
        }
        return text;
    }

    public override string ToString()
    {
        return Path;
    }
}
