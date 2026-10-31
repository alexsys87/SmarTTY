using VisualTTY;

namespace SmarTTY;

/// <summary>
/// The package managers of Linux and other Unix hosts that SmarTTY can drive, in the order of their probes. The package manager
/// window and the background probe of the session (which decides whether Tools > Manage Linux Packages is offered at all) use
/// the same list, so that they cannot disagree about what is supported.
/// </summary>
public static class PackageManagerCatalog
{
    /// <summary>
    /// New instances of the clients, in the order of their probes. The order only matters for equal scores (the first wins), and the
    /// managers that are rare next to a system manager come last: a host that answers a probe with the top score is not asked the rest.
    /// </summary>
    public static ILinuxPackageManagerClient[] CreateUnixClients()
    {
        return new ILinuxPackageManagerClient[]
        {
            new AptPackageManagerClient(),
            new DnfPackageManagerClient(),
            new ZypperPackageManagerClient(),
            new PacmanPackageManagerClient(),
            new PortagePackageManagerClient(),
            new YumPackageManagerClient(),
            new OpkgPackageManagerClient(),
            new ApkPackageManagerClient(),
            new XbpsPackageManagerClient(),
            new PkgPackageManagerClient(),
            new HomebrewPackageManagerClient()
        };
    }
}
