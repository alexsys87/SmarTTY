using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

/// <summary>
/// Package manager client for dnf: Fedora, RHEL 8+ (CentOS Stream, Rocky Linux, AlmaLinux), Amazon Linux 2023.
/// On these systems "yum" is only a symlink to dnf, which is why the probe score is higher than the yum client's one.
/// </summary>
public class DnfPackageManagerClient : ILinuxPackageManagerClient
{
    // "dnf list" line: name.arch version repository ("@System" for the installed packages).
    // A package id always carries the architecture suffix, which is also what keeps the header rows from matching.
    private static readonly Regex ListLineRegex = new Regex(
        @"^(?<id>[^ \t]+\.[^ \t]+)[ \t]+(?<version>[^ \t]+)(?:[ \t]+[^ \t]+)?[ \t]*$",
        RegexOptions.Compiled);

    public int Probe(ISSHCommandRunner runner)
    {
        string[] output = runner.RunCommand("dnf --version");
        if (output != null && output.Any(line => line.IndexOf("dnf", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return 1000;
        }
        return 0;
    }

    public string GetCommandLineForIndexUpdate()
    {
        return "dnf -y makecache";
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions = false)
    {
        string[] installedLines = runner.RunCommand("LC_ALL=C dnf -q list --installed");
        if (installedLines == null)
        {
            throw new Exception("Failed to list packages with dnf");
        }
        Dictionary<string, LinuxPackageInfo> packages = new Dictionary<string, LinuxPackageInfo>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> installed in ParseListLines(installedLines))
        {
            packages[installed.Key] = new LinuxPackageInfo
            {
                ID = installed.Key,
                Version = installed.Value,
                State = LinuxPackageInfo.PackageState.Installed
            };
        }
        // "dnf list --available" fails when no repository can be reached: the installed packages are still listed then.
        foreach (KeyValuePair<string, string> available in ParseListLines(runner.RunCommand("LC_ALL=C dnf -q list --available")))
        {
            if (!packages.ContainsKey(available.Key))
            {
                packages.Add(available.Key, new LinuxPackageInfo
                {
                    ID = available.Key,
                    Version = available.Value,
                    State = LinuxPackageInfo.PackageState.Available
                });
            }
        }
        // The list of upgrades is a bonus only: if the command fails, the packages are still listed, just without updates.
        foreach (KeyValuePair<string, string> upgrade in ParseListLines(runner.RunCommand("LC_ALL=C dnf -q list --upgrades")))
        {
            if (packages.TryGetValue(upgrade.Key, out LinuxPackageInfo packageInfo) && packageInfo.State == LinuxPackageInfo.PackageState.Installed)
            {
                packageInfo.RawState = "upgradable from: " + packageInfo.Version;
                packageInfo.State = LinuxPackageInfo.PackageState.Upgradeable;
                packageInfo.Version = upgrade.Value;
            }
        }
        List<LinuxPackageInfo> result = packages.Values.ToList();
        result.Sort((LinuxPackageInfo first, LinuxPackageInfo second) => string.Compare(first.ID, second.ID, StringComparison.OrdinalIgnoreCase));
        return result.ToArray();
    }

    public string GetCommandLineForPackageOperation(LinuxPackageInfo[] packages, LinuxPackageInfo.PackageAction action, PackageCommitOptions options)
    {
        if (packages == null || packages.Length == 0)
        {
            return null;
        }
        string optionsFlag = "";
        if (options != null && options.NoPrompt)
        {
            optionsFlag = "-y ";
        }
        string actionName;
        switch (action)
        {
            case LinuxPackageInfo.PackageAction.Install:
                actionName = "install";
                break;
            case LinuxPackageInfo.PackageAction.Remove:
                actionName = "remove";
                break;
            default:
                return null;
        }
        // "dnf install" moves an already installed package to the newer version too, so upgrades need no separate command.
        return "dnf " + actionName + " " + optionsFlag + string.Join(" ", packages.Select((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.ID).ToArray());
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseListLines(string[] lines)
    {
        List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
        if (lines == null)
        {
            return result;
        }
        // dnf wraps a row whose package name does not fit into its column, like yum does.
        foreach (string line in YumPackageManagerClient.JoinContinuationLines(lines))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            Match match = ListLineRegex.Match(line.Trim());
            if (match.Success)
            {
                result.Add(new KeyValuePair<string, string>(match.Groups["id"].Value, match.Groups["version"].Value));
            }
        }
        return result;
    }
}