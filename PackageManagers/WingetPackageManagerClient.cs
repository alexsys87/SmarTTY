using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VisualTTY;

namespace SmarTTY;

/// <summary>
/// Package manager client for winget (Windows Package Manager) on a Windows host. The commands go to the default shell of the
/// SSH server, which is cmd.exe or PowerShell, not sh: nothing here is POSIX shell, and there is no sudo (a session of an
/// administrator is elevated already).
/// </summary>
public class WingetPackageManagerClient : ILinuxPackageManagerClient
{
    /// <summary>The shell that runs the commands, which decides how several commands are chained.</summary>
    public enum ShellFlavour
    {
        /// <summary>cmd.exe, the default shell of Win32-OpenSSH; also the assumption while the shell is not known.</summary>
        Cmd,

        /// <summary>Windows PowerShell 5.1 or PowerShell 7: the former has no "&amp;&amp;".</summary>
        PowerShell
    }

    // The agreements are accepted on the command line: a prompt for them could not be answered over the command channel.
    private const string SourceAgreements = " --accept-source-agreements";

    private const string PackageAgreements = " --accept-package-agreements";

    // Characters that cmd.exe or PowerShell would interpret inside a double-quoted package id. No real id has them.
    private static readonly char[] UnsafeIdCharacters = { '"', '&', '|', '<', '>', '^', '%', ';', '$', '`', '!' };

    /// <summary>The shell the last successful <see cref="Probe"/> found; chooses the separator of <see cref="JoinCommands"/>.</summary>
    public ShellFlavour Shell { get; private set; }

    public int Probe(ISSHCommandRunner runner)
    {
        // "winget --version" prints e.g. "v1.9.25200"; the error of a shell that does not know the command is not accepted.
        string[] output = runner.RunCommand("winget --version");
        if (output == null || !output.Any(line => IsVersionLine(CleanLine(line))))
        {
            return 0;
        }
        // The same command as the detection of a Windows host: cmd.exe expands %OS%, PowerShell expands $env:OS.
        string[] shellOutput = runner.RunCommand("echo %OS% $env:OS");
        Shell = shellOutput != null && shellOutput.Any(line => CleanLine(line).StartsWith("%OS%", StringComparison.Ordinal)) ? ShellFlavour.PowerShell : ShellFlavour.Cmd;
        return PackageManagerSelector.MaxScore;
    }

    public string GetCommandLineForIndexUpdate()
    {
        return "winget source update";
    }

