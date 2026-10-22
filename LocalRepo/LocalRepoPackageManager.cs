namespace SmarTTY.LocalRepo;

/// <summary>The package manager of an OpenWrt device that the local repository server feeds.</summary>
public enum LocalRepoPackageManager
{
    /// <summary>opkg with .ipk packages and a "Packages" index (OpenWrt up to 24.10).</summary>
    Opkg,

    /// <summary>apk (apk-tools 3) with .apk packages and a "packages.adb" index (OpenWrt 25.12 and snapshots).</summary>
    Apk
}
