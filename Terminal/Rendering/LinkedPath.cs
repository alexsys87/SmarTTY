using SmarTTY.IO;
using System;

namespace SmarTTY.Terminal.Rendering;

/// <summary>Where a file link of the output points: what a terminal tab does with the path a link scanner found before it opens it.</summary>
public static class LinkedPath
{
    /// <summary>
    /// An absolute path as it is; a relative one is put under the directory <paramref name="directoryFallback"/> gives (the directory the shell
    /// is in now, read from the title or the prompt, for a name relative to a directory the shell never reported). A fallback that throws
    /// counts as none.
    /// </summary>
    public static string Resolve(string path, Func<string> directoryFallback)
    {
        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            return path;
        }
        string directory = null;
        try
        {
            directory = directoryFallback?.Invoke();
        }
        catch (Exception)
        {
        }
        return RemotePathUtils.Combine(directory, path);
    }
}
