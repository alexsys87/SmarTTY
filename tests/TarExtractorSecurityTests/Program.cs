using System;
using System.IO;
using System.Text;
using SmarTTY.Sync;

// Regression harness for the TAR extractor of directory downloads: entry names that try to leave the target
// folder, names Windows cannot hold, the GNU/POSIX header formats and the conversion of absolute link targets.
// Run with: dotnet run --project tests/TarExtractorSecurityTests
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

    private static byte[] Header(string name, long size, byte typeFlag, bool posixMagic, string prefix = null, bool junkAt345 = false)
    {
        byte[] header = new byte[512];
        Encoding.UTF8.GetBytes(name).CopyTo(header, 0);
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 108);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 116);
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0").CopyTo(header, 124);
        Encoding.ASCII.GetBytes("00000000000\0").CopyTo(header, 136);
        header[156] = typeFlag;
        Encoding.ASCII.GetBytes(posixMagic ? "ustar\000" : "ustar  \0").CopyTo(header, 257);
        if (prefix != null)
        {
            Encoding.UTF8.GetBytes(prefix).CopyTo(header, 345);
        }
        if (junkAt345)
        {
            Encoding.ASCII.GetBytes("12345670123\0").CopyTo(header, 345);
        }
        for (int i = 148; i < 156; i++)
        {
            header[i] = (byte)' ';
        }
        long sum = 0;
        foreach (byte b in header)
        {
            sum += b;
        }
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
        return header;
    }

    private static byte[] Archive(params (byte[] header, string content)[] entries)
    {
        using MemoryStream stream = new MemoryStream();
        foreach ((byte[] header, string content) in entries)
        {
            stream.Write(header, 0, 512);
            if (content != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(content);
                stream.Write(data, 0, data.Length);
                stream.Write(new byte[(512 - data.Length % 512) % 512], 0, (512 - data.Length % 512) % 512);
            }
        }
        stream.Write(new byte[1024], 0, 1024);
        return stream.ToArray();
    }

    private static string Extract(byte[] archive, out Exception error)
    {
        string root = Path.Combine(Path.GetTempPath(), "smartty-tar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        error = null;
        try
        {
            new TarExtractor(new MemoryStream(archive), root).ExtractAll(ignoreErrors: false, stripFirstDirectory: false);
        }
        catch (Exception exception)
        {
            error = exception;
        }
        return root;
    }

    private static int Main()
    {
        // --- traversal ---
        Check(TarExtractor.IsTraversalEntryName("../x"), "traversal: leading ..");
        Check(TarExtractor.IsTraversalEntryName("a/../../x"), "traversal: .. in the middle");
        Check(TarExtractor.IsTraversalEntryName("a\\..\\..\\x"), "traversal: backslash separators");
        Check(TarExtractor.IsTraversalEntryName("/etc/passwd"), "traversal: rooted unix name");
        Check(TarExtractor.IsTraversalEntryName("\\\\server\\share\\x"), "traversal: UNC name");
        Check(TarExtractor.IsTraversalEntryName(".."), "traversal: just ..");
        Check(!TarExtractor.IsTraversalEntryName("..foo"), "traversal: a name starting with two dots is fine");
        Check(!TarExtractor.IsTraversalEntryName("a/b..c/d"), "traversal: dots inside a name are fine");
        Check(!TarExtractor.IsTraversalEntryName("./dir/file"), "traversal: ./ prefix is fine");
        Check(!TarExtractor.IsTraversalEntryName("dir/"), "traversal: a directory record is fine");

        // --- names that cannot exist on Windows ---
        Check(TarExtractor.HasInvalidLocalNameCharacters("C:/Windows/x"), "invalid: drive letter");
        Check(TarExtractor.HasInvalidLocalNameCharacters("file:stream"), "invalid: alternate data stream");
        Check(TarExtractor.HasInvalidLocalNameCharacters("dir/a?b"), "invalid: question mark");
        Check(TarExtractor.HasInvalidLocalNameCharacters("a|b"), "invalid: pipe");
        Check(TarExtractor.HasInvalidLocalNameCharacters("dir/.../x"), "invalid: dots-only segment");
        Check(TarExtractor.HasInvalidLocalNameCharacters("tab\there"), "invalid: control character");
        Check(!TarExtractor.HasInvalidLocalNameCharacters("dir/sub/file.txt"), "invalid: ordinary path");
        Check(!TarExtractor.HasInvalidLocalNameCharacters("./dir/.hidden"), "invalid: leading dot name is fine");
        Check(!TarExtractor.HasInvalidLocalNameCharacters("Привет/файл.txt"), "invalid: Cyrillic is fine");

        // --- whole archives ---
        string root = Extract(Archive((Header("a/../../evil.txt", 4, (byte)'0', true), "evil")), out Exception error);
        Check(error != null && error.Message.Contains("Invalid file name"), "archive: a/../../evil.txt is refused");
        Check(!File.Exists(Path.Combine(Path.GetDirectoryName(root), "evil.txt")), "archive: nothing was written outside the root");
        Directory.Delete(root, true);

        root = Extract(Archive((Header("C:/Temp/evil.txt", 4, (byte)'0', true), "evil"), (Header("ok.txt", 2, (byte)'0', true), "ok")), out error);
        Check(error == null, "archive: a drive-letter name is skipped, not fatal");
        Check(File.Exists(Path.Combine(root, "ok.txt")) && File.ReadAllText(Path.Combine(root, "ok.txt")) == "ok", "archive: the next entry is still extracted");
        Check(!File.Exists("C:/Temp/evil.txt") && Directory.GetFiles(root).Length == 1, "archive: the skipped entry did not create anything");
        Directory.Delete(root, true);

        root = Extract(Archive((Header("stream:name", 3, (byte)'0', true), "abc")), out error);
        Check(error == null && Directory.GetFiles(root).Length == 0, "archive: a name with ':' creates nothing");
        Directory.Delete(root, true);

        // --- header formats ---
        root = Extract(Archive((Header("gnu.txt", 5, (byte)'0', posixMagic: false, junkAt345: true), "hello")), out error);
        Check(error == null && File.Exists(Path.Combine(root, "gnu.txt")), "gnu format: bytes at 345 are not a name prefix");
        Directory.Delete(root, true);

        root = Extract(Archive((Header("posix.txt", 5, (byte)'0', posixMagic: true, prefix: "pre"), "hello")), out error);
        Check(error == null && File.Exists(Path.Combine(root, "pre\\posix.txt")), "posix format: the prefix field is still honoured");
        Directory.Delete(root, true);

        // --- levels up ---
        Check(TarExtractor.CountParentReferences("..\\..\\x", isForward: true) == 2, "levels: backslashes count as separators");
        Check(TarExtractor.CountParentReferences("a/../..", isForward: true) == 1, "levels: net levels above the start");
        Check(TarExtractor.CountParentReferences("link", isForward: false) == 1, "levels: a file at the root");
        Check(TarExtractor.CountParentReferences("dir/link", isForward: false) == 2, "levels: a file one directory down");

        // --- absolute link targets ---
        Check(TarExtractor.MakeRelativeToRoot("link", "/srv/proj/x", "/srv/proj", false) == "x", "link: root level symlink to a root level file");
        Check(TarExtractor.MakeRelativeToRoot("dir/link", "/srv/proj/x", "/srv/proj", false) == "..\\x", "link: symlink one directory down");
        Check(TarExtractor.MakeRelativeToRoot("a/b/link", "/srv/proj/c/d", "/srv/proj/", false) == "..\\..\\c\\d", "link: trailing slash on the root, two levels down");
        Check(TarExtractor.MakeRelativeToRoot("dir/link", "/srv/proj/x", "/srv/proj", true) == "x", "link: a hard link is relative to the root");
        Check(TarExtractor.MakeRelativeToRoot("link", "/srv/proj", "/srv/proj", false) == ".", "link: a link to the root itself does not throw");
        Check(TarExtractor.MakeRelativeToRoot("link", "/srv/proj2/x", "/srv/proj", false) == null, "link: a sibling folder with the same prefix is outside");
        Check(TarExtractor.MakeRelativeToRoot("link", "/etc/passwd", "/srv/proj", false) == null, "link: a target outside the root");

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed.");
        return 0;
    }
}
