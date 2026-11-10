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
        runner.Responses[AptPackageManagerClient.ListCommand(false)] = new[]
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
            "removed/noble 2.0-1 amd64 [residual-config]",
            AptPackageManagerClient.ExitMarker + "0"
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

        DoAptFailureChecks(client);
        Check(client.GetCommandLineForPackageOperation(null, LinuxPackageInfo.PackageAction.Install, null) == null, "apt: no packages -> null");
    }

    // Debian 12 before "apt update": "apt list" failed, and the window closed with "Failed to list packages with apt" before Refresh
    // could be pressed, without what apt said.
    private static void DoAptFailureChecks(AptPackageManagerClient client)
    {
        Check(AptPackageManagerClient.ListCommand(false) == "LC_ALL=C apt list 2>&1; echo \"" + AptPackageManagerClient.ExitMarker + "$?\"", "apt: the list keeps the messages and the exit status of apt");
        Check(AptPackageManagerClient.ListCommand(true).StartsWith("LC_ALL=C apt list -a 2>&1;", StringComparison.Ordinal), "apt: all versions with -a");

        List<string> split = AptPackageManagerClient.SplitExitStatus(new[] { "Listing...", "E: x", AptPackageManagerClient.ExitMarker + "100" }, out int? status);
        Check(status == 100 && split.Count == 2 && split[1] == "E: x", "apt: the exit status line is read and taken off");
        Check(AptPackageManagerClient.SplitExitStatus(new[] { "Listing..." }, out status) != null && status == null, "apt: no exit status line -> unknown status");
        Check(AptPackageManagerClient.SplitExitStatus(null, out status) == null && status == null, "apt: the command did not run -> null");

        string[] aptError =
        {
            "WARNING: apt does not have a stable CLI interface. Use with caution in scripts.",
            "",
            "Listing...",
            "E: Could not open file /var/lib/apt/lists/deb.debian.org_debian_dists_bookworm_main_binary-amd64_Packages - open (2: No such file or directory)",
            AptPackageManagerClient.ExitMarker + "100"
        };
        string[] dpkg =
        {
            "bash\t5.2.15-2+b7\tamd64\tii ",
            "libc6\t2.36-9+deb12u9\tamd64\tii ",
            "libc6\t2.36-9+deb12u9\ti386\tii ",
            "openssh-server\t1:9.2p1-2+deb12u5\tamd64\thi ",
            "oldconf\t1.0-1\tall\trc ",
            "halfdone\t2.0\tall\tiU ",
            "garbage line"
        };
        FakeRunner debian = new FakeRunner();
        debian.Responses[AptPackageManagerClient.ListCommand(false)] = aptError;
        debian.Responses[AptPackageManagerClient.InstalledQueryCommand] = dpkg;
        LinuxPackageInfo[] installed = client.QueryAllPackages(debian, false);
        Check(installed.Select(p => p.ID).SequenceEqual(new[] { "bash", "libc6", "openssh-server" }), "apt failed: the installed packages come from dpkg (" + string.Join(" ", installed.Select(p => p.ID)) + ")");
        Check(installed.All(p => p.State == LinuxPackageInfo.PackageState.Installed) && Find(installed, "libc6").Version == "2.36-9+deb12u9", "apt failed: dpkg packages are installed, with their versions; one row for two architectures");

        FakeRunner partial = new FakeRunner();
        partial.Responses[AptPackageManagerClient.ListCommand(false)] = new[]
        {
            "Listing...",
            "bash/now 5.2.15-2+b7 amd64 [installed,local]",
            "E: Could not open file /var/lib/apt/lists/x_Packages - open (2: No such file or directory)",
            AptPackageManagerClient.ExitMarker + "100"
        };
        LinuxPackageInfo[] listed = client.QueryAllPackages(partial, false);
        Check(listed.Length == 1 && listed[0].ID == "bash", "apt failed but listed packages: they are shown, dpkg is not asked");

        FakeRunner empty = new FakeRunner();
        empty.Responses[AptPackageManagerClient.ListCommand(false)] = new[] { "Listing...", AptPackageManagerClient.ExitMarker + "0" };
        Check(client.QueryAllPackages(empty, false).Length == 0, "apt succeeded with no packages: an empty list, no error");

        FakeRunner nothing = new FakeRunner();
        nothing.Responses[AptPackageManagerClient.ListCommand(false)] = new[] { "E: Malformed entry 1 in list file /etc/apt/sources.list (Suite)", "E: The list of sources could not be read.", AptPackageManagerClient.ExitMarker + "100" };
        string message = null;
        try
        {
            client.QueryAllPackages(nothing, false);
        }
        catch (Exception exception)
        {
            message = exception.Message;
        }
        Check(message != null && message.Contains("E: Malformed entry 1 in list file /etc/apt/sources.list (Suite)") && message.Contains("exited with 100") && message.Contains("apt-get update"), "apt and dpkg failed: the error says what apt said and what to do");
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
