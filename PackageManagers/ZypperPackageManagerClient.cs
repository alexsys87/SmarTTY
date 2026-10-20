using System;
using System.Collections.Generic;
using System.Linq;
using VisualTTY;

namespace SmarTTY;

/// <summary>
/// Package manager client for zypper: openSUSE Leap, openSUSE Tumbleweed and SUSE Linux Enterprise.
/// </summary>
public class ZypperPackageManagerClient : ILinuxPackageManagerClient
{
    // Both "zypper packages" and "zypper list-updates" print a table with '|' separated cells,
    // e.g. "i | @System | curl | 8.9.1-1.1" and "v | Update | curl | 8.9.1-1.1 | 8.10.1-1.1".

    public int Probe(ISSHCommandRunner runner)
    {
        string[] output = runner.RunCommand("zypper --version");
        if (output != null && output.Any(line => line.IndexOf("zypper", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return 1000;
        }
        return 0;
    }

    public string GetCommandLineForIndexUpdate()
    {
        return "zypper refresh";
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions = false)
    {
        string[] installedLines = runner.RunCommand("zypper packages --installed-only");
        string[] availableLines = runner.RunCommand("zypper packages --not-installed-only");
        if (installedLines == null || availableLines == null)
        {
            throw new Exception("Failed to list packages with zypper");
        }
        Dictionary<string, LinuxPackageInfo> packages = new Dictionary<string, LinuxPackageInfo>(StringComparer.Ordinal);
        AddPackages(packages, installedLines, LinuxPackageInfo.PackageState.Installed);
        AddPackages(packages, availableLines, LinuxPackageInfo.PackageState.Available);
        // "zypper list-updates" is a bonus only: when it fails, the packages are still listed, just without updates.
        string[] updateLines = runner.RunCommand("zypper list-updates");
        if (updateLines != null)
        {
            foreach (string line in updateLines)
            {
                string[] cells = SplitRow(line);
                if (cells == null || cells.Length < 5)
                {
                    continue;
                }
                string name = cells[2];
                string version = cells[4];
                if (name.Length == 0 || name == "Name" || version.Length == 0 || version.IndexOf(' ') >= 0)
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
        // zypper has no "-y"; "--non-interactive" answers "yes" to every question it would otherwise ask.
        string prefix = (options != null && options.NoPrompt) ? "zypper --non-interactive " : "zypper ";
        string actionName;
        switch (action)
        {
            case LinuxPackageInfo.PackageAction.Install:
                // "zypper install" also moves an installed package to the newer version, like "zypper update <name>".
                actionName = "install";
                break;
            case LinuxPackageInfo.PackageAction.Remove:
                actionName = "remove";
                break;
            default:
                return null;
        }
        return prefix + actionName + " " + string.Join(" ", packages.Select((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.ID).ToArray());
    }

    private static void AddPackages(Dictionary<string, LinuxPackageInfo> packages, string[] lines, LinuxPackageInfo.PackageState state)
    {
        if (lines == null)
        {
            return;
        }
        foreach (string line in lines)
        {
            string[] cells = SplitRow(line);
            if (cells == null || cells.Length < 4)
            {
                continue;
            }
            string name = cells[2];
            string version = cells[3];
            if (name.Length == 0 || version.Length == 0 || name == "Name")
            {
                continue;
            }
            if (!packages.ContainsKey(name))
            {
                packages.Add(name, new LinuxPackageInfo
                {
                    ID = name,
                    Version = version,
                    State = state
                });
            }
        }
    }

    private static string[] SplitRow(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.IndexOf('|') < 0)
        {
            return null;
        }
        string[] cells = line.Split('|');
        for (int cellIndex = 0; cellIndex < cells.Length; cellIndex++)
        {
            cells[cellIndex] = cells[cellIndex].Trim();
        }
        return cells;
    }
}