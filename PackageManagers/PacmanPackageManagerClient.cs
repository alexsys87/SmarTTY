using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

/// <summary>
/// Package manager client for pacman: Arch Linux, Manjaro, EndeavourOS, Garuda Linux and other Arch-based distributions.
/// </summary>
public class PacmanPackageManagerClient : ILinuxPackageManagerClient
{
    // "pacman -Q" line: name version.
    private static readonly Regex InstalledLineRegex = new Regex(
        @"^(?<name>[^ \t]+)[ \t]+(?<version>[^ \t]+)[ \t]*$",
        RegexOptions.Compiled);

    // "pacman -Sl" line: repository name version [installed] ("[installed: <older version>]" for outdated packages).
    private static readonly Regex AvailableLineRegex = new Regex(
        @"^(?<repo>[^ \t]+)[ \t]+(?<name>[^ \t]+)[ \t]+(?<version>[^ \t]+)(?:[ \t]+\[[^\]]*\])?[ \t]*$",
        RegexOptions.Compiled);

    // "pacman -Qu" line: name installed-version -> available-version.
    private static readonly Regex UpgradeLineRegex = new Regex(
        @"^(?<name>[^ \t]+)[ \t]+(?<installed>[^ \t]+)[ \t]+->[ \t]+(?<version>[^ \t]+)[ \t]*$",
        RegexOptions.Compiled);

    public int Probe(ISSHCommandRunner runner)
    {
        string[] output = runner.RunCommand("pacman --version");
        if (output != null && output.Any(line => line.IndexOf("Pacman", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return 1000;
        }
        return 0;
    }

    public string GetCommandLineForIndexUpdate()
    {
        return "pacman -Sy";
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions = false)
    {
        string[] installedLines = runner.RunCommand("pacman -Q");
        if (installedLines == null)
        {
            throw new Exception("Failed to list packages with pacman");
        }
        Dictionary<string, LinuxPackageInfo> packages = new Dictionary<string, LinuxPackageInfo>(StringComparer.Ordinal);
        foreach (string line in installedLines)
        {
            Match match = InstalledLineRegex.Match(line.Trim());
            if (match.Success)
            {
                packages[match.Groups["name"].Value] = new LinuxPackageInfo
                {
                    ID = match.Groups["name"].Value,
                    Version = match.Groups["version"].Value,
                    State = LinuxPackageInfo.PackageState.Installed
                };
            }
        }
        // "pacman -Sl" needs the sync databases: if they were never downloaded, only the installed packages are listed.
        string[] availableLines = runner.RunCommand("pacman -Sl");
        if (availableLines != null)
        {
            foreach (string line in availableLines)
            {
                Match match = AvailableLineRegex.Match(line.Trim());
                if (!match.Success)
                {
                    continue;
                }
                string name = match.Groups["name"].Value;
                if (!packages.ContainsKey(name))
                {
                    packages.Add(name, new LinuxPackageInfo
                    {
                        ID = name,
                        Version = match.Groups["version"].Value,
                        State = LinuxPackageInfo.PackageState.Available
                    });
                }
            }
        }
        // "pacman -Qu" exits with 1 when there is nothing to update, so a failed run only means "no updates here".
        string[] upgradeLines = runner.RunCommand("pacman -Qu");
        if (upgradeLines != null)
        {
            foreach (string line in upgradeLines)
            {
                Match match = UpgradeLineRegex.Match(line.Trim());
                if (!match.Success)
                {
                    continue;
                }
                if (packages.TryGetValue(match.Groups["name"].Value, out LinuxPackageInfo packageInfo) && packageInfo.State == LinuxPackageInfo.PackageState.Installed)
                {
                    packageInfo.RawState = "upgradable from: " + packageInfo.Version;
                    packageInfo.State = LinuxPackageInfo.PackageState.Upgradeable;
                    packageInfo.Version = match.Groups["version"].Value;
                }
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
        // pacman has no "-y": "--noconfirm" accepts the default answer instead of asking, and pacman asks anyway by default.
        string optionsFlag = ((options != null && options.NoPrompt) ? "--noconfirm " : "");
        string actionName;
        switch (action)
        {
            case LinuxPackageInfo.PackageAction.Install:
                // "pacman -S" also moves an installed package to the version from the sync databases.
                actionName = "-S ";
                break;
            case LinuxPackageInfo.PackageAction.Remove:
                actionName = "-R ";
                break;
            default:
                return null;
        }
        return "pacman " + actionName + optionsFlag + string.Join(" ", packages.Select((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.ID).ToArray());
    }
}