using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

public class AptPackageManagerClient : ILinuxPackageManagerClient
{
    private const string UpgradableTo = "upgradable to:";

    private static readonly Regex LineRegex = new Regex("^([^/ ]+)/([^ ]+) ([^ ]+) ", RegexOptions.Compiled);

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
        return "apt-get " + actionName + " " + optionsFlag + string.Join(" ", packages.Select((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.ID).ToArray());
    }

    public int Probe(ISSHCommandRunner runner)
    {
        if (runner.RunCommand("apt -v") != null)
        {
            return 1000;
        }
        return 0;
    }

    public string GetCommandLineForIndexUpdate()
    {
        return "apt-get update";
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions)
    {
        // apt translates the "[installed]" marks: LC_ALL=C keeps them in English whatever the locale of the host is
        // (LANG alone is not enough, LANGUAGE takes precedence over it for the translated messages).
        string[] lines = runner.RunCommand("LC_ALL=C apt list" + (allVersions ? " -a" : "")) ?? throw new Exception("Failed to list packages with apt");
        return ParsePackageList(lines);
    }

    /// <summary>
    /// Parses the output of "apt list": "name/suite version arch [state]", where the state is e.g. "installed",
    /// "installed,automatic", "upgradable from: 1.0-1" or "installed,upgradable to: 1.0-2" ("apt list --installed").
    /// </summary>
    public static LinuxPackageInfo[] ParsePackageList(IEnumerable<string> lines)
    {
        List<LinuxPackageInfo> packagesList = new List<LinuxPackageInfo>();
        foreach (string line in lines)
        {
            Match match = LineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }
            LinuxPackageInfo linuxPackageInfo = new LinuxPackageInfo
            {
                ID = match.Groups[1].Value,
                Version = match.Groups[3].Value,
                State = LinuxPackageInfo.PackageState.Available
            };
            int bracketIndex = line.IndexOf('[');
            if (bracketIndex != -1)
            {
                string bracketContent = line.Substring(bracketIndex).Trim(' ', '[', ']');
                linuxPackageInfo.RawState = bracketContent;
                // "installed,upgradable to: X" contains "installed" too, so the upgrade has to be checked first.
                int upgradeToIndex = bracketContent.IndexOf(UpgradableTo, StringComparison.Ordinal);
                if (upgradeToIndex != -1)
                {
                    linuxPackageInfo.State = LinuxPackageInfo.PackageState.Upgradeable;
                    linuxPackageInfo.RawState = "upgradable from: " + linuxPackageInfo.Version;
                    linuxPackageInfo.Version = bracketContent.Substring(upgradeToIndex + UpgradableTo.Length).Trim();
                }
                else if (bracketContent.Contains("upgradable"))
                {
                    linuxPackageInfo.State = LinuxPackageInfo.PackageState.Upgradeable;
                }
                else if (bracketContent.Split(',').Any(state => state.Trim() == "installed"))
                {
                    linuxPackageInfo.State = LinuxPackageInfo.PackageState.Installed;
                }
            }
            packagesList.Add(linuxPackageInfo);
        }
        return packagesList.ToArray();
    }
}
