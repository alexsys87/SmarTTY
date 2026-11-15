using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SmarTTY.TextEditing;

/// <summary>
/// A block of bytes the user coloured in the hex editor (a header, a checksum, a field of a structure), with an
/// optional label. The document moves it with the bytes when bytes are inserted or removed before or inside it.
/// </summary>
public sealed class HexHighlight
{
    internal HexHighlight(int start, int length, int argb, string label)
    {
        Start = start;
        Length = length;
        Argb = argb;
        Label = label ?? "";
    }

    public int Start { get; internal set; }

    public int Length { get; internal set; }

    /// <summary>The offset just after the block.</summary>
    public int End => Start + Length;

    /// <summary>The colour as 0xAARRGGBB (the document has no UI types).</summary>
    public int Argb { get; }

    public string Label { get; }

    public bool Contains(int offset)
    {
        return offset >= Start && offset < End;
    }
}

/// <summary>
/// The bytes of a file being edited in the hex editor. It keeps the undo and redo history, remembers the version that
/// was last saved (so that undoing back to it clears the modified mark, as in the text editor) and compares the bytes
/// with the saved ones to show which of them changed. It has no UI and no file access: the editor control draws it and
/// <see cref="Controls.RemoteFileEditor"/> saves it.
/// </summary>
/// <summary>The arithmetic of the scrolling of the hex editor, apart from the control so that it can be tested.</summary>
public static class HexScroll
{
    /// <summary>The horizontal scroll position kept inside the content: not before its start, not past the point where its end is at the right edge.</summary>
    public static int ClampScrollX(int scrollX, int contentWidth, int visibleWidth)
    {
        return Math.Max(0, Math.Min(scrollX, Math.Max(0, contentWidth - visibleWidth)));
    }

    /// <summary>
    /// How many rows (or columns) to scroll at a timer tick while a selection is dragged <paramref name="distance"/> pixels beyond the
    /// edge of the table, where <paramref name="unit"/> pixels are one row (one column): one at the edge, more the farther out the mouse is.
    /// </summary>
    public static int AutoScrollSteps(int distance, int unit, int maxSteps = 20)
    {
        if (distance <= 0)
        {
            return 0;
        }
        return Math.Min(Math.Max(1, maxSteps), 1 + distance / Math.Max(1, unit));
    }
}

public sealed class HexDocument
{
    private sealed class Edit
    {
        public int Offset;

        public byte[] Removed;

        public byte[] Inserted;
    }

    // No undo step can be the saved one: the document was marked modified without an edit (a restored draft).
    private static readonly object NeverSaved = new object();

    private readonly List<byte> _bytes;

    /// <summary>
    /// The undo steps, the newest on top. Unlike a plain stack it can drop its oldest step, so that the history does not keep
    /// the bytes of every edit for as long as the tab is open (see <see cref="UndoLimitBytes"/>).
    /// </summary>
    private sealed class EditHistory
    {
        private readonly LinkedList<Edit> _items = new LinkedList<Edit>();

        public int Count => _items.Count;

        /// <summary>The bytes that the steps hold (those removed and those inserted).</summary>
        public long Bytes { get; private set; }

        public void Push(Edit edit)
        {
            _items.AddLast(edit);
            Bytes += Size(edit);
        }

        public Edit Peek()
        {
            return _items.Last.Value;
        }

        public Edit Pop()
        {
            Edit edit = _items.Last.Value;
            _items.RemoveLast();
            Bytes -= Size(edit);
            return edit;
        }

        public Edit RemoveOldest()
        {
            Edit edit = _items.First.Value;
            _items.RemoveFirst();
            Bytes -= Size(edit);
            return edit;
        }

        public void Clear()
        {
            _items.Clear();
            Bytes = 0;
        }

        private static long Size(Edit edit)
        {
            return (long)edit.Removed.Length + edit.Inserted.Length;
        }
    }

    /// <summary>What <see cref="UndoLimitBytes"/> is at the start: 64 MB of bytes in the undo steps.</summary>
    public const long DefaultUndoLimitBytes = 64L * 1024 * 1024;

    private readonly EditHistory _undo = new EditHistory();

    private readonly Stack<Edit> _redo = new Stack<Edit>();

