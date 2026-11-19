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

public partial class RemoteFileEditor : CodeEditorPP, IRemoteFileLoadView, IRemoteSaveSource, IRemoteFileContentHost, IRemoteFileFlowHost
{

    private ITerminalSessionContext _context;

    // The file as it is on the server: its probes, reading, and the version that tells whether somebody else changed it.
    private RemoteFileServer _server;

    public readonly string RemotePath;

    private readonly TextDocument.TextPosition _initialCaret;

    // What the column of _initialCaret counts (a compiler message may say screen columns or bytes).
    private readonly ColumnUnit _initialColumnUnit;

    private Thread _loadThread;

    // The loading of the file (the questions about the write permission and the size, the download): its flags are the ones of the editor.
    private RemoteFileLoader _loader;

    // What a save does on the server: the upload, the check, the replacement (with sudo), the clean-up.
    private RemoteFileSaver _saver;

    // The state of the saves (the answers to the questions, the generation) and the taking of the bytes to save.
    private RemoteFileSession _session;

    private GlobalSettings _settings;

    private Action<Exception> _errorHandler;

    /// <summary>
    /// Shows the prompt that a <c>sudo</c> command printed on its shell channel (the channel, the command line). The
    /// themed WPF window is injected here by <c>App</c>: this editor is compiled by the Mono test harness, which
    /// cannot reference the WPF dialogs.
    /// </summary>
    public static Action<SshShellChannel, string> SudoConfirmationPrompt;

    // The bytes on the server, the encoding and the line breaks, and the switching between the text and the hex view (see RemoteFileContent).
    // Made by the constructor that opens a file: the base classes may ask for IsModified before that.
    private RemoteFileContent _content;

    // The hex view of the file: the WinForms control of this editor, or a view given by the constructor (the WPF view of the tab), which is shown
    // through _hexPlace.
    private IHexEditorView _hexEditor;

    private HexEditorControl _hexControl;

    private IHexViewPlace _hexPlace;

    /// <summary>The hex view of the file.</summary>
    public IHexEditorView HexEditor => _hexEditor;

    /// <summary>The hex view as a WinForms control of this editor (for the context menu of the panel); null when the view was given.</summary>
    internal HexEditorControl HexEditorControl => _hexControl;

    /// <summary>
    /// Where a hex view given to the constructor is shown, and what takes the colours and the font of the text (the tab). Until it is set such a
    /// view is neither shown nor hidden.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal IHexViewPlace HexViewPlace
    {
        get
        {
            return _hexPlace;
        }
        set
        {
            _hexPlace = value;
            UpdateHexAppearance();
        }
    }

    /// <summary>The editor shows the bytes instead of the text.</summary>
    public bool IsHexMode => _content != null && _content.HexMode;

    /// <summary>The file is loaded and nothing is being saved: the hex view can be opened or closed.</summary>
    public bool CanToggleHexMode => _flow != null && _flow.CanToggleHexMode;

    /// <summary>Raised when the editor switched between the text and the hex view.</summary>
    public event EventHandler HexModeChanged;

    // Whether the file is loaded, loaded again or saved, and what a save and a reload do (see RemoteFileFlow).
    private RemoteFileFlow _flow;

    // What a reload puts back once the new text is in: the caret, the hex view at its offset, the folds.
    private bool _restoreCaret;

    private TextDocument.TextPosition _caretToRestore;

    // A reload started in the hex view: open it again on the new bytes, at the same offset.
    private bool _restoreHexAfterReload;

    private int _hexOffsetToRestore;

    // The blocks folded when the reload started: the fresh document has no folds, so they are put back once it is in.
    private FoldedLineInfo[] _foldsToRestore;

    /// <summary>The encoding the file is read in and saved in.</summary>
    public TextFileEncoding FileEncoding => _content.FileEncoding;

    /// <summary>The line breaks of the file as it was loaded, or as the user converted them.</summary>
    public LineEndingKind LineEndings => _content.LineEndings;

    /// <summary>Raised on the UI thread when the encoding or the line endings changed (or the file was loaded).</summary>
    public event EventHandler FormatChanged;

    /// <summary>
    /// The encoding can be reinterpreted: the whole file has been loaded from the server (reading it again in another
    /// encoding does not write anything, so a read-only file qualifies, a preview of a huge file does not).
    /// </summary>
    public bool CanReopenWithEncoding => _flow.CanReopenWithEncoding;

