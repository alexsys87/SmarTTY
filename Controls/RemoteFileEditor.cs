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
            BeginInvoke((ThreadStart)(() =>
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
            }));
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

    public void Save()
    {
        string text = _settings.Editor.TemporaryDirectory + "/" + Guid.NewGuid().ToString() + ".txt";
        try
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
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            using (ScpChannelStream scpChannelStream = _context.Session.OpenUpload(text, bytes.Length, mode: TemporaryFileMode))
            {
                scpChannelStream.Write(bytes, 0, bytes.Length);
            }
            // Quote both paths: a file name with ", $ or ` must neither break the command nor expand.
            string content = "cat " + RemotePathUtils.ShellQuote(text) + " > " + RemotePathUtils.ShellQuote(RemotePath);
            if (_context.Session.RunCommandChecked(content, requireSuccess: true) != null)
            {
                ((EditableTextBrowser)this).IsModified = false;
                return;
            }
            if (MessageBox.Show("Failed to overwrite " + RemotePath + ". Try again with sudo?", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                throw new OperationCanceledException();
            }
            string sudoCommand = "sudo sh -c " + RemotePathUtils.ShellQuote(content);
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
            ((EditableTextBrowser)this).IsModified = false;
        }
        finally
        {
            _context.Session.RunCommandChecked("rm -f " + RemotePathUtils.ShellQuote(text), requireSuccess: true);
        }
    }
}
