using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SmarTTY.Sync;

public class StatCommandBuilder
{
    public enum StatFlavor
    {
        Unknown,
        GnuLs,
        Bsd,
        GnuFindNullSeparated,
        GnuFindNewlineSeparated
    }

    public delegate IEnumerable<string> CommandRunner(string command);

    public enum StatEntryKind
    {
        Unknown,
        File,
        Directory,
        Symlink
    }

    public struct StatEntry
    {
        public StatEntryKind Kind;

        public string Name;

        public string LinkTarget;

        public long Size;

        public long ModifiedTime;

        public int DeviceId;

        public int ModifiedSeconds => (int)ModifiedTime;

        public override string ToString()
        {
            return Name;
        }
    }

    public StatFlavor Flavor { get; }

    public StatCommandBuilder(StatFlavor statFlavor)
    {
        Flavor = statFlavor;
    }

    public static StatFlavor DetectFlavor(CommandRunner callback, string testPath = ".")
    {
        if (callback("stat -c \"path=%N\" " + SmarTTY.IO.RemotePathUtils.ShellQuote(testPath))?.FirstOrDefault((string line) => line.StartsWith("path=")) != null)
        {
            if (callback("echo | xargs -d \"\\n\" echo") != null)
            {
                return StatFlavor.GnuLs;
            }
            if (callback("echo | xargs -0 echo") != null)
            {
                return StatFlavor.GnuFindNullSeparated;
            }
            return StatFlavor.GnuFindNewlineSeparated;
        }
        if (callback("stat -f \"path=%N\" " + SmarTTY.IO.RemotePathUtils.ShellQuote(testPath))?.FirstOrDefault((string line) => line.StartsWith("path=")) != null)
        {
            return StatFlavor.Bsd;
        }
        return StatFlavor.Unknown;
    }

    public string GetListCommand(string dirPath, bool followSymlinks = false)
    {
        string symlinkFlag = (followSymlinks ? "L" : "");
        string findSymlinkFlag = (followSymlinks ? "-L" : "");
        // "VAR=value cd ..." sets the variable for that one command only (plain shell semantics): "ls"/"find", "xargs"
        // and "stat" after the "&&" still run in whatever locale the exec channel started in, often C/POSIX on a
        // minimal image or a container with no LANG passed to sshd, which makes GNU stat print a non-ASCII name escaped
        // byte by byte instead of as UTF-8 (TryReadQuotedWord cannot put such a run of bytes back together, since each
        // one arrives as its own already-decoded character). "export" instead makes it a shell variable that stays set
        // for the rest of this one command string, and C.UTF-8 (unlike "en_US", which is a locale name with no
        // encoding and may not even be installed) is the UTF-8 locale glibc ships built in, so it needs no locale-gen.
        string setLocale = "export LC_ALL=C.UTF-8 2>/dev/null; ";
        // "cd" only needs to enter the directory (the "x" permission); listing it needs "r" as well, and a directory
        // that has one without the other (0711, common for a parent kept walkable but not browsable) lets "cd" and
        // this whole pipeline succeed while "ls"/"find" write "Permission denied" to stderr and nothing to stdout.
        // "xargs" then still runs its command once on the plain "." argument appended after the (empty) input, which
        // succeeds and makes the pipe's exit status 0 - the caller's "|| echo ListFailedMarker" never fires, and a
        // folder that could not be read looks like one that is simply empty. Checking "-r" before the pipe runs makes
        // the whole "&&" chain fail instead, so the caller is told the listing could not be trusted.
        string prefix = setLocale + "cd " + SmarTTY.IO.RemotePathUtils.ShellQuote(dirPath) + " && [ -r . ] && ";
        if (this.Flavor == StatFlavor.Bsd)
        {
            // "--" before the names "ls" printed (and the trailing "."): a plain file called "-v" or "--help" would
            // otherwise be read by stat as one of its own options instead of a name.
            return prefix + "ls -1A" + symlinkFlag + " | xargs -d \"\\n\" stat -" + symlinkFlag + "f \"StatEntryB:|%N|%Dm|%d|%z|%Xp|%Y\" -- .";
        }
        if (this.Flavor == StatFlavor.GnuFindNullSeparated)
        {
            return prefix + "find . " + findSymlinkFlag + " -maxdepth 1 -print0 | xargs -0 stat -" + symlinkFlag + "c \"StatEntryL:|%N|%Y|%d|%s|%f\"";
        }
        if (this.Flavor == StatFlavor.GnuFindNewlineSeparated)
        {
            return prefix + "find . " + findSymlinkFlag + " -maxdepth 1 -print | xargs stat -" + symlinkFlag + "c \"StatEntryL:|%N|%Y|%d|%s|%f\"";
        }
        // Same "--" as the BSD case, for the same reason: "ls" (unlike "find .", whose names already start with "./")
        // prints a plain file called "-v" as just "-v", which stat would otherwise try to read as an option.
        return prefix + "ls -1A" + symlinkFlag + " | xargs -d \"\\n\" stat -" + symlinkFlag + "c \"StatEntryL:|%N|%Y|%d|%s|%f\" -- .";
    }

