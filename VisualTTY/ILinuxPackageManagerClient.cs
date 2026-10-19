
namespace VisualTTY;

public interface ILinuxPackageManagerClient
{
    int Probe(ISSHCommandRunner runner);

    LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions = false);

    string GetCommandLineForPackageOperation(LinuxPackageInfo[] packages, LinuxPackageInfo.PackageAction action, PackageCommitOptions options);

    /// <summary>
    /// The command that refreshes the package lists from the repositories (e.g. "apt-get update"); privilege escalation is added by the caller.
    /// </summary>
    string GetCommandLineForIndexUpdate();
}
