using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

/// <summary>
/// Which package names are put into the command lines that are typed in the terminal (as root, through sudo su -c "..."). The names come
/// from the output of the package manager of the host; one that holds a quote, a $, a backtick or a blank would break the command or run
/// something else with the rights of root, and one that starts with '-' would be read as an option. The clients of Homebrew and winget have
/// their own rules (<c>FormulaNameRegex</c>, <c>IsSafeId</c>); this one is for all the others.
/// </summary>
public static class PackageNames
{
    // Letters, digits and what the names of Debian, RPM, Arch, Alpine, opkg, Void, FreeBSD and Gentoo packages are made of ("category/name",
    // "name:arch", "name@version", "name=version", "name-1.0~rc1+git").
    private static readonly Regex SafeNameRegex = new Regex(@"^[A-Za-z0-9._+~:@%,=/][A-Za-z0-9._+~:@%,=/\-]*$", RegexOptions.Compiled);

    /// <summary>True for a name that can be put into a command line as it is.</summary>
    public static bool IsSafe(string id)
    {
        return id != null && SafeNameRegex.IsMatch(id);
    }

    /// <summary>The packages whose names are safe; <paramref name="refused"/> gets the names of the others (null when there are none).</summary>
    public static LinuxPackageInfo[] Safe(LinuxPackageInfo[] packages, out string[] refused)
    {
        refused = packages.Where(package => !IsSafe(package.ID)).Select(package => package.ID ?? "").ToArray();
        if (refused.Length == 0)
        {
            refused = null;
            return packages;
        }
        return packages.Where(package => IsSafe(package.ID)).ToArray();
    }
}
