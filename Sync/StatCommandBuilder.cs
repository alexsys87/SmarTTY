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

    public delegate IEnumerable<string> CommandRunner(string text);

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

    public static StatFlavor DetectFlavor(CommandRunner callback, string text2 = ".")
    {
        if (callback("stat -c \"path=%N\" \"" + text2 + "\"")?.FirstOrDefault((string text) => text.StartsWith("path=")) != null)
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
        if (callback("stat -f \"path=%N\" \"" + text2 + "\"")?.FirstOrDefault((string text) => text.StartsWith("path=")) != null)
        {
            return StatFlavor.Bsd;
        }
        return StatFlavor.Unknown;
    }

    public string GetListCommand(string text3, bool flag = false)
    {
        string text = (flag ? "L" : "");
        string text2 = (flag ? "-L" : "");
        if (this.Flavor == StatFlavor.Bsd)
        {
            return "LANG=en_US cd \"" + text3 + "\" && ls -1A" + text + " | xargs -d \"\\n\" stat -" + text + "f \"StatEntryB:|%N|%Dm|%d|%z|%Xp|%Y\" .";
        }
        if (this.Flavor == StatFlavor.GnuFindNullSeparated)
        {
            return "LANG=en_US cd \"" + text3 + "\" && find . " + text2 + " -maxdepth 1 -print0 | xargs -0 stat -" + text + "c \"StatEntryL:|%N|%Y|%d|%s|%f\"";
        }
        if (this.Flavor == StatFlavor.GnuFindNewlineSeparated)
        {
            return "LANG=en_US cd \"" + text3 + "\" && find . " + text2 + " -maxdepth 1 -print | xargs stat -" + text + "c \"StatEntryL:|%N|%Y|%d|%s|%f\"";
        }
        return "LANG=en_US cd \"" + text3 + "\" && ls -1A" + text + " | xargs -d \"\\n\" stat -" + text + "c \"StatEntryL:|%N|%Y|%d|%s|%f\" .";
    }

    public string GetStatCommand(string text2, bool flag)
    {
        string text = (flag ? " -L" : "");
        if (this.Flavor == StatFlavor.Bsd)
        {
            return "stat" + text + " -f \"StatEntryB:|%N|%Dm|%d|%z|%Xp|%Y\" \"" + text2 + "\"";
        }
        return "stat" + text + " -c \"StatEntryL:|%N|%Y|%d|%s|%f\" \"" + text2 + "\"";
    }

    public StatEntry ParseEntry(string text2)
    {
        StatEntry result = default;
        if (text2.Length < 6)
        {
            return default;
        }
        string[] array = text2.Split('|');
        if (!long.TryParse(array[2], out result.ModifiedTime))
        {
            return default;
        }
        if (!int.TryParse(array[3], out result.DeviceId))
        {
            return default;
        }
        if (!long.TryParse(array[4], out result.Size))
        {
            return default;
        }
        if (!int.TryParse(array[5], NumberStyles.HexNumber, null, out var result2))
        {
            return default;
        }
        switch ((result2 >> 12) & 0xF)
        {
            case 8:
                result.Kind = StatEntryKind.File;
                break;
            case 4:
                result.Kind = StatEntryKind.Directory;
                break;
            case 10:
                result.Kind = StatEntryKind.Symlink;
                break;
        }
        if (text2.StartsWith("StatEntryL:|"))
        {
            if (array.Length != 6)
            {
                return default;
            }
            int num = array[1].IndexOf("->");
            if (num == -1)
            {
                result.Name = array[1].Trim(' ', '\'');
            }
            else
            {
                result.Name = array[1].Substring(0, num).Trim(' ', '\'');
                result.LinkTarget = array[1].Substring(num + 2).Trim(' ', '\'');
            }
        }
        else if (text2.StartsWith("StatEntryB:|"))
        {
            if (array.Length != 7)
            {
                return default;
            }
            result.Name = array[1];
            result.LinkTarget = array[6];
        }
        string text = result.Name;
        if (text != null && text.StartsWith("./"))
        {
            result.Name = result.Name.Substring(2);
        }
        return result;
    }
}
