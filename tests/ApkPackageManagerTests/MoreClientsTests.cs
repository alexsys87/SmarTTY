using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY;
using VisualTTY;

// Regression harness for the xbps (Void Linux), pkg (FreeBSD) and Homebrew package manager clients.
// Run with: dotnet run --project tests/ApkPackageManagerTests
internal static class MoreClientsTests
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

    private static LinuxPackageInfo Find(LinuxPackageInfo[] packages, string id)
    {
        return packages.Single(package => package.ID == id);
    }

    private static LinuxPackageInfo Package(string id, LinuxPackageInfo.PackageState state)
    {
        return new LinuxPackageInfo { ID = id, State = state };
    }

    public static int Run()
    {
        DoXbpsChecks();
        DoPkgChecks();
        DoHomebrewChecks();
        DoDetectionChecks();
        return _passed;
    }

    private static void DoXbpsChecks()
    {
        XbpsPackageManagerClient client = new XbpsPackageManagerClient();
        FakeRunner probeRunner = new FakeRunner();
        Check(client.Probe(probeRunner) == 0, "xbps: no binary -> 0");
        probeRunner.Responses["command -v xbps-install || which xbps-install"] = new[] { "/usr/bin/xbps-install" };
        Check(client.Probe(probeRunner) == PackageManagerSelector.MaxScore, "xbps: xbps-install found -> 1000");
        probeRunner.Responses["command -v xbps-install || which xbps-install"] = new[] { "sh: xbps-install: not found" };
        Check(client.Probe(probeRunner) == 0, "xbps: an error text that names the tool is not its path");
        probeRunner.Responses["command -v xbps-install || which xbps-install"] = new[] { "/usr/bin/true" };
        Check(client.Probe(probeRunner) == 0, "xbps: a foreign answer is not accepted");

        Check(client.GetCommandLineForIndexUpdate() == "xbps-install -S -y", "xbps: the index update");

        FakeRunner runner = new FakeRunner();
        Check(Throws(() => client.QueryAllPackages(runner)), "xbps: a failed listing of the installed packages is an error");
        runner.Responses["xbps-query -l"] = new[]
        {
            "ii base-system-0.114_2                  Void Linux base system meta-package",
            "ii bash-5.2.15_1                        GNU Bourne Again SHell",
            "ii gtk+3-3.24.38_1                      The GTK+ toolkit 3",
            "uu half-1.0_1                           Unpacked only",
            "",
            "garbage"
        };
        runner.Responses["xbps-query -Rs ''"] = new[]
        {
            "[*] bash-5.2.21_1                       GNU Bourne Again SHell",
            "[-] 0ad-0.0.26_1                        Free, real-time strategy game",
            "[*] gtk+3-3.24.38_1                     The GTK+ toolkit 3",
            "[-] htop-3.3.0_1                        Interactive process viewer"
        };
        runner.Responses["xbps-install -un"] = new[]
        {
            "bash-5.2.21_1 update x86_64 https://repo-default.voidlinux.org/current 1365027 321432",
            "libfoo-1.0_1 install x86_64 https://repo-default.voidlinux.org/current 100 100",
            "ghost-9.9_1 update x86_64 https://repo-default.voidlinux.org/current 100 100"
        };
        LinuxPackageInfo[] packages = client.QueryAllPackages(runner);
        Check(packages.Length == 6, "xbps: installed and available packages, one row per name (" + packages.Length + ")");
        LinuxPackageInfo bash = Find(packages, "bash");
        Check(bash.State == LinuxPackageInfo.PackageState.Upgradeable && bash.Version == "5.2.21_1" && bash.RawState == "upgradable from: 5.2.15_1", "xbps: bash has an update");
        Check(Find(packages, "gtk+3").State == LinuxPackageInfo.PackageState.Installed && Find(packages, "gtk+3").Version == "3.24.38_1", "xbps: a dash in the name does not break the version");
        Check(Find(packages, "0ad").State == LinuxPackageInfo.PackageState.Available && Find(packages, "htop").Version == "3.3.0_1", "xbps: a package of the repository is available");
        Check(Find(packages, "half").State == LinuxPackageInfo.PackageState.Installed, "xbps: an unpacked package is listed too");
        Check(packages.All(package => package.ID != "libfoo" && package.ID != "ghost"), "xbps: an update plan line of a package that is not installed adds nothing");
        Check(packages.Select(package => package.ID).SequenceEqual(packages.Select(package => package.ID).OrderBy(id => id, StringComparer.OrdinalIgnoreCase)), "xbps: the list is sorted");

        FakeRunner offline = new FakeRunner();
        offline.Responses["xbps-query -l"] = new[] { "ii bash-5.2.15_1  GNU Bourne Again SHell" };
        LinuxPackageInfo[] offlinePackages = client.QueryAllPackages(offline);
        Check(offlinePackages.Length == 1 && offlinePackages[0].State == LinuxPackageInfo.PackageState.Installed, "xbps: without the repository data the installed packages are still listed");

        PackageCommitOptions quiet = new PackageCommitOptions { NoPrompt = true };
        LinuxPackageInfo[] chosen = { Package("htop", LinuxPackageInfo.PackageState.Available), Package("bash", LinuxPackageInfo.PackageState.Upgradeable) };
        Check(client.GetCommandLineForPackageOperation(chosen, LinuxPackageInfo.PackageAction.Install, new PackageCommitOptions()) == "xbps-install htop && xbps-install -u bash", "xbps: install and upgrade are separate commands");
        Check(client.GetCommandLineForPackageOperation(chosen, LinuxPackageInfo.PackageAction.Install, quiet) == "xbps-install -y htop && xbps-install -u -y bash", "xbps: NoPrompt adds -y");
        Check(client.GetCommandLineForPackageOperation(chosen.Take(1).ToArray(), LinuxPackageInfo.PackageAction.Install, null) == "xbps-install htop", "xbps: only new packages -> one command, no options -> no flags");
        Check(client.GetCommandLineForPackageOperation(chosen, LinuxPackageInfo.PackageAction.Remove, quiet) == "xbps-remove -y htop bash", "xbps: remove");
        Check(client.GetCommandLineForPackageOperation(new LinuxPackageInfo[0], LinuxPackageInfo.PackageAction.Install, null) == null && client.GetCommandLineForPackageOperation(chosen, LinuxPackageInfo.PackageAction.None, null) == null, "xbps: nothing to do -> null");
        Check(XbpsPackageManagerClient.TrySplitPackageVersion("a-b-c-1.2_3", out string name, out string version) && name == "a-b-c" && version == "1.2_3", "xbps: the version is cut at the last dash");
        Check(!XbpsPackageManagerClient.TrySplitPackageVersion("nodash", out _, out _) && !XbpsPackageManagerClient.TrySplitPackageVersion("-1.0", out _, out _) && !XbpsPackageManagerClient.TrySplitPackageVersion("name-", out _, out _), "xbps: a name without a version is not a package");
    }

    private static void DoPkgChecks()
    {
        PkgPackageManagerClient client = new PkgPackageManagerClient();
        FakeRunner probeRunner = new FakeRunner();
        Check(client.Probe(probeRunner) == 0, "pkg: no binary -> 0");
        probeRunner.Responses["pkg -vv"] = new[] { "Version                 : 1.21.3", "PKG_DBDIR = \"/var/db/pkg\";", "ABI = \"FreeBSD:14:amd64\";" };
        Check(client.Probe(probeRunner) == PackageManagerSelector.MaxScore, "pkg: 'pkg -vv' of FreeBSD -> 1000");
        probeRunner.Responses["pkg -vv"] = new[] { "The package management tool is not yet installed on your system.", "Do you want to fetch and install it now? [y/N]:" };
        Check(client.Probe(probeRunner) == 0, "pkg: the bootstrap stub of a fresh FreeBSD is not a package manager yet");

        Check(client.GetCommandLineForIndexUpdate() == "pkg update", "pkg: the index update");

        FakeRunner runner = new FakeRunner();
        Check(Throws(() => client.QueryAllPackages(runner)), "pkg: a failed listing of the installed packages is an error");
        runner.Responses["pkg query '%n %v'"] = new[] { "bash 5.2.21", "py311-pip 23.3.1", "curl 8.7.1", "" };
        runner.Responses["pkg rquery '%n %v'"] = new[] { "bash 5.2.26", "curl 8.7.1", "git 2.45.2", "py311-pip 24.0" };
        runner.Responses["pkg version -vRl '<'"] = new[]
        {
            "bash-5.2.21                        <   needs updating (remote has 5.2.26)",
            "py311-pip-23.3.1                   <   needs updating (remote has 24.0)",
            "gone-1.0                           <   needs updating (remote has 2.0)"
        };
        LinuxPackageInfo[] packages = client.QueryAllPackages(runner);
        Check(packages.Length == 4, "pkg: installed and available packages, one row per name (" + packages.Length + ")");
        Check(Find(packages, "bash").State == LinuxPackageInfo.PackageState.Upgradeable && Find(packages, "bash").Version == "5.2.26" && Find(packages, "bash").RawState == "upgradable from: 5.2.21", "pkg: bash has an update");
        Check(Find(packages, "py311-pip").State == LinuxPackageInfo.PackageState.Upgradeable && Find(packages, "py311-pip").Version == "24.0", "pkg: a dash in the name does not break the update");
        Check(Find(packages, "curl").State == LinuxPackageInfo.PackageState.Installed && Find(packages, "git").State == LinuxPackageInfo.PackageState.Available && Find(packages, "git").Version == "2.45.2", "pkg: installed and available");
        Check(packages.All(package => package.ID != "gone"), "pkg: an update of a package that is not installed adds nothing");

        FakeRunner noRemote = new FakeRunner();
        noRemote.Responses["pkg query '%n %v'"] = new[] { "bash 5.2.21" };
        noRemote.Responses["pkg version -vRl '<'"] = new[] { "bash-5.2.21 < needs updating" };
        LinuxPackageInfo[] noRemotePackages = client.QueryAllPackages(noRemote);
        Check(noRemotePackages.Length == 1 && noRemotePackages[0].State == LinuxPackageInfo.PackageState.Upgradeable && noRemotePackages[0].Version == "5.2.21", "pkg: without the catalogue the installed packages are listed; an update line without the new version keeps the old one");

        PackageCommitOptions quiet = new PackageCommitOptions { NoPrompt = true };
        LinuxPackageInfo[] chosen = { Package("git", LinuxPackageInfo.PackageState.Available), Package("bash", LinuxPackageInfo.PackageState.Upgradeable) };
        Check(client.GetCommandLineForPackageOperation(chosen, LinuxPackageInfo.PackageAction.Install, new PackageCommitOptions()) == "pkg install git && pkg upgrade bash", "pkg: install and upgrade are separate commands");
        Check(client.GetCommandLineForPackageOperation(chosen, LinuxPackageInfo.PackageAction.Install, quiet) == "pkg install -y git && pkg upgrade -y bash", "pkg: NoPrompt adds -y");
        Check(client.GetCommandLineForPackageOperation(chosen, LinuxPackageInfo.PackageAction.Remove, quiet) == "pkg delete -y git bash", "pkg: remove");
        Check(client.GetCommandLineForPackageOperation(null, LinuxPackageInfo.PackageAction.Install, null) == null && client.GetCommandLineForPackageOperation(chosen, LinuxPackageInfo.PackageAction.None, null) == null, "pkg: nothing to do -> null");
    }

    private static void DoHomebrewChecks()
    {
        HomebrewPackageManagerClient client = new HomebrewPackageManagerClient();
        string brew = HomebrewPackageManagerClient.PathPrefix;
        string query = brew + "HOMEBREW_NO_AUTO_UPDATE=1 HOMEBREW_NO_ANALYTICS=1 ";
        FakeRunner probeRunner = new FakeRunner();
        Check(client.Probe(probeRunner) == 0, "brew: no binary -> 0");
        probeRunner.Responses[brew + "brew --version"] = new[] { "Homebrew 4.3.5", "Homebrew/homebrew-core (git revision 1234; last commit 2024-07-01)" };
        Check(client.Probe(probeRunner) == HomebrewPackageManagerClient.ProbeScore, "brew: 'brew --version' -> the Homebrew score");
        Check(HomebrewPackageManagerClient.ProbeScore < new YumPackageManagerClient().Probe(new FakeRunner { Responses = { ["command -v yum || which yum"] = new[] { "/usr/bin/yum" } } }), "brew: the score is below yum, so a system manager wins on a Linux host");
        probeRunner.Responses[brew + "brew --version"] = new[] { "zsh: command not found: brew" };
        Check(client.Probe(probeRunner) == 0, "brew: an error text is not Homebrew");
        Check(brew.Contains("/opt/homebrew/bin") && brew.Contains("/usr/local/bin") && brew.Contains("/home/linuxbrew/.linuxbrew/bin"), "brew: the PATH has the directories of Apple silicon, Intel and Linuxbrew");

        Check(client.GetCommandLineForIndexUpdate() == brew + "brew update", "brew: the index update finds brew on a Mac without its directory in PATH");

        FakeRunner runner = new FakeRunner();
        Check(Throws(() => client.QueryAllPackages(runner)), "brew: a failed listing of the installed packages is an error");
        runner.Responses[query + "brew list --formula --versions"] = new[] { "git 2.45.1", "python@3.12 3.12.3 3.12.4", "wget 1.24.5", "gtk+3 3.24.43", "" };
        runner.Responses[query + "brew formulae"] = new[] { "git", "htop", "python@3.12", "wget", "gtk+3", "bad name", "" };
        runner.Responses[query + "brew outdated --formula --verbose"] = new[]
        {
            "git (2.45.1) < 2.45.2",
            "python@3.12 (3.12.3, 3.12.4) < 3.12.5",
            "wget (1.24.5) < 1.25.0 [pinned at 1.24.5]",
            "ghost (1.0) < 2.0"
        };
        LinuxPackageInfo[] packages = client.QueryAllPackages(runner);
        Check(packages.Length == 5, "brew: installed and available formulae, one row per name (" + packages.Length + ")");
        Check(Find(packages, "git").State == LinuxPackageInfo.PackageState.Upgradeable && Find(packages, "git").Version == "2.45.2" && Find(packages, "git").RawState == "upgradable from: 2.45.1", "brew: git has an update");
        Check(Find(packages, "python@3.12").State == LinuxPackageInfo.PackageState.Upgradeable && Find(packages, "python@3.12").RawState == "upgradable from: 3.12.4", "brew: of several installed versions the newest is the old one");
        Check(Find(packages, "wget").State == LinuxPackageInfo.PackageState.Installed, "brew: a pinned formula is not offered for the update");
        Check(Find(packages, "gtk+3").Version == "3.24.43" && Find(packages, "htop").State == LinuxPackageInfo.PackageState.Available, "brew: '+' in a name; a formula that is not installed is available");
        Check(packages.All(package => package.ID != "ghost" && package.ID != "bad name"), "brew: an update of a formula that is not installed and a line that is no formula name add nothing");
        Check(runner.Commands.All(command => command.StartsWith(brew, StringComparison.Ordinal)) && runner.Commands.Skip(1).All(command => command.Contains("HOMEBREW_NO_AUTO_UPDATE=1")), "brew: every query runs with the PATH of Homebrew and without the automatic update");

        FakeRunner offline = new FakeRunner();
        offline.Responses[query + "brew list --formula --versions"] = new[] { "git 2.45.1" };
        LinuxPackageInfo[] offlinePackages = client.QueryAllPackages(offline);
        Check(offlinePackages.Length == 1 && offlinePackages[0].State == LinuxPackageInfo.PackageState.Installed, "brew: when the other queries fail, the installed formulae are still listed");

        LinuxPackageInfo[] chosen = { Package("htop", LinuxPackageInfo.PackageState.Available), Package("git", LinuxPackageInfo.PackageState.Upgradeable) };
        PackageCommitOptions quiet = new PackageCommitOptions { NoPrompt = true };
        Check(client.GetCommandLineForPackageOperation(chosen, LinuxPackageInfo.PackageAction.Install, quiet) == "brew install htop && brew upgrade git", "brew: install and upgrade are separate commands, NoPrompt changes nothing");
        Check(client.GetCommandLineForPackageOperation(chosen, LinuxPackageInfo.PackageAction.Remove, null) == "brew uninstall htop git", "brew: remove");
        Check(client.GetCommandLineForPackageOperation(null, LinuxPackageInfo.PackageAction.Install, null) == null && client.GetCommandLineForPackageOperation(chosen, LinuxPackageInfo.PackageAction.None, null) == null, "brew: nothing to do -> null");
    }

    private static void DoDetectionChecks()
    {
        ILinuxPackageManagerClient[] all =
        {
            new AptPackageManagerClient(), new DnfPackageManagerClient(), new ZypperPackageManagerClient(), new PacmanPackageManagerClient(),
            new PortagePackageManagerClient(), new YumPackageManagerClient(), new OpkgPackageManagerClient(), new ApkPackageManagerClient(),
            new XbpsPackageManagerClient(), new PkgPackageManagerClient(), new HomebrewPackageManagerClient()
        };
        string brew = HomebrewPackageManagerClient.PathPrefix + "brew --version";
        FakeRunner voidHost = new FakeRunner();
        voidHost.Responses["command -v xbps-install || which xbps-install"] = new[] { "/usr/bin/xbps-install" };
        Check(PackageManagerSelector.Pick(all, voidHost) is XbpsPackageManagerClient, "Selector: Void Linux -> xbps");
        FakeRunner freebsd = new FakeRunner();
        freebsd.Responses["pkg -vv"] = new[] { "PKG_DBDIR = \"/var/db/pkg\";" };
        Check(PackageManagerSelector.Pick(all, freebsd) is PkgPackageManagerClient, "Selector: FreeBSD -> pkg");
        FakeRunner mac = new FakeRunner();
        mac.Responses[brew] = new[] { "Homebrew 4.3.5" };
        Check(PackageManagerSelector.Pick(all, mac) is HomebrewPackageManagerClient, "Selector: a Mac with Homebrew -> Homebrew");
        FakeRunner ubuntuWithBrew = new FakeRunner();
        ubuntuWithBrew.Responses["apt -v"] = new[] { "apt 2.7.14 (amd64)" };
        ubuntuWithBrew.Responses[brew] = new[] { "Homebrew 4.3.5" };
        Check(PackageManagerSelector.Pick(all, ubuntuWithBrew) is AptPackageManagerClient && ubuntuWithBrew.Commands.Count == 1, "Selector: Linuxbrew next to apt -> apt, Homebrew is not even asked");
        FakeRunner yumWithBrew = new FakeRunner();
        yumWithBrew.Responses["command -v yum || which yum"] = new[] { "/usr/bin/yum" };
        yumWithBrew.Responses[brew] = new[] { "Homebrew 4.3.5" };
        Check(PackageManagerSelector.Pick(all, yumWithBrew) is YumPackageManagerClient, "Selector: Linuxbrew next to yum (500) -> yum");
        FakeRunner openwrt = new FakeRunner();
        openwrt.Responses["command -v opkg || which opkg"] = new[] { "/bin/opkg" };
        Check(PackageManagerSelector.Pick(all, openwrt) is OpkgPackageManagerClient, "Selector: OpenWrt with opkg is not taken for anything new");

        FakeRunner debian = new FakeRunner();
        debian.Responses["apt -v"] = new[] { "apt 2.6.1 (amd64)" };
        Check(new XbpsPackageManagerClient().Probe(debian) == 0 && new PkgPackageManagerClient().Probe(debian) == 0 && new HomebrewPackageManagerClient().Probe(debian) == 0, "the new clients stay silent on a Debian host");

        string supported = "apt, dnf, zypper, pacman, portage, yum, opkg, apk, xbps, pkg, homebrew";
        Check(PackageManagerSelector.BuildNotFoundMessage(new FakeRunner(), all) == "Could not detect the package manager on the remote host. Supported: " + supported + ".", "Not found: the message lists the new managers");
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch
        {
            return true;
        }
    }
}
