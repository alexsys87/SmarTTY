using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

// Checks of CodeEditing/RemoteFileContent.cs, the content of a remote file in the two views of its editor: the bytes on the server, the encoding
// and the line breaks, and the switching between the text and the hex view (the bytes shown, which view can be edited, the text kept while the
// bytes are shown, the question about bytes the text cannot hold, the modified state of both, a save), with a fake editor and a fake hex view
// that record what they are asked, in order.
// The same code is run in the real editor against real servers by tests/RemoteFileEditorIntegrationTests (the "hex-view" and "hex-draft" scenarios).
// Run with: dotnet run --project tests/RemoteFileContentTests
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
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static void SameBytes(byte[] actual, byte[] expected, string name)
    {
        bool same = actual != null && expected != null && actual.SequenceEqual(expected);
        Check(same, name + (same ? "" : " (expected " + Hex(expected) + ", got " + Hex(actual) + ")"));
    }

    private static string Hex(byte[] bytes)
    {
        return bytes == null ? "<null>" : string.Join(" ", bytes.Select(b => b.ToString("X2")));
    }

    private const string Path = "/etc/app/hosts.txt";

    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    private static byte[] Bytes(string text)
    {
        return Utf8.GetBytes(text);
    }

    // What the content asked of the editor and of the hex view, and the events it raised, in order.
    private static readonly List<string> Log = new List<string>();

    private static string TakeLog()
    {
        string text = string.Join(", ", Log);
        Log.Clear();
        return text;
    }

    private sealed class FakeHost : IRemoteFileContentHost
    {
        public EditableTextDocument TextDocument { get; private set; } = new EditableTextDocument();

        public bool Modified;

        public bool ReadOnly;

        public TextDocument.TextPosition CaretPosition = new TextDocument.TextPosition(0, 0);

        public bool Loaded = true;

        public bool Saving;

        public bool ReadOnlyMode;

        public bool Answer = true;

        public bool HexShown;

        public readonly List<string> Errors = new List<string>();

        public readonly List<string> Questions = new List<string>();

        public object SavedVersion;

        public bool IsTextModified
        {
            get
            {
                return Modified;
            }
            set
            {
                Log.Add("text modified:" + value);
                Modified = value;
            }
        }

        public bool TextReadOnly
        {
            get
            {
                return ReadOnly;
            }
            set
            {
                Log.Add("text read-only:" + value);
                ReadOnly = value;
            }
        }

        public TextDocument.TextPosition Caret => CaretPosition;

        public void SetCaret(TextDocument.TextPosition position)
        {
            Log.Add("caret " + position.Line + ":" + position.Column);
            CaretPosition = position;
        }

        public void SetDocument(EditableTextDocument document)
        {
            Log.Add("text document");
            TextDocument = document;
        }

        public void FocusText()
        {
            Log.Add("focus text");
        }

        public void MarkTextSaved(object version)
        {
            Log.Add("text saved");
            SavedVersion = version;
            Modified = false;
        }

        public void RaiseModifiedChanged()
        {
            Log.Add("modified changed");
        }

        public bool IsLoaded => Loaded;

        public bool IsSaving => Saving;

        public bool IsReadOnlyMode => ReadOnlyMode;

        public void ShowHexView()
        {
            Log.Add("show hex");
            HexShown = true;
        }

        public void HideHexView()
        {
            Log.Add("hide hex");
            HexShown = false;
        }

        public void ShowError(string message)
        {
            Log.Add("error");
            Errors.Add(message);
        }

        public bool ConfirmWarning(string question)
        {
            Log.Add("question");
            Questions.Add(question);
            return Answer;
        }

        public void SetText(string text)
        {
            TextDocument = new EditableTextDocument();
            TextDocument.SetText(text);
        }
    }

    private sealed class FakeHexView : IRemoteHexView
    {
        private HexDocument _document = new HexDocument();

        public bool IsReadOnly = true;

        public HexDocument Document
        {
            get
            {
                return _document;
            }
            set
            {
                Log.Add(value == null ? "hex document: none" : "hex document");
                // As the hex editor: no document is an empty one.
                _document = value ?? new HexDocument();
            }
        }

        public bool ReadOnly
        {
            get
            {
                return IsReadOnly;
            }
            set
            {
                Log.Add("hex read-only:" + value);
                IsReadOnly = value;
            }
        }

        public bool Focus()
        {
            Log.Add("focus hex");
            return true;
        }

        public void Invalidate()
        {
            Log.Add("redraw hex");
        }
    }

    private sealed class Setup
    {
        public FakeHost Host = new FakeHost();

        public FakeHexView Hex = new FakeHexView();

        public RemoteFileSession Session = new RemoteFileSession(Path);

        public RemoteFileContent Content;

        public Setup()
        {
            Content = new RemoteFileContent(Path, Host, Hex, Session);
            Content.HexModeChanged += (sender, e) => Log.Add("event hex:" + Content.HexMode);
            Content.FormatChanged += (sender, e) => Log.Add("event format");
        }

        // The file as it is loaded: its bytes, read in the encoding, are the text of the editor.
        public Setup Loaded(string text, TextFileEncoding encoding = null)
        {
            encoding = encoding ?? TextFileEncodings.Utf8;
            byte[] bytes = TextFileEncodings.Encode(text, encoding);
            Content.ApplyContent(bytes, encoding, text);
            Log.Clear();
            return this;
        }

        // An edit of the text, as typing would make it.
        public void Edit(string text)
        {
            Host.SetText(text);
            Host.Modified = true;
        }
    }

    private static void Initial()
    {
        Console.WriteLine("-- before a file is loaded");
        Setup s = new Setup();
        Check(s.Content.RawBytes == null && s.Content.RawEncoding == TextFileEncodings.Utf8, "no bytes from the server, read as UTF-8");
        Check(s.Content.FileEncoding == TextFileEncodings.Utf8 && s.Content.LineEndings == LineEndingKind.None, "the file is saved as UTF-8, its line breaks are not known");
        Check(!s.Content.HexMode && s.Content.HexDocument == null, "the text view, no hex document");
        s.Host.Modified = true;
        Check(s.Content.IsModified, "the modified state is the text's");
        s.Content.IsModified = false;
        Check(!s.Host.Modified, "and is set on the text");
        Equal(TakeLog(), "text modified:False", "nothing else is asked");
    }

    private static void Apply()
    {
        Console.WriteLine("-- the content of a loaded file");
        Setup s = new Setup();
        byte[] file = Bytes("one\r\ntwo\r\n");
        s.Session.ConvertCrLf = true;
        s.Session.BomAnswered = true;
        s.Session.HexBytesForTextSave = new byte[] { 1 };
        s.Content.ApplyContent(file, TextFileEncodings.Utf8, "one\r\ntwo\r\n");
        Equal(TakeLog(), "hide hex, hex read-only:True, hex document: none, text document, text modified:False, event format", "the hex view is reset, the text is put in as not modified, the format is told");
        Check(ReferenceEquals(s.Content.RawBytes, file) && s.Content.RawEncoding == TextFileEncodings.Utf8 && s.Content.FileEncoding == TextFileEncodings.Utf8, "the bytes and the encoding of the file");
        Equal(s.Content.LineEndings, LineEndingKind.CrLf, "the line breaks are found");
        Equal(s.Host.TextDocument.DefaultLineEnding, TextDocument.LineEnding.CrLf, "and new lines get them");
        Equal(s.Host.TextDocument.GetText(), "one\r\ntwo\r\n", "the text");
        Check(s.Session.ConvertCrLf == null && !s.Session.BomAnswered && s.Session.HexBytesForTextSave == null, "the questions of a save are asked again, no bytes are kept from a hex view");

        s.Content.ApplyContent(Bytes("a\rb"), TextFileEncodings.Utf8, "a\rb");
        Equal(s.Host.TextDocument.DefaultLineEnding, TextDocument.LineEnding.Cr, "CR line breaks");
        s.Content.ApplyContent(null, TextFileEncodings.Utf8, "");
        Check(s.Content.RawBytes == null && s.Content.LineEndings == LineEndingKind.None, "a new file: no bytes, no line breaks");
        Equal(s.Host.TextDocument.DefaultLineEnding, TextDocument.LineEnding.Lf, "new lines get LF");
    }

    private static void EnterAndExitUnchanged()
    {
        Console.WriteLine("-- into the hex view and back, nothing changed");
        Setup s = new Setup().Loaded("abc\ndef\n");
        s.Host.CaretPosition = new TextDocument.TextPosition(1, 1);
        s.Content.EnterHexMode();
        Equal(TakeLog(), "text read-only:True, hex document, hex read-only:False, show hex, focus hex, modified changed, event hex:True, event format", "opening the hex view: the text is read-only, the document, the hex view can be edited, it is shown and focused, then the events");
        Check(s.Content.HexMode && s.Host.HexShown && ReferenceEquals(s.Hex.Document, s.Content.HexDocument), "the hex view shows the hex document");
        SameBytes(s.Content.HexDocument.ToArray(), Bytes("abc\ndef\n"), "the bytes of the file");
        Check(!s.Content.IsModified, "not modified");
        s.Content.EnterHexMode();
        Equal(TakeLog(), "", "a second time does nothing");

        s.Content.ExitHexMode();
        Equal(TakeLog(), "hide hex, hex read-only:True, text read-only:False, text modified:False, focus text, modified changed, event hex:False, event format", "closing it keeps the text document");
        Check(!s.Content.HexMode && !s.Host.HexShown, "the text view");
        SameBytes(s.Session.HexBytesForTextSave, Bytes("abc\ndef\n"), "the bytes are kept for a save of the text");
        Equal(s.Session.HexTextSnapshot, "abc\ndef\n", "with the text they are");
        s.Content.ExitHexMode();
        Equal(TakeLog(), "", "a second time does nothing");

        HexDocument first = s.Content.HexDocument;
        s.Content.EnterHexMode();
        Check(ReferenceEquals(s.Content.HexDocument, first), "the hex view of the same text is the same document");
        Equal(TakeLog(), "text read-only:True, hex read-only:False, show hex, focus hex, modified changed, event hex:True, event format", "which the view has already");
    }

    private static void EnterRules()
    {
        Console.WriteLine("-- when the hex view opens, and what it shows");
        Setup s = new Setup().Loaded("abc\n");
        s.Host.Loaded = false;
        s.Content.EnterHexMode();
        Check(!s.Content.HexMode && TakeLog() == "", "not while the file is not loaded");
        s.Host.Loaded = true;
        s.Host.Saving = true;
        s.Content.EnterHexMode();
        Check(!s.Content.HexMode && TakeLog() == "", "not while a save runs");
        s.Host.Saving = false;

        s.Host.ReadOnlyMode = true;
        s.Content.EnterHexMode();
        Check(s.Content.HexMode && s.Hex.IsReadOnly, "a file that is only read: the hex view cannot be edited");
        s.Content.ExitHexMode();
        Check(s.Host.ReadOnly && s.Hex.IsReadOnly, "and back, neither can the text");
        s.Host.ReadOnlyMode = false;

        s = new Setup().Loaded("abc\n");
        s.Edit("xyz\n");
        s.Content.EnterHexMode();
        SameBytes(s.Content.HexDocument.ToArray(), Bytes("xyz\n"), "an edited text: its bytes in the encoding of the file");
        Check(s.Content.IsModified && s.Content.HexDocument.IsModified, "they differ from the file: modified");

        s = new Setup().Loaded("abc\n", TextFileEncodings.Utf8Bom);
        s.Edit("abc\n");
        s.Content.EnterHexMode();
        SameBytes(s.Content.HexDocument.ToArray(), new byte[] { 0xEF, 0xBB, 0xBF, 0x61, 0x62, 0x63, 0x0A }, "the byte order mark of the encoding is part of the bytes");
        Check(!s.Content.IsModified, "the same bytes as the file: not modified");

        // An unchanged text shows the bytes of the file, also when the text would not give them back.
        byte[] file = { 0x41, 0xFF, 0x0A };
        s = new Setup();
        s.Content.ApplyContent(file, TextFileEncodings.Utf8, TextFileEncodings.Decode(file, TextFileEncodings.Utf8));
        s.Content.EnterHexMode();
        SameBytes(s.Content.HexDocument.ToArray(), file, "an unchanged text: the bytes of the file, not the text encoded again");

        // A text the encoding cannot store has no bytes: the editor says so, the text view stays.
        s = new Setup().Loaded("abc\n", TextFileEncodings.Windows1251);
        s.Edit("\u00e9t\u00e9\n");
        s.Content.EnterHexMode();
        Equal(TakeLog(), "error", "a letter Windows-1251 cannot store: an error, nothing else");
        Check(s.Host.Errors.Count == 1 && s.Host.Errors[0].Contains("(\"\u00e9\", U+00E9) that Windows-1251 cannot store"), "the error names the letter and the encoding");
        Check(!s.Content.HexMode && s.Content.HexDocument == null, "the text view stays");

        // The coloured blocks of a hex document that is made again are kept.
        s = new Setup().Loaded("abcdef\n");
        s.Content.EnterHexMode();
        s.Content.HexDocument.AddHighlight(1, 2, unchecked((int)0xFF00FF00), "block");
        s.Content.ExitHexMode();
        s.Edit("abcdeF\n");
        s.Content.EnterHexMode();
        Check(s.Content.HexDocument.Highlights.Count == 1 && s.Content.HexDocument.Highlights[0].Start == 1 && s.Content.HexDocument.Highlights[0].Label == "block", "a new hex document of an edited text keeps the coloured blocks");
    }

    private static void ExitAfterChange()
    {
        Console.WriteLine("-- back to the text after a change of the bytes");
        Setup s = new Setup().Loaded("abc\ndef\n");
        s.Host.CaretPosition = new TextDocument.TextPosition(3, 1);
        s.Content.EnterHexMode();
        s.Content.HexDocument.Replace(4, 3, Bytes("D\r"));
        Log.Clear();
        s.Content.ExitHexMode();
        Equal(TakeLog(), "hide hex, hex read-only:True, text read-only:False, text document, caret 1:1, text modified:True, focus text, modified changed, event hex:False, event format", "a new text document, the caret brought into the line, modified");
        Equal(s.Host.TextDocument.GetText(), "abc\nD\r\n", "the text is the bytes decoded");
        Equal(s.Content.LineEndings, LineEndingKind.Mixed, "its line breaks are found again");
        Equal(s.Host.TextDocument.DefaultLineEnding, TextDocument.LineEnding.Lf, "mixed line breaks: new lines get LF");

        s = new Setup().Loaded("a\nb\nc\n");
        s.Host.CaretPosition = new TextDocument.TextPosition(1, 2);
        s.Content.EnterHexMode();
        s.Content.HexDocument.Replace(0, 6, Bytes("x\r\n"));
        s.Content.ExitHexMode();
        Equal(s.Host.CaretPosition.Line + ":" + s.Host.CaretPosition.Column, "1:0", "a caret below the last line goes to the last line");
        Equal(s.Content.LineEndings, LineEndingKind.CrLf, "CRLF");
        Equal(s.Host.TextDocument.DefaultLineEnding, TextDocument.LineEnding.CrLf, "new lines get CRLF");

        // The bytes changed and changed back: the text from before, not modified.
        s = new Setup().Loaded("abc\n");
        s.Content.EnterHexMode();
        s.Content.HexDocument.ReplaceByte(0, (byte)'x');
        s.Content.HexDocument.Undo();
        Log.Clear();
        s.Content.ExitHexMode();
        Check(!TakeLog().Contains("text document") && !s.Host.Modified, "the bytes of the file again: the text document is kept, not modified");

        // An edited text whose bytes were changed back to those of the file in the hex view: the bytes decoded, not modified.
        s = new Setup().Loaded("abc\n");
        s.Edit("abX\n");
        s.Content.EnterHexMode();
        s.Content.HexDocument.ReplaceByte(2, (byte)'c');
        Log.Clear();
        s.Content.ExitHexMode();
        Check(TakeLog().Contains("text document") && s.Host.TextDocument.GetText() == "abc\n" && !s.Host.Modified, "an edited text whose bytes are the file's again: the text of the file, not modified");

        // An edited text whose bytes did not change in the hex view: the text is kept, and it is modified.
        s = new Setup().Loaded("abc\n");
        s.Edit("abX\n");
        EditableTextDocument edited = s.Host.TextDocument;
        s.Content.EnterHexMode();
        s.Content.ExitHexMode();
        Check(ReferenceEquals(s.Host.TextDocument, edited) && s.Host.Modified, "an edited text comes back as it was, modified");
    }

    private static void Lossy()
    {
        Console.WriteLine("-- bytes the text cannot hold");
        Setup s = new Setup().Loaded("abc\n");
        s.Content.EnterHexMode();
        s.Content.HexDocument.ReplaceByte(0, 0xFF);
        s.Host.Answer = false;
        Log.Clear();
        s.Content.ExitHexMode();
        Equal(TakeLog(), "question, focus hex", "the editor asks; answered No: the hex view keeps the focus");
        Check(s.Host.Questions[0] == "Not all bytes can be shown as text in UTF-8: saving the text after it is edited would change them.\n\nShow the bytes as text anyway?", "the question names the encoding");
        Check(s.Content.HexMode && s.Session.HexBytesForTextSave == null, "and nothing else changes");
        s.Host.Answer = true;
        s.Content.ExitHexMode();
        Check(!s.Content.HexMode, "answered Yes: the text view");
        Equal(s.Host.TextDocument.GetText(), "\uFFFDbc\n", "a byte that is not UTF-8 is U+FFFD");
        SameBytes(s.Session.HexBytesForTextSave, new byte[] { 0xFF, 0x62, 0x63, 0x0A }, "the bytes are kept for a save of the text");
        Equal(s.Session.HexTextSnapshot, "\uFFFDbc\n", "with the text they were shown as");

        // A new text from the bytes is shown again as those bytes, not as the text encoded.
        HexDocument shown = s.Content.HexDocument;
        s.Content.EnterHexMode();
        Check(ReferenceEquals(s.Content.HexDocument, shown), "the same hex document");
        s.Content.ExitHexMode();
        s.Edit("\uFFFDbc!\n");
        s.Content.EnterHexMode();
        SameBytes(s.Content.HexDocument.ToArray(), Bytes("\uFFFDbc!\n"), "a text edited since: the text encoded");

        // The hex document changed after the view was left (an undo of it): the kept bytes of the text are shown, in a new document.
        s = new Setup().Loaded("abc\n");
        s.Content.EnterHexMode();
        s.Content.HexDocument.ReplaceByte(0, 0xFF);
        s.Content.ExitHexMode();
        shown = s.Content.HexDocument;
        shown.Undo();
        s.Host.Modified = true;
        s.Content.EnterHexMode();
        Check(!ReferenceEquals(s.Content.HexDocument, shown), "a hex document whose bytes are not the kept ones is made again");
        SameBytes(s.Content.HexDocument.ToArray(), new byte[] { 0xFF, 0x62, 0x63, 0x0A }, "with the kept bytes of the text, not the text encoded");

        // An unchanged file whose bytes are not UTF-8 is shown as it was, without a question.
        byte[] file = { 0x41, 0xFF, 0x0A };
        s = new Setup();
        s.Content.ApplyContent(file, TextFileEncodings.Utf8, "A\uFFFD\n");
        s.Content.EnterHexMode();
        s.Host.Questions.Clear();
        s.Content.ExitHexMode();
        Check(s.Host.Questions.Count == 0 && !s.Content.HexMode, "the bytes of the file: no question");
    }

    private static void Modified()
    {
        Console.WriteLine("-- the modified state of the two views");
        Setup s = new Setup().Loaded("abc\n");
        s.Content.EnterHexMode();
        Log.Clear();
        s.Content.HexDocument.ReplaceByte(0, (byte)'x');
        Equal(TakeLog(), "modified changed", "a change of the hex document is told");
        Check(s.Content.IsModified && !s.Host.Modified, "the hex document is modified, the text is not touched");
        s.Content.IsModified = false;
        Check(!s.Content.IsModified && !s.Content.HexDocument.IsModified, "the saved state is set on the hex document");
        s.Content.IsModified = true;
        Check(s.Content.IsModified && s.Content.HexDocument.IsModified, "and the modified state");
        Equal(TakeLog(), "modified changed, modified changed", "both are told");
        HexDocument hex = s.Content.HexDocument;
        s.Host.Answer = true;
        s.Content.ExitHexMode();
        Log.Clear();
        hex.Undo();
        Equal(TakeLog(), "", "a change of the hex document while the text is shown is not told");
        s.Host.Modified = false;
        s.Content.IsModified = true;
        Check(s.Host.Modified && s.Content.IsModified, "in the text view the state is the text's");
    }

    private static void Format()
    {
        Console.WriteLine("-- the encoding and the line breaks of a save");
        Setup s = new Setup().Loaded("a\nb\n");
        s.Session.HexBytesForTextSave = new byte[] { 1 };
        s.Session.HexTextSnapshot = "x";
        s.Content.SetSaveEncoding(TextFileEncodings.Utf8Bom);
        Equal(TakeLog(), "text modified:True, event format", "a new encoding: modified, the format is told");
        Check(s.Content.FileEncoding == TextFileEncodings.Utf8Bom && s.Content.RawEncoding == TextFileEncodings.Utf8, "the file is saved in it, the bytes of the server are still read in theirs");
        Check(s.Session.HexBytesForTextSave == null && s.Session.HexTextSnapshot == null, "the bytes kept from the hex view are dropped");
        s.Content.SetSaveEncoding(TextFileEncodings.Utf8Bom);
        s.Content.SetSaveEncoding(null);
        Equal(TakeLog(), "", "the same encoding, or none: nothing");
        s.Host.ReadOnly = true;
        s.Content.SetSaveEncoding(TextFileEncodings.Utf16Le);
        s.Content.SetLineEnding(TextDocument.LineEnding.CrLf);
        Check(TakeLog() == "" && s.Content.FileEncoding == TextFileEncodings.Utf8Bom && s.Content.LineEndings == LineEndingKind.Lf, "a read-only text: neither changes");
        s.Host.ReadOnly = false;

        s.Session.HexBytesForTextSave = new byte[] { 1 };
        s.Content.SetLineEnding(TextDocument.LineEnding.CrLf);
        Equal(TakeLog(), "text modified:True, event format", "new line breaks: modified, the format is told");
        Equal(s.Host.TextDocument.GetText(), "a\r\nb\r\n", "every line break of the text is converted");
        Check(s.Content.LineEndings == LineEndingKind.CrLf && s.Host.TextDocument.DefaultLineEnding == TextDocument.LineEnding.CrLf, "and new lines get it");
        Check(s.Session.HexBytesForTextSave == null, "the bytes kept from the hex view are dropped");
        s.Content.SetLineEnding(TextDocument.LineEnding.Cr);
        Equal(s.Content.LineEndings, LineEndingKind.Cr, "CR");
        s.Content.SetLineEnding(TextDocument.LineEnding.Lf);
        Equal(s.Content.LineEndings, LineEndingKind.Lf, "LF");

        s.Content.EnterHexMode();
        // The text view is read-only in the hex view; the rule holds without that too.
        s.Host.ReadOnly = false;
        Log.Clear();
        s.Content.SetSaveEncoding(TextFileEncodings.Utf16Le);
        s.Content.SetLineEnding(TextDocument.LineEnding.CrLf);
        Check(TakeLog() == "" && s.Content.FileEncoding == TextFileEncodings.Utf8Bom && s.Content.LineEndings == LineEndingKind.Lf, "in the hex view neither changes");
    }

    private static void Changes()
    {
        Console.WriteLine("-- the changes against the file on the server");
        Setup s = new Setup().Loaded("one\ntwo\n", TextFileEncodings.Windows1251);
        s.Edit("one\nTWO\n");
        TextDiff.Result diff = s.Content.GetChanges();
        Check(diff.UnifiedText.Contains("--- " + Path + " (on the server)") && diff.UnifiedText.Contains("+++ " + Path + " (in the editor)"), "the two sides are named after the file");
        Check(diff.UnifiedText.Contains("-two") && diff.UnifiedText.Contains("+TWO"), "the changed line");
        // The bytes of the server are read in their encoding, not in the one the file is to be saved in.
        s = new Setup().Loaded("\u043e\u0434\u0438\u043d\n", TextFileEncodings.Windows1251);
        s.Content.SetSaveEncoding(TextFileEncodings.Utf8);
        Check(s.Content.GetChanges().IsIdentical, "a new encoding of a save alone: no changes");
        s = new Setup();
        s.Content.ApplyContent(null, TextFileEncodings.Utf8, "");
        s.Edit("new\n");
        Check(s.Content.GetChanges().UnifiedText.Contains("+new"), "a new file: everything is added");
    }

    private static void Save()
    {
        Console.WriteLine("-- a save");
        Setup s = new Setup().Loaded("abc\n");
        s.Content.EnterHexMode();
        s.Content.HexDocument.ReplaceByte(0, (byte)'x');
        object version = s.Content.HexDocument.CurrentVersion;
        s.Session.HexBytesForTextSave = new byte[] { 1 };
        Log.Clear();
        byte[] saved = Bytes("xbc\n");
        s.Content.CompleteSave(new RemoteSaveJob { Bytes = saved, HexMode = true, Version = version, Encoding = TextFileEncodings.Utf8 });
        Equal(TakeLog(), "modified changed, redraw hex, event format", "a save of the hex view: the hex document is saved, the view drawn again, the format told");
        Check(ReferenceEquals(s.Content.RawBytes, saved) && !s.Content.IsModified, "the saved bytes are the file's now, not modified");
        Check(s.Session.HexBytesForTextSave == null, "the bytes kept for a save of the text are dropped");

        // Recorded as it is (a fault, see the pull request of 3A): the bytes are the file's, so the text from before the hex view comes back.
        s.Content.ExitHexMode();
        Equal(s.Host.TextDocument.GetText(), "abc\n", "back in the text after a save in the hex view: the text from before the hex view");
        Check(!s.Host.Modified, "not modified");

        // A save of the text that wrote the bytes of the hex view saves the hex view too.
        s = new Setup().Loaded("abc\n");
        s.Content.EnterHexMode();
        s.Content.HexDocument.ReplaceByte(0, 0xFF);
        s.Content.ExitHexMode();
        HexDocument hex = s.Content.HexDocument;
        Check(hex.IsModified, "the hex document is modified");
        object textVersion = new object();
        Log.Clear();
        s.Content.CompleteSave(new RemoteSaveJob { Bytes = s.Session.HexBytesForTextSave, Version = textVersion, Encoding = null });
        Equal(TakeLog(), "text saved, event format", "the text is saved, the format told");
        Check(ReferenceEquals(s.Host.SavedVersion, textVersion), "the version of the text that was saved");
        Check(!hex.IsModified, "the hex document of the same bytes is saved too");
        Check(s.Content.RawEncoding == TextFileEncodings.Utf8, "a job without an encoding: the bytes are in the encoding of the file");
        Check(s.Session.HexBytesForTextSave != null, "the bytes kept for a save of the text stay");

        s.Edit("other\n");
        s.Content.CompleteSave(new RemoteSaveJob { Bytes = Bytes("other\n"), Version = textVersion, Encoding = TextFileEncodings.Utf8Bom });
        Check(s.Content.RawEncoding == TextFileEncodings.Utf8Bom, "a job with an encoding: the bytes are read in it");
        s.Content.HexDocument.ReplaceByte(0, 0x41);
        bool before = s.Content.HexDocument.IsModified;
        s.Content.CompleteSave(new RemoteSaveJob { Bytes = Bytes("x\n"), Version = textVersion });
        Check(before && s.Content.HexDocument.IsModified, "a hex document of other bytes stays modified");
    }

    private static void Drafts()
    {
        Console.WriteLine("-- a draft of the hex view, and a reset");
        Setup s = new Setup().Loaded("0123\n");
        s.Content.ShowRestoredHexDraft(new byte[] { 0x30, 0x80, 0x0A });
        Check(s.Content.HexMode && s.Content.IsModified, "the bytes of a draft are shown in the hex view, modified");
        Equal(TakeLog(), "text read-only:True, hex document, hex read-only:False, show hex, focus hex, modified changed, event hex:True, event format", "as the hex view opens");
        s.Content.ShowRestoredHexDraft(Bytes("0123\n"));
        Check(!s.Content.IsModified, "a draft with the bytes of the file is not modified");

        Log.Clear();
        s.Content.ResetHexMode();
        Equal(TakeLog(), "hide hex, hex read-only:True, hex document: none, event hex:False", "a reset hides the view, forgets the document and tells it");
        Check(!s.Content.HexMode && s.Content.HexDocument == null && s.Session.HexBytesForTextSave == null, "no hex view, no document, no bytes kept");
        s.Content.ResetHexMode();
        Equal(TakeLog(), "hide hex, hex read-only:True, hex document: none", "a reset without a hex document tells nothing");

        // A reset after the hex view was left still forgets its document, and tells it.
        s = new Setup().Loaded("abc\n");
        s.Content.EnterHexMode();
        s.Content.ExitHexMode();
        Log.Clear();
        s.Content.ResetHexMode();
        Check(TakeLog().EndsWith("event hex:False") && s.Content.HexDocument == null, "a document that is kept: the reset tells it too");
    }

    private static int Main()
    {
        Initial();
        Apply();
        EnterAndExitUnchanged();
        EnterRules();
        ExitAfterChange();
        Lossy();
        Modified();
        Format();
        Changes();
        Save();
        Drafts();
        Console.WriteLine();
        Console.WriteLine("RemoteFileContentTests: all " + _passed + " checks passed.");
        return 0;
    }
}
