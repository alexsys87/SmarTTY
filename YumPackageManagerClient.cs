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
        return "yum " + text2 + " " + text + string.Join(" ", packages.Select((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.ID).ToArray());
    }

    public int Probe(ISSHCommandRunner runner)
    {
        if (runner.RunCommand("which yum") != null)
        {
            return 500;
        }
        return 0;
    }

    private void JoinContinuationLines(ref string[] items)
    {
        List<string> list = new List<string>();
        string[] array = items;
        foreach (string text in array)
        {
            if (text.StartsWith(" ") && list.Count > 0)
            {
                list[list.Count - 1] += text;
            }
            else
            {
                list.Add(text);
            }
        }
        items = list.ToArray();
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions)
    {
        List<LinuxPackageInfo> list = new List<LinuxPackageInfo>();
        string[] array = runner.RunCommand("yum list installed");
        string[] array2 = runner.RunCommand("yum list available");
        Regex regex = new Regex("^([^ ]+)[ \t]+([^ ]+)[ \t]+([^ ]+)[ \t]*$");
        if (array == null || array2 == null)
        {
            throw new Exception("Failed to list packages with yum");
        }
        this.JoinContinuationLines(ref array);
        this.JoinContinuationLines(ref array2);
        for (int num = 0; num < 2; num++)
        {
            string[] array3 = ((num == 0) ? array : array2);
            foreach (string input in array3)
            {
                Match match = regex.Match(input);
                if (match.Success)
                {
                    LinuxPackageInfo item = new LinuxPackageInfo
                    {
                        ID = match.Groups[1].Value,
                        Version = match.Groups[2].Value,
                        State = ((num != 0) ? LinuxPackageInfo.PackageState.Available : LinuxPackageInfo.PackageState.Installed)
                    };
                    list.Add(item);
                }
            }
        }
        list.Sort((LinuxPackageInfo linuxPackageInfo, LinuxPackageInfo linuxPackageInfo2) => linuxPackageInfo.ID.CompareTo(linuxPackageInfo2.ID));
        return list.ToArray();
    }
}
