using SmarTTY.LocalRepo;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SmarTTY.IO;

/// <summary>
/// Installing package files that are already on an OpenWrt device (the file list's "Install package" items): which files
/// are packages, which of them the device's package manager can install, and the command that installs them. opkg installs
/// .ipk files, apk (OpenWrt 25.12 and newer) installs .apk files. Pure logic without SSH, tested by tests/RemoteFileOperationsTests.
/// </summary>
public static class RemotePackageInstall
{
    /// <summary>True for an opkg package file name (.ipk, case-insensitive).</summary>
    public static bool IsIpk(string name)
    {
        return name != null && name.EndsWith(".ipk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for an apk package file name (.apk, case-insensitive).</summary>
    public static bool IsApk(string name)
    {
        return name != null && name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a .ipk or .apk file name; such files get the package icon in the file list.</summary>
    public static bool IsPackageFile(string name)
    {
        return IsIpk(name) || IsApk(name);
    }

    /// <summary>The package manager that installs the file (opkg for .ipk, apk for .apk), or null if it is not a package file.</summary>
    public static LocalRepoPackageManager? ManagerFor(string name)
    {
        if (IsIpk(name))
        {
            return LocalRepoPackageManager.Opkg;
        }
        return IsApk(name) ? LocalRepoPackageManager.Apk : (LocalRepoPackageManager?)null;
    }

    /// <summary>
    /// True if the package file can be installed on a device whose package manager is <paramref name="manager"/> (null: the
    /// host is not OpenWrt, so nothing is offered): an .ipk needs opkg and an .apk needs apk.
    /// </summary>
    public static bool CanInstall(string name, LocalRepoPackageManager? manager)
    {
        return manager != null && ManagerFor(name) == manager;
    }

    /// <summary>The name of the package manager's command, for messages.</summary>
    public static string ToolName(LocalRepoPackageManager manager)
    {
        return manager == LocalRepoPackageManager.Apk ? "apk" : "opkg";
    }

    /// <summary>
    /// The command that installs the package files (absolute paths on the device) with the package manager, in one run so that
    /// packages that depend on each other are resolved together. apk gets --allow-untrusted: a package file that was built
    /// locally is not signed with a key from /etc/apk/keys, and apk refuses such a file otherwise. A path that is not absolute
    /// gets a "./" in front, because apk reads a name without a slash as a package name of a repository, not as a file.
    /// </summary>
    public static string BuildInstallCommand(LocalRepoPackageManager manager, IEnumerable<string> paths)
    {
        string[] files = (paths ?? Enumerable.Empty<string>()).Where((string path) => !string.IsNullOrEmpty(path)).Select((string path) => path.StartsWith("/") || path.StartsWith("./") ? path : "./" + path).ToArray();
        if (files.Length == 0)
        {
            throw new ArgumentException("No package files to install.", nameof(paths));
        }
        string quoted = string.Join(" ", files.Select(RemotePathUtils.ShellQuote));
        return manager == LocalRepoPackageManager.Apk ? "apk add --allow-untrusted " + quoted : "opkg install " + quoted;
    }
}
