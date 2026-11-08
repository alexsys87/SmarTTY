using System;
using System.Collections.Generic;
using SmarTTY.Ssh.QuickSync;
using SmarTTY.Sync;

// Regression harness for Sync/SyncRequest.cs, the mapping behind the "Synchronize folders" window: the direction and
// the options the window collects become the settings of the synchronization engine, and the "Exclude" text becomes
// the file and directory filters. Run with: dotnet run --project tests/SyncRequestTests
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

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private static int Main()
    {
        try
        {
            CheckDirection();
            CheckOptions();
            CheckStatParsing();
            CheckExclusionMasks();
            CheckRegexExclusions();
            CheckSelection();
            CheckPathNormalizationIsLeftToTheEngine();
            CheckLocalPathDisplay();
            Console.WriteLine();
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void CheckDirection()
    {
        Console.WriteLine("== Direction");
        Equal(SyncDirection.Download, SyncRequest.Create(false, "C:/local", "/remote", null, false, false, false, QuickSyncOverwriteTrigger.Stat).Direction, "download when the upload flag is off");
        Equal(SyncDirection.Upload, SyncRequest.Create(true, "C:/local", "/remote", null, false, false, false, QuickSyncOverwriteTrigger.Stat).Direction, "upload when the upload flag is on");
    }

    private static void CheckOptions()
    {
        Console.WriteLine("== Options");
        SyncSettings plain = SyncRequest.Create(false, "C:/local", "/remote", null, deleteExtraneous: false, skipSymlinks: false, skipHardlinks: false, QuickSyncOverwriteTrigger.Stat);
        Equal(SyncOptions.None, plain.Options, "no options by default");
        Equal(QuickSyncOverwriteTrigger.Stat, plain.QuickSyncOverwriteTrigger, "the overwrite trigger is taken over");

        SyncSettings all = SyncRequest.Create(false, "C:/local", "/remote", null, deleteExtraneous: true, skipSymlinks: true, skipHardlinks: true, QuickSyncOverwriteTrigger.Always);
        Check((all.Options & SyncOptions.DeleteExtraneous) != 0, "delete extraneous is set");
        Check((all.Options & SyncOptions.SkipSymlinks) != 0, "skip symlinks is set");
        Check((all.Options & SyncOptions.SkipHardlinks) != 0, "skip hardlinks is set");
        Equal(QuickSyncOverwriteTrigger.Always, all.QuickSyncOverwriteTrigger, "the overwrite trigger can be Always");
    }

    /// <summary>
    /// The first matching mask decides, exactly like QuickSyncRunner.IsIncluded, which lives with the engine (that
    /// file pulls in the whole SSH layer and is not compiled here).
    /// </summary>
    private static bool IsIncluded(string path, QuickSyncFilter[] filters)
    {
        if (string.IsNullOrEmpty(path) || filters == null)
        {
            return true;
        }
        foreach (QuickSyncFilter filter in filters)
        {
            if (filter.IsMatch(path))
            {
                return filter.Action == QuickSyncAction.Include;
            }
        }
        return false;
    }

    // The listing of the server: "stat -c" prints the name quoted for a shell, and a name may hold quotes and bars.
    private static void CheckStatParsing()
    {
        Console.WriteLine("== Output of stat");
        StatCommandBuilder builder = new StatCommandBuilder(StatCommandBuilder.StatFlavor.GnuLs);
        StatCommandBuilder.StatEntry plain = builder.ParseEntry("StatEntryL:|'report.txt'|1700000000|2049|1234|81a4");
        Equal("report.txt", plain.Name, "stat: a plain name");
        Check(plain.Kind == StatCommandBuilder.StatEntryKind.File && plain.Size == 1234 && plain.ModifiedTime == 1700000000 && plain.DeviceId == 2049, "stat: the numbers of a file");
        StatCommandBuilder.StatEntry dot = builder.ParseEntry("StatEntryL:|'./sub dir'|1700000000|2049|4096|41ed");
        Equal("sub dir", dot.Name, "stat: a name with a blank and a leading ./");
        Check(dot.Kind == StatCommandBuilder.StatEntryKind.Directory, "stat: a directory");
        StatCommandBuilder.StatEntry apostrophe = builder.ParseEntry("StatEntryL:|'./it'\\''s a file.txt'|1700000000|2049|5|81a4");
        Equal("it's a file.txt", apostrophe.Name, "stat: a name with an apostrophe is not cut");
        StatCommandBuilder.StatEntry bar = builder.ParseEntry("StatEntryL:|'./a|b.txt'|1700000000|2049|5|81a4");
        Equal("a|b.txt", bar.Name, "stat: a name with a | is kept (it used to vanish from the listing)");
        StatCommandBuilder.StatEntry barAndApostrophe = builder.ParseEntry("StatEntryL:|'./x|it'\\''s'|1700000000|2049|5|81a4");
        Equal("x|it's", barAndApostrophe.Name, "stat: a name with a | and an apostrophe");
        StatCommandBuilder.StatEntry link = builder.ParseEntry("StatEntryL:|'./latest' -> 'releases/2024 q1'|1700000000|2049|18|a1ff");
        Check(link.Kind == StatCommandBuilder.StatEntryKind.Symlink && link.Name == "latest" && link.LinkTarget == "releases/2024 q1", "stat: a symbolic link and its target");
        StatCommandBuilder.StatEntry linkApostrophe = builder.ParseEntry("StatEntryL:|'./it'\\''s' -> '../o'\\''k'|1700000000|2049|18|a1ff");
        Check(linkApostrophe.Name == "it's" && linkApostrophe.LinkTarget == "../o'k", "stat: apostrophes in the name and in the target of a link");
        StatCommandBuilder.StatEntry newline = builder.ParseEntry("StatEntryL:|'./a'$'\\n''b'|1700000000|2049|5|81a4");
        Equal("a\nb", newline.Name, "stat: a name with a line break in the $'...' form");
        // Lines that are no entries must not stop the run.
        Equal<string>(null, builder.ParseEntry("sh: 1: cd: can't cd to /x/y").Name, "stat: a message of the shell is no entry");
        Equal<string>(null, builder.ParseEntry("a long line without any bars, as a shell may print it").Name, "stat: a long line without bars is no entry (it used to throw)");
        Equal<string>(null, builder.ParseEntry("StatEntryL:|x|y").Name, "stat: a short entry is no entry");
        // BusyBox does not escape: the old way of cutting the quotes is the fallback.
        Equal("plain", new StatCommandBuilder(StatCommandBuilder.StatFlavor.GnuLs).ParseEntry("StatEntryL:|'./plain'|1|2|3|81a4").Name, "stat: busybox form of a plain name");
    }

    private static void CheckExclusionMasks()
    {
        Console.WriteLine("== Exclusion masks");
        SyncSettings none = SyncRequest.Create(false, "C:/local", "/remote", "", false, false, false, QuickSyncOverwriteTrigger.Stat);
        Equal<QuickSyncFilter[]>(null, none.FileFilters, "an empty exclude box keeps every file (null filter list)");

        SyncSettings spaced = SyncRequest.Create(false, "C:/local", "/remote", "  ", false, false, false, QuickSyncOverwriteTrigger.Stat);
        Equal<QuickSyncFilter[]>(null, spaced.FileFilters, "a whitespace-only exclude box keeps every file");

        SyncSettings excluded = SyncRequest.Create(false, "C:/local", "/remote", "*.log; build ;", false, false, false, QuickSyncOverwriteTrigger.Stat);
        Check(excluded.FileFilters != null, "a filled exclude box produces filters");
        Check(!IsIncluded("app.log", excluded.FileFilters), "a *.log mask excludes a log file");
        Check(IsIncluded("app.txt", excluded.FileFilters), "a file that matches no mask is kept");
        // The last mask "build" has no wildcard, so it matches the name "build" alone; the "build" directory is
        // matched by the same masks through DirectoryFilters.
        Check(!IsIncluded("build", excluded.DirectoryFilters), "a plain name mask excludes that directory");

        // $ ^ and | in a mask are characters of a name, not regular expression syntax.
        SyncSettings special = SyncRequest.Create(false, "C:/local", "/remote", "~$*;$RECYCLE.BIN;a^b;c|d", false, false, false, QuickSyncOverwriteTrigger.Stat);
        Check(!IsIncluded("~$report.docx", special.FileFilters), "a ~$* mask excludes the temporary file of Office");
        Check(IsIncluded("~report.docx", special.FileFilters), "...and not a file without the $");
        Check(!IsIncluded("$RECYCLE.BIN", special.DirectoryFilters), "a mask with a leading $ matches that name");
        Check(!IsIncluded("a^b", special.FileFilters) && IsIncluded("a", special.FileFilters) && IsIncluded("b", special.FileFilters), "^ is a character of the name");
        Check(!IsIncluded("c|d", special.FileFilters) && IsIncluded("c", special.FileFilters) && IsIncluded("d", special.FileFilters), "| is no alternative");

        SyncSettings caseInsensitive = SyncRequest.Create(false, "C:/local", "/remote", "*.LOG", false, false, false, QuickSyncOverwriteTrigger.Stat);
        Check(!IsIncluded("app.log", caseInsensitive.FileFilters), "masks match case-insensitively");

        string[] masks = SyncRequest.SplitMasks("a; b ;;c");
        Equal(3, masks.Length, "SplitMasks drops the empty entries");
        Equal("a", masks[0], "SplitMasks keeps the first mask");
        Equal("b", masks[1], "SplitMasks trims a mask");
        Equal("c", masks[2], "SplitMasks keeps the last mask");
        Equal<string[]>(null, SyncRequest.SplitMasks("   "), "SplitMasks returns null for whitespace only");
    }

    /// <summary>
    /// The "Regular expression" mode of the Exclude box: the whole text is one pattern, and the include-all tail keeps
    /// the files the pattern does not match (the engine excludes a name that matches no filter).
    /// </summary>
    private static void CheckRegexExclusions()
    {
        Console.WriteLine("== Regex exclusions");
        SyncSettings empty = SyncRequest.Create(false, "C:/local", "/remote", "", false, false, false, QuickSyncOverwriteTrigger.Stat, excludeIsRegex: true);
        Equal<QuickSyncFilter[]>(null, empty.FileFilters, "an empty regex box keeps every file");

        SyncSettings excluded = SyncRequest.Create(false, "C:/local", "/remote", @"\.(log|tmp)$", false, false, false, QuickSyncOverwriteTrigger.Stat, excludeIsRegex: true);
        Check(excluded.FileFilters != null, "a regex box produces filters");
        Check(!IsIncluded("app.log", excluded.FileFilters), "the regex excludes a matching file");
        Check(!IsIncluded("notes.TMP", excluded.FileFilters), "the regex matches case-insensitively");
        Check(IsIncluded("app.txt", excluded.FileFilters), "a file the regex does not match is kept (include-all tail)");

        SyncSettings dirs = SyncRequest.Create(false, "C:/local", "/remote", @"^(build|\.git)$", false, false, false, QuickSyncOverwriteTrigger.Stat, excludeIsRegex: true);
        Check(!IsIncluded("build", dirs.DirectoryFilters), "the regex also filters directories");
        Check(IsIncluded("src", dirs.DirectoryFilters), "a directory the regex does not match is kept");

        // A pattern with an anchor or a group is used as written (FromRegex takes the raw text).
        SyncSettings anchored = SyncRequest.Create(false, "C:/local", "/remote", "^app.*\\.log$", false, false, false, QuickSyncOverwriteTrigger.Stat, excludeIsRegex: true);
        Check(!IsIncluded("app-2024.log", anchored.FileFilters), "an anchored regex matches a file name");
        Check(IsIncluded("other.log", anchored.FileFilters), "an anchored regex leaves other names alone");

        // Mask mode is untouched by the new flag: the semicolon list still behaves like masks.
        SyncSettings masks = SyncRequest.Create(false, "C:/local", "/remote", "*.log", false, false, false, QuickSyncOverwriteTrigger.Stat, excludeIsRegex: false);
        Check(!IsIncluded("app.log", masks.FileFilters), "mask mode still excludes by wildcard");
        Check(IsIncluded("app.txt", masks.FileFilters), "mask mode still keeps a non-matching file");

        bool thrown = false;
        try
        {
            SyncRequest.Create(false, "C:/local", "/remote", "([", false, false, false, QuickSyncOverwriteTrigger.Stat, excludeIsRegex: true);
        }
        catch (ArgumentException)
        {
            thrown = true;
        }
        Check(thrown, "an invalid regex is reported as an ArgumentException");
    }

    /// <summary>
    /// The "Only selected folders" option: the picked folders narrow the directory filters so that only their subtrees
    /// are synchronized. The engine matches files by name alone, so the selection lives on the directory filters; the
    /// root is deliberately left out, which is what keeps the loose files at the root from being copied.
    /// </summary>
    private static void CheckSelection()
    {
        Console.WriteLine("== Selected folders");

        // Without a selection the directory filters are just the exclude filters, as before.
        SyncSettings all = SyncRequest.Create(false, "C:/local", "/remote", "*.log", false, false, false, QuickSyncOverwriteTrigger.Stat);
        Check(IsIncluded("src", all.DirectoryFilters), "without a selection a folder is included");
        Check(IsIncluded(".", all.DirectoryFilters), "without a selection the root is included");
        Check(!IsIncluded("app.log", all.FileFilters), "the file filters still exclude");

        // A selection keeps the picked folder and its subtree, and drops its siblings.
        SyncSettings picked = SyncRequest.Create(false, "C:/local", "/remote", null, false, false, false, QuickSyncOverwriteTrigger.Stat, selectionPaths: new[] { "src" });
        Check(IsIncluded("src", picked.DirectoryFilters), "a selected folder is included");
        Check(IsIncluded("src/.", picked.DirectoryFilters), "the selected folder is included as a visited directory");
        Check(IsIncluded("src/sub", picked.DirectoryFilters), "a subfolder of a selected folder is included");
        Check(!IsIncluded("docs", picked.DirectoryFilters), "an unselected folder is left out");
        Check(!IsIncluded("docs/.", picked.DirectoryFilters), "an unselected folder is left out as a visited directory");
        Check(!IsIncluded(".", picked.DirectoryFilters), "the root is left out, so loose files at the root are not copied");
        Check(IsIncluded("app.txt", picked.FileFilters), "a selection does not restrict the file filters");

        // The exclude box and the selection coexist: the exclusion is kept, and the selection is added after it.
        SyncSettings both = SyncRequest.Create(false, "C:/local", "/remote", "build", false, false, false, QuickSyncOverwriteTrigger.Stat, selectionPaths: new[] { "src" });
        Check(!IsIncluded("build", both.DirectoryFilters), "an excluded folder is still left out with a selection");
        Check(IsIncluded("src", both.DirectoryFilters), "the selected folder is included next to the exclusion");
        Check(IsIncluded("src/keep", both.DirectoryFilters), "a subfolder of the selection is included next to the exclusion");
        Check(!IsIncluded("docs", both.DirectoryFilters), "an unselected folder is left out next to the exclusion");

        // The selection alone (an empty exclude box) still produces filters, without the include-all tail that would
        // defeat it.
        QuickSyncFilter[] only = SyncRequest.BuildDirectoryFilters("", false, new[] { "src" });
        Equal(1, only.Length, "a selection alone produces the folder filter only");
        Check(IsIncluded("src", only), "the folder filter includes the selected folder");
        Check(!IsIncluded("docs", only), "the folder filter leaves out the rest");

        // CleanSelection normalizes the picker output.
        string[] cleaned = SyncRequest.CleanSelection(new[] { " src ", "src", "docs\\sub", "", "..", "a/../b", "." });
        Equal(2, cleaned.Length, "CleanSelection drops the empty, the duplicate and the climbing entries");
        Equal("src", cleaned[0], "CleanSelection trims and de-duplicates");
        Equal("docs/sub", cleaned[1], "CleanSelection uses forward slashes");
        Equal<string[]>(null, SyncRequest.CleanSelection(null), "CleanSelection returns null for no selection");
        Equal<string[]>(null, SyncRequest.CleanSelection(new[] { "  ", "." }), "CleanSelection returns null when nothing is left");
    }

    private static void CheckPathNormalizationIsLeftToTheEngine()
    {
        Console.WriteLine("== Paths");
        // The engine normalizes the paths itself (QuickSyncRunner.Run), so the builder passes them through unchanged.
        SyncSettings settings = SyncRequest.Create(true, "C:/local/dir", "/home/user/dir", null, false, false, false, QuickSyncOverwriteTrigger.Stat);
        Equal("C:/local/dir", settings.LocalPath, "the local path is passed through");
        Equal("/home/user/dir", settings.RemotePath, "the remote path is passed through");
    }

    private static void CheckLocalPathDisplay()
    {
        Console.WriteLine("== LocalPath display");
        // FromUserPath adds the \\?\ prefix the Win32 long-path calls need; DisplayPath takes it off again, so the
        // path shown to the user (a synchronization warning, a "Failed to delete" error) has the shape they typed.
        LocalPath path = LocalPath.FromUserPath(@"C:\Users\me\project");
        Equal(@"C:\Users\me\project", path.DisplayPath, "DisplayPath drops the long-path prefix");
        Equal(@"\\?\C:\Users\me\project", path.FullPath, "FullPath keeps the long-path prefix");
        Equal(@"\\?\C:\Users\me\project", path.ToString(), "ToString is the raw path (with the prefix)");
        Check(!path.DisplayPath.StartsWith(@"\\?\"), "the displayed path has no long-path prefix");

        // A path built from a raw UNC name has no prefix to drop and stays as it is.
        Equal(@"\\server\share\file", LocalPath.FromRaw(@"\\server\share\file").DisplayPath, "a raw UNC path is shown unchanged");
        Equal("", LocalPath.FromRaw("").DisplayPath, "an empty path is shown unchanged");
        Equal(null, LocalPath.StripLongPathPrefix(null), "StripLongPathPrefix tolerates null");

        // Combine keeps the prefix, so the displayed child path is still the user's shape.
        LocalPath child = LocalPath.FromUserPath(@"C:\data").Combine("sub");
        Equal(@"C:\data\sub", child.DisplayPath, "a combined child path is shown without the prefix");
    }
}
