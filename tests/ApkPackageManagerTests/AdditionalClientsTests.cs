using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY;
using VisualTTY;

// Regression harness for the dnf, zypper, pacman and portage package manager clients.
// Run with: dotnet run --project tests/ApkPackageManagerTests
internal static class AdditionalClientsTests
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

    public static int Run()
    {
        DoDnfChecks();
        DoZypperChecks();
        DoPacmanChecks();
        DoPortageChecks();
        DoDetectionChecks();
        return _passed;
    }

    private static void DoDnfChecks()
    {
        DnfPackageManagerClient client = new DnfPackageManagerClient();
        FakeRunner probeRunner = new FakeRunner();
        Check(client.Probe(probeRunner) == 0, "dnf: no binary -> 0");
        probeRunner.Responses["dnf --version"] = new[] { "4.21.1", "  Installed: dnf-0:4.21.1-1.fc40.noarch at 2024-11-01T10:00:00Z", "  Built    : Fedora Project" };
        Check(client.Probe(probeRunner) == 1000, "dnf: 'dnf --version' -> 1000");
        probeRunner.Responses["dnf --version"] = new[] { "1.0.0" };
        Check(client.Probe(probeRunner) == 0, "dnf: a foreign binary named dnf is not accepted");

        FakeRunner listRunner = new FakeRunner();
        listRunner.Responses["dnf -q list --installed"] = new[]
        {
            "Installed Packages",
            "bash.x86_64                    5.2.26-4.fc40                    @System",
            "curl.x86_64                    8.9.1-2.fc40                     @System",
            "kernel.x86_64                  6.12.4-200.fc41                  @System"
        };
        listRunner.Responses["dnf -q list --available"] = new[]
        {
            "Available Packages",
            "bash.x86_64                    5.2.32-1.fc40                    updates",
            "htop.x86_64                    3.3.0-5.fc40                     fedora",
            "nano.x86_64                    8.1-3.fc40                       fedora"
        };
        listRunner.Responses["dnf -q list --upgrades"] = new[] { "bash.x86_64                    5.2.32-1.fc40                    updates" };
        LinuxPackageInfo[] packages = client.QueryAllPackages(listRunner);
        Check(packages.Length == 5, "dnf: header rows skipped, installed and available merged (" + packages.Length + ")");
        Check(packages.Select(package => package.ID).SequenceEqual(new[] { "bash.x86_64", "curl.x86_64", "htop.x86_64", "kernel.x86_64", "nano.x86_64" }), "dnf: sorted by name");
        Check(Find(packages, "bash.x86_64").State == LinuxPackageInfo.PackageState.Upgradeable, "dnf: upgradeable package marked");
        Check(Find(packages, "bash.x86_64").Version == "5.2.32-1.fc40", "dnf: upgrade shows the new version");
        Check(Find(packages, "bash.x86_64").RawState == "upgradable from: 5.2.26-4.fc40", "dnf: raw state names the installed version");
        Check(Find(packages, "curl.x86_64").State == LinuxPackageInfo.PackageState.Installed && Find(packages, "curl.x86_64").Version == "8.9.1-2.fc40", "dnf: installed package keeps its own row and version");
        Check(Find(packages, "kernel.x86_64").State == LinuxPackageInfo.PackageState.Installed, "dnf: kernel stays installed when it has no upgrade");
        Check(Find(packages, "htop.x86_64").State == LinuxPackageInfo.PackageState.Available && Find(packages, "htop.x86_64").Version == "3.3.0-5.fc40", "dnf: available package");

        FakeRunner noUpgradeInfoRunner = new FakeRunner();
        noUpgradeInfoRunner.Responses["dnf -q list --installed"] = new[] { "curl.x86_64                    8.9.1-2.fc40                    @System" };
        noUpgradeInfoRunner.Responses["dnf -q list --available"] = new[] { "htop.x86_64                    3.3.0-5.fc40                     fedora" };
        LinuxPackageInfo[] noUpgradeInfo = client.QueryAllPackages(noUpgradeInfoRunner);
        Check(noUpgradeInfo.Length == 2 && noUpgradeInfo.All(package => package.State != LinuxPackageInfo.PackageState.Upgradeable), "dnf: a failed 'list --upgrades' does not break the listing");

        bool failed = false;
        try
        {
            client.QueryAllPackages(new FakeRunner());
        }
        catch (Exception exception)
        {
            failed = exception.Message.Length > 0;
        }
        Check(failed, "dnf: QueryAllPackages throws when 'dnf list' fails");

        LinuxPackageInfo fresh = new LinuxPackageInfo { ID = "htop.x86_64", State = LinuxPackageInfo.PackageState.Available };
        LinuxPackageInfo freshToo = new LinuxPackageInfo { ID = "nano.x86_64", State = LinuxPackageInfo.PackageState.Available };
        LinuxPackageInfo upgradeable = new LinuxPackageInfo { ID = "bash.x86_64", State = LinuxPackageInfo.PackageState.Upgradeable };
        PackageCommitOptions ask = new PackageCommitOptions { NoPrompt = false };
        PackageCommitOptions noPrompt = new PackageCommitOptions { NoPrompt = true };
        Check(client.GetCommandLineForPackageOperation(new[] { fresh, freshToo }, LinuxPackageInfo.PackageAction.Install, noPrompt) == "dnf install -y htop.x86_64 nano.x86_64", "dnf: install without confirmation");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, LinuxPackageInfo.PackageAction.Install, ask) == "dnf install htop.x86_64", "dnf: install asks by default");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, LinuxPackageInfo.PackageAction.Install, null) == "dnf install htop.x86_64", "dnf: no options behave like asking");
        Check(client.GetCommandLineForPackageOperation(new[] { upgradeable }, LinuxPackageInfo.PackageAction.Install, noPrompt) == "dnf install -y bash.x86_64", "dnf: an upgrade is the same install command");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, LinuxPackageInfo.PackageAction.Remove, noPrompt) == "dnf remove -y htop.x86_64", "dnf: remove");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, LinuxPackageInfo.PackageAction.None, noPrompt) == null, "dnf: unsupported action -> null");
        Check(client.GetCommandLineForPackageOperation(new LinuxPackageInfo[0], LinuxPackageInfo.PackageAction.Install, noPrompt) == null, "dnf: empty selection -> null");
        Check(client.GetCommandLineForIndexUpdate() == "dnf -y makecache", "dnf: refresh command");

        string refresh = PackageIndexUpdater.BuildCommandLine(client.GetCommandLineForIndexUpdate(), isRoot: true);
        FakeRunner updateRunner = new FakeRunner();
        updateRunner.Responses[refresh] = new[] { "Metadata cache created.", "__SMARTTY_EXIT__=0" };
        PackageIndexUpdater.Run(updateRunner, client, isRoot: true);
        Check(updateRunner.Commands.SequenceEqual(new[] { refresh }), "dnf: refresh goes through PackageIndexUpdater");
    }

    private static void DoZypperChecks()
    {
        ZypperPackageManagerClient client = new ZypperPackageManagerClient();
        FakeRunner probeRunner = new FakeRunner();
        Check(client.Probe(probeRunner) == 0, "zypper: no binary -> 0");
        probeRunner.Responses["zypper --version"] = new[] { "zypper 1.14.68" };
        Check(client.Probe(probeRunner) == 1000, "zypper: 'zypper --version' -> 1000");
        probeRunner.Responses["zypper --version"] = new[] { "1.0.0" };
        Check(client.Probe(probeRunner) == 0, "zypper: a foreign binary named zypper is not accepted");

        FakeRunner listRunner = new FakeRunner();
        listRunner.Responses["zypper packages --installed-only"] = new[]
        {
            " S | Repository | Name | Version",
            "---+------------+------+--------",
            " i | @System    | bash | 5.2.26-1.1",
            " i | @System    | curl | 8.9.1-1.1"
        };
        listRunner.Responses["zypper packages --not-installed-only"] = new[]
        {
            " S | Repository | Name | Version",
            "---+------------+------+--------",
            "   | Main       | curl | 8.9.1-1.1",
            "   | Main       | htop | 3.3.0-1.1"
        };
        listRunner.Responses["zypper list-updates"] = new[]
        {
            " S | Repository | Name | Current Version | Available Version",
            "---+------------+------+-----------------+-------------------",
            " v | Update     | bash | 5.2.26-1.1      | 5.2.32-1.1",
            " v | Update     | curl | 8.9.1-1.1       | 8.10.1-1.1",
            "   | (empty)    |      |                 |"
        };
        LinuxPackageInfo[] packages = client.QueryAllPackages(listRunner);
        Check(packages.Length == 3, "zypper: table rows merged, headers skipped (" + packages.Length + ")");
        Check(packages.Select(package => package.ID).SequenceEqual(new[] { "bash", "curl", "htop" }), "zypper: sorted by name");
        Check(Find(packages, "bash").State == LinuxPackageInfo.PackageState.Upgradeable && Find(packages, "bash").Version == "5.2.32-1.1", "zypper: update from 'list-updates'");
        Check(Find(packages, "bash").RawState == "upgradable from: 5.2.26-1.1", "zypper: raw state names the installed version");
        Check(Find(packages, "curl").State == LinuxPackageInfo.PackageState.Upgradeable && Find(packages, "curl").Version == "8.10.1-1.1", "zypper: a package in both repositories and in updates stays one row");
        Check(Find(packages, "htop").State == LinuxPackageInfo.PackageState.Available && Find(packages, "htop").Version == "3.3.0-1.1", "zypper: available package");

        bool failed = false;
        try
        {
            client.QueryAllPackages(new FakeRunner());
        }
        catch (Exception exception)
        {
            failed = exception.Message.Length > 0;
        }
        Check(failed, "zypper: QueryAllPackages throws when the listing fails");

        LinuxPackageInfo fresh = new LinuxPackageInfo { ID = "htop", State = LinuxPackageInfo.PackageState.Available };
        LinuxPackageInfo upgradeable = new LinuxPackageInfo { ID = "bash", State = LinuxPackageInfo.PackageState.Upgradeable };
        PackageCommitOptions ask = new PackageCommitOptions { NoPrompt = false };
        PackageCommitOptions noPrompt = new PackageCommitOptions { NoPrompt = true };
        Check(client.GetCommandLineForPackageOperation(new[] { fresh, upgradeable }, LinuxPackageInfo.PackageAction.Install, ask) == "zypper install htop bash", "zypper: install asks by default");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, LinuxPackageInfo.PackageAction.Install, noPrompt) == "zypper --non-interactive install htop", "zypper: install without confirmation");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, LinuxPackageInfo.PackageAction.Remove, noPrompt) == "zypper --non-interactive remove htop", "zypper: remove");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, LinuxPackageInfo.PackageAction.None, noPrompt) == null, "zypper: unsupported action -> null");
        Check(client.GetCommandLineForPackageOperation(new LinuxPackageInfo[0], LinuxPackageInfo.PackageAction.Install, noPrompt) == null, "zypper: empty selection -> null");
        Check(client.GetCommandLineForIndexUpdate() == "zypper refresh", "zypper: refresh command");

        string refresh = PackageIndexUpdater.BuildCommandLine(client.GetCommandLineForIndexUpdate(), isRoot: false);
        FakeRunner updateRunner = new FakeRunner();
        updateRunner.Responses[refresh] = new[] { "Retrieving repository 'Main' metadata", "All repositories have been refreshed.", "__SMARTTY_EXIT__=0" };
        PackageIndexUpdater.Run(updateRunner, client, isRoot: false);
        Check(updateRunner.Commands.SequenceEqual(new[] { refresh }), "zypper: refresh as another user goes through 'sudo -n'");
    }

    private static void DoPacmanChecks()
    {
        PacmanPackageManagerClient client = new PacmanPackageManagerClient();
        FakeRunner probeRunner = new FakeRunner();
        Check(client.Probe(probeRunner) == 0, "pacman: no binary -> 0");
        probeRunner.Responses["pacman --version"] = new[] { "Pacman v6.1.0 - libalpm v14.1.0", "Copyright (C) 2006-2024 Pacman Development Team" };
        Check(client.Probe(probeRunner) == 1000, "pacman: 'pacman --version' -> 1000");
        probeRunner.Responses["pacman --version"] = new[] { "1.0.0" };
        Check(client.Probe(probeRunner) == 0, "pacman: a foreign binary named pacman is not accepted");

        FakeRunner listRunner = new FakeRunner();
        listRunner.Responses["pacman -Q"] = new[] { "bash 5.2.037-1", "curl 8.11.1-1", "linux 6.12.4.arch1-1" };
        listRunner.Responses["pacman -Sl"] = new[]
        {
            "core bash 5.2.037-1 [installed]",
            "core curl 8.11.1-1 [installed: 8.11.0-1]",
            "extra htop 3.3.0-1",
            "extra nano 8.2-1",
            "multilib lib32-glibc 2.40+r16+gaa533d58ff-2 [installed]"
        };
        listRunner.Responses["pacman -Qu"] = new[] { "bash 5.2.037-1 -> 5.2.037-2", "curl 8.11.1-1 -> 8.12.0-1" };
        LinuxPackageInfo[] packages = client.QueryAllPackages(listRunner);
        Check(packages.Length == 6, "pacman: sync repositories merged with the installed ones (" + packages.Length + ")");
        Check(packages.Select(package => package.ID).SequenceEqual(new[] { "bash", "curl", "htop", "lib32-glibc", "linux", "nano" }), "pacman: sorted by name, brackets stripped");
        Check(Find(packages, "bash").State == LinuxPackageInfo.PackageState.Upgradeable && Find(packages, "bash").Version == "5.2.037-2", "pacman: update from 'pacman -Qu'");
        Check(Find(packages, "bash").RawState == "upgradable from: 5.2.037-1", "pacman: raw state names the installed version");
        Check(Find(packages, "curl").State == LinuxPackageInfo.PackageState.Upgradeable && Find(packages, "curl").Version == "8.12.0-1", "pacman: '[installed: ...]' marks do not leak into the id");
        Check(Find(packages, "linux").State == LinuxPackageInfo.PackageState.Installed, "pacman: an installed package with no update stays installed");
        Check(Find(packages, "htop").State == LinuxPackageInfo.PackageState.Available && Find(packages, "htop").Version == "3.3.0-1", "pacman: available package");
        Check(Find(packages, "lib32-glibc").State == LinuxPackageInfo.PackageState.Available && Find(packages, "lib32-glibc").Version == "2.40+r16+gaa533d58ff-2", "pacman: a package from a secondary repository is listed ('pacman -Q' stays authoritative)");

        FakeRunner noSyncRunner = new FakeRunner();
        noSyncRunner.Responses["pacman -Q"] = new[] { "bash 5.2.037-1" };
        LinuxPackageInfo[] noSync = client.QueryAllPackages(noSyncRunner);
        Check(noSync.Length == 1 && noSync[0].State == LinuxPackageInfo.PackageState.Installed, "pacman: missing sync databases still give the installed packages");

        FakeRunner noUpdatesRunner = new FakeRunner();
        noUpdatesRunner.Responses["pacman -Q"] = new[] { "bash 5.2.037-1" };
        noUpdatesRunner.Responses["pacman -Sl"] = new[] { "core bash 5.2.037-1 [installed]" };
        Check(client.QueryAllPackages(noUpdatesRunner)[0].State == LinuxPackageInfo.PackageState.Installed, "pacman: 'pacman -Qu' failing with exit code 1 means 'no updates'");

        bool failed = false;
        try
        {
            client.QueryAllPackages(new FakeRunner());
        }
        catch (Exception exception)
        {
            failed = exception.Message.Length > 0;
        }
        Check(failed, "pacman: QueryAllPackages throws when 'pacman -Q' fails");

        LinuxPackageInfo fresh = new LinuxPackageInfo { ID = "htop", State = LinuxPackageInfo.PackageState.Available };
        LinuxPackageInfo freshToo = new LinuxPackageInfo { ID = "nano", State = LinuxPackageInfo.PackageState.Available };
        LinuxPackageInfo installed = new LinuxPackageInfo { ID = "linux", State = LinuxPackageInfo.PackageState.Installed };
        PackageCommitOptions ask = new PackageCommitOptions { NoPrompt = false };
        PackageCommitOptions noPrompt = new PackageCommitOptions { NoPrompt = true };
        Check(client.GetCommandLineForPackageOperation(new[] { fresh, freshToo }, LinuxPackageInfo.PackageAction.Install, noPrompt) == "pacman -S --noconfirm htop nano", "pacman: install without confirmation");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, LinuxPackageInfo.PackageAction.Install, ask) == "pacman -S htop", "pacman: install asks by default");
        Check(client.GetCommandLineForPackageOperation(new[] { installed }, LinuxPackageInfo.PackageAction.Remove, noPrompt) == "pacman -R --noconfirm linux", "pacman: remove");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, LinuxPackageInfo.PackageAction.None, noPrompt) == null, "pacman: unsupported action -> null");
        Check(client.GetCommandLineForPackageOperation(new LinuxPackageInfo[0], LinuxPackageInfo.PackageAction.Install, noPrompt) == null, "pacman: empty selection -> null");
        Check(client.GetCommandLineForIndexUpdate() == "pacman -Sy", "pacman: refresh command");

        string refresh = PackageIndexUpdater.BuildCommandLine(client.GetCommandLineForIndexUpdate(), isRoot: false);
        Check(refresh == "{ sudo -n pacman -Sy; } 2>&1; echo __SMARTTY_EXIT__=$?", "pacman: refresh as another user goes through 'sudo -n'");
        FakeRunner updateRunner = new FakeRunner();
        updateRunner.Responses[refresh] = new[] { ":: Synchronizing package databases...", "error: failed retrieving file 'core.db'", "__SMARTTY_EXIT__=1" };
        string failure = null;
        try
        {
            PackageIndexUpdater.Run(updateRunner, client, isRoot: false);
        }
        catch (Exception exception)
        {
            failure = exception.Message;
        }
        Check(failure != null && failure.Contains("exit code 1") && failure.Contains("core.db") && !failure.Contains("__SMARTTY_EXIT__"), "pacman: failed refresh reports the output without the exit marker");
    }

    private static void DoPortageChecks()
    {
        PortagePackageManagerClient client = new PortagePackageManagerClient();
        FakeRunner probeRunner = new FakeRunner();
        Check(client.Probe(probeRunner) == 0, "portage: no binary -> 0");
        probeRunner.Responses["emerge --version"] = new[] { "Portage 3.0.66 (python 3.12.8-final-0, default/linux/amd64/23.0, gcc-14, glibc-2.40-r5, 6.12.9-gentoo x86_64)" };
        Check(client.Probe(probeRunner) == 1000, "portage: 'emerge --version' -> 1000");
        probeRunner.Responses["emerge --version"] = new[] { "emerge 1.0.0" };
        Check(client.Probe(probeRunner) == 0, "portage: a foreign binary named emerge is not accepted");

        FakeRunner listRunner = new FakeRunner();
        listRunner.Responses["portageq match / '*/*'"] = new[]
        {
            "app-admin/sudo-1.9.16_p5",
            "dev-lang/python-3.12.8",
            "dev-libs/glib-2.80.0",
            "dev-python/beautifulsoup4-4.12.3",
            "x11-libs/gtk+-3.24.38"
        };
        listRunner.Responses["portageq all_best_visible /"] = new[]
        {
            "app-admin/sudo-1.9.16_p5",
            "app-editors/vim-9.1.0",
            "dev-lang/python-3.13.1",
            "dev-libs/glib-2.82.1",
            "dev-python/beautifulsoup4-4.12.3",
            "x11-libs/gtk+-3.24.38"
        };
        listRunner.Responses["emerge -uDNp @world"] = new[]
        {
            "Calculating dependencies... done!",
            "",
            "[ebuild     U  ] dev-lang/python-3.13.1:3.13::gentoo [3.13.0:3.13::gentoo] USE=\"-test\"",
            "[ebuild     U  ] dev-libs/glib-2.82.1:0/2::gentoo [2.80.0:0/2::gentoo] USE=\"-test\"",
            "[ebuild  N     ] app-misc/hello-2.12.1::gentoo USE=\"nls\"",
            "[ebuild   R    ] sys-apps/baselayout-2.15::gentoo ",
            "[ebuild  UD    ] app-misc/oldstuff-1.0::gentoo [2.0] ",
            "",
            "The following USE changes are necessary:",
            "    # required by glib (argument)"
        };
        LinuxPackageInfo[] packages = client.QueryAllPackages(listRunner);
        Check(packages.Length == 6, "portage: installed and available packages merged (" + packages.Length + ")");
        Check(packages.Select(package => package.ID).SequenceEqual(new[] { "app-admin/sudo", "app-editors/vim", "dev-lang/python", "dev-libs/glib", "dev-python/beautifulsoup4", "x11-libs/gtk+" }), "portage: category/package ids, sorted by name");
        Check(Find(packages, "app-admin/sudo").State == LinuxPackageInfo.PackageState.Installed && Find(packages, "app-admin/sudo").Version == "1.9.16_p5", "portage: installed package");
        Check(Find(packages, "app-editors/vim").State == LinuxPackageInfo.PackageState.Available && Find(packages, "app-editors/vim").Version == "9.1.0", "portage: available package");
        Check(Find(packages, "dev-libs/glib").State == LinuxPackageInfo.PackageState.Upgradeable && Find(packages, "dev-libs/glib").Version == "2.82.1", "portage: upgrade from 'emerge --pretend'");
        Check(Find(packages, "dev-libs/glib").RawState == "upgradable from: 2.80.0", "portage: raw state names the installed version");
        Check(Find(packages, "dev-lang/python").State == LinuxPackageInfo.PackageState.Upgradeable && Find(packages, "dev-lang/python").RawState == "upgradable from: 3.13.0", "portage: the slot and the repository are dropped from the versions");
        Check(Find(packages, "dev-python/beautifulsoup4").Version == "4.12.3", "portage: a package name with a digit is split correctly");
        Check(Find(packages, "x11-libs/gtk+").Version == "3.24.38", "portage: a package name with '+' is split correctly");
        Check(packages.All(package => package.ID != "sys-apps/baselayout"), "portage: a rebuild is not an update");
        Check(packages.All(package => package.ID != "app-misc/oldstuff"), "portage: a downgrade is not an update");

        FakeRunner installedRunner = new FakeRunner();
        installedRunner.Responses["portageq match / '*/*'"] = new[] { "app-admin/sudo-1.9.16_p5" };
        LinuxPackageInfo[] installedOnly = client.QueryAllPackages(installedRunner);
        Check(installedOnly.Length == 1 && installedOnly[0].State == LinuxPackageInfo.PackageState.Installed, "portage: a missing ebuild tree still gives the installed packages");

        FakeRunner noUpdatesRunner = new FakeRunner();
        noUpdatesRunner.Responses["portageq match / '*/*'"] = new[] { "app-admin/sudo-1.9.16_p5" };
        noUpdatesRunner.Responses["portageq all_best_visible /"] = new[] { "app-admin/sudo-1.9.16_p5" };
        Check(client.QueryAllPackages(noUpdatesRunner)[0].State == LinuxPackageInfo.PackageState.Installed, "portage: a failing 'emerge -uDNp @world' leaves the packages installed");

        bool failed = false;
        try
        {
            client.QueryAllPackages(new FakeRunner());
        }
        catch (Exception exception)
        {
            failed = exception.Message.Length > 0;
        }
        Check(failed, "portage: QueryAllPackages throws when the listing fails");

        LinuxPackageInfo fresh = new LinuxPackageInfo { ID = "app-misc/hello", State = LinuxPackageInfo.PackageState.Available };
        LinuxPackageInfo freshToo = new LinuxPackageInfo { ID = "app-editors/vim", State = LinuxPackageInfo.PackageState.Available };
        LinuxPackageInfo upgradeable = new LinuxPackageInfo { ID = "dev-libs/glib", State = LinuxPackageInfo.PackageState.Upgradeable };
        LinuxPackageInfo installed = new LinuxPackageInfo { ID = "app-admin/sudo", State = LinuxPackageInfo.PackageState.Installed };
        PackageCommitOptions ask = new PackageCommitOptions { NoPrompt = false };
        PackageCommitOptions noPrompt = new PackageCommitOptions { NoPrompt = true };
        Check(client.GetCommandLineForPackageOperation(new[] { fresh, freshToo }, LinuxPackageInfo.PackageAction.Install, noPrompt) == "emerge --ask=n app-misc/hello app-editors/vim", "portage: install without confirmation");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, LinuxPackageInfo.PackageAction.Install, ask) == "emerge app-misc/hello", "portage: install asks by default");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, LinuxPackageInfo.PackageAction.Install, null) == "emerge app-misc/hello", "portage: install without options asks by default");
        Check(client.GetCommandLineForPackageOperation(new[] { upgradeable }, LinuxPackageInfo.PackageAction.Install, noPrompt) == "emerge --ask=n dev-libs/glib", "portage: upgrade keeps the category");
        Check(client.GetCommandLineForPackageOperation(new[] { installed }, LinuxPackageInfo.PackageAction.Remove, noPrompt) == "emerge --ask=n --unmerge app-admin/sudo", "portage: remove without confirmation");
        Check(client.GetCommandLineForPackageOperation(new[] { installed }, LinuxPackageInfo.PackageAction.Remove, ask) == "emerge --unmerge app-admin/sudo", "portage: remove asks by default");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, LinuxPackageInfo.PackageAction.None, noPrompt) == null, "portage: unsupported action -> null");
        Check(client.GetCommandLineForPackageOperation(new LinuxPackageInfo[0], LinuxPackageInfo.PackageAction.Install, noPrompt) == null, "portage: empty selection -> null");
        Check(client.GetCommandLineForIndexUpdate() == "emerge --sync", "portage: refresh command");

        string refresh = PackageIndexUpdater.BuildCommandLine(client.GetCommandLineForIndexUpdate(), isRoot: true);
        Check(refresh == "{ emerge --sync; } 2>&1; echo __SMARTTY_EXIT__=$?", "portage: refresh goes through the exit marker");
        FakeRunner updateRunner = new FakeRunner();
        updateRunner.Responses[refresh] = new[] { ">>> Synchronizing repository 'gentoo'...", "!!! Repository 'gentoo' is missing a master: repositories.xml", "__SMARTTY_EXIT__=1" };
        string failure = null;
        try
        {
            PackageIndexUpdater.Run(updateRunner, client, isRoot: true);
        }
        catch (Exception exception)
        {
            failure = exception.Message;
        }
        Check(failure != null && failure.Contains("exit code 1") && failure.Contains("repositories.xml") && !failure.Contains("__SMARTTY_EXIT__"), "portage: failed refresh reports the output without the exit marker");
    }

    private static void DoDetectionChecks()
    {
        FakeRunner bothRunner = new FakeRunner();
        bothRunner.Responses["dnf --version"] = new[] { "4.21.1", "  Installed: dnf-0:4.21.1-1.fc40.noarch at 2024-11-01T10:00:00Z" };
        bothRunner.Responses["which yum"] = new[] { "/usr/bin/yum" };
        Check(new DnfPackageManagerClient().Probe(bothRunner) > new YumPackageManagerClient().Probe(bothRunner), "dnf wins over yum where yum is a symlink to dnf");

        FakeRunner archRunner = new FakeRunner();
        archRunner.Responses["which yum"] = new[] { "/usr/bin/yum" };
        archRunner.Responses["pacman --version"] = new[] { "Pacman v6.1.0 - libalpm v14.1.0" };
        Check(new PacmanPackageManagerClient().Probe(archRunner) > new YumPackageManagerClient().Probe(archRunner), "pacman wins over a stray yum package");

        FakeRunner aptRunner = new FakeRunner();
        aptRunner.Responses["apt -v"] = new[] { "apt 2.7.14 (amd64)" };
        Check(new DnfPackageManagerClient().Probe(aptRunner) == 0 && new ZypperPackageManagerClient().Probe(aptRunner) == 0 && new PacmanPackageManagerClient().Probe(aptRunner) == 0 && new PortagePackageManagerClient().Probe(aptRunner) == 0, "the new clients stay silent on a Debian host");

        FakeRunner gentooRunner = new FakeRunner();
        gentooRunner.Responses["emerge --version"] = new[] { "Portage 3.0.66 (python 3.12.8-final-0, default/linux/amd64/23.0, gcc-14, glibc-2.40-r5, 6.12.9-gentoo x86_64)" };
        gentooRunner.Responses["which yum"] = new[] { "/usr/bin/yum" };
        Check(new PortagePackageManagerClient().Probe(gentooRunner) > new YumPackageManagerClient().Probe(gentooRunner), "portage wins over a stray yum package");
        Check(new PortagePackageManagerClient().Probe(gentooRunner) == 1000 && new DnfPackageManagerClient().Probe(gentooRunner) == 0 && new AptPackageManagerClient().Probe(gentooRunner) == 0 && new OpkgPackageManagerClient().Probe(gentooRunner) == 0 && new ApkPackageManagerClient().Probe(gentooRunner) == 0, "portage is the only client on a Gentoo host");
    }
}
