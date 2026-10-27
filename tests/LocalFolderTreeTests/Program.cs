using System;
using System.Collections.Generic;
using System.IO;
using SmarTTY.IO;

// Regression harness for the local folder browser's core rules (IO/LocalFolderTree.cs): the roots the tree shows, the
// subdirectories of a node, the "folders first" ordering and path normalization. The rules touch the file system but
// have no UI dependency, so they run here without a window.
// Run with: dotnet run --project tests/LocalFolderTreeTests
internal static class Program
{
    private static int _passed;
    private static string _work;

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
        _work = Path.Combine(Path.GetTempPath(), "smartty-localfoldertree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        try
        {
            TestRoots();
            TestChildDirectories();
            TestChildPath();
            TestNormalizePath();
            TestPathSegments();
            TestDisplayName();
        }
        finally
        {
            try
            {
                Directory.Delete(_work, recursive: true);
            }
            catch
            {
            }
        }
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " local folder tree checks passed.");
        return 0;
    }

    private static void TestRoots()
    {
        List<LocalFolderEntry> roots = LocalFolderTree.GetRoots();
        Check(roots.Count >= 1, "the tree has at least one root");
        Check(roots[0].Name == "This PC", "the first root is This PC");
        Check(roots[0].Path == null, "This PC stands for no folder of its own");
        foreach (LocalFolderEntry root in roots)
        {
            if (root.Path != null)
            {
                Check(Directory.Exists(root.Path), "the root '" + root.Name + "' exists on disk");
            }
        }
        // No root other than "This PC" may appear twice with the same path.
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LocalFolderEntry root in roots)
        {
            if (root.Path != null)
            {
                Check(seen.Add(root.Path), "the root path '" + root.Path + "' is listed once");
            }
        }
        // GetReadyDrives must not hand out the same mount point twice, which is what made GetRoots repeat a node
        // (the CI runner reports /proc/sys/fs/binfmt_misc twice).
        HashSet<string> mounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.IO.DriveInfo drive in LocalFolderTree.GetReadyDrives())
        {
            Check(mounts.Add(drive.Name), "the mount point '" + drive.Name + "' is returned once");
        }
        Check(LocalFolderTree.GetReadyDrives().Count == mounts.Count, "GetReadyDrives returns exactly the unique mounts");
    }

    private static void TestChildDirectories()
    {
        string parent = Path.Combine(_work, "parent");
        Directory.CreateDirectory(Path.Combine(parent, "bravo"));
        Directory.CreateDirectory(Path.Combine(parent, "Alpha"));
        Directory.CreateDirectory(Path.Combine(parent, "charlie"));
        File.WriteAllText(Path.Combine(parent, "a-file.txt"), "x");

        List<string> children = LocalFolderTree.GetChildDirectories(parent);
        Check(children.Count == 3, "only the three directories are returned, not the file");
        Check(children[0] == "Alpha" && children[1] == "bravo" && children[2] == "charlie", "the directories are sorted case-insensitively");
        Check(!children.Contains("a-file.txt"), "a file is not a child directory");

        Check(LocalFolderTree.GetChildDirectories(null).Count == 0, "a null path has no children");
        Check(LocalFolderTree.GetChildDirectories("").Count == 0, "an empty path has no children");
        Check(LocalFolderTree.GetChildDirectories(Path.Combine(_work, "missing")).Count == 0, "a missing folder has no children");

        string empty = Path.Combine(_work, "empty");
        Directory.CreateDirectory(empty);
        Check(LocalFolderTree.GetChildDirectories(empty).Count == 0, "an empty folder has no children");
    }

    private static void TestChildPath()
    {
        string parent = Path.Combine(_work, "work");
        Check(LocalFolderTree.GetChildPath(parent, "project") == Path.Combine(parent, "project"), "a child path is the parent joined with the name");
        Check(LocalFolderTree.GetChildPath(null, "project") == null, "a child of a null parent is null");
        Check(LocalFolderTree.GetChildPath(parent, null) == null, "a child with a null name is null");
        Check(LocalFolderTree.GetChildPath(parent, "") == null, "a child with an empty name is null");
    }

    private static void TestNormalizePath()
    {
        string basePath = Path.Combine(_work, "base");
        Directory.CreateDirectory(basePath);

        Check(LocalFolderTree.NormalizePath(null, basePath) == null, "a null path is not normalized");
        Check(LocalFolderTree.NormalizePath("   ", basePath) == null, "a blank path is not normalized");
        Check(LocalFolderTree.NormalizePath("sub", basePath) == Path.Combine(basePath, "sub"), "a relative path is made absolute against the base");
        Check(LocalFolderTree.NormalizePath(basePath + Path.DirectorySeparatorChar, null) == basePath, "the trailing separator is removed");
        Check(LocalFolderTree.NormalizePath("~", basePath) == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "'~' expands to the user profile");
        Check(LocalFolderTree.NormalizePath("sub/..", basePath) == basePath, "'.' and '..' segments are resolved");
    }

    private static void TestPathSegments()
    {
        string root = Path.GetPathRoot(_work);
        List<string> segments = LocalFolderTree.GetPathSegments(_work);
        Check(segments.Count >= 2, "a full path has at least a root and one folder");
        Check(string.Equals(segments[0], root, StringComparison.OrdinalIgnoreCase), "the first segment is the drive root");
        Check(segments[segments.Count - 1] == _work, "the last segment is the path itself");

        string nested = Path.Combine(_work, "a", "b", "c");
        List<string> deep = LocalFolderTree.GetPathSegments(nested);
        Check(deep.Count == segments.Count + 3, "each folder of a nested path adds one segment");
        Check(deep[deep.Count - 1] == nested, "the last segment of a nested path is the path itself");

        Check(LocalFolderTree.GetPathSegments(null).Count == 0, "a null path has no segments");
    }

    private static void TestDisplayName()
    {
        string project = Path.Combine(_work, "project");
        Directory.CreateDirectory(project);
        Check(LocalFolderTree.GetDisplayName(project) == "project", "the display name is the last folder");
        Check(LocalFolderTree.GetDisplayName(project + Path.DirectorySeparatorChar) == "project", "a trailing separator is ignored in the display name");
        string root = Path.GetPathRoot(_work);
        Check(LocalFolderTree.GetDisplayName(root) == root, "the drive root is its own display name");
        Check(LocalFolderTree.GetDisplayName(null) == "", "a null path has an empty display name");
    }
}
