using System;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.IO;

public static class LsOutputParser
{
    public enum LsEntryFlags
    {
        None = 0,
        Directory = 1,
        Special = 2,
        Executable = 4
    }

    public struct LsEntry
    {
        public string Name;

        public long Size;

        public DateTime ModifiedTime;

        public LsEntryFlags Flags;

        /// <summary>The mode column of ls as printed ("-rwxr-xr-x", "drwxrwsr-x"); see <see cref="RemotePermissions.ParseMode"/>.</summary>
        public string Mode;

        public bool IsDirectory => (Flags & LsEntryFlags.Directory) == LsEntryFlags.Directory;

        public bool IsExecutable => (Flags & LsEntryFlags.Executable) == LsEntryFlags.Executable;
    }

    public static class LsParser
    {
        private static readonly Regex _lineRegex = new Regex("^(?<mode>[^ ]+) +[^ ]+ +[^ ]+ +[^ ]+ +([0-9]+) +([0-9]{4})-([0-9]{2})-([0-9]{2}) +([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\\.[0-9]+)? +([0-9+\\-]+) +([^ ].*)$");

        public static LsEntry ParseLine(string line)
        {
            if (line == null)
            {
                return default;
            }
            // The completion of the Smart Terminal lists the directory with a PTY, and the ls of BusyBox on OpenWrt colours
            // its output whenever it writes to a terminal (FEATURE_LS_COLOR_IS_DEFAULT), as does an "ls --color" alias: the
            // names would carry the colour codes ("\e[1;34msubdir\e[m/") into the popup.
            line = StripEscapeSequences(line);
            Match match = _lineRegex.Match(line);
            if (!match.Success)
            {
                return default;
            }
            string fileName = match.Groups[9].Value;
            bool hasSuffixChar = true;
            bool isExecutableMark = false;
            LsEntryFlags lsEntryFlags = LsOutputParser.LsEntryFlags.None;
            switch (fileName[fileName.Length - 1])
            {
                case '/':
                    lsEntryFlags = LsOutputParser.LsEntryFlags.Directory;
                    break;
                case '*':
                    isExecutableMark = true;
                    break;
                case '=':
                case '@':
                case '|':
                    lsEntryFlags = LsOutputParser.LsEntryFlags.Special;
                    break;
                default:
                    hasSuffixChar = false;
                    break;
            }
            if (hasSuffixChar)
            {
                fileName = fileName.Substring(0, fileName.Length - 1);
            }
            if (isExecutableMark)
            {
                lsEntryFlags |= LsOutputParser.LsEntryFlags.Executable;
            }
            DateTime dateTime = new DateTime(int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), int.Parse(match.Groups[4].Value), int.Parse(match.Groups[5].Value), int.Parse(match.Groups[6].Value), int.Parse(match.Groups[7].Value));
            int arrowIndex = fileName.IndexOf(" -> ");
            if (arrowIndex != -1)
            {
                fileName = fileName.Substring(0, arrowIndex);
            }
            return new LsEntry
            {
                Name = fileName,
                Size = long.Parse(match.Groups[1].Value),
                Flags = lsEntryFlags,
                Mode = match.Groups["mode"].Value,
                ModifiedTime = dateTime
            };
        }

        /// <summary>
        /// Removes the terminal control sequences from a line of output: CSI sequences (colours, "\e[01;34m"), OSC sequences
        /// (the hyperlinks of "ls --hyperlink", ended by BEL or ST) and other two-character escapes. ls prints a control
        /// character in a file name as "?" when it writes to a terminal, so no ESC that belongs to a name is lost.
        /// </summary>
        public static string StripEscapeSequences(string line)
        {
            if (line == null || line.IndexOf('\x1b') < 0)
            {
                return line;
            }
            StringBuilder result = new StringBuilder(line.Length);
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (c != '\x1b')
                {
                    result.Append(c);
                    i++;
                    continue;
                }
                if (i + 1 >= line.Length)
                {
                    break;
                }
                char kind = line[i + 1];
                if (kind == '[')
                {
                    // CSI: parameter and intermediate bytes, then one final byte in '@'..'~'.
                    int end = i + 2;
                    while (end < line.Length && (line[end] < '@' || line[end] > '~'))
                    {
                        end++;
                    }
                    i = end + 1;
                }
                else if (kind == ']')
                {
                    // OSC: ends with BEL or with ST (ESC \).
                    int end = i + 2;
                    while (end < line.Length && line[end] != '\a' && !(line[end] == '\x1b' && end + 1 < line.Length && line[end + 1] == '\\'))
                    {
                        end++;
                    }
                    i = end >= line.Length ? end : (line[end] == '\a' ? end + 1 : end + 2);
                }
                else
                {
                    i += 2;
                }
            }
            return result.ToString();
        }

        public static string BuildListCommand(string path, bool followSymlinks, bool recursive, bool useGls = false)
        {
            // ShellQuote: a directory with a single quote in its name used to break the command.
            if (useGls)
            {
                return string.Format("/opt/local/bin/gls -a{0}Fl{1} --full-time {2}", followSymlinks ? "L" : "", recursive ? "R" : "", RemotePathUtils.ShellQuote(path));
            }
            return string.Format("ls -a{0}Fl{1} --full-time {2}", followSymlinks ? "L" : "", recursive ? "R" : "", RemotePathUtils.ShellQuote(path));
        }
    }
}
