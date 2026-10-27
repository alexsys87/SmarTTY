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
            CheckExclusionMasks();
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

        SyncSettings caseInsensitive = SyncRequest.Create(false, "C:/local", "/remote", "*.LOG", false, false, false, QuickSyncOverwriteTrigger.Stat);
        Check(!IsIncluded("app.log", caseInsensitive.FileFilters), "masks match case-insensitively");

        string[] masks = SyncRequest.SplitMasks("a; b ;;c");
        Equal(3, masks.Length, "SplitMasks drops the empty entries");
        Equal("a", masks[0], "SplitMasks keeps the first mask");
        Equal("b", masks[1], "SplitMasks trims a mask");
        Equal("c", masks[2], "SplitMasks keeps the last mask");
        Equal<string[]>(null, SyncRequest.SplitMasks("   "), "SplitMasks returns null for whitespace only");
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
