using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY;
using VisualTTY;

// Regression harness for the apt, yum and opkg package manager clients, the wrapped dnf rows, the listing of
// installed packages when the repositories cannot be reached and the click order of the package check boxes.
// Run with: dotnet run --project tests/ApkPackageManagerTests
internal static class PackageListFixesTests
{
    private sealed class FakeRunner : ISSHCommandRunner
    {
        public readonly Dictionary<string, string[]> Responses = new Dictionary<string, string[]>();

        public string[] RunCommand(string command)
        {
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
        DoAptChecks();
        DoYumChecks();
        DoDnfChecks();
        DoZypperChecks();
        DoOpkgChecks();
        DoToggleChecks();
        return _passed;
    }

    private static void DoAptChecks()
    {
        AptPackageManagerClient client = new AptPackageManagerClient();
        FakeRunner runner = new FakeRunner();
        runner.Responses["LC_ALL=C apt list"] = new[]
        {
            "WARNING: apt does not have a stable CLI interface. Use with caution in scripts.",
            "",
            "Listing...",
            "bash/noble,now 5.2.21-2ubuntu4 amd64 [installed]",
            "curl/noble-updates 8.5.0-2ubuntu10.6 amd64 [upgradable from: 8.5.0-2ubuntu10.5]",
            "htop/noble 3.3.0-4build1 amd64",
            "libc6/noble-updates,now 2.39-0ubuntu8.4 amd64 [installed,automatic]",
            "nano/noble-updates,now 7.2-2ubuntu0.1 amd64 [installed,upgradable to: 7.2-2ubuntu0.2]",
            "oldpkg/now 1.0-1 amd64 [installed,local]",
            "removed/noble 2.0-1 amd64 [residual-config]"
        };
        LinuxPackageInfo[] packages = client.QueryAllPackages(runner, false);
        Check(packages.Length == 7, "apt: the warning and 'Listing...' are skipped (" + packages.Length + ")");
        Check(Find(packages, "bash").State == LinuxPackageInfo.PackageState.Installed, "apt: installed package");
        Check(Find(packages, "libc6").State == LinuxPackageInfo.PackageState.Installed, "apt: '[installed,automatic]' is installed");
        Check(Find(packages, "oldpkg").State == LinuxPackageInfo.PackageState.Installed, "apt: '[installed,local]' is installed");
        Check(Find(packages, "htop").State == LinuxPackageInfo.PackageState.Available, "apt: available package");
        Check(Find(packages, "removed").State == LinuxPackageInfo.PackageState.Available, "apt: a removed package with its configuration left is not installed");
        LinuxPackageInfo curl = Find(packages, "curl");
        Check(curl.State == LinuxPackageInfo.PackageState.Upgradeable && curl.Version == "8.5.0-2ubuntu10.6", "apt: '[upgradable from: ...]' is an update");
        LinuxPackageInfo nano = Find(packages, "nano");
        Check(nano.State == LinuxPackageInfo.PackageState.Upgradeable, "apt: '[installed,upgradable to: ...]' is an update, not just installed");
        Check(nano.Version == "7.2-2ubuntu0.2" && nano.RawState == "upgradable from: 7.2-2ubuntu0.1", "apt: '[installed,upgradable to: ...]' shows the new version");

        bool failed = false;
        try
        {
            client.QueryAllPackages(new FakeRunner(), false);
        }
        catch (Exception)
        {
            failed = true;
        }
        Check(failed, "apt: QueryAllPackages throws when 'apt list' fails");
        Check(client.GetCommandLineForPackageOperation(null, LinuxPackageInfo.PackageAction.Install, null) == null, "apt: no packages -> null");
    }