    // The undo step (null: the empty history) that the bytes matched when they were last saved.
    private object _savedStep;

    // The bytes as they were on the server: what the editor highlights as changed.
    private byte[] _savedBytes;

    // Where the bytes differ from the saved ones when their number differs (an insert or a delete moves every byte after it, and
    // comparing offset by offset would call all of them changed): the first _samePrefix bytes and the last _sameSuffix bytes are
    // known to equal the saved ones (the same count from the start, from the end), the bytes between them are changed. They are
    // kept up to date by every edit at no cost and found by a scan after an undo, a redo or a save (_diffStale).
    private int _samePrefix;

    private int _sameSuffix;

    private bool _diffStale = true;

    private bool _lastModified;

    // The coloured blocks, sorted by their start; they never overlap.
    private readonly List<HexHighlight> _highlights = new List<HexHighlight>();

    /// <summary>The bytes changed (an edit, an undo or a redo).</summary>
    public event EventHandler Changed;

    /// <summary>Whether the document differs from the saved version changed.</summary>
    public event EventHandler ModifiedChanged;

    /// <summary>A coloured block was added or removed (an edit that only moves them raises <see cref="Changed"/>).</summary>
    public event EventHandler HighlightsChanged;

    public HexDocument()
        : this(Array.Empty<byte>())
    {
    }

    /// <summary>A document whose bytes are the saved version.</summary>
    public HexDocument(byte[] bytes)
    {
        _bytes = new List<byte>(bytes ?? Array.Empty<byte>());
        _savedBytes = _bytes.ToArray();
        _savedStep = null;
        _lastModified = false;
        _samePrefix = _bytes.Count;
        _sameSuffix = _bytes.Count;
        _diffStale = false;
    }

    /// <summary>
    /// A document with unsaved changes: <paramref name="bytes"/> are the current bytes and
    /// <paramref name="originalBytes"/> the bytes that are still on the server. The document is modified when they
    /// differ.
    /// </summary>
    public HexDocument(byte[] bytes, byte[] originalBytes)
    {
        _bytes = new List<byte>(bytes ?? Array.Empty<byte>());
        _savedBytes = originalBytes ?? Array.Empty<byte>();
        _savedStep = ByteArraysEqual(_bytes.ToArray(), _savedBytes) ? null : NeverSaved;
        _lastModified = _savedStep != null;
    }

    public int Length => _bytes.Count;

    public byte this[int offset] => _bytes[offset];

    public bool IsModified => !ReferenceEquals(CurrentStep, _savedStep);

    /// <summary>Identifies the current content for <see cref="MarkSaved()"/>, like the text editor's version.</summary>
    public object CurrentVersion
    {
        get
        {
            // A save that starts now describes this step: the second digit of a byte being typed must not be joined to it afterwards
            // (the saved mark would then sit on a content that was never saved).
            _sealedStep = CurrentStep;
            return _sealedStep;
        }
    }

    private object _sealedStep;

    private object CurrentStep => (_undo.Count > 0) ? _undo.Peek() : null;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// The byte at <paramref name="offset"/> is not in the saved version: it was typed, or replaced. When the length changed, a byte
    /// that merely moved (the ones after an insert, after a delete) is not changed: the bytes are lined up from the start and from
    /// the end, and what is left between is the change.
    /// </summary>
    public bool IsByteChanged(int offset)
    {
        if (offset < 0 || offset >= _bytes.Count || !IsModified)
        {
            return false;
        }
        if (_savedBytes.Length == _bytes.Count)
        {
            return _savedBytes[offset] != _bytes[offset];
        }
        if (_diffStale)
        {
            ScanDifference();
        }
        return offset >= _samePrefix && offset < _bytes.Count - _sameSuffix;
    }

    private void ScanDifference()
    {
        int saved = _savedBytes.Length;
        int current = _bytes.Count;
        int common = Math.Min(saved, current);
        int prefix = 0;
        while (prefix < common && _savedBytes[prefix] == _bytes[prefix])
        {
            prefix++;
        }
        int suffix = 0;
        while (suffix < common && _savedBytes[saved - 1 - suffix] == _bytes[current - 1 - suffix])
        {
            suffix++;
        }
        _samePrefix = prefix;
        _sameSuffix = suffix;
        _diffStale = false;
    }

