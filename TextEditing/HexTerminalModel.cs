using System;
using System.Collections.Generic;
using System.Text;

namespace SmarTTY.TextEditing;

/// <summary>One row that <see cref="HexTerminalModel.FormatRows"/> made: the text of the HEX view and of the text view, and how many bytes it holds.</summary>
public struct HexRow
{
    /// <summary>The bytes as two lowercase digits each, a blank between them.</summary>
    public string Hex;

    /// <summary>The same bytes as characters: the printable ASCII as it is, every other byte as a dot.</summary>
    public string Text;

    /// <summary>The number of bytes in <see cref="Hex"/>.</summary>
    public int ByteCount;

    /// <summary>The row is full: what follows goes to the next row (false for the last row of a piece of data, which the next data goes on in).</summary>
    public bool EndsRow;
}

/// <summary>One piece of the stream as it came: data (an echo of what was typed, or what the device sent) or a break between messages.</summary>
public struct HexChunk
{
    public bool IsBreak;

    public bool IsEcho;

    /// <summary>The bytes of the chunk are the bytes [Start, Start + Count) of the stream.</summary>
    public int Start;

    public int Count;
}

/// <summary>
/// What the HEX view of a device terminal keeps apart from the two views: the stream of the bytes with the map to their rows, the chunks it came in (the layout is
/// made again from them when the number of bytes in a row changes), when a break is made, how the bytes are formatted into rows, and the match that the search
/// selected. The control writes the rows to its HEX and text views and reads the selection from them; no user interface here (tests/HexTerminalModelTests).
/// </summary>
public sealed class HexTerminalModel
{
    private readonly HexStreamMap _stream = new HexStreamMap();
    private readonly List<HexChunk> _chunks = new List<HexChunk>();
    private DateTime _lastDataTime;
    private bool _lastWasEcho;
    private int _matchOffset = -1;
    private int _matchLength;
    private TextDocument.TextPosition _matchSelectionStart;
    private TextDocument.TextPosition _matchSelectionEnd;

    /// <summary>All the bytes on the screen (the echo too) in the order they are shown, with the row and the column of each.</summary>
    public HexStreamMap Stream => _stream;

    /// <summary>The number of bytes per row the next data is laid out with (set from the width of the window and the settings).</summary>
    public int BytesPerLine { get; set; } = 16;

    /// <summary>The number of bytes per row the data on the screen was laid out with (0: nothing yet).</summary>
    public int LaidOutBytesPerLine { get; private set; }

    /// <summary>The data on the screen carries more than one layout: the history is laid out again after a resize only when it does not match.</summary>
    public bool LayoutMixed { get; private set; }

    /// <summary>The chunks the stream came in, in order.</summary>
    public IReadOnlyList<HexChunk> Chunks => _chunks;

    /// <summary>The offset of the match found last (-1: none).</summary>
    public int MatchOffset => _matchOffset;

    /// <summary>The length of the match found last.</summary>
    public int MatchLength => _matchLength;

    /// <summary>
    /// A break is made in front of data that comes after an echo (or the other way round) when asked for, or after a pause of
    /// <paramref name="timeoutMs"/> milliseconds or more (0: never because of a pause).
    /// </summary>
    public bool ShouldBreak(bool isEcho, bool breaksAroundEcho, int timeoutMs, DateTime now)
    {
        return _lastDataTime != DateTime.MinValue && ((breaksAroundEcho && isEcho != _lastWasEcho) || (timeoutMs > 0 && (now - _lastDataTime).TotalMilliseconds >= (double)timeoutMs));
    }

    /// <summary>Records a break between two messages (the cursors of the views are moved to the next row by the caller).</summary>
    public void AddBreak()
    {
        _chunks.Add(new HexChunk
        {
            IsBreak = true,
            Start = _stream.Length
        });
        _stream.AddBreak();
    }

