using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SmarTTY.IO;

/// <summary>One line of the process list of the server.</summary>
public sealed class RemoteProcess
{
    public int Pid;

    public int ParentPid;

    /// <summary>The state letter of the kernel: R running, S sleeping, D waiting for disk, Z zombie, T stopped, I idle.</summary>
    public string State;

    /// <summary>The user name, or the number when /etc/passwd does not know the user.</summary>
    public string User;

    /// <summary>The name the kernel keeps for the process (up to 15 characters), e.g. "sshd".</summary>
    public string Name;

    /// <summary>The command line with its arguments; for a kernel thread, which has none, the name in brackets.</summary>
    public string Command;

    public long MemoryKb;

    /// <summary>Processor time used so far (user + system) in clock ticks.</summary>
    public long Ticks;

    /// <summary>Share of one processor's time since the previous list, 100 is one full core; null in the first list.</summary>
    public double? CpuPercent;
}

/// <summary>The counters of one disk from /proc/diskstats (only whole disks are listed, not their partitions).</summary>
public sealed class DiskDevice
{
    public string Name;

    /// <summary>Milliseconds the disk had requests in progress; it grows by 1000 per second of a busy disk.</summary>
    public long IoMs;

    /// <summary>Sectors (512 bytes each, whatever the disk's own sector size) read and written so far.</summary>
    public long SectorsRead;

    public long SectorsWritten;
}

/// <summary>The process list and the numbers around it, as read by <see cref="RemoteProcessList.ListCommand"/>.</summary>
public sealed class ProcessSnapshot
{
    public readonly List<RemoteProcess> Processes = new List<RemoteProcess>();

    /// <summary>Processor time of all cores together in clock ticks (the "cpu" line of /proc/stat) and the number of cores.</summary>
    public long TotalTicks;

    /// <summary>The part of <see cref="TotalTicks"/> in which the cores did nothing (idle and waiting for input/output); null if unknown.</summary>
    public long? IdleTicks;

    public int CpuCount = 1;

    public double? UptimeSeconds;

    /// <summary>"0.10 0.05 0.01" (1, 5 and 15 minutes), or null.</summary>
    public string LoadAverage;

    public long? MemTotalKb;

    /// <summary>MemAvailable, or MemFree on a kernel that does not have it.</summary>
    public long? MemFreeKb;

    /// <summary>Buffers and page cache: memory that holds copies of disk data and is given back to programs when they need it; null if unknown.</summary>
    public long? MemCachedKb;

    public readonly List<DiskDevice> Disks = new List<DiskDevice>();

    /// <summary>The file system of "/" (df): size and used space in kilobytes, null if df is not there or says nothing.</summary>
    public long? StorageTotalKb;

    public long? StorageUsedKb;

    /// <summary>How busy all cores together were since the previous list, 0 to 100; null in the first list.</summary>
    public double? CpuTotalPercent;

    /// <summary>How busy the busiest disk was since the previous list (the "util" of iostat), 0 to 100; null in the first list or without disks.</summary>
    public double? DiskBusyPercent;

    /// <summary>What the real disks together read and wrote per second since the previous list; null like <see cref="DiskBusyPercent"/>.</summary>
    public double? DiskReadBytesPerSecond;

    public double? DiskWriteBytesPerSecond;

    /// <summary>The name of the busiest disk of the interval, for a tooltip.</summary>
    public string BusiestDisk;
}

