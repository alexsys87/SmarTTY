using System.Text.RegularExpressions;

namespace SmarTTY.Ssh.QuickSync;

public struct MaskSpec
{
    public readonly Regex Regex;

    public readonly bool IsInclude;

    public bool HasRegex => Regex != null;

    public MaskSpec(Regex regex, bool isInclude)
    {
        this.Regex = regex;
        IsInclude = isInclude;
    }
}
