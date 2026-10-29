using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY;
using VisualTTY;

// Checks of the winget client (Windows hosts): probing, the shell flavour, parsing of "winget list" / "winget search" tables
// and the generated command lines.
internal static class WingetTests
{
    private sealed class FakeRunner : ISSHCommandRunner
    {
        public readonly Dictionary<string, string[]> Responses = new Dictionary<string, string[]>();

        public readonly List<string> Commands = new List<string>();

        public string[] RunCommand(string command)
        {
            Commands.Add(command);
            return Responses.TryGetValue(command, out string[] lines) ? lines : null;
        }
    }

    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static readonly string[] ListOutput =
    {
        "\b-\b\\\b|\b \b",
        "Name                                                  Id                      Version        Available  Source",
        "----------------------------------------------------------------------------------------------------------------",
        "Git                                                   Git.Git                 2.45.1         2.46.0     winget",
        "Mozilla Firefox (x64)                                 Mozilla.Firefox         127.0                     winget",
        "Microsoft Visual C++ 2015-2022 Redistributable (x64)  ARP\\Machine\\X64\\{GUID}  14.38.33135.0",
        "Truncated na…                                         Some.Truncated.Id…      1.0                       winget",
        "2 upgrades available."
    };

    public static int Run()
    {
        WingetPackageManagerClient client = new WingetPackageManagerClient();

        FakeRunner runner = new FakeRunner();
        Check(client.Probe(runner) == 0, "winget: no winget (command fails) -> 0");
        runner.Responses["winget --version"] = new[] { "'winget' is not recognized as an internal or external command," };
        Check(client.Probe(runner) == 0, "winget: the error of the shell is not a version");
        runner.Responses["winget --version"] = new[] { "v1.9.25200" };
        runner.Responses["echo %OS% $env:OS"] = new[] { "Windows_NT $env:OS" };
        Check(client.Probe(runner) == PackageManagerSelector.MaxScore && client.Shell == WingetPackageManagerClient.ShellFlavour.Cmd, "winget: cmd.exe -> 1000, cmd flavour");
        runner.Responses["echo %OS% $env:OS"] = new[] { "%OS% Windows_NT" };
        client.Probe(runner);
        Check(client.Shell == WingetPackageManagerClient.ShellFlavour.PowerShell, "winget: PowerShell flavour");

        // Parsing: spinner noise, the "Available" column, an app without a source, a cut id, the footer.
        LinuxPackageInfo[] packages = WingetPackageManagerClient.ParsePackageTable(ListOutput, installed: true);
        Check(packages.Length == 3, "list: 3 usable rows (" + packages.Length + ")");
        LinuxPackageInfo git = packages.Single(p => p.ID == "Git.Git");
        Check(git.State == LinuxPackageInfo.PackageState.Upgradeable && git.Version == "2.46.0" && git.RawState == "upgradable from: 2.45.1" && git.Name == "Git", "list: Git has an update");
        LinuxPackageInfo firefox = packages.Single(p => p.ID == "Mozilla.Firefox");
        Check(firefox.State == LinuxPackageInfo.PackageState.Installed && firefox.Version == "127.0", "list: Firefox is installed without an update");
        Check(packages.Any(p => p.ID == "ARP\\Machine\\X64\\{GUID}" && p.Version == "14.38.33135.0"), "list: an app that winget did not install keeps its ARP id");

        string[] noUpdates =
        {
            "Name      Id        Version Source",
            "-----------------------------------",
            "Git       Git.Git   2.45.1  winget",
        };
        LinuxPackageInfo[] plain = WingetPackageManagerClient.ParsePackageTable(noUpdates, installed: true);
        Check(plain.Length == 1 && plain[0].State == LinuxPackageInfo.PackageState.Installed && plain[0].Version == "2.45.1", "list: a Source column is not taken for Available");
        string[] russian =
        {
            "Имя       Идентификатор  Версия  Доступно Источник",
            "---------------------------------------------------",
            "Git       Git.Git        2.45.1  2.46.0   winget",
        };
        Check(WingetPackageManagerClient.ParsePackageTable(russian, installed: true).Single().State == LinuxPackageInfo.PackageState.Upgradeable, "list: the headers of another language do not matter");
        string[] search =
        {
            "Name     Id          Version Match      Source",
            "------------------------------------------------",
            "7-Zip    7zip.7zip   24.08   Tag: 7zip  winget",
        };
        LinuxPackageInfo[] found = WingetPackageManagerClient.ParsePackageTable(search, installed: false);
        Check(found.Length == 1 && found[0].State == LinuxPackageInfo.PackageState.Available && found[0].Version == "24.08", "search: a row is a package that is not installed");
        Check(WingetPackageManagerClient.ParsePackageTable(new[] { "No installed package found matching input criteria." }, installed: true).Length == 0, "list: no table -> no packages");

        // The whole list: installed packages win over the catalog entry of the same id; the catalog is a bonus.
        FakeRunner full = new FakeRunner();
        Check(throws(() => new WingetPackageManagerClient().QueryAllPackages(full)), "query: a failing 'winget list' is an error");
        full.Responses["winget list --accept-source-agreements"] = ListOutput;
        Check(new WingetPackageManagerClient().QueryAllPackages(full).Length == 3, "query: the installed packages are listed without the catalog");
        full.Responses["winget search --source winget --accept-source-agreements"] = new[] { "Name   Id         Version Source", "--------------------------------", "Git    git.git    2.0     winget", "7-Zip  7zip.7zip  24.08   winget" };
        LinuxPackageInfo[] everything = new WingetPackageManagerClient().QueryAllPackages(full);
        Check(everything.Length == 4 && everything.Single(p => p.ID.Equals("Git.Git", StringComparison.OrdinalIgnoreCase)).State == LinuxPackageInfo.PackageState.Upgradeable, "query: the catalog adds packages and does not replace installed ones");

        // Command lines.
        LinuxPackageInfo[] two = { new LinuxPackageInfo { ID = "7zip.7zip", State = LinuxPackageInfo.PackageState.Available }, new LinuxPackageInfo { ID = "Git.Git", State = LinuxPackageInfo.PackageState.Upgradeable } };
        WingetPackageManagerClient cmd = new WingetPackageManagerClient();
        string install = cmd.GetCommandLineForPackageOperation(two, LinuxPackageInfo.PackageAction.Install, new PackageCommitOptions { NoPrompt = true });
        Check(install == "winget install --id \"7zip.7zip\" --exact --accept-package-agreements --accept-source-agreements --silent && winget upgrade --id \"Git.Git\" --exact --accept-package-agreements --accept-source-agreements --silent", "command: install and upgrade, chained for cmd.exe");
        Check(cmd.GetCommandLineForPackageOperation(two.Take(1).ToArray(), LinuxPackageInfo.PackageAction.Remove, null) == "winget uninstall --id \"7zip.7zip\" --exact --accept-source-agreements", "command: uninstall");
        Check(client.JoinCommands(new[] { "a", "b" }) == "a; b", "command: PowerShell chains with ';'");
        Check(cmd.GetCommandLineForPackageOperation(new[] { new LinuxPackageInfo { ID = "x\" & calc" } }, LinuxPackageInfo.PackageAction.Install, null) == null, "command: an id with shell metacharacters is refused");
        Check(cmd.GetCommandLineForPackageOperation(null, LinuxPackageInfo.PackageAction.Install, null) == null && cmd.GetCommandLineForIndexUpdate() == "winget source update", "command: nothing selected -> null; index update");

        // The refresh runs winget as it is, without sh and sudo.
        FakeRunner refresh = new FakeRunner();
        refresh.Responses["winget source update"] = new[] { "Done" };
        PackageIndexUpdater.Run(refresh, cmd, isRoot: true);
        Check(refresh.Commands.SequenceEqual(new[] { "winget source update" }), "refresh: the command is sent as it is");
        Check(throws(() => PackageIndexUpdater.Run(new FakeRunner(), cmd, isRoot: true)), "refresh: a failed command is an error");
        return _passed;
    }

    private static bool throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
