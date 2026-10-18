
namespace VisualTTY;

public interface ILinuxPackageManagerClient
{
    int Probe(ISSHCommandRunner runner);

    LinuxPackageInfo[] QueryAllPackages(ISSHCommandRunner runner, bool allVersions = false);

    string GetCommandLineForPackageOperation(LinuxPackageInfo[] packages, LinuxPackageInfo.PackageAction action, PackageCommitOptions options);
}