    /// <summary>The file did not exist when the editor was opened: it is created by the first save.</summary>
    public bool IsNewFile { get => _loader.IsNewFile; private set => _loader.IsNewFile = value; }

    /// <summary>
    /// The text cannot be changed: the user chose to read a file that is big or that he has no right to write, or only
    /// the beginning of a huge file is shown (<see cref="IsPreview"/>).
    /// </summary>
    public bool IsReadOnlyMode { get => _loader.IsReadOnlyMode; private set => _loader.IsReadOnlyMode = value; }

    /// <summary>The file has been loaded completely and is not being loaded again: only then the text can be saved.</summary>
    public bool CanSave => _flow.CanSave;

    /// <summary>Only the first <see cref="EditorFilePolicy.PreviewBytes"/> of a file that is too big to edit are shown.</summary>
    public bool IsPreview { get => _loader.IsPreview; private set => _loader.IsPreview = value; }

    // Unsaved changes are written to a draft on this computer every few seconds (see EditorDraftStore), so that they
    // survive a lost connection and a closed program. The drafts of all editors live in one folder.
    private static readonly string DraftsDirectory = Path.Combine(GlobalSettings.DataDirectory, "Drafts");

    private static readonly EditorDraftStore Drafts = new EditorDraftStore(DraftsDirectory, new DpapiDraftProtector());

    private const int DraftIntervalMs = 10000;

    private readonly string _draftKey;

    // The keys of the drafts the editors of this program have claimed (see DraftKeyClaims).
    private static readonly DraftKeyClaims DraftKeys = new DraftKeyClaims(DraftsDirectory);

    // The draft of this editor: its key, when the text is taken, the writes and removals (see RemoteFileDrafts).
    private RemoteFileDrafts _drafts;

    private System.Windows.Forms.Timer _draftTimer;

    public event EventHandler LoadFailed;

    /// <summary>Raised on the UI thread once the whole file has been downloaded and the editor became editable.</summary>
    public event EventHandler LoadCompleted;

    public RemoteFileEditor()
        : this((IHexEditorView)null)
    {
    }

    // A null hex view: the editor makes its own WinForms one.
    private RemoteFileEditor(IHexEditorView hexView)
    {
        InitializeComponent();
        if (hexView != null)
        {
            _hexEditor = hexView;
            return;
        }
        _hexControl = new HexEditorControl
        {
            Dock = DockStyle.Fill,
            Visible = false,
            Font = Font,
            BackColor = BackColor,
            ForeColor = ForeColor
        };
        _hexEditor = _hexControl;
        Controls.Add(_hexControl);
    }

    // Gives a hex view of the place its colours and font.
    private void UpdateHexAppearance()
    {
        if (_hexPlace == null)
        {
            return;
        }
        System.Drawing.Font font = LogicalFont ?? Font;
        _hexPlace.SetAppearance(BackColor.ToRgb(), ForeColor.ToRgb(), font.FontFamily.Name, font.SizeInPoints * FontZoom);
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
        if (IsHexMode)
        {
            _content.ExitHexMode();
        }
        else
        {
            _content.EnterHexMode();
        }
    }

    /// <summary>Puts the keyboard focus on the view that is shown: the text or the hex editor.</summary>
    public void FocusActiveView()
    {
        if (IsHexMode)
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
        if (_hexControl != null)
        {
            _hexControl.Font = Font;
        }
        UpdateHexAppearance();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        if (_hexControl != null)
        {
            _hexControl.BackColor = BackColor;
        }
        UpdateHexAppearance();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        if (_hexControl != null)
        {
            _hexControl.ForeColor = ForeColor;
        }
        UpdateHexAppearance();
    }

    /// <summary>The unsaved state covers both views: the bytes in hex mode, the text in text mode.</summary>
    public override bool IsModified
    {
        get
        {
            return (_content != null) ? _content.IsModified : base.IsModified;
        }
        set
        {
            if (_content != null)
            {
                _content.IsModified = value;
            }
            else
            {
                base.IsModified = value;
            }
        }
    }

    public override void CopySelection(bool select = true)
    {
        if (IsHexMode)
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
        if (IsHexMode)
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
        if (IsHexMode)
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
        if (IsHexMode)
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
        if (IsHexMode)
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
        if (IsHexMode)
        {
            _hexEditor.SelectAll();
        }
        else
        {
            base.SelectAllText();
        }
    }

