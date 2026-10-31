using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

/// <summary>
/// Package manager client for Homebrew: formulae on macOS (Apple silicon and Intel) and on Linux (Linuxbrew). Casks are not listed.
/// Homebrew refuses to run as root and never needs it, so the package manager window does not wrap its commands in sudo.
/// </summary>
public class HomebrewPackageManagerClient : ILinuxPackageManagerClient
{
    /// <summary>
    /// Below yum: a Linux host that has Linuxbrew in addition to apt, dnf or another system manager is managed by that manager.
    /// A Mac has no other one, so Homebrew wins there.
    /// </summary>
    public const int ProbeScore = 400;

    /// <summary>
    /// The commands run in a non-login sh of the SSH exec channel, and its PATH does not have the directory of Homebrew:
    /// /opt/homebrew/bin (Apple silicon), /usr/local/bin (Intel) and /home/linuxbrew/.linuxbrew/bin (Linuxbrew). They are appended.
    /// </summary>
    public const string PathPrefix = "PATH=\"$PATH:/opt/homebrew/bin:/usr/local/bin:/home/linuxbrew/.linuxbrew/bin\" ";

    // The list is read from the local data and a query must not wait for "brew update" that Homebrew starts by itself first.
    private const string QueryPrefix = PathPrefix + "HOMEBREW_NO_AUTO_UPDATE=1 HOMEBREW_NO_ANALYTICS=1 ";

    // "brew list --versions" line: "git 2.45.2"; several installed versions of a formula are listed in one row: "python@3.12 3.12.3 3.12.4".
    private static readonly Regex InstalledLineRegex = new Regex(
        @"^(?<name>[^ \t]+)(?:[ \t]+(?<versions>[^ \t].*?))?[ \t]*$",
        RegexOptions.Compiled);

    // "brew outdated --verbose" line: "git (2.45.1) < 2.45.2" or "python@3.12 (3.12.3, 3.12.4) < 3.12.5".
    private static readonly Regex OutdatedLineRegex = new Regex(
        @"^(?<name>[^ \t]+)[ \t]+\([^)]*\)[ \t]+<[ \t]+(?<version>[^ \t]+)[ \t]*$",
        RegexOptions.Compiled);

    // A formula name: letters, digits and "@ + . _ -" ("python@3.12", "gtk+3"), and an optional "tap/repo/" before it.
    private static readonly Regex FormulaNameRegex = new Regex(@"^[A-Za-z0-9@+._/\-]+$", RegexOptions.Compiled);

    public int Probe(ISSHCommandRunner runner)
    {
        string[] output = runner.RunCommand(PathPrefix + "brew --version");
        if (output != null && output.Any(line => line.TrimStart().StartsWith("Homebrew", StringComparison.Ordinal)))
        {
            return ProbeScore;
        }
        return 0;
    }

    public string GetCommandLineForIndexUpdate()
    {
        return PathPrefix + "brew update";
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions = false)
    {
        string[] installedLines = runner.RunCommand(QueryPrefix + "brew list --formula --versions");
        if (installedLines == null)
        {
            throw new Exception("Failed to list packages with Homebrew");
        }
        Dictionary<string, LinuxPackageInfo> packages = new Dictionary<string, LinuxPackageInfo>(StringComparer.Ordinal);
        foreach (string line in installedLines)
        {
            Match match = InstalledLineRegex.Match(line.Trim());
            if (!match.Success || !FormulaNameRegex.IsMatch(match.Groups["name"].Value))
            {
                continue;
            }
            string versions = match.Groups["versions"].Success ? match.Groups["versions"].Value : "";
            // The newest of the installed versions is the last one.
            string version = versions.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            packages[match.Groups["name"].Value] = new LinuxPackageInfo
            {
                ID = match.Groups["name"].Value,
                Version = version,
                State = LinuxPackageInfo.PackageState.Installed
            };
        }
        // "brew formulae" prints the names of all formulae of the taps from the local data (no versions, no network).
        string[] formulaeLines = runner.RunCommand(QueryPrefix + "brew formulae");
        if (formulaeLines != null)
        {
            foreach (string line in formulaeLines)
            {
                string name = line.Trim();
                if (name.Length != 0 && FormulaNameRegex.IsMatch(name) && !packages.ContainsKey(name))
                {
                    packages.Add(name, new LinuxPackageInfo
                    {
                        ID = name,
                        Version = "",
                        State = LinuxPackageInfo.PackageState.Available
                    });
                }
            }
        }
        // A bonus: a failure just means "no updates here". A line that does not end at the new version (a pinned formula has a note after it) is not taken.
        string[] outdatedLines = runner.RunCommand(QueryPrefix + "brew outdated --formula --verbose");
        if (outdatedLines != null)
        {
            foreach (string line in outdatedLines)
            {
                Match match = OutdatedLineRegex.Match(line.Trim());
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
        // Homebrew never asks a question before it installs or removes something: NoPrompt has nothing to switch off.
        switch (action)
        {
            case LinuxPackageInfo.PackageAction.Install:
                {
                    // "brew install <name>" of an installed formula only says that it is installed: the newer version is "brew upgrade <name>".
                    string[] upgrade = packages.Where(package => package.State == LinuxPackageInfo.PackageState.Upgradeable).Select(package => package.ID).ToArray();
                    string[] install = packages.Where(package => package.State != LinuxPackageInfo.PackageState.Upgradeable).Select(package => package.ID).ToArray();
                    List<string> commands = new List<string>();
                    if (install.Length != 0)
                    {
                        commands.Add("brew install " + string.Join(" ", install));
                    }
                    if (upgrade.Length != 0)
                    {
                        commands.Add("brew upgrade " + string.Join(" ", upgrade));
                    }
                    return string.Join(" && ", commands);
                }
            case LinuxPackageInfo.PackageAction.Remove:
                return "brew uninstall " + string.Join(" ", packages.Select((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.ID).ToArray());
            default:
                return null;
        }
    }
}
