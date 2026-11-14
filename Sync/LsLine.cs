using System.Text.RegularExpressions;

namespace SmarTTY.Sync;

/// <summary>Reading of the lines of "ls -l".</summary>
internal static class LsLine
{
    // permissions, link count, owner, group (some ls leave it out), size (or "major, minor" of a device), the date (a month name and a
    // day, or an ISO date), the time of day or, for a file older than half a year, the year, and then the name.
    private static readonly Regex NameAfterDate = new Regex(
        @"^\S+\s+\d+\s+\S+(?:\s+\S+)?\s+(?:\d+,\s+)?\d+\s+(?:[A-Za-z]{3,}\.?\s+\d{1,2}|\d{4}-\d{2}-\d{2})\s+(?:\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:\s+[+-]\d{4})?|\d{4})\s(.*)$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The name field of a line of "ls -l" (it may still hold " -> target" of a link and the indicator of -F), or null when the line
    /// has no date that is known here. The name is not found by the first colon: that is the one in the time "05:49", and a file
    /// that is older than half a year has the year there instead of a time.
    /// </summary>
    public static string NameField(string line)
    {
        Match match = NameAfterDate.Match(line);
        return match.Success ? match.Groups[1].Value : null;
    }
}
