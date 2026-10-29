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

        Console.WriteLine();
        Console.WriteLine("All " + _passed + " checks passed.");
        return 0;
    }
}
