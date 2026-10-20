using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY;
using VisualTTY;

// Regression harness for the apk (Alpine Linux / OpenWrt 25.12+) package manager client: probing, parsing of
// "apk list" output and the generated command lines, plus the package list refresh ("Refresh" button) for all
// package managers. Run with: dotnet run --project tests/ApkPackageManagerTests
internal static class Program
{
    private static int _passed;

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
        return packages.Single(p => p.ID == id);
    }

    private static int Main()
    {
        ApkPackageManagerClient client = new ApkPackageManagerClient();

        // Probe
        FakeRunner runner = new FakeRunner();
        Check(client.Probe(runner) == 0, "Probe: no apk binary (command fails) -> 0");
        runner.Responses["apk --version"] = new[] { "apk-tools 3.0.0_pre20241113, compiled for aarch64." };
        Check(client.Probe(runner) == 1000, "Probe: OpenWrt apk-tools 3 -> 1000");
        runner.Responses["apk --version"] = new[] { "apk-tools 2.14.4, compiled for x86_64." };
        Check(client.Probe(runner) == 1000, "Probe: Alpine apk-tools 2 -> 1000");
        runner.Responses["apk --version"] = new[] { "some other apk tool 1.0" };
        Check(client.Probe(runner) == 0, "Probe: a foreign 'apk' binary is not accepted");

        // Parsing
        string[] lines =
        {
            "busybox-1.37.0-r3 aarch64 {busybox} (GPL-2.0-only) [installed]",
            "busybox-1.37.0-r5 aarch64 {busybox} (GPL-2.0-only) [upgradable from: busybox-1.37.0-r3]",
            "kmod-fs-ext4-6.12.55~abc123-r1 aarch64_cortex-a53 {linux} (GPL-2.0-only) [installed]",
            "curl-8.11.1-r1 aarch64_cortex-a53 {curl} (curl)",
            "libopenssl3-3.0.15-r1 aarch64_cortex-a53 {openssl} (Apache-2.0)",
            "luci-app-foo-git-24.100.1-r1 all {luci-app-foo} (Apache-2.0 AND (MIT OR BSD-3-Clause))",
            "libfoo2-2-dev-2.0_rc1-r0 aarch64 {libfoo} ()",
            "nano-8.2 x86_64 {nano} (GPL-3.0-or-later)",
            "WARNING: opening from cache https://downloads.openwrt.org/snapshots/packages/x/base/packages.adb: No such file",
            "",
            "garbage line"
        };
        LinuxPackageInfo[] packages = ApkPackageManagerClient.ParsePackageList(lines);
        Check(packages.Length == 7, "Parse: junk lines skipped, upgrade pair merged (" + packages.Length + " packages)");
        Check(packages.Select(p => p.ID).SequenceEqual(packages.Select(p => p.ID).OrderBy(i => i, StringComparer.OrdinalIgnoreCase)), "Parse: sorted by name");

        LinuxPackageInfo busybox = Find(packages, "busybox");
        Check(busybox.State == LinuxPackageInfo.PackageState.Upgradeable, "busybox is upgradeable");
        Check(busybox.Version == "1.37.0-r5", "busybox shows the new version");
        Check(busybox.RawState == "upgradable from: 1.37.0-r3", "busybox raw state names the installed version");
        Check(busybox.IsThreeState, "an upgradeable package is three-state in the UI");

        LinuxPackageInfo kmod = Find(packages, "kmod-fs-ext4");
        Check(kmod.State == LinuxPackageInfo.PackageState.Installed && kmod.Version == "6.12.55~abc123-r1", "name with dashes and a ~hash version");

        Check(Find(packages, "curl").State == LinuxPackageInfo.PackageState.Available, "curl is available");
        Check(Find(packages, "curl").Version == "8.11.1-r1", "curl version");
        Check(Find(packages, "luci-app-foo-git").Version == "24.100.1-r1", "license with nested parentheses");
        Check(Find(packages, "libfoo2-2-dev").Version == "2.0_rc1-r0", "digits inside the package name and an empty license");
        Check(Find(packages, "nano").Version == "8.2", "version without -rN falls back to the generic split");

        // Order independence: the upgrade line may come before the installed one
        LinuxPackageInfo[] reversed = ApkPackageManagerClient.ParsePackageList(new[] { lines[1], lines[0] });
        Check(reversed.Length == 1 && reversed[0].State == LinuxPackageInfo.PackageState.Upgradeable && reversed[0].Version == "1.37.0-r5", "upgrade pair merges in either order");

        // QueryAllPackages
        FakeRunner queryRunner = new FakeRunner();
        bool failed = false;
        try
        {
            client.QueryAllPackages(queryRunner);
        }
        catch (Exception)
        {
            failed = true;
        }
        Check(failed, "QueryAllPackages throws when 'apk list' fails");
        queryRunner.Responses["apk list"] = lines;
        Check(client.QueryAllPackages(queryRunner).Length == 7, "QueryAllPackages runs 'apk list' and parses it");

        // Command lines
        LinuxPackageInfo fresh = new LinuxPackageInfo { ID = "curl", State = LinuxPackageInfo.PackageState.Available };
        LinuxPackageInfo fresh2 = new LinuxPackageInfo { ID = "nano", State = LinuxPackageInfo.PackageState.Available };
        LinuxPackageInfo upgrade = new LinuxPackageInfo { ID = "busybox", State = LinuxPackageInfo.PackageState.Upgradeable };
        LinuxPackageInfo installed = new LinuxPackageInfo { ID = "kmod-fs-ext4", State = LinuxPackageInfo.PackageState.Installed };
        PackageCommitOptions ask = new PackageCommitOptions { NoPrompt = false };
        PackageCommitOptions noPrompt = new PackageCommitOptions { NoPrompt = true };
        const LinuxPackageInfo.PackageAction Install = LinuxPackageInfo.PackageAction.Install;
        const LinuxPackageInfo.PackageAction Remove = LinuxPackageInfo.PackageAction.Remove;

        Check(client.GetCommandLineForPackageOperation(new[] { fresh, fresh2 }, Install, ask) == "apk -i add curl nano", "install asks for confirmation by default");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, Install, noPrompt) == "apk add curl", "install without confirmation");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, Install, null) == "apk -i add curl", "null options behave like asking");
        Check(client.GetCommandLineForPackageOperation(new[] { installed, fresh }, Remove, ask) == "apk -i del kmod-fs-ext4 curl", "remove uses 'apk del'");
        Check(client.GetCommandLineForPackageOperation(new[] { upgrade }, Install, noPrompt) == "apk add -u busybox", "upgrade uses 'apk add -u'");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh, upgrade }, Install, noPrompt) == "apk add curl && apk add -u busybox", "new packages and upgrades are split");
        Check(client.GetCommandLineForPackageOperation(new LinuxPackageInfo[0], Install, ask) == null, "empty selection -> null");
        Check(client.GetCommandLineForPackageOperation(new[] { fresh }, LinuxPackageInfo.PackageAction.None, ask) == null, "unsupported action -> null");

        // Refresh of the package lists
        Check(client.GetCommandLineForIndexUpdate() == "apk update", "apk refresh command");
        Check(new AptPackageManagerClient().GetCommandLineForIndexUpdate() == "apt-get update", "apt refresh command");
        Check(new OpkgPackageManagerClient().GetCommandLineForIndexUpdate() == "opkg update", "opkg refresh command");
        Check(new YumPackageManagerClient().GetCommandLineForIndexUpdate() == "yum -y makecache", "yum refresh command");

        string wrapped = PackageIndexUpdater.BuildCommandLine("apk update", isRoot: true);
        Check(wrapped == "{ apk update; } 2>&1; echo __SMARTTY_EXIT__=$?", "refresh as root runs the command as is, reporting the exit status on the last line");
        Check(!wrapped.Contains("sudo"), "refresh as root does not involve sudo (OpenWrt has none)");
        string wrappedSudo = PackageIndexUpdater.BuildCommandLine("apk update", isRoot: false);
        Check(wrappedSudo == "{ sudo -n apk update; } 2>&1; echo __SMARTTY_EXIT__=$?", "refresh as another user goes through 'sudo -n'");

        FakeRunner idRunner = new FakeRunner();
        Check(!PackageIndexUpdater.IsRoot(idRunner), "IsRoot: unknown user (command fails) -> false");
        idRunner.Responses["id -u"] = new[] { "0" };
        Check(PackageIndexUpdater.IsRoot(idRunner), "IsRoot: uid 0 -> true");
        idRunner.Responses["id -u"] = new[] { "1000" };
        Check(!PackageIndexUpdater.IsRoot(idRunner), "IsRoot: uid 1000 -> false");

        Check(PackageIndexUpdater.WrapForPrivileges("opkg install curl", true) == "opkg install curl", "install command line as root has no sudo");
        Check(PackageIndexUpdater.WrapForPrivileges("apt-get install -y curl", false) == "sudo su -c \"apt-get install -y curl\"", "install command line as another user uses sudo su -c");

        FakeRunner updateRunner = new FakeRunner();
        updateRunner.Responses[wrapped] = new[] { "fetch https://downloads.openwrt.org/releases/25.12.0/packages/x/base/packages.adb", "OK: 120 distinct packages available", "__SMARTTY_EXIT__=0" };
        PackageIndexUpdater.Run(updateRunner, client, isRoot: true);
        Check(updateRunner.Commands.SequenceEqual(new[] { wrapped }), "refresh runs exactly one wrapped command");

        string failure = null;
        updateRunner.Responses[wrappedSudo] = new[] { "sudo: a password is required", "__SMARTTY_EXIT__=1" };
        try
        {
            PackageIndexUpdater.Run(updateRunner, client, isRoot: false);
        }
        catch (Exception exception)
        {
            failure = exception.Message;
        }
        Check(failure != null && failure.Contains("exit code 1") && failure.Contains("a password is required") && failure.Contains("passwordless sudo"), "failed refresh reports the exit code, the output and a hint");
        Check(failure != null && !failure.Contains("__SMARTTY_EXIT__"), "the exit marker is not shown to the user");

        failure = null;
        updateRunner.Responses[wrapped] = new[] { "ERROR: unable to select packages", "__SMARTTY_EXIT__=1" };
        try
        {
            PackageIndexUpdater.Run(updateRunner, client, isRoot: true);
        }
        catch (Exception exception)
        {
            failure = exception.Message;
        }
        Check(failure != null && failure.Contains("unable to select packages") && !failure.Contains("sudo"), "failed refresh as root does not mention sudo");

        failure = null;
        updateRunner.Responses.Remove(wrapped);
        try
        {
            PackageIndexUpdater.Run(updateRunner, client, isRoot: true);
        }
        catch (Exception exception)
        {
            failure = exception.Message;
        }
        Check(failure != null && failure.Contains("apk update"), "refresh fails clearly when the command cannot be run at all");

        failure = null;
        updateRunner.Responses[wrapped] = Enumerable.Range(1, 30).Select(i => "line " + i).Concat(new[] { "__SMARTTY_EXIT__=100" }).ToArray();
        try
        {
            PackageIndexUpdater.Run(updateRunner, client, isRoot: true);
        }
        catch (Exception exception)
        {
            failure = exception.Message;
        }
        Check(failure != null && failure.Contains("line 30") && failure.Contains("line 23") && !failure.Contains("line 22\n"), "only the tail of a long error output is shown");

        _passed += AdditionalClientsTests.Run();

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