    public string GetStatCommand(string path, bool followSymlinks)
    {
        string symlinkFlag = (followSymlinks ? " -L" : "");
        if (this.Flavor == StatFlavor.Bsd)
        {
            return "stat" + symlinkFlag + " -f \"StatEntryB:|%N|%Dm|%d|%z|%Xp|%Y\" " + SmarTTY.IO.RemotePathUtils.ShellQuote(path);
        }
        return "stat" + symlinkFlag + " -c \"StatEntryL:|%N|%Y|%d|%s|%f\" " + SmarTTY.IO.RemotePathUtils.ShellQuote(path);
    }

    private static void ParseGnuEntry(string line, string[] parts, ref StatEntry entry)
    {
        int count = parts.Length;
        if (!long.TryParse(parts[count - 4], out long modified) || !int.TryParse(parts[count - 3], out int deviceId)
            || !long.TryParse(parts[count - 2], out long size) || !int.TryParse(parts[count - 1], NumberStyles.HexNumber, null, out int modeHex))
        {
            return;
        }
        string quoted = string.Join("|", parts, 1, count - 5);
        if (!TryReadNames(quoted, out string name, out string target))
        {
            return;
        }
        entry.ModifiedTime = modified;
        entry.DeviceId = deviceId;
        entry.Size = size;
        switch ((modeHex >> 12) & 0xF)
        {
            case 8:
                entry.Kind = StatEntryKind.File;
                break;
            case 4:
                entry.Kind = StatEntryKind.Directory;
                break;
            case 10:
                entry.Kind = StatEntryKind.Symlink;
                break;
        }
        entry.Name = name != null && name.StartsWith("./") ? name.Substring(2) : name;
        entry.LinkTarget = target;
    }

    /// <summary>
    /// Reads what GNU stat prints for %N: the name quoted for a shell ('it'\''s', 'a'$'\n''b'), and for a symbolic link "-&gt; " and the
    /// quoted target. False when the text is not in that form (the caller then falls back to cutting the quotes off).
    /// </summary>
    internal static bool TryReadNames(string text, out string name, out string target)
    {
        name = null;
        target = null;
        int position = 0;
        if (!TryReadQuotedWord(text, ref position, out name))
        {
            return false;
        }
        while (position < text.Length && text[position] == ' ')
        {
            position++;
        }
        if (position == text.Length)
        {
            return true;
        }
        if (string.CompareOrdinal(text, position, "->", 0, 2) != 0)
        {
            return false;
        }
        position += 2;
        while (position < text.Length && text[position] == ' ')
        {
            position++;
        }
        return TryReadQuotedWord(text, ref position, out target) && position == text.Length;
    }

