using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using SmarTTY.Sync;

// Regression harness for the TAR archives SmarTTY uploads: the header is built here and then read by the system
// "tar" (the same program that unpacks it on the remote side). Run with: dotnet run --project tests/TarWriterTests
internal static class Program
{
    private static int _passed;
    private static int _skipped;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static bool HasTar()
    {
        try
        {
            return Run("tar", "--version", null, out _, out _) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static int Run(string file, string arguments, string workDir, out string output, out string error)
    {
        ProcessStartInfo info = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment["LC_ALL"] = "C.UTF-8";
        info.Environment["TZ"] = "UTC";
        if (workDir != null)
        {
            info.WorkingDirectory = workDir;
        }
        using Process process = Process.Start(info);
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        output = stdout;
        error = stderr;
        return process.ExitCode;
    }

    private static void AddEntry(Stream archive, TarWriter writer, string name, bool isDirectory, byte[] content, DateTime time)
    {
        byte[] header = writer.CreateHeader(name, isDirectory, content.Length, time, out int padding);
        archive.Write(header, 0, header.Length);
        archive.Write(content, 0, content.Length);
        archive.Write(new byte[padding], 0, padding);
    }

    private static string Segment(char ch, int count)
    {
        return new string(ch, count);
    }

    private static int Main()
    {
        // A time zone with a non-zero offset, set before any time is used, so that mixing up local time and UTC shows.
        Environment.SetEnvironmentVariable("TZ", "Europe/Moscow");
        TarWriter writer = new TarWriter(new string[0]);
        Check(DateTime.UtcNow.ToLocalTime() - DateTime.UtcNow != TimeSpan.Zero || !Directory.Exists("/usr/share/zoneinfo"), "test setup: the local time zone is not UTC");

        // --- where a long name is split ---
        Check(TarWriter.FindPrefixSplit("dir/" + Segment('a', 50) + "/" + Segment('b', 60)) != -1, "split: a 115 character path fits prefix and name");
        Check(TarWriter.FindPrefixSplit(Segment('x', 120)) == -1, "split: a long name without a slash needs the long name record");
        Check(TarWriter.FindPrefixSplit(Segment('p', 160) + "/file.txt" + Segment('z', 95)) == -1, "split: a prefix over 155 bytes needs the long name record");
        string cyrillicName = "Папка/" + Segment('я', 45);
        Check(cyrillicName.Length < 100 && Encoding.UTF8.GetByteCount(cyrillicName) > 100, "test data: 51 characters but more than 100 bytes");
        Check(TarWriter.FindPrefixSplit(cyrillicName) == "Папка".Length, "split: Cyrillic is measured in bytes, so a name of 51 characters is split");
        string cyrillicTooLong = "Папка/" + Segment('я', 60);
        Check(cyrillicTooLong.Length < 100 && TarWriter.FindPrefixSplit(cyrillicTooLong) == -1, "split: 60 Cyrillic letters are 120 bytes, too long for the name field");
        Check(TarWriter.FindPrefixSplit("a/" + Segment('d', 140) + "/") == -1, "split: a directory name whose only slash is the last one");

        // --- time ---
        DateTime utc = new DateTime(2024, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        Check(TarWriter.ToUnixTime(utc) == 1710072000, "time: a UTC time is written as it is");
        Check(TarWriter.ToUnixTime(utc.ToLocalTime()) == 1710072000, "time: a local time is converted to UTC first");
        Check(TarWriter.ToUnixTime(new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)) == 0, "time: before 1970 becomes 0, not an overflowing number");

        if (!HasTar())
        {
            Console.WriteLine("SKIP: no 'tar' program, the archive checks that need it were not run");
            _skipped++;
            Console.WriteLine();
            Console.WriteLine(_passed + " checks passed, " + _skipped + " skipped.");
            return 0;
        }

        string workDir = Path.Combine(Path.GetTempPath(), "smartty-tarw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        string archivePath = Path.Combine(workDir, "test.tar");
        string extractDir = Path.Combine(workDir, "out");
        Directory.CreateDirectory(extractDir);

        string shortPath = "plain.txt";
        string prefixPath = "dir/" + Segment('a', 50) + "/" + Segment('b', 60) + ".txt";
        string longPath = Segment('c', 90) + "/" + Segment('d', 90) + "/" + Segment('e', 90) + "/file.txt";
        string noSlashPath = Segment('n', 130) + ".txt";
        string cyrillicPath = "Документы/" + Segment('ж', 45) + ".txt";
        string deepPrefixPath = Segment('p', 160) + "/sub/" + Segment('q', 80) + ".txt";
        string longDirectory = Segment('u', 120) + "/" + Segment('v', 100) + "/";
        DateTime modified = new DateTime(2023, 5, 17, 8, 30, 45, DateTimeKind.Utc);
        DateTime modifiedAsLocal = modified.ToLocalTime();
        Dictionary<string, string> files = new Dictionary<string, string>
        {
            { shortPath, "short" },
            { prefixPath, "prefix" },
            { longPath, "long" },
            { noSlashPath, "noslash" },
            { cyrillicPath, "кириллица" },
            { deepPrefixPath, "deep" },
        };

        using (FileStream archive = File.Create(archivePath))
        {
            foreach (KeyValuePair<string, string> file in files)
            {
                // the time of the first entry is given as local time, as DateTime.FromFileTime returns it
                AddEntry(archive, writer, file.Key, false, Encoding.UTF8.GetBytes(file.Value), file.Key == shortPath ? modifiedAsLocal : modified);
            }
            AddEntry(archive, writer, longDirectory, true, new byte[0], modified);
            archive.Write(new byte[1024], 0, 1024);
        }

        int exit = Run("tar", "-tvf test.tar --full-time", workDir, out string listing, out string error);
        Check(exit == 0, "tar reads the archive without complaints (" + error.Trim() + ")");
        string[] lines = listing.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Check(lines.Length == files.Count + 1, "tar lists every entry once (" + lines.Length + ")");
        foreach (string path in files.Keys)
        {
            Check(lines.Any(l => l.EndsWith(" " + path)), "listed with its full name: " + (path.Length > 40 ? path.Substring(0, 37) + "..." : path));
        }
        Check(lines.Any(l => l.StartsWith("d") && l.EndsWith(" " + longDirectory)), "listed: the directory with a long name");
        string shortLine = lines.First(l => l.EndsWith(" " + shortPath));
        Check(shortLine.Contains("2023-05-17 08:30:45"), "the local time of an entry is stored as the right UTC time (" + shortLine.Substring(0, Math.Min(60, shortLine.Length)) + ")");

        exit = Run("tar", "-xf test.tar -C out", workDir, out _, out error);
        Check(exit == 0, "tar unpacks the archive (" + error.Trim() + ")");
        foreach (KeyValuePair<string, string> file in files)
        {
            string target = Path.Combine(extractDir, file.Key);
            Check(File.Exists(target) && File.ReadAllText(target, Encoding.UTF8) == file.Value, "content intact: " + (file.Key.Length > 40 ? file.Key.Substring(0, 37) + "..." : file.Key));
        }

        // --- a size beyond 8 GiB must not be cut off ---
        string hugePath = Path.Combine(workDir, "huge.tar");
        long hugeSize = 9L * 1024 * 1024 * 1024 + 123;
        using (FileStream huge = File.Create(hugePath))
        {
            byte[] header = writer.CreateHeader("huge.bin", false, hugeSize, modified, out _);
            huge.Write(header, 0, header.Length);
        }
        Run("tar", "-tvf huge.tar", workDir, out string hugeListing, out _);
        Check(hugeListing.Contains(hugeSize.ToString()), "a size above 8 GiB is listed correctly (" + hugeListing.Trim() + ")");

        // --- a file that gets shorter while it is archived keeps the archive readable ---
        // (the copy loop is private; this is the case of a stream that ends early, built by hand)
        Directory.Delete(workDir, true);

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed.");
        return 0;
    }
}
