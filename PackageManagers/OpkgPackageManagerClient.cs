using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

public class OpkgPackageManagerClient : ILinuxPackageManagerClient
{
    public int Probe(ISSHCommandRunner runner)
    {
        if (runner.RunCommand("which opkg") != null || runner.RunCommand("opkg -v") != null)
        {
            return 1000;
        }
        return 0;
    }

    public string GetCommandLineForIndexUpdate()
    {
        return "opkg update";
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions = false)
    {
        // "opkg list-installed" outputs "package_name - version"
        string[] installedLines = runner.RunCommand("opkg list-installed") ?? throw new Exception("Failed to list packages with opkg");
        Dictionary<string, LinuxPackageInfo> packages = new Dictionary<string, LinuxPackageInfo>(StringComparer.Ordinal);
        foreach (string line in installedLines)
        {
            string[] fields = SplitLine(line);
            if (fields != null && !packages.ContainsKey(fields[0]))
            {
                packages.Add(fields[0], new LinuxPackageInfo
                {
                    ID = fields[0],
                    Version = fields[1],
                    State = LinuxPackageInfo.PackageState.Installed
                });
            }
        }

        // "opkg list" outputs "package_name - version - description"; it is empty until "opkg update" has been run
        // after a reboot (OpenWrt keeps the lists in RAM), and a package found in several feeds is listed once per feed.
        foreach (string line in runner.RunCommand("opkg list") ?? new string[0])
        {
            string[] fields = SplitLine(line);
            if (fields != null && !packages.ContainsKey(fields[0]))
            {
                packages.Add(fields[0], new LinuxPackageInfo
                {
                    ID = fields[0],
                    Version = fields[1],
                    State = LinuxPackageInfo.PackageState.Available
                });
            }
        }

        // "opkg list-upgradable" outputs "package_name - installed_version - new_version".
        foreach (string line in runner.RunCommand("opkg list-upgradable") ?? new string[0])
        {
            string[] fields = SplitLine(line);
            if (fields == null || fields.Length < 3 || fields[2].Length == 0)
            {
                continue;
            }
            if (packages.TryGetValue(fields[0], out LinuxPackageInfo packageInfo) && packageInfo.State == LinuxPackageInfo.PackageState.Installed)
            {
                packageInfo.RawState = "upgradable from: " + packageInfo.Version;
                packageInfo.State = LinuxPackageInfo.PackageState.Upgradeable;
                packageInfo.Version = fields[2];
            }
        }

        List<LinuxPackageInfo> result = packages.Values.ToList();
        result.Sort((LinuxPackageInfo first, LinuxPackageInfo second) => string.Compare(first.ID, second.ID, StringComparison.OrdinalIgnoreCase));
        return result.ToArray();
    }

    /// <summary>
    /// Splits an "opkg list" style line into the package name, the version and (when present) the third field:
    /// the description or the new version. Returns null for the lines that are not package rows.
    /// </summary>
    private static string[] SplitLine(string line)
    {
        int dashIndex = line.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIndex <= 0 || char.IsWhiteSpace(line[0]))
        {
            return null;
        }
        string name = line.Substring(0, dashIndex).Trim();
        string rest = line.Substring(dashIndex + 3);
        int secondDashIndex = rest.IndexOf(" - ", StringComparison.Ordinal);
        if (secondDashIndex < 0)
        {
            return new[] { name, rest.Trim() };
        }
        return new[] { name, rest.Substring(0, secondDashIndex).Trim(), rest.Substring(secondDashIndex + 3).Trim() };
    }

    public string GetCommandLineForPackageOperation(LinuxPackageInfo[] packages, LinuxPackageInfo.PackageAction action, PackageCommitOptions options)
    {
        if (packages == null || packages.Length == 0)
        {
            return null;
        }
        string actionName = action switch
        {
            LinuxPackageInfo.PackageAction.Install => "install",
            LinuxPackageInfo.PackageAction.Remove => "remove",
            _ => null
        };
        if (actionName == null)
        {
            return null;
        }
        string packageNames = string.Join(" ", packages.Select(p => p.ID));
        return "opkg " + actionName + " " + packageNames;
    }
}
