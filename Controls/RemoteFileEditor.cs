using SmarTTY.Ssh;
using SmarTTY.Forms;
using SmarTTY.IO;
using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Controls;

public partial class RemoteFileEditor : CodeEditorPP
{

    private ITerminalSessionContext _context;

    public readonly string RemotePath;

    private readonly TextDocument.TextPosition _initialCaret;

    private Thread _loadThread;

    private GlobalSettings _settings;

    private Action<Exception> _errorHandler;

    // Minimal interval between previews of a file that is still being downloaded.
    private const int PreviewIntervalMs = 500;

    // Permissions of the temporary copy uploaded on save: it may hold a file only root can read.
    private const int TemporaryFileMode = 0x180; // 0600

    // The user's answer to "Convert CRLF line endings to LF?", so that it is asked once per file.
    private bool? _convertCrLf;

    /// <summary>The file did not exist when the editor was opened: it is created by the first save.</summary>
    public bool IsNewFile { get; private set; }

    /// <summary>
    /// The text cannot be changed: the user chose to read a file that is big or that he has no right to write, or only
    /// the beginning of a huge file is shown (<see cref="IsPreview"/>).
    /// </summary>
    public bool IsReadOnlyMode { get; private set; }

    /// <summary>Only the first <see cref="EditorFilePolicy.PreviewBytes"/> of a file that is too big to edit are shown.</summary>
    public bool IsPreview { get; private set; }

    // The user agreed to write the file with sudo (asked when the file was opened): a save does not try the plain write first.
    private bool _sudoOnSave;

    // Unsaved changes are written to a draft on this computer every few seconds (see EditorDraftStore), so that they
    // survive a lost connection and a closed program. The drafts of all editors live in one folder.
    private static readonly EditorDraftStore Drafts = new EditorDraftStore(Path.Combine(GlobalSettings.DataDirectory, "Drafts"), new DpapiDraftProtector());

    private const int DraftIntervalMs = 10000;

    // Drafts older than this are removed when an editor starts.
    private static readonly TimeSpan DraftMaxAge = TimeSpan.FromDays(30);

    private static int _draftCleanupDone;

    private readonly string _draftKey;

    // Two editors of the same file in one session must not share a draft: the save or the discard of one would remove the
    // draft of the other, and neither would see the other's text. The first editor of a file has the plain key, the next ones
    // the key with a number; the number is free again when the editor is closed.
    private static readonly HashSet<string> ClaimedDraftKeys = new HashSet<string>();

    private static string ClaimDraftKey(string baseKey)
    {
        lock (ClaimedDraftKeys)
        {
            string key = baseKey;
            for (int number = 2; !ClaimedDraftKeys.Add(key); number++)
            {
                key = baseKey + "-" + number.ToString(CultureInfo.InvariantCulture);
            }
            return key;
        }
    }

    private static void ReleaseDraftKey(string key)
    {
        lock (ClaimedDraftKeys)
        {
            ClaimedDraftKeys.Remove(key);
        }
    }

    private string _baseHash = "";

    private System.Windows.Forms.Timer _draftTimer;

    // The text version the draft on disk holds (null version = the text as opened is not a draft).
    private volatile bool _draftWritten;

    private object _draftVersion;

    // Every draft write or removal takes a number; one that is no longer the latest by the time it runs is skipped, so
    // a late write cannot bring back the draft of a text that has been saved meanwhile.
    private int _draftSequence;

    private readonly object _draftLock = new object();

    public event EventHandler LoadFailed;

    /// <summary>Raised on the UI thread once the whole file has been downloaded and the editor became editable.</summary>
    public event EventHandler LoadCompleted;

    public RemoteFileEditor()
    {
        InitializeComponent();
    }

