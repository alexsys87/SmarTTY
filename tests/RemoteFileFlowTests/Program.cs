using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SmarTTY.CodeEditing;
using SmarTTY.IO;
using SmarTTY.TextEditing;

// Checks of CodeEditing/RemoteFileFlow.cs, the loading and the saves of a remote file in its editor: what can be done before the file is loaded
// and while it is loaded again, a save when a tab closes (the conflict, sudo, the wait for a background save), a save in the background (a save
// asked for meanwhile, a superseded one, an error, the conflict question and its three answers, the check of the file), the offer to reload a
// service, a reload and a load that failed. The editor is a fake that logs what it is asked and keeps the threads it is given in queues that the
// checks run; the server is a fake connection under the real RemoteFileServer and RemoteFileSaver; the session, the content and the loader (its
// flags) are the real classes.
// The same code is run in the real editor against real servers by tests/RemoteFileEditorIntegrationTests (the "flow" scenario and the scenarios
// of the saves).
// Run with: dotnet run --project tests/RemoteFileFlowTests
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

    private static T Throws<T>(Action action, string name) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            Check(true, name);
            return exception;
        }
        Check(false, name + " (nothing was thrown)");
        return null;
    }

    // What the flow asked of the editor and of the server, and the events it raised, in order.
    private static readonly List<string> Log = new List<string>();

    private static string TakeLog()
    {
        string text = string.Join(", ", Log);
        Log.Clear();
        return text;
    }

    private const string ServiceReloadStart = "N=$(command -v nginx";

    // The connection: the signature of the file, the replace script, the check of the file, the reload of a service, sudo.
    private sealed class FakeServer : IRemoteFileWriteHost
    {
        public bool IsWindows { get; set; }

        public string Signature = "v1";

        // The signature the file has after the replace script ran (null: the same).
        public string SignatureAfterReplace;

        public int? ReplaceStatus = 0;

        public int? ValidationStatus = 0;

        public List<string> ValidationOutput = new List<string>();

        public int? ServiceStatus = 0;

        public List<string> ServiceOutput = new List<string>();

        public bool ServiceThrows;

        public bool ConnectionLost;

        public SudoRunResult Sudo = new SudoRunResult { ExitStatus = 0, HasExitStatus = true };

        public byte[] Uploaded;

        public List<string> RunCommandChecked(string command)
        {
            if (command.StartsWith("stat -c", StringComparison.Ordinal))
            {
                Log.Add("signature");
                return Signature == null ? null : new List<string> { Signature };
            }
            Log.Add(command.StartsWith("rm -f", StringComparison.Ordinal) ? "remove temporary" : "checked " + command);
            return new List<string>();
        }

        public List<string> RunCommand(string command, out int? exitStatus, TimeSpan? timeout)
        {
            if (command.StartsWith(ServiceReloadStart, StringComparison.Ordinal))
            {
                Log.Add("service reload");
                if (ServiceThrows)
                {
                    throw new IOException("The connection was lost.");
                }
                exitStatus = ServiceStatus;
                return ServiceOutput;
            }
            if (command.Contains("command -v sh "))
            {
                Log.Add("check");
                exitStatus = ValidationStatus;
                return ValidationOutput;
            }
            Log.Add("replace");
            exitStatus = ReplaceStatus;
            if (ReplaceStatus == 0 && SignatureAfterReplace != null)
            {
                Signature = SignatureAfterReplace;
            }
            return new List<string>();
        }

        public void ThrowIfConnectionLost()
        {
            if (ConnectionLost)
            {
                throw new IOException("The connection to the server was lost.");
            }
        }

        public Stream OpenScpDownload(string path, out long length) { throw new NotSupportedException(); }

        public Stream OpenSftpRead(string path, out long length) { throw new NotSupportedException(); }

        public bool SftpExists(string path) { return false; }

        public bool? SftpCanWrite(string path) { return true; }

        public string SftpSignature(string path)
        {
            Log.Add("signature");
            return Signature;
        }

        public void UploadScp(string path, byte[] bytes, int mode)
        {
            Log.Add("upload");
            Uploaded = bytes;
        }

        public void SftpSave(string path, byte[] bytes, bool backup)
        {
            Log.Add("sftp save");
            Uploaded = bytes;
        }

        public SudoRunResult RunSudo(string sudoCommand)
        {
            Log.Add("sudo");
            return Sudo;
        }
    }

    // The text view as the content sees it: a document and its modified state.
    private sealed class FakeText : IRemoteFileContentHost
    {
        public EditableTextDocument TextDocument { get; private set; } = new EditableTextDocument();

        public bool Modified;

        public bool IsTextModified { get => Modified; set => Modified = value; }

        public bool TextReadOnly { get; set; }

        public TextDocument.TextPosition Caret => new TextDocument.TextPosition(0, 0);

        public void SetCaret(TextDocument.TextPosition position) { }

        public void SetDocument(EditableTextDocument document)
        {
            TextDocument = document;
        }

        public void FocusText() { }

        // The version of the text: an edit makes a new one, and a save of an older one leaves the text modified.
        public object Version = new object();

        public void Edit()
        {
            Version = new object();
            Modified = true;
        }

        public void MarkTextSaved(object version)
        {
            Log.Add("text saved");
            Modified = !ReferenceEquals(version, Version);
        }

        public void RaiseModifiedChanged() { }

        public bool IsLoaded => true;

        public bool IsSaving => false;

        public bool IsReadOnlyMode => false;

        public void ShowHexView() { }

        public void HideHexView() { }

        public void ShowError(string message) { }

        public bool ConfirmWarning(string question) { return true; }
    }

    private sealed class FakeHexView : IRemoteHexView
    {
        public HexDocument Document { get; set; }

        public bool ReadOnly { get; set; }

        public bool Focus() { return true; }

        public void Invalidate() { }
    }

    // The editor: the questions, the threads (kept in queues), the view, and the source of the bytes to save.
    private sealed class FakeEditor : IRemoteFileFlowHost, IRemoteSaveSource
    {
        public World World;

        public Func<string, PromptButtons, PromptIcon, PromptAnswer> Answer = (text, buttons, icon) => PromptAnswer.Cancel;

        public readonly List<string> Questions = new List<string>();

        public readonly List<string> Errors = new List<string>();

        public readonly List<string> Informed = new List<string>();

        public readonly Queue<Action> Threads = new Queue<Action>();

        public readonly Queue<Action> Pool = new Queue<Action>();

        public readonly Queue<Action> Ui = new Queue<Action>();

        public bool Closed;

        public bool InText = true;

        public bool OfferDisabled;

        public bool Backup;

        public bool DisableCheck = true;

        public ValidationIssue IssueGoneTo;

        public bool TextReadOnly;

        public RemoteSaveJob LastJob;

        public RemoteSaveJob PrepareSave()
        {
            Log.Add("prepare");
            LastJob = World.Session.PrepareSave(this, () => new RemoteSaveOptions
            {
                TemporaryDirectory = "/tmp",
                BackupBeforeSave = Backup,
                DisableConfigCheck = DisableCheck,
                IsNewFile = World.Loader.IsNewFile,
                IsWindowsHost = World.Server.IsWindows
            });
            return LastJob;
        }

        public PromptAnswer Ask(string text, PromptButtons buttons, PromptIcon icon)
        {
            Log.Add("ask " + buttons + " " + icon);
            Questions.Add(text);
            return Answer(text, buttons, icon);
        }

        public void Inform(string text)
        {
            Log.Add("inform");
            Informed.Add(text);
        }

        public void ReportError(Exception exception)
        {
            Log.Add("error");
            Errors.Add(exception.Message);
        }

        public bool ShowIssuesInText(List<ValidationIssue> issues)
        {
            Log.Add("issues " + issues.Count);
            return InText;
        }

        public void GoToIssue(ValidationIssue issue)
        {
            Log.Add("go to issue " + issue.Line);
            IssueGoneTo = issue;
        }

        public bool ServiceReloadOfferDisabled => OfferDisabled;

        public void ThrowIfConnectionLost()
        {
            Log.Add("connection check");
            World.Server.ThrowIfConnectionLost();
        }

        public void StartThread(Action work, string name)
        {
            Log.Add("thread " + name);
            Threads.Enqueue(work);
        }

        public void QueueWork(Action work)
        {
            Pool.Enqueue(work);
        }

        public bool Post(Action action)
        {
            if (Closed)
            {
                Log.Add("post refused");
                return false;
            }
            Ui.Enqueue(action);
            return true;
        }

        public void ClearMessages()
        {
            Log.Add("clear messages");
        }

        public void KeepViewForReload()
        {
            Log.Add("keep view");
        }

        public void StartLoading()
        {
            Log.Add("start loading");
        }

        public void SetTextReadOnly(bool readOnly)
        {
            Log.Add("text read-only:" + readOnly);
            TextReadOnly = readOnly;
        }

        public void RaiseLoadFailed()
        {
            Log.Add("load failed");
        }

        public void DiscardDraft()
        {
            Log.Add("discard draft");
        }

        // The source of the bytes to save: the text of the content.
        bool IRemoteSaveSource.HexMode => false;

        object IRemoteSaveSource.HexVersion => null;

        byte[] IRemoteSaveSource.HexBytes()
        {
            return null;
        }

        string IRemoteSaveSource.GetText()
        {
            return World.Text.TextDocument.GetText();
        }

        object IRemoteSaveSource.TextVersion => World.Text.Version;

        LineEndingKind IRemoteSaveSource.LineEndings => LineEndingKind.Lf;

        TextFileEncoding IRemoteSaveSource.FileEncoding => TextFileEncodings.Utf8;

        void IRemoteSaveSource.ConvertLineEndingsToLf() { }

        void IRemoteSaveSource.SaveWithoutByteOrderMark() { }

        bool IRemoteSaveSource.Confirm(string question)
        {
            return true;
        }

        void IRemoteSaveSource.ClearMessages() { }

        SyntaxHighlighting.Language IRemoteSaveSource.DetectSyntax()
        {
            return SyntaxHighlighting.Language.Bash;
        }

        string IRemoteSaveSource.FirstLine()
        {
            return World.Text.TextDocument.GetLineText(0);
        }
    }

    private sealed class World
    {
        public readonly string Path;

        public FakeServer Server = new FakeServer();

        public FakeEditor Editor = new FakeEditor();

        public FakeText Text = new FakeText();

        public RemoteFileSession Session;

        public RemoteFileContent Content;

        public RemoteFileLoader Loader;

        public RemoteFileFlow Flow;

        public World(string path = "/srv/app/data.txt")
        {
            Path = path;
            Editor.World = this;
            RemoteFileServer server = new RemoteFileServer(Server, path);
            Session = new RemoteFileSession(path);
            Content = new RemoteFileContent(path, Text, new FakeHexView(), Session);
            Loader = new RemoteFileLoader(server, path, null, null, null, () => false);
            RemoteFileSaver saver = new RemoteFileSaver(Server, server, path, () => Loader.SudoOnSave);
            Flow = new RemoteFileFlow(path, Editor, server, saver, Session, Content, Loader);
            Flow.SavingChanged += (sender, e) => Log.Add("event saving:" + Flow.IsSaving);
            Flow.FormatChanged += (sender, e) => Log.Add("event format");
        }

        // The file is loaded with this text, and edited.
        public World Loaded(string text, bool edited = true)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Content.ApplyContent(bytes, TextFileEncodings.Utf8, text);
            Flow.FinishLoading(Server.Signature);
            if (edited)
            {
                Text.Edit();
            }
            Log.Clear();
            return this;
        }

        // Runs what the pool, the threads and the UI thread were given, until nothing is left: the pool first, then the threads, then the
        // UI thread, one at a time.
        public void Run()
        {
            while (Editor.Threads.Count + Editor.Pool.Count + Editor.Ui.Count > 0)
            {
                if (Editor.Pool.Count > 0)
                {
                    Editor.Pool.Dequeue()();
                }
                else if (Editor.Threads.Count > 0)
                {
                    Editor.Threads.Dequeue()();
                }
                else
                {
                    Editor.Ui.Dequeue()();
                }
            }
        }
    }

    private static void BeforeTheLoad()
    {
        Console.WriteLine("-- before the file is loaded");
        World w = new World();
        Check(!w.Flow.IsLoaded && !w.Flow.IsReloading && !w.Flow.IsSaving, "not loaded, not reloading, not saving");
        Check(!w.Flow.CanSave && !w.Flow.CanToggleHexMode && !w.Flow.CanReopenWithEncoding && !w.Flow.CanFollowLog, "nothing can be saved, switched, read again or followed");
        Check(w.Flow.TrySave(), "closing the tab: nothing of the user's to save, it may close");
        w.Flow.Save();
        w.Flow.SaveInBackground();
        w.Flow.Reload();
        Equal(TakeLog(), "", "a save, a background save and a reload do nothing");
    }

    private static void Loaded()
    {
        Console.WriteLine("-- a loaded file");
        World w = new World();
        w.Session.BackupDone = true;
        w.Content.ApplyContent(Encoding.UTF8.GetBytes("x\n"), TextFileEncodings.Utf8, "x\n");
        w.Flow.FinishLoading("v1");
        Check(w.Flow.IsLoaded && !w.Flow.IsReloading && !w.Session.BackupDone, "loaded, the backup is made again by the next save");
        Check(w.Flow.CanSave && w.Flow.CanToggleHexMode && w.Flow.CanReopenWithEncoding && w.Flow.CanFollowLog, "it can be saved, switched, read again and followed");
        w.Loader.IsReadOnlyMode = true;
        Check(!w.Flow.CanSave && w.Flow.CanToggleHexMode && w.Flow.CanReopenWithEncoding, "a file that is only read cannot be saved, but switched and read again");
        w.Loader.IsReadOnlyMode = false;
        w.Loader.IsPreview = true;
        Check(!w.Flow.CanReopenWithEncoding && w.Flow.CanFollowLog, "a preview cannot be read again in another encoding, but followed");
        w.Loader.IsPreview = false;
        w.Loader.IsNewFile = true;
        Check(!w.Flow.CanReopenWithEncoding && !w.Flow.CanFollowLog && w.Flow.CanSave, "a new file cannot be read again or followed, but saved");
        w.Loader.IsNewFile = false;
        w.Server.IsWindows = true;
        Check(!w.Flow.CanFollowLog && w.Flow.CanReopenWithEncoding, "a file of a Windows host cannot be followed");
        w.Server.IsWindows = false;
        w.Content.ApplyContent(null, TextFileEncodings.Utf8, "");
        Check(!w.Flow.CanReopenWithEncoding, "no bytes from the server: nothing to read again");
    }

    private static void SaveOnClose()
    {
        Console.WriteLine("-- a save when the tab closes");
        World w = new World().Loaded("text\n");
        w.Server.SignatureAfterReplace = "v2";
        w.Flow.Save();
        Equal(TakeLog(), "prepare, signature, upload, replace, signature, text saved, discard draft, remove temporary", "the conflict check, the upload and the replacement, the new signature, the text saved, the draft dropped, the temporary file removed");
        Equal(Encoding.UTF8.GetString(w.Server.Uploaded), "text\n", "the text is uploaded");
        Check(w.Editor.LastJob.Validator == null, "a save on close does not run the check of the file");
        Check(w.Content.RawBytes.SequenceEqual(Encoding.UTF8.GetBytes("text\n")), "the saved bytes are the file's now");

        // The next save compares with the new signature: no conflict.
        w.Text.Edit();
        w.Flow.Save();
        Check(w.Editor.Questions.Count == 0, "the signature of the save is the one the next save compares with");
        Log.Clear();

        // Somebody changed the file: the editor asks.
        w.Server.Signature = "theirs";
        w.Text.Edit();
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.No;
        Throws<OperationCanceledException>(() => w.Flow.Save(), "answered No: the save is cancelled");
        Equal(TakeLog(), "prepare, signature, ask YesNo Warning, remove temporary", "the conflict question; the temporary file is removed all the same");
        Equal(w.Editor.Questions.Last(), "/srv/app/data.txt has been changed on the server since it was opened in the editor. Overwrite it with your version?", "the question");
        Check(w.Text.Modified, "the changes stay in the editor");
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.Yes;
        w.Flow.Save();
        Equal(TakeLog(), "prepare, signature, ask YesNo Warning, upload, replace, signature, text saved, discard draft, remove temporary", "answered Yes: the file is overwritten");

        // The answer "overwrite" of a background conflict: the next save does not compare.
        w.Server.Signature = "somebody else's";
        w.Session.ForceOverwrite = true;
        w.Text.Edit();
        Log.Clear();
        w.Flow.Save();
        Equal(TakeLog(), "prepare, upload, replace, signature, text saved, discard draft, remove temporary", "a save after the answer to overwrite: no conflict check");

        // A save on close does not run the check of the file, even where there is one.
        w = new World("/srv/app/run.sh").Loaded("#!/bin/sh\nif x; then\n");
        w.Editor.DisableCheck = false;
        w.Server.ValidationStatus = 2;
        w.Flow.Save();
        Check(!TakeLog().Contains("check"), "a shell script saved on close: no check");

        // The backup flag and a new file.
        w = new World().Loaded("text\n");
        w.Editor.Backup = true;
        w.Flow.Save();
        Check(w.Session.BackupDone, "a save with the backup copy: the next one makes none");
        w = new World().Loaded("text\n");
        w.Loader.IsNewFile = true;
        w.Server.Signature = null;
        w.Flow.Save();
        Check(!w.Loader.IsNewFile, "a new file is no longer new once it is saved");
        Log.Clear();

        // The draft is dropped only when nothing is modified after the save.
        w = new World().Loaded("text\n");
        w.Content.EnterHexMode();
        w.Content.HexDocument.ReplaceByte(0, (byte)'T');
        Log.Clear();
        w.Flow.Save();
        Check(!TakeLog().Contains("discard draft"), "the hex view holds other changes: the draft stays");
    }

    private static void SaveRules()
    {
        Console.WriteLine("-- when a save on close runs, and what it says");
        World w = new World().Loaded("text\n");
        w.Loader.IsReadOnlyMode = true;
        w.Flow.Save();
        Equal(TakeLog(), "", "a file that is only read: nothing");
        w.Loader.IsReadOnlyMode = false;
        w.Flow.Reload();
        Log.Clear();
        InvalidOperationException reloading = Throws<InvalidOperationException>(() => w.Flow.Save(), "a save during a reload throws");
        Equal(reloading.Message, "The file /srv/app/data.txt is being reloaded. Try again when it has been loaded.", "and says that the file is being reloaded");
        Check(!w.Flow.TrySave(), "closing the tab during a reload: the tab stays");
        Equal(TakeLog(), "error", "and the error is shown");

        // A refused write: sudo is offered.
        w = new World().Loaded("text\n");
        w.Server.ReplaceStatus = 1;
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.No;
        Check(!w.Flow.TrySave(), "answered No to sudo: the tab stays");
        Equal(TakeLog(), "prepare, signature, upload, replace, ask YesNo Question, remove temporary", "a refused write: the question about sudo, nothing is reported");
        Equal(w.Editor.Questions.Last(), "Failed to overwrite /srv/app/data.txt. Try again with sudo?", "the question");
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.Yes;
        Check(w.Flow.TrySave(), "answered Yes: saved through sudo");
        Equal(TakeLog(), "prepare, signature, upload, replace, ask YesNo Question, sudo, signature, text saved, discard draft, remove temporary", "sudo replaces the file, the signature is fetched");
        w.Loader.SudoOnSave = true;
        w.Text.Edit();
        w.Flow.Save();
        Equal(TakeLog(), "prepare, signature, upload, sudo, signature, text saved, discard draft, remove temporary", "a file that is written with sudo: no plain attempt, no question");
        w.Loader.SudoOnSave = false;
        w.Loader.IsNewFile = true;
        w.Server.Signature = null;
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.No;
        w.Flow.TrySave();
        Equal(w.Editor.Questions.Last(), "Failed to create /srv/app/data.txt. Try again with sudo?", "a new file: failed to create");
        Log.Clear();
        w.Server.Sudo = new SudoRunResult { ExitStatus = 1, HasExitStatus = true };
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.Yes;
        Check(!w.Flow.TrySave(), "sudo that fails: the tab stays");
        Check(TakeLog().Contains("error") && w.Editor.Errors.Last().Length > 0, "and the error is reported");
    }

    private static void Background()
    {
        Console.WriteLine("-- a save in the background");
        World w = new World().Loaded("text\n");
        w.Server.SignatureAfterReplace = "v2";
        w.Flow.SaveInBackground();
        Equal(TakeLog(), "prepare, event saving:True, thread Saving /srv/app/data.txt", "the bytes are taken, the save is told, a thread of its own is started");
        Check(w.Flow.IsSaving && !w.Flow.CanToggleHexMode && w.Flow.CanSave, "saving: the hex view cannot be switched, another save can be asked for");
        w.Flow.SaveInBackground();
        Equal(TakeLog(), "", "a save asked for meanwhile waits");
        w.Text.Edit();
        w.Run();
        Equal(TakeLog(), "signature, upload, replace, signature, text saved, event saving:False, prepare, event saving:True, thread Saving /srv/app/data.txt, remove temporary, signature, upload, replace, signature, text saved, discard draft, event saving:False, remove temporary",
            "the thread uploads, the UI thread completes the save (the text was edited meanwhile: still modified), then the save asked for meanwhile runs");
        Check(!w.Flow.IsSaving, "not saving at the end");

        // A save asked for meanwhile with nothing modified is dropped.
        w.Flow.SaveInBackground();
        w.Flow.SaveInBackground();
        w.Text.Modified = false;
        Log.Clear();
        w.Run();
        Check(!TakeLog().Contains("prepare"), "a save asked for meanwhile with nothing to save does not run");

        // Not saved while the file is loading or is only read.
        w = new World();
        w.Flow.SaveInBackground();
        w.Loaded("x\n");
        w.Loader.IsReadOnlyMode = true;
        w.Flow.SaveInBackground();
        Equal(TakeLog(), "", "before the load and for a file that is only read: nothing");

        // The editor was closed before the end: the temporary file is removed by the thread.
        w = new World().Loaded("text\n");
        w.Flow.SaveInBackground();
        w.Editor.Closed = true;
        Log.Clear();
        w.Run();
        Equal(TakeLog(), "signature, upload, replace, signature, post refused, remove temporary", "a closed editor: the end is not posted, the thread removes the temporary file");

        // An error of the thread is reported.
        w = new World().Loaded("text\n");
        w.Server.ReplaceStatus = RemoteSaveCommand.ExitNoSpace;
        w.Flow.SaveInBackground();
        Log.Clear();
        w.Run();
        Equal(TakeLog(), "signature, upload, replace, error, event saving:False, remove temporary", "the error of the thread is reported on the UI thread");
        Check(w.Text.Modified, "still modified");

        // A refused write: the sudo question on the UI thread.
        w = new World().Loaded("text\n");
        w.Server.ReplaceStatus = 1;
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.No;
        w.Flow.SaveInBackground();
        Log.Clear();
        w.Run();
        Equal(TakeLog(), "signature, upload, replace, ask YesNo Question, event saving:False, remove temporary", "answered No: nothing is reported");

        // A synchronous save while the background one runs supersedes it.
        w = new World().Loaded("text\n");
        w.Flow.SaveInBackground();
        w.Editor.Threads.Dequeue()();
        w.Text.Edit();
        w.Flow.Save();
        Log.Clear();
        w.Run();
        Equal(TakeLog(), "event saving:False, remove temporary", "the end of a superseded background save only cleans up");
        w = new World().Loaded("text\n");
        w.Server.ReplaceStatus = RemoteSaveCommand.ExitNoSpace;
        w.Flow.SaveInBackground();
        w.Editor.Threads.Dequeue()();
        w.Server.ReplaceStatus = 0;
        w.Text.Edit();
        w.Flow.Save();
        Log.Clear();
        w.Run();
        Equal(TakeLog(), "event saving:False, remove temporary", "a superseded background save that failed: its error is not reported");
    }

    private static void Conflict()
    {
        Console.WriteLine("-- a conflict found by a background save");
        World w = new World().Loaded("text\n");
        w.Server.Signature = "theirs";
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.Cancel;
        w.Flow.SaveInBackground();
        Log.Clear();
        w.Run();
        Equal(TakeLog(), "signature, ask YesNoCancel Warning, event saving:False, remove temporary", "the conflict: no upload, the question; Cancel: nothing");
        Equal(w.Editor.Questions.Last(), "/srv/app/data.txt has been changed on the server since it was opened in the editor.\n\nYes: overwrite it with your version.\nNo: load the version from the server (your changes are lost).\nCancel: do not save.", "the question");

        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.Yes;
        w.Flow.SaveInBackground();
        Log.Clear();
        w.Run();
        Equal(TakeLog(), "signature, ask YesNoCancel Warning, event saving:False, prepare, event saving:True, thread Saving /srv/app/data.txt, remove temporary, upload, replace, signature, text saved, discard draft, event saving:False, remove temporary",
            "answered Yes: the save runs again without the conflict check");

        w = new World().Loaded("text\n");
        w.Loader.IsNewFile = true;
        w.Server.Signature = "created";
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.No;
        w.Flow.SaveInBackground();
        w.Session.ForceOverwrite = true;
        w.Session.SkipValidation = true;
        Log.Clear();
        w.Run();
        Equal(TakeLog(), "signature, ask YesNoCancel Warning, event saving:False, clear messages, keep view, event format, start loading, remove temporary", "answered No: the version of the server is loaded");
        Check(w.Flow.IsReloading && !w.Loader.IsNewFile && !w.Session.ForceOverwrite && !w.Session.SkipValidation, "a reload, the file is not new any more, the next save asks again");
        Check(w.Editor.Questions.Last().StartsWith("/srv/app/data.txt has been created on the server since you started this new file."), "a new file that somebody created: the question says so");
    }

    private static void Validation()
    {
        Console.WriteLine("-- the check of the file");
        World w = new World("/srv/app/run.sh").Loaded("#!/bin/sh\necho 1\nif x; then\n");
        w.Editor.DisableCheck = false;
        w.Server.ValidationStatus = 2;
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.No;
        w.Flow.SaveInBackground();
        string temporary = w.Editor.LastJob.TemporaryPath;
        w.Server.ValidationOutput = new List<string> { temporary + ": line 4: syntax error: unexpected end of file" };
        Log.Clear();
        w.Run();
        Equal(TakeLog(), "signature, upload, check, issues 1, ask YesNo Warning, go to issue 4, event saving:False, remove temporary", "a problem: shown, asked about; No: the caret goes to it");
        Equal(w.Editor.Questions.Last(), "The check (shell -n) found 1 problem in /srv/app/run.sh, they are shown in the text.\n\nThe file on the server has not been changed.\n\nYes: save it anyway.\nNo: do not save, go to line 4.", "the question");
        Check(w.Text.Modified, "nothing is saved");

        w.Editor.InText = false;
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.Yes;
        w.Server.ValidationOutput = new List<string> { "x" };
        w.Flow.SaveInBackground();
        w.Server.ValidationOutput = new List<string> { w.Editor.LastJob.TemporaryPath + ": line 4: syntax error", w.Editor.LastJob.TemporaryPath + ": line 2: other" };
        Log.Clear();
        w.Run();
        Equal(w.Editor.Questions.Last(), "The check (shell -n) found 2 problems in /srv/app/run.sh.\n\nThe file on the server has not been changed.\n\nYes: save it anyway.\nNo: do not save, go to line 2.", "two problems not shown in the text");
        Equal(TakeLog(), "signature, upload, check, issues 2, ask YesNo Warning, event saving:False, prepare, event saving:True, thread Saving /srv/app/data.txt".Replace("data.txt", "run.sh") + ", remove temporary, signature, upload, replace, signature, text saved, discard draft, event saving:False, remove temporary",
            "answered Yes: saved again without the check");

        // Saved although nothing was edited: the answer Yes saves again all the same.
        w = new World("/srv/app/run.sh").Loaded("#!/bin/sh\nif x; then\n", edited: false);
        w.Editor.DisableCheck = false;
        w.Server.ValidationStatus = 2;
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.Yes;
        w.Flow.SaveInBackground();
        w.Server.ValidationOutput = new List<string> { w.Editor.LastJob.TemporaryPath + ": line 3: syntax error" };
        Log.Clear();
        w.Run();
        Check(TakeLog().Contains("prepare") && w.Server.Uploaded != null, "an unmodified text that the check rejects is saved again when the answer is Yes");
    }

    private static void ServiceReload()
    {
        Console.WriteLine("-- the offer to reload the service that reads the file");
        World w = new World("/etc/nginx/app.conf").Loaded("# x\n");
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.No;
        w.Flow.SaveInBackground();
        Log.Clear();
        w.Run();
        Equal(TakeLog(), "signature, upload, replace, signature, text saved, discard draft, event saving:False, ask YesNo Question, remove temporary", "after a save the reload of nginx is offered once the save has ended (the temporary file is removed on the pool)");
        Equal(w.Editor.Questions.Last(), "/etc/nginx/app.conf was saved.\n\nReload nginx now so that the change takes effect?", "the question");

        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.Yes;
        w.Text.Edit();
        w.Flow.SaveInBackground();
        Log.Clear();
        w.Run();
        Equal(TakeLog(), "signature, upload, replace, signature, text saved, discard draft, event saving:False, ask YesNo Question, remove temporary, service reload, inform", "answered Yes: the reload runs on the pool and its success is told on the UI thread");
        Equal(w.Editor.Informed.Last(), "nginx reloaded.", "nginx reloaded");

        w.Server.ServiceStatus = 1;
        w.Server.ServiceOutput = new List<string> { "nginx: not found", "", "3", "4", "5", "6", "7", "8", "9" };
        w.Editor.Answer = (text, buttons, icon) => text.StartsWith("Could not") ? PromptAnswer.No : PromptAnswer.Yes;
        w.Text.Edit();
        w.Flow.SaveInBackground();
        Log.Clear();
        w.Run();
        Check(TakeLog().EndsWith("service reload, connection check, ask YesNo Warning"), "a reload that fails: the connection is checked, sudo is offered");
        Equal(w.Editor.Questions.Last(), "Could not reload nginx (exit code 1):\n\nnginx: not found\n\n3\n4\n5\n6\n7\n8\n\nTry again with sudo?", "the question has the first eight lines of the output");
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.Yes;
        w.Text.Edit();
        w.Flow.SaveInBackground();
        Log.Clear();
        w.Run();
        Check(TakeLog().EndsWith("ask YesNo Warning, sudo, inform"), "answered Yes: reloaded through sudo");

        w.Server.ServiceThrows = true;
        w.Text.Edit();
        w.Flow.SaveInBackground();
        w.Run();
        Equal(w.Editor.Questions.Last(), "Could not reload nginx:\n\n\nTry again with sudo?", "no answer from the command: no exit code");
        w.Server.ServiceThrows = false;
        w.Server.ConnectionLost = true;
        w.Text.Edit();
        w.Flow.SaveInBackground();
        Log.Clear();
        w.Run();
        Check(TakeLog().EndsWith("service reload, connection check, error"), "a lost connection is reported, sudo is not offered");
        w.Server.ConnectionLost = false;

        w.Editor.OfferDisabled = true;
        w.Text.Edit();
        w.Flow.SaveInBackground();
        Log.Clear();
        w.Run();
        Check(!TakeLog().Contains("ask"), "the offer switched off: no question");
        w.Editor.OfferDisabled = false;

        // A save asked for while saving offers it once, after itself.
        w.Text.Edit();
        w.Editor.Answer = (text, buttons, icon) => PromptAnswer.No;
        w.Flow.SaveInBackground();
        w.Flow.SaveInBackground();
        w.Editor.Threads.Dequeue()();
        w.Text.Edit();
        w.Editor.Questions.Clear();
        w.Run();
        Equal(w.Editor.Questions.Count, 1, "a save asked for while one ran, with an edit meanwhile: the offer once, after the second save");

        w = new World("/srv/app/data.txt").Loaded("x\n");
        w.Flow.SaveInBackground();
        w.Run();
        Check(w.Editor.Questions.Count == 0, "a file no service reads: no offer");
        w = new World("/etc/nginx/app.conf").Loaded("x\n");
        w.Server.IsWindows = true;
        w.Flow.SaveInBackground();
        w.Run();
        Check(w.Editor.Questions.Count == 0, "a Windows host: no offer");
    }

    private static void Reloads()
    {
        Console.WriteLine("-- a reload, and a load that failed");
        World w = new World().Loaded("x\n");
        w.Flow.Reload();
        Equal(TakeLog(), "clear messages, keep view, event format, start loading", "the messages go, the view is kept, the format told, the loading started");
        Check(w.Flow.IsReloading && w.Flow.IsLoaded && !w.Flow.CanSave && !w.Flow.CanToggleHexMode && !w.Flow.CanReopenWithEncoding && !w.Flow.CanFollowLog, "reloading: nothing can be saved, switched, read again or followed");
        w.Flow.Reload();
        Equal(TakeLog(), "", "a second reload meanwhile does nothing");
        w.Flow.FinishLoading("v3");
        Check(!w.Flow.IsReloading && w.Flow.CanSave, "the new text is in");

        w.Loader.IsNewFile = true;
        w.Flow.Reload();
        w.Loader.IsNewFile = false;
        w.Loader.IsPreview = true;
        w.Flow.Reload();
        w.Loader.IsPreview = false;
        w.Flow.SaveInBackground();
        w.Flow.Reload();
        Equal(TakeLog(), "prepare, event saving:True, thread Saving /srv/app/data.txt", "no reload of a new file, of a preview, or while a save runs");
        w.Run();
        Log.Clear();

        w.Flow.Reload();
        Log.Clear();
        w.Flow.LoadFailed(new IOException("gone"));
        Equal(TakeLog(), "error, text read-only:False, event format", "a reload that fails: the error, the text can be edited again, the format told");
        Check(!w.Flow.IsReloading && w.Flow.CanSave, "the text of the user is kept and can be saved");
        w.Loader.IsReadOnlyMode = true;
        w.Flow.Reload();
        Log.Clear();
        w.Flow.LoadFailed(new IOException("gone"));
        Equal(TakeLog(), "error, text read-only:True, event format", "a file that is only read stays read-only");

        w = new World();
        w.Flow.LoadFailed(new IOException("no such file"));
        Equal(TakeLog(), "error, load failed", "a first load that fails: the error, and the tab closes");
        Equal(w.Editor.Errors.Last(), "no such file", "the error of the load");
    }

    private static int Main()
    {
        BeforeTheLoad();
        Loaded();
        SaveOnClose();
        SaveRules();
        Background();
        Conflict();
        Validation();
        ServiceReload();
        Reloads();
        Console.WriteLine();
        Console.WriteLine("RemoteFileFlowTests: all " + _passed + " checks passed.");
        return 0;
    }
}
