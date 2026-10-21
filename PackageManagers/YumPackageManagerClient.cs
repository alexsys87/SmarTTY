using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

public class YumPackageManagerClient : ILinuxPackageManagerClient
{
    // "yum list" line: name.arch version repository ("@repository" for the installed packages).
    private static readonly Regex ListLineRegex = new Regex(
        @"^(?<id>[^ \t]+\.[^ \t]+)[ \t]+(?<version>[^ \t]+)[ \t]+[^ \t]+[ \t]*$",
        RegexOptions.Compiled);

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
        return "yum " + actionName + " " + optionsFlag + string.Join(" ", packages.Select((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.ID).ToArray());
    }

    public int Probe(ISSHCommandRunner runner)
    {
        if (runner.RunCommand("which yum") != null)
        {
            return 500;
        }
        return 0;
    }

    public string GetCommandLineForIndexUpdate()
    {
        return "yum -y makecache";
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions)
    {
        string[] installedLines = runner.RunCommand("LC_ALL=C yum list installed") ?? throw new Exception("Failed to list packages with yum");
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
        // "yum list available" fails when no repository can be reached: the installed packages are still listed then.
        foreach (KeyValuePair<string, string> available in ParseListLines(runner.RunCommand("LC_ALL=C yum list available")))
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
        // "yum list updates" exits with 1 when there is nothing to update, so a failed run only means "no updates here".
        foreach (KeyValuePair<string, string> upgrade in ParseListLines(runner.RunCommand("LC_ALL=C yum list updates")))
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

    /// <summary>
    /// Joins the lines that yum wraps when a package name does not fit into its column: the rest of the row
    /// goes to the next line, indented with spaces.
    /// </summary>
    internal static string[] JoinContinuationLines(string[] items)
    {
        List<string> mergedLines = new List<string>();
        foreach (string line in items)
        {
            if (line.Length > 0 && char.IsWhiteSpace(line[0]) && mergedLines.Count > 0)
            {
                mergedLines[mergedLines.Count - 1] += " " + line.Trim();
            }
            else
            {
                mergedLines.Add(line);
            }
        }
        return mergedLines.ToArray();
    }

    private static List<KeyValuePair<string, string>> ParseListLines(string[] lines)
    {
        List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
        if (lines == null)
        {
            return result;
        }
        foreach (string line in JoinContinuationLines(lines))
        {
            // The id must carry the architecture suffix, which keeps "Loaded plugins: ..." and the headers out.
            Match match = ListLineRegex.Match(line.Trim());
            if (match.Success)
            {
                result.Add(new KeyValuePair<string, string>(match.Groups["id"].Value, match.Groups["version"].Value));
            }
        }
        return result;
    }
}
