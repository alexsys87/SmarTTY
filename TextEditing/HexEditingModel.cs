using System;
using System.Globalization;
using System.Text;

namespace SmarTTY.TextEditing;

/// <summary>The column of the hex editor that the caret is in (and typing goes to).</summary>
public enum HexEditArea
{
    Hex,
    Ascii
}

/// <summary>What the view does after the model changed something, besides redrawing and telling its own listeners (it always does both).</summary>
[Flags]
public enum HexChangeEffects
{
    None = 0,

    /// <summary>The caret is drawn at once (the blink starts from "visible").</summary>
    ShowCaret = 1,

    /// <summary>The blink timer starts again.</summary>
    RestartCaretBlink = 2,

    /// <summary>The rows scroll until the caret is in view.</summary>
    EnsureCaretVisible = 4
}

/// <summary>Raised by <see cref="HexEditingModel"/> after an operation changed the document, the caret, the selection or the mode.</summary>
public delegate void HexChangedHandler(HexChangeEffects effects);

/// <summary>
/// The editing of the bytes of a <see cref="HexDocument"/>: the caret, the selection, the nibble being typed, the column, the typing mode, and what keys
/// and commands do to the bytes. It is what <c>HexEditorControl</c> did in its fields and its private methods, moved here as it was; the view keeps the
/// painting, the scrolling, the mouse, the clipboard, the dialogs, the timers and the layout, and reacts to <see cref="Changed"/> where the old code
/// called its <c>AfterContentChange</c> and <c>AfterCaretChange</c>.
/// </summary>
public sealed class HexEditingModel
{
    /// <summary>Returned by <see cref="Search"/> when there was nothing to look for (no pattern).</summary>
    public const int SearchSkipped = -2;

    /// <summary>Returned by <see cref="Search"/> when the pattern is not in the document.</summary>
    public const int NotFound = -1;

    private HexDocument _document;

    public HexEditingModel(HexDocument document)
    {
        _document = document;
        Anchor = -1;
        Overwrite = true;
        BytesPerRow = 16;
        LastWrap = true;
        LastSearchText = "";
    }

    /// <summary>Raised after an operation changed the bytes, the caret, the selection or the mode (not for a change of <see cref="ReadOnly"/>).</summary>
    public event HexChangedHandler Changed;

    /// <summary>The bytes being edited (the view sets it and does what a new document needs: the caret and the selection are its business).</summary>
    public HexDocument Document
    {
        get
        {
            return _document;
        }
        set
        {
            _document = value;
        }
    }

    public int CaretOffset { get; set; }

    /// <summary>The other end of the selection (the caret is at one end); -1 when there is none.</summary>
    public int Anchor { get; set; }

    /// <summary>0 when the first digit of the byte at the caret is next, 1 when the second is.</summary>
    public int Nibble { get; set; }

    public HexEditArea Area { get; set; }

    /// <summary>Typing replaces the byte under the caret instead of inserting a new one.</summary>
    public bool Overwrite { get; set; }

    /// <summary>The text cannot be changed (a read-only file, a preview of a huge file).</summary>
    public bool ReadOnly { get; set; }

    public bool BigEndian { get; set; }

    /// <summary>The number of bytes in a row: the vertical movement counts in rows.</summary>
    public int BytesPerRow { get; set; }

    /// <summary>The first digit of a byte was typed in a step of the undo history that the second one joins.</summary>
    public bool SecondNibblePending { get; set; }

    /// <summary>The first half of a character outside the basic plane typed in the ASCII column, until the second half comes.</summary>
    public char PendingHighSurrogate { get; set; }

    /// <summary>The pattern of the last search (null: none yet), for repeating it.</summary>
    public byte[] LastPattern { get; private set; }

    public bool LastIgnoreCase { get; private set; }

    public bool LastWrap { get; private set; }

    public string LastSearchText { get; private set; }

    public bool HasSelection => Anchor >= 0 && Anchor != CaretOffset;

    public int SelectionStart => HasSelection ? Math.Min(Anchor, CaretOffset) : CaretOffset;

    public int SelectionEnd => HasSelection ? Math.Max(Anchor, CaretOffset) : CaretOffset;

    public int SelectionLength => HasSelection ? Math.Abs(CaretOffset - Anchor) : 0;

    public bool CanUndo => !ReadOnly && _document.CanUndo;

    public bool CanRedo => !ReadOnly && _document.CanRedo;

