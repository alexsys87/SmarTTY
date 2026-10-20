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

        Check(LsOutputParser.LsParser.ParseLine("total 20").Name == null, "total line ignored");
        Check(LsOutputParser.LsParser.ParseLine("").Name == null, "empty line ignored");

        Check(LsOutputParser.LsParser.BuildListCommand("/tmp/it's here", followSymlinks: false, recursive: false) == "ls -aFl --full-time '/tmp/it'\\''s here'", "a quote in the listed directory is escaped");
        Check(LsOutputParser.LsParser.BuildListCommand("/srv", followSymlinks: true, recursive: false) == "ls -aLFl --full-time '/srv'", "follow symlinks");

        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
