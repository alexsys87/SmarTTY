using SmarTTY.IO;
using SmarTTY.Ssh;
using SmarTTY.Terminal;
using SmarTTY.Utilities;
using System;
using System.IO;
using System.Text;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The loading of a file for the remote file editor, which runs on a thread of its own: whether the file exists (a new file), whether the user
/// may write it (and what to do if not), the size questions, the download with the preview of its beginning. The code is that of
/// <c>RemoteFileEditor.LoadFile</c>, <c>CheckWriteAccess</c> and <c>ChooseLoadLimit</c>, moved here as it was; the questions go through
/// <see cref="IUserPrompt"/>, the calls to the editor through <see cref="IUiDispatcher"/> and <see cref="IRemoteFileLoadView"/>. The answers
/// the user gave stay here (they are what the rest of the editor reads) and are kept for a reload.
/// </summary>
public sealed class RemoteFileLoader
{
    // Minimal interval between previews of a file that is still being downloaded.
    private const int PreviewIntervalMs = 500;

    private readonly RemoteFileServer _server;

    private readonly string _remotePath;

    private readonly IUserPrompt _prompt;

    private readonly IUiDispatcher _ui;

    private readonly IRemoteFileLoadView _view;

    private readonly Func<bool> _autoConfirmSudo;

    /// <param name="autoConfirmSudo">Whether the connection saves with sudo without the question about the missing write permission (read when the question would come).</param>
    public RemoteFileLoader(RemoteFileServer server, string remotePath, IUserPrompt prompt, IUiDispatcher ui, IRemoteFileLoadView view, Func<bool> autoConfirmSudo)
    {
        _server = server;
        _remotePath = remotePath;
        _prompt = prompt;
        _ui = ui;
        _view = view;
        _autoConfirmSudo = autoConfirmSudo;
    }

    /// <summary>The file did not exist when the editor was opened: it is created by the first save.</summary>
    public bool IsNewFile { get; set; }

    /// <summary>
    /// The text cannot be changed: the user chose to read a file that is big or that he has no right to write, or only
    /// the beginning of a huge file is shown (<see cref="IsPreview"/>).
    /// </summary>
    public bool IsReadOnlyMode { get; set; }

    /// <summary>Only the first <see cref="EditorFilePolicy.PreviewBytes"/> of a file that is too big to edit are shown.</summary>
    public bool IsPreview { get; set; }

    /// <summary>The user agreed to write the file with sudo (asked when the file was opened): a save does not try the plain write first.</summary>
    public bool SudoOnSave { get; set; }

