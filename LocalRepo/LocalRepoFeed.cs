using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SmarTTY.LocalRepo;

/// <summary>
/// A directory of an OpenWrt build that holds a feed: for opkg a "Packages" index and/or .ipk files, for apk a "packages.adb"
/// index and/or .apk files.
/// </summary>
public sealed class LocalRepoFeed
{
    /// <summary>The feed name used in the opkg configuration ("src/gz NAME URL").</summary>
    public string Name { get; set; }

    public string Directory { get; set; }

    /// <summary>The folder is an opkg feed (it has a Packages index or .ipk files).</summary>
    public bool HasOpkg { get; set; }

    /// <summary>The folder is an apk feed (it has packages.adb or .apk files).</summary>
    public bool HasApk { get; set; }

    /// <summary>True if the device with the given package manager can use this feed.</summary>
    public bool IsFor(LocalRepoPackageManager manager)
    {
        return manager == LocalRepoPackageManager.Apk ? HasApk : HasOpkg;
    }

    /// <summary>The path of the feed on the local server, e.g. "/r0/base"; the URL of the feed is the server address plus this path.</summary>
    public string UrlPath { get; set; }
}

/// <summary>Finds the feeds in the folders chosen by the user, e.g. bin/packages/arm_cortex-a9_neon with its base, packages and luci subfolders.</summary>
public static class LocalRepoFeedScanner
{
    private const int MaxDepth = 5;

    /// <summary>The URL prefix under which the root folder with the given index is served.</summary>
    public static string RootPrefix(int rootIndex)
    {
        return "/r" + rootIndex;
    }

    public static bool IsOpkgFeedDirectory(string directory)
    {
        return File.Exists(Path.Combine(directory, "Packages")) || File.Exists(Path.Combine(directory, "Packages.gz")) || IpkIndexBuilder.HasPackageFiles(directory);
    }

    public static bool IsApkFeedDirectory(string directory)
    {
        return ApkFeedInfo.HasIndex(directory) || ApkFeedInfo.HasPackageFiles(directory);
    }

    public static bool IsFeedDirectory(string directory)
    {
        return IsOpkgFeedDirectory(directory) || IsApkFeedDirectory(directory);
    }

    public static List<LocalRepoFeed> Scan(IList<string> roots)
    {
        List<LocalRepoFeed> feeds = new List<LocalRepoFeed>();
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < roots.Count; i++)
        {
            string root = roots[i];
            if (!string.IsNullOrWhiteSpace(root) && System.IO.Directory.Exists(root))
            {
                Visit(root, root, i, 0, feeds, names);
            }
        }
        return feeds;
    }

    private static void Visit(string root, string directory, int rootIndex, int depth, List<LocalRepoFeed> feeds, HashSet<string> names)
    {
        if (IsFeedDirectory(directory))
        {
            feeds.Add(CreateFeed(root, directory, rootIndex, names));
            return;
        }
        if (depth >= MaxDepth)
        {
            return;
        }
        string[] subdirectories;
        try
        {
            subdirectories = System.IO.Directory.GetDirectories(directory);
        }
        catch
        {
            return;
        }
        Array.Sort(subdirectories, StringComparer.OrdinalIgnoreCase);
        foreach (string subdirectory in subdirectories)
        {
            if (!Path.GetFileName(subdirectory).StartsWith(".", StringComparison.Ordinal))
            {
                Visit(root, subdirectory, rootIndex, depth + 1, feeds, names);
            }
        }
    }

    private static LocalRepoFeed CreateFeed(string root, string directory, int rootIndex, HashSet<string> names)
    {
        string trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string relative = directory.Length > trimmedRoot.Length ? directory.Substring(trimmedRoot.Length).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : "";
        string[] segments = relative.Length == 0 ? new string[0] : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        StringBuilder urlPath = new StringBuilder(RootPrefix(rootIndex));
        foreach (string segment in segments)
        {
            urlPath.Append('/').Append(Uri.EscapeDataString(segment));
        }

        string rootName = Path.GetFileName(trimmedRoot);
        if (rootName.Length == 0)
        {
            rootName = "repo" + rootIndex;
        }
        string baseName = "smartty_" + Sanitize(string.Join("_", new[] { rootName }.Concat(segments).ToArray()));
        string name = baseName;
        for (int suffix = 2; !names.Add(name); suffix++)
        {
            name = baseName + "_" + suffix;
        }
        return new LocalRepoFeed
        {
            Name = name,
            Directory = directory,
            UrlPath = urlPath.ToString(),
            HasOpkg = IsOpkgFeedDirectory(directory),
            HasApk = IsApkFeedDirectory(directory)
        };
    }

    private static string Sanitize(string text)
    {
        StringBuilder result = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            bool allowed = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_';
            result.Append(allowed ? ch : '_');
        }
        return result.ToString();
    }
}
