using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

/// <summary>
/// Package manager client for Portage: Gentoo Linux, Calculate Linux, Funtoo and other Gentoo-based distributions.
/// </summary>
public class PortagePackageManagerClient : ILinuxPackageManagerClient
{
    // A package line of "emerge --pretend": "[ebuild     U  ] dev-libs/glib-2.82.1::gentoo [2.80.0] USE="..." ".
    private static readonly Regex EbuildLineRegex = new Regex(
        "^\\[ebuild(?<flags>[^\\]]*)\\](?:[ \\t]+(?<cpv>[^ \\t]+))(?:[ \\t]+\\[(?<installed>[^\\]]*)\\])?",
        RegexOptions.Compiled);

    public int Probe(ISSHCommandRunner runner)
    {
        string[] output = runner.RunCommand("emerge --version");
        if (output != null && output.Any((string line) => line.IndexOf("Portage", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return 1000;
        }
        return 0;
    }

    public string GetCommandLineForIndexUpdate()
    {
        return "emerge --sync";
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions = false)
    {
        // "portageq match / '*/*'" lists the packages installed into /var/db/pkg in the category/name-version form.
        string[] installedLines = runner.RunCommand("portageq match / '*/*'");
        if (installedLines == null)
        {
            throw new Exception("Failed to list packages with portage");
        }
        Dictionary<string, LinuxPackageInfo> packages = new Dictionary<string, LinuxPackageInfo>(StringComparer.Ordinal);
        AddPackages(packages, installedLines, LinuxPackageInfo.PackageState.Installed);
        // "portageq all_best_visible" reads the ebuild repositories: without "emerge --sync" only the installed packages are listed.
        AddPackages(packages, runner.RunCommand("portageq all_best_visible /"), LinuxPackageInfo.PackageState.Available);
        // "emerge -uDNp @world" prints the packages that "emerge @world" would touch, the installed versions stay in place.
        string[] upgradeLines = runner.RunCommand("emerge -uDNp @world");
        if (upgradeLines != null)
        {
            foreach (string line in upgradeLines)
            {
                Match match = EbuildLineRegex.Match(line.Trim());
                if (!match.Success)
                {
                    continue;
                }
                string flags = match.Groups["flags"].Value;
                // "U" is an upgrade and "UD" a downgrade: only the first one is an update.
                if (flags.IndexOf('U') < 0 || flags.IndexOf('D') >= 0)
                {
                    continue;
                }
                string id;
                string version;
                if (!TrySplitCPV(match.Groups["cpv"].Value, out id, out version))
                {
                    continue;
                }
                LinuxPackageInfo packageInfo;
                if (packages.TryGetValue(id, out packageInfo) && packageInfo.State == LinuxPackageInfo.PackageState.Installed)
                {
                    string installedVersion = NormalizeVersion(match.Groups["installed"].Value);
                    packageInfo.RawState = "upgradable from: " + (installedVersion.Length > 0 ? installedVersion : packageInfo.Version);
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
        // Portage asks the questions of the merge plan by default: "--ask=n" takes the default answer instead.
        string optionsFlag = ((options != null && options.NoPrompt) ? "--ask=n " : "");
        string actionFlag;
        switch (action)
        {
            case LinuxPackageInfo.PackageAction.Install:
                actionFlag = "";
                break;
            case LinuxPackageInfo.PackageAction.Remove:
                actionFlag = "--unmerge ";
                break;
            default:
                return null;
        }
        return "emerge " + optionsFlag + actionFlag + string.Join(" ", packages.Select((LinuxPackageInfo linuxPackageInfo) => linuxPackageInfo.ID).ToArray());
    }

    private static void AddPackages(Dictionary<string, LinuxPackageInfo> packages, string[] lines, LinuxPackageInfo.PackageState state)
    {
        if (lines == null)
        {
            return;
        }
        foreach (string line in lines)
        {
            string id;
            string version;
            if (!TrySplitCPV(line, out id, out version) || packages.ContainsKey(id))
            {
                continue;
            }
            packages.Add(id, new LinuxPackageInfo
            {
                ID = id,
                Version = version,
                State = state
            });
        }
    }

    // Turns "dev-libs/glib-2.82.1:2::gentoo" into the "dev-libs/glib" atom and the "2.82.1" version.
    // Gentoo versions start with a digit and contain no hyphen except the "-rN" revision suffix, so the
    // rightmost hyphen in front of a digit separates the package name from the version.
    private static bool TrySplitCPV(string cpv, out string id, out string version)
    {
        id = null;
        version = null;
        string text = NormalizeVersion(cpv);
        int slashIndex = text.IndexOf('/');
        if (slashIndex <= 0 || slashIndex == text.Length - 1)
        {
            return false;
        }
        for (int index = text.Length - 1; index > slashIndex + 1; index--)
        {
            if (text[index - 1] == '-' && text[index] >= '0' && text[index] <= '9')
            {
                id = text.Substring(0, index - 1);
                version = text.Substring(index);
                return true;
            }
        }
        return false;
    }

    // Drops the ":slot" and the "::repository" suffixes: "2.80.0:0/1::gentoo" becomes "2.80.0".
    private static string NormalizeVersion(string value)
    {
        string text = ((value == null) ? "" : value.Trim());
        int slotIndex = text.IndexOf(':');
        if (slotIndex >= 0)
        {
            text = text.Substring(0, slotIndex);
        }
        return text;
    }
}
