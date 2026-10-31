using SmarTTY.Ssh;
using SmarTTY.CodeEditing;
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

    // What the column of _initialCaret counts (a compiler message may say screen columns or bytes).
    private readonly ColumnUnit _initialColumnUnit;

    private Thread _loadThread;

    private GlobalSettings _settings;

    private Action<Exception> _errorHandler;

    // Minimal interval between previews of a file that is still being downloaded.
    private const int PreviewIntervalMs = 500;

    // Permissions of the temporary copy uploaded on save: it may hold a file only root can read.
    private const int TemporaryFileMode = 0x180; // 0600

    // The user's answer to "Convert CRLF line endings to LF?", so that it is asked once per file.
    private bool? _convertCrLf;

    /// <summary>
    /// Shows the prompt that a <c>sudo</c> command printed on its shell channel (the channel, the command line). The
    /// themed WPF window is injected here by <c>App</c>: this editor is compiled by the Mono test harness, which
    /// cannot reference the WPF dialogs.
    /// </summary>
    public static Action<SshShellChannel, string> SudoConfirmationPrompt;

    // The bytes of the file as they are on the server (what was downloaded, then what was saved): "Reopen with
    // encoding" decodes them again.
    private byte[] _rawBytes;

    // The encoding _rawBytes (the file as it is on the server) is read with.
    private TextFileEncoding _rawEncoding = TextFileEncodings.Utf8;

    // The hex view: its bytes, its undo history and the bytes it saved. The text document is kept apart, so that the
    // two views can be switched back and forth without saving one into the other (see ToggleHexMode).
    private HexEditorControl _hexEditor;

    private HexDocument _hexDocument;

    private bool _hexMode;

    // The text, its caret and its saved state at the moment the hex view was opened.
    private string _textBeforeHex;

    private bool _textModifiedBeforeHex;

    private TextDocument.TextPosition _textCaretBeforeHex;

    // The bytes the hex view had when it was left, and the text they were shown as: saving the text before it is
    // edited writes these bytes back exactly, which keeps a file in an encoding that does not round-trip intact.
    private byte[] _hexBytesForTextSave;

    private string _hexTextSnapshot;

    /// <summary>The hex view of the file.</summary>
    public HexEditorControl HexEditor => _hexEditor;

    /// <summary>The editor shows the bytes instead of the text.</summary>
    public bool IsHexMode => _hexMode;

    /// <summary>The file is loaded and nothing is being saved: the hex view can be opened or closed.</summary>
    public bool CanToggleHexMode => _loaded && !_reloading && !IsSaving;

    /// <summary>Raised when the editor switched between the text and the hex view.</summary>
    public event EventHandler HexModeChanged;

    // The file on the server as it was when it was opened or last saved (see FetchSignature): a save that finds
    // something else there asks before it overwrites the changes of somebody else.
    private string _remoteSignature;

    // The conflict question was answered "overwrite": the next save does not check the server again.
    private bool _forceOverwrite;

    // The user chose to save although the check of the file found problems: the next save does not run the check.
    private bool _skipValidation;

    // The conflict question was answered "reload": the server version is loaded once the save has ended.
    private bool _pendingReload;

    // A reload of the opened file is running (the text stays on screen and the editor read-only until it is done).
    private bool _reloading;

    private bool _loaded;

    private bool _restoreCaret;

    private TextDocument.TextPosition _caretToRestore;

    // A reload started in the hex view: open it again on the new bytes, at the same offset.
    private bool _restoreHexAfterReload;

    private int _hexOffsetToRestore;

    // The blocks folded when the reload started: the fresh document has no folds, so they are put back once it is in.
    private FoldedLineInfo[] _foldsToRestore;

    // The file~ copy of the original is made by the first save of the file after it was loaded.
    private bool _backupDone;

    // The question about a byte order mark before "#!" is asked once per file.
    private bool _bomAnswered;

    /// <summary>The encoding the file is read in and saved in.</summary>
    public TextFileEncoding FileEncoding { get; private set; } = TextFileEncodings.Utf8;

    /// <summary>The line breaks of the file as it was loaded, or as the user converted them.</summary>
    public LineEndingKind LineEndings { get; private set; } = LineEndingKind.None;

    /// <summary>Raised on the UI thread when the encoding or the line endings changed (or the file was loaded).</summary>
    public event EventHandler FormatChanged;

    /// <summary>
    /// The encoding can be reinterpreted: the whole file has been loaded from the server (reading it again in another
    /// encoding does not write anything, so a read-only file qualifies, a preview of a huge file does not).
    /// </summary>
    public bool CanReopenWithEncoding => _loaded && !_reloading && _rawBytes != null && !IsNewFile && !IsPreview;

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
    private static readonly string DraftsDirectory = Path.Combine(GlobalSettings.DataDirectory, "Drafts");

    private static readonly EditorDraftStore Drafts = new EditorDraftStore(DraftsDirectory, new DpapiDraftProtector());

    private const int DraftIntervalMs = 10000;

    // Drafts older than this are removed when an editor starts.
    private static readonly TimeSpan DraftMaxAge = TimeSpan.FromDays(30);

    private static int _draftCleanupDone;

    private readonly string _draftKey;

    // Two editors of the same file in one session must not share a draft: the save or the discard of one would remove the
    // draft of the other, and neither would see the other's text. The first editor of a file has the plain key, the next ones
    // the key with a number; the number is free again when the editor is closed.
    // This holds across programs too: SmarTTY may be started more than once, so a key is also claimed with a lock file that
    // is kept open (and deleted by the system when it is closed, or when the program ends in any way) while the editor lives.
    // The value is that lock (null when the Drafts folder cannot be used: then only this program's own editors are told apart).
    private static readonly Dictionary<string, FileStream> ClaimedDraftKeys = new Dictionary<string, FileStream>();

    private static string ClaimDraftKey(string baseKey)
    {
        lock (ClaimedDraftKeys)
        {
            string key = baseKey;
            for (int number = 1; ; number++)
            {
                key = number == 1 ? baseKey : baseKey + "-" + number.ToString(CultureInfo.InvariantCulture);
                if (ClaimedDraftKeys.ContainsKey(key))
                {
                    continue;
                }
                FileStream claim = TryLockDraftKey(key, out bool heldElsewhere);
                if (heldElsewhere && number < 1000)
                {
                    continue;
                }
                ClaimedDraftKeys[key] = claim;
                return key;
            }
        }
    }

    /// <summary>
    /// Takes the lock file of the key. <paramref name="heldElsewhere"/> is true if another program has it; null with
    /// false means the lock could not be made at all (the folder is not writable), which is no reason not to edit.
    /// </summary>
    private static FileStream TryLockDraftKey(string key, out bool heldElsewhere)
    {
        heldElsewhere = false;
        try
        {
            Directory.CreateDirectory(DraftsDirectory);
            return new FileStream(Path.Combine(DraftsDirectory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) == 32 || (exception.HResult & 0xFFFF) == 33)
        {
            // ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION: an editor of another program has the key.
            heldElsewhere = true;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static void ReleaseDraftKey(string key)
    {
        lock (ClaimedDraftKeys)
        {
            if (ClaimedDraftKeys.TryGetValue(key, out FileStream claim))
            {
                ClaimedDraftKeys.Remove(key);
                try
                {
                    claim?.Dispose();
                }
                catch
                {
                }
            }
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
        _hexEditor = new HexEditorControl
        {
            Dock = DockStyle.Fill,
            Visible = false,
            Font = Font,
            BackColor = BackColor,
            ForeColor = ForeColor
        };
        Controls.Add(_hexEditor);
    }

    /// <summary>Switches the editor between the text and the hex view.</summary>
    public void ToggleHexMode()
    {
        if (!CanToggleHexMode)
        {
            return;
        }
        // The messages are rows of the text view, so they do not belong in the hex view.
        ClearMessages();
        if (_hexMode)
        {
            ExitHexMode();
        }
        else
        {
            EnterHexMode();
        }
    }

    /// <summary>Puts the keyboard focus on the view that is shown: the text or the hex editor.</summary>
    public void FocusActiveView()
    {
        if (_hexMode)
        {
            _hexEditor.Focus();
        }
        else
        {
            Focus();
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_hexEditor != null)
        {
            _hexEditor.Font = Font;
        }
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        if (_hexEditor != null)
        {
            _hexEditor.BackColor = BackColor;
        }
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        if (_hexEditor != null)
        {
            _hexEditor.ForeColor = ForeColor;
        }
    }

    /// <summary>
    /// The bytes the editor shows: the server version when the text is unchanged, otherwise the text encoded in the
    /// file encoding, or the exact bytes the hex view left when the text has not been edited since.
    /// </summary>
    private byte[] GetBytesForHexView(string text)
    {
        if (_hexBytesForTextSave != null && _hexTextSnapshot == text)
        {
            return _hexBytesForTextSave;
        }
        if (!base.IsModified && _rawBytes != null)
        {
            return _rawBytes;
        }
        return TextFileEncodings.Encode(text, FileEncoding);
    }

    private void EnterHexMode()
    {
        if (_hexMode || !_loaded || IsSaving)
        {
            return;
        }
        string text = base.EditableDocument.GetText();
        HexDocument document = _hexDocument;
        bool reuse = document != null && _hexTextSnapshot == text && _hexBytesForTextSave != null
            && HexDocument.ByteArraysEqual(document.ToArray(), _hexBytesForTextSave);
        if (!reuse)
        {
            try
            {
                byte[] serverBytes = _rawBytes ?? Array.Empty<byte>();
                document = new HexDocument(GetBytesForHexView(text), serverBytes);
            }
            catch (InvalidOperationException exception)
            {
                ThemedMessageBox.Show(this, exception.Message, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return;
            }
        }
        ShowHexView(document);
    }

    /// <summary>
    /// Shows <paramref name="document"/> in the hex view: the text document is kept aside for <see cref="ExitHexMode"/>.
    /// </summary>
    private void ShowHexView(HexDocument document)
    {
        _textBeforeHex = base.EditableDocument.GetText();
        _textModifiedBeforeHex = base.IsModified;
        _textCaretBeforeHex = CaretPosition;
        AttachHexDocument(document);
        _hexMode = true;
        ((EditableTextBrowser)this).ReadOnly = true;
        if (!ReferenceEquals(_hexEditor.Document, document))
        {
            _hexEditor.Document = document;
        }
        _hexEditor.ReadOnly = IsReadOnlyMode;
        _hexEditor.Visible = true;
        _hexEditor.BringToFront();
        _hexEditor.Focus();
        OnModifiedChanged();
        HexModeChanged?.Invoke(this, EventArgs.Empty);
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the bytes of a restored draft in the hex view.</summary>
    private void ShowRestoredHexDraft(byte[] bytes)
    {
        ShowHexView(new HexDocument(bytes, _rawBytes ?? Array.Empty<byte>()));
    }

    private void ExitHexMode()
    {
        if (!_hexMode || _hexDocument == null)
        {
            return;
        }
        byte[] bytes = _hexDocument.ToArray();
        bool keepText = !_textModifiedBeforeHex && _textBeforeHex != null
            && HexDocument.ByteArraysEqual(bytes, _rawBytes ?? Array.Empty<byte>());
        string text;
        if (keepText)
        {
            // Nothing changed: show the text exactly as it was, even when the encoding does not round-trip.
            text = _textBeforeHex;
        }
        else
        {
            text = TextFileEncodings.Decode(bytes, FileEncoding);
            byte[] roundTrip = null;
            try
            {
                roundTrip = TextFileEncodings.Encode(text, FileEncoding);
            }
            catch
            {
                // A character the encoding cannot store is just as lossy.
            }
            if (!HexDocument.ByteArraysEqual(roundTrip, bytes)
                && ThemedMessageBox.Show(this, "Not all bytes can be shown as text in " + FileEncoding.DisplayName
                    + ": saving the text after it is edited would change them.\n\nShow the bytes as text anyway?",
                    "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                _hexEditor.Focus();
                return;
            }
        }
        _hexBytesForTextSave = bytes;
        _hexTextSnapshot = text;
        _hexMode = false;
        _hexEditor.Visible = false;
        _hexEditor.ReadOnly = true;
        ((EditableTextBrowser)this).ReadOnly = IsReadOnlyMode;

        // When the bytes are still the text that was shown before, the text document and its undo history are kept:
        // switching to the hex view and back must not lose them.
        if (text == _textBeforeHex && base.EditableDocument.GetText() == text)
        {
            ((EditableTextBrowser)this).IsModified = !HexDocument.ByteArraysEqual(bytes, _rawBytes ?? Array.Empty<byte>());
            Focus();
            OnModifiedChanged();
            HexModeChanged?.Invoke(this, EventArgs.Empty);
            FormatChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        LineEndingKind lineEndings = LineEndingDetector.Detect(text);
        LineEndings = lineEndings;
        EditableTextDocument document = new EditableTextDocument
        {
            DefaultLineEnding = (lineEndings == LineEndingKind.CrLf) ? TextDocument.LineEnding.CrLf : ((lineEndings == LineEndingKind.Cr) ? TextDocument.LineEnding.Cr : TextDocument.LineEnding.Lf)
        };
        document.SetText(text);
        SetDocument(document);
        int line = Math.Max(0, Math.Min(_textCaretBeforeHex.Line, Math.Max(0, document.LineCount - 1)));
        SetCaret(new TextDocument.TextPosition(Math.Min(_textCaretBeforeHex.Column, document.GetLineText(line).Length), line));
        ((EditableTextBrowser)this).IsModified = !HexDocument.ByteArraysEqual(bytes, _rawBytes ?? Array.Empty<byte>());
        Focus();
        OnModifiedChanged();
        HexModeChanged?.Invoke(this, EventArgs.Empty);
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AttachHexDocument(HexDocument document)
    {
        if (_hexDocument != null)
        {
            _hexDocument.ModifiedChanged -= OnHexDocumentModifiedChanged;
        }
        _hexDocument = document;
        if (_hexDocument != null)
        {
            _hexDocument.ModifiedChanged += OnHexDocumentModifiedChanged;
        }
    }

    private void OnHexDocumentModifiedChanged(object sender, EventArgs e)
    {
        if (_hexMode && ReferenceEquals(sender, _hexDocument))
        {
            OnModifiedChanged();
        }
    }

    /// <summary>Hides and forgets the hex view (the file is being loaded again, or its text was replaced).</summary>
    private void ResetHexMode()
    {
        bool hadHex = _hexMode || _hexDocument != null;
        AttachHexDocument(null);
        _hexMode = false;
        _hexBytesForTextSave = null;
        _hexTextSnapshot = null;
        _textBeforeHex = null;
        _textModifiedBeforeHex = false;
        _hexEditor.Visible = false;
        _hexEditor.ReadOnly = true;
        _hexEditor.Document = null;
        if (hadHex)
        {
            HexModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The unsaved state covers both views: the bytes in hex mode, the text in text mode.</summary>
    public override bool IsModified
    {
        get
        {
            return (_hexMode && _hexDocument != null) ? _hexDocument.IsModified : base.IsModified;
        }
        set
        {
            if (_hexMode && _hexDocument != null)
            {
                if (value)
                {
                    _hexDocument.MarkModified();
                }
                else
                {
                    _hexDocument.MarkSaved();
                }
            }
            else
            {
                base.IsModified = value;
            }
        }
    }

    public override void CopySelection(bool select = true)
    {
        if (_hexMode)
        {
            _hexEditor.CopySelection();
        }
        else
        {
            base.CopySelection(select);
        }
    }

    public override void CopyAll()
    {
        if (_hexMode)
        {
            _hexEditor.CopyAll();
        }
        else
        {
            base.CopyAll();
        }
    }

    public override void Cut()
    {
        if (_hexMode)
        {
            _hexEditor.Cut();
        }
        else
        {
            base.Cut();
        }
    }

    public override void Paste()
    {
        if (_hexMode)
        {
            _hexEditor.Paste();
        }
        else
        {
            base.Paste();
        }
    }

    public override void DeleteSelection()
    {
        if (_hexMode)
        {
            _hexEditor.DeleteSelection();
        }
        else
        {
            base.DeleteSelection();
        }
    }

    public override void SelectAllText()
    {
        if (_hexMode)
        {
            _hexEditor.SelectAll();
        }
        else
        {
            base.SelectAllText();
        }
    }

    /// <param name="initialColumnUnit">What the column of <paramref name="textPosition"/> counts: the caret is put at the character it points at once the text is loaded.</param>
    public RemoteFileEditor(ITerminalSessionContext terminalSessionContext, string text, GlobalSettings globalSettings, TextDocument.TextPosition textPosition, Action<Exception> action = null, ColumnUnit initialColumnUnit = ColumnUnit.Characters)
        : this()
    {
        _initialColumnUnit = initialColumnUnit;
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
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
                BeginInvoke((ThreadStart)(() => FinishLoading(null, null)));
                return;
            }
            IsNewFile = false;
            // A reload keeps the answers given when the file was opened (the permission and the size questions).
            if (!_reloading)
            {
                CheckWriteAccess(RemotePath, forNewFile: false);
            }
            // Taken before the download: a change made while it runs is then noticed by the next save.
            string signature = FetchSignature();
            MemoryStream downloaded = new MemoryStream();
            // Only the text of the preview is decoded as it arrives; the file is decoded once it is complete.
            StringBuilder content = new StringBuilder();
            bool showPreview = !_reloading;
            using (Stream scpDownloadStream = OpenForReading(out long remoteLength))
            {
                // The size is known from the first SCP header (or from SFTP), before any data is read.
                long limit = _reloading ? (IsPreview ? EditorFilePolicy.PreviewBytes : 0) : ChooseLoadLimit(remoteLength);
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
                        downloaded.Write(bytes, 0, value);
                        if (showPreview)
                        {
                            content.Append(utf8StreamDecoder.Decode(bytes, 0, value));
                        }
                    }
                    if (limit > 0 && received >= limit)
                    {
                        break;
                    }
                    // Show the partially downloaded text now and then. Re-parsing and re-highlighting the
                    // whole document after every 64 KB block made opening large files quadratic.
                    if (showPreview && (DateTime.Now - lastPreview).TotalMilliseconds >= PreviewIntervalMs)
                    {
                        lastPreview = DateTime.Now;
                        string preview = content.ToString().TrimStart('\uFEFF');
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
            byte[] file = downloaded.ToArray();
            BeginInvoke((ThreadStart)(() => FinishLoading(file, signature)));
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
                    if (_reloading)
                    {
                        // The text on screen is still the user's: keep the tab.
                        _reloading = false;
                        ((EditableTextBrowser)this).ReadOnly = IsReadOnlyMode;
                        FormatChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        LoadFailed?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch
                {
                }
            }));
        }
    }

    private bool IsWindowsHost => _context.Session.RemoteOs == SmarTTY.Ssh.RemoteOsKind.Windows;

    /// <summary>
    /// The file on the server as a stream, and its size: over SCP for a Unix host, over SFTP for a Windows host, which has none
    /// of the commands that the rest of the editor uses either (test, stat, cat, cp, rm, sudo).
    /// </summary>
    private Stream OpenForReading(out long length)
    {
        if (IsWindowsHost)
        {
            return SmarTTY.Transport.WindowsRemoteFileEditing.OpenRead(_context.Session, RemotePath, out length);
        }
        ScpDownloadStream stream = _context.Session.OpenDownload(RemotePath);
        length = stream.Length;
        return stream;
    }

    /// <summary>Shows a question from the loading thread; the answer of a closed editor is "cancel".</summary>
    private DialogResult Ask(string text, MessageBoxButtons buttons)
    {
        if (IsDisposed)
        {
            return DialogResult.Cancel;
        }
        return (DialogResult)Invoke((Func<DialogResult>)(() => ThemedMessageBox.Show(this, text, "SmarTTY", buttons, MessageBoxIcon.Warning)));
    }

    /// <summary>
    /// Warns when the user cannot write the file (or, for a new file, its directory) before he types anything: he may
    /// choose to save with sudo (asked for at the save), to read only, or not to open the file. Loading thread.
    /// </summary>
    private void CheckWriteAccess(string target, bool forNewFile)
    {
        if (IsWindowsHost)
        {
            // A new file: its folder is not probed (that would mean creating a file to delete it), a refused save says so then.
            if (forNewFile || SmarTTY.Transport.WindowsRemoteFileEditing.CanWrite(_context.Session, target) != false)
            {
                return;
            }
            // No sudo on Windows: the choice is to read the file or not to open it.
            if (Ask("You do not have permission to write to " + target + ".\n\n"
                + "Yes: open the file read-only.\n"
                + "No: do not open the file.", MessageBoxButtons.YesNo) != DialogResult.Yes)
            {
                throw new OperationCanceledException();
            }
            IsReadOnlyMode = true;
            return;
        }
        if (_context.Session.RunCommandChecked("test -w " + RemotePathUtils.ShellQuote(target), requireSuccess: true) != null)
        {
            return;
        }
        // No answer is not "not writable": without a connection nothing is known.
        _context.Session.ThrowIfConnectionLost();
        if (_context.AutoConfirmSudo)
        {
            // The connection asks to save with sudo without the question: the save goes through sudo directly.
            _sudoOnSave = true;
            return;
        }
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
        if (IsWindowsHost)
        {
            return SmarTTY.Transport.WindowsRemoteFileEditing.Exists(_context.Session, path);
        }
        if (_context.Session.RunCommandChecked("test -e " + RemotePathUtils.ShellQuote(path), requireSuccess: true) != null)
        {
            return true;
        }
        // No answer is not "does not exist": without a connection the file may be there.
        _context.Session.ThrowIfConnectionLost();
        return false;
    }

    /// <summary>
    /// Shows the downloaded file (UI thread) and makes the editor editable. <paramref name="file"/> is null for a
    /// new file.
    /// </summary>
    private void FinishLoading(byte[] file, string signature)
    {
        try
        {
            TextFileEncoding encoding = (file == null) ? TextFileEncodings.Utf8 : TextFileEncodings.Detect(file);
            string text = (file == null) ? "" : TextFileEncodings.Decode(file, encoding);
            _baseHash = EditorDraftStore.HashText(text);
            EditorDraft restoredDraft = null;
            if (_reloading)
            {
                // The user dropped the changes to load the file again: their draft must not come back.
                DiscardDraft();
            }
            else if (!IsReadOnlyMode)
            {
                restoredDraft = AskToRestoreDraft(file, text);
                if (restoredDraft != null && restoredDraft.Bytes == null)
                {
                    text = restoredDraft.Text ?? text;
                }
            }
            ApplyContent(file, encoding, text);
            _remoteSignature = signature;
            _backupDone = false;
            _reloading = false;
            _loaded = true;
            ((EditableTextBrowser)this).ReadOnly = IsReadOnlyMode;
            if (restoredDraft != null)
            {
                if (restoredDraft.Bytes != null)
                {
                    // The draft holds the bytes of the hex view: show them there, where they can be saved as they are.
                    ShowRestoredHexDraft(restoredDraft.Bytes);
                }
                else
                {
                    // The restored text differs from the file: it is an unsaved change, and the draft timer keeps it safe again.
                    ((EditableTextBrowser)this).IsModified = true;
                }
            }
            if (!IsReadOnlyMode)
            {
                StartDraftTimer();
            }
            LoadCompleted?.Invoke(this, EventArgs.Empty);
            try
            {
                if (_restoreCaret)
                {
                    _restoreCaret = false;
                    int line = Math.Min(_caretToRestore.Line, Math.Max(0, base.EditableDocument.LineCount - 1));
                    SetCaret(new TextDocument.TextPosition(Math.Min(_caretToRestore.Column, base.EditableDocument.GetLineText(line).Length), line));
                }
                else if (_initialCaret.IsValid)
                {
                    SetCaretFromColumnUnit(_initialCaret, _initialColumnUnit);
                }
                if (_restoreHexAfterReload)
                {
                    _restoreHexAfterReload = false;
                    EnterHexMode();
                    if (_hexMode)
                    {
                        _hexEditor.GoToOffset(_hexOffsetToRestore);
                    }
                }
                // The fresh document dropped the folds of the previous text: put back the blocks that were
                // collapsed, matched by their header text (the same rule the editor uses for its own folds).
                FoldedLineInfo[] folds = _foldsToRestore;
                _foldsToRestore = null;
                if (folds != null && folds.Length != 0)
                {
                    RestoreFoldState(folds);
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
    /// Puts <paramref name="text"/> (the file <paramref name="file"/>, read as <paramref name="encoding"/>) in the editor
    /// as unmodified.
    /// </summary>
    private void ApplyContent(byte[] file, TextFileEncoding encoding, string text)
    {
        ResetHexMode();
        _rawBytes = file;
        _rawEncoding = encoding;
        FileEncoding = encoding;
        LineEndings = LineEndingDetector.Detect(text);
        EditableTextDocument document = new EditableTextDocument
        {
            DefaultLineEnding = (LineEndings == LineEndingKind.CrLf) ? TextDocument.LineEnding.CrLf : ((LineEndings == LineEndingKind.Cr) ? TextDocument.LineEnding.Cr : TextDocument.LineEnding.Lf)
        };
        document.SetText(text);
        SetDocument(document);
        ((EditableTextBrowser)this).IsModified = false;
        _convertCrLf = null;
        _bomAnswered = false;
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Offers the draft of unsaved changes left by an earlier editor of this file (the connection was lost or the program
    /// ended before they were saved). Returns the draft to restore, or null to open the file as it is on the server.
    /// </summary>
    private EditorDraft AskToRestoreDraft(byte[] serverBytes, string serverText)
    {
        EditorDraft draft = Drafts.TryLoad(_draftKey);
        if (draft == null)
        {
            return null;
        }
        bool same = (draft.Bytes != null)
            ? HexDocument.ByteArraysEqual(draft.Bytes, serverBytes ?? Array.Empty<byte>())
            : (draft.Text == serverText);
        if (same)
        {
            // Nothing to restore: the changes were saved after all.
            QueueDraft(null);
            return null;
        }
        string changed = (!draft.IsNewFile && draft.BaseHash != _baseHash)
            ? "The file on the server has changed since then; saving the draft would overwrite those changes.\n\n"
            : "";
        string when = draft.SavedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        if (ThemedMessageBox.Show(this, "Unsaved changes of " + RemotePath + " from " + when + " were found: the connection was lost or the program was closed before they were saved.\n\n"
            + changed
            + "Yes: restore the draft.\n"
            + "No: discard it and open the file from the server.", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            return draft;
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

    /// <summary>Writes the draft if the content changed since the last one (UI thread; the file is written on another thread).</summary>
    private void WriteDraftIfChanged()
    {
        try
        {
            if (IsDisposed || IsReadOnlyMode || !IsModified)
            {
                return;
            }
            bool hex = _hexMode && _hexDocument != null;
            object version = hex ? _hexDocument.CurrentVersion : CurrentVersion;
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
                Text = hex ? null : base.EditableDocument.GetText(),
                Bytes = hex ? _hexDocument.ToArray() : null
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

    /// <summary>
    /// Identifies the version of the file on the server (modification time and size, or a checksum where the system
    /// has no stat) so that a change made by somebody else can be noticed. Null when it cannot be found out, or when
    /// there is no such file. Runs a command: call it from any thread but not often.
    /// </summary>
    private string FetchSignature()
    {
        try
        {
            if (IsWindowsHost)
            {
                return SmarTTY.Transport.WindowsRemoteFileEditing.Signature(_context.Session, RemotePath);
            }
            string path = RemotePathUtils.ShellQuote(RemotePath);
            System.Collections.Generic.List<string> output = _context.Session.RunCommandChecked("stat -c '%Y %s' " + path + " 2>/dev/null || md5sum " + path, requireSuccess: true);
            if (output == null || output.Count == 0)
            {
                return null;
            }
            string signature = string.Join("\n", output).Trim();
            return (signature.Length == 0) ? null : signature;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The file on the server is not the one that was opened (or a new file has been created there meanwhile).
    /// Runs a command (any thread). Without a signature of the opened file nothing can be said: not a conflict.
    /// </summary>
    private bool HasServerChanged()
    {
        string current = FetchSignature();
        if (current == null)
        {
            return false;
        }
        if (IsNewFile)
        {
            return true;
        }
        return _remoteSignature != null && current != _remoteSignature;
    }

    private string ConflictDescription()
    {
        return IsNewFile
            ? RemotePath + " has been created on the server since you started this new file."
            : RemotePath + " has been changed on the server since it was opened in the editor.";
    }

    /// <summary>
    /// Loads the file from the server again, dropping the text in the editor and its draft (the caller asks first when
    /// there are unsaved changes). The caret stays where it was.
    /// </summary>
    public void Reload()
    {
        if (IsSaving || !_loaded || _reloading || IsNewFile || IsPreview)
        {
            return;
        }
        // The messages name lines of the text that is about to be replaced.
        ClearMessages();
        _reloading = true;
        _restoreCaret = true;
        _caretToRestore = CaretPosition;
        _restoreHexAfterReload = _hexMode;
        _hexOffsetToRestore = _hexMode ? _hexEditor.CaretOffset : 0;
        _foldsToRestore = GetFoldState();
        if (_hexMode)
        {
            _hexEditor.ReadOnly = true;
        }
        ((EditableTextBrowser)this).ReadOnly = true;
        FormatChanged?.Invoke(this, EventArgs.Empty);
        _loadThread = new Thread(LoadFile);
        _loadThread.Start();
    }

    /// <summary>
    /// Puts the caret at a place named by a tool: <paramref name="position"/> is a line and a column counted from 0 in
    /// <paramref name="unit"/> (screen columns with tab stops of 8 for gcc, bytes for clang and go). The column is turned into the
    /// index of the character it points at in the line, so a tab or a non-ASCII letter in front of it does not move the caret.
    /// </summary>
    public void SetCaretFromColumnUnit(TextDocument.TextPosition position, ColumnUnit unit)
    {
        if (unit != ColumnUnit.Characters && unit != ColumnUnit.Auto && position.IsValid && base.EditableDocument != null
            && position.Line >= 0 && position.Line < base.EditableDocument.LineCount)
        {
            position.Column = CompilerColumns.ToCharIndex(base.EditableDocument.GetLineText(position.Line), position.Column, unit);
        }
        SetCaret(position);
    }

    /// <summary>
    /// Saves the file in another encoding (the text stays as it is). The file counts as changed: it has to be saved.
    /// </summary>
    public void SetSaveEncoding(TextFileEncoding encoding)
    {
        if (encoding == null || encoding == FileEncoding || ((EditableTextBrowser)this).ReadOnly || _hexMode)
        {
            return;
        }
        FileEncoding = encoding;
        // The bytes kept from the hex view are in the old encoding: save the text in the chosen one now.
        _hexBytesForTextSave = null;
        _hexTextSnapshot = null;
        ((EditableTextBrowser)this).IsModified = true;
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reads the file again as <paramref name="encoding"/> (when the text came out as garbage): the bytes that are on
    /// the server are decoded anew, the unsaved changes are dropped (the caller asks first).
    /// </summary>
    public void ReopenWithEncoding(TextFileEncoding encoding)
    {
        if (encoding == null || !CanReopenWithEncoding || IsSaving)
        {
            return;
        }
        DiscardDraft();
        string text = TextFileEncodings.Decode(_rawBytes, encoding);
        _baseHash = EditorDraftStore.HashText(text);
        ApplyContent(_rawBytes, encoding, text);
    }

    /// <summary>
    /// Converts every line break of the file to <paramref name="lineEnding"/>, and gives the lines typed later the same
    /// one. The file counts as changed: it has to be saved.
    /// </summary>
    public void SetLineEnding(TextDocument.LineEnding lineEnding)
    {
        if (((EditableTextBrowser)this).ReadOnly || _hexMode)
        {
            return;
        }
        base.EditableDocument.SetLineEnding(lineEnding);
        base.EditableDocument.DefaultLineEnding = lineEnding;
        LineEndings = (lineEnding == TextDocument.LineEnding.CrLf) ? LineEndingKind.CrLf : ((lineEnding == TextDocument.LineEnding.Cr) ? LineEndingKind.Cr : LineEndingKind.Lf);
        // The text no longer matches the bytes kept from the hex view.
        _hexBytesForTextSave = null;
        _hexTextSnapshot = null;
        ((EditableTextBrowser)this).IsModified = true;
        FormatChanged?.Invoke(this, EventArgs.Empty);
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

        // The text version being saved (EditableTextBrowser.CurrentVersion), or the hex version
        // (HexDocument.CurrentVersion) when HexMode is set.
        public object Version;

        // The save came from the hex view: CompleteSave marks the hex document saved instead of the text.
        public bool HexMode;

        // Saves started later supersede this one (see _saveGeneration).
        public int Generation;

        // The user already agreed to overwrite what changed on the server.
        public bool SkipConflictCheck;

        // The command also copies the old file to file~.
        public bool IncludesBackup;

        // The file on the server after the save (FetchSignature).
        public string NewSignature;

        // The encoding the bytes were made with: they become the file as it is on the server.
        public TextFileEncoding Encoding;

        // The check run on the uploaded file before it replaces the target (null: none), and what it found if it failed.
        public ConfigValidator Validator;

        public ValidationResult Validation;
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
        // Saving on close does not run the check: the user is leaving and cannot act on what it finds.
        job.Validator = null;
        try
        {
            if (!job.SkipConflictCheck && HasServerChanged()
                && ThemedMessageBox.Show(ConflictDescription() + " Overwrite it with your version?", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                throw new OperationCanceledException();
            }
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
            bool conflict = false;
            Exception error = null;
            try
            {
                conflict = !job.SkipConflictCheck && HasServerChanged();
                if (!conflict)
                {
                    replaced = UploadAndReplace(job);
                }
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
                BeginInvoke((ThreadStart)(() => FinishBackgroundSave(job, replaced, conflict, error, uploadDone)));
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

    private void FinishBackgroundSave(SaveJob job, bool replaced, bool conflict, Exception error, ManualResetEvent uploadDone)
    {
        ServiceReload reloadToOffer = null;
        try
        {
            // Superseded by a synchronous save of a newer version: only clean up.
            if (job.Generation == _saveGeneration)
            {
                if (error != null)
                {
                    ReportError(error);
                }
                else if (conflict)
                {
                    switch (ThemedMessageBox.Show(ConflictDescription() + "\n\nYes: overwrite it with your version.\nNo: load the version from the server (your changes are lost).\nCancel: do not save.", "SmarTTY", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning))
                    {
                        case DialogResult.Yes:
                            _forceOverwrite = true;
                            _saveRequestedWhileSaving = true;
                            break;
                        case DialogResult.No:
                            _pendingReload = true;
                            break;
                    }
                }
                else if (job.Validation != null)
                {
                    HandleValidationFailure(job);
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
                        reloadToOffer = ReloadToOffer();
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
            if (_pendingReload)
            {
                _pendingReload = false;
                _saveRequestedWhileSaving = false;
                _forceOverwrite = false;
                _skipValidation = false;
                if (IsNewFile)
                {
                    // Somebody created the file this new one was going to be: there is a version to load now.
                    IsNewFile = false;
                }
                Reload();
            }
            else if (_saveRequestedWhileSaving)
            {
                _saveRequestedWhileSaving = false;
                if (IsModified || _forceOverwrite || _skipValidation)
                {
                    SaveInBackground();
                }
            }
            // A save queued meanwhile will offer it again once it is written.
            if (reloadToOffer != null && !IsSaving)
            {
                try
                {
                    OfferReload(reloadToOffer);
                }
                catch (Exception exception)
                {
                    ReportError(exception);
                }
            }
        }
    }

    /// <summary>Gives the inline messages the colours of the current theme (dark or light).</summary>
    public void RefreshMessageColors()
    {
        if (_settings == null)
        {
            return;
        }
        GlobalSettings.EditorColors colors = _settings.Editor.GetEffectiveColors(_settings.IsLightTheme);
        ApplyMessageColors(colors.LineNumberBackground, colors.ErrorColor, colors.WarningColor, colors.LineNumberForeground);
    }

    /// <summary>
    /// The check found problems in the text, so the file on the server was not replaced. Says what was found and
    /// either saves anyway (the next save skips the check) or puts the caret on the first problem (UI thread).
    /// </summary>
    private void HandleValidationFailure(SaveJob job)
    {
        // How many problems get an inline message; ParseIssues caps the list at the same number.
        const int MaxInline = 50;
        List<ValidationIssue> issues = job.Validation.Issues;
        // The messages sit in the rows of the text view; the wrapped rows and the hex view have no such rows.
        bool showInText = !WordWrap && !IsHexMode;
        if (showInText)
        {
            RefreshMessageColors();
            List<InlineMessageProvider.MessageRequest> messages = new List<InlineMessageProvider.MessageRequest>();
            for (int i = 0; i < issues.Count && i < MaxInline; i++)
            {
                messages.Add(new InlineMessageProvider.MessageRequest
                {
                    Line = issues[i].Line,
                    Severity = InlineMessageProvider.MessageSeverity.Error,
                    Text = issues[i].Message,
                    Column = issues[i].Column
                });
            }
            AddMessages(messages);
        }
        string text = "The check (" + job.Validator.Name + ") found " + issues.Count + (issues.Count == 1 ? " problem" : " problems")
            + " in " + RemotePath + (showInText ? ", they are shown in the text" : "") + ".\n\nThe file on the server has not been changed.\n\nYes: save it anyway.\nNo: do not save, go to line " + issues[0].Line + ".";
        if (ThemedMessageBox.Show(text, "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            _skipValidation = true;
            _saveRequestedWhileSaving = true;
            return;
        }
        GoToPosition(Math.Max(0, issues[0].Line - 1), Math.Max(0, issues[0].Column - 1));
        FocusActiveView();
    }

    /// <summary>What to offer after a successful save so that the service reads the file again, or null.</summary>
    private ServiceReload ReloadToOffer()
    {
        if (IsWindowsHost || _settings.Editor.DisableServiceReloadOffer)
        {
            return null;
        }
        return ConfigValidators.FindReload(RemotePath);
    }

    /// <summary>Asks whether to reload the service that reads the file just saved, and does so (UI thread).</summary>
    private void OfferReload(ServiceReload reload)
    {
        if (ThemedMessageBox.Show(RemotePath + " was saved.\n\nReload " + reload.Name + " now so that the change takes effect?", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        ThreadPool.QueueUserWorkItem((object state) =>
        {
            List<string> output = null;
            int? exitStatus = null;
            try
            {
                output = _context.Session.RunCommand(reload.Command, out exitStatus, timeout: TimeSpan.FromSeconds(30));
            }
            catch
            {
            }
            try
            {
                BeginInvoke((ThreadStart)(() => FinishReload(reload, output, exitStatus)));
            }
            catch
            {
                // The editor was closed meanwhile.
            }
        });
    }

    private void FinishReload(ServiceReload reload, List<string> output, int? exitStatus)
    {
        try
        {
            if (exitStatus == 0)
            {
                ThemedMessageBox.Show(reload.Name + " reloaded.", "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // A lost connection is not a refused reload: do not offer sudo for it.
            _context.Session.ThrowIfConnectionLost();
            StringBuilder details = new StringBuilder();
            if (output != null)
            {
                for (int i = 0; i < output.Count && i < 8; i++)
                {
                    details.Append(output[i]).Append('\n');
                }
            }
            if (ThemedMessageBox.Show("Could not reload " + reload.Name + (exitStatus.HasValue ? " (exit code " + exitStatus.Value + ")" : "") + ":\n\n" + details + "\nTry again with sudo?", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }
            RunSudoCommand(reload.Command);
            ThemedMessageBox.Show(reload.Name + " reloaded.", "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    /// <summary>
    /// The file can be run in a terminal of the session (F5): it is loaded in full, the host has a shell, and the file
    /// name or the first line says how to run it.
    /// </summary>
    public bool CanRunInTerminal => _loaded && !_reloading && !IsPreview && !IsWindowsHost && base.EditableDocument != null
        && RemoteScript.CanRun(RemotePathUtils.GetFileName(RemotePath), base.EditableDocument.GetLineText(0));

    /// <summary>
    /// The file can be followed like tail -f in a terminal tab: it is on the server and the host is a Unix one. It is the
    /// file on the server that is followed, not the text in the editor.
    /// </summary>
    public bool CanFollowLog => _loaded && !_reloading && !IsNewFile && !IsWindowsHost;

    /// <summary>The interpreter for a file that is not executable and has no shebang (see <see cref="RemoteScript.DefaultInterpreter"/>).</summary>
    public string RunInterpreter => RemoteScript.DefaultInterpreter(RemotePathUtils.GetFileName(RemotePath)) ?? "sh";

    /// <summary>
    /// The differences between the file on the server (as it was opened, or last saved) and the text in the editor,
    /// for the Show Changes window.
    /// </summary>
    public TextDiff.Result GetChanges()
    {
        string original = _rawBytes == null ? "" : TextFileEncodings.Decode(_rawBytes, _rawEncoding);
        return TextDiff.Compare(original, base.EditableDocument.GetText(), RemotePath + " (on the server)", RemotePath + " (in the editor)");
    }

    private void SetSaving(bool saving)
    {
        IsSaving = saving;
        SavingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Takes the bytes to save (UI thread), asking once whether to convert CRLF line endings that a file which did not
    /// have them has got, and whether to drop a byte order mark that breaks a script.
    /// </summary>
    private SaveJob PrepareSave()
    {
        // The problems the previous check named are about the text as it was: the check runs again now.
        ClearMessages();
        bool hexMode = _hexMode && _hexDocument != null;
        object version;
        byte[] bytes;
        if (hexMode)
        {
            // The bytes are saved as they are: the CRLF and BOM questions are about text that is not shown here.
            version = _hexDocument.CurrentVersion;
            bytes = _hexDocument.ToArray();
        }
        else
        {
            string value = base.EditableDocument.GetText();
            // Bytes that came from the hex view are saved back exactly while the text has not been edited since: an
            // encoding that does not round-trip intact must not change the file.
            bool exactBytes = _hexBytesForTextSave != null && _hexTextSnapshot == value;
            if (!exactBytes)
            {
                if (value.Contains("\r\n") && LineEndings != LineEndingKind.CrLf)
                {
                    if (!_convertCrLf.HasValue)
                    {
                        _convertCrLf = ThemedMessageBox.Show("The text has CRLF line endings, which break shell scripts and OpenWrt configuration files. Convert them to LF endings?", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                    }
                    if (_convertCrLf.Value)
                    {
                        SetLineEnding(TextDocument.LineEnding.Lf);
                    }
                }
                value = base.EditableDocument.GetText();
                if (FileEncoding == TextFileEncodings.Utf8Bom && value.StartsWith("#!", StringComparison.Ordinal) && !_bomAnswered)
                {
                    _bomAnswered = true;
                    if (ThemedMessageBox.Show("The file starts with #! and is saved with a UTF-8 byte order mark, which makes the system fail to run it as a script. Save it without the mark?", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        SetSaveEncoding(TextFileEncodings.Utf8);
                    }
                }
                value = base.EditableDocument.GetText();
                exactBytes = _hexBytesForTextSave != null && _hexTextSnapshot == value;
            }
            version = CurrentVersion;
            bytes = exactBytes ? _hexBytesForTextSave : TextFileEncodings.Encode(value, FileEncoding);
        }
        string temporaryPath = _settings.Editor.TemporaryDirectory + "/" + Guid.NewGuid().ToString() + ".txt";
        // Quote both paths: a file name with ", $ or ` must neither break the command nor expand.
        string quotedTemporary = RemotePathUtils.ShellQuote(temporaryPath);
        string quotedTarget = RemotePathUtils.ShellQuote(RemotePath);
        string replaceCommand = "cat " + quotedTemporary + " > " + quotedTarget;
        bool backup = _settings.Editor.BackupBeforeSave && !IsNewFile && !_backupDone;
        if (backup)
        {
            // A failed copy does not stop the save (cat decides the result), as with the "~" files of other editors.
            replaceCommand = "[ ! -e " + quotedTarget + " ] || cp -p " + quotedTarget + " " + RemotePathUtils.ShellQuote(RemotePath + "~") + "; " + replaceCommand;
        }
        // The check of the file runs on the text; the bytes of the hex view are saved as they are.
        ConfigValidator validator = null;
        if (!hexMode && !IsWindowsHost && !_skipValidation && !_settings.Editor.DisableConfigCheck)
        {
            validator = ConfigValidators.Find(RemotePath, DetectSyntax(), base.EditableDocument.GetLineText(0));
        }
        SaveJob job = new SaveJob
        {
            Bytes = bytes,
            TemporaryPath = temporaryPath,
            ReplaceCommand = replaceCommand,
            Version = version,
            HexMode = hexMode,
            Generation = ++_saveGeneration,
            SkipConflictCheck = _forceOverwrite,
            IncludesBackup = backup,
            Encoding = FileEncoding,
            Validator = validator
        };
        _forceOverwrite = false;
        _skipValidation = false;
        return job;
    }

    /// <summary>
    /// Uploads the text to the temporary file and copies it over the target (any thread). Returns false when
    /// the target could not be written, so that sudo can be offered.
    /// </summary>
    private bool UploadAndReplace(SaveJob job)
    {
        if (IsWindowsHost)
        {
            // Written over SFTP straight into the file (with the "~" backup if the job asks for one): there is no temporary file
            // in /tmp and no cat, and a refused write is an error, not a question about sudo.
            SmarTTY.Transport.WindowsRemoteFileEditing.Save(_context.Session, RemotePath, job.Bytes, job.IncludesBackup);
            job.NewSignature = FetchSignature();
            return true;
        }
        using (ScpChannelStream scpChannelStream = _context.Session.OpenUpload(job.TemporaryPath, job.Bytes.Length, mode: TemporaryFileMode))
        {
            scpChannelStream.Write(job.Bytes, 0, job.Bytes.Length);
        }
        if (job.Validator != null)
        {
            // The target is still the old file: a text that the check rejects never replaces it.
            ValidationResult validation = RunValidation(job);
            if (validation.Outcome == ValidationOutcome.Failed)
            {
                job.Validation = validation;
                return false;
            }
        }
        if (_sudoOnSave)
        {
            // The user said at opening that this file is written with sudo: no failing plain attempt first.
            return false;
        }
        if (_context.Session.RunCommandChecked(job.ReplaceCommand, requireSuccess: true) != null)
        {
            job.NewSignature = FetchSignature();
            return true;
        }
        // A lost connection is not a refused write: do not offer sudo for it.
        _context.Session.ThrowIfConnectionLost();
        return false;
    }

    /// <summary>
    /// Runs the check of the file on the uploaded copy (any thread). A check that cannot run, or that finds nothing it
    /// can point to, does not stop the save.
    /// </summary>
    private ValidationResult RunValidation(SaveJob job)
    {
        string command = job.Validator.BuildCommand(RemotePathUtils.ShellQuote(job.TemporaryPath));
        List<string> output = _context.Session.RunCommand(command, out int? exitStatus, timeout: TimeSpan.FromSeconds(20));
        return job.Validator.Interpret(output, exitStatus, job.TemporaryPath);
    }

    /// <summary>Asks whether to retry with sudo and does so (UI thread).</summary>
    private void SaveWithSudo(SaveJob job)
    {
        if (!_sudoOnSave && ThemedMessageBox.Show("Failed to " + (IsNewFile ? "create " : "overwrite ") + RemotePath + ". Try again with sudo?", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            throw new OperationCanceledException();
        }
        RunSudoCommand(job.ReplaceCommand);
    }

    /// <summary>Runs a command through sudo on a shell channel, with the password prompt of the editor (UI thread).</summary>
    private void RunSudoCommand(string command)
    {
        string sudoCommand = "sudo sh -c " + RemotePathUtils.ShellQuote(command);
        SshShellChannel sshShellChannel = _context.Session.OpenShellChannel(sudoCommand, "xterm");
        SudoConfirmationPrompt?.Invoke(sshShellChannel, sudoCommand);
        try
        {
            sshShellChannel.Input.Write(new byte[1] { 3 }, 0, 1);
        }
        catch
        {
        }
        sshShellChannel.WaitClosed();
        int sudoExitStatus = sshShellChannel.ExitStatus;
        if (!sshShellChannel.HasExitStatus && sshShellChannel.ExitSignal == null)
        {
            throw new Exception("Sudo did not report an exit status");
        }
        if (sudoExitStatus != 0)
        {
            throw new Exception("Sudo exited with code " + sudoExitStatus);
        }
    }

    private void CompleteSave(SaveJob job)
    {
        // A save through sudo has no signature yet.
        _remoteSignature = job.NewSignature ?? FetchSignature();
        _rawBytes = job.Bytes;
        _rawEncoding = job.Encoding ?? FileEncoding;
        if (job.IncludesBackup)
        {
            _backupDone = true;
        }
        IsNewFile = false;
        if (job.HexMode && _hexDocument != null)
        {
            _hexDocument.MarkSaved(job.Version, job.Bytes);
            _hexEditor.Invalidate();
            // The text is rebuilt from these bytes when the hex view is closed.
            _hexBytesForTextSave = null;
            _hexTextSnapshot = null;
        }
        else
        {
            MarkSaved(job.Version);
            if (_hexDocument != null && HexDocument.ByteArraysEqual(_hexDocument.ToArray(), job.Bytes))
            {
                // The text was saved back as the exact bytes of the hex view: both views are saved now.
                _hexDocument.MarkSaved(_hexDocument.CurrentVersion, job.Bytes);
            }
        }
        FormatChanged?.Invoke(this, EventArgs.Empty);
        if (!IsModified)
        {
            // Everything is on the server: the draft is of no use (a later change makes a new one).
            DiscardDraft();
        }
    }

    private void RemoveTemporaryFile(SaveJob job)
    {
        if (IsWindowsHost)
        {
            // Nothing was uploaded to a temporary file.
            return;
        }
        try
        {
            _context.Session.RunCommandChecked("rm -f " + RemotePathUtils.ShellQuote(job.TemporaryPath), requireSuccess: true);
        }
        catch
        {
        }
    }
}