    /// <summary>
    /// Records data that came: the number of bytes is brought between 0 and the length of the buffer, a chunk of the same kind that came last is made longer,
    /// the bytes are added to the stream. Returns the number of bytes taken.
    /// </summary>
    public int AddData(byte[] buffer, int count, bool isEcho, DateTime now)
    {
        count = Math.Max(0, Math.Min(count, buffer.Length));
        if (_chunks.Count > 0 && CanAppend(_chunks[_chunks.Count - 1], isEcho))
        {
            HexChunk last = _chunks[_chunks.Count - 1];
            last.Count += count;
            _chunks[_chunks.Count - 1] = last;
        }
        else
        {
            _chunks.Add(new HexChunk
            {
                IsEcho = isEcho,
                Start = _stream.Length,
                Count = count
            });
        }
        _stream.Append(buffer, count);
        _lastDataTime = now;
        _lastWasEcho = isEcho;
        return count;
    }

    private static bool CanAppend(HexChunk chunk, bool isEcho)
    {
        if (!chunk.IsBreak)
        {
            return chunk.IsEcho == isEcho;
        }
        return false;
    }

    /// <summary>
    /// Data is about to be laid out with <see cref="BytesPerLine"/>: the layout is mixed when the data on the screen has another number of bytes in its rows.
    /// </summary>
    public void BeginLayout()
    {
        if (LaidOutBytesPerLine != 0 && LaidOutBytesPerLine != BytesPerLine)
        {
            LayoutMixed = true;
        }
        LaidOutBytesPerLine = BytesPerLine;
    }

    /// <summary>
    /// Lays <paramref name="count"/> bytes out in rows of <paramref name="bytesPerLine"/>. <paramref name="startColumn"/> is the number of bytes already in the row
    /// the cursor is in. The last row is not full unless the bytes end at the end of a row (it is not <see cref="HexRow.EndsRow"/>: the next data goes on there).
    /// </summary>
    public static List<HexRow> FormatRows(byte[] buffer, int count, int startColumn, int bytesPerLine)
    {
        List<HexRow> rows = new List<HexRow>();
        int currentColumn = startColumn;
        StringBuilder stringBuilder = new StringBuilder(bytesPerLine * 4);
        StringBuilder hexTextBuilder = new StringBuilder(bytesPerLine * 2);
        int pendingBytes = 0;
        for (int byteIndex = 0; byteIndex < count; byteIndex++)
        {
            byte currentByte = buffer[byteIndex];
            if (currentColumn >= bytesPerLine)
            {
                rows.Add(new HexRow
                {
                    Hex = stringBuilder.ToString(),
                    Text = hexTextBuilder.ToString(),
                    ByteCount = pendingBytes,
                    EndsRow = true
                });
                pendingBytes = 0;
                int length = (hexTextBuilder.Length = 0);
                stringBuilder.Length = length;
                currentColumn = 0;
            }
            if (currentColumn != 0)
            {
                stringBuilder.Append(" ");
            }
            currentColumn++;
            pendingBytes++;
            stringBuilder.AppendFormat("{0:x2}", currentByte);
            // Only the printable ASCII range is shown as it is. Encoding.ASCII turned every byte above 0x7F into "?" (the same as a real
            // 0x3F), and 0x7F went to the screen as a control character.
            hexTextBuilder.Append(HexDocument.IsPrintable(currentByte) ? (char)currentByte : '.');
        }
        rows.Add(new HexRow
        {
            Hex = stringBuilder.ToString(),
            Text = hexTextBuilder.ToString(),
            ByteCount = pendingBytes,
            EndsRow = false
        });
        return rows;
    }

    /// <summary>Forgets the stream, the chunks, the match and the layout (the views are reset by the caller).</summary>
    public void Clear()
    {
        _chunks.Clear();
        _stream.Clear();
        _matchOffset = -1;
        _lastDataTime = DateTime.MinValue;
        LaidOutBytesPerLine = 0;
        LayoutMixed = false;
    }

