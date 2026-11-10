using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

public class AptPackageManagerClient : ILinuxPackageManagerClient, ILocalPackageFileInstaller
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

    public string LocalPackageFileFilter => "Debian packages (*.deb)|*.deb";

    public string InstallToolName => "apt-get";

    public bool IsPackageFile(string fileName)
    {
        return LocalPackageFiles.HasExtension(fileName, ".deb");
    }

    public string GetCommandLineForPackageFiles(string[] remotePaths, PackageCommitOptions options)
    {
        if (!LocalPackageFiles.HasFiles(remotePaths))
        {
            return null;
        }
        // A path with a slash is read as a file; apt-get also installs what the package needs from the repositories.
        return "apt-get install " + ((options != null && options.NoPrompt) ? "-y " : "") + LocalPackageFiles.QuoteAll(remotePaths);
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

    /// <summary>The line that "apt list" is followed by, with its exit status after the colon (see <see cref="ListCommand"/>).</summary>
    public const string ExitMarker = "SMARTTY-APT-EXIT:";

    /// <summary>
    /// The installed packages from the database of dpkg, which needs no package lists of the repositories: "name\tversion\tarch\tii ".
    /// </summary>
    public const string InstalledQueryCommand = "LC_ALL=C dpkg-query -W -f='${Package}\\t${Version}\\t${Architecture}\\t${db:Status-Abbrev}\\n'";

    /// <summary>
    /// "apt list", with its messages and its exit status kept: an error of apt (a list file that cannot be read, a broken source on a
    /// host whose lists were never downloaded) made the command fail as a whole, and the window closed with "Failed to list
    /// packages with apt", before Refresh (apt-get update) could be pressed, and without what apt said.
    /// </summary>
    public static string ListCommand(bool allVersions)
    {
        // apt translates the "[installed]" marks: LC_ALL=C keeps them in English whatever the locale of the host is
        // (LANG alone is not enough, LANGUAGE takes precedence over it for the translated messages).
        return "LC_ALL=C apt list" + (allVersions ? " -a" : "") + " 2>&1; echo \"" + ExitMarker + "$?\"";
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions)
    {
        string[] lines = runner.RunCommand(ListCommand(allVersions));
        List<string> output = SplitExitStatus(lines, out int? exitStatus);
        LinuxPackageInfo[] packages = output == null ? new LinuxPackageInfo[0] : ParsePackageList(output);
        // An error of apt that still let it list what it could (the packages of the sources it could read) is no reason to show nothing.
        if ((exitStatus == 0 && output != null) || packages.Length > 0)
        {
            return packages;
        }
        // apt listed nothing: the installed packages are still in the database of dpkg, which needs no lists of the repositories. The
        // window opens with them, and Refresh (apt-get update) downloads the lists.
        string[] installed = runner.RunCommand(InstalledQueryCommand);
        LinuxPackageInfo[] fromDpkg = installed == null ? new LinuxPackageInfo[0] : ParseDpkgQuery(installed);
        if (fromDpkg.Length > 0)
        {
            return fromDpkg;
        }
        throw new Exception(BuildListError(output, exitStatus));
    }

    /// <summary>
    /// The output of <see cref="ListCommand"/> without its last line, and the exit status of "apt list" from it; null when the command
    /// did not run at all (the shell failed, the connection is gone), and a null status when the line is missing.
    /// </summary>
    public static List<string> SplitExitStatus(string[] lines, out int? exitStatus)
    {
        exitStatus = null;
        if (lines == null)
        {
            return null;
        }
        List<string> output = new List<string>(lines);
        for (int i = output.Count - 1; i >= 0; i--)
        {
            string line = output[i]?.Trim() ?? "";
            if (line.StartsWith(ExitMarker, StringComparison.Ordinal))
            {
                if (int.TryParse(line.Substring(ExitMarker.Length), out int status))
                {
                    exitStatus = status;
                }
                output.RemoveAt(i);
                break;
            }
        }
        return output;
    }

    /// <summary>The message when neither apt nor dpkg listed anything: what apt said (its E: and W: lines), and what to do.</summary>
    public static string BuildListError(List<string> output, int? exitStatus)
    {
        List<string> messages = (output ?? new List<string>())
            .Select(line => line?.Trim() ?? "")
            .Where(line => line.StartsWith("E:", StringComparison.Ordinal) || line.StartsWith("W:", StringComparison.Ordinal) || line.StartsWith("Err:", StringComparison.Ordinal))
            .Take(10)
            .ToList();
        string text = "Failed to list packages with apt" + (exitStatus.HasValue && exitStatus != 0 ? " (apt list exited with " + exitStatus + ")" : "") + ".";
        if (messages.Count > 0)
        {
            text += "\n\n" + string.Join("\n", messages);
        }
        return text + "\n\nRun \"apt-get update\" as root (or with sudo) in a terminal tab and open the package manager again.";
    }

    /// <summary>
    /// The installed packages from the output of <see cref="InstalledQueryCommand"/>. The second letter of the status is "i" for an
    /// installed package ("ii", "hi" when it is on hold); a removed one whose configuration is left ("rc") is no package to show.
    /// </summary>
    public static LinuxPackageInfo[] ParseDpkgQuery(IEnumerable<string> lines)
    {
        List<LinuxPackageInfo> packages = new List<LinuxPackageInfo>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines ?? new string[0])
        {
            string[] fields = (line ?? "").Split('\t');
            if (fields.Length < 4 || fields[0].Length == 0 || fields[3].Length < 2 || fields[3][1] != 'i')
            {
                continue;
            }
            // A package of two architectures (libc6 amd64 and i386) is one row for apt-get, which takes the name.
            if (!seen.Add(fields[0]))
            {
                continue;
            }
            packages.Add(new LinuxPackageInfo
            {
                ID = fields[0],
                Version = fields[1],
                State = LinuxPackageInfo.PackageState.Installed,
                RawState = "installed"
            });
        }
        return packages.ToArray();
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
            // A package of two architectures (libc6 amd64 and i386) is listed twice under one name; one row is enough
            // for apt-get, which takes the name. The installed one wins over the one that is not installed.
            int sameName = packagesList.FindIndex(existing => existing.ID == linuxPackageInfo.ID);
            if (sameName < 0)
            {
                packagesList.Add(linuxPackageInfo);
            }
            else if (packagesList[sameName].State == LinuxPackageInfo.PackageState.Available && linuxPackageInfo.State != LinuxPackageInfo.PackageState.Available)
            {
                packagesList[sameName] = linuxPackageInfo;
            }
        }
        return packagesList.ToArray();
    }
}
