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
        private static readonly Regex _lineRegex = new Regex("^[^ ]+ +[^ ]+ +[^ ]+ +[^ ]+ +([0-9]+) +([0-9]{4})-([0-9]{2})-([0-9]{2}) +([0-9]{2}):([0-9]{2}):([0-9]{2})\\.[0-9]+ +([0-9+\\-]+) +([^ ].*)$");

        public static LsEntry ParseLine(string text2)
        {
            Match match = _lineRegex.Match(text2);
            if (!match.Success)
            {
                return default;
            }
            string text = match.Groups[9].Value;
            bool flag = true;
            bool flag2 = false;
            LsEntryFlags lsEntryFlags = LsOutputParser.LsEntryFlags.None;
            switch (text[text.Length - 1])
            {
                case '/':
                    lsEntryFlags = LsOutputParser.LsEntryFlags.Directory;
                    break;
                case '*':
                    flag2 = true;
                    break;
                case '=':
                case '@':
                case '|':
                    lsEntryFlags = LsOutputParser.LsEntryFlags.Special;
                    break;
                default:
                    flag = false;
                    break;
            }
            if (flag)
            {
                text = text.Substring(0, text.Length - 1);
            }
            if (flag2)
            {
                lsEntryFlags |= LsOutputParser.LsEntryFlags.Executable;
            }
            DateTime dateTime = new DateTime(int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), int.Parse(match.Groups[4].Value), int.Parse(match.Groups[5].Value), int.Parse(match.Groups[6].Value), int.Parse(match.Groups[7].Value));
            int num = text.IndexOf(" -> ");
            if (num != -1)
            {
                text = text.Substring(0, num);
            }
            return new LsEntry
            {
                Name = text,
                Size = long.Parse(match.Groups[1].Value),
                Flags = lsEntryFlags,
                ModifiedTime = dateTime
            };
        }

        public static string BuildListCommand(string text, bool flag, bool flag2, bool flag3 = false)
        {
            if (flag3)
            {
                return string.Format("/opt/local/bin/gls -a{0}Fl{1} --full-time '{2}'", flag ? "L" : "", flag2 ? "R" : "", text);
            }
            return string.Format("ls -a{0}Fl{1} --full-time '{2}'", flag ? "L" : "", flag2 ? "R" : "", text);
        }
    }
}