    /// <summary>"1,024 bytes (0x400)".</summary>
    public static string FormatByteCount(int count)
    {
        return count.ToString("N0", CultureInfo.InvariantCulture) + (count == 1 ? " byte" : " bytes")
            + " (0x" + count.ToString("X", CultureInfo.InvariantCulture) + ")";
    }

    public void SelectAll()
    {
        Anchor = 0;
        CaretOffset = _document.Length;
        Nibble = 0;
        Area = HexEditArea.Hex;
        Raise(HexChangeEffects.EnsureCaretVisible);
    }

    /// <summary>
    /// The bytes the checksums are computed for: the selection, or the whole file without one.
    /// <paramref name="description"/> says which.
    /// </summary>
    public byte[] GetChecksumBytes(out string description)
    {
        if (HasSelection)
        {
            description = "Selection 0x" + SelectionStart.ToString("X8", CultureInfo.InvariantCulture) + " - 0x"
                + (SelectionEnd - 1).ToString("X8", CultureInfo.InvariantCulture) + ", " + FormatByteCount(SelectionLength);
            return _document.GetBytes(SelectionStart, SelectionLength);
        }
        description = "The whole file, " + FormatByteCount(_document.Length);
        return _document.ToArray();
    }

    /// <summary>Repeats <paramref name="pattern"/> over the selected bytes (one undo step); false without a selection.</summary>
    public bool FillSelection(byte[] pattern)
    {
        if (ReadOnly || !HasSelection || pattern == null || pattern.Length == 0)
        {
            return false;
        }
        int start = SelectionStart;
        int count = SelectionLength;
        _document.Replace(start, count, HexValueFormats.RepeatPattern(pattern, count));
        Anchor = start;
        CaretOffset = start + count;
        Nibble = 0;
        AfterContentChange();
        return true;
    }

    /// <summary>
    /// Inserts <paramref name="count"/> bytes that repeat <paramref name="pattern"/> at the caret (before the selection;
    /// also in overwrite mode) and selects them.
    /// </summary>
    public bool InsertPattern(int count, byte[] pattern)
    {
        if (ReadOnly || count <= 0 || pattern == null || pattern.Length == 0)
        {
            return false;
        }
        int start = HasSelection ? SelectionStart : CaretOffset;
        _document.Insert(start, HexValueFormats.RepeatPattern(pattern, count));
        Anchor = start;
        CaretOffset = start + count;
        Nibble = 0;
        AfterContentChange();
        return true;
    }

    /// <summary>
    /// Reverses the byte order of each <paramref name="size"/>-byte word in the selection (one undo step). The selection
    /// must hold whole words.
    /// </summary>
    public bool SwapSelectionByteOrder(int size, out string error)
    {
        error = null;
        if (ReadOnly)
        {
            error = "The file cannot be changed.";
            return false;
        }
        if (!HasSelection || SelectionLength < size || SelectionLength % size != 0)
        {
            error = "Select whole " + (size * 8).ToString(CultureInfo.InvariantCulture) + "-bit words: a multiple of "
                + size.ToString(CultureInfo.InvariantCulture) + " bytes.";
            return false;
        }
        int start = SelectionStart;
        int count = SelectionLength;
        bool caretAtEnd = CaretOffset > Anchor;
        _document.Replace(start, count, HexValueFormats.SwapByteOrder(_document.GetBytes(start, count), size));
        Anchor = caretAtEnd ? start : start + count;
        CaretOffset = caretAtEnd ? start + count : start;
        Nibble = 0;
        AfterContentChange();
        return true;
    }

    /// <summary>
    /// Writes <paramref name="text"/>, read as <paramref name="type"/> in the current byte order, over the bytes at the
    /// caret (one undo step). The bytes past the end of the file are added.
    /// </summary>
    public bool WriteValue(HexValueType type, string text, out string error)
    {
        if (ReadOnly)
        {
            error = "The file cannot be changed.";
            return false;
        }
        if (!HexValueFormats.TryParse(type, text, BigEndian, out byte[] bytes, out error))
        {
            return false;
        }
        int offset = HasSelection ? SelectionStart : CaretOffset;
        int replaced = Math.Min(bytes.Length, _document.Length - offset);
        _document.Replace(offset, replaced, bytes);
        Anchor = -1;
        CaretOffset = offset;
        Nibble = 0;
        AfterContentChange();
        return true;
    }

    /// <summary>Up to <paramref name="count"/> bytes from the caret (from the start of the selection).</summary>
    public byte[] GetBytesAtCaret(int count)
    {
        int offset = HasSelection ? SelectionStart : CaretOffset;
        int available = Math.Max(0, Math.Min(count, _document.Length - offset));
        return _document.GetBytes(offset, available);
    }

