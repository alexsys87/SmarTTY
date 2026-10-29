using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SmarTTY.TextEditing;

/// <summary>
/// The bytes of a file being edited in the hex editor. It keeps the undo and redo history, remembers the version that
/// was last saved (so that undoing back to it clears the modified mark, as in the text editor) and compares the bytes
/// with the saved ones to show which of them changed. It has no UI and no file access: the editor control draws it and
/// <see cref="Controls.RemoteFileEditor"/> saves it.
/// </summary>
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

    private readonly Stack<Edit> _undo = new Stack<Edit>();

    private readonly Stack<Edit> _redo = new Stack<Edit>();

    // The undo step (null: the empty history) that the bytes matched when they were last saved.
    private object _savedStep;

    // The bytes as they were on the server: what the editor highlights as changed.
    private byte[] _savedBytes;

    private bool _lastModified;

    /// <summary>The bytes changed (an edit, an undo or a redo).</summary>
    public event EventHandler Changed;

    /// <summary>Whether the document differs from the saved version changed.</summary>
    public event EventHandler ModifiedChanged;

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
    public object CurrentVersion => CurrentStep;

    private object CurrentStep => (_undo.Count > 0) ? _undo.Peek() : null;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>The byte at <paramref name="offset"/> differs from the saved version (or lies beyond it).</summary>
    public bool IsByteChanged(int offset)
    {
        if (offset < 0 || offset >= _bytes.Count)
        {
            return false;
        }
        return offset >= _savedBytes.Length || _savedBytes[offset] != _bytes[offset];
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
        if (_undo.Count > 0 && _redo.Count == 0 && !ReferenceEquals(_undo.Peek(), _savedStep))
        {
            Edit last = _undo.Peek();
            // A step that was saved must keep its bytes: the saved mark would otherwise stay on changed content.
            if (last.Offset == offset && last.Inserted.Length == 1 && last.Removed.Length <= 1 && offset < _bytes.Count)
            {
                last.Inserted = new[] { value };
                _bytes[offset] = value;
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
        _redo.Clear();
        _undo.Push(edit);
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
        _undo.Push(edit);
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
        offset = 0;
        text = (text ?? "").Trim();
        if (text.Length == 0)
        {
            return false;
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
