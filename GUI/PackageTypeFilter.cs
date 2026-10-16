using SmarTTY.Utilities;

namespace SmarTTY.GUI;

public enum PackageTypeFilter
{
    [EnumDisplayText("All")]
    All,
    [EnumDisplayText("Installed packages")]
    Installed,
    [EnumDisplayText("Updates")]
    Updates
}