    /// <summary>Marks the selected bytes (the byte under the caret without a selection) with a colour (0xAARRGGBB) and a label.</summary>
    public bool HighlightSelection(int argb, string label)
    {
        int start = HasSelection ? SelectionStart : CaretOffset;
        int count = HasSelection ? SelectionLength : 1;
        if (start + count > _document.Length || count <= 0)
        {
            return false;
        }
        _document.AddHighlight(start, count, argb, (label ?? "").Trim());
        return true;
    }

    /// <summary>Removes the colour from the selected bytes, or the whole block under the caret without a selection.</summary>
    public bool RemoveHighlight()
    {
        return HasSelection ? _document.RemoveHighlights(SelectionStart, SelectionLength) : _document.RemoveHighlights(CaretOffset, 0);
    }

    public void ClearHighlights()
    {
        _document.ClearHighlights();
    }

    /// <summary>
    /// Selects the next coloured block after the caret (the previous one when <paramref name="forward"/> is false); false when there is none
    /// (the view says so with a beep).
    /// </summary>
    public bool GoToHighlight(bool forward)
    {
        int from = HasSelection ? SelectionStart : CaretOffset;
        HexHighlight highlight = _document.FindHighlight(from, forward);
        if (highlight == null)
        {
            return false;
        }
        Anchor = highlight.Start;
        CaretOffset = highlight.End;
        Nibble = 0;
        Area = HexEditArea.Hex;
        AfterCaretChange();
        return true;
    }

    public void Undo()
    {
        if (ReadOnly)
        {
            return;
        }
        _document.Undo();
        Nibble = 0;
        AfterContentChange();
    }

    public void Redo()
    {
        if (ReadOnly)
        {
            return;
        }
        _document.Redo();
        Nibble = 0;
        AfterContentChange();
    }

    public void ToggleOverwriteMode()
    {
        if (ReadOnly)
        {
            return;
        }
        Overwrite = !Overwrite;
        Raise(HexChangeEffects.None);
    }

    /// <summary>Moves the caret to <paramref name="offset"/> (clamped to the document) and selects nothing.</summary>
    public void GoToOffset(int offset)
    {
        Anchor = -1;
        CaretOffset = Math.Max(0, Math.Min(offset, _document.Length));
        Nibble = 0;
        Area = HexEditArea.Hex;
        Raise(HexChangeEffects.EnsureCaretVisible);
    }

    /// <summary>
    /// Finds <paramref name="pattern"/> and selects it. The search starts after the selection (before it when
    /// <paramref name="forward"/> is false) and, with <paramref name="wrap"/>, continues from the other end. Returns the offset of the match,
    /// <see cref="NotFound"/>, or <see cref="SearchSkipped"/> when there is no pattern.
    /// </summary>
    public int Search(byte[] pattern, bool forward, bool ignoreCase, bool wrap)
    {
        if (pattern == null || pattern.Length == 0)
        {
            return SearchSkipped;
        }
        LastPattern = (byte[])pattern.Clone();
        LastIgnoreCase = ignoreCase;
        LastWrap = wrap;
        LastSearchText = HexDocument.FormatHex(pattern);
        int start = HasSelection
            ? (forward ? SelectionEnd : SelectionStart - 1)
            : (forward ? CaretOffset : CaretOffset - 1);
        int offset = _document.IndexOf(pattern, start, forward, ignoreCase, wrap);
        if (offset < 0)
        {
            return NotFound;
        }
        Anchor = offset;
        CaretOffset = offset + pattern.Length;
        Nibble = 0;
        Area = HexEditArea.Hex;
        Raise(HexChangeEffects.EnsureCaretVisible);
        return offset;
    }

    /// <summary>The selected bytes, or the byte under the caret without a selection (none at the end).</summary>
    public byte[] GetSelectedBytes()
    {
        if (HasSelection)
        {
            return _document.GetBytes(SelectionStart, SelectionLength);
        }
        if (CaretOffset < _document.Length)
        {
            return new[] { _document[CaretOffset] };
        }
        return Array.Empty<byte>();
    }

