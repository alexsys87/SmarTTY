using System;
using System.IO;
using System.Linq;

namespace SmarTTY.LocalRepo;

/// <summary>
/// Facts about an apk feed of an OpenWrt build: a folder with .apk packages and the index "packages.adb" that
/// "make package/index" writes next to them. Unlike the opkg index, the apk index is a binary ADB database that only
/// apk itself can write, so SmarTTY serves the index of the build as is and only reports when it is out of date.
/// </summary>
public static class ApkFeedInfo
{
    public const string IndexName = "packages.adb";

    public static bool HasIndex(string directory)
    {
        return File.Exists(Path.Combine(directory, IndexName));
    }

    /// <summary>True when the directory itself holds at least one .apk file.</summary>
    public static bool HasPackageFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.apk").Any((string file) => file.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// How many .apk files are newer than the index (0 when all are covered or there is no index). Such packages are not in
    /// the index yet, so apk on the device does not see them until "make package/index" has been run.
    /// </summary>
    public static int CountPackagesNewerThanIndex(string directory)
    {
        try
        {
            string index = Path.Combine(directory, IndexName);
            if (!File.Exists(index))
            {
                return 0;
            }
            DateTime indexTime = File.GetLastWriteTimeUtc(index);
            return Directory.GetFiles(directory, "*.apk").Count((string file) => file.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(file) > indexTime);
        }
        catch
        {
            return 0;
        }
    }
}
