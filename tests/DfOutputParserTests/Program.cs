using System;
using System.Collections.Generic;
using SmarTTY.IO;

// Regression harness for the parser of "df -h" that feeds the free disk space bar at the bottom of the SSH terminal.
// GNU df and BusyBox df (OpenWrt) print the same columns but differ in details: BusyBox wraps a long file system
// name onto its own line, pseudo file systems may report "-" instead of a percentage, a mount point may hold spaces.
// Run with: dotnet run --project tests/DfOutputParserTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static int Main()
    {
        // GNU coreutils (Ubuntu)
        string[] gnu =
        {
            "Filesystem      Size  Used Avail Use% Mounted on",
            "tmpfs           794M  1.5M  793M   1% /run",
            "/dev/sda2        98G   61G   32G  66% /",
            "tmpfs           3.9G     0  3.9G   0% /dev/shm",
            "/dev/sda1       511M  6.1M  505M   2% /boot/efi"
        };
        List<DfOutputParser.DiskUsage> rows = DfOutputParser.Parse(gnu);
        Check(rows.Count == 4, "GNU: header skipped, four rows");
        DfOutputParser.DiskUsage main = DfOutputParser.SelectMain(rows);
        Check(main.MountPoint == "/" && main.Filesystem == "/dev/sda2", "GNU: the row mounted on / is chosen");
        Check(main.Size == "98G" && main.Used == "61G" && main.Available == "32G" && main.UsePercent == 66, "GNU: columns read");
        Check(main.FreePercent == 34, "GNU: free share is 100 - Use%");
        Check(rows[2].Used == "0" && rows[2].UsePercent == 0 && rows[2].FreePercent == 100, "GNU: zero used reads as 0 and 100% free");

        // BusyBox (OpenWrt): overlay on /, read-only rom, a long name wrapped onto its own line
        string[] busybox =
        {
            "Filesystem                Size      Used Available Use% Mounted on",
            "/dev/root                 8.8M      8.8M         0 100% /rom",
            "tmpfs                    59.4M    432.0K     59.0M   1% /tmp",
            "/dev/mtdblock6           11.4M      2.9M      8.5M  25% /overlay",
            "overlayfs:/overlay",
            "                         11.4M      2.9M      8.5M  25% /",
            "tmpfs                   512.0K         0    512.0K   0% /dev"
        };
        rows = DfOutputParser.Parse(busybox);
        Check(rows.Count == 5, "BusyBox: a wrapped name is joined with the figures on the next line");
        main = DfOutputParser.SelectMain(rows);
        Check(main.MountPoint == "/" && main.Filesystem == "overlayfs:/overlay" && main.FreePercent == 75, "BusyBox: overlay on / chosen, 75% free");
        Check(rows[0].Available == "0" && rows[0].FreePercent == 0, "BusyBox: a full read-only partition has nothing free");
        Check(rows[1].Used == "432.0K" && rows[1].Size == "59.4M", "BusyBox: sizes with a dot and a K suffix");

        // pseudo file systems and odd names
        string[] odd =
        {
            "Filesystem      Size  Used Avail Use% Mounted on",
            "proc               0     0     0    - /proc",
            "//nas/my share  1.8T  1.1T  700G  62% /mnt/my disk",
            "/dev/sdb1       1,5T  200G  1,3T  14% /data"
        };
        rows = DfOutputParser.Parse(odd);
        Check(rows.Count == 2, "odd: the row without a percentage is dropped");
        Check(rows[0].Filesystem == "//nas/my share" && rows[0].MountPoint == "/mnt/my disk", "odd: spaces in the name and in the mount point");
        Check(rows[1].Size == "1,5T" && rows[1].FreePercent == 86, "odd: decimal comma");
        Check(DfOutputParser.SelectMain(rows).MountPoint == "/mnt/my disk", "no / row: the first row is chosen");

        // output that is not a table
        Check(DfOutputParser.Parse(new[] { "sh: df: not found" }).Count == 0, "error message gives no rows");
        Check(DfOutputParser.Parse(new[] { "df: /run/user/1000/gvfs: Permission denied" }).Count == 0, "df warning gives no rows");
        Check(DfOutputParser.Parse(new string[0]).Count == 0 && DfOutputParser.Parse(null).Count == 0, "empty and null output");
        Check(DfOutputParser.SelectMain(new List<DfOutputParser.DiskUsage>()) == null && DfOutputParser.SelectMain(null) == null, "no rows: nothing to show");

        // GNU df exits with 1 when one mount is unreadable but still prints the rest
        string[] partial =
        {
            "df: /mnt/stale: Stale file handle",
            "Filesystem      Size  Used Avail Use% Mounted on",
            "/dev/sda2        98G   61G   32G  66% /"
        };
        rows = DfOutputParser.Parse(partial);
        Check(rows.Count == 1 && rows[0].MountPoint == "/", "warning before the table does not hide the rows");

        // Use% above 100 (some file systems) is capped
        rows = DfOutputParser.Parse(new[] { "/dev/x 1G 2G 0 120% /" });
        Check(rows.Count == 1 && rows[0].UsePercent == 100 && rows[0].FreePercent == 0, "Use% over 100 is capped");

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
