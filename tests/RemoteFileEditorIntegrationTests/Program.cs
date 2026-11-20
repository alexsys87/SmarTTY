using SmarTTY.CodeEditing;
using SmarTTY.Controls;
using SmarTTY.Ssh;
using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using VisualTTY;

/// <summary>
/// Runs the real remote file editor (Controls/RemoteFileEditor.cs) against the test SSH servers of tests/SshNetIntegrationTests (OpenSSH, and
/// Dropbear with BusyBox ash, as on OpenWRT): the file is loaded, changed in the editor, saved, and read back from the server, byte by byte.
/// The questions the editor asks the user are caught (ThemedMessageBox.ShowImpl) and answered by the checks. It is a .NET Framework 4.8
/// WinForms program: on Linux it is built with the .NET SDK and run by Mono under a virtual X display, see the project file.
///
/// It is the safety net of the move of the logic of the editor out of the control (stage 1, step A4, and stage 3, step 3A: the "hex-view" and
/// "hex-draft" scenarios): it was written against the code before the move, and what it checks must stay the same.
/// </summary>
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

    private static string Hex(byte[] bytes)
    {
        return bytes == null ? "<missing>" : string.Join(" ", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static void SameBytes(byte[] actual, byte[] expected, string name)
    {
        bool same = actual != null && actual.SequenceEqual(expected);
        Check(same, name + (same ? "" : " (expected " + Hex(expected) + ", got " + Hex(actual) + ")"));
    }

    private sealed class SessionContext : ITerminalSessionContext
    {
        public SshSession Session { get; set; }

        public string TitleWithUserName { get; set; }

        public VirtualTerminalSettings VTSettings { get; } = new VirtualTerminalSettings();

        public bool IsRoot => false;

        public bool AutoConfirmSudo => false;

        public SmarTTY.Completion.ShellCompletionAnalyzer ProvideAnalyzer()
        {
            return null;
        }
    }

    /// <summary>One test server: a session, a directory of its own, and the questions the editor asked.</summary>
    private sealed class Server
    {
        public string Name;

        public SshSession Session;

        public SessionContext Context;

        public string Host;

        public int Port;

        public string User;

        public string Password;

        // How many times the server closed the connection after a transfer (see Settle).
        public int Reconnects;

        // The directory of the server's run, and the one of the scenario that runs.
        public string Root;

        public string Directory;

        public readonly List<string> Prompts = new List<string>();

        // The answer to the next question, by the words in it; null: the question was not expected.
        public Func<string, MessageBoxButtons, DialogResult?> Answer;
    }

    private static SshSession Connect(string host, int port, string user, string password)
    {
        SshSession session = new SshSession(new SshConnectionParameters
        {
            Host = host,
            DisplayHostName = host,
            Port = port,
            UserName = user,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        });
        if (!session.Authenticate(user, password))
        {
            session.Dispose();
            throw new InvalidOperationException("Could not authenticate as " + user);
        }
        return session;
    }

    private static string Value(string name, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private static List<string> Remote(Server server, string command)
    {
        List<string> output = server.Session.RunCommand(command, out int? status);
        if (status != 0)
        {
            throw new InvalidOperationException("The command '" + command + "' failed with " + status + ": " + string.Join("|", output));
        }
        return output;
    }

    // printf with octal escapes: every byte, on any shell.
    private static void WriteRemote(Server server, string name, byte[] bytes)
    {
        StringBuilder format = new StringBuilder();
        foreach (byte b in bytes)
        {
            format.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
        }
        Remote(server, "printf '" + format + "' > '" + server.Directory + "/" + name + "'");
    }

    private static byte[] ReadRemote(Server server, string name)
    {
        string path = server.Directory + "/" + name;
        List<string> exists = server.Session.RunCommand("[ -e '" + path + "' ] && echo yes || echo no", out _);
        if (exists.Count == 0 || exists[exists.Count - 1].Trim() != "yes")
        {
            return null;
        }
        List<string> lines = Remote(server, "od -An -v -tu1 '" + path + "'");
        List<byte> bytes = new List<byte>();
        foreach (string line in lines)
        {
            foreach (string part in line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                bytes.Add(byte.Parse(part, CultureInfo.InvariantCulture));
            }
        }
        return bytes.ToArray();
    }

    private static string Mode(Server server, string name)
    {
        return Remote(server, "stat -c %a '" + server.Directory + "/" + name + "'").Last().Trim();
    }

    private static void Pump(Func<bool> done, string what, int seconds = 30)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds))
            {
                throw new TimeoutException("FAIL: timed out waiting for " + what);
            }
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    // After a transfer the SSH layer may still be closing its channel, and a request made at that moment is one of those that can lose the
    // connection (see RunScenario): give it a moment.
    private static void Settle(Server server)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 400)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private sealed class Opened
    {
        public RemoteFileEditor Editor;

        public Form Form;

        public Server Server;

        public string Path;

        public void Close()
        {
            Form.Dispose();
        }
    }

    private static Opened Open(Server server, string name, Action<GlobalSettings> configure = null)
    {
        GlobalSettings settings = new GlobalSettings();
        configure?.Invoke(settings);
        Form form = new Form { Width = 800, Height = 600 };
        string path = server.Directory + "/" + name;
        RemoteFileEditor editor = new RemoteFileEditor(server.Context, path, settings, new TextDocument.TextPosition());
        editor.TextControl.Dock = DockStyle.Fill;
        form.Controls.Add(editor.TextControl);
        form.Show();
        Pump(() => editor.CanSave || editor.IsReadOnlyMode, "the file " + name + " to load");
        Settle(server);
        return new Opened { Editor = editor, Form = form, Server = server, Path = path };
    }

    private static string Text(Opened opened)
    {
        return opened.Editor.TextControl.Document.GetText();
    }

    // Replaces a word in a line of the text, as typing would.
    private static void Replace(Opened opened, int line, string oldText, string newText)
    {
        string text = opened.Editor.TextControl.Document.GetLineText(line);
        int index = text.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException("The line " + line + " is '" + text + "', it has no '" + oldText + "'");
        }
        opened.Editor.TextControl.ReplaceText(new TextDocument.TextPosition(index, line), new TextDocument.TextPosition(index + oldText.Length, line), select: false, newText);
    }

    private static void Prompted(Server server, string name, Func<string, MessageBoxButtons, DialogResult?> answer, Action action)
    {
        server.Prompts.Clear();
        server.Answer = answer;
        try
        {
            action();
        }
        finally
        {
            server.Answer = null;
        }
    }

    private static string OnlyPrompt(Server server, string name)
    {
        Check(server.Prompts.Count == 1, name + ": one question was asked (" + string.Join(" || ", server.Prompts).Replace("\n", " ") + ")");
        return server.Prompts[0];
    }

    private static void NoPrompt(Server server, string name)
    {
        Check(server.Prompts.Count == 0, name + ": no question was asked (" + string.Join(" || ", server.Prompts).Replace("\n", " ") + ")");
    }

    private static void SaveAndWait(Opened opened)
    {
        opened.Editor.SaveInBackground();
        Pump(() => !opened.Editor.IsSaving, "the background save of " + opened.Path + " to end");
        // The end of the save is posted to the editor: let what it posted run.
        for (int i = 0; i < 20; i++)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Settle(opened.Server);
    }

    private static void Save(Opened opened)
    {
        opened.Editor.Save();
        Settle(opened.Server);
    }

    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    private static byte[] Bytes(string text)
    {
        return Utf8NoBom.GetBytes(text);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        return parts.SelectMany(p => p).ToArray();
    }

    // ---- scenarios

    private static void LoadAndSave(Server server)
    {
        Console.WriteLine("-- load, change, save: UTF-8, LF");
        WriteRemote(server, "utf8.txt", Bytes("héllo\nwörld\n"));
        Opened o = Open(server, "utf8.txt");
        Equal(Text(o), "héllo\nwörld\n", "the text of the file is loaded");
        Check(o.Editor.FileEncoding == TextFileEncodings.Utf8, "plain UTF-8 is recognised");
        Equal(o.Editor.LineEndings, LineEndingKind.Lf, "LF line endings are recognised");
        Check(!o.Editor.IsModified && !o.Editor.IsNewFile && !o.Editor.IsPreview, "a loaded file is not modified, new or a preview");
        Replace(o, 1, "wörld", "WORLD");
        Check(o.Editor.IsModified, "a change makes the editor modified");
        Prompted(server, "save", (text, buttons) => null, () => Save(o));
        NoPrompt(server, "a plain save");
        SameBytes(ReadRemote(server, "utf8.txt"), Bytes("héllo\nWORLD\n"), "the saved file on the server is the edited text");
        Check(!o.Editor.IsModified, "after a save the editor is not modified");
        o.Close();
    }

    // The editor as the text of a search bar: what TextSearcher asks of it goes through ISearchTarget, on the real control.
    private static void SearchTarget(Server server)
    {
        Console.WriteLine("-- the editor as the text of a search bar (ISearchTarget)");
        WriteRemote(server, "search.txt", Bytes("one two\nthree two\nfour\n"));
        Opened o = Open(server, "search.txt");
        ISearchTarget target = o.Editor.TextControl;
        TextMatcher two = TextMatcher.Literal("two", ignoreCase: false);
        TextDocument.TextPosition none = TextDocument.TextPosition.Invalid;
        Check(!target.HasSelection, "nothing is selected in a file that was just opened");
        Equal(target.GetLineText(1), "three two", "the text of a line");
        TextDocument.TextPosition found = target.Find(new TextDocument.TextPosition(0, 0), two, false, true, FindSelectMode.SelectOrClear, none, none);
        Check(found.IsValid && found.Line == 0 && found.Column == 4, "the first match is found from the start of the text");
        Check(target.HasSelection, "the match is selected");
        Equal(target.GetRangeText(target.SelectionStart, target.SelectionEnd), "two", "the selection is the match");
        // A match is selected with the caret at its beginning, as the search bar knows: it goes on from the later end of the selection (forwards) or the earlier one (backwards).
        TextDocument.TextPosition from = target.SelectionEnd > target.SelectionStart ? target.SelectionEnd : target.SelectionStart;
        found = target.Find(from, two, false, true, FindSelectMode.SelectOrClear, none, none);
        Check(found.IsValid && found.Line == 1 && found.Column == 6, "the next match is on the next line");
        from = target.SelectionEnd < target.SelectionStart ? target.SelectionEnd : target.SelectionStart;
        found = target.Find(from, two, true, true, FindSelectMode.SelectOrClear, none, none);
        Check(found.IsValid && found.Line == 0 && found.Column == 4, "searching backwards goes to the match before");
        found = target.Find(new TextDocument.TextPosition(0, 0), TextMatcher.Literal("absent", ignoreCase: false), false, true, FindSelectMode.SelectOrClear, none, none);
        Check(!found.IsValid, "a text that is not there is not found");
        Check(!target.HasSelection, "and the selection is cleared by that search (SelectOrClear)");

        // The search can be limited to a range.
        found = target.Find(new TextDocument.TextPosition(0, 1), two, false, true, FindSelectMode.None, new TextDocument.TextPosition(0, 1), new TextDocument.TextPosition(9, 1));
        Check(found.IsValid && found.Line == 1, "a search in a range stays in it, also when it wraps");

        // The editor reports the edits that span lines (an edit inside one line is a change of the line, which the search bar never learned of).
        List<string> ranges = new List<string>();
        TextRangeChangedHandler handler = (start, oldEnd, newEnd) => ranges.Add(start.Line + ":" + start.Column + "-" + newEnd.Line + ":" + newEnd.Column);
        target.RangeChanged += handler;
        target.ReplaceText(new TextDocument.TextPosition(7, 0), new TextDocument.TextPosition(7, 0), select: false, "\n");
        Equal(string.Join(",", ranges), "0:7-1:0", "an edit that adds a line is reported with its range");
        Equal(Text(o), "one two\n\nthree two\nfour\n", "and made");
        TextDocument.TextPosition newEnd;
        int count = target.ReplaceAll(two, "2", none, none, out newEnd);
        Equal(count, 2, "replace all counts the matches");
        Equal(Text(o), "one 2\n\nthree 2\nfour\n", "and replaces them");
        target.RangeChanged -= handler;
        int reported = ranges.Count;
        target.ReplaceText(new TextDocument.TextPosition(0, 0), new TextDocument.TextPosition(0, 0), select: false, "\n");
        Equal(ranges.Count, reported, "a handler that was removed is not called");

        int id = target.AddHighlight(new TextDocument.TextPosition(0, 0), new TextDocument.TextPosition(3, 0), new HighlightStyle(RgbColor.FromArgb(40, 90, 140, 255), RgbColor.Empty, RgbColor.Empty));
        Equal(target.SetHighlight(id, new TextDocument.TextPosition(0, 1), new TextDocument.TextPosition(5, 1), new HighlightStyle(RgbColor.FromArgb(40, 90, 140, 255), RgbColor.Empty, RgbColor.Empty)), id, "a highlighted range can be moved and keeps its number");
        target.RemoveHighlight(id);
        target.SetMatchHighlight(two);
        target.SetMatchHighlight(null);
        Check(true, "highlights and the marks of the matches can be set and removed");
        o.Close();
    }

    // The editor as what the status bar below it reads (IEditorStatusSource), on the real editor and panel: every member is the editor's own.
    private static void StatusSource(Server server)
    {
        Console.WriteLine("-- the editor as the source of the status bar (IEditorStatusSource)");
        WriteRemote(server, "status.sh", Bytes("#!/bin/sh\necho one\necho two\n"));
        Opened o = Open(server, "status.sh");
        // The panel is WPF-based and cannot be made under Mono: its StateChanged event is a field of the check.
        EventHandler panelStateChanged = null;
        IEditorStatusSource source = new RemoteFileEditorStatusSource(o.Editor, handler => panelStateChanged += handler, handler => panelStateChanged -= handler);
        int stateChanges = 0;
        int languageChanges = 0;
        source.StateChanged += (sender, e) => stateChanges++;
        source.CurrentLanguageChanged += (sender, e) => languageChanges++;

        Equal(source.RemotePath, o.Path, "the path of the file");
        Check(!source.IsHexMode && !source.IsModified && !source.IsNewFile && !source.IsPreview && !source.IsSaving && !source.IsReadOnlyMode && !source.ReadOnly, "a loaded file: text view, not modified, not new, not a preview, not saving, not read-only");
        Equal(source.GetLineText(1), "echo one", "the text of a line");
        Equal(source.FirstLine, "#!/bin/sh", "the first line");
        Equal(source.GetEffectiveLineTerminator(0), "\n", "the line break of a line");
        Check(source.FileEncoding == o.Editor.FileEncoding && source.LineEndings == LineEndingKind.Lf, "the encoding and the line endings are the editor's");
        Check(source.CanReopenWithEncoding, "the file can be read again in another encoding");
        Equal(source.CurrentLanguage, o.Editor.CurrentLanguage, "the syntax in use is the editor's (the application applies it; here nothing has)");
        Check(!source.SyntaxOverride.HasValue, "and no syntax is chosen by hand");
        o.Editor.ReportLanguage(SyntaxHighlighting.Language.Bash);
        Equal(source.CurrentLanguage, SyntaxHighlighting.Language.Bash, "a syntax that the application applied is read");
        Equal(languageChanges, 1, "and the bar is told");

        Replace(o, 1, "one", "uno");
        Check(source.IsModified, "a change makes the source modified");
        panelStateChanged?.Invoke(null, EventArgs.Empty);
        Equal(stateChanges, 1, "the event of the panel reaches the bar");
        source.SetSyntax(SyntaxHighlighting.Language.Python);
        Check(source.SyntaxOverride == SyntaxHighlighting.Language.Python, "the syntax can be chosen by hand");
        source.SetSyntax(null);
        Check(!source.SyntaxOverride.HasValue, "and null goes back to the detection");
        source.SetLineEnding(TextDocument.LineEnding.CrLf);
        Equal(source.LineEndings, LineEndingKind.CrLf, "the line endings can be changed");
        source.SetLineEnding(TextDocument.LineEnding.Lf);
        source.SetSaveEncoding(TextFileEncodings.Utf8Bom);
        Check(source.FileEncoding == TextFileEncodings.Utf8Bom, "the encoding to save in can be changed");

        // A selection of two lines is measured from the text and the line breaks of its lines.
        o.Editor.TextControl.SetSelection(true, new TextDocument.TextPosition(2, 1));
        o.Editor.TextControl.SetCaret(new TextDocument.TextPosition(4, 2));
        Check(source.HasSelection, "a selection");
        Check(source.SelectionStart.Line == 1 && source.SelectionEnd.Line == 2, "with its two ends");
        Equal(source.CaretPosition.Line + ":" + source.CaretPosition.Column, "2:4", "the caret");

        // The hex view: what the bar reads and the options its menu changes.
        o.Editor.ToggleHexMode();
        Check(source.IsHexMode, "the hex view is on");
        IHexViewStatus hex = source.HexView;
        Equal(hex.Length, o.Editor.HexEditor.Length, "the size of the file in the hex view");
        Check(hex.InsertMode == o.Editor.HexEditor.InsertMode && hex.CaretOffset == o.Editor.HexEditor.CaretOffset && hex.SelectionLength == o.Editor.HexEditor.SelectionLength, "the caret, the selection and the typing mode are the hex editor's");
        hex.BigEndian = true;
        Check(o.Editor.HexEditor.BigEndian && hex.BigEndian, "the byte order can be changed");
        hex.BytesPerRow = 32;
        hex.GroupSize = 4;
        Check(o.Editor.HexEditor.BytesPerRow == 32 && hex.GroupSize == 4, "the layout can be changed");
        hex.ShowDataInspector = true;
        hex.ColorByValue = true;
        Check(o.Editor.HexEditor.ShowDataInspector && o.Editor.HexEditor.ColorByValue, "and the inspector and the colours");
        o.Close();
    }

    private static void Encodings(Server server)
    {
        Console.WriteLine("-- encodings and line endings kept by a save");
        byte[] bom = { 0xEF, 0xBB, 0xBF };
        WriteRemote(server, "bom.txt", Concat(bom, Bytes("one\ntwo\n")));
        Opened o = Open(server, "bom.txt");
        Check(o.Editor.FileEncoding == TextFileEncodings.Utf8Bom, "a UTF-8 file with a byte order mark is recognised");
        Equal(Text(o), "one\ntwo\n", "the byte order mark is not part of the text");
        Replace(o, 0, "one", "ONE");
        Save(o);
        SameBytes(ReadRemote(server, "bom.txt"), Concat(bom, Bytes("ONE\ntwo\n")), "the byte order mark is kept by the save");
        o.Close();

        // A file that is not UTF-8 is opened as ISO-8859-1, which keeps every byte: saving it does not damage the bytes that were not changed.
        Encoding cp1251 = Encoding.GetEncoding(1251);
        string russian = "key=\u041f\u0440\u0438\u0432\u0435\u0442\n\u043c\u0438\u0440\n";
        WriteRemote(server, "cp1251.txt", cp1251.GetBytes(russian));
        o = Open(server, "cp1251.txt");
        Check(o.Editor.FileEncoding == TextFileEncodings.Latin1, "a file that is not UTF-8 is opened as ISO-8859-1");
        Replace(o, 0, "key", "KEY");
        Save(o);
        SameBytes(ReadRemote(server, "cp1251.txt"), cp1251.GetBytes("KEY=\u041f\u0440\u0438\u0432\u0435\u0442\n\u043c\u0438\u0440\n"), "a file opened as ISO-8859-1 keeps the bytes that were not changed");
        o.Close();

        // The user chooses the real encoding: the file is read again with it, and saved with it.
        o = Open(server, "cp1251.txt");
        o.Editor.ReopenWithEncoding(TextFileEncodings.Windows1251);
        Equal(Text(o), "KEY=\u041f\u0440\u0438\u0432\u0435\u0442\n\u043c\u0438\u0440\n", "the file is read as Windows-1251");
        Check(o.Editor.FileEncoding == TextFileEncodings.Windows1251 && !o.Editor.IsModified, "Windows-1251 is the encoding of the file, the editor is not modified");
        Replace(o, 1, "\u043c\u0438\u0440", "\u041c\u0418\u0420");
        Save(o);
        SameBytes(ReadRemote(server, "cp1251.txt"), cp1251.GetBytes("KEY=\u041f\u0440\u0438\u0432\u0435\u0442\n\u041c\u0418\u0420\n"), "a Windows-1251 file stays in Windows-1251 when it is saved");
        o.Close();

        WriteRemote(server, "crlf.txt", Bytes("a\r\nb\r\n"));
        o = Open(server, "crlf.txt");
        Equal(o.Editor.LineEndings, LineEndingKind.CrLf, "CRLF line endings are recognised");
        Replace(o, 1, "b", "B");
        Prompted(server, "crlf save", (text, buttons) => null, () => Save(o));
        NoPrompt(server, "a save of a file that is CRLF as a whole");
        SameBytes(ReadRemote(server, "crlf.txt"), Bytes("a\r\nB\r\n"), "CRLF line endings are kept by the save");
        o.Close();
    }

    private static void ChangeFormat(Server server)
    {
        Console.WriteLine("-- changing the line endings and the encoding");
        WriteRemote(server, "format.txt", Bytes("x\ny\n"));
        Opened o = Open(server, "format.txt");
        o.Editor.SetLineEnding(TextDocument.LineEnding.CrLf);
        Check(o.Editor.IsModified, "changing the line endings modifies the editor");
        Save(o);
        SameBytes(ReadRemote(server, "format.txt"), Bytes("x\r\ny\r\n"), "the file is saved with the line endings that were chosen");
        o.Editor.SetSaveEncoding(TextFileEncodings.Utf8Bom);
        Save(o);
        SameBytes(ReadRemote(server, "format.txt"), Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Bytes("x\r\ny\r\n")), "the file is saved with the encoding that was chosen");
        o.Close();

        byte[] cp = Encoding.GetEncoding(1251).GetBytes("да\n");
        WriteRemote(server, "reopen.txt", cp);
        o = Open(server, "reopen.txt");
        o.Editor.ReopenWithEncoding(TextFileEncodings.Koi8R);
        Equal(Text(o), Encoding.GetEncoding(20866).GetString(cp), "the file is read again in the encoding that was chosen");
        Check(o.Editor.FileEncoding == TextFileEncodings.Koi8R && !o.Editor.IsModified, "the new encoding is the encoding of the file, the editor is not modified");
        o.Close();
    }

    private static void ConvertCrLf(Server server)
    {
        Console.WriteLine("-- questions asked when a file is saved");
        // A file whose line endings are mixed has CRLF in the text, and the editor asks whether to turn them into LF.
        WriteRemote(server, "mixed.sh", Bytes("a\r\nb\nc\n"));
        Opened o = Open(server, "mixed.sh");
        Equal(o.Editor.LineEndings, LineEndingKind.Mixed, "a file with LF and CRLF has mixed line endings");
        Replace(o, 2, "c", "C");
        Prompted(server, "crlf question", (text, buttons) => text.Contains("CRLF") ? DialogResult.Yes : (DialogResult?)null, () => Save(o));
        Check(OnlyPrompt(server, "mixed line endings").Contains("Convert them to LF"), "the editor asks whether to convert the CRLF line endings");
        SameBytes(ReadRemote(server, "mixed.sh"), Bytes("a\nb\nC\n"), "answered Yes: the line endings are LF in the file");
        o.Close();

        WriteRemote(server, "mixed2.sh", Bytes("a\r\nb\nc\n"));
        o = Open(server, "mixed2.sh");
        Replace(o, 2, "c", "C");
        Prompted(server, "crlf question no", (text, buttons) => text.Contains("CRLF") ? DialogResult.No : (DialogResult?)null, () => Save(o));
        SameBytes(ReadRemote(server, "mixed2.sh"), Bytes("a\r\nb\nC\n"), "answered No: the line endings are kept as they were");
        o.Close();

        byte[] bom = { 0xEF, 0xBB, 0xBF };
        WriteRemote(server, "script.sh", Concat(bom, Bytes("#!/bin/sh\necho 1\n")));
        o = Open(server, "script.sh");
        Replace(o, 1, "1", "2");
        Prompted(server, "bom question", (text, buttons) => text.Contains("#!") ? DialogResult.Yes : (DialogResult?)null, () => Save(o));
        Check(OnlyPrompt(server, "a script with a byte order mark").Contains("without the mark"), "a script with a byte order mark: the editor asks whether to save it without the mark");
        SameBytes(ReadRemote(server, "script.sh"), Bytes("#!/bin/sh\necho 2\n"), "answered Yes: the file is saved without the mark");
        o.Close();

        WriteRemote(server, "script2.sh", Concat(bom, Bytes("#!/bin/sh\necho 1\n")));
        o = Open(server, "script2.sh");
        Replace(o, 1, "1", "3");
        Prompted(server, "bom question no", (text, buttons) => text.Contains("#!") ? DialogResult.No : (DialogResult?)null, () => Save(o));
        SameBytes(ReadRemote(server, "script2.sh"), Concat(bom, Bytes("#!/bin/sh\necho 3\n")), "answered No: the mark is kept");
        o.Close();
    }

    private static void NewFile(Server server)
    {
        Console.WriteLine("-- a new file");
        Opened o = Open(server, "created.txt");
        Check(o.Editor.IsNewFile, "a file that does not exist is a new file");
        Equal(Text(o), "", "a new file is empty");
        o.Editor.TextControl.ReplaceText(new TextDocument.TextPosition(0, 0), new TextDocument.TextPosition(0, 0), select: false, "first line\nsecond line\n");
        Prompted(server, "new file", (text, buttons) => null, () => Save(o));
        NoPrompt(server, "saving a new file");
        SameBytes(ReadRemote(server, "created.txt"), Bytes("first line\nsecond line\n"), "a new file is created by the save");
        o.Close();
    }

    private static void Permissions(Server server)
    {
        Console.WriteLine("-- the mode of the file and the backup copy");
        WriteRemote(server, "secret.txt", Bytes("token\n"));
        Remote(server, "chmod 640 '" + server.Directory + "/secret.txt'");
        Opened o = Open(server, "secret.txt");
        Replace(o, 0, "token", "TOKEN");
        Save(o);
        Equal(Mode(server, "secret.txt"), "640", "a save keeps the mode of the file");
        SameBytes(ReadRemote(server, "secret.txt"), Bytes("TOKEN\n"), "the saved text is on the server");
        Check(ReadRemote(server, "secret.txt~") == null, "no backup copy is made when the setting is off");
        o.Close();

        WriteRemote(server, "backed.txt", Bytes("old\n"));
        o = Open(server, "backed.txt", settings => settings.Editor.BackupBeforeSave = true);
        Replace(o, 0, "old", "new");
        Save(o);
        SameBytes(ReadRemote(server, "backed.txt"), Bytes("new\n"), "the text is saved");
        SameBytes(ReadRemote(server, "backed.txt~"), Bytes("old\n"), "the old text is kept in the backup copy");
        Replace(o, 0, "new", "newer");
        Save(o);
        SameBytes(ReadRemote(server, "backed.txt~"), Bytes("old\n"), "the backup copy is made once, when the file is first saved");
        o.Close();
    }

    private static void Conflicts(Server server)
    {
        Console.WriteLine("-- a file that changed on the server");
        WriteRemote(server, "conflict.txt", Bytes("mine\n"));
        Opened o = Open(server, "conflict.txt");
        // The size and the time are what tells that the file changed: make sure that they are not the same.
        Thread.Sleep(1100);
        WriteRemote(server, "conflict.txt", Bytes("theirs, and longer\n"));
        Replace(o, 0, "mine", "MINE");
        Prompted(server, "conflict no", (text, buttons) => text.Contains("Overwrite") ? DialogResult.No : (DialogResult?)null, () =>
        {
            try
            {
                Save(o);
                Check(false, "a save that the user refused throws");
            }
            catch (OperationCanceledException)
            {
                Check(true, "a save that the user refused is cancelled");
            }
        });
        Check(OnlyPrompt(server, "conflict").Contains("Overwrite it with your version?"), "the editor asks whether to overwrite the changed file");
        SameBytes(ReadRemote(server, "conflict.txt"), Bytes("theirs, and longer\n"), "answered No: the file on the server is not touched");
        Check(o.Editor.IsModified, "the editor keeps its changes");
        Prompted(server, "conflict yes", (text, buttons) => text.Contains("Overwrite") ? DialogResult.Yes : (DialogResult?)null, () => Save(o));
        SameBytes(ReadRemote(server, "conflict.txt"), Bytes("MINE\n"), "answered Yes: the file is overwritten");
        o.Close();

        Console.WriteLine("-- a file that changed on the server, saved in the background");
        WriteRemote(server, "bg.txt", Bytes("mine\n"));
        o = Open(server, "bg.txt");
        Thread.Sleep(1100);
        WriteRemote(server, "bg.txt", Bytes("theirs, and longer\n"));
        Replace(o, 0, "mine", "MINE");
        Prompted(server, "bg conflict cancel", (text, buttons) => buttons == MessageBoxButtons.YesNoCancel ? DialogResult.Cancel : (DialogResult?)null, () => SaveAndWait(o));
        Check(OnlyPrompt(server, "background conflict").Contains("Cancel: do not save."), "a background save asks: overwrite, load the server's version, or cancel");
        SameBytes(ReadRemote(server, "bg.txt"), Bytes("theirs, and longer\n"), "answered Cancel: the file is not touched");
        Check(o.Editor.IsModified, "answered Cancel: the changes stay in the editor");
        Prompted(server, "bg conflict yes", (text, buttons) => buttons == MessageBoxButtons.YesNoCancel ? DialogResult.Yes : (DialogResult?)null, () => SaveAndWait(o));
        Pump(() => !o.Editor.IsModified, "the overwrite that was answered Yes");
        SameBytes(ReadRemote(server, "bg.txt"), Bytes("MINE\n"), "answered Yes: the save runs again and overwrites the file");
        o.Close();

        WriteRemote(server, "bg2.txt", Bytes("mine\n"));
        o = Open(server, "bg2.txt");
        Thread.Sleep(1100);
        WriteRemote(server, "bg2.txt", Bytes("theirs, and longer\n"));
        Replace(o, 0, "mine", "MINE");
        Prompted(server, "bg conflict no", (text, buttons) => buttons == MessageBoxButtons.YesNoCancel ? DialogResult.No : (DialogResult?)null, () => SaveAndWait(o));
        Pump(() => Text(o) == "theirs, and longer\n", "the version of the server to be loaded");
        Check(!o.Editor.IsModified, "answered No: the server's version is loaded and the changes are given up");
        SameBytes(ReadRemote(server, "bg2.txt"), Bytes("theirs, and longer\n"), "answered No: the file on the server is the other version");
        o.Close();
    }

    private static void BackgroundSave(Server server)
    {
        Console.WriteLine("-- a save in the background");
        WriteRemote(server, "background.txt", Bytes("a\nb\n"));
        Opened o = Open(server, "background.txt");
        int changes = 0;
        o.Editor.SavingChanged += (sender, e) => changes++;
        Replace(o, 0, "a", "A");
        Prompted(server, "background", (text, buttons) => null, () => SaveAndWait(o));
        NoPrompt(server, "a background save");
        SameBytes(ReadRemote(server, "background.txt"), Bytes("A\nb\n"), "a background save writes the file");
        Check(!o.Editor.IsModified && !o.Editor.IsSaving, "after it the editor is not modified and not saving");
        Equal(changes, 2, "the editor says that it started and that it finished saving");

        // A change made while it saves is saved by the next save, and the file ends up with the last text.
        Replace(o, 1, "b", "B");
        o.Editor.SaveInBackground();
        Check(o.Editor.IsSaving, "a background save is running");
        Replace(o, 0, "A", "AA");
        o.Editor.SaveInBackground();
        Pump(() => !o.Editor.IsSaving && !o.Editor.IsModified, "both saves to end", 60);
        SameBytes(ReadRemote(server, "background.txt"), Bytes("AA\nB\n"), "a save asked for while one runs makes the file the last text");
        o.Close();
    }

    private static void Reloading(Server server)
    {
        Console.WriteLine("-- loading the file again");
        WriteRemote(server, "reload.txt", Bytes("v1\n"));
        Opened o = Open(server, "reload.txt");
        Thread.Sleep(1100);
        WriteRemote(server, "reload.txt", Bytes("version 2\n"));
        o.Editor.Reload();
        Pump(() => Text(o) == "version 2\n", "the file to be loaded again");
        Settle(server);
        Check(!o.Editor.IsModified && o.Editor.CanSave, "after Reload the new text is shown, not modified");
        Replace(o, 0, "version 2", "version 3");
        Save(o);
        SameBytes(ReadRemote(server, "reload.txt"), Bytes("version 3\n"), "a save after Reload writes the file");
        o.Close();
    }

    private static void Validation(Server server)
    {
        Console.WriteLine("-- the check of a shell script before it is saved");
        WriteRemote(server, "ok.sh", Bytes("#!/bin/sh\necho start\n"));
        Opened o = Open(server, "ok.sh");
        Replace(o, 1, "start", "finish");
        Prompted(server, "valid script", (text, buttons) => null, () => SaveAndWait(o));
        NoPrompt(server, "a script without a problem");
        SameBytes(ReadRemote(server, "ok.sh"), Bytes("#!/bin/sh\necho finish\n"), "a script that passes the check is saved");

        Replace(o, 1, "echo finish", "if true; then");
        Prompted(server, "broken script no", (text, buttons) => text.Contains("The check (") ? DialogResult.No : (DialogResult?)null, () => SaveAndWait(o));
        string question = OnlyPrompt(server, "a script with a syntax error");
        Check(question.Contains("found 1 problem") && question.Contains("The file on the server has not been changed."), "the editor tells what the check found and that the file was not changed");
        Check(question.Contains("Yes: save it anyway.") && question.Contains("No: do not save, go to line "), "the editor offers to save anyway, or to go to the line of the problem");
        SameBytes(ReadRemote(server, "ok.sh"), Bytes("#!/bin/sh\necho finish\n"), "answered No: the file on the server is the one it was");
        Check(o.Editor.IsModified, "answered No: the changes stay in the editor");

        Prompted(server, "broken script yes", (text, buttons) => text.Contains("The check (") ? DialogResult.Yes : (DialogResult?)null, () => SaveAndWait(o));
        Pump(() => !o.Editor.IsModified && !o.Editor.IsSaving, "the save that was answered Yes");
        SameBytes(ReadRemote(server, "ok.sh"), Bytes("#!/bin/sh\nif true; then\n"), "answered Yes: the file is saved with the problem");
        Settle(server);
        o.Close();

        // Saving when a tab closes does not run the check: the user is leaving and cannot act on what it finds.
        WriteRemote(server, "closing.sh", Bytes("#!/bin/sh\necho 1\n"));
        o = Open(server, "closing.sh");
        Replace(o, 1, "echo 1", "if true; then");
        Prompted(server, "closing", (text, buttons) => null, () => Save(o));
        NoPrompt(server, "a save that is not a background save");
        SameBytes(ReadRemote(server, "closing.sh"), Bytes("#!/bin/sh\nif true; then\n"), "a save made when the tab closes does not run the check");
        o.Close();
    }

    private static void ServiceReload(Server server)
    {
        Console.WriteLine("-- the offer to reload the service that reads the file");
        Remote(server, "mkdir -p '" + server.Directory + "/etc/nginx'");
        WriteRemote(server, "etc/nginx/app.conf", Bytes("# one\n"));
        Opened o = Open(server, "etc/nginx/app.conf", settings => settings.Editor.DisableConfigCheck = true);
        Replace(o, 0, "one", "two");
        Prompted(server, "reload", (text, buttons) => text.Contains("Reload nginx") ? DialogResult.No : (DialogResult?)null, () => SaveAndWait(o));
        string offer = OnlyPrompt(server, "a file of /etc/nginx");
        Check(offer.Contains("was saved.") && offer.Contains("Reload nginx now so that the change takes effect?"), "after the save the editor offers to reload nginx");
        SameBytes(ReadRemote(server, "etc/nginx/app.conf"), Bytes("# two\n"), "the file is saved before the offer");
        o.Close();

        WriteRemote(server, "etc/nginx/other.conf", Bytes("# one\n"));
        o = Open(server, "etc/nginx/other.conf", settings => { settings.Editor.DisableConfigCheck = true; settings.Editor.DisableServiceReloadOffer = true; });
        Replace(o, 0, "one", "two");
        Prompted(server, "no reload offer", (text, buttons) => null, () => SaveAndWait(o));
        NoPrompt(server, "a save with the offer switched off");
        o.Close();

        WriteRemote(server, "elsewhere.conf", Bytes("# one\n"));
        o = Open(server, "elsewhere.conf", settings => settings.Editor.DisableConfigCheck = true);
        Replace(o, 0, "one", "two");
        Prompted(server, "elsewhere", (text, buttons) => null, () => SaveAndWait(o));
        NoPrompt(server, "a .conf file outside /etc/nginx");
        o.Close();
    }

    private static void Drafts(Server server)
    {
        Console.WriteLine("-- the draft of unsaved changes");
        WriteRemote(server, "draft.txt", Bytes("base\nline\n"));
        Opened o = Open(server, "draft.txt");
        Replace(o, 0, "base", "BASE edited");
        // The draft is taken by a timer (every ten seconds) and written on another thread.
        Stopwatch wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(13))
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
        o.Close();
        Thread.Sleep(500);
        SameBytes(ReadRemote(server, "draft.txt"), Bytes("base\nline\n"), "the file on the server is not changed by the draft");

        o = null;
        Prompted(server, "draft yes", (text, buttons) => text.Contains("Unsaved changes of") ? DialogResult.Yes : (DialogResult?)null, () => o = Open(server, "draft.txt"));
        string question = OnlyPrompt(server, "a file with a draft");
        Check(question.Contains("Yes: restore the draft.") && question.Contains("No: discard it and open the file from the server."), "the editor offers the draft of the changes that were not saved");
        Equal(Text(o), "BASE edited\nline\n", "answered Yes: the text of the draft is in the editor");
        Check(o.Editor.IsModified, "answered Yes: the restored text is an unsaved change");
        o.Editor.DiscardDraft();
        o.Close();
        Thread.Sleep(500);

        Prompted(server, "draft gone", (text, buttons) => null, () => o = Open(server, "draft.txt"));
        NoPrompt(server, "a file whose draft was discarded");
        Equal(Text(o), "base\nline\n", "after the draft was discarded the file is shown as it is on the server");
        o.Close();
    }

    // What the editor told, in order: the modified state (its ModifiedChanged event), the view (HexModeChanged) and the format (FormatChanged).
    private static List<string> RecordEvents(RemoteFileEditor editor)
    {
        List<string> events = new List<string>();
        editor.TextControl.ModifiedChanged += (sender, modified) => events.Add("modified:" + modified);
        editor.HexModeChanged += (sender, e) => events.Add("hex:" + editor.IsHexMode);
        editor.FormatChanged += (sender, e) => events.Add("format");
        return events;
    }

    private static string Events(List<string> events)
    {
        string text = string.Join(",", events);
        events.Clear();
        return text;
    }

    private static string Caret(RemoteFileEditor editor)
    {
        return editor.TextControl.CaretPosition.Line + ":" + editor.TextControl.CaretPosition.Column;
    }

    // The switching between the text and the hex view (stage 3, step 3A: the net before it moves out of the control): what each view shows, which
    // one can be edited, what the editor tells and in which order, the undo history and the caret kept, the question about bytes the text cannot
    // hold, saving in either view, a reload and a new encoding while the hex view is open, and the commands that go to the hex view.
    private static void HexView(Server server)
    {
        Console.WriteLine("-- the hex view: switching, editing, saving, reloading");
        WriteRemote(server, "hex.txt", Bytes("abc\ndef\n"));
        Opened o = Open(server, "hex.txt");
        RemoteFileEditor e = o.Editor;
        List<string> events = RecordEvents(e);
        Check(e.CanToggleHexMode && !e.IsHexMode, "a loaded file can be shown as bytes");

        e.ToggleHexMode();
        Equal(Events(events), "modified:False,hex:True,format", "opening the hex view tells the modified state, the view and the format, in this order");
        Check(e.IsHexMode && e.HexEditorControl.Visible && !e.HexEditor.ReadOnly && e.TextControl.ReadOnly, "the hex view is shown and can be edited, the text is read-only");
        SameBytes(e.HexEditor.Document.ToArray(), Bytes("abc\ndef\n"), "the hex view shows the bytes of the file");
        Check(!e.IsModified, "a file that was not changed is not modified in the hex view");
        e.ToggleHexMode();
        Equal(Events(events), "modified:False,hex:False,format", "closing it tells the same, in the same order");
        Check(!e.IsHexMode && !e.HexEditorControl.Visible && e.HexEditor.ReadOnly && !e.TextControl.ReadOnly, "the text is shown and can be edited again, the hex view is read-only");
        Equal(Text(o), "abc\ndef\n", "the text is the one before");
        Check(!e.IsModified, "and it is not modified");

        // An edited text: the hex view shows its bytes, and going back keeps the text, its undo history and the caret.
        Replace(o, 0, "abc", "ABC");
        e.TextControl.SetCaret(new TextDocument.TextPosition(2, 1));
        events.Clear();
        e.ToggleHexMode();
        Equal(Events(events), "modified:True,hex:True,format", "the hex view of an edited text is modified");
        SameBytes(e.HexEditor.Document.ToArray(), Bytes("ABC\ndef\n"), "an edited text is shown as its bytes in the encoding of the file");
        HexDocument shown = e.HexEditor.Document;
        e.ToggleHexMode();
        Equal(Events(events), "modified:True,hex:False,format", "back in the text: modified");
        Equal(Text(o), "ABC\ndef\n", "the edited text is back");
        Check(e.IsModified && e.TextControl.CanUndo, "it is modified and its undo history is kept");
        Equal(Caret(e), "1:2", "the caret is where it was");

        // The same text again: the same hex document, with its own undo history. A change of the bytes makes a new text when the view closes:
        // its line breaks are found again, the caret is brought into the shorter line, and the undo history of the text is gone.
        e.TextControl.SetCaret(new TextDocument.TextPosition(3, 1));
        e.ToggleHexMode();
        Check(ReferenceEquals(e.HexEditor.Document, shown), "the hex view of a text that did not change is the same document");
        e.HexEditor.Document.Replace(4, 3, Bytes("D\r"));
        Check(e.IsModified && e.HexEditor.Document.CanUndo, "a change of the bytes is modified and can be undone");
        events.Clear();
        e.ToggleHexMode();
        Equal(Events(events), "modified:True,hex:False,format", "back in the text after a change of the bytes");
        Equal(Text(o), "ABC\nD\r\n", "the text is made of the changed bytes");
        Equal(e.LineEndings, LineEndingKind.Mixed, "and its line breaks are found again (LF and CRLF)");
        Check(e.IsModified && !e.TextControl.CanUndo, "it is modified and is a new document, without the undo history of the text");
        Equal(Caret(e), "1:1", "the caret is brought into the line, which is shorter now");

        // A byte that is not UTF-8: the text could not hold it, so the editor asks.
        e.ToggleHexMode();
        Check(ReferenceEquals(e.HexEditor.Document, shown), "the hex view of the text made from it is that document again");
        e.HexEditor.Document.ReplaceByte(0, 0xFF);
        Prompted(server, "lossy no", (text, buttons) => text.Contains("Not all bytes") ? DialogResult.No : (DialogResult?)null, () => e.ToggleHexMode());
        string lossy = OnlyPrompt(server, "bytes that are not UTF-8");
        Check(lossy.Contains("Not all bytes can be shown as text in UTF-8") && lossy.Contains("Show the bytes as text anyway?"), "the editor says that the bytes cannot be shown as text and asks");
        Check(e.IsHexMode && e.HexEditorControl.Visible, "answered No: the hex view stays");
        Prompted(server, "lossy yes", (text, buttons) => text.Contains("Not all bytes") ? DialogResult.Yes : (DialogResult?)null, () => e.ToggleHexMode());
        Check(!e.IsHexMode, "answered Yes: the text is shown");
        Equal(Text(o), "\uFFFDBC\nD\r\n", "the byte that is not UTF-8 is shown as U+FFFD");
        // Saved before it is edited, the text is written as the bytes of the hex view, and no question about the CRLF is asked.
        Prompted(server, "exact bytes", (text, buttons) => null, () => Save(o));
        NoPrompt(server, "a save of the text made from the hex view");
        SameBytes(ReadRemote(server, "hex.txt"), Concat(new byte[] { 0xFF }, Bytes("BC\nD\r\n")), "the file gets the bytes of the hex view, not the text");
        Check(!e.IsModified, "the text is saved");
        e.ToggleHexMode();
        Check(ReferenceEquals(e.HexEditor.Document, shown) && !e.IsModified, "the hex view of those bytes is saved too");

        // A save in the hex view writes its bytes.
        e.HexEditor.Document.ReplaceByte(0, (byte)'a');
        Check(e.IsModified, "a changed byte modifies the editor");
        Prompted(server, "hex save", (text, buttons) => null, () => Save(o));
        NoPrompt(server, "a save in the hex view");
        SameBytes(ReadRemote(server, "hex.txt"), Bytes("aBC\nD\r\n"), "a save in the hex view writes its bytes");
        Check(e.IsHexMode && !e.IsModified, "the hex view stays and is saved");
        e.IsModified = true;
        Check(e.IsModified && e.HexEditor.Document.IsModified, "the modified state that is set goes to the hex document");
        e.IsModified = false;
        Check(!e.IsModified && !e.HexEditor.Document.IsModified, "and so does the saved state");
        e.ToggleHexMode();
        // Recorded as it is (a fault, see the pull request of 3A): the bytes are those of the file, so the text from before the hex view is shown.
        Equal(Text(o), "\uFFFDBC\nD\r\n", "back in the text after a save in the hex view: the text from before the hex view");
        Check(!e.IsModified, "not modified");

        // A reload started in the hex view opens it again on the new bytes, at the same offset.
        e.ToggleHexMode();
        e.HexEditor.GoToOffset(3);
        Thread.Sleep(1100);
        WriteRemote(server, "hex.txt", Bytes("reloaded bytes\n"));
        events.Clear();
        e.Reload();
        Check(e.HexEditor.ReadOnly && e.TextControl.ReadOnly && !e.CanSave && !e.CanToggleHexMode, "while the file is loaded again neither view can be edited");
        Pump(() => e.CanSave, "the file to be loaded again");
        Settle(server);
        Check(e.IsHexMode && e.HexEditorControl.Visible && !e.HexEditor.ReadOnly, "the hex view is open again");
        SameBytes(e.HexEditor.Document.ToArray(), Bytes("reloaded bytes\n"), "on the bytes that are on the server now");
        Equal(e.HexEditor.CaretOffset, 3, "at the same offset");
        Check(!e.IsModified, "not modified");
        Equal(Text(o), "reloaded bytes\n", "the text behind it is the new file");
        Equal(Events(events), "format,hex:False,format,modified:False,hex:True,format", "a reload in the hex view tells the format, the text view, the format, then the modified state, the hex view and the format");

        // The encoding and the line breaks are not changed in the hex view; Select all and Delete are the hex view's.
        e.SetSaveEncoding(TextFileEncodings.Utf8Bom);
        e.SetLineEnding(TextDocument.LineEnding.CrLf);
        Check(e.FileEncoding == TextFileEncodings.Utf8 && e.LineEndings == LineEndingKind.Lf && !e.IsModified, "the encoding and the line breaks cannot be changed in the hex view");
        e.SelectAllText();
        Equal(e.HexEditor.SelectionLength, 15, "Select all selects the bytes");
        e.DeleteSelection();
        Equal(e.HexEditor.Document.Length, 0, "Delete removes them");
        Check(e.IsModified, "which modifies the editor");
        e.HexEditor.Document.Undo();
        Check(!e.IsModified && e.HexEditor.Document.Length == 15, "undo in the hex view brings them back");

        // Reading the file again in another encoding leaves the hex view.
        events.Clear();
        e.ReopenWithEncoding(TextFileEncodings.Windows1251);
        Equal(Events(events), "hex:False,format", "a new encoding in the hex view tells the text view and the format");
        Check(!e.IsHexMode && !e.HexEditorControl.Visible && e.HexEditor.Length == 0 && !ReferenceEquals(e.HexEditor.Document, shown), "and forgets the hex view (it shows an empty document)");
        Check(e.FileEncoding == TextFileEncodings.Windows1251 && !e.IsModified, "the file is read in the encoding that was chosen");

        // Not here: a text that the encoding cannot store (the editor says so and stays in the text), because the encodings of Mono do not
        // throw for such a letter; RemoteFileContentTests has it.
        o.Close();
    }

    // Where a hex view given to the editor is shown (the tab of the application): what the editor asks it.
    private sealed class FakeHexPlace : IHexViewPlace
    {
        public readonly List<string> Calls = new List<string>();

        public void ShowHexView()
        {
            Calls.Add("show");
        }

        public void HideHexView()
        {
            Calls.Add("hide");
        }

        public void SetAppearance(RgbColor background, RgbColor foreground, string fontFamily, float fontPoints)
        {
            Calls.Add("look " + background.ToArgb().ToString("x8", CultureInfo.InvariantCulture) + " " + foreground.ToArgb().ToString("x8", CultureInfo.InvariantCulture)
                + " " + fontFamily + " " + fontPoints.ToString("0.##", CultureInfo.InvariantCulture));
        }

        public string Take()
        {
            string text = string.Join(",", Calls);
            Calls.Clear();
            return text;
        }
    }

    // A hex view given to the editor instead of its own WinForms control (stage 3B-3: the WPF view of the tab), shown through a place: the editor
    // asks the place to show and hide it where it showed and hid its own, gives it the colours and the font of the text (at its zoom), and the
    // view does what the editor's own did. A WinForms view stands in for the WPF one, which Mono cannot make.
    private static void HexPlace(Server server)
    {
        Console.WriteLine("-- a hex view given to the editor and shown by its place");
        WriteRemote(server, "place.txt", Bytes("abc\ndef\n"));
        GlobalSettings settings = new GlobalSettings();
        Form form = new Form { Width = 800, Height = 600 };
        string path = server.Directory + "/place.txt";
        HexEditorControl given = new HexEditorControl { Dock = DockStyle.Fill };
        RemoteFileEditor e = new RemoteFileEditor(server.Context, path, settings, new TextDocument.TextPosition(), null, ColumnUnit.Characters, given);
        e.TextControl.Dock = DockStyle.Fill;
        Check(ReferenceEquals(e.HexEditor, given), "the hex view of the editor is the one it was given");
        Check(e.HexEditorControl == null, "and it has no WinForms one of its own");
        Check(!e.TextControl.Controls.Contains(given), "the given view is not put among the editor's controls");
        FakeHexPlace place = new FakeHexPlace();
        e.HexViewPlace = place;
        string look = place.Take();
        // The font of the text is Courier New of 12 points (Mono may give another family for that name).
        string family = e.TextControl.LogicalFont.FontFamily.Name;
        Check(e.TextControl.LogicalFont.SizeInPoints == 12f, "the font of the text has 12 points");
        Check(look.StartsWith("look ", StringComparison.Ordinal) && look.EndsWith(" " + family + " 12", StringComparison.Ordinal), "the place gets the colours and the font of the text when it is set (" + look + ")");
        form.Controls.Add(given);
        form.Controls.Add(e.TextControl);
        form.Show();
        Opened o = new Opened { Editor = e, Form = form, Server = server, Path = path };
        Pump(() => e.CanSave, "the file to load");
        Settle(server);
        place.Calls.RemoveAll(call => call.StartsWith("look ", StringComparison.Ordinal));
        Equal(place.Take(), "hide", "loading the file hides the hex view, as it did the editor's own");

        e.TextControl.BackColor = System.Drawing.Color.FromArgb(10, 20, 30);
        look = place.Take();
        Check(look.StartsWith("look ff0a141e ", StringComparison.Ordinal), "a new background goes to the place (" + look + ")");
        e.TextControl.ForeColor = System.Drawing.Color.FromArgb(200, 210, 220);
        look = place.Take();
        Check(look.EndsWith("look ff0a141e ffc8d2dc " + family + " 12", StringComparison.Ordinal), "a new text colour too (" + look + ")");
        e.TextControl.FontZoom = 1.5f;
        look = place.Take();
        Check(look.EndsWith(" " + family + " 18", StringComparison.Ordinal), "the zoom of the text goes with the font (" + look + ")");

        List<string> events = RecordEvents(e);
        e.ToggleHexMode();
        Equal(place.Take(), "show", "opening the hex view asks the place to show it");
        Equal(Events(events), "modified:False,hex:True,format", "and tells what the editor tells with its own view");
        Check(e.IsHexMode && !given.ReadOnly && e.TextControl.ReadOnly, "the given view can be edited, the text cannot");
        SameBytes(given.Document.ToArray(), Bytes("abc\ndef\n"), "the given view shows the bytes of the file");
        given.Document.ReplaceByte(0, (byte)'A');
        Check(e.IsModified, "a change in the given view modifies the editor");
        e.SelectAllText();
        Equal(given.SelectionLength, 8, "Select all of the editor selects the bytes of the given view");
        given.GoToOffset(0);
        SaveAndWait(o);
        SameBytes(ReadRemote(server, "place.txt"), Bytes("Abc\ndef\n"), "a save in the given view writes its bytes");
        Check(!e.IsModified && !given.IsModified, "and both are saved");

        given.GoToOffset(2);
        Thread.Sleep(1100);
        WriteRemote(server, "place.txt", Bytes("reloaded\n"));
        e.Reload();
        Pump(() => e.CanSave, "the file to be loaded again");
        Settle(server);
        Equal(place.Take(), "hide,show", "a reload in the hex view hides it and shows it again");
        SameBytes(given.Document.ToArray(), Bytes("reloaded\n"), "on the new bytes");
        Equal(given.CaretOffset, 2, "at the same offset");

        e.ToggleHexMode();
        Equal(place.Take(), "hide", "closing the hex view asks the place to hide it");
        Check(!e.IsHexMode && given.ReadOnly && !e.TextControl.ReadOnly, "the given view is read-only again, the text can be edited");
        Equal(Text(o), "reloaded\n", "the text is the file");
        o.Close();
    }

    // The draft of the bytes of the hex view comes back in the hex view.
    private static void HexDraft(Server server)
    {
        Console.WriteLine("-- the draft of the hex view");
        WriteRemote(server, "hexdraft.bin", Bytes("0123\n"));
        Opened o = Open(server, "hexdraft.bin");
        o.Editor.ToggleHexMode();
        o.Editor.HexEditor.Document.ReplaceByte(1, 0x80);
        Stopwatch wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(13))
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
        o.Close();
        Thread.Sleep(500);
        o = null;
        Prompted(server, "hex draft yes", (text, buttons) => text.Contains("Unsaved changes of") ? DialogResult.Yes : (DialogResult?)null, () => o = Open(server, "hexdraft.bin"));
        OnlyPrompt(server, "a file with a draft of the hex view");
        Check(o.Editor.IsHexMode && o.Editor.HexEditorControl.Visible, "answered Yes: the draft of the bytes is shown in the hex view");
        SameBytes(o.Editor.HexEditor.Document.ToArray(), new byte[] { (byte)'0', 0x80, (byte)'2', (byte)'3', (byte)'\n' }, "with the bytes of the draft");
        Check(o.Editor.IsModified, "as an unsaved change");
        Equal(Text(o), "0123\n", "the text behind it is the file");
        o.Editor.DiscardDraft();
        o.Close();
        Thread.Sleep(500);
    }

    // The paths of the loading and the saves that the other scenarios do not take (stage 3, step 3A-2: the net before the state and the
    // decisions of the loading and the saves move out of the control): before the file is loaded, while it is loaded again, a reload that
    // fails, a refused write and the question about sudo, a service reload that fails, the line of a problem of the check, the caret of a
    // compiler message.
    private static void Flow(Server server)
    {
        Console.WriteLine("-- the loading and the saves: before the load, during a reload, failures");
        WriteRemote(server, "flow.txt", Bytes("one\n\tb=1\nthree\n"));

        // Before the file is loaded (the loading thread posts its end to the UI thread, which has not run yet): nothing can be saved, and
        // closing the tab has nothing of the user's to save. The caret of a compiler message (gcc: screen columns, tab stops of 8) is put
        // on the character it names once the text is in.
        GlobalSettings settings = new GlobalSettings();
        Form form = new Form { Width = 800, Height = 600 };
        string path = server.Directory + "/flow.txt";
        RemoteFileEditor e = new RemoteFileEditor(server.Context, path, settings, new TextDocument.TextPosition(8, 1), null, ColumnUnit.TabStops);
        e.TextControl.Dock = DockStyle.Fill;
        form.Controls.Add(e.TextControl);
        form.Show();
        Check(!e.CanSave && !e.CanToggleHexMode && !e.CanReopenWithEncoding && !e.CanFollowLog && !e.CanRunInTerminal, "before the file is loaded nothing can be saved, switched, read again, followed or run");
        int savingChanges = 0;
        e.SavingChanged += (sender, args) => savingChanges++;
        Prompted(server, "before the load", (text, buttons) => null, () =>
        {
            Check(e.TrySave(), "closing the tab before the file arrived: nothing to save, the tab may close");
            e.Save();
            e.SaveInBackground();
            e.Reload();
        });
        NoPrompt(server, "saves and a reload before the file is loaded");
        Check(!e.IsSaving && savingChanges == 0, "no save was started");
        Opened o = new Opened { Editor = e, Form = form, Server = server, Path = path };
        Pump(() => e.CanSave, "the file to load");
        Settle(server);
        Equal(Caret(e), "1:1", "the column of a compiler message in screen columns is the character after the tab");
        Check(e.CanToggleHexMode && e.CanReopenWithEncoding && e.CanFollowLog && !e.CanRunInTerminal, "a loaded text file: switched, read again, followed, not run");

        // While the file is loaded again the text cannot be saved: a save on close says so, and the caret comes back where it was.
        List<string> events = RecordEvents(e);
        e.TextControl.SetCaret(new TextDocument.TextPosition(2, 2));
        Thread.Sleep(1100);
        WriteRemote(server, "flow.txt", Bytes("one\n\tb=2\nthree, longer\n"));
        e.Reload();
        Check(!e.CanSave && !e.CanToggleHexMode && !e.CanReopenWithEncoding && !e.CanFollowLog && e.TextControl.ReadOnly, "while the file is loaded again: read-only, nothing can be saved or switched");
        try
        {
            e.Save();
            Check(false, "a save during a reload throws");
        }
        catch (InvalidOperationException exception)
        {
            Check(exception.Message == "The file " + path + " is being reloaded. Try again when it has been loaded.", "a save during a reload says that the file is being reloaded");
        }
        Prompted(server, "close during reload", (text, buttons) => text.Contains("is being reloaded") ? DialogResult.OK : (DialogResult?)null, () => Check(!e.TrySave(), "closing the tab during a reload: the tab stays"));
        Check(OnlyPrompt(server, "a save on close during a reload").Contains("is being reloaded"), "and the error is shown");
        e.SaveInBackground();
        Check(!e.IsSaving, "a background save during a reload does not start");
        e.Reload();
        Pump(() => e.CanSave, "the file to be loaded again");
        Settle(server);
        Equal(Text(o), "one\n\tb=2\nthree, longer\n", "the new text");
        Equal(Caret(e), "2:2", "the caret is where it was");
        Equal(Events(events), "format,format", "a reload tells the format when it starts and when the text is in (a second Reload meanwhile does nothing)");

        // A reload that fails keeps the text of the user and the tab.
        Replace(o, 0, "one", "ONE");
        Remote(server, "chmod 000 '" + path + "'");
        events.Clear();
        Prompted(server, "failed reload", (text, buttons) => buttons == MessageBoxButtons.OK ? DialogResult.OK : (DialogResult?)null, () =>
        {
            e.Reload();
            Pump(() => !e.TextControl.ReadOnly, "the reload to fail");
        });
        Remote(server, "chmod 644 '" + path + "'");
        Settle(server);
        OnlyPrompt(server, "a reload that fails");
        Check(e.CanSave && !e.TextControl.ReadOnly && e.IsModified, "after a reload that failed the text can be edited and saved, and it is still modified");
        Equal(Text(o), "ONE\n\tb=2\nthree, longer\n", "the text of the user is kept");
        Equal(Events(events), "format,format", "the format is told when the reload starts and when it failed");

        // A file the user may not write: the replacement is refused and the editor asks about sudo.
        Remote(server, "chmod 444 '" + path + "'");
        Prompted(server, "sudo no", (text, buttons) => text.Contains("Try again with sudo?") ? DialogResult.No : (DialogResult?)null, () =>
        {
            try
            {
                e.Save();
                Check(false, "a save whose sudo question was answered No throws");
            }
            catch (OperationCanceledException)
            {
                Check(true, "a save whose sudo question was answered No is cancelled");
            }
        });
        Equal(OnlyPrompt(server, "a refused write"), "Failed to overwrite " + path + ". Try again with sudo?", "the editor asks whether to try again with sudo");
        Check(e.IsModified, "the changes stay in the editor");
        Prompted(server, "sudo no on close", (text, buttons) => text.Contains("Try again with sudo?") ? DialogResult.No : (DialogResult?)null, () => Check(!e.TrySave(), "closing the tab with the question answered No: the tab stays"));
        OnlyPrompt(server, "a refused write on close");
        Prompted(server, "sudo no in the background", (text, buttons) => text.Contains("Try again with sudo?") ? DialogResult.No : (DialogResult?)null, () => SaveAndWait(o));
        OnlyPrompt(server, "a refused write in the background");
        Check(e.IsModified && !e.IsSaving, "a background save answered No: still modified, not saving");
        Remote(server, "chmod 644 '" + path + "'");
        SameBytes(ReadRemote(server, "flow.txt"), Bytes("one\n\tb=2\nthree, longer\n"), "the file on the server is not touched");
        o.Close();

        // The check found a problem and the user does not save: the caret goes to its line.
        WriteRemote(server, "flowcheck.sh", Bytes("#!/bin/sh\necho 1\necho 2\n"));
        o = Open(server, "flowcheck.sh");
        Replace(o, 2, "echo 2", "if true; then");
        Prompted(server, "check no", (text, buttons) => text.Contains("The check (") ? DialogResult.No : (DialogResult?)null, () => SaveAndWait(o));
        OnlyPrompt(server, "a script with a problem");
        Equal(o.Editor.TextControl.CaretPosition.Line, 3, "answered No: the caret is on the line the check named");
        o.Close();

        // A service reload that fails: the editor offers sudo for it.
        Remote(server, "mkdir -p '" + server.Directory + "/etc/nginx'");
        WriteRemote(server, "etc/nginx/flow.conf", Bytes("# one\n"));
        o = Open(server, "etc/nginx/flow.conf", s => s.Editor.DisableConfigCheck = true);
        Replace(o, 0, "one", "two");
        Prompted(server, "service reload yes", (text, buttons) => text.Contains("Reload nginx now") ? DialogResult.Yes : (text.Contains("Could not reload nginx") ? DialogResult.No : (DialogResult?)null), () =>
        {
            SaveAndWait(o);
            Pump(() => server.Prompts.Count >= 2, "the answer of the service reload");
        });
        // The exit code is in the question when the command gave one; a channel that the SSH layer closed early gives none (see Settle), and the
        // question is the same without it.
        Check(server.Prompts.Count == 2 && server.Prompts[1].StartsWith("Could not reload nginx") && server.Prompts[1].EndsWith("\nTry again with sudo?"), "answered Yes: nginx is not there, the editor says it could not reload it and offers sudo" + (server.Prompts.Count == 2 && server.Prompts[1].StartsWith("Could not reload nginx") ? "" : " (" + string.Join(" || ", server.Prompts).Replace("\n", " ") + ")"));
        o.Close();
    }

    // The SSH layer drops the connection now and then after a transfer or a command (see Settle); that is not what is checked here. A scenario
    // that was cut short by it starts again, on a new connection and in a directory of its own. A scenario that fails the same way every
    // time, or that fails for any other reason, fails the run.
    private static bool IsConnectionLoss(Exception exception)
    {
        return exception is IOException || exception is TimeoutException || exception is InvalidOperationException && exception.Message.Contains("connection")
            || exception is Renci.SshNet.Common.SshException;
    }

    private static void Reconnect(Server server)
    {
        server.Reconnects++;
        try
        {
            server.Session.Dispose();
        }
        catch (Exception)
        {
        }
        server.Session = Connect(server.Host, server.Port, server.User, server.Password);
        server.Context.Session = server.Session;
    }

    private static void RunScenario(Server server, string name, Action<Server> scenario)
    {
        const int Attempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            int passedBefore = _passed;
            server.Directory = server.Root + "/" + name + "-" + attempt;
            try
            {
                Remote(server, "mkdir -p '" + server.Directory + "'");
                scenario(server);
                return;
            }
            catch (Exception exception) when (attempt < Attempts && IsConnectionLoss(exception))
            {
                _passed = passedBefore;
                Console.WriteLine("  (the connection to the server was lost during \"" + name + "\": " + exception.Message.Split('\n')[0] + "; again on a new connection, attempt " + (attempt + 1) + ")");
                Reconnect(server);
            }
        }
    }

    // The commands of the editor tab (RemoteFileEditorCommands, moved out of the WinForms panel in stage 3E-4, so that the WPF tab runs them too)
    // on the real editor: the state they read of the text view, the editor and the hex view, what each command does, the events of the commands
    // that the main window runs, a refresh asked for many times that runs once, a disabled command that does nothing, Reload with its question.
    private static void CommandsOfTheTab(Server server)
    {
        Console.WriteLine("-- the commands of the tab on the editor");
        WriteRemote(server, "commands.txt", Bytes("b\na\nc\n"));
        Opened o = Open(server, "commands.txt");
        try
        {
            RemoteFileEditor e = o.Editor;
            List<Action> posted = new List<Action>();
            RemoteFileEditorCommands commands = new RemoteFileEditorCommands(e, action =>
            {
                posted.Add(action);
                return true;
            });
            List<string> events = new List<string>();
            commands.StateChanged += (sender, args) => events.Add("state");
            commands.SaveRequested += (sender, args) => events.Add("save");
            commands.FindRequested += (sender, args) => events.Add("find");
            commands.ReplaceRequested += (sender, args) => events.Add("replace");
            commands.GoToLineRequested += (sender, args) => events.Add("goto");
            commands.ShowChangesRequested += (sender, args) => events.Add("changes");
            commands.RunRequested += (sender, args) => events.Add("run");
            commands.FollowLogRequested += (sender, args) => events.Add("follow");
            commands.ShowAllCharactersChanged += (sender, args) => events.Add("marks");
            commands.WordWrapChanged += (sender, args) => events.Add("wrap");
            commands.HexDialogRequested += (sender, args) => events.Add("dialog:" + args.Kind);

            commands.RefreshState();
            EditorCommandState state = commands.CommandState;
            Equal(Events(events), "state", "a refresh tells the state once");
            Check(!state.HexMode && !state.ReadOnly && !state.IsModified && !state.IsNewFile && !state.IsSaving && state.HasText && !state.HasSelection
                && !state.HasLineSelection && !state.CanUndo && !state.CanRedo && state.CanUseHex && !state.ShowAllCharacters && !state.WordWrap,
                "the state of a file just loaded");
            Check(!state.IsEnabled(EditorToolbarCommand.Save) && state.IsEnabled(EditorToolbarCommand.Reload) && !state.IsEnabled(LineTransformKind.SortAscending),
                "nothing to save, Reload can run, sorting needs a selection of lines");

            events.Clear();
            commands.ScheduleRefresh();
            commands.ScheduleRefresh();
            commands.ScheduleRefresh();
            Equal(posted.Count, 1, "a refresh asked for three times is posted once");
            posted[0]();
            posted.Clear();
            Equal(Events(events), "state", "and tells the state once when it runs");
            commands.ScheduleRefresh();
            Equal(posted.Count, 1, "after it ran a refresh is posted again");
            posted.Clear();

            events.Clear();
            commands.RunCommand(EditorToolbarCommand.Redo);
            Equal(Events(events), "state", "a disabled command (Redo with nothing to redo) only reads the state again");
            Equal(Text(o), "b\na\nc\n", "and changes nothing");

            events.Clear();
            commands.RunCommand(EditorToolbarCommand.ShowAllCharacters);
            Check(e.TextControl.ShowAllCharacters && commands.CommandState.ShowAllCharacters && commands.CommandState.IsChecked(EditorToolbarCommand.ShowAllCharacters),
                "Show All Characters switches the marks on, and the state shows it checked");
            commands.RunCommand(EditorToolbarCommand.WordWrap);
            Check(e.TextControl.WordWrap && commands.CommandState.WordWrap, "Word Wrap switches the wrap on");
            commands.RunCommand(EditorToolbarCommand.WordWrap);
            commands.RunCommand(EditorToolbarCommand.ShowAllCharacters);
            Check(!e.TextControl.WordWrap && !e.TextControl.ShowAllCharacters, "and again off");
            Equal(Events(events), "marks,state,wrap,state,wrap,state,marks,state", "each switch tells the main window, then the state");

            events.Clear();
            foreach (EditorToolbarCommand command in new[] { EditorToolbarCommand.Find, EditorToolbarCommand.Replace, EditorToolbarCommand.GoToLine,
                EditorToolbarCommand.FollowLog, EditorToolbarCommand.Save, EditorToolbarCommand.ShowChanges })
            {
                commands.RunCommand(command);
            }
            Equal(Events(events), "find,state,replace,state,goto,state,follow,state,state,state",
                "Find, Replace, Go to Line and Follow Log go to the main window; Save and Show Changes are disabled while nothing changed");

            e.TextControl.SetSelection(true, new TextDocument.TextPosition(0, 0));
            e.TextControl.SetCaret(new TextDocument.TextPosition(0, 3));
            commands.RefreshState();
            Check(commands.CommandState.HasLineSelection && commands.CommandState.HasSelection && commands.CommandState.IsEnabled(LineTransformKind.SortAscending),
                "with the lines selected sorting can run");
            commands.RunTransform(LineTransformKind.SortAscending);
            Equal(Text(o), "a\nb\nc\n", "Sort Ascending sorts the selected lines");
            state = commands.CommandState;
            Check(state.IsModified && state.CanUndo && state.IsEnabled(EditorToolbarCommand.Save) && state.IsEnabled(EditorToolbarCommand.ShowChanges),
                "the file is modified: it can be saved, undone and compared");
            commands.RunCommand(EditorToolbarCommand.Undo);
            Equal(Text(o), "b\na\nc\n", "Undo takes the sort back");
            Check(commands.CommandState.CanRedo && !commands.CommandState.IsModified, "and the text is as saved, with a Redo");
            commands.RunCommand(EditorToolbarCommand.Redo);
            Equal(Text(o), "a\nb\nc\n", "Redo sorts again");
            events.Clear();
            commands.RunCommand(EditorToolbarCommand.Save);
            commands.RunCommand(EditorToolbarCommand.ShowChanges);
            Equal(Events(events), "save,state,changes,state", "Save and Show Changes go to the main window when there is a change");

            commands.RunCommand(EditorToolbarCommand.ToggleHex);
            state = commands.CommandState;
            Check(e.IsHexMode && state.HexMode && state.HasText && state.IsVisible(EditorToolbarCommand.DataInspector) && !state.IsEnabled(EditorToolbarCommand.WordWrap)
                && !state.IsEnabled(LineTransformKind.TrimTrailingWhitespace), "Hex View opens the hex view: the inspector is offered, the wrap and the Text menu are not");
            bool inspector = e.HexEditor.ShowDataInspector;
            commands.RunCommand(EditorToolbarCommand.DataInspector);
            Check(e.HexEditor.ShowDataInspector == !inspector && commands.CommandState.ShowDataInspector == !inspector, "Data Inspector switches the inspector");
            commands.RunCommand(EditorToolbarCommand.DataInspector);
            events.Clear();
            commands.RequestHexDialog(HexDialogKind.Checksums);
            Equal(Events(events), "dialog:Checksums", "a dialog of the hex view goes to the main window");
            commands.RunCommand(EditorToolbarCommand.ToggleHex);
            Check(!e.IsHexMode && !commands.CommandState.HexMode, "Hex View again closes it");

            Prompted(server, "reload", (text, buttons) => DialogResult.No, () => commands.RunCommand(EditorToolbarCommand.Reload));
            Check(server.Prompts.Count == 1 && server.Prompts[0].Contains("load " + o.Path + " from the server again"), "Reload with a change asks first");
            Equal(Text(o), "a\nb\nc\n", "answered No it keeps the text");
            Prompted(server, "reload", (text, buttons) => DialogResult.Yes, () => commands.RunCommand(EditorToolbarCommand.Reload));
            Pump(() => e.CanSave && Text(o) == "b\na\nc\n", "the file to load again");
            Check(!e.IsModified, "answered Yes it loads the file from the server again");
            WriteRemote(server, "commands.txt", Bytes("c\nb\na\n"));
            Prompted(server, "reload", (text, buttons) => DialogResult.No, () => commands.RunCommand(EditorToolbarCommand.Reload));
            Pump(() => e.CanSave && Text(o) == "c\nb\na\n", "the file to load again");
            Equal(server.Prompts.Count, 0, "Reload without a change asks nothing and loads the file again");
        }
        finally
        {
            o.Close();
        }
        foreach (string[] item in new[] { new[] { "", "False" }, new[] { "x", "True" }, new[] { "\n", "True" } })
        {
            WriteRemote(server, "commands-text.txt", Bytes(item[0]));
            Opened one = Open(server, "commands-text.txt");
            try
            {
                RemoteFileEditorCommands commands = new RemoteFileEditorCommands(one.Editor, action => true);
                commands.RefreshState();
                Equal(commands.CommandState.HasText.ToString(), item[1], "a file of '" + item[0].Replace("\n", "\\n") + "' has text: " + item[1]);
                Equal(commands.CommandState.IsEnabled(EditorToolbarCommand.SelectAll).ToString(), item[1], "and Select All follows it");
            }
            finally
            {
                one.Close();
            }
        }
    }

    private static void Scenarios(Server server)
    {
        RunScenario(server, "load-save", LoadAndSave);
        RunScenario(server, "encodings", Encodings);
        RunScenario(server, "search-target", SearchTarget);
        RunScenario(server, "status-source", StatusSource);
        RunScenario(server, "format", ChangeFormat);
        RunScenario(server, "questions", ConvertCrLf);
        RunScenario(server, "new-file", NewFile);
        RunScenario(server, "permissions", Permissions);
        RunScenario(server, "conflicts", Conflicts);
        RunScenario(server, "background", BackgroundSave);
        RunScenario(server, "reload", Reloading);
        RunScenario(server, "validation", Validation);
        RunScenario(server, "service-reload", ServiceReload);
        RunScenario(server, "drafts", Drafts);
        RunScenario(server, "hex-view", HexView);
        RunScenario(server, "hex-draft", HexDraft);
        RunScenario(server, "flow", Flow);
        RunScenario(server, "hex-place", HexPlace);
        RunScenario(server, "commands", CommandsOfTheTab);
    }

    private static void RunServer(string name, string host, int port, string user, string password)
    {
        Console.WriteLine();
        Console.WriteLine("== Remote file editor on " + name + " (" + host + ":" + port + " as " + user + ")");
        Server server = new Server { Name = name, Host = host, Port = port, User = user, Password = password };
        server.Session = Connect(host, port, user, password);
        try
        {
            server.Context = new SessionContext { Session = server.Session, TitleWithUserName = user + "@" + host + ":" + port };
            string home = Remote(server, "echo $HOME").Last().Trim();
            server.Root = home + "/rfe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Remote(server, "mkdir -p '" + server.Root + "'");
            ThemedMessageBox.ShowImpl = (text, caption, buttons, icon, owner) =>
            {
                lock (server.Prompts)
                {
                    server.Prompts.Add(text);
                }
                DialogResult? answer = server.Answer?.Invoke(text, buttons);
                if (answer == null)
                {
                    Console.WriteLine("  (unexpected question) " + text.Replace("\n", " "));
                    return buttons == MessageBoxButtons.OK ? DialogResult.OK : DialogResult.Cancel;
                }
                return answer.Value;
            };
            try
            {
                Scenarios(server);
                Console.WriteLine("  (" + server.Reconnects + " new connections after a loss)");
            }
            finally
            {
                ThemedMessageBox.ShowImpl = null;
                try
                {
                    Remote(server, "rm -rf '" + server.Root + "'");
                }
                catch (Exception)
                {
                }
            }
        }
        finally
        {
            server.Session.Dispose();
        }
    }

    private static int Main()
    {
        bool ran = false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMARTTY_SSH_TEST_PORT")))
        {
            ran = true;
            RunServer("OpenSSH", Value("SMARTTY_SSH_TEST_HOST", "127.0.0.1"), int.Parse(Value("SMARTTY_SSH_TEST_PORT", "2222")), Value("SMARTTY_SSH_TEST_USER", "smartty-test"), Value("SMARTTY_SSH_TEST_PASSWORD", "smartty-test-password"));
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMARTTY_DROPBEAR_TEST_PORT")))
        {
            ran = true;
            RunServer("Dropbear + BusyBox ash", Value("SMARTTY_DROPBEAR_TEST_HOST", "127.0.0.1"), int.Parse(Value("SMARTTY_DROPBEAR_TEST_PORT", "2223")), Value("SMARTTY_DROPBEAR_TEST_USER", "smartty-ash"), Value("SMARTTY_DROPBEAR_TEST_PASSWORD", "smartty-ash-password"));
        }
        if (!ran)
        {
            Console.WriteLine("SKIP: no test SSH server (SMARTTY_SSH_TEST_PORT or SMARTTY_DROPBEAR_TEST_PORT is not set).");
            return 0;
        }
        Console.WriteLine();
        Console.WriteLine("RemoteFileEditorIntegrationTests: all " + _passed + " checks passed.");
        return 0;
    }
}