    // An edit at offset that took out removed bytes and put in inserted ones: what lay before it and what lay after it is still as
    // it was, what it touched may now differ (it may equal the saved byte again; that is found by the next scan).
    private void NoteEdit(int offset, int removed, int inserted)
    {
        if (_diffStale)
        {
            return;
        }
        _samePrefix = Math.Min(_samePrefix, offset);
        int tailAfterEdit = _bytes.Count - (offset + inserted);
        _sameSuffix = Math.Max(0, Math.Min(_sameSuffix, tailAfterEdit));
    }

    /// <summary>
    /// The most that the undo steps may hold, as the bytes that they took out and put in. When the steps hold more, the oldest are
    /// forgotten (they can no longer be undone); the newest is always kept, however large it is.
    /// </summary>
    public long UndoLimitBytes { get; set; } = DefaultUndoLimitBytes;

    /// <summary>The bytes held by the undo steps.</summary>
    public long UndoBytes => _undo.Bytes;

    /// <summary>How many steps can be undone.</summary>
    public int UndoSteps => _undo.Count;

    private void TrimUndo()
    {
        while (_undo.Count > 1 && _undo.Bytes > UndoLimitBytes)
        {
            Edit dropped = _undo.RemoveOldest();
            if (_savedStep == null)
            {
                // The saved bytes were what the history started from: undoing everything left no longer gets back to them.
                _savedStep = NeverSaved;
            }
            else if (ReferenceEquals(_savedStep, dropped))
            {
                // The saved bytes are what the history now starts from.
                _savedStep = null;
            }
        }
    }

    public byte[] ToArray()
    {
        return _bytes.ToArray();
    }

