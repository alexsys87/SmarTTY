using System;
using System.IO;
using SmarTTY.Sync;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Checks the "delete the local copies" command of the transfer queue, <see cref="QuickSyncRunner.DeleteLocalItems"/>,
/// against a real folder. The local file system layer behind it is the Win32 one (Sync/LocalFileSystem.cs), so the
/// checks run on Windows only and are skipped with a notice elsewhere.
/// </summary>
internal static class LocalDeleteTests
{
    public static void Run()
    {
        Test.Section("Local deletion (QuickSyncRunner.DeleteLocalItems)");
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: the local file system layer is the Win32 one, so the deletion checks run on Windows only.");
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "smartty-local-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string keep = Path.Combine(root, "keep.txt");
            string gone = Path.Combine(root, "gone.txt");
            string folder = Path.Combine(root, "folder");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "inner.txt"), "inner");
            File.WriteAllText(keep, "keep");
            File.WriteAllText(gone, "gone");

            // A named file and a named folder (with its contents) go; the file that was not named stays.
            new QuickSyncRunner(null).DeleteLocalItems(root, new[] { "gone.txt", "folder" }, null);

            Test.Check(!File.Exists(gone), "a named file is deleted");
            Test.Check(!Directory.Exists(folder), "a named folder is deleted with its contents");
            Test.Check(File.Exists(keep), "a file that was not named is kept");

            // A name that is not there is tolerated and leaves the folder alone.
            new QuickSyncRunner(null).DeleteLocalItems(root, new[] { "missing.txt" }, null);
            Test.Check(File.Exists(keep), "deleting a missing name leaves the folder alone");

            // Names are matched case-insensitively, like the rest of the engine.
            File.WriteAllText(Path.Combine(root, "Case.TXT"), "x");
            new QuickSyncRunner(null).DeleteLocalItems(root, new[] { "case.txt" }, null);
            Test.Check(!File.Exists(Path.Combine(root, "Case.TXT")), "names are matched case-insensitively");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }
}
