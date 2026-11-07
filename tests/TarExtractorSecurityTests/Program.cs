using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using SmarTTY.Sync;
using SmarTTY.Transport;

// Regression harness for the TAR extractor of directory downloads: entry names that try to leave the target
// folder, names Windows cannot hold, the GNU/POSIX header formats (base-256 numbers, GNU long names and link targets, pax
// extended headers) and the conversion of link targets, which must not lead out of the folder either.
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

    private static byte[] Header(string name, long size, byte typeFlag, bool posixMagic, string prefix = null, bool junkAt345 = false, string linkName = null, byte[] sizeField = null, byte[] mtimeField = null)
    {
        byte[] header = new byte[512];
        Encoding.UTF8.GetBytes(name).CopyTo(header, 0);
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 108);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 116);
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0").CopyTo(header, 124);
        Encoding.ASCII.GetBytes("00000000000\0").CopyTo(header, 136);
        if (sizeField != null)
        {
            sizeField.CopyTo(header, 124);
        }
        if (mtimeField != null)
        {
            mtimeField.CopyTo(header, 136);
        }
        header[156] = typeFlag;
        if (linkName != null)
        {
            Encoding.UTF8.GetBytes(linkName).CopyTo(header, 157);
        }
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

    /// <summary>The number field of GNU tar for values that octal cannot hold: base-256, two's complement, the top bit marks it.</summary>
    private static byte[] Base256(long value, int length = 12)
    {
        byte[] field = new byte[length];
        long rest = value;
        for (int i = length - 1; i >= 0; i--)
        {
            field[i] = (byte)(rest & 0xFF);
            rest >>= 8;
        }
        field[0] |= 0x80;
        return field;
    }

    /// <summary>The records of a pax extended header: "length key=value\n", the length counts its own digits.</summary>
    private static string Pax(params (string key, string value)[] records)
    {
        StringBuilder text = new StringBuilder();
        foreach ((string key, string value) in records)
        {
            string body = " " + key + "=" + value + "\n";
            int bodyLength = Encoding.UTF8.GetByteCount(body);
            int total = bodyLength + 1;
            for (int digits = 1; ; digits++)
            {
                total = bodyLength + digits;
                if (total.ToString().Length == digits)
                {
                    break;
                }
            }
            text.Append(total).Append(body);
        }
        return text.ToString();
    }

    /// <summary>A pax extended header ('x', for the next entry) or global header ('g') with its data.</summary>
    private static (byte[] header, string content) PaxEntry(byte type, params (string key, string value)[] records)
    {
        string body = Pax(records);
        return (Header(type == (byte)'g' ? "pax_global_header" : "./PaxHeaders/x", Encoding.UTF8.GetByteCount(body), type, true), body);
    }

    /// <summary>A GNU long name ('L') or long link target ('K') record: the text and its NUL.</summary>
    private static (byte[] header, string content) LongEntry(char type, string text)
    {
        string body = text + "\0";
        return (Header("././@LongLink", Encoding.UTF8.GetByteCount(body), (byte)type, false), body);
    }

    /// <summary>The targets of the links the extractor has queued for CreateSymlinks (it can only run on Windows).</summary>
    private static List<string> PendingLinkTargets(TarExtractor extractor)
    {
        List<string> targets = new List<string>();
        FieldInfo list = typeof(TarExtractor).GetField("_symlinks", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (object item in (IEnumerable)list.GetValue(extractor))
        {
            targets.Add((string)item.GetType().GetField("Target").GetValue(item));
        }
        return targets;
    }

    private static string Extract(byte[] archive, out Exception error)
    {
        return Extract(archive, out error, out _);
    }

    private static string Extract(byte[] archive, out Exception error, out TarExtractor extractor, bool preserveTimestamps = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "smartty-tar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        error = null;
        extractor = new TarExtractor(new MemoryStream(archive), root) { PreserveTimestamps = preserveTimestamps };
        try
        {
            extractor.ExtractAll(ignoreErrors: false, stripFirstDirectory: false);
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

        // --- links: ".." behind the root of an absolute target led out of the download folder (B-1) ---
        const string hosts = "/srv/proj/../../../Windows/System32/drivers/etc/hosts";
        Check(TarExtractor.MakeRelativeToRoot("link", hosts, "/srv/proj", false) == null, "escape: .. after the root of an absolute symlink target is refused");
        Check(TarExtractor.MakeRelativeToRoot("a/b/link", hosts, "/srv/proj", false) == null, "escape: .. after the root is refused for a deep symlink too");
        Check(TarExtractor.MakeRelativeToRoot("link", hosts, "/srv/proj", true) == null, "escape: .. after the root of an absolute hard link target is refused");
        Check(TarExtractor.MakeRelativeToRoot("link", "/srv/proj/a/../b", "/srv/proj", false) == "a\\..\\b", "escape: .. that stays inside the root is fine");
        Check(TarExtractor.MakeRelativeToRoot("link", "/srv/proj/C:/Windows/hosts", "/srv/proj", false) == null, "escape: a drive letter after the root is refused (a rooted path would replace the folder)");
        Check(TarExtractor.MakeRelativeToRoot("link", "/srv/proj/a/./../../x", "/srv/proj", false) == null, "escape: .. behind a . segment is still counted");

        // The relative targets had holes of their own: "." segments were counted as levels, rooted targets were not looked at.
        Check(TarExtractor.CountParentReferences("a/./../../x", isForward: true) == 1, "levels: a . segment is not a level");
        Check(TarExtractor.CountParentReferences("./link", isForward: false) == 1, "levels: ./ before the name is not a level");
        Check(TarExtractor.ResolveRelativeTarget("link", "a/./../../x", false, "/srv/proj") == null, "resolve: .. behind a . segment is refused");
        Check(TarExtractor.ResolveRelativeTarget("./link", "../x", false, "/srv/proj") == null, "resolve: an entry name with ./ gives no extra level");
        Check(TarExtractor.ResolveRelativeTarget("./././link", "../../../x", false, "/srv/proj") == null, "resolve: many ./ in the entry name give no extra levels");
        Check(TarExtractor.ResolveRelativeTarget("./dir/link", "../x", false, "/srv/proj") == "..\\x", "resolve: a legitimate link of a ./ name still works");
        Check(TarExtractor.ResolveRelativeTarget("link", "C:\\Windows\\hosts", false, "/srv/proj") == null, "resolve: a drive letter target is refused");
        Check(TarExtractor.ResolveRelativeTarget("link", "C:/Windows/hosts", false, "/srv/proj") == null, "resolve: a drive letter target with slashes is refused");
        Check(TarExtractor.ResolveRelativeTarget("link", "\\Windows\\hosts", false, "/srv/proj") == null, "resolve: a target rooted by a backslash is refused");
        Check(TarExtractor.ResolveRelativeTarget("link", "\\\\server\\share\\x", false, "/srv/proj") == null, "resolve: a UNC target is refused");
        Check(TarExtractor.ResolveRelativeTarget("link", "file:stream", false, "/srv/proj") == null, "resolve: an alternate data stream target is refused");
        Check(TarExtractor.ResolveRelativeTarget("link", hosts, false, "/srv/proj") == null, "resolve: the escaping absolute target stays unresolved");
        Check(TarExtractor.ResolveRelativeTarget("dir/link", "/srv/proj/x", false, "/srv/proj") == "..\\x", "resolve: an absolute target inside the root still works");
        Check(TarExtractor.ResolveRelativeTarget("link", "/srv/proj/x", false, null) == null, "resolve: an absolute target without a remote root is unresolved");
        Check(TarExtractor.ResolveRelativeTarget("dir/link", "sub/file", false, "/srv/proj") == "sub\\file", "resolve: a relative target inside the root works");
        Check(TarExtractor.ResolveRelativeTarget("dir/link", "../x", false, "/srv/proj") == "..\\x", "resolve: one level up from a subfolder works");
        Check(TarExtractor.ResolveRelativeTarget("link", "../x", false, "/srv/proj") == null, "resolve: one level up from the root is refused");
        Check(TarExtractor.ResolveRelativeTarget("dir/link", "../../x", false, "/srv/proj") == null, "resolve: two levels up from a subfolder is refused");
        Check(TarExtractor.ResolveRelativeTarget("link", "../x", true, "/srv/proj") == null, "resolve: a hard link target is relative to the root and cannot go up");
        Check(TarExtractor.ResolveRelativeTarget("dir/link", "sub/x", true, "/srv/proj") == "sub\\x", "resolve: a hard link inside the root works");

        // --- numbers: base-256 for sizes of 8 GiB and more and for times before 1970 (B-2) ---
        byte[] gnuNegativeTime = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xED, 0x30, 0x08, 0x80 }; // 1960-01-01, as GNU tar 1.35 wrote it
        byte[] gnuBigSize = { 0x80, 0, 0, 0, 0, 0, 0, 0x02, 0x40, 0, 0, 0 };                              // 9 GiB, as GNU tar 1.35 wrote it
        Check(TarArchiveChecker.TryParseNumber(gnuNegativeTime, 0, 12, out long number) && number == -315619200L, "number: a negative base-256 time of GNU tar");
        Check(TarArchiveChecker.TryParseNumber(gnuBigSize, 0, 12, out number) && number == 9L << 30, "number: a base-256 size of 9 GiB");
        Check(Base256(-315619200L).SequenceEqual(gnuNegativeTime) && Base256(9L << 30).SequenceEqual(gnuBigSize), "number: the test helper writes what GNU tar writes");
        Check(TarArchiveChecker.ParseNumber(gnuBigSize, 0, 12) == 9L << 30, "number: ParseNumber reads the base-256 size");
        Check(TarArchiveChecker.ParseNumber(gnuNegativeTime, 0, 12) == -1, "number: a negative number is not a valid size");
        Check(TarArchiveChecker.TryParseNumber(Encoding.ASCII.GetBytes("00000000144\0"), 0, 12, out number) && number == 100, "number: octal");
        Check(TarArchiveChecker.TryParseNumber(Encoding.ASCII.GetBytes("    144 \0\0\0"), 0, 12, out number) && number == 100, "number: octal padded with blanks (V7 tar)");
        Check(TarArchiveChecker.TryParseNumber(new byte[12], 0, 12, out number) && number == 0, "number: an empty field is 0");
        Check(!TarArchiveChecker.TryParseNumber(Encoding.ASCII.GetBytes("12x45\0\0\0\0\0\0\0"), 0, 12, out _), "number: a letter is not a number");
        byte[] tooBig = { 0x80, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01 };
        Check(!TarArchiveChecker.TryParseNumber(tooBig, 0, 12, out _), "number: a base-256 number that does not fit into 64 bits is refused");
        byte[] minusOne = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        Check(TarArchiveChecker.TryParseNumber(minusOne, 0, 12, out number) && number == -1, "number: base-256 minus one");

        root = Extract(Archive((Header("base256.txt", 5, (byte)'0', true, sizeField: Base256(5)), "hello")), out error);
        Check(error == null && File.ReadAllText(Path.Combine(root, "base256.txt")) == "hello", "archive: a size written in base-256 is read");
        Directory.Delete(root, true);

        root = Extract(Archive((Header("old.txt", 2, (byte)'0', true, mtimeField: Base256(-315619200L)), "ok")), out error, out _, preserveTimestamps: true);
        Check(error == null && File.ReadAllText(Path.Combine(root, "old.txt")) == "ok", "archive: a negative base-256 time does not stop the download");
        Check(File.GetLastWriteTimeUtc(Path.Combine(root, "old.txt")) == new DateTime(1960, 1, 1, 0, 0, 0, DateTimeKind.Utc), "archive: and it is applied as the time of the file");
        Directory.Delete(root, true);

        root = Extract(Archive((Header("garbage-time.txt", 2, (byte)'0', true, mtimeField: Encoding.ASCII.GetBytes("zzzzzzzzzzz\0")), "ok")), out error);
        Check(error == null && File.Exists(Path.Combine(root, "garbage-time.txt")), "archive: a damaged time field costs the time, not the download");
        Directory.Delete(root, true);

        // 9 GiB are announced and the archive ends: the header is read (the failure is the missing body, not a FormatException of the header).
        root = Extract(Archive((Header("huge.bin", 0, (byte)'0', true, sizeField: Base256(9L << 30)), null)), out error);
        Check(error is IOException && error.Message.Contains("Cannot read TAR archive body"), "archive: a size of 9 GiB is read from the header");
        Directory.Delete(root, true);

        root = Extract(Archive((Header("broken.bin", 0, (byte)'0', true, sizeField: Encoding.ASCII.GetBytes("12x45\0\0\0\0\0\0\0")), null)), out error);
        Check(error is InvalidDataException && error.Message.Contains("size"), "archive: a size that is no number is a clear error");
        Directory.Delete(root, true);

        // --- extension headers: GNU 'L' and 'K', pax 'x' and 'g' (B-3) ---
        string longName = new string('d', 60) + "/" + new string('e', 60) + "/file.txt";
        string shortName = longName.Substring(0, 100);
        string longTarget = new string('t', 150);
        string shortTarget = longTarget.Substring(0, 100);

        root = Extract(Archive(PaxEntry((byte)'x', ("path", longName)), (Header(shortName, 4, (byte)'0', true), "pax!")), out error);
        string longPath = Path.Combine(root, longName.Replace('/', '\\'));
        Check(error == null && File.Exists(longPath) && File.ReadAllText(longPath) == "pax!", "pax: the path of an extended header is the name of the file");
        Check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == 1, "pax: the extended header itself is not extracted as a file");
        Directory.Delete(root, true);

        root = Extract(Archive(PaxEntry((byte)'x', ("size", "5")), (Header("sized.txt", 0, (byte)'0', true), "hello")), out error);
        Check(error == null && File.Exists(Path.Combine(root, "sized.txt")) && File.ReadAllText(Path.Combine(root, "sized.txt")) == "hello", "pax: the size of an extended header is the size of the data");
        Check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == 1, "pax: only the real file is created");
        Directory.Delete(root, true);

        root = Extract(Archive(PaxEntry((byte)'x', ("mtime", "1700000000.123456789")), (Header("timed.txt", 2, (byte)'0', true), "ok")), out error, out _, preserveTimestamps: true);
        Check(error == null && File.GetLastWriteTimeUtc(Path.Combine(root, "timed.txt")) == new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), "pax: the time of an extended header (with a fraction) is the time of the file");
        Directory.Delete(root, true);

        root = Extract(Archive(PaxEntry((byte)'x', ("path", "Привет/файл.txt")), (Header("fallback.txt", 2, (byte)'0', true), "ok")), out error);
        Check(error == null && File.Exists(Path.Combine(root, "Привет\\файл.txt")), "pax: a UTF-8 path is read");
        Directory.Delete(root, true);

        root = Extract(Archive(PaxEntry((byte)'x', ("path", "../evil.txt")), (Header("fine.txt", 4, (byte)'0', true), "evil")), out error);
        Check(error != null && error.Message.Contains("Invalid file name"), "pax: a path with .. is refused like any other name");
        Check(!File.Exists(Path.Combine(Path.GetDirectoryName(root), "evil.txt")), "pax: nothing was written outside the root");
        Directory.Delete(root, true);

        root = Extract(Archive(PaxEntry((byte)'x', ("path", "/etc/evil.txt")), (Header("fine.txt", 4, (byte)'0', true), "evil")), out error);
        Check(error != null && error.Message.Contains("Invalid file name"), "pax: a rooted path is refused");
        Directory.Delete(root, true);

        root = Extract(Archive(PaxEntry((byte)'x', ("linkpath", longTarget)), (Header("lnk", 0, (byte)'2', true, linkName: shortTarget), null)), out error, out TarExtractor extractor);
        Check(error == null && PendingLinkTargets(extractor).SequenceEqual(new[] { longTarget }), "pax: linkpath is the target of the link");
        Directory.Delete(root, true);

        root = Extract(Archive(LongEntry('K', longTarget), (Header("lnk", 0, (byte)'2', false, linkName: shortTarget), null)), out error, out extractor);
        Check(error == null && PendingLinkTargets(extractor).SequenceEqual(new[] { longTarget }), "gnu: a long link target (K) is the target of the link, not its first 100 characters");
        Check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == 0, "gnu: the K record does not create a file");
        Directory.Delete(root, true);

        root = Extract(Archive(LongEntry('L', longName), (Header(shortName, 4, (byte)'0', false), "gnu!")), out error);
        longPath = Path.Combine(root, longName.Replace('/', '\\'));
        Check(error == null && File.Exists(longPath) && File.ReadAllText(longPath) == "gnu!", "gnu: a long name (L) is the name of the file");
        Directory.Delete(root, true);

        root = Extract(Archive(LongEntry('L', longName), LongEntry('K', longTarget), (Header(shortName, 0, (byte)'2', false, linkName: shortTarget), null)), out error, out extractor);
        Check(error == null && PendingLinkTargets(extractor).SequenceEqual(new[] { longTarget }), "gnu: a long name and a long link target together");
        Check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == 0, "gnu: L and K create no files");
        Directory.Delete(root, true);

        root = Extract(Archive(LongEntry('L', "../evil.txt"), (Header("fine.txt", 4, (byte)'0', false), "evil")), out error);
        Check(error != null && error.Message.Contains("Invalid file name"), "gnu: a long name with .. is refused");
        Directory.Delete(root, true);

        root = Extract(Archive(PaxEntry((byte)'g', ("comment", "0123456789abcdef")), (Header("after.txt", 2, (byte)'0', true), "ok")), out error);
        Check(error == null && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(Path.GetFileName).SequenceEqual(new[] { "after.txt" }), "pax: a global header creates nothing and the next entry is extracted");
        Directory.Delete(root, true);

        root = Extract(Archive((Header("./PaxHeaders/x", 1L << 30, (byte)'x', true), null)), out error);
        Check(error is InvalidDataException && error.Message.Contains("too big"), "pax: an extension header of a gigabyte is refused without allocating it");
        Directory.Delete(root, true);

        root = Extract(Archive((Header("./PaxHeaders/x", 4000, (byte)'x', true), null)), out error);
        Check(error is IOException && error.Message.Contains("Incomplete"), "pax: an extension header cut off by the end of the archive is an error");
        Directory.Delete(root, true);

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed.");
        return 0;
    }
}
