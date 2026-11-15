using System;
using System.Collections.Generic;

namespace SmarTTY.TextEditing;

/// <summary>
/// The bytes a HEX terminal shows, as one stream, with the map from a byte to its place on the screen.
/// The screen shows the bytes in rows ("48 65 6c ...": byte number i of a row stands at column 3·i), and a sequence
/// that goes on in the next row cannot be found in the text of the rows. So the search runs over the stream, and the
/// map tells where a found sequence is: a row is a line of the HEX view with the offset of its first byte and the
/// number of its bytes (fewer than the width allows where a break ended the row).
/// A break (an echo, a pause in the data) separates messages: a match never goes across one.
/// No user interface here: tests/HexStreamMapTests checks it.
/// </summary>
public sealed class HexStreamMap
{
    /// <summary>One line of the HEX view: the bytes [First, First + Count) of the stream.</summary>
    public struct Row
    {
        public int Line;

        public int First;

        public int Count;
    }

    private readonly List<byte> _bytes = new List<byte>();

    // The offsets a break stands in front of, in ascending order.
    private readonly List<int> _breaks = new List<int>();

    // In ascending order of both the line and the first byte.
    private readonly List<Row> _rows = new List<Row>();

    private int _mappedLength;

    /// <summary>The number of bytes in the stream.</summary>
    public int Length => _bytes.Count;

    /// <summary>The number of bytes that have a place on the screen (all of them, but while the rows are laid out again).</summary>
    public int MappedLength => _mappedLength;

    public int RowCount => _rows.Count;

    public Row GetRow(int index)
    {
        return _rows[index];
    }

    public void Append(byte[] buffer, int count)
    {
        if (buffer == null)
        {
            return;
        }
        for (int index = 0; index < count && index < buffer.Length; index++)
        {
            _bytes.Add(buffer[index]);
        }
    }

    /// <summary>The bytes that come next belong to another message: no match holds bytes of both sides.</summary>
    public void AddBreak()
    {
        int offset = _bytes.Count;
        if (offset > 0 && (_breaks.Count == 0 || _breaks[_breaks.Count - 1] != offset))
        {
            _breaks.Add(offset);
        }
    }

    public byte[] GetRange(int start, int count)
    {
        byte[] result = new byte[count];
        _bytes.CopyTo(start, result, 0, count);
        return result;
    }

    public void Clear()
    {
        _bytes.Clear();
        _breaks.Clear();
        ClearRows();
    }

    /// <summary>Forgets the places of the bytes (the stream stays): the rows are about to be laid out again.</summary>
    public void ClearRows()
    {
        _rows.Clear();
        _mappedLength = 0;
    }

    /// <summary>The next <paramref name="count"/> bytes without a place were written to the line <paramref name="line"/> of the view.</summary>
    public void MapBytes(int line, int count)
    {
        count = Math.Min(count, _bytes.Count - _mappedLength);
        if (count <= 0)
        {
            return;
        }
        if (_rows.Count > 0 && _rows[_rows.Count - 1].Line == line)
        {
            Row last = _rows[_rows.Count - 1];
            last.Count += count;
            _rows[_rows.Count - 1] = last;
        }
        else
        {
            _rows.Add(new Row
            {
                Line = line,
                First = _mappedLength,
                Count = count
            });
        }
        _mappedLength += count;
    }