    public RemoteFileEditor(ITerminalSessionContext terminalSessionContext, string text, GlobalSettings globalSettings, TextDocument.TextPosition textPosition, Action<Exception> action = null)
        : this()
    {
        this._context = terminalSessionContext;
        RemotePath = text;
        _draftKey = ClaimDraftKey(EditorDraftStore.KeyFor(terminalSessionContext?.TitleWithUserName, text));
        if (Interlocked.Exchange(ref _draftCleanupDone, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem((object state) => Drafts.DeleteOlderThan(DraftMaxAge));
        }
        Disposed += (object sender, EventArgs e) =>
        {
            _draftTimer?.Dispose();
            ReleaseDraftKey(_draftKey);
        };
        ((EditableTextBrowser)this).ReadOnly = true;
        _initialCaret = textPosition;
        _loadThread = new Thread(LoadFile);
        _loadThread.Start();
        this._settings = globalSettings;
        _errorHandler = action;
    }

    private void ReportError(Exception exception)
    {
        if (_errorHandler != null)
        {
            _errorHandler(exception);
        }
        else
        {
            MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    private void LoadFile()
    {
        try
        {
            if (!RemotePathExists(RemotePath))
            {
                // Opening a path that does not exist starts a new file, as in vi or nano.
                string directory = RemotePathUtils.GetDirectoryName(RemotePath);
                if (!string.IsNullOrEmpty(directory) && !RemotePathExists(directory))
                {
                    throw new RemoteOperationException("Cannot create " + RemotePath + ": the directory " + directory + " does not exist.");
                }
                CheckWriteAccess(string.IsNullOrEmpty(directory) ? "/" : directory, forNewFile: true);
                IsNewFile = true;
                BeginInvoke((ThreadStart)(() => FinishLoading("")));
                return;
            }
            CheckWriteAccess(RemotePath, forNewFile: false);
            StringBuilder content = new StringBuilder();
            using (ScpDownloadStream scpDownloadStream = _context.Session.OpenDownload(RemotePath))
            {
                // The size is known from the first SCP header, before any data is read.
                long limit = ChooseLoadLimit(scpDownloadStream.Length);
                long received = 0;
                Utf8StreamDecoder utf8StreamDecoder = new Utf8StreamDecoder();
                byte[] bytes = new byte[65536];
                DateTime lastPreview = DateTime.Now;
                while (true)
                {
                    int value = scpDownloadStream.Read(bytes, 0, bytes.Length);
                    if (value <= 0)
                    {
                        break;
                    }
                    if (limit > 0 && received + value > limit)
                    {
                        // A preview of a huge file ends at the limit; the rest is not downloaded.
                        value = (int)(limit - received);
                    }
                    received += value;
                    if (value > 0)
                    {
                        content.Append(utf8StreamDecoder.Decode(bytes, 0, value));
                    }
                    if (limit > 0 && received >= limit)
                    {
                        break;
                    }
                    // Show the partially downloaded text now and then. Re-parsing and re-highlighting the
                    // whole document after every 64 KB block made opening large files quadratic.
                    if ((DateTime.Now - lastPreview).TotalMilliseconds >= PreviewIntervalMs)
                    {
                        lastPreview = DateTime.Now;
                        string preview = content.ToString();
                        BeginInvoke((ThreadStart)(() =>
                        {
                            try
                            {
                                EditableTextDocument editableTextDocument = new EditableTextDocument
                                {
                                    DefaultLineEnding = TextDocument.LineEnding.Lf
                                };
                                editableTextDocument.SetText(preview);
                                SetDocument(editableTextDocument);
                            }
                            catch
                            {
                            }
                        }));
                    }
                }
            }
            string text = content.ToString();
            BeginInvoke((ThreadStart)(() => FinishLoading(text)));
        }
        catch (OperationCanceledException)
        {
            // The user chose not to open the file: the tab closes without an error message.
            try
            {
                BeginInvoke((ThreadStart)(() => LoadFailed?.Invoke(this, EventArgs.Empty)));
            }
            catch
            {
            }
        }
        catch (Exception exception)
        {
            BeginInvoke((ThreadStart)(() =>
            {
                try
                {
                    ReportError(exception);
                    LoadFailed?.Invoke(this, EventArgs.Empty);
                }
                catch
                {
                }
            }));
        }
    }

    /// <summary>Shows a question from the loading thread; the answer of a closed editor is "cancel".</summary>
    private DialogResult Ask(string text, MessageBoxButtons buttons)
    {
        if (IsDisposed)
        {
            return DialogResult.Cancel;
        }
        return (DialogResult)Invoke((Func<DialogResult>)(() => MessageBox.Show(this, text, "SmarTTY", buttons, MessageBoxIcon.Warning)));
    }

    /// <summary>
    /// Warns when the user cannot write the file (or, for a new file, its directory) before he types anything: he may
    /// choose to save with sudo (asked for at the save), to read only, or not to open the file. Loading thread.
    /// </summary>
    private void CheckWriteAccess(string target, bool forNewFile)
    {
        if (_context.Session.RunCommandChecked("test -w " + RemotePathUtils.ShellQuote(target), requireSuccess: true) != null)
        {
            return;
        }
        // No answer is not "not writable": without a connection nothing is known.
        _context.Session.ThrowIfConnectionLost();
        string what = forNewFile ? "the directory " + target + ", where this new file would be created" : target;
        switch (Ask("You do not have permission to write to " + what + ".\n\n"
            + "Yes: edit the text and save it with sudo (the password is asked when you save).\n"
            + "No: open the file read-only.\n"
            + "Cancel: do not open the file.", MessageBoxButtons.YesNoCancel))
        {
            case DialogResult.Yes:
                _sudoOnSave = true;
                break;
            case DialogResult.No:
                IsReadOnlyMode = true;
                break;
            default:
                throw new OperationCanceledException();
        }
    }

    /// <summary>
    /// Decides how much of a file of <paramref name="length"/> bytes is loaded: 0 for all of it, otherwise the number of
    /// bytes of a read-only preview. A big file is opened only after a question. Loading thread.
    /// </summary>
    private long ChooseLoadLimit(long length)
    {
        string size = StringUtils.FormatSize(length, appendUnit: true);
        switch (EditorFilePolicy.Classify(length))
        {
            case LargeFileAction.Warn:
                if (IsReadOnlyMode)
                {
                    if (Ask(RemotePath + " is " + size + ". A large file makes the editor slow and takes a lot of memory.\n\nOpen it read-only?", MessageBoxButtons.OKCancel) != DialogResult.OK)
                    {
                        throw new OperationCanceledException();
                    }
                    return 0;
                }
                switch (Ask(RemotePath + " is " + size + ". A large file makes the editor slow and takes a lot of memory.\n\n"
                    + "Yes: open it for editing.\n"
                    + "No: open it read-only (safer).\n"
                    + "Cancel: do not open it.", MessageBoxButtons.YesNoCancel))
                {
                    case DialogResult.Yes:
                        return 0;
                    case DialogResult.No:
                        IsReadOnlyMode = true;
                        return 0;
                    default:
                        throw new OperationCanceledException();
                }
            case LargeFileAction.PreviewOnly:
                if (Ask(RemotePath + " is " + size + ", too large to edit here.\n\nOpen the first " + StringUtils.FormatSize(EditorFilePolicy.PreviewBytes, appendUnit: true) + " read-only?", MessageBoxButtons.YesNo) != DialogResult.Yes)
                {
                    throw new OperationCanceledException();
                }
                IsReadOnlyMode = true;
                IsPreview = true;
                return EditorFilePolicy.PreviewBytes;
            default:
                return 0;
        }
    }

    private bool RemotePathExists(string path)
    {
        if (_context.Session.RunCommandChecked("test -e " + RemotePathUtils.ShellQuote(path), requireSuccess: true) != null)
        {
            return true;
        }
        // No answer is not "does not exist": without a connection the file may be there.
        _context.Session.ThrowIfConnectionLost();
        return false;
    }

    /// <summary>Shows the downloaded text (UI thread) and makes the editor editable.</summary>
    private void FinishLoading(string text)
    {
        try
        {
            _baseHash = EditorDraftStore.HashText(text);
            bool restored = false;
            if (!IsReadOnlyMode)
            {
                string restoredText = AskToRestoreDraft(text);
                if (restoredText != null)
                {
                    text = restoredText;
                    restored = true;
                }
            }
            EditableTextDocument document = new EditableTextDocument
            {
                DefaultLineEnding = TextDocument.LineEnding.Lf
            };
            document.SetText(text);
            SetDocument(document);
            ((EditableTextBrowser)this).ReadOnly = IsReadOnlyMode;
            if (restored)
            {
                // The restored text differs from the file: it is an unsaved change, and the draft timer keeps it safe again.
                ((EditableTextBrowser)this).IsModified = true;
            }
            if (!IsReadOnlyMode)
            {
                StartDraftTimer();
            }
            LoadCompleted?.Invoke(this, EventArgs.Empty);
            try
            {
                if (_initialCaret.IsValid)
                {
                    SetCaret(_initialCaret);
                }
            }
            catch
            {
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Offers the draft of unsaved changes left by an earlier editor of this file (the connection was lost or the program
    /// ended before they were saved). Returns the text to restore, or null to open the file as it is on the server.
    /// </summary>
    private string AskToRestoreDraft(string serverText)
    {
        EditorDraft draft = Drafts.TryLoad(_draftKey);
        if (draft == null)
        {
            return null;
        }
        if (draft.Text == serverText)
        {
            // Nothing to restore: the changes were saved after all.
            QueueDraft(null);
            return null;
        }
        string changed = (!draft.IsNewFile && draft.BaseHash != _baseHash)
            ? "The file on the server has changed since then; saving the draft would overwrite those changes.\n\n"
            : "";
        string when = draft.SavedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        if (MessageBox.Show(this, "Unsaved changes of " + RemotePath + " from " + when + " were found: the connection was lost or the program was closed before they were saved.\n\n"
            + changed
            + "Yes: restore the draft.\n"
            + "No: discard it and open the file from the server.", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            return draft.Text;
        }
        QueueDraft(null);
        return null;
    }

    private void StartDraftTimer()
    {
        if (_draftTimer != null)
        {
            return;
        }
        _draftTimer = new System.Windows.Forms.Timer { Interval = DraftIntervalMs };
        _draftTimer.Tick += (object sender, EventArgs e) => WriteDraftIfChanged();
        _draftTimer.Start();
    }

    /// <summary>Writes the draft if the text changed since the last one (UI thread; the file is written on another thread).</summary>
    private void WriteDraftIfChanged()
    {
        try
        {
            if (IsDisposed || IsReadOnlyMode || !((EditableTextBrowser)this).IsModified)
            {
                return;
            }
            object version = CurrentVersion;
            if (_draftWritten && ReferenceEquals(version, _draftVersion))
            {
                return;
            }
            _draftWritten = true;
            _draftVersion = version;
            QueueDraft(new EditorDraft
            {
                RemotePath = RemotePath,
                SavedUtc = DateTime.UtcNow,
                BaseHash = _baseHash,
                IsNewFile = IsNewFile,
                Text = base.EditableDocument.GetText()
            });
        }
        catch
        {
            // A draft is a convenience: a failure to take it must not disturb the editing.
        }
    }

    /// <summary>Writes <paramref name="draft"/>, or removes the draft when it is null; the latest request wins.</summary>
    private void QueueDraft(EditorDraft draft)
    {
        int sequence = Interlocked.Increment(ref _draftSequence);
        ThreadPool.QueueUserWorkItem((object state) =>
        {
            lock (_draftLock)
            {
                if (sequence != Volatile.Read(ref _draftSequence))
                {
                    return;
                }
                try
                {
                    if (draft == null)
                    {
                        Drafts.Delete(_draftKey);
                    }
                    else
                    {
                        Drafts.Save(_draftKey, draft);
                    }
                }
                catch
                {
                    // The text on disk is not the one that was meant to be written: let the next timer tick try again (the
                    // version was marked as written before this ran). A newer request has its own outcome.
                    if (draft != null && sequence == Volatile.Read(ref _draftSequence))
                    {
                        _draftWritten = false;
                    }
                }
            }
        });
    }

    /// <summary>
    /// The user chose not to save the changes (closing the tab or the program): the draft is removed, otherwise the next
    /// opening of the file would offer them again.
    /// </summary>
    public void DiscardDraft()
    {
        _draftWritten = false;
        _draftVersion = null;
        QueueDraft(null);
    }

    public bool TrySave()
    {
        try
        {
            Save();
            return true;
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                return false;
            }
            ReportError(exception);
            return false;
        }
    }

    /// <summary>A save in progress: the text and the temporary file it is uploaded to.</summary>
    private sealed class SaveJob
    {
        public byte[] Bytes;

        public string TemporaryPath;

        public string ReplaceCommand;

        // The text version being saved (EditableTextBrowser.CurrentVersion).
        public object Version;

        // Saves started later supersede this one (see _saveGeneration).
        public int Generation;
    }

    private int _saveGeneration;

    // Set by the background thread once its upload and replace attempt finished (successfully or not).
    private ManualResetEvent _backgroundUploadDone;

    private bool _saveRequestedWhileSaving;

    /// <summary>A background save (SaveInBackground) is running.</summary>
    public bool IsSaving { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="IsSaving"/> changes.</summary>
    public event EventHandler SavingChanged;

    /// <summary>Saves the file and returns when it is written (used when a tab or the window closes).</summary>
    public void Save()
    {
        if (IsReadOnlyMode)
        {
            return;
        }
        // A background save of an older version must not overwrite this one afterwards.
        _backgroundUploadDone?.WaitOne();
        SaveJob job = PrepareSave();
        try
        {
            if (!UploadAndReplace(job))
            {
                SaveWithSudo(job);
            }
            CompleteSave(job);
        }
        finally
        {
            RemoveTemporaryFile(job);
        }
    }

    /// <summary>
    /// Saves the file without blocking the UI on a slow connection (Ctrl+S). Errors and the sudo prompt are
    /// shown when they happen; a save requested meanwhile runs once this one has finished.
    /// </summary>
    public void SaveInBackground()
    {
        if (IsReadOnlyMode)
        {
            return;
        }
        if (IsSaving)
        {
            _saveRequestedWhileSaving = true;
            return;
        }
        SaveJob job = PrepareSave();
        ManualResetEvent uploadDone = new ManualResetEvent(initialState: false);
        _backgroundUploadDone = uploadDone;
        SetSaving(true);
        Thread thread = new Thread(() =>
        {
            bool replaced = false;
            Exception error = null;
            try
            {
                replaced = UploadAndReplace(job);
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                uploadDone.Set();
            }
            try
            {
                BeginInvoke((ThreadStart)(() => FinishBackgroundSave(job, replaced, error, uploadDone)));
            }
            catch
            {
                // The editor was closed meanwhile.
                RemoveTemporaryFile(job);
            }
        })
        {
            IsBackground = true,
            Name = "Saving " + RemotePath
        };
        thread.Start();
    }

    private void FinishBackgroundSave(SaveJob job, bool replaced, Exception error, ManualResetEvent uploadDone)
    {
        try
        {
            // Superseded by a synchronous save of a newer version: only clean up.
            if (job.Generation == _saveGeneration)
            {
                if (error != null)
                {
                    ReportError(error);
                }
                else
                {
                    if (!replaced)
                    {
                        SaveWithSudo(job);
                    }
                    // The sudo prompt pumps messages: a synchronous save may have run meanwhile.
                    if (job.Generation == _saveGeneration)
                    {
                        CompleteSave(job);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        finally
        {
            ThreadPool.QueueUserWorkItem((object state) => RemoveTemporaryFile(job));
            if (_backgroundUploadDone == uploadDone)
            {
                _backgroundUploadDone = null;
            }
            uploadDone.Dispose();
            SetSaving(false);
            if (_saveRequestedWhileSaving)
            {
                _saveRequestedWhileSaving = false;
                if (((EditableTextBrowser)this).IsModified)
                {
                    SaveInBackground();
                }
            }
        }
    }

    private void SetSaving(bool saving)
    {
        IsSaving = saving;
        SavingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes the text to save (UI thread), asking once whether to convert CRLF line endings.</summary>
    private SaveJob PrepareSave()
    {
        string value = base.EditableDocument.GetText();
        if (value.Contains("\r\n"))
        {
            if (!_convertCrLf.HasValue)
            {
                _convertCrLf = MessageBox.Show("Convert CRLF line endings to LF endings?", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            }
            if (_convertCrLf.Value)
            {
                base.EditableDocument.SetLineEnding(TextDocument.LineEnding.Lf);
                value = base.EditableDocument.GetText();
            }
        }
        string temporaryPath = _settings.Editor.TemporaryDirectory + "/" + Guid.NewGuid().ToString() + ".txt";
        return new SaveJob
        {
            Bytes = Encoding.UTF8.GetBytes(value),
            TemporaryPath = temporaryPath,
            // Quote both paths: a file name with ", $ or ` must neither break the command nor expand.
            ReplaceCommand = "cat " + RemotePathUtils.ShellQuote(temporaryPath) + " > " + RemotePathUtils.ShellQuote(RemotePath),
            Version = CurrentVersion,
            Generation = ++_saveGeneration
        };
    }

    /// <summary>
    /// Uploads the text to the temporary file and copies it over the target (any thread). Returns false when
    /// the target could not be written, so that sudo can be offered.
    /// </summary>
    private bool UploadAndReplace(SaveJob job)
    {
        using (ScpChannelStream scpChannelStream = _context.Session.OpenUpload(job.TemporaryPath, job.Bytes.Length, mode: TemporaryFileMode))
        {
            scpChannelStream.Write(job.Bytes, 0, job.Bytes.Length);
        }
        if (_sudoOnSave)
        {
            // The user said at opening that this file is written with sudo: no failing plain attempt first.
            return false;
        }
        if (_context.Session.RunCommandChecked(job.ReplaceCommand, requireSuccess: true) != null)
        {
            return true;
        }
        // A lost connection is not a refused write: do not offer sudo for it.
        _context.Session.ThrowIfConnectionLost();
        return false;
    }

    /// <summary>Asks whether to retry with sudo and does so (UI thread).</summary>
    private void SaveWithSudo(SaveJob job)
    {
        if (!_sudoOnSave && MessageBox.Show("Failed to " + (IsNewFile ? "create " : "overwrite ") + RemotePath + ". Try again with sudo?", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            throw new OperationCanceledException();
        }
        string sudoCommand = "sudo sh -c " + RemotePathUtils.ShellQuote(job.ReplaceCommand);
        SshShellChannel sshShellChannel = _context.Session.OpenShellChannel(sudoCommand, "xterm");
        using (SudoConfirmationForm sudoConfirmationForm = new SudoConfirmationForm(sshShellChannel, sudoCommand))
        {
            sudoConfirmationForm.ShowModal();
        }
        try
        {
            sshShellChannel.Input.Write(new byte[1] { 3 }, 0, 1);
        }
        catch
        {
        }
        sshShellChannel.WaitClosed();
        if (sshShellChannel.ExitStatus != 0)
        {
            throw new Exception("Sudo exited with code " + sshShellChannel.ExitStatus);
        }
    }

    private void CompleteSave(SaveJob job)
    {
        IsNewFile = false;
        MarkSaved(job.Version);
        if (!((EditableTextBrowser)this).IsModified)
        {
            // Everything is on the server: the draft is of no use (a later change makes a new one).
            DiscardDraft();
        }
    }

    private void RemoveTemporaryFile(SaveJob job)
    {
        try
        {
            _context.Session.RunCommandChecked("rm -f " + RemotePathUtils.ShellQuote(job.TemporaryPath), requireSuccess: true);
        }
        catch
        {
        }
    }
}