    public byte[] GetBytes(int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > _bytes.Count - count)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        byte[] result = new byte[count];
        _bytes.CopyTo(offset, result, 0, count);
        return result;
    }

    public void Insert(int offset, byte[] bytes)
    {
        Replace(offset, 0, bytes);
    }

    public void Remove(int offset, int count)
    {
        Replace(offset, count, null);
    }

    public void ReplaceByte(int offset, byte value)
    {
        Replace(offset, 1, new[] { value });
    }

    /// <summary>
    /// Replaces the byte at <paramref name="offset"/> and joins the change to the last undo step, which must have put a
    /// single byte at the same place (the second digit of a byte being typed), so that one undo takes the whole byte
    /// back. Without such a step it is a plain <see cref="ReplaceByte"/>.
    /// </summary>
    public void ReplaceByteIntoLastStep(int offset, byte value)
    {
        if (_undo.Count > 0 && _redo.Count == 0 && !ReferenceEquals(_undo.Peek(), _savedStep) && !ReferenceEquals(_undo.Peek(), _sealedStep))
        {
            Edit last = _undo.Peek();
            // A step that was saved must keep its bytes: the saved mark would otherwise stay on changed content.
            if (last.Offset == offset && last.Inserted.Length == 1 && last.Removed.Length <= 1 && offset < _bytes.Count)
            {
                last.Inserted = new[] { value };
                _bytes[offset] = value;
                NoteEdit(offset, 1, 1);
                RaiseChanged();
                return;
            }
        }
        ReplaceByte(offset, value);
    }

    /// <summary>Replaces <paramref name="count"/> bytes at <paramref name="offset"/> (one undo step).</summary>
    public void Replace(int offset, int count, byte[] bytes)
    {
        if (offset < 0 || offset > _bytes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        if (count < 0 || offset > _bytes.Count - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        bytes = bytes ?? Array.Empty<byte>();
        if (count == 0 && bytes.Length == 0)
        {
            return;
        }
        Edit edit = new Edit
        {
            Offset = offset,
            Removed = GetBytes(offset, count),
            Inserted = (byte[])bytes.Clone()
        };
        Apply(edit);
        NoteEdit(offset, count, bytes.Length);
        _redo.Clear();
        _undo.Push(edit);
        TrimUndo();
        RaiseChanged();
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }
        Edit edit = _undo.Pop();
        Revert(edit);
        _diffStale = true;
        _redo.Push(edit);
        RaiseChanged();
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }
        Edit edit = _redo.Pop();
        Apply(edit);
        _diffStale = true;
        _undo.Push(edit);
        TrimUndo();
        RaiseChanged();
    }

    /// <summary>Records the current bytes as saved; an undo or redo back to this version clears the modified mark.</summary>
    public void MarkSaved()
    {
        MarkSaved(CurrentStep, _bytes.ToArray());
    }

    /// <summary>
    /// Records that <paramref name="version"/> (from <see cref="CurrentVersion"/> before a background save started) has
    /// been written, with the bytes that were written. Edits made meanwhile keep the document modified.
    /// </summary>
    public void MarkSaved(object version, byte[] savedBytes)
    {
        _savedStep = version;
        _savedBytes = savedBytes ?? Array.Empty<byte>();
        _diffStale = true;
        RaiseModifiedIfNeeded();
    }

    /// <summary>The current bytes are unsaved (a restored draft): there is no undo step that makes them clean.</summary>
    public void MarkModified()
    {
        _savedStep = NeverSaved;
        RaiseModifiedIfNeeded();
    }

    private void Apply(Edit edit)
    {
        if (edit.Removed.Length > 0)
        {
            _bytes.RemoveRange(edit.Offset, edit.Removed.Length);
        }
        if (edit.Inserted.Length > 0)
        {
            _bytes.InsertRange(edit.Offset, edit.Inserted);
        }
        MoveHighlights(edit.Offset, edit.Removed.Length, edit.Inserted.Length);
    }

    private void Revert(Edit edit)
    {
        if (edit.Inserted.Length > 0)
        {
            _bytes.RemoveRange(edit.Offset, edit.Inserted.Length);
        }
        if (edit.Removed.Length > 0)
        {
            _bytes.InsertRange(edit.Offset, edit.Removed);
        }
        MoveHighlights(edit.Offset, edit.Inserted.Length, edit.Removed.Length);
    }

    /// <summary>The coloured blocks, sorted by offset.</summary>
    public IReadOnlyList<HexHighlight> Highlights => _highlights;

    /// <summary>
    /// Colours <paramref name="length"/> bytes at <paramref name="start"/> with <paramref name="argb"/>. The block takes
    /// the place of the parts of other blocks it covers.
    /// </summary>
    public HexHighlight AddHighlight(int start, int length, int argb, string label)
    {
        if (start < 0 || length <= 0 || start > _bytes.Count - length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
        CutHighlights(start, start + length);
        HexHighlight highlight = new HexHighlight(start, length, argb, label);
        int index = 0;
        while (index < _highlights.Count && _highlights[index].Start < start)
        {
            index++;
        }
        _highlights.Insert(index, highlight);
        HighlightsChanged?.Invoke(this, EventArgs.Empty);
        return highlight;
    }

    /// <summary>
    /// Removes the colour from <paramref name="length"/> bytes at <paramref name="start"/>; with no length, removes the
    /// whole block at <paramref name="start"/>. False when there was nothing to remove.
    /// </summary>
    public bool RemoveHighlights(int start, int length)
    {
        bool removed;
        if (length <= 0)
        {
            HexHighlight highlight = GetHighlightAt(start);
            removed = highlight != null && _highlights.Remove(highlight);
        }
        else
        {
            removed = CutHighlights(start, start + length);
        }
        if (removed)
        {
            HighlightsChanged?.Invoke(this, EventArgs.Empty);
        }
        return removed;
    }

    public void ClearHighlights()
    {
        if (_highlights.Count == 0)
        {
            return;
        }
        _highlights.Clear();
        HighlightsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The block that colours the byte at <paramref name="offset"/>, or null.</summary>
    public HexHighlight GetHighlightAt(int offset)
    {
        int low = 0;
        int high = _highlights.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            HexHighlight highlight = _highlights[middle];
            if (offset < highlight.Start)
            {
                high = middle - 1;
            }
            else if (offset >= highlight.End)
            {
                low = middle + 1;
            }
            else
            {
                return highlight;
            }
        }
        return null;
    }

    /// <summary>
    /// The first block that starts after <paramref name="offset"/> (before it when <paramref name="forward"/> is false),
    /// continuing from the other end of the file; null when there are no blocks.
    /// </summary>
    public HexHighlight FindHighlight(int offset, bool forward)
    {
        if (_highlights.Count == 0)
        {
            return null;
        }
        if (forward)
        {
            foreach (HexHighlight highlight in _highlights)
            {
                if (highlight.Start > offset)
                {
                    return highlight;
                }
            }
            return _highlights[0];
        }
        for (int index = _highlights.Count - 1; index >= 0; index--)
        {
            if (_highlights[index].Start < offset)
            {
                return _highlights[index];
            }
        }
        return _highlights[_highlights.Count - 1];
    }

    /// <summary>Takes over the blocks of <paramref name="other"/> (the same file shown again), cut to this length.</summary>
    public void CopyHighlightsFrom(HexDocument other)
    {
        if (other == null || ReferenceEquals(other, this))
        {
            return;
        }
        _highlights.Clear();
        foreach (HexHighlight highlight in other._highlights)
        {
            int end = Math.Min(highlight.End, _bytes.Count);
            if (highlight.Start < end)
            {
                _highlights.Add(new HexHighlight(highlight.Start, end - highlight.Start, highlight.Argb, highlight.Label));
            }
        }
        HighlightsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Removes the parts of the blocks between start and end; a block that spans the range is split in two.
    private bool CutHighlights(int start, int end)
    {
        bool changed = false;
        for (int index = _highlights.Count - 1; index >= 0; index--)
        {
            HexHighlight highlight = _highlights[index];
            if (highlight.End <= start || highlight.Start >= end)
            {
                continue;
            }
            changed = true;
            bool keepsHead = highlight.Start < start;
            bool keepsTail = highlight.End > end;
            if (keepsHead && keepsTail)
            {
                _highlights.Insert(index + 1, new HexHighlight(end, highlight.End - end, highlight.Argb, highlight.Label));
                highlight.Length = start - highlight.Start;
            }
            else if (keepsHead)
            {
                highlight.Length = start - highlight.Start;
            }
            else if (keepsTail)
            {
                highlight.Length = highlight.End - end;
                highlight.Start = end;
            }
            else
            {
                _highlights.RemoveAt(index);
            }
        }
        return changed;
    }

    /// <summary>
    /// Moves the blocks after <paramref name="removed"/> bytes at <paramref name="offset"/> were replaced by
    /// <paramref name="inserted"/> ones. Bytes typed over in place keep their colour; bytes inserted at the start of a
    /// block go before it, inside it make it longer; a block whose bytes were all removed disappears.
    /// </summary>
    private void MoveHighlights(int offset, int removed, int inserted)
    {
        if (removed == inserted || _highlights.Count == 0)
        {
            return;
        }
        int removedEnd = offset + removed;
        int shift = inserted - removed;
        for (int index = _highlights.Count - 1; index >= 0; index--)
        {
            HexHighlight highlight = _highlights[index];
            int start = highlight.Start;
            int end = highlight.End;
            if (start >= removedEnd)
            {
                start += shift;
            }
            else if (start >= offset)
            {
                // The first bytes of the block were removed: it now starts after the new bytes.
                start = offset + inserted;
            }
            if (end <= offset)
            {
                // Nothing changes inside the block (bytes added right after it do not join it).
            }
            else if (end >= removedEnd)
            {
                end += shift;
            }
            else
            {
                end = offset;
            }
            if (end <= start)
            {
                _highlights.RemoveAt(index);
                continue;
            }
            highlight.Start = start;
            highlight.Length = end - start;
        }
    }

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseModifiedIfNeeded();
    }

    private void RaiseModifiedIfNeeded()
    {
        bool modified = IsModified;
        if (modified != _lastModified)
        {
            _lastModified = modified;
            ModifiedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Finds <paramref name="pattern"/> at or after <paramref name="start"/> (before it when
    /// <paramref name="forward"/> is false), wrapping around the end of the document unless
    /// <paramref name="wrapAround"/> is false. Returns -1 when there is no match. <paramref name="ignoreCase"/>
    /// folds ASCII letters only: the other bytes are compared exactly.
    /// </summary>
    public int IndexOf(byte[] pattern, int start, bool forward, bool ignoreCase = false, bool wrapAround = true)
    {
        if (pattern == null || pattern.Length == 0 || pattern.Length > _bytes.Count)
        {
            return -1;
        }
        int last = _bytes.Count - pattern.Length;
        if (forward)
        {
            if (start < 0)
            {
                start = 0;
            }
            if (start > last)
            {
                if (!wrapAround)
                {
                    return -1;
                }
                start = 0;
            }
            for (int offset = start; offset <= last; offset++)
            {
                if (Matches(pattern, offset, ignoreCase))
                {
                    return offset;
                }
            }
            if (wrapAround)
            {
                for (int offset = 0; offset < start; offset++)
                {
                    if (Matches(pattern, offset, ignoreCase))
                    {
                        return offset;
                    }
                }
            }
        }
        else
        {
            if (start > last)
            {
                start = last;
            }
            if (start < 0)
            {
                if (!wrapAround)
                {
                    return -1;
                }
                start = last;
            }
            for (int offset = start; offset >= 0; offset--)
            {
                if (Matches(pattern, offset, ignoreCase))
                {
                    return offset;
                }
            }
            if (wrapAround)
            {
                for (int offset = last; offset > start; offset--)
                {
                    if (Matches(pattern, offset, ignoreCase))
                    {
                        return offset;
                    }
                }
            }
        }
        return -1;
    }

    private bool Matches(byte[] pattern, int offset, bool ignoreCase)
    {
        for (int index = 0; index < pattern.Length; index++)
        {
            byte actual = _bytes[offset + index];
            byte expected = pattern[index];
            if (actual == expected)
            {
                continue;
            }
            if (!ignoreCase || FoldAscii(actual) != FoldAscii(expected))
            {
                return false;
            }
        }
        return true;
    }

    private static byte FoldAscii(byte value)
    {
        return (value >= (byte)'A' && value <= (byte)'Z') ? (byte)(value + ('a' - 'A')) : value;
    }

    /// <summary>Parses "48 65 6C 6C 6F", "48656c6c6f", "0x48, 0x65" or "\x48\x65" into bytes.</summary>
    public static bool TryParseHexBytes(string text, out byte[] bytes, out string error)
    {
        bytes = null;
        error = null;
        if (text == null)
        {
            error = "Enter the bytes to find.";
            return false;
        }
        StringBuilder digits = new StringBuilder();
        for (int index = 0; index < text.Length; index++)
        {
            char ch = text[index];
            if (char.IsWhiteSpace(ch) || ch == ',' || ch == ';' || ch == ':' || ch == '-')
            {
                continue;
            }
            if ((ch == '0' && index + 1 < text.Length && (text[index + 1] == 'x' || text[index + 1] == 'X')) ||
                (ch == '\\' && index + 1 < text.Length && (text[index + 1] == 'x' || text[index + 1] == 'X')))
            {
                index++;
                continue;
            }
            if (ch == '$')
            {
                continue;
            }
            if (Uri.IsHexDigit(ch))
            {
                digits.Append(ch);
                continue;
            }
            error = "Only hexadecimal digits and spaces are allowed in hex bytes (for example: 48 65 6C).";
            return false;
        }
        if (digits.Length == 0)
        {
            error = "Enter the bytes to find, for example 48 65 6C.";
            return false;
        }
        if (digits.Length % 2 != 0)
        {
            error = "Hexadecimal bytes need two digits each (for example: 4A 0F).";
            return false;
        }
        bytes = new byte[digits.Length / 2];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = byte.Parse(digits.ToString(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return true;
    }

    /// <summary>Parses an offset typed in the Go to Offset dialog: decimal ("1234") or hexadecimal ("0x4D2").</summary>
    public static bool TryParseOffset(string text, int length, out int offset)
    {
        return TryParseOffset(text, length, -1, out offset);
    }

    /// <summary>
    /// Parses an offset typed in the Go to Offset dialog: decimal ("1234") or hexadecimal ("0x4D2"), or, when
    /// <paramref name="currentOffset"/> is not negative, a distance from it ("+0x10", "-16").
    /// </summary>
    public static bool TryParseOffset(string text, int length, int currentOffset, out int offset)
    {
        offset = 0;
        text = (text ?? "").Trim();
        if (text.Length == 0)
        {
            return false;
        }
        if (currentOffset >= 0 && (text[0] == '+' || text[0] == '-'))
        {
            bool backwards = text[0] == '-';
            if (!TryParseOffset(text.Substring(1), int.MaxValue, -1, out int distance))
            {
                return false;
            }
            long target = backwards ? (long)currentOffset - distance : (long)currentOffset + distance;
            offset = (int)Math.Max(0, Math.Min(target, Math.Max(0, length)));
            return true;
        }
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith("$", StringComparison.Ordinal);
        if (hex)
        {
            text = text.StartsWith("$", StringComparison.Ordinal) ? text.Substring(1) : text.Substring(2);
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long parsed))
            {
                return false;
            }
            if (parsed < 0)
            {
                return false;
            }
            offset = (int)Math.Min(parsed, Math.Max(0, length));
            return true;
        }
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long decimalOffset) || decimalOffset < 0)
        {
            return false;
        }
        offset = (int)Math.Min(decimalOffset, Math.Max(0, length));
        return true;
    }

    /// <summary>What the text of the clipboard is when it is pasted into the hex column.</summary>
    public enum ClipboardKind
    {
        /// <summary>Not hexadecimal: it is pasted as text (UTF-8).</summary>
        Text,

        /// <summary>Hexadecimal bytes that can hardly be anything else ("48 65 6C", "0x48, 0x65", "deadbeef", a hash).</summary>
        Hex,

        /// <summary>Hexadecimal digits that may as well be a word or a number: "dead", "face", "add1", "FF".</summary>
        Ambiguous
    }

    /// <summary>
    /// Tells hex bytes from text for the paste into the hex column. Digits that parse as hex are not always hex: a short run of them
    /// that holds a letter (dead, face, add1) is as likely a word. The bytes are certain when they carry a marker (0x, \x, $), come as
    /// pairs ("DE AD BE EF"), have no letter (a number), or are long (8 digits in one run, 16 digits in all).
    /// </summary>
    public static ClipboardKind ClassifyClipboard(string text)
    {
        if (!TryParseHexBytes(text, out byte[] bytes, out _))
        {
            return ClipboardKind.Text;
        }
        if (text.IndexOf("0x", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("\\x", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf('$') >= 0)
        {
            return ClipboardKind.Hex;
        }
        string[] tokens = text.Split(new[] { ' ', '\t', '\r', '\n', ',', ';', ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2 && Array.TrueForAll(tokens, (string token) => token.Length == 2))
        {
            return ClipboardKind.Hex;
        }
        bool hasLetter = false;
        foreach (char ch in text)
        {
            if ((ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F'))
            {
                hasLetter = true;
                break;
            }
        }
        int digits = bytes.Length * 2;
        if (!hasLetter || digits >= 16 || (tokens.Length == 1 && digits >= 8))
        {
            return ClipboardKind.Hex;
        }
        return ClipboardKind.Ambiguous;
    }

    /// <summary>Parses the clipboard: hex bytes when they look like them, otherwise the text as UTF-8.</summary>
    public static byte[] ParseClipboardBytes(string text, bool preferHex)
    {
        if (preferHex && TryParseHexBytes(text, out byte[] bytes, out _))
        {
            return bytes;
        }
        return Encoding.UTF8.GetBytes(text ?? "");
    }

    public static string FormatHex(byte[] bytes)
    {
        if (bytes == null)
        {
            return "";
        }
        StringBuilder text = new StringBuilder(bytes.Length * 3);
        for (int index = 0; index < bytes.Length; index++)
        {
            if (index > 0)
            {
                text.Append(' ');
            }
            text.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    public static string FormatAscii(byte[] bytes)
    {
        if (bytes == null)
        {
            return "";
        }
        StringBuilder text = new StringBuilder(bytes.Length);
        foreach (byte value in bytes)
        {
            text.Append(IsPrintable(value) ? (char)value : '.');
        }
        return text.ToString();
    }

    public static bool IsPrintable(byte value)
    {
        return value >= 0x20 && value <= 0x7E;
    }

    public static bool ByteArraysEqual(byte[] first, byte[] second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }
        if (first == null || second == null || first.Length != second.Length)
        {
            return false;
        }
        for (int index = 0; index < first.Length; index++)
        {
            if (first[index] != second[index])
            {
                return false;
            }
        }
        return true;
    }
}