    /// <summary>The line of the byte at <paramref name="offset"/> and its number in that line (its column is 3 times that).</summary>
    public bool TryLocate(int offset, out int line, out int indexInLine)
    {
        line = -1;
        indexInLine = -1;
        if (offset < 0 || offset >= _mappedLength)
        {
            return false;
        }
        // The last row that begins at or before the offset.
        int low = 0;
        int high = _rows.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (_rows[middle].First <= offset)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }
        Row row = _rows[low];
        if (offset < row.First || offset >= row.First + row.Count)
        {
            return false;
        }
        line = row.Line;
        indexInLine = offset - row.First;
        return true;
    }

    /// <summary>
    /// The offset of the first byte that begins at or after the place (<paramref name="line"/>, <paramref name="column"/>)
    /// of the HEX view; the number of the bytes on the screen when there is none.
    /// </summary>
    public int OffsetAtOrAfter(int line, int column)
    {
        // The first row at or below the line.
        int low = 0;
        int high = _rows.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (_rows[middle].Line < line)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        if (low >= _rows.Count)
        {
            return _mappedLength;
        }
        Row row = _rows[low];
        if (row.Line > line || column <= 0)
        {
            return row.First;
        }
        // A byte takes the columns 3·i and 3·i + 1, the space after it 3·i + 2.
        int index = (int)Math.Min(((long)column + 2) / 3, row.Count);
        return row.First + index;
    }

    /// <summary>
    /// Finds <paramref name="pattern"/> in the stream. Forward: the first match that begins at or after
    /// <paramref name="startOffset"/>; backward: the last one that begins at or before it. With <paramref name="wrap"/>
    /// the search goes on from the other end of the stream up to where it began. A start before the first byte or
    /// behind the last one is fine. Returns the offset of the match, or -1.
    /// </summary>
    public int Find(byte[] pattern, int startOffset, bool backward, bool wrap)
    {
        if (pattern == null || pattern.Length == 0 || pattern.Length > _bytes.Count)
        {
            return -1;
        }
        int last = _bytes.Count - pattern.Length;
        int found;
        if (backward)
        {
            found = (startOffset >= 0) ? ScanBackward(pattern, Math.Min(startOffset, last), 0) : -1;
            if (found < 0 && wrap && startOffset < last)
            {
                found = ScanBackward(pattern, last, Math.Max(startOffset + 1, 0));
            }
        }
        else
        {
            int start = Math.Max(startOffset, 0);
            found = (start <= last) ? ScanForward(pattern, start, last) : -1;
            if (found < 0 && wrap && start > 0)
            {
                found = ScanForward(pattern, 0, Math.Min(start - 1, last));
            }
        }
        return found;
    }

    /// <summary>
    /// The matches that have a byte in [<paramref name="from"/>, <paramref name="to"/>), as ranges of offsets
    /// (start, then end, the end not included) added to <paramref name="ranges"/>. Matches that touch or overlap
    /// make one range. No more than <paramref name="maximumMatches"/> matches are looked at.
    /// </summary>
    public void CollectMatchRanges(byte[] pattern, int from, int to, List<int> ranges, int maximumMatches)
    {
        if (pattern == null || pattern.Length == 0 || pattern.Length > _bytes.Count || ranges == null)
        {
            return;
        }
        int last = Math.Min(_bytes.Count - pattern.Length, to - 1);
        int position = Math.Max(0, from - (pattern.Length - 1));
        int rangeStart = -1;
        int rangeEnd = -1;
        for (int count = 0; count < maximumMatches && position <= last; count++)
        {
            int match = ScanForward(pattern, position, last);
            if (match < 0)
            {
                break;
            }
            if (rangeStart >= 0 && match <= rangeEnd && SegmentEnd(match) == SegmentEnd(rangeStart))
            {
                rangeEnd = match + pattern.Length;
            }
            else
            {
                if (rangeStart >= 0)
                {
                    ranges.Add(rangeStart);
                    ranges.Add(rangeEnd);
                }
                rangeStart = match;
                rangeEnd = match + pattern.Length;
            }
            position = match + 1;
        }
        if (rangeStart >= 0)
        {
            ranges.Add(rangeStart);
            ranges.Add(rangeEnd);
        }
    }

    // The number of breaks at or before the offset: the index of the first break behind it.
    private int BreaksUpTo(int offset)
    {
        int low = 0;
        int high = _breaks.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (_breaks[middle] <= offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    private int SegmentEnd(int offset)
    {
        int next = BreaksUpTo(offset);
        return (next < _breaks.Count) ? _breaks[next] : _bytes.Count;
    }

    private bool MatchesAt(byte[] pattern, int offset)
    {
        for (int index = pattern.Length - 1; index >= 0; index--)
        {
            if (_bytes[offset + index] != pattern[index])
            {
                return false;
            }
        }
        return true;
    }

    // The first match that begins in [from, to], or -1.
    private int ScanForward(byte[] pattern, int from, int to)
    {
        int next = BreaksUpTo(from);
        int position = from;
        while (position <= to)
        {
            int segmentEnd = (next < _breaks.Count) ? _breaks[next] : _bytes.Count;
            int limit = Math.Min(to, segmentEnd - pattern.Length);
            while (position <= limit)
            {
                // The first byte is looked for by the library (it compares many bytes at once).
                int candidate = _bytes.IndexOf(pattern[0], position, limit - position + 1);
                if (candidate < 0)
                {
                    break;
                }
                if (MatchesAt(pattern, candidate))
                {
                    return candidate;
                }
                position = candidate + 1;
            }
            position = Math.Max(position, segmentEnd);
            next++;
        }
        return -1;
    }

    // The last match that begins in [lowest, from], or -1.
    private int ScanBackward(byte[] pattern, int from, int lowest)
    {
        int next = BreaksUpTo(from);
        int position = from;
        while (position >= lowest)
        {
            int segmentStart = (next > 0) ? _breaks[next - 1] : 0;
            int segmentEnd = (next < _breaks.Count) ? _breaks[next] : _bytes.Count;
            int floor = Math.Max(segmentStart, lowest);
            for (int candidate = Math.Min(position, segmentEnd - pattern.Length); candidate >= floor; candidate--)
            {
                if (_bytes[candidate] == pattern[0] && MatchesAt(pattern, candidate))
                {
                    return candidate;
                }
            }
            position = segmentStart - 1;
            next--;
        }
        return -1;
    }
}
