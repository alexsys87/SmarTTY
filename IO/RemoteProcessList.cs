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

/// <summary>The process list and the numbers around it, as read by <see cref="RemoteProcessList.ListCommand"/>.</summary>
public sealed class ProcessSnapshot
{
    public readonly List<RemoteProcess> Processes = new List<RemoteProcess>();

    /// <summary>Processor time of all cores together in clock ticks (the "cpu" line of /proc/stat) and the number of cores.</summary>
    public long TotalTicks;

    public int CpuCount = 1;

    public double? UptimeSeconds;

    /// <summary>"0.10 0.05 0.01" (1, 5 and 15 minutes), or null.</summary>
    public string LoadAverage;

    public long? MemTotalKb;

    /// <summary>MemAvailable, or MemFree on a kernel that does not have it.</summary>
    public long? MemFreeKb;
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
        "while read -r k u n s i w q f t r; do case $k in cpu) tt=$((u+n+s+i+w+q+f+t));; cpu[0-9]*) nc=$((nc+1));; esac; done < /proc/stat; "
        + "echo \"T|$tt|$nc\"; "
        + "{ read -r up r < /proc/uptime; } 2>/dev/null; echo \"U|$up\"; "
        + "{ read -r l1 l2 l3 r < /proc/loadavg; } 2>/dev/null; echo \"L|$l1 $l2 $l3\"; "
        + "while read -r k v r; do case $k in MemTotal:) mt=$v;; MemFree:) mf=$v;; MemAvailable:) ma=$v;; esac; done < /proc/meminfo; echo \"M|$mt|$mf|$ma\"; "
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
            string[] parts = line.Split(new[] { '|' }, line[0] == 'P' ? 9 : 4);
            switch (line[0])
            {
                case 'T':
                    snapshot.TotalTicks = ParseLong(Part(parts, 1)) ?? 0;
                    snapshot.CpuCount = (int)Math.Max(1, ParseLong(Part(parts, 2)) ?? 1);
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
        }
        return snapshot;
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