/// <summary>
/// The "Process manager" of the Tools menu: a command that lists the processes of the server and the commands that stop one.
/// Nothing is installed on the server and no program is needed besides the shell: the list comes from /proc (read by the shell
/// itself, plus one "tr" per process for the command line), so it works with BusyBox ash on OpenWrt, whose "ps" is very short of
/// columns, as well as with bash on a full Linux. The processor share is worked out here from two lists.
/// </summary>
public static class RemoteProcessList
{
    // Written for a POSIX sh without awk, sed, ps, stat or any other helper. The redirections that can fail (a process that
    // ends while it is read) are inside braces that send their messages to /dev/null. Fields after "(name) " in /proc/N/stat:
    // $1 state, $2 parent, $12 utime, $13 stime.
    private const string Script =
        "while read -r k u n s i w q f t r; do case $k in cpu) tt=$((u+n+s+i+w+q+f+t)); ti=$((i+w));; cpu[0-9]*) nc=$((nc+1));; esac; done < /proc/stat; "
        + "echo \"T|$tt|$nc|$ti\"; "
        + "{ read -r up r < /proc/uptime; } 2>/dev/null; echo \"U|$up\"; "
        + "{ read -r l1 l2 l3 r < /proc/loadavg; } 2>/dev/null; echo \"L|$l1 $l2 $l3\"; "
        + "while read -r k v r; do case $k in MemTotal:) mt=$v;; MemFree:) mf=$v;; MemAvailable:) ma=$v;; Buffers:) mb=$v;; Cached:) mc=$v;; esac; done < /proc/meminfo; echo \"M|$mt|$mf|$ma|$mb|$mc\"; "
        // Whole disks only: a name that is a directory of /sys/block is a disk, its partitions are one level deeper. The sixth
        // and tenth numbers of a line are the sectors read and written, the thirteenth the milliseconds with a request pending.
        + "{ while read -r mj mn dn rc rm rs rt wc wm ws wt fl io wq r; do [ -e \"/sys/block/$dn\" ] && echo \"D|$dn|$io|$rs|$ws\"; done < /proc/diskstats; } 2>/dev/null; "
        // POSIX output keeps a long device name on one line; the header line is skipped.
        + "df -P -k / 2>/dev/null | { read -r h; read -r fs dt du da dp dm; echo \"S|$dt|$du|$dm\"; }; "
        + "{ while IFS=: read -r n x u r; do echo \"N|$u|$n\"; done < /etc/passwd; } 2>/dev/null; "
        + "for d in /proc/[0-9]*; do "
        + "p=${d#/proc/}; "
        + "{ read -r line < \"$d/stat\"; } 2>/dev/null || continue; "
        + "c=${line#*(}; comm=${c%) *}; rest=${c##*) }; "
        + "set -- $rest; "
        + "st=$1; pp=$2; tk=$(( ${12:-0} + ${13:-0} )); uid=0; rss=0; "
        + "{ while read -r k a b; do case $k in Uid:) uid=$a;; VmRSS:) rss=$a;; esac; done < \"$d/status\"; } 2>/dev/null; "
        + "printf 'P|%s|%s|%s|%s|%s|%s|%s|' \"$p\" \"$pp\" \"$st\" \"$tk\" \"$rss\" \"$uid\" \"$comm\"; "
        + "{ tr '\\0\\n' '  ' < \"$d/cmdline\"; } 2>/dev/null; echo; "
        + "done";

    /// <summary>The command that prints the list; it is the same sh whatever shell the user logs in with.</summary>
    public static string ListCommand => "sh -c " + RemotePathUtils.ShellQuote(Script);

