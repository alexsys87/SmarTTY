using SmarTTY.Ssh;
using SmarTTY.Forms;
using SmarTTY.IO;
using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using System;
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
                IsNewFile = true;
                BeginInvoke((ThreadStart)(() => FinishLoading("")));
                return;
            }
            StringBuilder content = new StringBuilder();
            using (ScpDownloadStream scpDownloadStream = _context.Session.OpenDownload(RemotePath))
            {
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
                    content.Append(utf8StreamDecoder.Decode(bytes, 0, value));
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

    private bool RemotePathExists(string path)
    {
        return _context.Session.RunCommandChecked("test -e " + RemotePathUtils.ShellQuote(path), requireSuccess: true) != null;
    }

    /// <summary>Shows the downloaded text (UI thread) and makes the editor editable.</summary>
    private void FinishLoading(string text)
    {
        try
        {
            EditableTextDocument document = new EditableTextDocument
            {
                DefaultLineEnding = TextDocument.LineEnding.Lf
            };
            document.SetText(text);
            SetDocument(document);
            ((EditableTextBrowser)this).ReadOnly = false;
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
        return _context.Session.RunCommandChecked(job.ReplaceCommand, requireSuccess: true) != null;
    }

    /// <summary>Asks whether to retry with sudo and does so (UI thread).</summary>
    private void SaveWithSudo(SaveJob job)
    {
        if (MessageBox.Show("Failed to " + (IsNewFile ? "create " : "overwrite ") + RemotePath + ". Try again with sudo?", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
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
