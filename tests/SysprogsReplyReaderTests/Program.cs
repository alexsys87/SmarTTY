using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.Sync;

// Regression harness for the replies of the SysprogsSync helper (SysprogsReplyReader). The helper answers with records of a
// fixed size (ActionDone: 4 bytes, ActionFailed: 12) and the output arrives in pieces of any size, so a record is often cut in
// two. The parser used to step over the end of what it had, fail with "Offset and length were out of bounds", and from then on
// it ignored every reply: the files that could not be created or written never showed up in the warnings of the upload.
// Run with: dotnet run --project tests/SysprogsReplyReaderTests
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

    private static byte[] Done()
    {
        return new byte[] { 2, 0, 0, 0 };
    }

    private static byte[] Failed(int action, int error)
    {
        byte[] record = new byte[12];
        record[0] = 1;
        BitConverter.GetBytes(action).CopyTo(record, 4);
        BitConverter.GetBytes(error).CopyTo(record, 8);
        return record;
    }

    private sealed class Run
    {
        public readonly SysprogsReplyReader Reader = new SysprogsReplyReader();

        public readonly List<string> Failed = new List<string>();

        public int Corrupt;

        public Run()
        {
            Reader.ActionFailed += (action, error) => Failed.Add(action + ":" + error);
            Reader.Corrupt += () => Corrupt++;
        }

        public void Feed(byte[] data)
        {
            Reader.Feed(data, 0, data.Length);
        }
    }

    private static byte[] Join(params byte[][] parts)
    {
        return parts.SelectMany(part => part).ToArray();
    }

    private static int Main()
    {
        Check(SysprogsReplyReader.ActionDoneSize == 4 && SysprogsReplyReader.ActionFailedSize == 12, "sizes: ActionDone is 4 bytes and ActionFailed is 12");
        Check((byte)SysprogsReplyReader.Reply.Unknown == 0 && (byte)SysprogsReplyReader.Reply.ActionFailed == 1 && (byte)SysprogsReplyReader.Reply.ActionDone == 2, "sizes: the reply bytes are those of the helper");

        // ---- whole records
        Run r = new Run();
        r.Feed(Join(Done(), Failed(7, 13), Done(), Failed(8, 28)));
        Check(r.Failed.SequenceEqual(new[] { "7:13", "8:28" }) && r.Corrupt == 0 && r.Reader.PendingByteCount == 0, "whole records in one read: the failures come with their action index and error code");
        r = new Run();
        r.Feed(Join(Failed(-1, 0), Failed(int.MaxValue, int.MinValue)));
        Check(r.Failed.SequenceEqual(new[] { "-1:0", int.MaxValue + ":" + int.MinValue }), "whole records: the numbers are read as they are written (signed, 32 bits)");
        r = new Run();
        r.Feed(Join(Done(), Done(), Done()));
        Check(r.Failed.Count == 0 && r.Corrupt == 0 && r.Reader.PendingByteCount == 0, "whole records: ActionDone alone says nothing");

        // ---- a record cut at every place
        for (int cut = 1; cut < SysprogsReplyReader.ActionFailedSize; cut++)
        {
            r = new Run();
            byte[] record = Failed(1, 13);
            r.Reader.Feed(record, 0, cut);
            Check(r.Failed.Count == 0 && r.Reader.PendingByteCount == cut && !r.Reader.IsCorrupted, "ActionFailed cut after byte " + cut + ": it waits, and nothing is lost or reported");
            r.Reader.Feed(record, cut, SysprogsReplyReader.ActionFailedSize - cut);
            r.Feed(Failed(2, 28));
            Check(r.Failed.SequenceEqual(new[] { "1:13", "2:28" }) && r.Corrupt == 0 && r.Reader.PendingByteCount == 0, "ActionFailed cut after byte " + cut + ": both failures arrive, once and in order");
        }
        for (int cut = 1; cut < SysprogsReplyReader.ActionDoneSize; cut++)
        {
            r = new Run();
            byte[] done = Done();
            r.Reader.Feed(done, 0, cut);
            Check(r.Reader.PendingByteCount == cut, "ActionDone cut after byte " + cut + ": it waits");
            r.Reader.Feed(done, cut, SysprogsReplyReader.ActionDoneSize - cut);
            r.Feed(Failed(5, 13));
            Check(r.Failed.SequenceEqual(new[] { "5:13" }) && r.Corrupt == 0 && r.Reader.PendingByteCount == 0, "ActionDone cut after byte " + cut + ": the failure after it arrives");
        }

        // ---- a record that is cut into more than two pieces, and one read that ends in the middle of the next one
        r = new Run();
        byte[] slow = Failed(9, 28);
        for (int i = 0; i < slow.Length; i++)
        {
            r.Reader.Feed(slow, i, 1);
        }
        Check(r.Failed.SequenceEqual(new[] { "9:28" }) && r.Reader.PendingByteCount == 0, "a record that comes one byte at a time");
        r = new Run();
        byte[] two = Join(Failed(1, 1), Done(), Failed(2, 2));
        r.Reader.Feed(two, 0, 12 + 4 + 5);
        Check(r.Failed.SequenceEqual(new[] { "1:1" }) && r.Reader.PendingByteCount == 5, "a read that ends in the middle of the third record: the first is delivered, 5 bytes wait");
        r.Reader.Feed(two, 12 + 4 + 5, 7);
        Check(r.Failed.SequenceEqual(new[] { "1:1", "2:2" }) && r.Reader.PendingByteCount == 0, "and the third comes with the rest");

        // ---- a long stream, cut at random
        foreach (int seed in new[] { 1, 2, 3, 12345 })
        {
            Random random = new Random(seed);
            List<byte> stream = new List<byte>();
            List<string> expected = new List<string>();
            for (int i = 0; i < 5000; i++)
            {
                if (random.Next(3) == 0)
                {
                    int error = random.Next(1, 200);
                    stream.AddRange(Failed(i, error));
                    expected.Add(i + ":" + error);
                }
                else
                {
                    stream.AddRange(Done());
                }
            }
            byte[] all = stream.ToArray();
            foreach (int maxPiece in new[] { 1, 3, 11, 13, 64, 1500, 40000 })
            {
                r = new Run();
                for (int position = 0; position < all.Length;)
                {
                    int piece = Math.Min(all.Length - position, random.Next(1, maxPiece + 1));
                    r.Reader.Feed(all, position, piece);
                    position += piece;
                }
                Check(r.Failed.SequenceEqual(expected) && r.Corrupt == 0 && r.Reader.PendingByteCount == 0 && !r.Reader.IsCorrupted, "seed " + seed + ", pieces of 1.." + maxPiece + " bytes: all " + expected.Count + " failures, in order, nothing left over");
            }
        }

        // ---- the buffer belongs to the caller
        r = new Run();
        byte[] shared = Join(Failed(3, 28), Done());
        r.Reader.Feed(shared, 0, 7);
        Array.Clear(shared, 0, shared.Length);
        r.Reader.Feed(Failed(3, 28), 7, 5);
        Check(r.Failed.SequenceEqual(new[] { "3:28" }), "the part of a record that waits is kept as a copy: the caller may reuse its buffer");
        r = new Run();
        byte[] padded = new byte[40];
        Array.Fill(padded, (byte)0xEE);
        Failed(4, 13).CopyTo(padded, 10);
        r.Reader.Feed(padded, 10, 12);
        Check(r.Failed.SequenceEqual(new[] { "4:13" }) && r.Corrupt == 0, "only the bytes between offset and offset + count are read (the others are 0xEE, which would be corrupt)");
        r = new Run();
        r.Reader.Feed(new byte[0], 0, 0);
        r.Reader.Feed(Done(), 2, 0);
        Check(r.Failed.Count == 0 && r.Corrupt == 0 && r.Reader.PendingByteCount == 0, "an empty read changes nothing");

        // ---- output that is no sequence of replies
        foreach (byte bad in new byte[] { 0, 3, 99, 255 })
        {
            r = new Run();
            r.Feed(Join(Failed(1, 13), new byte[] { bad }, Failed(2, 28), Done()));
            Check(r.Failed.SequenceEqual(new[] { "1:13" }) && r.Corrupt == 1 && r.Reader.IsCorrupted, "reply byte " + bad + ": what came before is delivered, the corruption is reported once, what comes after is not read");
            r.Feed(Failed(3, 13));
            Check(r.Failed.Count == 1 && r.Corrupt == 1, "reply byte " + bad + ": and nothing is read from later output either");
        }
        r = new Run();
        byte[] half = Failed(1, 13);
        r.Reader.Feed(half, 0, 6);
        r.Reader.Feed(Join(half.Skip(6).ToArray(), new byte[] { 77 }), 0, 7);
        Check(r.Failed.SequenceEqual(new[] { "1:13" }) && r.Corrupt == 1, "garbage right behind a record that was completed from two pieces");

        // ---- the cost: the replies of a whole upload in one read must not be copied once for every record
        List<byte> big = new List<byte>();
        for (int i = 0; i < 100000; i++)
        {
            big.AddRange(Failed(i, 28));
        }
        byte[] bigData = big.ToArray();
        r = new Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        r.Reader.Feed(bigData, 0, bigData.Length);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(r.Failed.Count == 100000 && r.Failed[99999] == "99999:28", "100000 failures in one read are all delivered");
        Check(allocated < 20L * 1024 * 1024, "and cost no copy of the data for every record: " + allocated / 1024 + " KiB allocated (the event handler strings included; copying the 1.2 MB for every record would be about 60 GB)");
        r = new Run();
        r.Reader.Feed(bigData, 0, 5);
        before = GC.GetAllocatedBytesForCurrentThread();
        r.Reader.Feed(bigData, 5, bigData.Length - 5);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(r.Failed.Count == 100000 && allocated < 20L * 1024 * 1024 + 2 * bigData.Length, "a record cut at the start of the read: one copy of the read, not one per record (" + allocated / 1024 + " KiB)");

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed.");
        return 0;
    }
}
