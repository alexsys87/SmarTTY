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
/// It is the safety net of the move of the logic of the editor out of the control (stage 1, step A4): it was written against the code before
/// the move, and what it checks must stay the same.
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
        RemoteFileEditor editor = new RemoteFileEditor(server.Context, path, settings, new TextDocument.TextPosition()) { Dock = DockStyle.Fill };
        form.Controls.Add(editor);
        form.Show();
        Pump(() => editor.CanSave || editor.IsReadOnlyMode, "the file " + name + " to load");
        Settle(server);
        return new Opened { Editor = editor, Form = form, Server = server, Path = path };
    }

    private static string Text(Opened opened)
    {
        return opened.Editor.Document.GetText();
    }

    // Replaces a word in a line of the text, as typing would.
    private static void Replace(Opened opened, int line, string oldText, string newText)
    {
        string text = opened.Editor.Document.GetLineText(line);
        int index = text.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException("The line " + line + " is '" + text + "', it has no '" + oldText + "'");
        }
        opened.Editor.ReplaceText(new TextDocument.TextPosition(index, line), new TextDocument.TextPosition(index + oldText.Length, line), select: false, newText);
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
        ISearchTarget target = o.Editor;
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
        o.Editor.ReplaceText(new TextDocument.TextPosition(0, 0), new TextDocument.TextPosition(0, 0), select: false, "first line\nsecond line\n");
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

    private static void Scenarios(Server server)
    {
        RunScenario(server, "load-save", LoadAndSave);
        RunScenario(server, "encodings", Encodings);
        RunScenario(server, "search-target", SearchTarget);
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
