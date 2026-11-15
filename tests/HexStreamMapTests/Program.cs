using System;
using System.Collections.Generic;
using SmarTTY.TextEditing;

// Regression harness for the byte stream of the HEX terminal (TextEditing/HexStreamMap.cs): the search for a byte
// sequence that does not stop at the end of a row, and the map from a byte to its row and column (B-121).
// Run with: dotnet run --project tests/HexStreamMapTests
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
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    // What the HEX terminal does with its data: the bytes go to the stream, and to rows of bytesPerLine bytes; a break
    // ends the row. Null in the data is a break.
    private sealed class Terminal
    {
        public readonly HexStreamMap Map = new HexStreamMap();

        public readonly List<byte[]> Data = new List<byte[]>();

        public readonly List<byte> All = new List<byte>();

        public readonly List<int> Breaks = new List<int>();

        private int _line;

        private int _column;

        private int _bytesPerLine;

        public Terminal(int bytesPerLine)
        {
            _bytesPerLine = bytesPerLine;
        }

        public void Append(params byte[] bytes)
        {
            Data.Add(bytes);
            All.AddRange(bytes);
            Map.Append(bytes, bytes.Length);
            Render(bytes);
        }

        public void Break()
        {
            Data.Add(null);
            Breaks.Add(All.Count);
            Map.AddBreak();
            _line++;
            _column = 0;
        }

        public void Resize(int bytesPerLine)
        {
            _bytesPerLine = bytesPerLine;
            _line = 0;
            _column = 0;
            Map.ClearRows();
            foreach (byte[] item in Data)
            {
                if (item == null)
                {
                    _line++;
                    _column = 0;
                }
                else
                {
                    Render(item);
                }
            }
        }

        private void Render(byte[] bytes)
        {
            int pending = 0;
            for (int index = 0; index < bytes.Length; index++)
            {
                if (_column >= _bytesPerLine)
                {
                    Map.MapBytes(_line, pending);
                    pending = 0;
                    _line++;
                    _column = 0;
                }
                _column++;
                pending++;
            }
            Map.MapBytes(_line, pending);
        }

        // The offsets of all matches, found without the class under test.
        public List<int> Expected(byte[] pattern)
        {
            List<int> result = new List<int>();
            for (int offset = 0; offset + pattern.Length <= All.Count; offset++)
            {
                bool match = true;
                for (int index = 0; index < pattern.Length && match; index++)
                {
                    match = All[offset + index] == pattern[index];
                }
                foreach (int item in Breaks)
                {
                    match &= !(item > offset && item < offset + pattern.Length);
                }
                if (match)
                {
                    result.Add(offset);
                }
            }
            return result;
        }

        // "Find next" pressed until the search comes back to the first match.
        public List<int> Walk(byte[] pattern, int start, bool backward)
        {
            List<int> result = new List<int>();
            int position = start;
            for (int step = 0; step < All.Count + 2; step++)
            {
                int found = Map.Find(pattern, position, backward, wrap: true);
                if (found < 0 || (result.Count > 0 && found == result[0]))
                {
                    break;
                }
                result.Add(found);
                position = backward ? found - 1 : found + 1;
            }
            return result;
        }
    }

    private static string Join(List<int> items)
    {
        return string.Join(",", items);
    }

    private static byte[] Filler(int count)
    {
        byte[] result = new byte[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = 0x20;
        }
        return result;
    }

    private static int Main()
    {
        byte[] hello = { 0x48, 0x65, 0x6c, 0x6c, 0x6f };

        // An empty stream: nothing is found, nothing has a place.
        HexStreamMap empty = new HexStreamMap();
        Equal(empty.Find(hello, 0, backward: false, wrap: true), -1, "nothing is found in an empty stream");
        Equal(empty.Find(hello, 0, backward: true, wrap: true), -1, "nothing is found backward in an empty stream");
        Equal(empty.Find(new byte[0], 0, backward: false, wrap: true), -1, "an empty pattern is not found");
        Equal(empty.Find(null, 0, backward: false, wrap: true), -1, "no pattern is not found");
        Check(!empty.TryLocate(0, out _, out _), "an empty stream has no byte to locate");
        Equal(empty.OffsetAtOrAfter(0, 0), 0, "the caret of an empty view is at the offset 0");
        empty.AddBreak();
        empty.Append(new byte[] { 1, 2 }, 2);
        Equal(empty.Find(new byte[] { 1, 2 }, 0, backward: false, wrap: false), 0, "a break in front of the first byte separates nothing");

        // Acceptance 1: 16 bytes in a row, 5 bytes searched for, at every shift against the end of the row.
        for (int shift = 0; shift < 16; shift++)
        {
            Terminal terminal = new Terminal(16);
            terminal.Append(Filler(32 + shift));
            terminal.Append(hello);
            terminal.Append(Filler(20));
            int offset = 32 + shift;
            Equal(terminal.Map.Find(hello, 0, backward: false, wrap: false), offset, "found forward at the shift " + shift);
            Equal(terminal.Map.Find(hello, terminal.All.Count, backward: true, wrap: false), offset, "found backward at the shift " + shift);
            Check(terminal.Map.TryLocate(offset, out int firstLine, out int firstIndex), "the first byte has a place at the shift " + shift);
            Check(terminal.Map.TryLocate(offset + 4, out int lastLine, out int lastIndex), "the last byte has a place at the shift " + shift);
            Equal(firstLine, 2, "the line of the first byte at the shift " + shift);
            Equal(firstIndex, shift, "the column of the first byte at the shift " + shift);
            Equal(lastLine, (shift + 4 < 16) ? 2 : 3, "the line of the last byte at the shift " + shift);
            Equal(lastIndex, (shift + 4) % 16, "the column of the last byte at the shift " + shift);
            // The selection of the match, read back as the place to go on from.
            Equal(terminal.Map.OffsetAtOrAfter(firstLine, 3 * firstIndex), offset, "the start of the selection is the first byte at the shift " + shift);
            Equal(terminal.Map.OffsetAtOrAfter(lastLine, 3 * lastIndex + 2), offset + 5, "the end of the selection is behind the last byte at the shift " + shift);
        }

        // A match that ends with the last byte of a row, and one that begins with the first byte of a row.
        Terminal edges = new Terminal(16);
        edges.Append(Filler(11));
        edges.Append(hello);
        edges.Append(hello);
        edges.Append(Filler(3));
        Equal(Join(edges.Walk(hello, 0, backward: false)), "11,16", "matches at the end and at the start of a row");
        edges.Map.TryLocate(15, out int edgeLine, out int edgeIndex);
        Equal(edgeLine * 100 + edgeIndex, 15, "the last byte of the row 0");
        edges.Map.TryLocate(16, out edgeLine, out edgeIndex);
        Equal(edgeLine * 100 + edgeIndex, 100, "the first byte of the row 1");

        // One byte, and a pattern longer than a row.
        Terminal sizes = new Terminal(16);
        byte[] longPattern = new byte[40];
        for (int index = 0; index < longPattern.Length; index++)
        {
            longPattern[index] = (byte)(0x80 + index);
        }
        sizes.Append(Filler(7));
        sizes.Append(longPattern);
        sizes.Append(Filler(9));
        Equal(sizes.Map.Find(longPattern, 0, backward: false, wrap: false), 7, "40 bytes are found over 16 bytes rows");
        Equal(sizes.Map.Find(longPattern, 55, backward: true, wrap: false), 7, "40 bytes are found backward");
        sizes.Map.TryLocate(7 + 39, out int longLine, out int longIndex);
        Equal(longLine * 100 + longIndex, 214, "the last of the 40 bytes is in the third row");
        Equal(Join(sizes.Walk(new byte[] { 0x85 }, 0, backward: false)), "12", "one byte is found");
        Equal(sizes.Map.Find(new byte[100], 0, backward: false, wrap: true), -1, "a pattern longer than the stream is not found");

        // Overlapping matches come one byte after another, in both directions.
        Terminal overlap = new Terminal(4);
        overlap.Append(0xaa, 0xaa, 0xaa, 0x01, 0xaa, 0xaa);
        byte[] pair = { 0xaa, 0xaa };
        Equal(Join(overlap.Walk(pair, 0, backward: false)), "0,1,4", "overlapping matches, forward");
        Equal(Join(overlap.Walk(pair, 5, backward: true)), "4,1,0", "overlapping matches, backward");

        // Forward, backward and around: every match once, whatever the start.
        Terminal round = new Terminal(16);
        round.Append(Filler(14));
        round.Append(hello);
        round.Append(Filler(30));
        round.Append(hello);
        round.Append(Filler(5));
        round.Append(hello);
        round.Append(hello);
        List<int> all = round.Expected(hello);
        Equal(Join(all), "14,49,59,64", "the reference search");
        Equal(Join(round.Walk(hello, 0, backward: false)), "14,49,59,64", "forward from the start");
        Equal(Join(round.Walk(hello, 50, backward: false)), "59,64,14,49", "forward from the middle goes around");
        Equal(Join(round.Walk(hello, round.All.Count, backward: true)), "64,59,49,14", "backward from the end");
        Equal(Join(round.Walk(hello, 20, backward: true)), "14,64,59,49", "backward from the middle goes around");
        Equal(Join(round.Walk(hello, -1, backward: true)), "64,59,49,14", "backward from before the first byte goes around");
        Equal(round.Map.Find(hello, 65, backward: false, wrap: false), -1, "nothing behind the last match without going around");
        Equal(round.Map.Find(hello, 13, backward: true, wrap: false), -1, "nothing before the first match without going around");
        Equal(round.Map.Find(hello, 14, backward: true, wrap: false), 14, "backward finds the match that begins at the start");
        Equal(round.Map.Find(hello, 1000, backward: false, wrap: true), 14, "a start behind the stream goes around");

        // A break: no match across it, matches on both sides of it, and the row is shorter.
        Terminal broken = new Terminal(16);
        broken.Append(Filler(3));
        broken.Append(0x48, 0x65, 0x6c);
        broken.Break();
        broken.Append(0x6c, 0x6f);
        broken.Append(hello);
        broken.Append(Filler(12));
        broken.Append(hello);
        Equal(Join(broken.Expected(hello)), "8,25", "the reference search does not cross the break");
        Equal(Join(broken.Walk(hello, 0, backward: false)), "8,25", "no match across a break, forward");
        Equal(Join(broken.Walk(hello, broken.All.Count, backward: true)), "25,8", "no match across a break, backward");
        Equal(Join(broken.Walk(new byte[] { 0x6c, 0x6c }, 0, backward: false)), "10,27", "two bytes on the sides of a break are not a match");
        Equal(Join(broken.Walk(new byte[] { 0x65, 0x6c }, 0, backward: false)), "4,9,26", "a match that ends at a break is found");
        Equal(Join(broken.Walk(new byte[] { 0x6c, 0x6f }, 0, backward: false)), "6,11,28", "a match that begins at a break is found");
        Equal(broken.Map.GetRow(0).Count, 6, "the row before the break is short");
        broken.Map.TryLocate(6, out int brokenLine, out int brokenIndex);
        Equal(brokenLine * 100 + brokenIndex, 100, "the byte behind the break begins the next row");
        broken.Map.TryLocate(25, out brokenLine, out brokenIndex);
        Equal(brokenLine * 100 + brokenIndex, 203, "the rows behind the break are counted from it");
        Equal(broken.Map.OffsetAtOrAfter(0, 3 * 6), 6, "behind the end of a short row is the first byte of the next row");
        Equal(broken.Map.OffsetAtOrAfter(0, 1000), 6, "far behind the end of a short row too");

        // The size of the window changes: other rows, the same matches at the same offsets.
        foreach (int width in new[] { 1, 3, 5, 8, 16, 33 })
        {
            round.Resize(width);
            Equal(Join(round.Walk(hello, 0, backward: false)), Join(all), "the same matches with " + width + " bytes in a row");
            round.Map.TryLocate(49, out int line, out int index);
            Equal(line * 1000 + index, 49 / width * 1000 + 49 % width, "the place of a match with " + width + " bytes in a row");
            broken.Resize(width);
            Equal(Join(broken.Walk(hello, 0, backward: false)), "8,25", "the break stays with " + width + " bytes in a row");
        }
        Equal(round.Map.MappedLength, round.Map.Length, "every byte has a place after the rows were laid out again");

        // Data that arrive later: found by the next search, and a match may begin in the old data.
        Terminal live = new Terminal(16);
        live.Append(Filler(14));
        live.Append(0x48, 0x65);
        Equal(live.Map.Find(hello, 0, backward: false, wrap: true), -1, "half of the bytes are not a match");
        live.Append(0x6c, 0x6c, 0x6f);
        Equal(live.Map.Find(hello, 0, backward: false, wrap: true), 14, "found once the other half arrived");
        Check(live.Map.TryLocate(18, out int liveLine, out int liveIndex) && liveLine == 1 && liveIndex == 2, "the new bytes have a place");
        live.Map.Clear();
        Equal(live.Map.Find(hello, 0, backward: false, wrap: true), -1, "nothing is found after the terminal was cleared");
        Equal(live.Map.Length + live.Map.RowCount, 0, "a cleared stream is empty");

        // Every pattern against the reference, with breaks in random places.
        Random random = new Random(121);
        for (int round2 = 0; round2 < 200; round2++)
        {
            Terminal terminal = new Terminal(1 + random.Next(20));
            int parts = 1 + random.Next(12);
            for (int part = 0; part < parts; part++)
            {
                byte[] bytes = new byte[random.Next(30)];
                for (int index = 0; index < bytes.Length; index++)
                {
                    bytes[index] = (byte)random.Next(3);
                }
                terminal.Append(bytes);
                if (random.Next(3) == 0)
                {
                    terminal.Break();
                }
            }
            byte[] pattern = new byte[1 + random.Next(4)];
            for (int index = 0; index < pattern.Length; index++)
            {
                pattern[index] = (byte)random.Next(3);
            }
            List<int> expected = terminal.Expected(pattern);
            Equal(Join(terminal.Walk(pattern, 0, backward: false)), Join(expected), "random data, forward, round " + round2);
            expected.Reverse();
            Equal(Join(terminal.Walk(pattern, terminal.All.Count, backward: true)), Join(expected), "random data, backward, round " + round2);
            // Every byte is where the rows say it is.
            for (int offset = 0; offset < terminal.All.Count; offset++)
            {
                Check(terminal.Map.TryLocate(offset, out int line, out int index), "random data, a byte has a place, round " + round2);
                Equal(terminal.Map.OffsetAtOrAfter(line, 3 * index), offset, "random data, the place gives the offset back, round " + round2);
            }
            // The marks of the matches: the ranges hold exactly the bytes of the matches that reach into the window.
            int from = random.Next(terminal.All.Count + 1);
            int to = from + random.Next(terminal.All.Count + 1 - from);
            List<int> ranges = new List<int>();
            terminal.Map.CollectMatchRanges(pattern, from, to, ranges, int.MaxValue);
            bool[] marked = new bool[terminal.All.Count];
            for (int index = 0; index < ranges.Count; index += 2)
            {
                Check(index == 0 || ranges[index] >= ranges[index - 1], "random data, the ranges are in order, round " + round2);
                for (int offset = ranges[index]; offset < ranges[index + 1]; offset++)
                {
                    marked[offset] = true;
                }
            }
            bool[] wanted = new bool[terminal.All.Count];
            foreach (int match in terminal.Expected(pattern))
            {
                if (match < to && match + pattern.Length > from)
                {
                    for (int index = 0; index < pattern.Length; index++)
                    {
                        wanted[match + index] = true;
                    }
                }
            }
            for (int offset = 0; offset < marked.Length; offset++)
            {
                Check(marked[offset] == wanted[offset], "random data, the marked bytes, round " + round2);
            }
        }

        // The history has no limit: a search in megabytes is one pass.
        HexStreamMap big = new HexStreamMap();
        byte[] block = new byte[1 << 20];
        for (int index = 0; index < 16; index++)
        {
            big.Append(block, block.Length);
        }
        big.Append(hello, hello.Length);
        DateTime started = DateTime.UtcNow;
        Equal(big.Find(hello, 0, backward: false, wrap: true), 16 << 20, "found at the end of 16 MB");
        Equal(big.Find(hello, (16 << 20) - 1, backward: true, wrap: true), 16 << 20, "found backward around 16 MB");
        Check((DateTime.UtcNow - started).TotalSeconds < 5, "16 MB are searched in both directions in seconds");

        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
