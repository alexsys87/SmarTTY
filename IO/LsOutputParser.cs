using System;
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

        public bool IsDirectory => (Flags & LsEntryFlags.Directory) == LsEntryFlags.Directory;

        public bool IsExecutable => (Flags & LsEntryFlags.Executable) == LsEntryFlags.Executable;
    }

    public static class LsParser
    {
        private static readonly Regex _lineRegex = new Regex("^[^ ]+ +[^ ]+ +[^ ]+ +[^ ]+ +([0-9]+) +([0-9]{4})-([0-9]{2})-([0-9]{2}) +([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\\.[0-9]+)? +([0-9+\\-]+) +([^ ].*)$");

        public static LsEntry ParseLine(string line)
        {
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
                ModifiedTime = dateTime
            };
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
