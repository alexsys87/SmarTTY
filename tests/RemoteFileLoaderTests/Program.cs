using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SmarTTY.CodeEditing;
using SmarTTY.IO;
using SmarTTY.Ssh;

// Checks of CodeEditing/RemoteFileLoader.cs, the loading thread of the remote file editor (does the file exist, may it be written, the
// questions about a missing write permission and a large file, the download, the preview, a closed tab), with a fake connection, a fake user
// and a UI thread that runs what it is given at once. The same code is run in the editor against real servers by
// tests/RemoteFileEditorIntegrationTests.
// Run with: dotnet run --project tests/RemoteFileLoaderTests
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

    // A stream of a given length that makes its bytes as they are read, so that a file of tens of megabytes costs nothing.
    private sealed class PatternStream : Stream
    {
        private readonly long _length;

        private long _position;

        public Action OnRead;

        public PatternStream(long length)
        {
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            OnRead?.Invoke();
            int n = (int)Math.Min(count, _length - _position);
            for (int i = 0; i < n; i++)
            {
                buffer[offset + i] = (byte)'a';
            }
            _position += n;
            return n;
        }
    }

    private sealed class FakeHost : IRemoteFileHost
    {
        public bool IsWindows { get; set; }

        public readonly HashSet<string> Existing = new HashSet<string>();

        public readonly HashSet<string> Writable = new HashSet<string>();

        public long FileLength;

        public string Signature = "sig-1";

        public Func<Stream> MakeStream;

        public Exception FailOnExists;

        public readonly List<string> Calls = new List<string>();

        public List<string> RunCommandChecked(string command)
        {
            Calls.Add(command);
            const string exists = "test -e '";
            const string writable = "test -w '";
            if (command.StartsWith(exists))
            {
                if (FailOnExists != null)
                {
                    throw FailOnExists;
                }
                return Existing.Contains(command.Substring(exists.Length, command.Length - exists.Length - 1)) ? new List<string>() : null;
            }
            if (command.StartsWith(writable))
            {
                return Writable.Contains(command.Substring(writable.Length, command.Length - writable.Length - 1)) ? new List<string>() : null;
            }
            if (command.StartsWith("stat -c"))
            {
                return new List<string> { Signature };
            }
            return null;
        }

        public void ThrowIfConnectionLost() { }

        public Stream OpenScpDownload(string path, out long length)
        {
            Calls.Add("scp " + path);
            Stream stream = MakeStream != null ? MakeStream() : new PatternStream(FileLength);
            length = FileLength;
            return stream;
        }

        public Stream OpenSftpRead(string path, out long length)
        {
            Calls.Add("sftp " + path);
            length = FileLength;
            return MakeStream != null ? MakeStream() : new PatternStream(FileLength);
        }

        public bool SftpExists(string path) { return Existing.Contains(path); }

        public bool? SftpCanWrite(string path) { return Writable.Contains(path); }

        public string SftpSignature(string path) { return Signature; }
    }

    private sealed class FakePrompt : IUserPrompt
    {
        public readonly Queue<PromptAnswer> Answers = new Queue<PromptAnswer>();

        public readonly List<string> Questions = new List<string>();

        public readonly List<PromptButtons> Buttons = new List<PromptButtons>();

        public PromptAnswer Ask(string text, PromptButtons buttons)
        {
            Questions.Add(text);
            Buttons.Add(buttons);
            return Answers.Count > 0 ? Answers.Dequeue() : PromptAnswer.Cancel;
        }
    }

    private sealed class FakeUi : IUiDispatcher
    {
        public bool Post(Action action)
        {
            action();
            return true;
        }
    }

    private sealed class FakeView : IRemoteFileLoadView
    {
        public bool IsReloading { get; set; }

        public bool IsClosed { get; set; }

        public readonly List<string> Previews = new List<string>();

        public bool Finished;

        public byte[] File;

        public string Signature;

        public bool WasCancelled;

        public Exception Failure;

        public void ShowPreview(string text) { Previews.Add(text); }

        public void Finish(byte[] file, string signature) { Finished = true; File = file; Signature = signature; }

        public void Cancelled() { WasCancelled = true; }

        public void Failed(Exception exception) { Failure = exception; }

        public bool Ended => Finished || WasCancelled || Failure != null;
    }

    private sealed class Run
    {
        public FakeHost Host = new FakeHost();

        public FakePrompt Prompt = new FakePrompt();

        public FakeView View = new FakeView();

        public bool AutoSudo;

        public RemoteFileLoader Loader;

        public void Start(string path = "/etc/app/a.conf")
        {
            Loader = new RemoteFileLoader(new RemoteFileServer(Host, path), path, Prompt, new FakeUi(), View, () => AutoSudo);
            Loader.Run();
        }
    }

    private static Run ExistingFile(long length)
    {
        Run run = new Run();
        run.Host.Existing.Add("/etc/app/a.conf");
        run.Host.Writable.Add("/etc/app/a.conf");
        run.Host.FileLength = length;
        return run;
    }

    private static void ExistingWritableFile()
    {
        Run run = ExistingFile(5);
        run.Start();
        Check(run.View.Finished, "an existing file is loaded");
        Equal(run.View.File.Length, 5, "the whole file is handed over");
        Equal(run.View.Signature, "sig-1", "with the signature taken before the download");
        Check(!run.Loader.IsNewFile && !run.Loader.IsReadOnlyMode && !run.Loader.IsPreview && !run.Loader.SudoOnSave, "no flag is set");
        Equal(run.Prompt.Questions.Count, 0, "a file that can be written raises no question");
        Equal(string.Join("|", run.Host.Calls), "test -e '/etc/app/a.conf'|test -w '/etc/app/a.conf'|" + RemoteFileSignature.BuildCommand("/etc/app/a.conf") + "|scp /etc/app/a.conf", "the order of the calls: exists, writable, signature, download");
        Equal(run.View.Previews.Count, 0, "no preview for a short download");
    }

    private static void NewFile()
    {
        Run run = new Run();
        run.Host.Existing.Add("/etc/app");
        run.Host.Writable.Add("/etc/app");
        run.Start();
        Check(run.View.Finished && run.View.File == null && run.View.Signature == null, "a path that does not exist is a new file: finished with no bytes and no signature");
        Check(run.Loader.IsNewFile, "the loader says it is new");
        Check(!run.Host.Calls.Exists(c => c.StartsWith("scp") || c.StartsWith("stat")), "nothing is downloaded and no signature is asked for");

        Run noDirectory = new Run();
        noDirectory.Start();
        Check(noDirectory.View.Failure is RemoteOperationException, "a path in a directory that does not exist is an error");
        Equal(noDirectory.View.Failure.Message, "Cannot create /etc/app/a.conf: the directory /etc/app does not exist.", "the error names the directory");
        Check(!noDirectory.View.Finished && !noDirectory.View.WasCancelled, "and nothing else is reported");

        Run root = new Run();
        root.Host.Writable.Add("/");
        root.Start("noslash");
        Check(root.View.Finished && root.Loader.IsNewFile, "a path with no directory is a new file in /");
        Check(root.Host.Calls.Contains("test -w '/'"), "its directory is /");
    }

    private static void NotWritable()
    {
        // An existing file the user cannot write.
        Run run = ExistingFile(3);
        run.Host.Writable.Clear();
        run.Prompt.Answers.Enqueue(PromptAnswer.Yes);
        run.Start();
        Equal(run.Prompt.Questions.Count, 1, "one question");
        Equal(run.Prompt.Buttons[0], PromptButtons.YesNoCancel, "with three buttons");
        Equal(run.Prompt.Questions[0], "You do not have permission to write to /etc/app/a.conf.\n\nYes: edit the text and save it with sudo (the password is asked when you save).\nNo: open the file read-only.\nCancel: do not open the file.", "the text of the question");
        Check(run.Loader.SudoOnSave && !run.Loader.IsReadOnlyMode, "Yes: the save will use sudo");
        Check(run.View.Finished, "and the file is loaded");

        run = ExistingFile(3);
        run.Host.Writable.Clear();
        run.Prompt.Answers.Enqueue(PromptAnswer.No);
        run.Start();
        Check(run.Loader.IsReadOnlyMode && !run.Loader.SudoOnSave && run.View.Finished, "No: read-only, loaded");

        run = ExistingFile(3);
        run.Host.Writable.Clear();
        run.Prompt.Answers.Enqueue(PromptAnswer.Cancel);
        run.Start();
        Check(run.View.WasCancelled && !run.View.Finished && run.View.Failure == null, "Cancel: the tab is told, with no error");
        Check(!run.Host.Calls.Exists(c => c.StartsWith("scp")), "and nothing is downloaded");

        run = ExistingFile(3);
        run.Host.Writable.Clear();
        run.AutoSudo = true;
        run.Start();
        Equal(run.Prompt.Questions.Count, 0, "a connection that saves with sudo asks nothing");
        Check(run.Loader.SudoOnSave && run.View.Finished, "and saves through sudo");

        // A new file in a directory that cannot be written.
        run = new Run();
        run.Host.Existing.Add("/etc/app");
        run.Prompt.Answers.Enqueue(PromptAnswer.No);
        run.Start();
        Check(run.Prompt.Questions[0].StartsWith("You do not have permission to write to the directory /etc/app, where this new file would be created.\n\n"), "a new file: the question names the directory");
        Check(run.Loader.IsReadOnlyMode && run.Loader.IsNewFile && run.View.Finished, "No: read-only new file");
    }

    private static void NotWritableOnWindows()
    {
        Run run = ExistingFile(3);
        run.Host.IsWindows = true;
        run.Host.Writable.Clear();
        run.Prompt.Answers.Enqueue(PromptAnswer.Yes);
        run.Start();
        Equal(run.Prompt.Buttons[0], PromptButtons.YesNo, "Windows: no sudo, two buttons");
        Equal(run.Prompt.Questions[0], "You do not have permission to write to /etc/app/a.conf.\n\nYes: open the file read-only.\nNo: do not open the file.", "Windows: the text of the question");
        Check(run.Loader.IsReadOnlyMode && !run.Loader.SudoOnSave && run.View.Finished, "Windows, Yes: read-only");
        Check(run.Host.Calls.Contains("sftp /etc/app/a.conf"), "Windows: read over SFTP");
        Check(!run.Host.Calls.Exists(c => c.StartsWith("test ")), "Windows: no shell command");

        run = ExistingFile(3);
        run.Host.IsWindows = true;
        run.Host.Writable.Clear();
        run.Prompt.Answers.Enqueue(PromptAnswer.No);
        run.Start();
        Check(run.View.WasCancelled, "Windows, No: the file is not opened");

        run = new Run();
        run.Host.IsWindows = true;
        run.Host.Existing.Add("/etc/app");
        run.Start();
        Check(run.View.Finished && run.Loader.IsNewFile && run.Prompt.Questions.Count == 0, "Windows: the folder of a new file is not probed, so no question");
    }

    private static void LargeFiles()
    {
        long warn = EditorFilePolicy.WarnBytes;
        Run run = ExistingFile(warn);
        run.Prompt.Answers.Enqueue(PromptAnswer.Yes);
        run.Start();
        Equal(run.Prompt.Buttons[0], PromptButtons.YesNoCancel, "a large file: three buttons");
        Equal(run.Prompt.Questions[0], "/etc/app/a.conf is 8192KB. A large file makes the editor slow and takes a lot of memory.\n\nYes: open it for editing.\nNo: open it read-only (safer).\nCancel: do not open it.", "a large file: the text of the question");
        Check(run.View.Finished && run.View.File.Length == warn && !run.Loader.IsReadOnlyMode, "Yes: the whole file is loaded for editing");

        run = ExistingFile(warn);
        run.Prompt.Answers.Enqueue(PromptAnswer.No);
        run.Start();
        Check(run.Loader.IsReadOnlyMode && !run.Loader.IsPreview && run.View.Finished && run.View.File.Length == warn, "No: the whole file, read-only");

        run = ExistingFile(warn);
        run.Prompt.Answers.Enqueue(PromptAnswer.Cancel);
        run.Start();
        Check(run.View.WasCancelled && !run.View.Finished, "Cancel: not opened");

        // The user already chose read-only (no write permission): a shorter question.
        run = ExistingFile(warn);
        run.Host.Writable.Clear();
        run.Prompt.Answers.Enqueue(PromptAnswer.No);
        run.Prompt.Answers.Enqueue(PromptAnswer.Ok);
        run.Start();
        Equal(run.Prompt.Buttons.Count, 2, "read-only and large: two questions");
        Equal(run.Prompt.Buttons[1], PromptButtons.OkCancel, "the second has OK and Cancel");
        Equal(run.Prompt.Questions[1], "/etc/app/a.conf is 8192KB. A large file makes the editor slow and takes a lot of memory.\n\nOpen it read-only?", "and its text");
        Check(run.View.Finished, "OK: loaded");

        run = ExistingFile(warn);
        run.Host.Writable.Clear();
        run.Prompt.Answers.Enqueue(PromptAnswer.No);
        run.Prompt.Answers.Enqueue(PromptAnswer.Cancel);
        run.Start();
        Check(run.View.WasCancelled, "Cancel of the second question: not opened");

        // Too large to edit: the first 8 MB, read-only.
        run = ExistingFile(EditorFilePolicy.PreviewOnlyBytes);
        run.Prompt.Answers.Enqueue(PromptAnswer.Yes);
        run.Start();
        Equal(run.Prompt.Buttons[0], PromptButtons.YesNo, "a huge file: two buttons");
        Equal(run.Prompt.Questions[0], "/etc/app/a.conf is 64MB, too large to edit here.\n\nOpen the first 8192KB read-only?", "a huge file: the text of the question");
        Check(run.Loader.IsPreview && run.Loader.IsReadOnlyMode, "Yes: a read-only preview");
        Equal(run.View.File.Length, (int)EditorFilePolicy.PreviewBytes, "only the first 8 MB are handed over");

        run = ExistingFile(EditorFilePolicy.PreviewOnlyBytes);
        run.Prompt.Answers.Enqueue(PromptAnswer.No);
        run.Start();
        Check(run.View.WasCancelled && !run.Loader.IsPreview, "No: not opened");
    }

    private static void Reloading()
    {
        Run run = ExistingFile(5);
        run.View.IsReloading = true;
        run.Host.Writable.Clear();
        run.Start();
        Check(run.View.Finished && run.Prompt.Questions.Count == 0, "a reload does not ask about the write permission again");
        Check(!run.Host.Calls.Exists(c => c.StartsWith("test -w")), "and does not even probe it");

        // A file that was not opened as a preview is reloaded whole, with no question about its size.
        run = ExistingFile(EditorFilePolicy.PreviewOnlyBytes);
        run.View.IsReloading = true;
        run.Start();
        Equal(run.View.File.Length, (int)EditorFilePolicy.PreviewOnlyBytes, "a reload of a file that was not a preview loads all of it");
        Equal(run.Prompt.Questions.Count, 0, "with no question about its size");

        // A file the user chose to preview is reloaded as a preview, with no question (the editor keeps these answers in the loader).
        run = ExistingFile(EditorFilePolicy.PreviewOnlyBytes);
        run.Prompt.Answers.Enqueue(PromptAnswer.Yes);
        run.Start();
        Check(run.Loader.IsPreview, "the first load chose a preview");
        run.Prompt.Questions.Clear();
        FakeView reload = new FakeView { IsReloading = true };
        run.View = reload;
        run.Loader = new RemoteFileLoader(new RemoteFileServer(run.Host, "/etc/app/a.conf"), "/etc/app/a.conf", run.Prompt, new FakeUi(), reload, () => false);
        run.Loader.IsPreview = true;
        run.Loader.IsReadOnlyMode = true;
        run.Loader.Run();
        Equal(reload.File.Length, (int)EditorFilePolicy.PreviewBytes, "a preview is reloaded as a preview");
        Equal(run.Prompt.Questions.Count, 0, "with no question");
        Equal(reload.Previews.Count, 0, "and no preview text is posted during a reload");
    }

    private static void PreviewText()
    {
        Run run = ExistingFile(0);
        int reads = 0;
        run.Host.MakeStream = () =>
        {
            PatternStream stream = new PatternStream(200000);
            stream.OnRead = () =>
            {
                if (++reads == 2)
                {
                    System.Threading.Thread.Sleep(600);
                }
            };
            return stream;
        };
        run.Host.FileLength = 200000;
        run.Start();
        Check(run.View.Previews.Count >= 1, "a download that takes a while posts the text so far");
        Check(run.View.Previews[0].Length > 0 && run.View.Previews[0].Trim('a').Length == 0, "the preview is the decoded text");
        Equal(run.View.File.Length, 200000, "and the whole file arrives at the end");
    }

    private static void ClosedTab()
    {
        Run run = ExistingFile(300000);
        run.Host.MakeStream = () =>
        {
            PatternStream stream = new PatternStream(300000);
            stream.OnRead = () => run.View.IsClosed = true;
            return stream;
        };
        run.Start();
        Check(!run.View.Ended, "a tab that was closed during the download is told nothing");
    }

    private static void Failures()
    {
        Run run = ExistingFile(3);
        run.Host.FailOnExists = new IOException("The connection to the server was lost.");
        run.Start();
        Check(run.View.Failure is IOException && !run.View.Finished, "an error of the server is handed to the tab");

        run = ExistingFile(3);
        run.Host.MakeStream = () => throw new RemoteOperationException("no such file");
        run.Start();
        Equal(run.View.Failure?.Message, "no such file", "an error of the download is handed to the tab");
    }

    private static int Main()
    {
        try
        {
            ExistingWritableFile();
            NewFile();
            NotWritable();
            NotWritableOnWindows();
            LargeFiles();
            Reloading();
            PreviewText();
            ClosedTab();
            Failures();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " remote file loader checks passed.");
        return 0;
    }
}
