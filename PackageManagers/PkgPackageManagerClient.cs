using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

/// <summary>
/// Package manager client for pkg(8) of FreeBSD (and the systems that use the same tool, such as DragonFly BSD and GhostBSD).
/// </summary>
public class PkgPackageManagerClient : ILinuxPackageManagerClient
{
    // "pkg query '%n %v'" and "pkg rquery '%n %v'" line: name version.
    private static readonly Regex NameVersionLineRegex = new Regex(
        @"^(?<name>[^ \t]+)[ \t]+(?<version>[^ \t]+)[ \t]*$",
        RegexOptions.Compiled);

    // "pkg version -vRl '<'" line: "bash-5.2.21   <   needs updating (remote has 5.2.26)".
    private static readonly Regex OutdatedLineRegex = new Regex(
        @"^(?<pkgver>[^ \t]+)[ \t]+<(?:[ \t]+needs updating \(remote has (?<version>[^)]+)\))?",
        RegexOptions.Compiled);

    public int Probe(ISSHCommandRunner runner)
    {
        // "pkg -vv" prints the configuration of the tool (PKG_DBDIR, ABI, the repositories). On a fresh FreeBSD "pkg" is only
        // a stub that offers to download the real tool; it prints nothing like it, so it is not taken for a working pkg.
        string[] output = runner.RunCommand("pkg -vv");
        if (output != null && output.Any(line => line.IndexOf("PKG_DBDIR", StringComparison.Ordinal) >= 0))
        {
            return PackageManagerSelector.MaxScore;
        }
        return 0;
    }

    public string GetCommandLineForIndexUpdate()
    {
        return "pkg update";
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions = false)
    {
        string[] installedLines = runner.RunCommand("pkg query '%n %v'");
        if (installedLines == null)
        {
            throw new Exception("Failed to list packages with pkg");
        }
        Dictionary<string, LinuxPackageInfo> packages = new Dictionary<string, LinuxPackageInfo>(StringComparer.Ordinal);
        foreach (string line in installedLines)
        {
            Match match = NameVersionLineRegex.Match(line.Trim());
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
        // "pkg rquery" reads the catalogue of the repositories, which "pkg update" downloads: without it only the installed packages are listed.
        string[] availableLines = runner.RunCommand("pkg rquery '%n %v'");
        if (availableLines != null)
        {
            foreach (string line in availableLines)
            {
                Match match = NameVersionLineRegex.Match(line.Trim());
                if (match.Success && !packages.ContainsKey(match.Groups["name"].Value))
                {
                    packages.Add(match.Groups["name"].Value, new LinuxPackageInfo
                    {
                        ID = match.Groups["name"].Value,
                        Version = match.Groups["version"].Value,
                        State = LinuxPackageInfo.PackageState.Available
                    });
                }
            }
        }
        // Compares the installed packages with the catalogue; a bonus, so a failure just means "no updates here".
        string[] outdatedLines = runner.RunCommand("pkg version -vRl '<'");
        if (outdatedLines != null)
        {
            foreach (string line in outdatedLines)
            {
                Match match = OutdatedLineRegex.Match(line.Trim());
                if (!match.Success || !TrySplitPackageVersion(match.Groups["pkgver"].Value, out string name))
                {
                    continue;
                }
                if (packages.TryGetValue(name, out LinuxPackageInfo packageInfo) && packageInfo.State == LinuxPackageInfo.PackageState.Installed)
                {
                    packageInfo.RawState = "upgradable from: " + packageInfo.Version;
                    packageInfo.State = LinuxPackageInfo.PackageState.Upgradeable;
                    if (match.Groups["version"].Success)
                    {
                        packageInfo.Version = match.Groups["version"].Value.Trim();
                    }
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
        string optionsFlag = (options != null && options.NoPrompt) ? "-y " : "";
        switch (action)
        {
            case LinuxPackageInfo.PackageAction.Install:
                {
                    // "pkg upgrade <name>" moves an installed package to the newer version and says nothing about the others.
                    string[] upgrade = packages.Where(package => package.State == LinuxPackageInfo.PackageState.Upgradeable).Select(package => package.ID).ToArray();
                    string[] install = packages.Where(package => package.State != LinuxPackageInfo.PackageState.Upgradeable).Select(package => package.ID).ToArray();
                    List<string> commands = new List<string>();
                    if (install.Length != 0)
                    {
                        commands.Add("pkg install " + optionsFlag + string.Join(" ", install));
                    }
                    if (upgrade.Length != 0)
                    {
                        commands.Add("pkg upgrade " + optionsFlag + string.Join(" ", upgrade));
                    }
                    return string.Join(" && ", commands);
                }
            case LinuxPackageInfo.PackageAction.Remove:
                return "pkg delete " + optionsFlag + string.Join(" ", packages.Select((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.ID).ToArray());
            default:
                return null;
        }
    }

    /// <summary>Cuts the version off "name-version": the name of a package may contain dashes, the version never does.</summary>
    internal static bool TrySplitPackageVersion(string packageVersion, out string name)
    {
        name = null;
        int dashIndex = packageVersion?.LastIndexOf('-') ?? -1;
        if (dashIndex <= 0 || dashIndex == packageVersion.Length - 1)
        {
            return false;
        }
        name = packageVersion.Substring(0, dashIndex);
        return true;
    }
}
