using SmarTTY.Ssh;
using SmarTTY.Forms;
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

    public event EventHandler LoadFailed;

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
            string text = "";
            using (ScpDownloadStream scpDownloadStream = _context.Session.OpenDownload(RemotePath))
            {
                Utf8StreamDecoder utf8StreamDecoder = new Utf8StreamDecoder();
                byte[] bytes = new byte[65536];
                while (true)
                {
                    int value = scpDownloadStream.Read(bytes, 0, bytes.Length);
                    if (value <= 0)
                    {
                        break;
                    }
                    text += utf8StreamDecoder.Decode(bytes, 0, value);
                    BeginInvoke((ThreadStart)(() =>
                    {
                        try
                        {
                            EditableTextDocument editableTextDocument = new EditableTextDocument
                            {
                                DefaultLineEnding = TextDocument.LineEnding.Lf
                            };
                            editableTextDocument.SetText(text);
                            SetDocument(editableTextDocument);
                        }
                        catch
                        {
                        }
                    }));
                }
            }
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
            if (value.Contains("\r\n") && MessageBox.Show("Convert CRLF line endings to LF endings?", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                base.EditableDocument.SetLineEnding(TextDocument.LineEnding.Lf);
                value = base.EditableDocument.GetText();
            }
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            using (ScpChannelStream scpChannelStream = _context.Session.OpenUpload(text, bytes.Length))
            {
                scpChannelStream.Write(bytes, 0, bytes.Length);
            }
            string content = $"cat \"{text}\" > \"{RemotePath}\"";
            if (_context.Session.RunCommandChecked(content, requireSuccess: true) != null)
            {
                ((EditableTextBrowser)this).IsModified = false;
                return;
            }
            if (MessageBox.Show("Failed to overwrite " + RemotePath + ". Try again with sudo?", "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                throw new OperationCanceledException();
            }
            string sudoCommand = "sudo sh -c '" + content + "'";
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
            _context.Session.RunCommandChecked("rm \"" + text + "\"", requireSuccess: true);
        }
    }
}
