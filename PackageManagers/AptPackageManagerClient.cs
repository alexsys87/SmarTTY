using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

public class AptPackageManagerClient : ILinuxPackageManagerClient
{

    public string GetCommandLineForPackageOperation(LinuxPackageInfo[] packages, LinuxPackageInfo.PackageAction action, PackageCommitOptions options)
    {
        if (packages.Length == 0)
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
        List<LinuxPackageInfo> packagesList = new List<LinuxPackageInfo>();
        string[] lines = runner.RunCommand("LANG=en_US apt list" + (allVersions ? " -a" : "")) ?? throw new Exception("Failed to list packages with apt");
        Regex regex = new Regex("^([^/]+)/([^ ]+) ([^ ]+) ");
        foreach (string line in lines)
        {
            int bracketIndex = line.IndexOf('[');
            Match match = regex.Match(line);
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
            if (bracketIndex != -1)
            {
                string bracketContent = line.Substring(bracketIndex);
                linuxPackageInfo.RawState = bracketContent.Trim(' ', '[', ']');
                if (bracketContent.Contains("installed"))
                {
                    linuxPackageInfo.State = LinuxPackageInfo.PackageState.Installed;
                }
                else if (bracketContent.Contains("upgradable"))
                {
                    linuxPackageInfo.State = LinuxPackageInfo.PackageState.Upgradeable;
                }
            }
            packagesList.Add(linuxPackageInfo);
        }
        return packagesList.ToArray();
    }
}
