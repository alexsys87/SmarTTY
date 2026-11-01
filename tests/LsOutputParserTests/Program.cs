using System;
using SmarTTY.IO;

// Regression harness for the side file manager listing parser. GNU ls prints fractional seconds with
// --full-time, BusyBox ls (OpenWrt) does not; both must be parsed.
// Run with: dotnet run --project tests/LsOutputParserTests
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
        LsOutputParser.LsEntry gnuDir = LsOutputParser.LsParser.ParseLine("drwxr-xr-x 2 root root 4096 2026-10-19 19:30:28.965886611 +0000 sub/");
        Check(gnuDir.Name == "sub" && gnuDir.IsDirectory && gnuDir.Size == 4096, "GNU directory line");

        LsOutputParser.LsEntry gnuExe = LsOutputParser.LsParser.ParseLine("-rwxr-xr-x 1 root root    3 2026-10-19 19:30:28.965886611 +0000 a.txt*");
        Check(gnuExe.Name == "a.txt" && gnuExe.IsExecutable && !gnuExe.IsDirectory, "GNU executable line");

        // BusyBox (OpenWrt): no fractional seconds, wide padding
        LsOutputParser.LsEntry bbDir = LsOutputParser.LsParser.ParseLine("drwxr-xr-x    2 root     root          4096 2026-10-19 19:30:28 +0000 sub/");
        Check(bbDir.Name == "sub" && bbDir.IsDirectory && bbDir.Size == 4096, "BusyBox directory line");
        Check(bbDir.ModifiedTime == new DateTime(2026, 10, 19, 19, 30, 28), "BusyBox timestamp parsed");

        LsOutputParser.LsEntry bbFile = LsOutputParser.LsParser.ParseLine("-rw-r--r--    1 root     root             0 2026-10-19 19:30:39 +0000 file with space.txt");
        Check(bbFile.Name == "file with space.txt" && !bbFile.IsDirectory && bbFile.Size == 0, "BusyBox name with spaces");

        LsOutputParser.LsEntry bbParent = LsOutputParser.LsParser.ParseLine("drwxr-xr-x    3 root     root          4096 2026-10-19 19:30:28 +0000 ../");
        Check(bbParent.Name == "..", "BusyBox parent entry");

        LsOutputParser.LsEntry bbLink = LsOutputParser.LsParser.ParseLine("lrwxrwxrwx    1 root     root             5 2026-10-19 19:30:28 +0000 lnk -> a.txt*");
        Check(bbLink.Name == "lnk", "BusyBox symlink without -L");

        // The mode column is kept, and the other columns still come out of the right groups.
        Check(gnuExe.Mode == "-rwxr-xr-x" && RemotePermissions.ParseMode(gnuExe.Mode) == Convert.ToInt32("755", 8), "GNU mode column");
        Check(bbFile.Mode == "-rw-r--r--" && RemotePermissions.ParseMode(bbFile.Mode) == Convert.ToInt32("644", 8) && bbFile.Size == 0, "BusyBox mode column");
        LsOutputParser.LsEntry setgidDir = LsOutputParser.LsParser.ParseLine("drwxrwsr-x. 2 root staff 4096 2026-10-19 19:30:28.965886611 +0000 shared/");
        Check(setgidDir.Name == "shared" && setgidDir.IsDirectory && RemotePermissions.ParseMode(setgidDir.Mode) == Convert.ToInt32("2775", 8), "mode with a setgid bit and an SELinux dot");

        // The colour of a name by its permissions: the most telling property wins.
        Check(RemotePermissions.Classify("-rwxr-xr-x") == PermissionHighlight.Executable, "executable file");
        Check(RemotePermissions.Classify("drwxr-xr-x") == PermissionHighlight.None, "an ordinary folder is not highlighted although it has execute bits");
        Check(RemotePermissions.Classify("-rw-r--r--") == PermissionHighlight.None, "ordinary file");
        Check(RemotePermissions.Classify("-r--r--r--") == PermissionHighlight.ReadOnly, "read-only file");
        Check(RemotePermissions.Classify("dr-xr-xr-x") == PermissionHighlight.ReadOnly, "read-only folder");
        Check(RemotePermissions.Classify("-rw-rw-rw-") == PermissionHighlight.WorldWritable, "world-writable file");
        Check(RemotePermissions.Classify("-rwxrwxrwx") == PermissionHighlight.WorldWritable, "world-writable beats executable");
        Check(RemotePermissions.Classify("drwxrwxrwx") == PermissionHighlight.WorldWritable, "world-writable folder");
        Check(RemotePermissions.Classify("drwxrwxrwt") == PermissionHighlight.None, "a sticky world-writable folder like /tmp is not highlighted");
        Check(RemotePermissions.Classify("-rwsr-xr-x") == PermissionHighlight.SetId, "setuid file");
        Check(RemotePermissions.Classify("drwxrwsr-x.") == PermissionHighlight.SetId, "setgid folder with an SELinux dot");
        Check(RemotePermissions.Classify("-rwsrwxrwx") == PermissionHighlight.WorldWritable, "world-writable beats setuid");
        Check(RemotePermissions.Classify("lrwxrwxrwx") == PermissionHighlight.None, "a symlink is never highlighted");
        Check(RemotePermissions.Classify(null) == PermissionHighlight.None && RemotePermissions.Classify("garbage") == PermissionHighlight.None, "no mode, no colour");
        Check(RemotePermissions.DescribeHighlight(PermissionHighlight.None) == null && RemotePermissions.DescribeHighlight(PermissionHighlight.SetId) == "setuid or setgid", "colour reasons");

        // BusyBox ls of OpenWrt colours its output when it writes to a terminal (the completion lists with a PTY), and
        // so does GNU ls with --color: the codes must not end up in the names shown by the completion popup.
        LsOutputParser.LsEntry bbColourDir = LsOutputParser.LsParser.ParseLine("drwxr-xr-x    2 root     root          4096 2026-11-01 21:05:49 +0000 \x1b[1;34msubdir\x1b[m/");
        Check(bbColourDir.Name == "subdir" && bbColourDir.IsDirectory, "BusyBox coloured directory");
        LsOutputParser.LsEntry bbColourExe = LsOutputParser.LsParser.ParseLine("-rwxr-xr-x    1 root     root             0 2026-11-01 21:05:55 +0000 \x1b[1;32mrun.sh\x1b[m*");
        Check(bbColourExe.Name == "run.sh" && bbColourExe.IsExecutable, "BusyBox coloured executable");
        LsOutputParser.LsEntry bbColourLink = LsOutputParser.LsParser.ParseLine("lrwxrwxrwx    1 root     root             6 2026-11-01 21:05:55 +0000 \x1b[1;36mlnk\x1b[m -> \x1b[1;34msubdir\x1b[m/");
        Check(bbColourLink.Name == "lnk", "BusyBox coloured symlink");
        LsOutputParser.LsEntry gnuColourDot = LsOutputParser.LsParser.ParseLine("drwxr-xr-x  3 root root 4096 2026-11-01 21:05:55.295631772 +0000 \x1b[0m\x1b[01;34m.\x1b[0m/");
        Check(gnuColourDot.Name == "." && gnuColourDot.IsDirectory, "GNU coloured entry with a leading reset");
        LsOutputParser.LsEntry gnuHyperlink = LsOutputParser.LsParser.ParseLine("-rw-r--r-- 1 root root 3 2026-10-19 19:30:28.965886611 +0000 \x1b]8;;file://host/tmp/a.txt\x1b\\a.txt\x1b]8;;\x1b\\");
        Check(gnuHyperlink.Name == "a.txt", "GNU hyperlinked name");
        Check(LsOutputParser.LsParser.StripEscapeSequences("plain name") == "plain name", "a line without escapes stays as it is");
        Check(LsOutputParser.LsParser.StripEscapeSequences("cut\x1b[1;3") == "cut", "an escape cut off at the end of the line is dropped");
        Check(LsOutputParser.LsParser.ParseLine(null).Name == null, "no line, no entry");

        Check(LsOutputParser.LsParser.ParseLine("total 20").Name == null, "total line ignored");
        Check(LsOutputParser.LsParser.ParseLine("").Name == null, "empty line ignored");

        Check(LsOutputParser.LsParser.BuildListCommand("/tmp/it's here", followSymlinks: false, recursive: false) == "ls -aFl --full-time '/tmp/it'\\''s here'", "a quote in the listed directory is escaped");
        Check(LsOutputParser.LsParser.BuildListCommand("/srv", followSymlinks: true, recursive: false) == "ls -aLFl --full-time '/srv'", "follow symlinks");

        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
