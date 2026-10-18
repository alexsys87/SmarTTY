
namespace SmarTTY.TextEditing;

public struct ScrollLimits
{
    public int Minimum;

    public int Maximum;

    public int LargeChange;

    public int Range => Maximum - Minimum;
}
