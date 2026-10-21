using System.Collections.Generic;
using System.Text;

namespace SmarTTY.Terminal;

/// <summary>
/// Cursor arithmetic for a remote line editor that counts UTF-8 bytes instead of characters, such as the BusyBox
/// shell built without Unicode support (OpenWrt). Such an editor moves the cursor one column per byte: Backspace
/// after a Cyrillic letter (two bytes) sends one "\b" and deletes only half of the letter, so every Backspace erased
/// one letter from the screen but half a letter from the command line. In this mode the terminal
///  * converts the editor's backward/forward moves from bytes into screen cells (a cell can be entered "in the
///    middle", the position is then a cell plus a byte offset), and
///  * repeats Backspace, Delete, Left and Right once per byte of the character they cross.
/// Pure logic without WinForms, tested by tests/TerminalTextTests.
/// </summary>
public static class ByteColumns
{
    /// <summary>
    /// Shell command that tells whether the login shell is BusyBox with byte-oriented line editing.
    /// It prints "2" when "п" (two bytes) counts as two characters, "1" when BusyBox is Unicode-aware, an
    /// "invalid option" error when BusyBox has no "wc -m" (no Unicode support) and "shell:..." for other shells.
    /// </summary>
    public const string ProbeCommand = "exe=$(readlink /proc/$$/exe 2>/dev/null); case \"$exe\" in *busybox*) printf '\\320\\277' | busybox wc -m 2>&1;; *) echo \"shell:$exe\";; esac";

    /// <summary>Interprets the output of <see cref="ProbeCommand"/>: true only for BusyBox that counts bytes.</summary>
    public static bool ParseProbe(IEnumerable<string> lines)
    {
        if (lines == null)
        {
            return false;
        }
        foreach (string rawLine in lines)
        {
            string line = rawLine?.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }
            if (line == "2")
            {
                return true;
            }
            if (line == "1" || line.StartsWith("shell:"))
            {
                return false;
            }
            // "wc: invalid option -- 'm'" / "unrecognized option": a BusyBox built without Unicode support.
            if (line.Contains("option") && line.Contains("wc"))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Number of UTF-8 bytes the remote editor counts for the screen cell at <paramref name="column"/>.
    /// Cells past the end of the text are blanks (1 byte). U+FFFD stands for one invalid byte the editor printed on
    /// its own (half of a letter being deleted), so it counts as 1. A surrogate pair occupies two cells of 2 bytes each.
    /// </summary>
    public static int CellByteLength(string line, int column)
    {
        if (line == null || column < 0 || column >= line.Length)
        {
            return 1;
        }
        char ch = line[column];
        if (ch < 0x80 || ch == '�')
        {
            return 1;
        }
        if (ch < 0x800)
        {
            return 2;
        }
        if (char.IsSurrogate(ch))
        {
            return 2;
        }
        return 3;
    }

    /// <summary>
    /// Moves the position (<paramref name="column"/>, <paramref name="offset"/> bytes into that cell) back by
    /// <paramref name="bytes"/>. Returns the number of bytes that could not be moved because the start of the line
    /// was reached.
    /// </summary>
    public static int MoveBack(string line, ref int column, ref int offset, int bytes)
    {
        while (bytes > 0)
        {
            if (offset > 0)
            {
                int step = (bytes < offset) ? bytes : offset;
                offset -= step;
                bytes -= step;
                continue;
            }
            if (column <= 0)
            {
                break;
            }
            column--;
            offset = CellByteLength(line, column);
        }
        return bytes;
    }

    /// <summary>Moves the position forward by <paramref name="bytes"/>, up to <paramref name="maxColumn"/>.</summary>
    public static void MoveForward(string line, ref int column, ref int offset, int bytes, int maxColumn)
    {
        while (bytes > 0 && column < maxColumn)
        {
            int length = CellByteLength(line, column);
            if (offset + bytes < length)
            {
                offset += bytes;
                return;
            }
            bytes -= length - offset;
            column++;
            offset = 0;
        }
    }

    /// <summary>
    /// How many Backspace or Left presses the remote editor needs to step over the character before the cursor:
    /// the rest of a half-crossed character, or all bytes of the previous one.
    /// </summary>
    public static int BackwardKeyCount(string line, int column, int offset)
    {
        if (offset > 0)
        {
            return offset;
        }
        return (column > 0) ? CellByteLength(line, column - 1) : 1;
    }

    /// <summary>Length of the UTF-8 sequence that <paramref name="lead"/> starts, or 0 if it is not a lead byte.</summary>
    public static int SequenceLength(byte lead)
    {
        if (lead >= 0xC2 && lead <= 0xDF)
        {
            return 2;
        }
        if (lead >= 0xE0 && lead <= 0xEF)
        {
            return 3;
        }
        if (lead >= 0xF0 && lead <= 0xF4)
        {
            return 4;
        }
        return 0;
    }

    public static bool IsContinuation(byte value)
    {
        return (value & 0xC0) == 0x80;
    }

    /// <summary>
    /// The bytes a half-crossed cell still holds on the remote side: the first <paramref name="offset"/> bytes of
    /// the character shown in it. The remote editor printed the rest of the line after them, so the cell becomes a
    /// placeholder for these bytes (shown as U+FFFD) and the text continues in the next cell.
    /// </summary>
    public static byte[] CellPrefix(string line, int column, int offset)
    {
        if (line == null || column < 0 || column >= line.Length || offset <= 0)
        {
            return new byte[0];
        }
        int length = (char.IsHighSurrogate(line[column]) && column + 1 < line.Length) ? 2 : 1;
        byte[] bytes = Encoding.UTF8.GetBytes(line.Substring(column, length));
        int count = (offset < bytes.Length) ? offset : bytes.Length;
        byte[] prefix = new byte[count];
        System.Array.Copy(bytes, prefix, count);
        return prefix;
    }

    /// <summary>
    /// Adds <paramref name="value"/> to the bytes of a partially received character; returns the character once
    /// the sequence is complete and valid, otherwise null.
    /// </summary>
    public static string TryComplete(List<byte> bytes, byte value)
    {
        bytes.Add(value);
        if (bytes.Count != SequenceLength(bytes[0]))
        {
            return null;
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a partial character can take one more byte: it must be incomplete and the byte a continuation byte.
    /// </summary>
    public static bool CanContinue(List<byte> bytes, byte value)
    {
        return bytes != null && bytes.Count > 0 && bytes.Count < SequenceLength(bytes[0]) && IsContinuation(value);
    }

    /// <summary>How many Delete or Right presses the remote editor needs to step over the character under the cursor.</summary>
    public static int ForwardKeyCount(string line, int column, int offset)
    {
        int count = CellByteLength(line, column) - offset;
        return (count > 0) ? count : 1;
    }
}