    // One shell word made of 'single quoted' parts, \x escapes outside quotes and $'ANSI-C' parts, up to a blank outside quotes.
    private static bool TryReadQuotedWord(string text, ref int position, out string word)
    {
        System.Text.StringBuilder result = new System.Text.StringBuilder();
        word = null;
        int i = position;
        bool any = false;
        while (i < text.Length && text[i] != ' ')
        {
            char ch = text[i];
            if (ch == '\'')
            {
                int end = text.IndexOf('\'', i + 1);
                if (end < 0)
                {
                    return false;
                }
                result.Append(text, i + 1, end - i - 1);
                i = end + 1;
            }
            else if (ch == '$' && i + 1 < text.Length && text[i + 1] == '\'')
            {
                i += 2;
                while (true)
                {
                    if (i >= text.Length)
                    {
                        return false;
                    }
                    char c = text[i];
                    if (c == '\'')
                    {
                        i++;
                        break;
                    }
                    if (c == '\\' && i + 1 < text.Length)
                    {
                        char e = text[i + 1];
                        i += 2;
                        switch (e)
                        {
                            case 'n': result.Append('\n'); break;
                            case 't': result.Append('\t'); break;
                            case 'r': result.Append('\r'); break;
                            case 'a': result.Append('\a'); break;
                            case 'b': result.Append('\b'); break;
                            case 'f': result.Append('\f'); break;
                            case 'v': result.Append('\v'); break;
                            case 'e': result.Append('\u001b'); break;
                            case '\\': result.Append('\\'); break;
                            case '\'': result.Append('\''); break;
                            case '"': result.Append('"'); break;
                            case 'x':
                            case '0':
                            case '1':
                            case '2':
                            case '3':
                            {
                                // \xHH or \NNN: one byte. A run of bytes is not decoded here (a name with non-ASCII bytes is printed by
                                // GNU stat as itself in a UTF-8 locale); the byte is taken as the character of that code.
                                int numberBase = e == 'x' ? 16 : 8;
                                int start = e == 'x' ? i : i - 1;
                                int length = e == 'x' ? 2 : 3;
                                if (start + length > text.Length)
                                {
                                    return false;
                                }
                                try
                                {
                                    result.Append((char)System.Convert.ToInt32(text.Substring(start, length), numberBase));
                                }
                                catch (System.FormatException)
                                {
                                    return false;
                                }
                                i = start + length;
                                break;
                            }
                            default:
                                result.Append('\\').Append(e);
                                break;
                        }
                        continue;
                    }
                    result.Append(c);
                    i++;
                }
            }
            else if (ch == '\\' && i + 1 < text.Length)
            {
                result.Append(text[i + 1]);
                i += 2;
            }
            else
            {
                return false; // an unquoted character: not the form of GNU stat
            }
            any = true;
        }
        if (!any)
        {
            return false;
        }
        position = i;
        word = result.ToString();
        return true;
    }

    public StatEntry ParseEntry(string line)
    {
        StatEntry entry = default;
        if (line.Length < 6)
        {
            return default;
        }
        string[] parts = line.Split('|');
        if (line.StartsWith("StatEntryL:|", System.StringComparison.Ordinal) && parts.Length > 6)
        {
            // A '|' in a name: the four numbers are the last fields, whatever is between the tag and them is the quoted name.
            ParseGnuEntry(line, parts, ref entry);
            return entry;
        }
        if (parts.Length < 6)
        {
            // Not an entry of ours (a line of the shell, a message): an unusable line is no entry.
            return default;
        }
        if (!long.TryParse(parts[2], out entry.ModifiedTime))
        {
            return default;
        }
        if (!int.TryParse(parts[3], out entry.DeviceId))
        {
            return default;
        }
        if (!long.TryParse(parts[4], out entry.Size))
        {
            return default;
        }
        if (!int.TryParse(parts[5], NumberStyles.HexNumber, null, out var modeHex))
        {
            return default;
        }
        switch ((modeHex >> 12) & 0xF)
        {
            case 8:
                entry.Kind = StatEntryKind.File;
                break;
            case 4:
                entry.Kind = StatEntryKind.Directory;
                break;
            case 10:
                entry.Kind = StatEntryKind.Symlink;
                break;
        }
        if (line.StartsWith("StatEntryL:|"))
        {
            if (parts.Length != 6)
            {
                return default;
            }
            int arrowIndex = parts[1].IndexOf("->");
            if (TryReadNames(parts[1], out string quotedName, out string quotedTarget))
            {
                // The quoting of GNU stat is undone (a name with an apostrophe is printed as 'it'\''s').
                entry.Name = quotedName;
                entry.LinkTarget = quotedTarget;
            }
            else if (arrowIndex == -1)
            {
                entry.Name = parts[1].Trim(' ', '\'');
            }
            else
            {
                entry.Name = parts[1].Substring(0, arrowIndex).Trim(' ', '\'');
                entry.LinkTarget = parts[1].Substring(arrowIndex + 2).Trim(' ', '\'');
            }
        }
        else if (line.StartsWith("StatEntryB:|"))
        {
            if (parts.Length != 7)
            {
                return default;
            }
            entry.Name = parts[1];
            entry.LinkTarget = parts[6];
        }
        string name = entry.Name;
        if (name != null && name.StartsWith("./"))
        {
            entry.Name = entry.Name.Substring(2);
        }
        return entry;
    }
}
