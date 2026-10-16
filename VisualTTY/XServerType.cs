using SmarTTY.Utilities;

namespace VisualTTY;

public enum XServerType
{
    [EnumDisplayText("Ask on first use")]
    Ask,
    [EnumDisplayText("vcxsrv (recommended)")]
    Vcxsrv,
    [EnumDisplayText("XMing (legacy)")]
    XMing,
    Custom
}