    /// <summary>
    /// The command that sends a signal to a process: "kill -15" (end: the program may clean up) or, with
    /// <paramref name="force"/>, "kill -9" (stop it at once). Numbers are used because every kill knows them.
    /// </summary>
    public static string SignalCommand(int pid, bool force)
    {
        return "kill -" + (force ? "9" : "15") + " " + pid.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads what <see cref="ListCommand"/> printed. <paramref name="previous"/> is the list before it: the processor share of
    /// each process is the part of the processor time of all cores that it used in between. Lines that are not part of the list
    /// (a message of the shell) are skipped.
    /// </summary>
    public static ProcessSnapshot Parse(IEnumerable<string> lines, ProcessSnapshot previous)
    {
        ProcessSnapshot snapshot = new ProcessSnapshot();
        Dictionary<string, string> users = new Dictionary<string, string>();
        foreach (string raw in lines ?? Enumerable.Empty<string>())
        {
            string line = (raw ?? "").TrimEnd('\r');
            if (line.Length < 2 || line[1] != '|')
            {
                continue;
            }
            string[] parts = line.Split(new[] { '|' }, line[0] == 'P' ? 9 : line[0] == 'D' ? 5 : line[0] == 'M' ? 6 : 4);
            switch (line[0])
            {
                case 'T':
                    snapshot.TotalTicks = ParseLong(Part(parts, 1)) ?? 0;
                    snapshot.CpuCount = (int)Math.Max(1, ParseLong(Part(parts, 2)) ?? 1);
                    snapshot.IdleTicks = ParseLong(Part(parts, 3));
                    break;
                case 'U':
                    if (double.TryParse(Part(parts, 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double uptime))
                    {
                        snapshot.UptimeSeconds = uptime;
                    }
                    break;
                case 'L':
                    snapshot.LoadAverage = string.IsNullOrWhiteSpace(Part(parts, 1)) ? null : Part(parts, 1).Trim();
                    break;
                case 'M':
                    snapshot.MemTotalKb = ParseLong(Part(parts, 1));
                    snapshot.MemFreeKb = ParseLong(Part(parts, 3)) ?? ParseLong(Part(parts, 2));
                    long? buffers = ParseLong(Part(parts, 4));
                    long? cached = ParseLong(Part(parts, 5));
                    snapshot.MemCachedKb = (buffers.HasValue || cached.HasValue) ? (buffers ?? 0) + (cached ?? 0) : (long?)null;
                    break;
                case 'D':
                    if (Part(parts, 1).Length > 0 && ParseLong(Part(parts, 2)) != null)
                    {
                        snapshot.Disks.Add(new DiskDevice
                        {
                            Name = Part(parts, 1),
                            IoMs = ParseLong(Part(parts, 2)).Value,
                            SectorsRead = ParseLong(Part(parts, 3)) ?? 0,
                            SectorsWritten = ParseLong(Part(parts, 4)) ?? 0
                        });
                    }
                    break;
                case 'S':
                    snapshot.StorageTotalKb = ParseLong(Part(parts, 1));
                    snapshot.StorageUsedKb = ParseLong(Part(parts, 2));
                    break;
                case 'N':
                    // The first entry of a number wins, as it does for the system.
                    if (!users.ContainsKey(Part(parts, 1)))
                    {
                        users[Part(parts, 1)] = Part(parts, 2);
                    }
                    break;
                case 'P':
                    {
                        if (parts.Length < 9 || ParseLong(parts[1]) == null)
                        {
                            break;
                        }
                        string name = parts[7];
                        string command = Clean(parts[8]).Trim();
                        string uid = parts[6].Trim();
                        snapshot.Processes.Add(new RemoteProcess
                        {
                            Pid = (int)ParseLong(parts[1]).Value,
                            ParentPid = (int)(ParseLong(parts[2]) ?? 0),
                            State = parts[3].Trim(),
                            Ticks = ParseLong(parts[4]) ?? 0,
                            MemoryKb = ParseLong(parts[5]) ?? 0,
                            // The number until the names are known (they are printed before the processes).
                            User = uid,
                            Name = Clean(name),
                            Command = command.Length > 0 ? command : "[" + Clean(name) + "]"
                        });
                        break;
                    }
            }
        }
        foreach (RemoteProcess process in snapshot.Processes)
        {
            if (users.TryGetValue(process.User, out string userName) && userName.Length > 0)
            {
                process.User = userName;
            }
        }
        if (previous != null && snapshot.TotalTicks > previous.TotalTicks)
        {
            Dictionary<int, long> before = new Dictionary<int, long>();
            foreach (RemoteProcess old in previous.Processes)
            {
                before[old.Pid] = old.Ticks;
            }
            double total = snapshot.TotalTicks - previous.TotalTicks;
            foreach (RemoteProcess process in snapshot.Processes)
            {
                if (before.TryGetValue(process.Pid, out long oldTicks) && process.Ticks >= oldTicks)
                {
                    process.CpuPercent = Math.Round((process.Ticks - oldTicks) / total * 100.0 * snapshot.CpuCount, 1);
                }
            }
            if (snapshot.IdleTicks.HasValue && previous.IdleTicks.HasValue && snapshot.IdleTicks.Value >= previous.IdleTicks.Value)
            {
                double idle = snapshot.IdleTicks.Value - previous.IdleTicks.Value;
                snapshot.CpuTotalPercent = Math.Round(Math.Max(0.0, Math.Min(100.0, (1.0 - idle / total) * 100.0)), 1);
            }
        }
        if (previous != null)
        {
            WorkOutDiskLoad(snapshot, previous);
        }
        return snapshot;
    }

    // Disks that are not hardware: their work is the work of the disk below them, which is listed itself.
    private static bool IsVirtualDisk(string name)
    {
        return name.StartsWith("loop", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal)
            || name.StartsWith("zram", StringComparison.Ordinal) || name.StartsWith("dm-", StringComparison.Ordinal)
            || name.StartsWith("md", StringComparison.Ordinal) || name.StartsWith("nbd", StringComparison.Ordinal);
    }

    // The time of the interval is the uptime difference (the server's own clock, whatever the delay of the network was).
    private static void WorkOutDiskLoad(ProcessSnapshot snapshot, ProcessSnapshot previous)
    {
        if (!snapshot.UptimeSeconds.HasValue || !previous.UptimeSeconds.HasValue)
        {
            return;
        }
        double seconds = snapshot.UptimeSeconds.Value - previous.UptimeSeconds.Value;
        if (seconds <= 0)
        {
            return;
        }
        Dictionary<string, DiskDevice> before = new Dictionary<string, DiskDevice>();
        foreach (DiskDevice old in previous.Disks)
        {
            before[old.Name] = old;
        }
        double busiest = -1;
        double read = 0;
        double written = 0;
        foreach (DiskDevice disk in snapshot.Disks)
        {
            // A counter that went down (a disk that was removed and came back) says nothing about the interval.
            if (IsVirtualDisk(disk.Name) || !before.TryGetValue(disk.Name, out DiskDevice old)
                || disk.IoMs < old.IoMs || disk.SectorsRead < old.SectorsRead || disk.SectorsWritten < old.SectorsWritten)
            {
                continue;
            }
            double busy = Math.Min(100.0, (disk.IoMs - old.IoMs) / (seconds * 1000.0) * 100.0);
            if (busy > busiest)
            {
                busiest = busy;
                snapshot.BusiestDisk = disk.Name;
            }
            read += (disk.SectorsRead - old.SectorsRead) * 512.0;
            written += (disk.SectorsWritten - old.SectorsWritten) * 512.0;
        }
        if (busiest >= 0)
        {
            snapshot.DiskBusyPercent = Math.Round(busiest, 1);
            snapshot.DiskReadBytesPerSecond = read / seconds;
            snapshot.DiskWriteBytesPerSecond = written / seconds;
        }
    }

    /// <summary>"1.2 MB/s", "300 kB/s", "0 kB/s": a speed in bytes per second for people.</summary>
    public static string FormatRate(double bytesPerSecond)
    {
        return FormatKb((long)Math.Round(Math.Max(0.0, bytesPerSecond) / 1024.0)) + "/s";
    }

    /// <summary>"1.5 GB", "320 MB", "12 kB": a size in kilobytes for people.</summary>
    public static string FormatKb(long kilobytes)
    {
        if (kilobytes >= 1024L * 1024 * 10)
        {
            return (kilobytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        }
        if (kilobytes >= 1024 * 10)
        {
            return (kilobytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }
        return kilobytes.ToString(CultureInfo.InvariantCulture) + " kB";
    }

    /// <summary>The state letter in words, for a tooltip or a column.</summary>
    public static string DescribeState(string state)
    {
        switch ((state ?? "").Trim())
        {
            case "R": return "running";
            case "S": return "sleeping";
            case "D": return "waiting for disk";
            case "Z": return "zombie";
            case "T": return "stopped";
            case "t": return "traced";
            case "I": return "idle";
            case "X": return "dead";
            default: return "";
        }
    }

    private static string Part(string[] parts, int index)
    {
        return index < parts.Length ? parts[index] : "";
    }

    private static long? ParseLong(string text)
    {
        return long.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : (long?)null;
    }

    // Control characters of a command line (escape sequences of a program that rewrote its title) must not reach the screen.
    private static string Clean(string text)
    {
        return new string((text ?? "").Select(c => c < ' ' || c == '\u007f' ? ' ' : c).ToArray());
    }
}
