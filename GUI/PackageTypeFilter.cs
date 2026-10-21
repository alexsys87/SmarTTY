using SmarTTY.Utilities;

namespace SmarTTY.GUI;

public enum PackageTypeFilter
{
    [EnumDisplayText("All")]
    All,
    [EnumDisplayText("Installed packages")]
    Installed,
    [EnumDisplayText("Not installed")]
    NotInstalled,
    [EnumDisplayText("Updates")]
    Updates,
    [EnumDisplayText("Pending changes")]
    Pending
}
