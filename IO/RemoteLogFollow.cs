using System;

namespace SmarTTY.IO;

/// <summary>
/// The command that follows a log file in a terminal tab, like "tail -f": the last lines of the file, then the lines that are
/// added, with an optional filter. It is built for the shell of a Unix host.
///
/// The class is free of any UI dependency, so the test harness can check it without a display.
/// </summary>
public static class RemoteLogFollow
{
    public const int DefaultLines = 200;

    public const int MaxLines = 100000;

    /// <summary>
    /// "sh -c '...'", so that it means the same whatever the login shell of the user is. It starts with the last
    /// <paramref name="lines"/> lines of the file and then follows it by name (tail -F: a log that is rotated is followed
    /// into the new file); a tail that does not know -F (an old BusyBox) is replaced by one with -f. Ctrl+C ends it, and does
    /// not start the fallback. With <paramref name="filter"/> only the lines that contain it (a regular expression when
    /// <paramref name="filterIsRegex"/> is set) are shown.
    /// </summary>
    public static string BuildCommand(string remotePath, int lines, string filter = null, bool filterIsRegex = false, bool ignoreCase = true)
    {
        if (string.IsNullOrEmpty(remotePath))
        {
            throw new ArgumentException("No file to follow.", nameof(remotePath));
        }
        lines = Math.Max(0, Math.Min(MaxLines, lines));
        string file = RemotePathUtils.ShellQuote(remotePath);
        // "--": a file name that starts with '-' is not an option. Exit status 1 is a tail that could not start (an unknown
        // option); a tail ended by Ctrl+C exits with 130 and is not started again.
        string follow = "tail -n " + lines + " -F -- " + file + "; rc=$?; if [ $rc -eq 1 ]; then tail -n " + lines + " -f -- " + file + "; fi";
        string script = follow;
        if (!string.IsNullOrEmpty(filter))
        {
            // The pattern after -e: one that starts with '-' is not an option. grep has to write line by line, so that the lines are shown
            // as they arrive: on a terminal it does that on its own, --line-buffered makes sure of it where it knows the option (a grep that
            // does not, an old BusyBox, answers the probe with its usage text, and is run without it).
            script = "lb=; [ -z \"$(grep --line-buffered -e x /dev/null 2>&1)\" ] && lb=--line-buffered; { " + follow + "; } | grep $lb "
                + (ignoreCase ? "-i " : "") + (filterIsRegex ? "-E " : "-F ") + "-e " + RemotePathUtils.ShellQuote(filter);
        }
        return "sh -c " + RemotePathUtils.ShellQuote(script);
    }

    /// <summary>Reads the number of lines the user typed; false when it is not a number from 0 to <see cref="MaxLines"/>.</summary>
    public static bool TryParseLines(string text, out int lines)
    {
        return int.TryParse((text ?? "").Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out lines)
            && lines >= 0 && lines <= MaxLines;
    }
}
