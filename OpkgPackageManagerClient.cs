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

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions = false)
    {
        List<LinuxPackageInfo> packagesList = new List<LinuxPackageInfo>();
        HashSet<string> installedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Query installed packages first: "opkg list-installed" outputs "package_name - version"
        string[] installedLines = runner.RunCommand("opkg list-installed");
        if (installedLines != null)
        {
            foreach (string line in installedLines)
            {
                int dashIndex = line.IndexOf(" - ", StringComparison.Ordinal);
                if (dashIndex > 0)
                {
                    string name = line.Substring(0, dashIndex).Trim();
                    string version = line.Substring(dashIndex + 3).Trim();
                    installedPackages.Add(name);
                    packagesList.Add(new LinuxPackageInfo
                    {
                        ID = name,
                        Version = version,
                        State = LinuxPackageInfo.PackageState.Installed
                    });
                }
            }
        }

        // Query available packages: "opkg list" outputs "package_name - version - description"
        string[] availableLines = runner.RunCommand("opkg list");
        if (availableLines != null)
        {
            foreach (string line in availableLines)
            {
                int dashIndex = line.IndexOf(" - ", StringComparison.Ordinal);
                if (dashIndex > 0)
                {
                    string name = line.Substring(0, dashIndex).Trim();
                    if (!installedPackages.Contains(name))
                    {
                        string rest = line.Substring(dashIndex + 3).Trim();
                        int secondDashIndex = rest.IndexOf(" - ", StringComparison.Ordinal);
                        string version = secondDashIndex > 0 ? rest.Substring(0, secondDashIndex).Trim() : rest;
                        packagesList.Add(new LinuxPackageInfo
                        {
                            ID = name,
                            Version = version,
                            State = LinuxPackageInfo.PackageState.Available
                        });
                    }
                }
            }
        }

        packagesList.Sort((LinuxPackageInfo first, LinuxPackageInfo second) => string.Compare(first.ID, second.ID, StringComparison.OrdinalIgnoreCase));
        return packagesList.ToArray();
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
