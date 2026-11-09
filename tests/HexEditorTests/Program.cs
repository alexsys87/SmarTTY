using System;
using System.Collections.Generic;
using SmarTTY.TextEditing;

// Regression harness for the bytes of the hex editor (TextEditing/HexDocument.cs): editing, undo and redo, the saved
// version, search and the parsing of clipboard bytes and offsets.
// Run with: dotnet run --project tests/HexEditorTests
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

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private static void EqualBytes(byte[] actual, byte[] expected, string name)
    {
        Check(HexDocument.ByteArraysEqual(actual, expected), name + " (expected " + HexDocument.FormatHex(expected) + ", got " + HexDocument.FormatHex(actual) + ")");
    }

    private static int Main()
    {
        byte[] hello = { (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };

        // A new document is the saved version: no edits, nothing to undo or redo.
        HexDocument document = new HexDocument(hello);
        Equal(document.Length, 5, "the document has the bytes");
        EqualBytes(document.ToArray(), hello, "the bytes are kept");
        Check(!document.IsModified && !document.CanUndo && !document.CanRedo, "a new document is clean");
        Check(!document.IsByteChanged(0), "an unchanged byte is not marked");
        Equal(document.CurrentVersion, null, "a clean history has no step");

        // Replacing a byte is one undo step and makes the document modified.
        document.ReplaceByte(1, (byte)'E');
        EqualBytes(document.ToArray(), new byte[] { (byte)'H', (byte)'E', (byte)'l', (byte)'l', (byte)'o' }, "a byte is replaced");
        Check(document.IsModified && document.CanUndo, "a replaced byte makes the document modified");
        Check(document.IsByteChanged(1) && !document.IsByteChanged(0), "only the changed byte is marked");

        // A byte that grew the document beyond the saved version is changed too.
        HexDocument grown = new HexDocument(hello);
        grown.Insert(5, new byte[] { (byte)'!' });
        Check(grown.IsByteChanged(5), "a byte beyond the saved version counts as changed");

        // The saved marker follows the undo history: undo, then redo, then save, then undo again.
        document.Undo();
        Check(!document.IsModified && !document.CanUndo && document.CanRedo, "undo returns to the saved version");
        document.Redo();
        Check(document.IsModified, "redo makes it modified again");
        document.MarkSaved();
        Check(!document.IsModified && !document.IsByteChanged(1), "saving clears the modified mark");
        document.Undo();
        Check(document.IsModified, "undo after save is modified");
        document.Redo();
        Check(!document.IsModified, "redo back to the saved version is clean");

        // Insert and remove, with a selection-sized replacement.
        document.Replace(1, 2, new byte[] { (byte)'e', (byte)'y' });
        EqualBytes(document.ToArray(), new byte[] { (byte)'H', (byte)'e', (byte)'y', (byte)'l', (byte)'o' }, "a range is replaced");
        document.Insert(5, new byte[] { (byte)'!' });
        EqualBytes(document.ToArray(), new byte[] { (byte)'H', (byte)'e', (byte)'y', (byte)'l', (byte)'o', (byte)'!' }, "bytes are inserted");
        document.Remove(1, 2);
        EqualBytes(document.ToArray(), new byte[] { (byte)'H', (byte)'l', (byte)'o', (byte)'!' }, "bytes are removed");
        document.Undo();
        EqualBytes(document.ToArray(), new byte[] { (byte)'H', (byte)'e', (byte)'y', (byte)'l', (byte)'o', (byte)'!' }, "remove is undone");
        document.Undo();
        EqualBytes(document.ToArray(), new byte[] { (byte)'H', (byte)'e', (byte)'y', (byte)'l', (byte)'o' }, "insert is undone");
        document.Undo();
        EqualBytes(document.ToArray(), new byte[] { (byte)'H', (byte)'E', (byte)'l', (byte)'l', (byte)'o' }, "replace is undone");

        // A new edit drops the redo history.
        document.Redo();
        document.ReplaceByte(2, (byte)'L');
        Check(!document.CanRedo, "a new edit drops the redo history");

        // The second digit of a typed byte joins the undo step of the first one: one undo takes the whole byte back.
        HexDocument typing = new HexDocument(new byte[] { 1 });
        typing.Insert(1, new byte[] { 0xA0 });
        typing.ReplaceByteIntoLastStep(1, 0xAB);
        EqualBytes(typing.ToArray(), new byte[] { 1, 0xAB }, "the second digit completes the byte");
        typing.Undo();
        EqualBytes(typing.ToArray(), new byte[] { 1 }, "one undo takes a typed byte back");
        Check(!typing.CanUndo && !typing.IsModified, "the typed byte was a single step");
        typing.Redo();
        EqualBytes(typing.ToArray(), new byte[] { 1, 0xAB }, "redo brings the whole typed byte back");
        HexDocument overtyped = new HexDocument(new byte[] { 1, 2 });
        overtyped.ReplaceByte(1, 0x30);
        overtyped.ReplaceByteIntoLastStep(1, 0x35);
        overtyped.Undo();
        EqualBytes(overtyped.ToArray(), new byte[] { 1, 2 }, "an overtyped byte is restored by one undo");
        HexDocument savedHalf = new HexDocument(new byte[] { 1 });
        savedHalf.Insert(1, new byte[] { 0xA0 });
        savedHalf.MarkSaved();
        savedHalf.ReplaceByteIntoLastStep(1, 0xAB);
        Check(savedHalf.IsModified, "a saved step is not changed: the document is modified");
        savedHalf.Undo();
        EqualBytes(savedHalf.ToArray(), new byte[] { 1, 0xA0 }, "undo returns to the saved bytes");
        Check(!savedHalf.IsModified, "and the document is clean again");

        // A background save records the bytes written for the version it took, so edits made meanwhile stay modified.
        HexDocument background = new HexDocument(hello);
        object version = background.CurrentVersion;
        background.ReplaceByte(0, (byte)'h');
        background.MarkSaved(version, hello);
        Check(background.IsModified, "edits made during a save keep the document modified");
        background.Undo();
        Check(!background.IsModified, "undo back to the bytes that were written is clean");
        EqualBytes(background.ToArray(), hello, "the saved bytes are remembered");

        // The second digit typed while a save is on its way is a step of its own: the save describes the first digit only.
        HexDocument typedDuringSave = new HexDocument(new byte[] { 1 });
        typedDuringSave.Insert(1, new byte[] { 0xA0 });
        object versionBeingSaved = typedDuringSave.CurrentVersion;
        byte[] bytesBeingSaved = typedDuringSave.ToArray();
        typedDuringSave.ReplaceByteIntoLastStep(1, 0xAB);
        typedDuringSave.MarkSaved(versionBeingSaved, bytesBeingSaved);
        Check(typedDuringSave.IsModified, "a byte completed while a save runs is not taken for saved");
        EqualBytes(typedDuringSave.ToArray(), new byte[] { 1, 0xAB }, "...and keeps the digit that was typed");
        typedDuringSave.Undo();
        EqualBytes(typedDuringSave.ToArray(), new byte[] { 1, 0xA0 }, "...one undo goes back to what was saved");
        Check(!typedDuringSave.IsModified, "...which is clean");

        // A restored draft is modified without a step that would make it clean.
        HexDocument restored = new HexDocument(new byte[] { 1, 2, 3 }, new byte[] { 1, 2 });
        Check(restored.IsModified && restored.IsByteChanged(2), "a restored draft is modified");
        restored.MarkModified();
        Check(restored.IsModified, "a restored draft stays modified");

        // Search forward and backward, with and without wrapping, and ASCII-only case folding.
        byte[] haystack = { 1, 2, 3, 4, 1, 2, (byte)'A', (byte)'b', 1, 2 };
        HexDocument search = new HexDocument(haystack);
        Equal(search.IndexOf(new byte[] { 1, 2 }, 0, forward: true), 0, "search finds a pattern at the start");
        Equal(search.IndexOf(new byte[] { 1, 2 }, 1, forward: true), 4, "search starts at the given offset");
        Equal(search.IndexOf(new byte[] { 1, 2 }, 5, forward: true), 8, "search finds a later pattern");
        Equal(search.IndexOf(new byte[] { 1, 2 }, 9, forward: true), 0, "search wraps around the end");
        Equal(search.IndexOf(new byte[] { 1, 2 }, 9, forward: true, wrapAround: false), -1, "search can stop at the end");
        Equal(search.IndexOf(new byte[] { 1, 2 }, 7, forward: false), 4, "backward search finds the previous pattern");
        Equal(search.IndexOf(new byte[] { 1, 2 }, 8, forward: false), 8, "backward search also finds a pattern at the start offset");
        Equal(search.IndexOf(new byte[] { 1, 2 }, -1, forward: false), 8, "backward search wraps around the start");
        Equal(search.IndexOf(new byte[] { 1, 2 }, -1, forward: false, wrapAround: false), -1, "backward search can stop at the start");
        Equal(search.IndexOf(new byte[] { (byte)'a', (byte)'B' }, 0, forward: true, ignoreCase: true), 6, "ASCII letters are folded");
        Equal(search.IndexOf(new byte[] { (byte)'a', (byte)'B' }, 0, forward: true, ignoreCase: false), -1, "case matters when asked");
        Equal(search.IndexOf(new byte[] { 9, 9 }, 0, forward: true), -1, "a missing pattern is not found");
        Equal(search.IndexOf(Array.Empty<byte>(), 0, forward: true), -1, "an empty pattern is not searched");
        Equal(search.IndexOf(new byte[] { 1, 2, 3, 4, 5 }, 0, forward: true), -1, "a pattern longer than the document is not found");

        // The clipboard and the dialogs.
        Check(HexDocument.TryParseHexBytes("48 65 6C 6C 6F", out byte[] parsed, out _), "spaced hex is parsed");
        EqualBytes(parsed, hello, "spaced hex gives the bytes");
        Check(HexDocument.TryParseHexBytes("0x48, 0x65, 0x6c", out parsed, out _) && parsed.Length == 3 && parsed[0] == 0x48, "0x hex is parsed");
        Check(HexDocument.TryParseHexBytes("\\x48\\x65", out parsed, out _) && parsed.Length == 2 && parsed[1] == 0x65, "\\x hex is parsed");
        Check(HexDocument.TryParseHexBytes("DEAD-BEEF", out parsed, out _) && parsed.Length == 4 && parsed[3] == 0xEF, "dashes separate bytes");
        Check(!HexDocument.TryParseHexBytes("4", out _, out string parseError) && parseError != null, "an odd number of digits is an error");
        Check(!HexDocument.TryParseHexBytes("hello", out _, out _), "letters beyond F are not hex");
        Check(!HexDocument.TryParseHexBytes("", out _, out _), "an empty query is an error");
        EqualBytes(HexDocument.ParseClipboardBytes("48 65", preferHex: true), new byte[] { 0x48, 0x65 }, "the clipboard is read as hex");
        EqualBytes(HexDocument.ParseClipboardBytes("Hello", preferHex: true), new byte[] { (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o' }, "text that is not hex is read as text");

        Check(HexDocument.TryParseOffset("1024", 2048, out int offset) && offset == 1024, "a decimal offset is parsed");
        Check(HexDocument.TryParseOffset("0x400", 2048, out offset) && offset == 1024, "a hexadecimal offset is parsed");
        Check(HexDocument.TryParseOffset("$10", 2048, out offset) && offset == 16, "a dollar offset is parsed");
        Check(HexDocument.TryParseOffset("x", 2048, out _) == false, "letters that are not hex are not an offset");
        Check(HexDocument.TryParseOffset("", 2048, out _) == false, "an empty offset is not an offset");
        Check(HexDocument.TryParseOffset("-1", 2048, out _) == false, "a negative offset is not an offset");
        Check(HexDocument.TryParseOffset("9999", 10, out offset) && offset == 10, "an offset beyond the file is clamped");

        Equal(HexDocument.FormatHex(new byte[] { 0, 0x0F, 0xA5 }), "00 0F A5", "bytes are formatted as hex");
        Equal(HexDocument.FormatAscii(new byte[] { (byte)'A', 0, (byte)'~', 0x7F }), "A.~.", "the ASCII column shows dots for control bytes");
        Check(HexDocument.IsPrintable((byte)' ') && HexDocument.IsPrintable((byte)'~') && !HexDocument.IsPrintable(0) && !HexDocument.IsPrintable(0x80), "printable ASCII is 0x20 to 0x7E");
        Check(HexDocument.ByteArraysEqual(null, null) && HexDocument.ByteArraysEqual(hello, (byte[])hello.Clone()) && !HexDocument.ByteArraysEqual(hello, new byte[] { 1 }), "byte arrays are compared by content");

        CheckRelativeOffsets();
        CheckHighlights();
        CheckValues();
        CheckCopyFormats();
        CheckChecksums();

        Console.WriteLine();
        Console.WriteLine("All " + _passed + " checks passed.");
        return 0;
    }

    private static void CheckRelativeOffsets()
    {
        Check(HexDocument.TryParseOffset("+0x10", 100, 20, out int offset) && offset == 36, "+0x10 moves forward from the caret");
        Check(HexDocument.TryParseOffset("-8", 100, 20, out offset) && offset == 12, "-8 moves back from the caret");
        Check(HexDocument.TryParseOffset("-50", 100, 20, out offset) && offset == 0, "moving back before the start stops at 0");
        Check(HexDocument.TryParseOffset("+500", 100, 20, out offset) && offset == 100, "moving past the end stops at the end");
        Check(!HexDocument.TryParseOffset("+", 100, 20, out _), "a sign alone is not an offset");
        Check(!HexDocument.TryParseOffset("-8", 100, out _), "without a caret a negative offset is still refused");
    }

    private static void CheckHighlights()
    {
        HexDocument document = new HexDocument(new byte[32]);
        int changes = 0;
        document.HighlightsChanged += (sender, e) => changes++;
        HexHighlight header = document.AddHighlight(0, 4, unchecked((int)0xFFFFD700), "magic");
        document.AddHighlight(8, 8, unchecked((int)0xFF00FF00), "payload");
        Equal(document.Highlights.Count, 2, "two blocks are coloured");
        Equal(changes, 2, "adding a block is reported");
        Check(document.GetHighlightAt(2) == header && document.GetHighlightAt(4) == null, "the block at an offset is found");
        Equal(document.GetHighlightAt(15)?.Label, "payload", "the label is kept");
        Equal(document.GetHighlightAt(16), null, "the byte after a block is not in it");

        // A new block covers the parts of the old ones it overlaps; a block in the middle splits the old one.
        document.AddHighlight(10, 2, unchecked((int)0xFFFF0000), "crc");
        Equal(document.Highlights.Count, 4, "a block in the middle splits the one it covers");
        Check(document.Highlights[1].Start == 8 && document.Highlights[1].Length == 2, "the head of the split block stays");
        Check(document.Highlights[2].Label == "crc" && document.Highlights[3].Start == 12 && document.Highlights[3].Length == 4, "the tail of the split block stays");

        // Navigation wraps around.
        Equal(document.FindHighlight(0, forward: true)?.Start, 8, "the next block after the caret");
        Equal(document.FindHighlight(12, forward: true)?.Start, 0, "after the last block the search continues at the start");
        Equal(document.FindHighlight(0, forward: false)?.Start, 12, "before the first block the search continues at the end");

        // Typing over bytes keeps the blocks where they are.
        document.ReplaceByte(1, 0xAA);
        Check(header.Start == 0 && header.Length == 4, "typing over a byte keeps the block");

        // Inserting before a block moves it, inside it makes it longer, right after it leaves it alone.
        document.Insert(0, new byte[] { 1, 2 });
        Check(header.Start == 2 && header.Length == 4, "bytes inserted before a block move it");
        document.Insert(3, new byte[] { 3 });
        Check(header.Start == 2 && header.Length == 5, "bytes inserted inside a block make it longer");
        document.Insert(7, new byte[] { 4 });
        Check(header.Start == 2 && header.Length == 5, "bytes inserted right after a block do not join it");

        // Undo moves the blocks back.
        document.Undo();
        document.Undo();
        document.Undo();
        Check(header.Start == 0 && header.Length == 4, "undo moves the block back");

        // Removing part of a block shortens it; removing all of it drops it.
        document.Remove(2, 4);
        Check(header.Start == 0 && header.Length == 2, "removing the tail of a block shortens it");
        Equal(document.Highlights[1].Start, 4, "the next block moves back");
        document.Remove(0, 2);
        Check(!ContainsHighlight(document, header), "a block whose bytes are all removed disappears");

        // Removing the colour from a range, a block, all of them.
        HexDocument other = new HexDocument(new byte[16]);
        other.AddHighlight(0, 16, unchecked((int)0xFF0000FF), "");
        Check(other.RemoveHighlights(4, 4) && other.Highlights.Count == 2, "removing the colour from the middle splits the block");
        Check(other.RemoveHighlights(1, 0) && other.Highlights.Count == 1, "removing the block at an offset");
        Check(!other.RemoveHighlights(5, 0), "there is no block to remove at an uncoloured byte");
        HexDocument copy = new HexDocument(new byte[10]);
        copy.CopyHighlightsFrom(other);
        Check(copy.Highlights.Count == 1 && copy.Highlights[0].Start == 8 && copy.Highlights[0].Length == 2, "the blocks are taken over and cut to the length");
        other.ClearHighlights();
        Equal(other.Highlights.Count, 0, "all blocks are removed");
    }

    private static bool ContainsHighlight(HexDocument document, HexHighlight highlight)
    {
        foreach (HexHighlight item in document.Highlights)
        {
            if (ReferenceEquals(item, highlight))
            {
                return true;
            }
        }
        return false;
    }

    private static string Read(byte[] bytes, HexValueType type, bool bigEndian)
    {
        return HexValueFormats.TryRead(bytes, type, bigEndian, out string text) ? text : null;
    }

    private static byte[] Parse(HexValueType type, string text, bool bigEndian)
    {
        return HexValueFormats.TryParse(type, text, bigEndian, out byte[] bytes, out _) ? bytes : null;
    }

    private static void CheckValues()
    {
        byte[] bytes = { 0x78, 0x56, 0x34, 0x12, 0xF0, 0xDE, 0xBC, 0x9A };
        Equal(HexValueFormats.ReadUnsigned(bytes, 0, 4, bigEndian: false), 0x12345678UL, "a little-endian 32-bit value");
        Equal(HexValueFormats.ReadUnsigned(bytes, 0, 4, bigEndian: true), 0x78563412UL, "a big-endian 32-bit value");
        Equal(HexValueFormats.ReadUnsigned(bytes, 0, 8, bigEndian: false), 0x9ABCDEF012345678UL, "a little-endian 64-bit value");
        EqualBytes(HexValueFormats.WriteUnsigned(0x1234, 2, bigEndian: true), new byte[] { 0x12, 0x34 }, "a big-endian 16-bit value is written");
        EqualBytes(HexValueFormats.WriteUnsigned(0x1234, 2, bigEndian: false), new byte[] { 0x34, 0x12 }, "a little-endian 16-bit value is written");

        Equal(Read(bytes, HexValueType.UInt16, false), "22136", "UInt16 little endian");
        Equal(Read(bytes, HexValueType.UInt16, true), "30806", "UInt16 big endian");
        Equal(Read(new byte[] { 0xFF, 0xFF }, HexValueType.Int16, false), "-1", "Int16 is signed");
        Equal(Read(new byte[] { 0x80 }, HexValueType.Int8, false), "-128", "Int8 is signed");
        Equal(Read(new byte[] { 0x80 }, HexValueType.UInt8, false), "128", "UInt8 is unsigned");
        Equal(Read(new byte[] { 0x48 }, HexValueType.Binary, false), "01001000", "the binary byte");
        Equal(Read(bytes, HexValueType.Int64, false), "-7296712173568108936", "Int64 little endian");
        Equal(Read(new byte[] { 0x00, 0x00, 0x80, 0x3F }, HexValueType.Float32, false), "1", "a little-endian float");
        Equal(Read(new byte[] { 0x3F, 0x80, 0x00, 0x00 }, HexValueType.Float32, true), "1", "a big-endian float");
        Equal(Read(new byte[] { 0, 0, 0, 0, 0, 0, 0xF8, 0x3F }, HexValueType.Float64, false), "1.5", "a little-endian double");
        Equal(Read(new byte[] { 0x00, 0x00, 0x00, 0x00 }, HexValueType.UnixTime32, false), "1970-01-01 00:00:00 UTC", "the epoch");
        Equal(Read(new byte[] { 0x65, 0x92, 0x00, 0x80 }, HexValueType.UnixTime32, true), "2024-01-01 00:00:00 UTC", "a big-endian time");
        Equal(Read(new byte[] { 0xD0, 0x96, 0x41 }, HexValueType.Utf8Char, false), "'\u0416' U+0416", "a two-byte UTF-8 character");
        Equal(Read(new byte[] { 0x41 }, HexValueType.Utf8Char, false), "'A' U+0041", "an ASCII character");
        Equal(Read(new byte[] { 0x0A }, HexValueType.Utf8Char, false), "U+000A", "a control character is shown by its code");
        Equal(Read(new byte[] { 0x96 }, HexValueType.Utf8Char, false), "(not the start of a character)", "a continuation byte");
        Equal(Read(new byte[] { 0xC0, 0x80 }, HexValueType.Utf8Char, false), "(invalid sequence)", "an overlong sequence");
        Equal(Read(new byte[] { 0xE2, 0x82 }, HexValueType.Utf8Char, false), "(incomplete)", "a cut sequence");
        Equal(Read(new byte[] { 1, 2, 3 }, HexValueType.UInt32, false), null, "too few bytes for the value");

        EqualBytes(Parse(HexValueType.UInt16, "0x1234", false), new byte[] { 0x34, 0x12 }, "UInt16 is written little endian");
        EqualBytes(Parse(HexValueType.UInt16, "4660", true), new byte[] { 0x12, 0x34 }, "UInt16 is written big endian");
        EqualBytes(Parse(HexValueType.Int16, "-2", false), new byte[] { 0xFE, 0xFF }, "a negative Int16");
        EqualBytes(Parse(HexValueType.Int8, "0xFF", false), new byte[] { 0xFF }, "a hexadecimal bit pattern for a signed byte");
        EqualBytes(Parse(HexValueType.Int64, "-9223372036854775808", true), new byte[] { 0x80, 0, 0, 0, 0, 0, 0, 0 }, "the smallest Int64");
        EqualBytes(Parse(HexValueType.UInt64, "18446744073709551615", false), new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, "the largest UInt64");
        Equal(Parse(HexValueType.UInt8, "256", false), null, "a byte holds up to 255");
        Equal(Parse(HexValueType.Int8, "-129", false), null, "Int8 holds -128 and more");
        Equal(Parse(HexValueType.UInt16, "-1", false), null, "an unsigned value is not negative");
        Equal(Parse(HexValueType.UInt32, "12abc", false), null, "letters are not a number");
        EqualBytes(Parse(HexValueType.Binary, "0b0100 1000", false), new byte[] { 0x48 }, "binary digits");
        Equal(Parse(HexValueType.Binary, "012", false), null, "2 is not a binary digit");
        EqualBytes(Parse(HexValueType.Float32, "1", true), new byte[] { 0x3F, 0x80, 0, 0 }, "a big-endian float is written");
        EqualBytes(Parse(HexValueType.Float64, "1,5", false), new byte[] { 0, 0, 0, 0, 0, 0, 0xF8, 0x3F }, "a decimal comma is accepted");
        Check(Parse(HexValueType.Float32, "NaN", false) != null, "NaN can be written");
        Equal(Parse(HexValueType.Float32, "1e300", false), null, "a float holds up to 3.4e38");
        EqualBytes(Parse(HexValueType.UnixTime32, "2024-01-01 00:00:00", true), new byte[] { 0x65, 0x92, 0x00, 0x80 }, "a time is written");
        EqualBytes(Parse(HexValueType.UnixTime32, "16", false), new byte[] { 16, 0, 0, 0 }, "a time in seconds");
        Equal(Parse(HexValueType.UnixTime32, "1960-01-01", false), null, "a time before 1970 does not fit");
        EqualBytes(Parse(HexValueType.Utf8Char, "\u0416", false), new byte[] { 0xD0, 0x96 }, "a character is written as UTF-8");
        EqualBytes(Parse(HexValueType.Utf8Char, " ", false), new byte[] { 0x20 }, "a space can be written");
        Equal(Parse(HexValueType.Utf8Char, "ab", false), null, "only one character");
        Check(!HexValueFormats.TryParse(HexValueType.UInt8, "", false, out _, out string error) && error.Length > 0, "an empty value explains itself");

        // Every value written in either byte order reads back the same.
        foreach (HexValueType type in HexValueFormats.InspectorTypes)
        {
            foreach (bool bigEndian in new[] { false, true })
            {
                byte[] sample = { 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48 };
                string text = Read(sample, type, bigEndian);
                byte[] written = Parse(type, text.EndsWith(" U+0041", StringComparison.Ordinal) ? "A" : text, bigEndian);
                Check(written != null && HexDocument.ByteArraysEqual(written, Copy(sample, written.Length)),
                    "a " + HexValueFormats.GetName(type) + (bigEndian ? " big" : " little") + " endian value reads back the same");
            }
        }

        EqualBytes(HexValueFormats.SwapByteOrder(new byte[] { 1, 2, 3, 4, 5 }, 2), new byte[] { 2, 1, 4, 3, 5 }, "16-bit words are swapped, the odd byte stays");
        EqualBytes(HexValueFormats.SwapByteOrder(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 4), new byte[] { 4, 3, 2, 1, 8, 7, 6, 5 }, "32-bit words are swapped");
        EqualBytes(HexValueFormats.SwapByteOrder(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 8), new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }, "a 64-bit word is swapped");
        EqualBytes(HexValueFormats.RepeatPattern(new byte[] { 0xDE, 0xAD }, 5), new byte[] { 0xDE, 0xAD, 0xDE, 0xAD, 0xDE }, "a pattern is repeated");
    }

    private static byte[] Copy(byte[] bytes, int count)
    {
        byte[] result = new byte[count];
        Array.Copy(bytes, result, count);
        return result;
    }

    private static void CheckCopyFormats()
    {
        byte[] bytes = { 0x48, 0x69, 0x0A };
        Equal(HexValueFormats.Format(bytes, HexCopyFormat.HexSpaced), "48 69 0A", "copy as spaced hex");
        Equal(HexValueFormats.Format(bytes, HexCopyFormat.HexCompact), "48690A", "copy as compact hex");
        Equal(HexValueFormats.Format(bytes, HexCopyFormat.CArray), "unsigned char data[3] = {\r\n    0x48, 0x69, 0x0A\r\n};", "copy as a C array");
        Equal(HexValueFormats.Format(Array.Empty<byte>(), HexCopyFormat.CArray), "unsigned char data[0] = { };", "an empty C array");
        Equal(HexValueFormats.Format(bytes, HexCopyFormat.PythonBytes), "b\"\\x48\\x69\\x0a\"", "copy as Python bytes");
        Equal(HexValueFormats.Format(bytes, HexCopyFormat.Base64), "SGkK", "copy as Base64");
        Equal(HexValueFormats.Format(bytes, HexCopyFormat.Utf8Text), "Hi\n", "copy as text");
        string array = HexValueFormats.Format(new byte[17], HexCopyFormat.CArray);
        Equal(array.Split('\n').Length, 4, "a C array has 16 bytes per line");
    }

    private static void CheckChecksums()
    {
        byte[] check = System.Text.Encoding.ASCII.GetBytes("123456789");
        Dictionary<string, string> sums = new Dictionary<string, string>();
        foreach (HexChecksum checksum in HexChecksums.Compute(check))
        {
            sums[checksum.Name] = checksum.Value;
        }
        Equal(sums["Sum (8-bit)"], "DD", "the 8-bit sum");
        Equal(sums["Sum (32-bit)"], "000001DD", "the 32-bit sum");
        Equal(sums["XOR (8-bit)"], "31", "the XOR of the bytes");
        Equal(sums["CRC-16/MODBUS"], "4B37", "CRC-16/MODBUS of the check string");
        Equal(sums["CRC-16/CCITT-FALSE"], "29B1", "CRC-16/CCITT-FALSE of the check string");
        Equal(sums["CRC-16/XMODEM"], "31C3", "CRC-16/XMODEM of the check string");
        Equal(sums["CRC-32"], "CBF43926", "CRC-32 of the check string");
        Equal(sums["MD5"], "25f9e794323b453885f5181f1b624d0b", "MD5 of the check string");
        Equal(sums["SHA-1"], "f7c3bc1d808e04732adf679965ccc34ca7ae3441", "SHA-1 of the check string");
        Equal(sums["SHA-256"], "15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225", "SHA-256 of the check string");
        Equal(HexChecksums.Crc32(Array.Empty<byte>()), 0u, "the CRC-32 of nothing");
    }
}