    /// <summary>Loads the file; the thread entry. Everything it finds out goes to the view on the UI thread.</summary>
    public void Run()
    {
        try
        {
            if (!_server.Exists(_remotePath))
            {
                // Opening a path that does not exist starts a new file, as in vi or nano.
                string directory = RemotePathUtils.GetDirectoryName(_remotePath);
                if (!string.IsNullOrEmpty(directory) && !_server.Exists(directory))
                {
                    throw new RemoteOperationException("Cannot create " + _remotePath + ": the directory " + directory + " does not exist.");
                }
                CheckWriteAccess(string.IsNullOrEmpty(directory) ? "/" : directory, forNewFile: true);
                IsNewFile = true;
                _ui.Post(() => _view.Finish(null, null));
                return;
            }
            IsNewFile = false;
            // A reload keeps the answers given when the file was opened (the permission and the size questions).
            if (!_view.IsReloading)
            {
                CheckWriteAccess(_remotePath, forNewFile: false);
            }
            // Taken before the download: a change made while it runs is then noticed by the next save.
            string signature = _server.FetchSignature();
            MemoryStream downloaded = new MemoryStream();
            // Only the text of the preview is decoded as it arrives; the file is decoded once it is complete.
            StringBuilder content = new StringBuilder();
            bool showPreview = !_view.IsReloading;
            using (Stream scpDownloadStream = _server.OpenForReading(out long remoteLength))
            {
                // The size is known from the first SCP header (or from SFTP), before any data is read.
                long limit = _view.IsReloading ? (IsPreview ? EditorFilePolicy.PreviewBytes : 0) : ChooseLoadLimit(remoteLength);
                long received = 0;
                Utf8StreamDecoder utf8StreamDecoder = new Utf8StreamDecoder();
                byte[] bytes = new byte[65536];
                DateTime lastPreview = DateTime.Now;
                while (true)
                {
                    if (_view.IsClosed)
                    {
                        // The tab was closed: nothing is waiting for the file any more.
                        return;
                    }
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
                        string preview = content.ToString().TrimStart('﻿');
                        _ui.Post(() =>
                        {
                            try
                            {
                                _view.ShowPreview(preview);
                            }
                            catch
                            {
                            }
                        });
                    }
                }
            }
            byte[] file = downloaded.ToArray();
            _ui.Post(() => _view.Finish(file, signature));
        }
        catch (OperationCanceledException)
        {
            // The user chose not to open the file: the tab closes without an error message.
            _ui.Post(() => _view.Cancelled());
        }
        catch (Exception exception)
        {
            // A closed tab needs no message about the file it was loading.
            _ui.Post(() => _view.Failed(exception));
        }
    }

    /// <summary>
    /// Warns when the user cannot write the file (or, for a new file, its directory) before he types anything: he may
    /// choose to save with sudo (asked for at the save), to read only, or not to open the file. Loading thread.
    /// </summary>
    private void CheckWriteAccess(string target, bool forNewFile)
    {
        if (_server.IsWindows)
        {
            // A new file: its folder is not probed (that would mean creating a file to delete it), a refused save says so then.
            if (forNewFile || _server.IsWritable(target))
            {
                return;
            }
            // No sudo on Windows: the choice is to read the file or not to open it.
            if (_prompt.Ask("You do not have permission to write to " + target + ".\n\n"
                + "Yes: open the file read-only.\n"
                + "No: do not open the file.", PromptButtons.YesNo) != PromptAnswer.Yes)
            {
                throw new OperationCanceledException();
            }
            IsReadOnlyMode = true;
            return;
        }
        // No answer is not "not writable": IsWritable checks the connection before it says no.
        if (_server.IsWritable(target))
        {
            return;
        }
        if (_autoConfirmSudo())
        {
            // The connection asks to save with sudo without the question: the save goes through sudo directly.
            SudoOnSave = true;
            return;
        }
        string what = forNewFile ? "the directory " + target + ", where this new file would be created" : target;
        switch (_prompt.Ask("You do not have permission to write to " + what + ".\n\n"
            + "Yes: edit the text and save it with sudo (the password is asked when you save).\n"
            + "No: open the file read-only.\n"
            + "Cancel: do not open the file.", PromptButtons.YesNoCancel))
        {
            case PromptAnswer.Yes:
                SudoOnSave = true;
                break;
            case PromptAnswer.No:
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
                    if (_prompt.Ask(_remotePath + " is " + size + ". A large file makes the editor slow and takes a lot of memory.\n\nOpen it read-only?", PromptButtons.OkCancel) != PromptAnswer.Ok)
                    {
                        throw new OperationCanceledException();
                    }
                    return 0;
                }
                switch (_prompt.Ask(_remotePath + " is " + size + ". A large file makes the editor slow and takes a lot of memory.\n\n"
                    + "Yes: open it for editing.\n"
                    + "No: open it read-only (safer).\n"
                    + "Cancel: do not open it.", PromptButtons.YesNoCancel))
                {
                    case PromptAnswer.Yes:
                        return 0;
                    case PromptAnswer.No:
                        IsReadOnlyMode = true;
                        return 0;
                    default:
                        throw new OperationCanceledException();
                }
            case LargeFileAction.PreviewOnly:
                if (_prompt.Ask(_remotePath + " is " + size + ", too large to edit here.\n\nOpen the first " + StringUtils.FormatSize(EditorFilePolicy.PreviewBytes, appendUnit: true) + " read-only?", PromptButtons.YesNo) != PromptAnswer.Yes)
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
}