    private static void DoYumChecks()
    {
        YumPackageManagerClient client = new YumPackageManagerClient();
        FakeRunner runner = new FakeRunner();
        runner.Responses["LC_ALL=C yum list installed"] = new[]
        {
            "Loaded plugins: fastestmirror",
            "Loading mirror speeds from cached hostfile",
            " * base: mirror.example.com",
            "Installed Packages",
            "bash.x86_64                          4.2.46-34.el7                  @base",
            "curl.x86_64                          7.29.0-59.el7                  @base",
            "a-very-long-package-name-that-does-not-fit.noarch",
            "                                     1.0-1.el7                      @epel"
        };
        runner.Responses["LC_ALL=C yum list available"] = new[]
        {
            "Loaded plugins: fastestmirror",
            "Available Packages",
            "curl.x86_64                          7.29.0-59.el7_9.2              updates",
            "htop.x86_64                          2.2.0-3.el7                    epel"
        };
        runner.Responses["LC_ALL=C yum list updates"] = new[]
        {
            "Loaded plugins: fastestmirror",
            "Updated Packages",
            "curl.x86_64                          7.29.0-59.el7_9.2              updates"
        };
        LinuxPackageInfo[] packages = client.QueryAllPackages(runner, false);
        Check(packages.Select(package => package.ID).SequenceEqual(new[] { "a-very-long-package-name-that-does-not-fit.noarch", "bash.x86_64", "curl.x86_64", "htop.x86_64" }), "yum: 'Loaded plugins' is not a package, wrapped rows are joined, no duplicates (" + string.Join(", ", packages.Select(package => package.ID)) + ")");
        Check(Find(packages, "a-very-long-package-name-that-does-not-fit.noarch").Version == "1.0-1.el7", "yum: a wrapped row keeps its version");
        LinuxPackageInfo curl = Find(packages, "curl.x86_64");
        Check(curl.State == LinuxPackageInfo.PackageState.Upgradeable && curl.Version == "7.29.0-59.el7_9.2" && curl.RawState == "upgradable from: 7.29.0-59.el7", "yum: update from 'yum list updates'");
        Check(Find(packages, "htop.x86_64").State == LinuxPackageInfo.PackageState.Available, "yum: available package");

        FakeRunner offline = new FakeRunner();
        offline.Responses["LC_ALL=C yum list installed"] = new[] { "bash.x86_64                          4.2.46-34.el7                  @base" };
        LinuxPackageInfo[] offlinePackages = client.QueryAllPackages(offline, false);
        Check(offlinePackages.Length == 1 && offlinePackages[0].State == LinuxPackageInfo.PackageState.Installed, "yum: unreachable repositories still give the installed packages");
        Check(client.GetCommandLineForPackageOperation(null, LinuxPackageInfo.PackageAction.Install, null) == null, "yum: no packages -> null");
    }

    private static void DoDnfChecks()
    {
        DnfPackageManagerClient client = new DnfPackageManagerClient();
        FakeRunner runner = new FakeRunner();
        runner.Responses["LC_ALL=C dnf -q list --installed"] = new[]
        {
            "Installed Packages",
            "bash.x86_64                         5.2.26-4.fc40                   @System",
            "python3-very-long-package-name-for-wrapping.noarch",
            "                                    1.2.3-1.fc40                    @fedora"
        };
        LinuxPackageInfo[] packages = client.QueryAllPackages(runner);
        Check(packages.Length == 2 && Find(packages, "python3-very-long-package-name-for-wrapping.noarch").Version == "1.2.3-1.fc40", "dnf: a wrapped row is joined back");
        Check(packages.All(package => package.State == LinuxPackageInfo.PackageState.Installed), "dnf: unreachable repositories still give the installed packages");
    }

    private static void DoZypperChecks()
    {
        ZypperPackageManagerClient client = new ZypperPackageManagerClient();
        FakeRunner runner = new FakeRunner();
        runner.Responses["LC_ALL=C zypper --non-interactive packages --installed-only"] = new[]
        {
            "S  | Repository | Name | Version    | Arch",
            "---+------------+------+------------+-------",
            "i+ | @System    | bash | 5.2.26-1.1 | x86_64"
        };
        LinuxPackageInfo[] packages = client.QueryAllPackages(runner);
        Check(packages.Length == 1 && packages[0].State == LinuxPackageInfo.PackageState.Installed, "zypper: unreachable repositories still give the installed packages");
    }