    /// <summary>Puts <paramref name="bytes"/> in at the caret (over the selection, or over the bytes under it in the overwrite mode).</summary>
    public void InsertBytes(byte[] bytes)
    {
        if (ReadOnly || bytes == null || bytes.Length == 0)
        {
            return;
        }
        if (HasSelection)
        {
            int start = SelectionStart;
            int count = SelectionLength;
            Anchor = -1;
            CaretOffset = start;
            _document.Replace(start, count, bytes);
        }
        else if (Overwrite)
        {
            int count = Math.Min(bytes.Length, Math.Max(0, _document.Length - CaretOffset));
            _document.Replace(CaretOffset, count, bytes);
        }
        else
        {
            _document.Insert(CaretOffset, bytes);
        }
        CaretOffset = Math.Min(_document.Length, CaretOffset + bytes.Length);
        Nibble = 0;
        Anchor = -1;
        AfterContentChange();
    }

    /// <summary>Removes the selection, or the byte under the caret (the Delete key).</summary>
    public void DeleteCurrentSelectionOrByte()
    {
        if (ReadOnly)
        {
            return;
        }
        if (HasSelection)
        {
            int start = SelectionStart;
            int count = SelectionLength;
            Anchor = -1;
            CaretOffset = start;
            _document.Remove(start, count);
        }
        else if (CaretOffset < _document.Length)
        {
            _document.Remove(CaretOffset, 1);
        }
        else
        {
            return;
        }
        Nibble = 0;
        AfterContentChange();
    }

    /// <summary>The Backspace key: removes the selection, or the byte before the caret.</summary>
    public void Backspace()
    {
        if (ReadOnly)
        {
            return;
        }
        if (HasSelection)
        {
            DeleteCurrentSelectionOrByte();
        }
        else if (CaretOffset > 0)
        {
            int offset = CaretOffset - 1;
            _document.Remove(offset, 1);
            CaretOffset = offset;
            Nibble = 0;
            AfterContentChange();
        }
    }

    private void AfterContentChange()
    {
        SecondNibblePending = false;
        CaretOffset = Math.Max(0, Math.Min(CaretOffset, _document.Length));
        if (Anchor > _document.Length)
        {
            Anchor = _document.Length;
        }
        if (Anchor == CaretOffset)
        {
            Anchor = -1;
        }
        Raise(HexChangeEffects.ShowCaret | HexChangeEffects.EnsureCaretVisible);
    }

    /// <summary>
    /// Makes room for a typed byte at a selection. Typing over a selection in the insert mode replaces it. In the overwrite mode
    /// the length of the file must not change: the selection is only dropped, and the typed byte overwrites the first byte of it
    /// (deleting the selection would shorten the file and shift the bytes after it, which is not what overwriting means).
    /// </summary>
    private void ReplaceSelectionForTyping()
    {
        if (!HasSelection)
        {
            return;
        }
        if (Overwrite)
        {
            CaretOffset = SelectionStart;
            Anchor = -1;
            Nibble = 0;
            SecondNibblePending = false;
            return;
        }
        DeleteCurrentSelectionOrByte();
    }

    private void EnterHexDigit(int digit)
    {
        if (ReadOnly)
        {
            return;
        }
        ReplaceSelectionForTyping();
        HexChangeEffects effects = HexChangeEffects.ShowCaret;
        if (Nibble == 0)
        {
            byte value = (byte)(digit << 4);
            if (Overwrite && CaretOffset < _document.Length)
            {
                byte existing = _document[CaretOffset];
                _document.ReplaceByte(CaretOffset, (byte)((existing & 0x0F) | value));
            }
            else
            {
                _document.Insert(CaretOffset, new[] { value });
            }
            Nibble = 1;
            SecondNibblePending = true;
        }
        else
        {
            bool joinStep = SecondNibblePending;
            SecondNibblePending = false;
            if (CaretOffset < _document.Length)
            {
                byte existing = _document[CaretOffset];
                byte value = (byte)((existing & 0xF0) | digit);
                if (joinStep)
                {
                    _document.ReplaceByteIntoLastStep(CaretOffset, value);
                }
                else
                {
                    _document.ReplaceByte(CaretOffset, value);
                }
            }
            else
            {
                _document.Insert(CaretOffset, new[] { (byte)digit });
            }
            Nibble = 0;
            CaretOffset = Math.Min(_document.Length, CaretOffset + 1);
            effects |= HexChangeEffects.EnsureCaretVisible;
        }
        Raise(effects);
    }

