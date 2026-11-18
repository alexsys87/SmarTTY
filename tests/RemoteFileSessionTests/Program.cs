using System;
using System.Collections.Generic;
using System.Text;
using SmarTTY.CodeEditing;
using SmarTTY.IO;
using SmarTTY.TextEditing;

// Checks of CodeEditing/RemoteFileSession.cs, the state of the saves of a remote file editor and the taking of the bytes to save (the text or the
// hex view, the question about CRLF line endings, the question about a byte order mark before "#!", the bytes kept from the hex view, the backup
// and the check of the file), with a fake editor that records what it is asked.
// The same code is run in the editor against real servers by tests/RemoteFileEditorIntegrationTests (the "questions" scenario).
// Run with: dotnet run --project tests/RemoteFileSessionTests
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

    private const string Path = "/etc/app/run.sh";

    private sealed class FakeSource : IRemoteSaveSource
    {
        public string Text = "echo hi\n";

        public bool HexMode { get; set; }

        public object HexVersion { get; set; } = new object();

        public byte[] Hex = new byte[] { 9, 8, 7 };

        public object TextVersion { get; set; } = new object();

        public LineEndingKind LineEndings { get; set; } = LineEndingKind.Lf;

        public TextFileEncoding FileEncoding { get; set; } = TextFileEncodings.Utf8;

        public bool Answer = true;

        public SyntaxHighlighting.Language Syntax = SyntaxHighlighting.Language.Bash;

        public readonly List<string> Log = new List<string>();

        public byte[] HexBytes()
        {
            Log.Add("hex bytes");
            return Hex;
        }

        public string GetText()
        {
            return Text;
        }

        public void ConvertLineEndingsToLf()
        {
            Log.Add("convert to LF");
            Text = Text.Replace("\r\n", "\n");
            LineEndings = LineEndingKind.Lf;
        }

        public void SaveWithoutByteOrderMark()
        {
            Log.Add("without BOM");
            FileEncoding = TextFileEncodings.Utf8;
        }

        public bool Confirm(string question)
        {
            Log.Add("ask: " + question);
            return Answer;
        }

        public void ClearMessages()
        {
            Log.Add("clear messages");
        }

        public SyntaxHighlighting.Language DetectSyntax()
        {
            return Syntax;
        }

        public string FirstLine()
        {
            int end = Text.IndexOf('\n');
            return end < 0 ? Text : Text.Substring(0, end);
        }
    }

    private static Func<RemoteSaveOptions> Options(RemoteSaveOptions options, List<string> log = null)
    {
        return () =>
        {
            log?.Add("options");
            return options;
        };
    }

    private static RemoteSaveOptions DefaultOptions()
    {
        return new RemoteSaveOptions { TemporaryDirectory = "/tmp" };
    }

    private const string CrLfQuestion = "ask: The text has CRLF line endings, which break shell scripts and OpenWrt configuration files. Convert them to LF endings?";

    private const string BomQuestion = "ask: The file starts with #! and is saved with a UTF-8 byte order mark, which makes the system fail to run it as a script. Save it without the mark?";

    private static void PlainText()
    {
        RemoteFileSession session = new RemoteFileSession(Path);
        FakeSource source = new FakeSource { Text = "echo hi é\n" };
        RemoteSaveJob job = session.PrepareSave(source, Options(DefaultOptions(), source.Log));
        Equal(string.Join("|", source.Log), "clear messages|options", "the messages are cleared first, the options are read after the text was settled, and nothing is asked");
        Equal(Convert.ToBase64String(job.Bytes), Convert.ToBase64String(Encoding.UTF8.GetBytes("echo hi é\n")), "the bytes are the text in the encoding of the file");
        Check(ReferenceEquals(job.Version, source.TextVersion), "the version is the one of the text");
        Check(!job.HexMode && job.Encoding == TextFileEncodings.Utf8, "a text save, with the encoding of the file");
        Check(job.TemporaryPath.StartsWith("/tmp/") && job.TemporaryPath.EndsWith(".txt") && job.TemporaryPath.Length == "/tmp/".Length + 36 + ".txt".Length, "the temporary file is in the folder of the settings, named by a guid");
        Equal(job.ReplaceCommand, RemoteSaveCommand.BuildScript(Path, job.TemporaryPath, job.Bytes.Length, false), "the replace script is the one of RemoteSaveCommand");
        Equal(job.Generation, 1, "the first save is generation 1");
        Equal(session.SaveGeneration, 1, "the session remembers it");
        Equal(session.PrepareSave(source, Options(DefaultOptions())).Generation, 2, "the next one is generation 2");
        Check(job.Validator != null, "a shell script has a check");
    }

    private static void FlagsOfTheNextSave()
    {
        RemoteFileSession session = new RemoteFileSession(Path);
        FakeSource source = new FakeSource();
        session.ForceOverwrite = true;
        session.SkipValidation = true;
        RemoteSaveJob job = session.PrepareSave(source, Options(DefaultOptions()));
        Check(job.SkipConflictCheck, "the overwrite the user agreed to goes to the job");
        Check(job.Validator == null, "and the check is skipped when the user chose to save anyway");
        Check(!session.ForceOverwrite && !session.SkipValidation, "both are used up by that save");
        job = session.PrepareSave(source, Options(DefaultOptions()));
        Check(!job.SkipConflictCheck && job.Validator != null, "the next save checks the server and the file again");

        RemoteSaveOptions off = DefaultOptions();
        off.DisableConfigCheck = true;
        Check(session.PrepareSave(source, Options(off)).Validator == null, "the setting switches the check off");
        RemoteSaveOptions windows = DefaultOptions();
        windows.IsWindowsHost = true;
        Check(session.PrepareSave(source, Options(windows)).Validator == null, "a Windows host has no check");
        source.HexMode = true;
        Check(session.PrepareSave(source, Options(DefaultOptions())).Validator == null, "the bytes of the hex view are not checked");
    }

    private static void Backup()
    {
        RemoteFileSession session = new RemoteFileSession(Path);
        FakeSource source = new FakeSource();
        RemoteSaveOptions options = DefaultOptions();
        Check(!session.PrepareSave(source, Options(options)).IncludesBackup, "no backup when the setting is off");
        options.BackupBeforeSave = true;
        RemoteSaveJob job = session.PrepareSave(source, Options(options));
        Check(job.IncludesBackup, "a backup when the setting is on");
        Equal(job.ReplaceCommand, RemoteSaveCommand.BuildScript(Path, job.TemporaryPath, job.Bytes.Length, true), "and the script copies the old file");
        session.BackupDone = true;
        Check(!session.PrepareSave(source, Options(options)).IncludesBackup, "the copy is made once, by the first save");
        session.BackupDone = false;
        options.IsNewFile = true;
        Check(!session.PrepareSave(source, Options(options)).IncludesBackup, "a new file has nothing to copy");
    }

    private static void CrLfLineEndings()
    {
        RemoteFileSession session = new RemoteFileSession(Path);
        FakeSource source = new FakeSource { Text = "a\r\nb\n", LineEndings = LineEndingKind.Lf };
        RemoteSaveJob job = session.PrepareSave(source, Options(DefaultOptions()));
        Equal(string.Join("|", source.Log), "clear messages|" + CrLfQuestion + "|convert to LF", "CRLF in a file that was not CRLF: one question, then the conversion");
        Equal(Encoding.UTF8.GetString(job.Bytes), "a\nb\n", "the bytes are the converted text");
        Equal(session.ConvertCrLf, (bool?)true, "the answer is kept");

        source.Log.Clear();
        source.Text = "c\r\nd\n";
        source.LineEndings = LineEndingKind.Lf;
        job = session.PrepareSave(source, Options(DefaultOptions()));
        Equal(string.Join("|", source.Log), "clear messages|convert to LF", "the next save does not ask again, and converts");

        session = new RemoteFileSession(Path);
        source = new FakeSource { Text = "a\r\nb\n", Answer = false };
        job = session.PrepareSave(source, Options(DefaultOptions()));
        Equal(string.Join("|", source.Log), "clear messages|" + CrLfQuestion, "answered No: nothing is converted");
        Equal(Encoding.UTF8.GetString(job.Bytes), "a\r\nb\n", "the CRLF are saved");
        Equal(session.ConvertCrLf, (bool?)false, "the No is kept");
        source.Log.Clear();
        session.PrepareSave(source, Options(DefaultOptions()));
        Equal(string.Join("|", source.Log), "clear messages", "and not asked again");

        session = new RemoteFileSession(Path);
        source = new FakeSource { Text = "a\r\nb\r\n", LineEndings = LineEndingKind.CrLf };
        session.PrepareSave(source, Options(DefaultOptions()));
        Equal(string.Join("|", source.Log), "clear messages", "a file that is CRLF as a whole: no question");
    }

    private static void ByteOrderMark()
    {
        RemoteFileSession session = new RemoteFileSession(Path);
        FakeSource source = new FakeSource { Text = "#!/bin/sh\necho\n", FileEncoding = TextFileEncodings.Utf8Bom };
        RemoteSaveJob job = session.PrepareSave(source, Options(DefaultOptions()));
        Equal(string.Join("|", source.Log), "clear messages|" + BomQuestion + "|without BOM", "a script saved with a mark: one question, then the encoding without it");
        Check(job.Encoding == TextFileEncodings.Utf8, "the job has the new encoding");
        Check(job.Bytes.Length == "#!/bin/sh\necho\n".Length, "and the bytes have no mark");
        Check(session.BomAnswered, "the question is marked as asked");
        source.Log.Clear();
        session.PrepareSave(source, Options(DefaultOptions()));
        Equal(string.Join("|", source.Log), "clear messages", "it is asked once");

        session = new RemoteFileSession(Path);
        source = new FakeSource { Text = "#!/bin/sh\n", FileEncoding = TextFileEncodings.Utf8Bom, Answer = false };
        job = session.PrepareSave(source, Options(DefaultOptions()));
        Equal(string.Join("|", source.Log), "clear messages|" + BomQuestion, "answered No: the encoding is kept");
        Check(job.Encoding == TextFileEncodings.Utf8Bom && job.Bytes[0] == 0xEF, "the mark is saved");

        session = new RemoteFileSession(Path);
        source = new FakeSource { Text = "echo\n", FileEncoding = TextFileEncodings.Utf8Bom };
        session.PrepareSave(source, Options(DefaultOptions()));
        Equal(string.Join("|", source.Log), "clear messages", "a mark on a file that is not a script: no question");
    }

    private static void BytesFromTheHexView()
    {
        RemoteFileSession session = new RemoteFileSession(Path);
        FakeSource source = new FakeSource { Text = "a\r\nb\n", LineEndings = LineEndingKind.Lf };
        session.HexBytesForTextSave = new byte[] { 1, 2, 3, 4 };
        session.HexTextSnapshot = "a\r\nb\n";
        RemoteSaveJob job = session.PrepareSave(source, Options(DefaultOptions()));
        Equal(string.Join("|", source.Log), "clear messages", "a text that was not edited since the hex view: no question");
        Equal(string.Join(",", job.Bytes), "1,2,3,4", "the bytes of the hex view are saved back exactly");

        source.Text = "edited\n";
        job = session.PrepareSave(source, Options(DefaultOptions()));
        Equal(Encoding.UTF8.GetString(job.Bytes), "edited\n", "an edited text is encoded");

        // The question changes the text so that it is the snapshot: the bytes are used after the questions.
        session = new RemoteFileSession(Path);
        source = new FakeSource { Text = "a\r\nb\n", LineEndings = LineEndingKind.Lf };
        session.HexBytesForTextSave = new byte[] { 5, 6 };
        session.HexTextSnapshot = "a\nb\n";
        job = session.PrepareSave(source, Options(DefaultOptions()));
        Equal(string.Join(",", job.Bytes), "5,6", "bytes whose text equals the text after the conversion are used");

        source = new FakeSource { HexMode = true, Text = "a\r\nb\n" };
        session = new RemoteFileSession(Path);
        job = session.PrepareSave(source, Options(DefaultOptions()));
        Equal(string.Join("|", source.Log), "clear messages|hex bytes", "hex mode: the bytes of the hex document, no question");
        Check(job.HexMode && ReferenceEquals(job.Version, source.HexVersion) && string.Join(",", job.Bytes) == "9,8,7", "the job is a hex save with the hex version");
        Equal(job.Encoding, TextFileEncodings.Utf8, "its encoding is the one of the file");
    }

    private static int Main()
    {
        try
        {
            PlainText();
            FlagsOfTheNextSave();
            Backup();
            CrLfLineEndings();
            ByteOrderMark();
            BytesFromTheHexView();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " remote file session checks passed.");
        return 0;
    }
}
