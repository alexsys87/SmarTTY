using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

public class YumPackageManagerClient : ILinuxPackageManagerClient
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

    private void JoinContinuationLines(ref string[] items)
    {
        List<string> mergedLines = new List<string>();
        foreach (string line in items)
        {
            if (line.StartsWith(" ") && mergedLines.Count > 0)
            {
                mergedLines[mergedLines.Count - 1] += line;
            }
            else
            {
                mergedLines.Add(line);
            }
        }
        items = mergedLines.ToArray();
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions)
    {
        List<LinuxPackageInfo> packagesList = new List<LinuxPackageInfo>();
        string[] installedLines = runner.RunCommand("yum list installed");
        string[] availableLines = runner.RunCommand("yum list available");
        Regex regex = new Regex("^([^ ]+)[ \t]+([^ ]+)[ \t]+([^ ]+)[ \t]*$");
        if (installedLines == null || availableLines == null)
        {
            throw new Exception("Failed to list packages with yum");
        }
        this.JoinContinuationLines(ref installedLines);
        this.JoinContinuationLines(ref availableLines);
        for (int passIndex = 0; passIndex < 2; passIndex++)
        {
            string[] currentLines = ((passIndex == 0) ? installedLines : availableLines);
            foreach (string input in currentLines)
            {
                Match match = regex.Match(input);
                if (match.Success)
                {
                    LinuxPackageInfo packageInfo = new LinuxPackageInfo
                    {
                        ID = match.Groups[1].Value,
                        Version = match.Groups[2].Value,
                        State = ((passIndex != 0) ? LinuxPackageInfo.PackageState.Available : LinuxPackageInfo.PackageState.Installed)
                    };
                    packagesList.Add(packageInfo);
                }
            }
        }
        packagesList.Sort((LinuxPackageInfo first, LinuxPackageInfo second) => first.ID.CompareTo(second.ID));
        return packagesList.ToArray();
    }
}
