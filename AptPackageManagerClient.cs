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
        string text = "";
        if (options != null && options.NoPrompt)
        {
            text = "-y ";
        }
        string text2;
        switch (action)
        {
            case LinuxPackageInfo.PackageAction.Install:
                text2 = "install";
                break;
            case LinuxPackageInfo.PackageAction.Remove:
                text2 = "remove";
                break;
            default:
                return null;
        }
        return "apt-get " + text2 + " " + text + string.Join(" ", packages.Select((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.ID).ToArray());
    }

    public int Probe(ISSHCommandRunner runner)
    {
        if (runner.RunCommand("apt -v") != null)
        {
            return 1000;
        }
        return 0;
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions)
    {
        List<LinuxPackageInfo> list = new List<LinuxPackageInfo>();
        string[] array = runner.RunCommand("LANG=en_US apt list" + (allVersions ? " -a" : "")) ?? throw new Exception("Failed to list packages with apt");
        Regex regex = new Regex("^([^/]+)/([^ ]+) ([^ ]+) ");
        string[] array2 = array;
        foreach (string text in array2)
        {
            int num2 = text.IndexOf('[');
            Match match = regex.Match(text);
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
            if (num2 != -1)
            {
                string text2 = text.Substring(num2);
                linuxPackageInfo.RawState = text2.Trim(' ', '[', ']');
                if (text2.Contains("installed"))
                {
                    linuxPackageInfo.State = LinuxPackageInfo.PackageState.Installed;
                }
                else if (text2.Contains("upgradable"))
                {
                    linuxPackageInfo.State = LinuxPackageInfo.PackageState.Upgradeable;
                }
            }
            list.Add(linuxPackageInfo);
        }
        return list.ToArray();
    }
}
