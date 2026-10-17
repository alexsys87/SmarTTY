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
        if (callback("stat -c \"path=%N\" \"" + testPath + "\"")?.FirstOrDefault((string line) => line.StartsWith("path=")) != null)
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
        if (callback("stat -f \"path=%N\" \"" + testPath + "\"")?.FirstOrDefault((string line) => line.StartsWith("path=")) != null)
        {
            return StatFlavor.Bsd;
        }
        return StatFlavor.Unknown;
    }

    public string GetListCommand(string dirPath, bool followSymlinks = false)
    {
        string symlinkFlag = (followSymlinks ? "L" : "");
        string findSymlinkFlag = (followSymlinks ? "-L" : "");
        if (this.Flavor == StatFlavor.Bsd)
        {
            return "LANG=en_US cd \"" + dirPath + "\" && ls -1A" + symlinkFlag + " | xargs -d \"\\n\" stat -" + symlinkFlag + "f \"StatEntryB:|%N|%Dm|%d|%z|%Xp|%Y\" .";
        }
        if (this.Flavor == StatFlavor.GnuFindNullSeparated)
        {
            return "LANG=en_US cd \"" + dirPath + "\" && find . " + findSymlinkFlag + " -maxdepth 1 -print0 | xargs -0 stat -" + symlinkFlag + "c \"StatEntryL:|%N|%Y|%d|%s|%f\"";
        }
        if (this.Flavor == StatFlavor.GnuFindNewlineSeparated)
        {
            return "LANG=en_US cd \"" + dirPath + "\" && find . " + findSymlinkFlag + " -maxdepth 1 -print | xargs stat -" + symlinkFlag + "c \"StatEntryL:|%N|%Y|%d|%s|%f\"";
        }
        return "LANG=en_US cd \"" + dirPath + "\" && ls -1A" + symlinkFlag + " | xargs -d \"\\n\" stat -" + symlinkFlag + "c \"StatEntryL:|%N|%Y|%d|%s|%f\" .";
    }

    public string GetStatCommand(string path, bool followSymlinks)
    {
        string symlinkFlag = (followSymlinks ? " -L" : "");
        if (this.Flavor == StatFlavor.Bsd)
        {
            return "stat" + symlinkFlag + " -f \"StatEntryB:|%N|%Dm|%d|%z|%Xp|%Y\" \"" + path + "\"";
        }
        return "stat" + symlinkFlag + " -c \"StatEntryL:|%N|%Y|%d|%s|%f\" \"" + path + "\"";
    }

    public StatEntry ParseEntry(string line)
    {
        StatEntry result = default;
        if (line.Length < 6)
        {
            return default;
        }
        string[] parts = line.Split('|');
        if (!long.TryParse(parts[2], out result.ModifiedTime))
        {
            return default;
        }
        if (!int.TryParse(parts[3], out result.DeviceId))
        {
            return default;
        }
        if (!long.TryParse(parts[4], out result.Size))
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
                result.Kind = StatEntryKind.File;
                break;
            case 4:
                result.Kind = StatEntryKind.Directory;
                break;
            case 10:
                result.Kind = StatEntryKind.Symlink;
                break;
        }
        if (line.StartsWith("StatEntryL:|"))
        {
            if (parts.Length != 6)
            {
                return default;
            }
            int arrowIndex = parts[1].IndexOf("->");
            if (arrowIndex == -1)
            {
                result.Name = parts[1].Trim(' ', '\'');
            }
            else
            {
                result.Name = parts[1].Substring(0, arrowIndex).Trim(' ', '\'');
                result.LinkTarget = parts[1].Substring(arrowIndex + 2).Trim(' ', '\'');
            }
        }
        else if (line.StartsWith("StatEntryB:|"))
        {
            if (parts.Length != 7)
            {
                return default;
            }
            result.Name = parts[1];
            result.LinkTarget = parts[6];
        }
        string name = result.Name;
        if (name != null && name.StartsWith("./"))
        {
            result.Name = result.Name.Substring(2);
        }
        return result;
    }
}
