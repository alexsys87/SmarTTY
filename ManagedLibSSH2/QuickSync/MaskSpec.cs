using System.Text.RegularExpressions;

namespace ManagedLibSSH2.QuickSync;

public struct MaskSpec
{
    public readonly Regex Regex;

    public readonly bool IsInclude;

    public bool HasRegex => Regex != null;

    public MaskSpec(Regex regex, bool flag)
    {
        this.Regex = regex;
        IsInclude = flag;
    }
}