    private static void DoOpkgChecks()
    {
        OpkgPackageManagerClient client = new OpkgPackageManagerClient();
        FakeRunner runner = new FakeRunner();
        runner.Responses["opkg list-installed"] = new[]
        {
            "base-files - 1562-r24106-10cc5fcd00",
            "busybox - 1.36.1-1",
            "luci - git-24.086.45142-09d5a38"
        };
        runner.Responses["opkg list"] = new[]
        {
            "busybox - 1.36.1-1 - Core utilities for embedded Linux",
            "curl - 8.7.1-1 - A client-side URL transfer utility",
            "curl - 8.7.1-1 - A client-side URL transfer utility",
            " that is listed twice, once per feed",
            "tcpdump - 4.99.4-1 - Network monitoring and data acquisition tool - with a dash in the description"
        };
        runner.Responses["opkg list-upgradable"] = new[] { "busybox - 1.36.1-1 - 1.36.1-2" };
        LinuxPackageInfo[] packages = client.QueryAllPackages(runner);
        Check(packages.Select(package => package.ID).SequenceEqual(new[] { "base-files", "busybox", "curl", "luci", "tcpdump" }), "opkg: a package from several feeds is listed once (" + string.Join(", ", packages.Select(package => package.ID)) + ")");
        LinuxPackageInfo busybox = Find(packages, "busybox");
        Check(busybox.State == LinuxPackageInfo.PackageState.Upgradeable && busybox.Version == "1.36.1-2" && busybox.RawState == "upgradable from: 1.36.1-1", "opkg: update from 'opkg list-upgradable'");
        Check(Find(packages, "luci").State == LinuxPackageInfo.PackageState.Installed && Find(packages, "luci").Version == "git-24.086.45142-09d5a38", "opkg: installed package");
        Check(Find(packages, "tcpdump").Version == "4.99.4-1", "opkg: a dash in the description does not leak into the version");

        FakeRunner noLists = new FakeRunner();
        noLists.Responses["opkg list-installed"] = new[] { "busybox - 1.36.1-1" };
        LinuxPackageInfo[] installedOnly = client.QueryAllPackages(noLists);
        Check(installedOnly.Length == 1 && installedOnly[0].State == LinuxPackageInfo.PackageState.Installed, "opkg: without the package lists the installed packages are still listed");

        bool failed = false;
        try
        {
            client.QueryAllPackages(new FakeRunner());
        }
        catch (Exception)
        {
            failed = true;
        }
        Check(failed, "opkg: QueryAllPackages throws when 'opkg list-installed' fails (instead of showing everything as not installed)");
    }

    private static void DoToggleChecks()
    {
        // The check box of an upgradeable package starts as "keep as is" and the first click must ask for the upgrade.
        LinuxPackageInfo upgradeable = new LinuxPackageInfo { ID = "curl", State = LinuxPackageInfo.PackageState.Upgradeable };
        Check(upgradeable.Action == LinuxPackageInfo.PackageAction.None, "toggle: an update is kept as is by default");
        upgradeable.ToggleSelection();
        Check(upgradeable.Action == LinuxPackageInfo.PackageAction.Install, "toggle: the first click on an update upgrades it");
        upgradeable.ToggleSelection();
        Check(upgradeable.Action == LinuxPackageInfo.PackageAction.Remove, "toggle: the second click removes it");
        upgradeable.ToggleSelection();
        Check(upgradeable.Action == LinuxPackageInfo.PackageAction.None, "toggle: the third click keeps it as is again");

        LinuxPackageInfo installed = new LinuxPackageInfo { ID = "bash", State = LinuxPackageInfo.PackageState.Installed, IsChecked = true };
        installed.ToggleSelection();
        Check(installed.Action == LinuxPackageInfo.PackageAction.Remove, "toggle: an installed package is removed on click");
        LinuxPackageInfo available = new LinuxPackageInfo { ID = "htop", State = LinuxPackageInfo.PackageState.Available, IsChecked = false };
        available.ToggleSelection();
        Check(available.Action == LinuxPackageInfo.PackageAction.Install, "toggle: an available package is installed on click");
    }
}