    private void EnterAscii(byte[] bytes)
    {
        if (ReadOnly || bytes == null || bytes.Length == 0)
        {
            return;
        }
        SecondNibblePending = false;
        ReplaceSelectionForTyping();
        if (Overwrite && CaretOffset < _document.Length)
        {
            _document.Replace(CaretOffset, Math.Min(bytes.Length, _document.Length - CaretOffset), bytes);
        }
        else
        {
            _document.Insert(CaretOffset, bytes);
        }
        CaretOffset = Math.Min(_document.Length, CaretOffset + bytes.Length);
        Nibble = 0;
        Raise(HexChangeEffects.ShowCaret | HexChangeEffects.EnsureCaretVisible);
    }

    private static int HexDigitValue(char ch)
    {
        if (ch >= '0' && ch <= '9')
        {
            return ch - '0';
        }
        if (ch >= 'a' && ch <= 'f')
        {
            return ch - 'a' + 10;
        }
        if (ch >= 'A' && ch <= 'F')
        {
            return ch - 'A' + 10;
        }
        return -1;
    }

    /// <summary>
    /// A character typed in the editor: a hex digit in the hex column, any character in the ASCII column. Returns true when the key was used.
    /// </summary>
    public bool TypeCharacter(char typed)
    {
        if (ReadOnly || char.IsControl(typed))
        {
            return false;
        }
        if (Area == HexEditArea.Hex)
        {
            int digit = HexDigitValue(typed);
            if (digit >= 0)
            {
                EnterHexDigit(digit);
                return true;
            }
            return false;
        }
        // What is typed in the ASCII column is stored as UTF-8, like what is pasted there: "é" is two bytes, a Cyrillic letter is two,
        // and a character outside the basic plane comes as two events (a surrogate pair), which are joined here. (It used to be one
        // Latin-1 byte up to U+00FF and nothing at all above it.)
        if (char.IsHighSurrogate(typed))
        {
            PendingHighSurrogate = typed;
            return true;
        }
        string text;
        if (char.IsLowSurrogate(typed))
        {
            if (PendingHighSurrogate == '\0')
            {
                return true;
            }
            text = new string(new[] { PendingHighSurrogate, typed });
        }
        else
        {
            text = typed.ToString();
        }
        PendingHighSurrogate = '\0';
        EnterAscii(Encoding.UTF8.GetBytes(text));
        return true;
    }

    /// <summary>The Left and Right keys: by a nibble in the hex column, by a byte in the ASCII column.</summary>
    public void MoveHorizontally(bool left, bool extendSelection)
    {
        if (!extendSelection)
        {
            Anchor = -1;
        }
        else if (Anchor < 0)
        {
            Anchor = CaretOffset;
        }
        if (Area == HexEditArea.Ascii)
        {
            CaretOffset = Math.Max(0, Math.Min(_document.Length, CaretOffset + (left ? -1 : 1)));
            Nibble = 0;
        }
        else if (left)
        {
            if (Nibble == 1)
            {
                Nibble = 0;
            }
            else if (CaretOffset > 0)
            {
                CaretOffset--;
                Nibble = 1;
            }
        }
        else if (Nibble == 0)
        {
            if (CaretOffset < _document.Length)
            {
                Nibble = 1;
            }
        }
        else
        {
            if (CaretOffset < _document.Length)
            {
                CaretOffset++;
            }
            Nibble = 0;
        }
        AfterCaretChange();
    }

    /// <summary>Moves the caret by <paramref name="rows"/> rows (the same column).</summary>
    public void MoveVertically(int rows, bool extendSelection)
    {
        int row = CaretOffset / BytesPerRow + rows;
        row = Math.Max(0, row);
        int column = CaretOffset % BytesPerRow;
        int offset = row * BytesPerRow + column;
        MoveTo(offset, extendSelection, Nibble);
    }

    public void MoveTo(int offset, bool extendSelection, int nibble)
    {
        if (!extendSelection)
        {
            Anchor = -1;
        }
        else if (Anchor < 0)
        {
            Anchor = CaretOffset;
        }
        CaretOffset = Math.Max(0, Math.Min(offset, _document.Length));
        Nibble = (nibble == 1) ? 1 : 0;
        Area = HexEditArea.Hex;
        AfterCaretChange();
    }

    /// <summary>The caret or the selection was changed from outside (the mouse): the same follow-up as after a movement by key.</summary>
    public void AfterCaretChange()
    {
        SecondNibblePending = false;
        if (Anchor == CaretOffset)
        {
            Anchor = -1;
        }
        Raise(HexChangeEffects.ShowCaret | HexChangeEffects.RestartCaretBlink | HexChangeEffects.EnsureCaretVisible);
    }

    private void Raise(HexChangeEffects effects)
    {
        Changed?.Invoke(effects);
    }
}
