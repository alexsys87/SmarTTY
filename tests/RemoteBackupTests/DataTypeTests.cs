using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SmarTTY.Transport;

// Members of types that carry data but are not regular files: a GNU sparse file ('S', with the extension blocks of its map of holes),
// a GNU dump directory ('D'), a multi-volume continuation ('M') and the vendor types ('A' to 'Z'). POSIX says that a reader treats
// a type it does not know as a regular file, so the data after such a header is skipped by the size of the header; reading it as a
// header gave "a header of the archive is damaged" for an archive that tar itself reads without a word.
internal static partial class Program
{
    private const int TarBlock = 512;

    private static byte[] TarHeader(string name, long size, char type, Action<byte[]> adjust = null)
    {
        byte[] header = new byte[TarBlock];
        Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 108);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 116);
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0").CopyTo(header, 124);
        Encoding.ASCII.GetBytes("14000000000\0").CopyTo(header, 136);
        header[156] = (byte)type;
        Encoding.ASCII.GetBytes("ustar  \0").CopyTo(header, 257);
        adjust?.Invoke(header);
        for (int index = 148; index < 156; index++)
        {
            header[index] = (byte)' ';
        }
        long sum = header.Sum(value => (long)value);
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
        return header;
    }

    private static byte[] TarData(int length, byte fill)
    {
        byte[] data = new byte[(length + TarBlock - 1) / TarBlock * TarBlock];
        for (int index = 0; index < length; index++)
        {
            data[index] = fill;
        }
        return data;
    }

    private static byte[] TarArchiveOf(params byte[][] blocks)
    {
        using MemoryStream archive = new MemoryStream();
        foreach (byte[] block in blocks)
        {
            archive.Write(block, 0, block.Length);
        }
        archive.Write(new byte[TarBlock * 2], 0, TarBlock * 2);
        return archive.ToArray();
    }

    private static byte[] SparseExtension(bool another)
    {
        byte[] block = new byte[TarBlock];
        block[504] = another ? (byte)1 : (byte)0;
        return block;
    }

    private static void DataTypeTests()
    {
        // Folders, links, devices and FIFOs carry no data, whatever their size field says (a hard link may repeat the size of its file).
        byte[] noData = TarArchiveOf(
            TarHeader("link", 5, '1', header => Encoding.ASCII.GetBytes("target").CopyTo(header, 157)),
            TarHeader("sym", 7, '2', header => Encoding.ASCII.GetBytes("target").CopyTo(header, 157)),
            TarHeader("chr", 9, '3'), TarHeader("blk", 11, '4'), TarHeader("dir/", 13, '5'), TarHeader("fifo", 15, '6'),
            TarHeader("z", 2, '0'), TarData(2, (byte)'z'));
        List<TarEntry> entries = TarArchiveReader.List(new MemoryStream(noData), out string problem);
        Check(problem == null && entries.Count == 7 && entries.Last().Name == "z" && entries.Last().Size == 2, "data types: a link, a device, a folder and a FIFO with a size in the header take no data");
        Check(entries.Take(6).All(entry => entry.Size == 0), "data types: the size of those members is 0");
        TarCheckResult checkedNoData = TarArchiveChecker.Check(new MemoryStream(noData));
        Check(checkedNoData.IsComplete && checkedNoData.Entries == 7, "data types: the checker agrees on a link, a device, a folder and a FIFO with a size");

        // Types with data that are not regular files: the body follows the header.
        foreach (char type in new[] { 'D', 'M', 'N', 'V', 'X', 'A', 'S' })
        {
            byte[] archive = TarArchiveOf(
                TarHeader("first", 5, '0'), TarData(5, (byte)'1'),
                TarHeader("odd-" + type, 700, type), TarData(700, (byte)'o'),
                TarHeader("last", 6, '0'), TarData(6, (byte)'2'));
            entries = TarArchiveReader.List(new MemoryStream(archive), out problem);
            Check(problem == null && entries.Count == 3, "data types: a member of type '" + type + "' with data does not damage the archive (" + problem + ")");
            Check(entries[1].Type == TarEntryType.Other && entries[1].Size == 700 && entries[2].Name == "last" && entries[2].Size == 6, "data types: type '" + type + "' is a member of its own with its size, and the next one is read");
            TarCheckResult checkedArchive = TarArchiveChecker.Check(new MemoryStream(archive));
            Check(checkedArchive.IsComplete && checkedArchive.Entries == 3 && checkedArchive.DataBytes == 11, "data types: the checker reads type '" + type + "' as well (" + checkedArchive.Problem + ")");
            MemoryStream kept = new MemoryStream();
            TarArchiveReader.Filter(new MemoryStream(archive), kept, entry => true);
            Check(kept.ToArray().SequenceEqual(archive), "data types: the filtered archive repeats type '" + type + "' block for block");
            kept = new MemoryStream();
            List<TarArchiveReader.FilteredFile> files = TarArchiveReader.Filter(new MemoryStream(archive), kept, entry => entry.Name == "last");
            Check(files.Count == 1 && kept.ToArray().SequenceEqual(TarArchiveOf(TarHeader("last", 6, '0'), TarData(6, (byte)'2'))), "data types: a member of type '" + type + "' that is not selected is left out with its data");
        }

        // An old GNU sparse file: after the header come the blocks of its map of holes (as long as the flag at offset 504 is set), then the data.
        byte[] sparse = TarArchiveOf(
            TarHeader("hole", 1000, 'S', header => header[482] = 1), SparseExtension(true), SparseExtension(true), SparseExtension(false), TarData(1000, (byte)'s'),
            TarHeader("after", 3, '0'), TarData(3, (byte)'a'));
        entries = TarArchiveReader.List(new MemoryStream(sparse), out problem);
        Check(problem == null && entries.Count == 2 && entries[0].Size == 1000 && entries[1].Name == "after", "sparse: the extension blocks of the map of holes are passed (" + problem + ")");
        TarCheckResult checkedSparse = TarArchiveChecker.Check(new MemoryStream(sparse));
        Check(checkedSparse.IsComplete && checkedSparse.Entries == 2, "sparse: the checker passes them too (" + checkedSparse.Problem + ")");
        MemoryStream sparseKept = new MemoryStream();
        TarArchiveReader.Filter(new MemoryStream(sparse), sparseKept, entry => true);
        Check(sparseKept.ToArray().SequenceEqual(sparse), "sparse: the filtered archive keeps the map of holes and the data as they are");
        sparseKept = new MemoryStream();
        TarArchiveReader.Filter(new MemoryStream(sparse), sparseKept, entry => entry.Name == "hole");
        Check(sparseKept.ToArray().SequenceEqual(TarArchiveOf(TarHeader("hole", 1000, 'S', header => header[482] = 1), SparseExtension(true), SparseExtension(true), SparseExtension(false), TarData(1000, (byte)'s'))), "sparse: a selected sparse file is written with its map");
        byte[] noExtension = TarArchiveOf(TarHeader("hole", 100, 'S'), TarData(100, (byte)'s'), TarHeader("after", 3, '0'), TarData(3, (byte)'a'));
        entries = TarArchiveReader.List(new MemoryStream(noExtension), out problem);
        Check(problem == null && entries.Count == 2, "sparse: a map that fits into the header has no extension blocks (" + problem + ")");

        // Damaged archives are still reported as such.
        byte[] cutInData = TarArchiveOf(TarHeader("dump/", 700, 'D'), TarData(700, (byte)'d'));
        TarArchiveReader.List(new MemoryStream(cutInData, 0, TarBlock + 300), out problem);
        Check(problem != null && problem.Contains("cut off"), "data types: an archive cut inside the data of a type 'D' member is reported (" + problem + ")");
        Check(!TarArchiveChecker.Check(new MemoryStream(cutInData, 0, TarBlock + 300)).IsComplete, "data types: the checker reports it too");
        byte[] cutInMap = TarArchiveOf(TarHeader("hole", 1000, 'S', header => header[482] = 1), SparseExtension(true));
        TarArchiveReader.List(new MemoryStream(cutInMap, 0, TarBlock * 2), out problem);
        Check(problem != null && problem.Contains("cut off"), "sparse: an archive cut inside the map of holes is reported (" + problem + ")");
        Check(!TarArchiveChecker.Check(new MemoryStream(cutInMap, 0, TarBlock * 2)).IsComplete, "sparse: the checker reports it too");
        using (MemoryStream endless = new MemoryStream())
        {
            byte[] head = TarHeader("hole", 1000, 'S', header => header[482] = 1);
            endless.Write(head, 0, head.Length);
            byte[] more = SparseExtension(true);
            for (int index = 0; index < 40000; index++)
            {
                endless.Write(more, 0, more.Length);
            }
            endless.Position = 0;
            TarArchiveReader.List(endless, out problem);
            Check(problem != null && problem.Contains("too big"), "sparse: a map of holes without an end is refused, not kept in memory (" + problem + ")");
            endless.Position = 0;
            TarCheckResult checkedEndless = TarArchiveChecker.Check(endless);
            Check(!checkedEndless.IsComplete && checkedEndless.Problem.Contains("too big"), "sparse: the checker refuses it as well (" + checkedEndless.Problem + ")");
        }
    }

    // An archive that GNU tar itself writes for a sparse file (--sparse in the gnu format gives type 'S' and extension blocks).
    private static void SparseArchiveTests()
    {
        string source = Path.Combine(_work, "sparse-source");
        Directory.CreateDirectory(source);
        string original = Path.Combine(source, "sparse.bin");
        const int islands = 60;
        using (FileStream stream = File.Create(original))
        {
            byte[] island = Enumerable.Range(0, 4096).Select(index => (byte)(index * 7 + 1)).ToArray();
            for (int index = 0; index < islands; index++)
            {
                stream.Position = (long)index * 1048576;
                stream.Write(island, 0, island.Length);
            }
            stream.SetLength((long)islands * 1048576 + 100000);
        }
        File.WriteAllText(Path.Combine(source, "plain.txt"), "plain\n");
        if (Run("/bin/sh", "tar --format=gnu -S -cf - -C " + source + " sparse.bin plain.txt", null, out byte[] archive, out string error) != 0)
        {
            Console.WriteLine("SKIP: tar cannot write a sparse archive here (" + error.Trim() + ")");
            return;
        }
        if (archive[156] != 'S' || archive[482] == 0)
        {
            Console.WriteLine("SKIP: this tar wrote no old GNU sparse member with an extended map");
            return;
        }
        List<TarEntry> entries = TarArchiveReader.List(new MemoryStream(archive), out string problem);
        Check(problem == null && entries.Count == 2 && entries[0].Name == "sparse.bin" && entries[1].Name == "plain.txt", "sparse (GNU tar): the archive is read to its end (" + problem + ")");
        Equal(entries[0].Size, (long)islands * 4096, "sparse (GNU tar): the size is that of the data kept in the archive");
        TarCheckResult checkedArchive = TarArchiveChecker.Check(new MemoryStream(archive));
        Check(checkedArchive.IsComplete && checkedArchive.Entries == 2, "sparse (GNU tar): the checker finds the archive complete (" + checkedArchive.Problem + ")");

        MemoryStream filtered = new MemoryStream();
        TarArchiveReader.Filter(new MemoryStream(archive), filtered, entry => true);
        string archiveFile = Path.Combine(_work, "sparse-filtered.tar");
        File.WriteAllBytes(archiveFile, filtered.ToArray());
        string restored = Path.Combine(_work, "sparse-restored");
        Directory.CreateDirectory(restored);
        Check(Run("/bin/sh", "tar -xf " + archiveFile + " -C " + restored, null, out _, out error) == 0, "sparse (GNU tar): the filtered archive unpacks with tar (" + error.Trim() + ")");
        using (SHA256 sha = SHA256.Create())
        {
            byte[] expected = sha.ComputeHash(File.ReadAllBytes(original));
            byte[] actual = sha.ComputeHash(File.ReadAllBytes(Path.Combine(restored, "sparse.bin")));
            Check(expected.SequenceEqual(actual), "sparse (GNU tar): the sparse file comes back with its content");
        }
        Equal(File.ReadAllText(Path.Combine(restored, "plain.txt")), "plain\n", "sparse (GNU tar): the member after the sparse file comes back");

        // Without the sparse file the other member alone.
        filtered = new MemoryStream();
        List<TarArchiveReader.FilteredFile> files = TarArchiveReader.Filter(new MemoryStream(archive), filtered, entry => entry.Name == "plain.txt");
        Check(files.Count == 1 && files[0].Name == "plain.txt" && filtered.Length == TarBlock * 4, "sparse (GNU tar): the sparse file is left out with its map and data when it is not selected");
    }
}
