using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SmarTTY.Terminal;

/// <summary>
/// The "Command history" window of the terminal menu: the list it shows, its search, and the removal of commands from
/// the history file of the shell on the server (Smart Terminal tabs).
/// </summary>
internal static class CommandHistoryEditing
{
    /// <summary>Every command once, the newest first, without blank lines.</summary>
    public static List<string> Distinct(IEnumerable<string> newestFirst)
    {
        List<string> result = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string command in newestFirst ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(command) && seen.Add(command))
            {
                result.Add(command);
            }
        }
        return result;
    }

    /// <summary>
    /// The commands that contain every word of <paramref name="search"/>, in any order and case ("git push" finds
    /// "git push origin main" and "push to git"); everything for an empty search.
    /// </summary>
    public static List<string> Filter(IEnumerable<string> commands, string search)
    {
        string[] words = (search ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return (commands ?? Enumerable.Empty<string>())
            .Where((string command) => command != null && words.All((string word) => command.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
            .ToList();
    }

    /// <summary>
    /// The history file as a word of a shell command: "~/" becomes "$HOME"/, the rest is quoted, so a file name that
    /// the user typed in the settings cannot break the command.
    /// </summary>
    public static string QuoteHistoryFile(string historyFile)
    {
        if (string.IsNullOrEmpty(historyFile))
        {
            return null;
        }
        if (historyFile == "~")
        {
            return "\"$HOME\"";
        }
        if (historyFile.StartsWith("~/", StringComparison.Ordinal))
        {
            return "\"$HOME\"/" + RemotePathUtils.ShellQuote(historyFile.Substring(2));
        }
        return RemotePathUtils.ShellQuote(historyFile);
    }

    // Reads the commands to remove (the first file), then copies the history file without them. A time stamp line of
    // bash (HISTTIMEFORMAT: "#1699999999") goes with the command after it; a line of the extended history of zsh
    // (": 1699999999:0;command") is compared by its command. POSIX awk: BusyBox awk on OpenWrt runs it too.
    private const string RemoveProgram =
        "NR == FNR { remove[$0] = 1; next } " +
        "{ " +
        "if (stamped) { stamped = 0; if ($0 in remove) next; print stamp } " +
        "if ($0 ~ /^#[0-9]+$/) { stamped = 1; stamp = $0; next } " +
        "command = $0; sub(/^: [0-9]+:[0-9]+;/, \"\", command); " +
        "if (command in remove) next; " +
        "print " +
        "} " +
        "END { if (stamped) print stamp }";

    /// <summary>
    /// A shell command (POSIX sh, BusyBox ash) that removes every line of <paramref name="commands"/> from the history
    /// file, or null when there is nothing to remove. The file is written over in place (cat &gt;), so it keeps its owner,
    /// its permissions and its inode, which an open shell may hold. A missing file is left alone. Commands of several
    /// lines are not in a history file as one line and are skipped.
    /// </summary>
    public static string BuildRemoveCommand(string historyFile, IEnumerable<string> commands)
    {
        string file = QuoteHistoryFile(historyFile);
        List<string> lines = (commands ?? Enumerable.Empty<string>())
            .Where((string command) => !string.IsNullOrEmpty(command) && command.IndexOf('\n') < 0 && command.IndexOf('\r') < 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (file == null || lines.Count == 0)
        {
            return null;
        }
        StringBuilder script = new StringBuilder();
        script.Append("f=").Append(file).Append("; [ -f \"$f\" ] || exit 0; ");
        // mktemp is missing on a few minimal systems: then names of this shell's own.
        script.Append("l=$(mktemp 2>/dev/null) || l=/tmp/.smartty-history-remove.$$; ");
        script.Append("t=$(mktemp 2>/dev/null) || t=/tmp/.smartty-history-copy.$$; ");
        script.Append("printf '%s\\n'");
        foreach (string line in lines)
        {
            script.Append(' ').Append(RemotePathUtils.ShellQuote(line));
        }
        script.Append(" > \"$l\" && awk '").Append(RemoveProgram).Append("' \"$l\" \"$f\" > \"$t\" && cat \"$t\" > \"$f\"; ");
        script.Append("r=$?; rm -f \"$l\" \"$t\"; exit $r");
        return script.ToString();
    }
}