    public LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions = false)
    {
        // "winget list" shows the installed packages of every source and the apps that were installed without winget;
        // the "Available" column of a package that has an update holds the new version.
        string[] installedLines = runner.RunCommand("winget list" + SourceAgreements) ?? throw new Exception("Failed to list packages with winget");
        Dictionary<string, LinuxPackageInfo> packages = new Dictionary<string, LinuxPackageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (LinuxPackageInfo package in ParsePackageTable(installedLines, installed: true))
        {
            if (!packages.ContainsKey(package.ID))
            {
                packages.Add(package.ID, package);
            }
        }
        // The catalog is a bonus only: when it cannot be read (no network, a winget that wants a query), the installed
        // packages are still listed.
        string[] catalogLines = runner.RunCommand("winget search --source winget" + SourceAgreements);
        if (catalogLines != null)
        {
            foreach (LinuxPackageInfo package in ParsePackageTable(catalogLines, installed: false))
            {
                if (!packages.ContainsKey(package.ID))
                {
                    packages.Add(package.ID, package);
                }
            }
        }
        List<LinuxPackageInfo> result = packages.Values.ToList();
        result.Sort((LinuxPackageInfo first, LinuxPackageInfo second) => string.Compare(first.Name, second.Name, StringComparison.OrdinalIgnoreCase));
        return result.ToArray();
    }

    public string GetCommandLineForPackageOperation(LinuxPackageInfo[] packages, LinuxPackageInfo.PackageAction action, PackageCommitOptions options)
    {
        if (packages == null || packages.Length == 0)
        {
            return null;
        }
        string silent = options != null && options.NoPrompt ? " --silent" : "";
        List<string> commands = new List<string>();
        foreach (LinuxPackageInfo package in packages)
        {
            if (!IsSafeId(package.ID))
            {
                continue;
            }
            string id = " --id \"" + package.ID + "\" --exact";
            switch (action)
            {
                case LinuxPackageInfo.PackageAction.Install:
                    // winget install of an installed package may refuse; "upgrade" is the verb of an update.
                    commands.Add((package.State == LinuxPackageInfo.PackageState.Upgradeable ? "winget upgrade" : "winget install") + id + PackageAgreements + SourceAgreements + silent);
                    break;
                case LinuxPackageInfo.PackageAction.Remove:
                    commands.Add("winget uninstall" + id + SourceAgreements + silent);
                    break;
                default:
                    return null;
            }
        }
        return commands.Count == 0 ? null : JoinCommands(commands);
    }

    /// <summary>
    /// Chains command lines for the shell of the host: "&amp;&amp;" in cmd.exe, ";" in PowerShell, where the Windows PowerShell 5.1 does not
    /// know "&amp;&amp;". winget takes one package per command line, so the packages are chained.
    /// </summary>
    public string JoinCommands(IEnumerable<string> commands)
    {
        return string.Join(Shell == ShellFlavour.PowerShell ? "; " : " && ", commands.Where(command => !string.IsNullOrEmpty(command)).ToArray());
    }

    /// <summary>True for an id that can be put into double quotes in cmd.exe and PowerShell as it is.</summary>
    public static bool IsSafeId(string id)
    {
        return !string.IsNullOrWhiteSpace(id) && id.IndexOfAny(UnsafeIdCharacters) < 0 && !id.Any(char.IsControl);
    }

    /// <summary>
    /// Parses the table of "winget list" (<paramref name="installed"/>: the state follows from the "Available" column) or of
    /// "winget search" (every row is a package that is not installed, as far as this list knows). The columns are found
    /// from the header line, which is the line above the row of dashes, so the language of winget does not matter.
    /// </summary>
    public static LinuxPackageInfo[] ParsePackageTable(IEnumerable<string> lines, bool installed)
    {
        List<string> cleaned = (lines ?? Enumerable.Empty<string>()).Select(CleanLine).ToList();
        List<LinuxPackageInfo> result = new List<LinuxPackageInfo>();
        for (int index = 1; index < cleaned.Count; index++)
        {
            if (!IsDashLine(cleaned[index]) || cleaned[index - 1].Trim().Length == 0)
            {
                continue;
            }
            // winget has five columns at most. Words of a header that are separated by one space are columns, unless that
            // gives more than five (a translated header of several words): then two spaces separate the columns.
            int[] starts = FindColumnStarts(cleaned[index - 1], 1);
            if (starts.Length > 5)
            {
                starts = FindColumnStarts(cleaned[index - 1], 2);
            }
            if (starts.Length < 3)
            {
                continue;
            }
            // Rows follow up to the first empty line; a further table (explicit upgrades) starts with its own header.
            List<string[]> rows = new List<string[]>();
            for (index++; index < cleaned.Count && cleaned[index].Trim().Length != 0; index++)
            {
                string[] cells = SplitRow(cleaned[index], starts);
                if (cells != null)
                {
                    rows.Add(cells);
                }
            }
            bool hasAvailable = installed && HasAvailableColumn(rows, starts.Length);
            foreach (string[] cells in rows)
            {
                LinuxPackageInfo package = ToPackage(cells, installed, hasAvailable);
                if (package != null)
                {
                    result.Add(package);
                }
            }
        }
        return result.ToArray();
    }

    private static LinuxPackageInfo ToPackage(string[] cells, bool installed, bool hasAvailable)
    {
        string id = cells[1];
        string version = cells[2];
        // A name cut short by winget ends with an ellipsis; an id cut short cannot be used in a command.
        if (id.IndexOf('…') >= 0 || !IsSafeId(id))
        {
            return null;
        }
        LinuxPackageInfo package = new LinuxPackageInfo
        {
            ID = id,
            UserFriendlyName = cells[0].Length == 0 || cells[0].IndexOf('…') >= 0 ? null : cells[0],
            Version = version,
            State = installed ? LinuxPackageInfo.PackageState.Installed : LinuxPackageInfo.PackageState.Available
        };
        if (installed && hasAvailable && cells.Length > 3 && cells[3].Length != 0)
        {
            package.State = LinuxPackageInfo.PackageState.Upgradeable;
            package.RawState = "upgradable from: " + version;
            package.Version = cells[3];
        }
        return package;
    }

    // Name, Id and Version come first. A fourth column is either "Available" (versions, so digits) or "Source" (words such as
    // "winget" and "msstore"): with five columns it is "Available" for sure.
    private static bool HasAvailableColumn(List<string[]> rows, int columnCount)
    {
        if (columnCount >= 5)
        {
            return true;
        }
        return columnCount == 4 && rows.Any(cells => cells[3].Any(char.IsDigit));
    }

    private static string[] SplitRow(string line, int[] starts)
    {
        // A row that does not line up with the header is the text under the table ("2 upgrades available.") or noise:
        // a real row has a space before each column.
        for (int column = 1; column < starts.Length; column++)
        {
            if (line.Length > starts[column] && line[starts[column] - 1] != ' ')
            {
                return null;
            }
        }
        if (line.Length <= starts[1])
        {
            return null;
        }
        string[] cells = new string[starts.Length];
        for (int column = 0; column < starts.Length; column++)
        {
            int begin = Math.Min(starts[column], line.Length);
            int end = column + 1 < starts.Length ? Math.Min(starts[column + 1], line.Length) : line.Length;
            cells[column] = line.Substring(begin, end - begin).Trim();
        }
        return cells;
    }

    private static int[] FindColumnStarts(string header, int gap)
    {
        List<int> starts = new List<int>();
        int spaces = gap;
        for (int position = 0; position < header.Length; position++)
        {
            if (header[position] == ' ')
            {
                spaces++;
            }
            else
            {
                if (spaces >= gap)
                {
                    starts.Add(position);
                }
                spaces = 0;
            }
        }
        return starts.ToArray();
    }

    private static bool IsDashLine(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length >= 3 && trimmed.All(character => character == '-');
    }

    private static bool IsVersionLine(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(1);
        }
        return trimmed.Length != 0 && char.IsDigit(trimmed[0]) && trimmed.All(character => char.IsDigit(character) || character == '.' || char.IsLetter(character) || character == '-');
    }

    /// <summary>
    /// Removes what the spinner of winget leaves in the output: backspaces erase the character before them, a carriage return
    /// in the middle of a line starts it over.
    /// </summary>
    internal static string CleanLine(string line)
    {
        if (line == null)
        {
            return "";
        }
        StringBuilder text = new StringBuilder();
        foreach (char character in line.TrimEnd('\r', '\n'))
        {
            if (character == '\b')
            {
                if (text.Length != 0)
                {
                    text.Length--;
                }
            }
            else if (character == '\r')
            {
                text.Clear();
            }
            else if (!char.IsControl(character) || character == '\t')
            {
                text.Append(character == '\t' ? ' ' : character);
            }
        }
        return text.ToString().TrimEnd();
    }
}
