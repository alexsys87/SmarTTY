using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

/// <summary>
/// Package manager client for xbps: Void Linux (glibc and musl).
/// </summary>
public class XbpsPackageManagerClient : ILinuxPackageManagerClient
{
    // "xbps-query -l" line: "ii bash-5.2.15_1   GNU Bourne Again SHell" (two letters of the state, then name-version_revision).
    private static readonly Regex InstalledLineRegex = new Regex(
        @"^(?<state>[a-z?]{2})[ \t]+(?<pkgver>[^ \t]+)(?:[ \t]|$)",
        RegexOptions.Compiled);

    // "xbps-query -Rs ''" line: "[-] 0ad-0.0.26_1   Free, real-time strategy game" ("[*]" for an installed package).
    private static readonly Regex RepositoryLineRegex = new Regex(
        @"^\[(?<mark>[*\-])\][ \t]+(?<pkgver>[^ \t]+)(?:[ \t]|$)",
        RegexOptions.Compiled);

    // "xbps-install -un" line: "bash-5.2.21_1 update x86_64 https://repo-default.voidlinux.org/current 1365027 321432".
    private static readonly Regex UpdateLineRegex = new Regex(
        @"^(?<pkgver>[^ \t]+)[ \t]+update(?:[ \t]|$)",
        RegexOptions.Compiled);

    public int Probe(ISSHCommandRunner runner)
    {
        // "xbps-install" is the tool that changes the system: xbps-query alone may be a leftover on a foreign host.
        // The answer is the path of the tool, so a line of an error text that only names it is not taken.
        string[] output = runner.RunCommand("command -v xbps-install || which xbps-install");
        if (output != null && output.Any(line => line.Trim().EndsWith("/xbps-install", StringComparison.Ordinal)))
        {
            return PackageManagerSelector.MaxScore;
        }
        return 0;
    }

    public string GetCommandLineForIndexUpdate()
    {
        // "-y" answers the question about importing the public key of a repository the first time it is synchronised:
        // nobody can answer it on the command channel, and without the answer the command waits for it.
        return "xbps-install -S -y";
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions = false)
    {
        string[] installedLines = runner.RunCommand("xbps-query -l");
        if (installedLines == null)
        {
            throw new Exception("Failed to list packages with xbps");
        }
        Dictionary<string, LinuxPackageInfo> packages = new Dictionary<string, LinuxPackageInfo>(StringComparer.Ordinal);
        foreach (string line in installedLines)
        {
            Match match = InstalledLineRegex.Match(line.Trim());
            if (match.Success && TrySplitPackageVersion(match.Groups["pkgver"].Value, out string name, out string version))
            {
                packages[name] = new LinuxPackageInfo
                {
                    ID = name,
                    Version = version,
                    State = LinuxPackageInfo.PackageState.Installed
                };
            }
        }
        // The repository data is downloaded by "xbps-install -S": if it never was, only the installed packages are listed.
        // The empty pattern of "-s" matches every package.
        string[] repositoryLines = runner.RunCommand("xbps-query -Rs ''");
        if (repositoryLines != null)
        {
            foreach (string line in repositoryLines)
            {
                Match match = RepositoryLineRegex.Match(line.Trim());
                if (match.Success && TrySplitPackageVersion(match.Groups["pkgver"].Value, out string name, out string version) && !packages.ContainsKey(name))
                {
                    packages.Add(name, new LinuxPackageInfo
                    {
                        ID = name,
                        Version = version,
                        State = LinuxPackageInfo.PackageState.Available
                    });
                }
            }
        }
        // "xbps-install -un" only plans the update and changes nothing; it is a bonus, a failure just means "no updates here".
        string[] updateLines = runner.RunCommand("xbps-install -un");
        if (updateLines != null)
        {
            foreach (string line in updateLines)
            {
                Match match = UpdateLineRegex.Match(line.Trim());
                if (!match.Success || !TrySplitPackageVersion(match.Groups["pkgver"].Value, out string name, out string version))
                {
                    continue;
                }
                if (packages.TryGetValue(name, out LinuxPackageInfo packageInfo) && packageInfo.State == LinuxPackageInfo.PackageState.Installed)
                {
                    packageInfo.RawState = "upgradable from: " + packageInfo.Version;
                    packageInfo.State = LinuxPackageInfo.PackageState.Upgradeable;
                    packageInfo.Version = version;
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
                    // "xbps-install <name>" does not move an installed package to the newer version: that is "xbps-install -u <name>",
                    // which in its turn refuses a package that is not installed. So the two kinds are separate commands.
                    string[] upgrade = packages.Where(package => package.State == LinuxPackageInfo.PackageState.Upgradeable).Select(package => package.ID).ToArray();
                    string[] install = packages.Where(package => package.State != LinuxPackageInfo.PackageState.Upgradeable).Select(package => package.ID).ToArray();
                    List<string> commands = new List<string>();
                    if (install.Length != 0)
                    {
                        commands.Add("xbps-install " + optionsFlag + string.Join(" ", install));
                    }
                    if (upgrade.Length != 0)
                    {
                        commands.Add("xbps-install -u " + optionsFlag + string.Join(" ", upgrade));
                    }
                    return string.Join(" && ", commands);
                }
            case LinuxPackageInfo.PackageAction.Remove:
                return "xbps-remove " + optionsFlag + string.Join(" ", packages.Select((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.ID).ToArray());
            default:
                return null;
        }
    }

    /// <summary>
    /// Splits "name-version_revision" at the last '-': the name of a package may contain dashes ("gtk+3-3.24.38_1"), the version never.
    /// </summary>
    internal static bool TrySplitPackageVersion(string packageVersion, out string name, out string version)
    {
        name = null;
        version = null;
        int dashIndex = packageVersion?.LastIndexOf('-') ?? -1;
        if (dashIndex <= 0 || dashIndex == packageVersion.Length - 1)
        {
            return false;
        }
        name = packageVersion.Substring(0, dashIndex);
        version = packageVersion.Substring(dashIndex + 1);
        return true;
    }
}