    /// <param name="initialColumnUnit">What the column of <paramref name="textPosition"/> counts: the caret is put at the character it points at once the text is loaded.</param>
    /// <param name="hexView">The hex view of the file (the WPF view of the tab, shown through <see cref="HexViewPlace"/>); null: the editor makes its own WinForms one.</param>
    public RemoteFileEditor(ITerminalSessionContext terminalSessionContext, string text, GlobalSettings globalSettings, TextDocument.TextPosition textPosition, Action<Exception> action = null, ColumnUnit initialColumnUnit = ColumnUnit.Characters, IHexEditorView hexView = null)
        : this(hexView)
    {
        _initialColumnUnit = initialColumnUnit;
        this._context = terminalSessionContext;
        RemotePath = text;
        SessionRemoteFileHost host = new SessionRemoteFileHost(terminalSessionContext);
        _server = new RemoteFileServer(host, text);
        _session = new RemoteFileSession(text);
        _content = new RemoteFileContent(text, this, _hexEditor, _session);
        _content.HexModeChanged += (object sender, EventArgs e) => HexModeChanged?.Invoke(this, EventArgs.Empty);
        _content.FormatChanged += (object sender, EventArgs e) => FormatChanged?.Invoke(this, EventArgs.Empty);
        _saver = new RemoteFileSaver(host, _server, text, () => _loader.SudoOnSave);
        _loader = new RemoteFileLoader(_server, text, new ControlUserPrompt(this), new ControlUiDispatcher(this), this, () => _context.AutoConfirmSudo);
        _draftKey = DraftKeys.Claim(EditorDraftStore.KeyFor(terminalSessionContext?.TitleWithUserName, text));
        _drafts = new RemoteFileDrafts(Drafts, _draftKey);
        _flow = new RemoteFileFlow(text, this, _server, _saver, _session, _content, _loader);
        _flow.SavingChanged += (object sender, EventArgs e) => SavingChanged?.Invoke(this, EventArgs.Empty);
        _flow.FormatChanged += (object sender, EventArgs e) => FormatChanged?.Invoke(this, EventArgs.Empty);
        RemoteFileDrafts.StartCleanupOnce(Drafts);
        Disposed += (object sender, EventArgs e) =>
        {
            _draftTimer?.Dispose();
            DraftKeys.Release(_draftKey);
        };
        ((EditableTextBrowser)this).ReadOnly = true;
        _initialCaret = textPosition;
        _loadThread = new Thread(_loader.Run);
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

    // What the loading thread (RemoteFileLoader) tells the editor: these run on the UI thread.
    bool IRemoteFileLoadView.IsReloading => _flow.IsReloading;

    bool IRemoteFileLoadView.IsClosed => IsDisposed;

    void IRemoteFileLoadView.ShowPreview(string text)
    {
        EditableTextDocument editableTextDocument = new EditableTextDocument
        {
            DefaultLineEnding = TextDocument.LineEnding.Lf
        };
        editableTextDocument.SetText(text);
        SetDocument(editableTextDocument);
    }

    void IRemoteFileLoadView.Finish(byte[] file, string signature)
    {
        FinishLoading(file, signature);
    }

    void IRemoteFileLoadView.Cancelled()
    {
        LoadFailed?.Invoke(this, EventArgs.Empty);
    }

    void IRemoteFileLoadView.Failed(Exception exception)
    {
        _flow.LoadFailed(exception);
    }

    private bool IsWindowsHost => _server.IsWindows;

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
            _drafts.BaseHash = EditorDraftStore.HashText(text);
            _drafts.SizeEstimate = text.Length;
            string expiryNotice = RemoteFileDrafts.TakeExpiryNotice();
            if (expiryNotice != null)
            {
                ThemedMessageBox.Show(this, expiryNotice, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            EditorDraft restoredDraft = null;
            if (_flow.IsReloading)
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
            _content.ApplyContent(file, encoding, text);
            _flow.FinishLoading(signature);
            ((EditableTextBrowser)this).ReadOnly = IsReadOnlyMode;
            if (restoredDraft != null)
            {
                if (restoredDraft.Bytes != null)
                {
                    // The draft holds the bytes of the hex view: show them there, where they can be saved as they are.
                    _content.ShowRestoredHexDraft(restoredDraft.Bytes);
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
                    _content.EnterHexMode();
                    if (IsHexMode)
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
        catch (Exception exception)
        {
            // Not silent: the tab would stay empty and read-only with no word about why.
            _flow.LoadFailed(exception);
        }
    }

    /// <summary>
    /// Offers the draft of unsaved changes left by an earlier editor of this file (the connection was lost or the program
    /// ended before they were saved). Returns the draft to restore, or null to open the file as it is on the server.
    /// </summary>
    private EditorDraft AskToRestoreDraft(byte[] serverBytes, string serverText)
    {
        EditorDraft draft = _drafts.FindUnsaved(serverBytes, serverText);
        if (draft == null)
        {
            return null;
        }
        string changed = _drafts.ServerChangedSince(draft)
            ? "The file on the server has changed since then; saving the draft would overwrite those changes.\n\n"
            : "";
        string when = draft.SavedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        if (ThemedMessageBox.Show(this, "Unsaved changes of " + RemotePath + " from " + when + " were found: the connection was lost or the program was closed before they were saved.\n\n"
            + changed
            + "Yes: restore the draft.\n"
            + "No: discard it and open the file from the server.\n\n"
            + "A draft that is not written to for " + RemoteFileDrafts.MaxAge.Days + " days is removed automatically.", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            return draft;
        }
        _drafts.Queue(null);
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
            HexDocument hexDocument = _content.HexDocument;
            bool hex = _content.HexMode && hexDocument != null;
            object version = hex ? hexDocument.CurrentVersion : CurrentVersion;
            if (!_drafts.ShouldCapture(version, hex ? hexDocument.Length : (long?)null))
            {
                return;
            }
            string draftText = hex ? null : base.EditableDocument.GetText();
            byte[] draftBytes = hex ? hexDocument.ToArray() : null;
            _drafts.Captured(hex ? draftBytes.Length : draftText.Length);
            _drafts.Queue(new EditorDraft
            {
                RemotePath = RemotePath,
                SavedUtc = DateTime.UtcNow,
                BaseHash = _drafts.BaseHash,
                IsNewFile = IsNewFile,
                Text = draftText,
                Bytes = draftBytes
            });
        }
        catch
        {
            // A draft is a convenience: a failure to take it must not disturb the editing.
        }
    }

    /// <summary>
    /// The user chose not to save the changes (closing the tab or the program): the draft is removed, otherwise the next
    /// opening of the file would offer them again.
    /// </summary>
    public void DiscardDraft()
    {
        _drafts.Discard();
    }

    /// <summary>
    /// Loads the file from the server again, dropping the text in the editor and its draft (the caller asks first when
    /// there are unsaved changes). The caret stays where it was.
    /// </summary>
    public void Reload()
    {
        _flow.Reload();
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
        _content.SetSaveEncoding(encoding);
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
        string text = TextFileEncodings.Decode(_content.RawBytes, encoding);
        _drafts.BaseHash = EditorDraftStore.HashText(text);
        _content.ApplyContent(_content.RawBytes, encoding, text);
    }

    /// <summary>
    /// Converts every line break of the file to <paramref name="lineEnding"/>, and gives the lines typed later the same
    /// one. The file counts as changed: it has to be saved.
    /// </summary>
    public void SetLineEnding(TextDocument.LineEnding lineEnding)
    {
        _content.SetLineEnding(lineEnding);
    }

    /// <summary>Saves the file when its tab closes: false when the tab has to stay (the user cancelled, or the save failed and said why).</summary>
    public bool TrySave()
    {
        return _flow.TrySave();
    }

    /// <summary>A background save (SaveInBackground) is running.</summary>
    public bool IsSaving => _flow != null && _flow.IsSaving;

    /// <summary>Raised on the UI thread when <see cref="IsSaving"/> changes.</summary>
    public event EventHandler SavingChanged;

    /// <summary>Saves the file and returns when it is written (used when a tab or the window closes).</summary>
    public void Save()
    {
        _flow.Save();
    }

    /// <summary>
    /// Saves the file without blocking the UI on a slow connection (Ctrl+S). Errors and the sudo prompt are
    /// shown when they happen; a save requested meanwhile runs once this one has finished.
    /// </summary>
    public void SaveInBackground()
    {
        _flow.SaveInBackground();
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
    /// The file can be run in a terminal of the session (F5): it is loaded in full, the host has a shell, and the file
    /// name or the first line says how to run it.
    /// </summary>
    public bool CanRunInTerminal => _flow.IsLoaded && !_flow.IsReloading && !IsPreview && !IsWindowsHost && base.EditableDocument != null
        && RemoteScript.CanRun(RemotePathUtils.GetFileName(RemotePath), base.EditableDocument.GetLineText(0));

    /// <summary>
    /// The file can be followed like tail -f in a terminal tab: it is on the server and the host is a Unix one. It is the
    /// file on the server that is followed, not the text in the editor.
    /// </summary>
    public bool CanFollowLog => _flow.CanFollowLog;

    /// <summary>The interpreter for a file that is not executable and has no shebang (see <see cref="RemoteScript.DefaultInterpreter"/>).</summary>
    public string RunInterpreter => RemoteScript.DefaultInterpreter(RemotePathUtils.GetFileName(RemotePath)) ?? "sh";

    /// <summary>
    /// The differences between the file on the server (as it was opened, or last saved) and the text in the editor,
    /// for the Show Changes window.
    /// </summary>
    public TextDiff.Result GetChanges()
    {
        return _content.GetChanges();
    }

    /// <summary>
    /// Takes the bytes to save (UI thread): the questions and the bytes are in <see cref="RemoteFileSession.PrepareSave"/>.
    /// </summary>
    private RemoteSaveJob PrepareSave()
    {
        return _session.PrepareSave(this, () => new RemoteSaveOptions
        {
            TemporaryDirectory = _settings.Editor.TemporaryDirectory,
            BackupBeforeSave = _settings.Editor.BackupBeforeSave,
            DisableConfigCheck = _settings.Editor.DisableConfigCheck,
            IsNewFile = IsNewFile,
            IsWindowsHost = IsWindowsHost
        });
    }

    // What the taking of the bytes to save (RemoteFileSession.PrepareSave) needs from the editor.
    bool IRemoteSaveSource.HexMode => _content.HexMode && _content.HexDocument != null;

    object IRemoteSaveSource.HexVersion => _content.HexDocument.CurrentVersion;

    byte[] IRemoteSaveSource.HexBytes()
    {
        return _content.HexDocument.ToArray();
    }

    string IRemoteSaveSource.GetText()
    {
        return base.EditableDocument.GetText();
    }

    object IRemoteSaveSource.TextVersion => CurrentVersion;

    LineEndingKind IRemoteSaveSource.LineEndings => LineEndings;

    TextFileEncoding IRemoteSaveSource.FileEncoding => FileEncoding;

    void IRemoteSaveSource.ConvertLineEndingsToLf()
    {
        SetLineEnding(TextDocument.LineEnding.Lf);
    }

    void IRemoteSaveSource.SaveWithoutByteOrderMark()
    {
        SetSaveEncoding(TextFileEncodings.Utf8);
    }

    bool IRemoteSaveSource.Confirm(string question)
    {
        return ThemedMessageBox.Show(question, "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    void IRemoteSaveSource.ClearMessages()
    {
        ClearMessages();
    }

    SyntaxHighlighting.Language IRemoteSaveSource.DetectSyntax()
    {
        return DetectSyntax();
    }

    string IRemoteSaveSource.FirstLine()
    {
        return base.EditableDocument.GetLineText(0);
    }

    // What the content of the file and the switching between the views (RemoteFileContent) need from the editor.
    EditableTextDocument IRemoteFileContentHost.TextDocument => base.EditableDocument;

    bool IRemoteFileContentHost.IsTextModified
    {
        get
        {
            return base.IsModified;
        }
        set
        {
            base.IsModified = value;
        }
    }

    bool IRemoteFileContentHost.TextReadOnly
    {
        get
        {
            return ((EditableTextBrowser)this).ReadOnly;
        }
        set
        {
            ((EditableTextBrowser)this).ReadOnly = value;
        }
    }

    TextDocument.TextPosition IRemoteFileContentHost.Caret => CaretPosition;

    void IRemoteFileContentHost.SetCaret(TextDocument.TextPosition position)
    {
        SetCaret(position);
    }

    void IRemoteFileContentHost.SetDocument(EditableTextDocument document)
    {
        SetDocument(document);
    }

    void IRemoteFileContentHost.FocusText()
    {
        Focus();
    }

    void IRemoteFileContentHost.MarkTextSaved(object version)
    {
        MarkSaved(version);
    }

    void IRemoteFileContentHost.RaiseModifiedChanged()
    {
        OnModifiedChanged();
    }

    bool IRemoteFileContentHost.IsLoaded => _flow.IsLoaded;

    bool IRemoteFileContentHost.IsSaving => IsSaving;

    bool IRemoteFileContentHost.IsReadOnlyMode => IsReadOnlyMode;

    void IRemoteFileContentHost.ShowHexView()
    {
        if (_hexControl != null)
        {
            _hexControl.Visible = true;
            _hexControl.BringToFront();
        }
        _hexPlace?.ShowHexView();
    }

    void IRemoteFileContentHost.HideHexView()
    {
        if (_hexControl != null)
        {
            _hexControl.Visible = false;
        }
        _hexPlace?.HideHexView();
    }

    void IRemoteFileContentHost.ShowError(string message)
    {
        ThemedMessageBox.Show(this, message, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
    }

    bool IRemoteFileContentHost.ConfirmWarning(string question)
    {
        return ThemedMessageBox.Show(this, question, "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    // What the loading and the saves (RemoteFileFlow) need from the editor: the questions, the threads and the view.
    RemoteSaveJob IRemoteFileFlowHost.PrepareSave()
    {
        return PrepareSave();
    }

    PromptAnswer IRemoteFileFlowHost.Ask(string text, PromptButtons buttons, PromptIcon icon)
    {
        MessageBoxButtons boxButtons = buttons == PromptButtons.OkCancel ? MessageBoxButtons.OKCancel
            : (buttons == PromptButtons.YesNo ? MessageBoxButtons.YesNo : MessageBoxButtons.YesNoCancel);
        switch (ThemedMessageBox.Show(text, "SmarTTY", boxButtons, icon == PromptIcon.Warning ? MessageBoxIcon.Warning : MessageBoxIcon.Question))
        {
            case DialogResult.OK:
                return PromptAnswer.Ok;
            case DialogResult.Yes:
                return PromptAnswer.Yes;
            case DialogResult.No:
                return PromptAnswer.No;
            default:
                return PromptAnswer.Cancel;
        }
    }

    void IRemoteFileFlowHost.Inform(string text)
    {
        ThemedMessageBox.Show(text, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void IRemoteFileFlowHost.ReportError(Exception exception)
    {
        ReportError(exception);
    }

    bool IRemoteFileFlowHost.ShowIssuesInText(List<ValidationIssue> issues)
    {
        // How many problems get an inline message; ParseIssues caps the list at the same number.
        const int MaxInline = 50;
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
        return showInText;
    }

    void IRemoteFileFlowHost.GoToIssue(ValidationIssue issue)
    {
        GoToPosition(Math.Max(0, issue.Line - 1), Math.Max(0, issue.Column - 1));
        FocusActiveView();
    }

    bool IRemoteFileFlowHost.ServiceReloadOfferDisabled => _settings.Editor.DisableServiceReloadOffer;

    void IRemoteFileFlowHost.ThrowIfConnectionLost()
    {
        _context.Session.ThrowIfConnectionLost();
    }

    void IRemoteFileFlowHost.StartThread(Action work, string name)
    {
        Thread thread = new Thread(() => work())
        {
            IsBackground = true,
            Name = name
        };
        thread.Start();
    }

    void IRemoteFileFlowHost.QueueWork(Action work)
    {
        ThreadPool.QueueUserWorkItem((object state) => work());
    }

    bool IRemoteFileFlowHost.Post(Action action)
    {
        try
        {
            BeginInvoke((ThreadStart)(() => action()));
            return true;
        }
        catch
        {
            // The editor was closed meanwhile.
            return false;
        }
    }

    void IRemoteFileFlowHost.ClearMessages()
    {
        ClearMessages();
    }

    void IRemoteFileFlowHost.KeepViewForReload()
    {
        _restoreCaret = true;
        _caretToRestore = CaretPosition;
        _restoreHexAfterReload = IsHexMode;
        _hexOffsetToRestore = IsHexMode ? _hexEditor.CaretOffset : 0;
        _foldsToRestore = GetFoldState();
        if (IsHexMode)
        {
            _hexEditor.ReadOnly = true;
        }
        ((EditableTextBrowser)this).ReadOnly = true;
    }

    void IRemoteFileFlowHost.StartLoading()
    {
        _loadThread = new Thread(_loader.Run);
        _loadThread.Start();
    }

    void IRemoteFileFlowHost.SetTextReadOnly(bool readOnly)
    {
        ((EditableTextBrowser)this).ReadOnly = readOnly;
    }

    void IRemoteFileFlowHost.RaiseLoadFailed()
    {
        LoadFailed?.Invoke(this, EventArgs.Empty);
    }

    void IRemoteFileFlowHost.DiscardDraft()
    {
        DiscardDraft();
    }

}
