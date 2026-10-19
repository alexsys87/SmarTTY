using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

/// <summary>
/// Package manager client for apk (apk-tools): Alpine Linux and OpenWrt 25.12+, where apk replaced opkg.
/// </summary>
public class ApkPackageManagerClient : ILinuxPackageManagerClient
{
    // "apk list" line: name-version arch {origin} (license) [installed] | [upgradable from: name-oldversion]
    private static readonly Regex LineRegex = new Regex(
        @"^(?<nv>\S+)\s+(?<arch>\S+)\s+\{(?<origin>[^}]*)\}\s+\((?<license>.*)\)(?:\s+\[(?<state>[^\]]*)\])?\s*$",
        RegexOptions.Compiled);

    // apk versions always end with "-rN", e.g. busybox-1.37.0-r3 or kmod-fs-ext4-6.12.55~abc123-r1.
    private static readonly Regex NameVersionRegex = new Regex(@"^(?<name>.+)-(?<ver>[^-]+-r\d+)$", RegexOptions.Compiled);

    private static readonly Regex NameVersionFallbackRegex = new Regex(@"^(?<name>.+)-(?<ver>\d[^-]*)$", RegexOptions.Compiled);

    public int Probe(ISSHCommandRunner runner)
    {
        string[] output = runner.RunCommand("apk --version");
        if (output != null && output.Any(line => line.IndexOf("apk-tools", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return 1000;
        }
        return 0;
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions = false)
    {
        string[] lines = runner.RunCommand("apk list") ?? throw new Exception("Failed to list packages with apk");
        return ParsePackageList(lines);
    }

    /// <summary>
    /// Parses the output of "apk list". A package that has a newer version in the repositories appears twice:
    /// the installed one as "[installed]" and the newer one as "[upgradable from: ...]". Both are merged into one entry.
    /// </summary>
    public static LinuxPackageInfo[] ParsePackageList(IEnumerable<string> lines)
    {
        Dictionary<string, PackageVersions> packages = new Dictionary<string, PackageVersions>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            Match match = LineRegex.Match(line.Trim());
            if (!match.Success || !TrySplitNameVersion(match.Groups["nv"].Value, out string name, out string version))
            {
                continue;
            }
            if (!packages.TryGetValue(name, out PackageVersions versions))
            {
                versions = new PackageVersions();
                packages.Add(name, versions);
            }
            string state = match.Groups["state"].Success ? match.Groups["state"].Value.Trim() : null;
            if (state == null)
            {
                versions.Available = versions.Available ?? version;
            }
            else if (state.Equals("installed", StringComparison.OrdinalIgnoreCase))
            {
                versions.Installed = version;
            }
            else if (state.StartsWith("upgradable", StringComparison.OrdinalIgnoreCase))
            {
                versions.Upgrade = versions.Upgrade ?? version;
            }
        }

        List<LinuxPackageInfo> result = new List<LinuxPackageInfo>();
        foreach (KeyValuePair<string, PackageVersions> entry in packages)
        {
            PackageVersions versions = entry.Value;
            LinuxPackageInfo info = new LinuxPackageInfo { ID = entry.Key };
            if (versions.Installed != null && versions.Upgrade != null)
            {
                info.State = LinuxPackageInfo.PackageState.Upgradeable;
                info.Version = versions.Upgrade;
                info.RawState = "upgradable from: " + versions.Installed;
            }
            else if (versions.Installed != null)
            {
                info.State = LinuxPackageInfo.PackageState.Installed;
                info.Version = versions.Installed;
            }
            else
            {
                info.State = LinuxPackageInfo.PackageState.Available;
                info.Version = versions.Available ?? versions.Upgrade;
            }
            result.Add(info);
        }
        result.Sort((LinuxPackageInfo first, LinuxPackageInfo second) => string.Compare(first.ID, second.ID, StringComparison.OrdinalIgnoreCase));
        return result.ToArray();
    }

    public string GetCommandLineForPackageOperation(LinuxPackageInfo[] packages, LinuxPackageInfo.PackageAction action, PackageCommitOptions options)
    {
        if (packages == null || packages.Length == 0)
        {
            return null;
        }
        // apk is non-interactive by default (unlike apt), so ask for confirmations explicitly unless told not to.
        string prefix = (options != null && options.NoPrompt) ? "apk " : "apk -i ";
        switch (action)
        {
            case LinuxPackageInfo.PackageAction.Install:
                {
                    // "apk add" keeps an already installed package as is, "-u" is needed to move it to the newer version.
                    string[] fresh = packages.Where(p => p.State != LinuxPackageInfo.PackageState.Upgradeable).Select(p => p.ID).ToArray();
                    string[] upgrades = packages.Where(p => p.State == LinuxPackageInfo.PackageState.Upgradeable).Select(p => p.ID).ToArray();
                    List<string> commands = new List<string>();
                    if (fresh.Length != 0)
                    {
                        commands.Add(prefix + "add " + string.Join(" ", fresh));
                    }
                    if (upgrades.Length != 0)
                    {
                        commands.Add(prefix + "add -u " + string.Join(" ", upgrades));
                    }
                    return string.Join(" && ", commands);
                }
            case LinuxPackageInfo.PackageAction.Remove:
                return prefix + "del " + string.Join(" ", packages.Select(p => p.ID));
            default:
                return null;
        }
    }

    private static bool TrySplitNameVersion(string nameVersion, out string name, out string version)
    {
        Match match = NameVersionRegex.Match(nameVersion);
        if (!match.Success)
        {
            match = NameVersionFallbackRegex.Match(nameVersion);
        }
        name = match.Success ? match.Groups["name"].Value : null;
        version = match.Success ? match.Groups["ver"].Value : null;
        return match.Success;
    }

    private sealed class PackageVersions
    {
        public string Installed;

        public string Upgrade;

        public string Available;
    }
}
