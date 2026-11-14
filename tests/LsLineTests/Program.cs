using System;
using SmarTTY.Sync;

// Checks of the name field of a line of "ls -l" (Sync/LsLine.cs), which the counting of a remote folder uses for the progress and for
// the exclusions. The name used to be taken after the first colon, i.e. the one in the time "05:49": a file older than half a year has
// the year there instead of a time, and its "name" became the rest of the line ("1 root root 1234 Jan 1 2020 name").
// Run with: dotnet run --project tests/LsLineTests
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

    private static void Name(string line, string expected, string label)
    {
        string actual = LsLine.NameField(line);
        Check(actual == expected, label + " (" + (actual ?? "null") + ")");
    }

    private static int Main()
    {
        try
        {
            Name("-rw-r--r-- 1 root root 1234 Jan  1 05:49 name.txt", "name.txt", "recent file: the name after the time");
            Name("-rw-r--r-- 1 root root 1234 Jan  1  2020 name.txt", "name.txt", "old file: the name after the year, not the whole line");
            Name("-rw-r--r-- 1 root root 1234 Dec 31 23:59 two words.txt", "two words.txt", "a name with a space");
            Name("-rw-r--r-- 1 root root 1234 Jan  1  2020 12:30 meeting", "12:30 meeting", "old file whose name starts like a time");
            Name("-rw-r--r-- 1 root root 1234 Jan  1 05:49 2020 report", "2020 report", "recent file whose name starts like a year");
            Name("-rw-r--r-- 1 root root 1234 Jan  1  2020 2021 notes", "2021 notes", "old file whose name starts like another year");
            Name("-rw-r--r-- 1 root root 1234 Jan  1 05:49 a:b", "a:b", "a colon inside the name");
            Name("lrwxrwxrwx 1 root root 7 Jan  1  2020 link -> target", "link -> target", "a link keeps its target for the caller to cut off");
            Name("drwxr-xr-x 2 root root 4096 Jan  1  2020 dir/", "dir/", "a directory with the -F indicator");
            Name("-rw-r--r--. 1 root root 1234 Jan  1 05:49 selinux", "selinux", "the SELinux dot after the permissions");
            Name("-rw-r--r-- 1 root 1234 Jan  1 05:49 nogroup", "nogroup", "an ls without the group column");
            Name("crw-rw---- 1 root tty 4, 0 Jan  1 05:49 tty0", "tty0", "a device node (major, minor instead of the size)");
            Name("-rw-r--r-- 1 root root 1234 2020-01-01 05:49 iso", "iso", "--time-style=long-iso");
            Name("-rw-r--r-- 1 root root 1234 2020-01-01 05:49:11.123456789 +0000 full", "full", "--time-style=full-iso");
            Name("-rw-r--r-- 1 root root 1234 Jan 01 2020 padded day", "padded day", "a day with a leading zero");
            Name("-rw-r--r-- 1 root root 1234 Sept. 1 05:49 abbreviation with a dot", "abbreviation with a dot", "a month name that is longer, with a dot");
            Check(LsLine.NameField("total 8") == null, "a line without a date is not guessed at (total)");
            Check(LsLine.NameField("-rw-r--r-- 1 root root 1234 янв  1 05:49 name") == null, "a month in another language is left to the caller's fallback");
            Check(LsLine.NameField("") == null, "an empty line");

            Console.WriteLine();
            Console.WriteLine("All " + _passed + " checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
    }
}