    /// <summary>
    /// The history is laid out again after a resize only when it was laid out with another number of bytes in a row or with more than one: the lines are laid
    /// out by the number of bytes in them, and with the same number nothing changes (the whole history was laid out again, and the scroll position lost, at every change of the size).
    /// </summary>
    public bool NeedsRelayout => _chunks.Count != 0 && (LayoutMixed || LaidOutBytesPerLine != BytesPerLine);

    /// <summary>Starts laying the history out again: the rows are forgotten (the bytes and the chunks are kept) and so is the match, which the caller selects again.</summary>
    public void BeginRelayout()
    {
        _matchOffset = -1;
        _stream.ClearRows();
        LaidOutBytesPerLine = 0;
        LayoutMixed = false;
    }

    // ---- the match that the search selected

    /// <summary>Remembers the match that was selected, and the selection of the HEX view that shows it.</summary>
    public void SetMatch(int offset, int length, TextDocument.TextPosition selectionStart, TextDocument.TextPosition selectionEnd)
    {
        _matchOffset = offset;
        _matchLength = length;
        _matchSelectionStart = selectionStart;
        _matchSelectionEnd = selectionEnd;
    }

    /// <summary>Forgets the match (the search found nothing, or the data was cleared).</summary>
    public void ClearMatch()
    {
        _matchOffset = -1;
    }

    /// <summary>The match found last is still what the HEX view has selected.</summary>
    public bool IsMatchSelected(bool hasSelection, TextDocument.TextPosition selectionStart, TextDocument.TextPosition selectionEnd)
    {
        return _matchOffset >= 0 && hasSelection && selectionStart == _matchSelectionStart && selectionEnd == _matchSelectionEnd;
    }

    /// <summary>
    /// Where the search begins: one byte behind (or before) the start of the match found last, so that overlapping matches are found one by one; otherwise behind the
    /// selection or the caret (before it, backward).
    /// </summary>
    public int GetSearchStart(bool backward, bool matchSelected, TextDocument.TextPosition selectionStart, TextDocument.TextPosition selectionEnd)
    {
        if (matchSelected)
        {
            return backward ? (_matchOffset - 1) : (_matchOffset + 1);
        }
        TextDocument.TextPosition position = new TextDocument.TextPosition(0, 0);
        if (selectionStart.IsValid)
        {
            position = selectionStart;
        }
        TextDocument.TextPosition end = selectionEnd;
        if (end.IsValid && (backward ? (end < position) : (end > position)))
        {
            position = end;
        }
        int offset = _stream.OffsetAtOrAfter(position.Line, position.Column);
        return backward ? (offset - 1) : offset;
    }

    // ---- where a byte is in the two views

    /// <summary>The column of the first digit of byte number <paramref name="indexInRow"/> in the HEX view (three columns per byte).</summary>
    public static int HexStartColumn(int indexInRow)
    {
        return 3 * indexInRow;
    }

    /// <summary>The column after the last digit of byte number <paramref name="indexInRow"/> in the HEX view (the blank behind it is not counted).</summary>
    public static int HexEndColumn(int indexInRow)
    {
        return 3 * indexInRow + 2;
    }

    /// <summary>The selection of the HEX view as the text view shows it (one column per byte): the columns of both ends turned to bytes, in the direction of the selection.</summary>
    public static void HexSelectionToText(ref TextDocument.TextPosition start, ref TextDocument.TextPosition end)
    {
        if (end < start)
        {
            start.Column = (start.Column + 2) / 3;
            end.Column = (end.Column + 1) / 3;
        }
        else
        {
            start.Column = (start.Column + 1) / 3;
            end.Column = (end.Column + 2) / 3;
        }
    }

    /// <summary>The selection of the text view as the HEX view shows it: the columns of both ends turned to the first and the last digit of the bytes.</summary>
    public static void TextSelectionToHex(ref TextDocument.TextPosition start, ref TextDocument.TextPosition end)
    {
        if (end < start)
        {
            start.Column = Math.Max(0, start.Column * 3 - 1);
            end.Column *= 3;
        }
        else
        {
            start.Column *= 3;
            end.Column = Math.Max(0, end.Column * 3 - 1);
        }
    }
}
