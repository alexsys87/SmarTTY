using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The loading and the saves of a remote file in its editor: whether the file is loaded, loaded again or saved in the background, the version of
/// the file on the server that a save compares with, a save asked for while one runs and a reload asked for by the answer to a conflict; and what
/// a save does (<c>Save</c> when a tab closes, <c>SaveInBackground</c> for Ctrl+S, the end of a background save with its questions, sudo, the check
/// of the file, the offer to reload the service that reads it), a reload and a load that failed. It is what <c>RemoteFileEditor</c> had in its fields
/// and in <c>TrySave</c>, <c>Save</c>, <c>SaveInBackground</c>, <c>FinishBackgroundSave</c>, <c>HandleValidationFailure</c>, <c>ReloadToOffer</c>,
/// <c>OfferReload</c>, <c>FinishReload</c>, <c>SaveWithSudo</c>, <c>CompleteSave</c>, <c>Reload</c> and <c>HandleLoadError</c>, moved here as it was.
/// The editor is reached through <see cref="IRemoteFileFlowHost"/> (the questions, the threads, the view); the server, the save, the loader (its
/// flags), the session (the state of the saves) and the content (the bytes, the modified state) are the classes of the editor.
/// </summary>
public sealed class RemoteFileFlow
{
    /// <summary>
    /// How long a save that is asked for when a tab or the window closes waits for the background save that is still running. That
    /// one is over in moments on a connection that works; on one that went away without a word it never ends, and the window waited
    /// with it for ever.
    /// </summary>
    private static readonly TimeSpan BackgroundSaveWait = TimeSpan.FromSeconds(30);

    private readonly string _remotePath;

    private readonly IRemoteFileFlowHost _host;

    private readonly RemoteFileServer _server;

    private readonly RemoteFileSaver _saver;

    private readonly RemoteFileSession _session;

    private readonly RemoteFileContent _content;

    // The loading of the file: its flags (new file, read-only, preview, sudo) are the ones of the editor.
    private readonly RemoteFileLoader _loader;

    // The file on the server as it was when it was opened or last saved (see RemoteFileServer.FetchSignature): a save that finds
    // something else there asks before it overwrites the changes of somebody else.
    private string _remoteSignature;

    // The conflict question was answered "reload": the server version is loaded once the save has ended.
    private bool _pendingReload;

    // Set by the background thread once its upload and replace attempt finished (successfully or not).
    private ManualResetEvent _backgroundUploadDone;

    private bool _saveRequestedWhileSaving;

    public RemoteFileFlow(string remotePath, IRemoteFileFlowHost host, RemoteFileServer server, RemoteFileSaver saver, RemoteFileSession session,
        RemoteFileContent content, RemoteFileLoader loader)
    {
        _remotePath = remotePath;
        _host = host;
        _server = server;
        _saver = saver;
        _session = session;
        _content = content;
        _loader = loader;
    }

    /// <summary>The whole file has been loaded (it stays loaded while it is loaded again).</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>A reload of the opened file is running (the text stays on screen and the editor read-only until it is done).</summary>
    public bool IsReloading { get; private set; }

    /// <summary>A background save (<see cref="SaveInBackground"/>) is running.</summary>
    public bool IsSaving { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="IsSaving"/> changes.</summary>
    public event EventHandler SavingChanged;

    /// <summary>Raised when a reload starts and when a reload failed (the editor tells the format then).</summary>
    public event EventHandler FormatChanged;

    /// <summary>The file has been loaded completely and is not being loaded again: only then the text can be saved.</summary>
    public bool CanSave => !_loader.IsReadOnlyMode && IsLoaded && !IsReloading;

    /// <summary>The file is loaded and nothing is being saved: the hex view can be opened or closed.</summary>
    public bool CanToggleHexMode => IsLoaded && !IsReloading && !IsSaving;

    /// <summary>
    /// The encoding can be reinterpreted: the whole file has been loaded from the server (reading it again in another
    /// encoding does not write anything, so a read-only file qualifies, a preview of a huge file does not).
    /// </summary>
    public bool CanReopenWithEncoding => IsLoaded && !IsReloading && _content.RawBytes != null && !_loader.IsNewFile && !_loader.IsPreview;

    /// <summary>
    /// The file can be followed like tail -f in a terminal tab: it is on the server and the host is a Unix one. It is the
    /// file on the server that is followed, not the text in the editor.
    /// </summary>
    public bool CanFollowLog => IsLoaded && !IsReloading && !_loader.IsNewFile && !_server.IsWindows;

    /// <summary>The downloaded file is in the editor (UI thread): <paramref name="signature"/> is the version a save compares with.</summary>
    public void FinishLoading(string signature)
    {
        _remoteSignature = signature;
        _session.BackupDone = false;
        IsReloading = false;
        IsLoaded = true;
    }

    /// <summary>Shows a loading error; a failed reload keeps the tab with the text of the user.</summary>
    public void LoadFailed(Exception exception)
    {
        try
        {
            _host.ReportError(exception);
            if (IsReloading)
            {
                // The text on screen is still the user's: keep the tab.
                IsReloading = false;
                _host.SetTextReadOnly(_loader.IsReadOnlyMode);
                FormatChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _host.RaiseLoadFailed();
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Loads the file from the server again, dropping the text in the editor and its draft (the caller asks first when
    /// there are unsaved changes). The caret stays where it was.
    /// </summary>
    public void Reload()
    {
        if (IsSaving || !IsLoaded || IsReloading || _loader.IsNewFile || _loader.IsPreview)
        {
            return;
        }
        // The messages name lines of the text that is about to be replaced.
        _host.ClearMessages();
        IsReloading = true;
        _host.KeepViewForReload();
        FormatChanged?.Invoke(this, EventArgs.Empty);
        _host.StartLoading();
    }

    /// <summary>Saves the file when its tab closes: false when the tab has to stay (the user cancelled, or the save failed and said why).</summary>
    public bool TrySave()
    {
        if (!IsLoaded && !IsReloading)
        {
            // The file never arrived: there is nothing of the user's to save.
            return true;
        }
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
            _host.ReportError(exception);
            return false;
        }
    }

    /// <summary>Saves the file and returns when it is written (used when a tab or the window closes).</summary>
    public void Save()
    {
        if (_loader.IsReadOnlyMode)
        {
            return;
        }
        if (IsReloading)
        {
            // The text on screen may be a preview or an older copy of the file: saving it would overwrite the file on the server.
            throw new InvalidOperationException("The file " + _remotePath + " is being reloaded. Try again when it has been loaded.");
        }
        if (!IsLoaded)
        {
            return;
        }
        // A background save of an older version must not overwrite this one afterwards. If it does not end, the save of the closing
        // tab is not started over it: the error is shown, the tab stays open, and closing it again either finds the file saved by then
        // or lets the user give the changes up.
        ManualResetEvent backgroundSave = _backgroundUploadDone;
        if (backgroundSave != null && !backgroundSave.WaitOne(BackgroundSaveWait))
        {
            throw new IOException("The save of " + _remotePath + " that was started earlier has not ended after " + (int)BackgroundSaveWait.TotalSeconds
                + " seconds: the connection may not answer. Close the tab again when it has (the changes are kept in the editor until then), or choose No to give them up.");
        }
        RemoteSaveJob job = _host.PrepareSave();
        // Saving on close does not run the check: the user is leaving and cannot act on what it finds.
        job.Validator = null;
        try
        {
            if (!job.SkipConflictCheck && _server.HasChanged(_remoteSignature, _loader.IsNewFile)
                && _host.Ask(_server.ConflictDescription(_loader.IsNewFile) + " Overwrite it with your version?", PromptButtons.YesNo, PromptIcon.Warning) != PromptAnswer.Yes)
            {
                throw new OperationCanceledException();
            }
            if (!_saver.UploadAndReplace(job))
            {
                SaveWithSudo(job);
            }
            CompleteSave(job);
        }
        finally
        {
            _saver.RemoveTemporaryFile(job);
        }
    }

    /// <summary>
    /// Saves the file without blocking the UI on a slow connection (Ctrl+S). Errors and the sudo prompt are
    /// shown when they happen; a save requested meanwhile runs once this one has finished.
    /// </summary>
    public void SaveInBackground()
    {
        if (!CanSave)
        {
            // While the file is loading (or a preview is shown) the text is empty or partial: saving it would overwrite the
            // file on the server with it.
            return;
        }
        if (IsSaving)
        {
            _saveRequestedWhileSaving = true;
            return;
        }
        RemoteSaveJob job = _host.PrepareSave();
        ManualResetEvent uploadDone = new ManualResetEvent(initialState: false);
        _backgroundUploadDone = uploadDone;
        SetSaving(true);
        _host.StartThread(() =>
        {
            bool replaced = false;
            bool conflict = false;
            Exception error = null;
            try
            {
                conflict = !job.SkipConflictCheck && _server.HasChanged(_remoteSignature, _loader.IsNewFile);
                if (!conflict)
                {
                    replaced = _saver.UploadAndReplace(job);
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
            if (!_host.Post(() => FinishBackgroundSave(job, replaced, conflict, error, uploadDone)))
            {
                // The editor was closed meanwhile.
                _saver.RemoveTemporaryFile(job);
            }
        }, "Saving " + _remotePath);
    }

    private void FinishBackgroundSave(RemoteSaveJob job, bool replaced, bool conflict, Exception error, ManualResetEvent uploadDone)
    {
        ServiceReload reloadToOffer = null;
        try
        {
            // Superseded by a synchronous save of a newer version: only clean up.
            if (job.Generation == _session.SaveGeneration)
            {
                if (error != null)
                {
                    _host.ReportError(error);
                }
                else if (conflict)
                {
                    switch (_host.Ask(_server.ConflictDescription(_loader.IsNewFile) + "\n\nYes: overwrite it with your version.\nNo: load the version from the server (your changes are lost).\nCancel: do not save.", PromptButtons.YesNoCancel, PromptIcon.Warning))
                    {
                        case PromptAnswer.Yes:
                            _session.ForceOverwrite = true;
                            _saveRequestedWhileSaving = true;
                            break;
                        case PromptAnswer.No:
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
                    if (job.Generation == _session.SaveGeneration)
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
            _host.ReportError(exception);
        }
        finally
        {
            _host.QueueWork(() => _saver.RemoveTemporaryFile(job));
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
                _session.ForceOverwrite = false;
                _session.SkipValidation = false;
                if (_loader.IsNewFile)
                {
                    // Somebody created the file this new one was going to be: there is a version to load now.
                    _loader.IsNewFile = false;
                }
                Reload();
            }
            else if (_saveRequestedWhileSaving)
            {
                _saveRequestedWhileSaving = false;
                if (_content.IsModified || _session.ForceOverwrite || _session.SkipValidation)
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
                    _host.ReportError(exception);
                }
            }
        }
    }

    /// <summary>
    /// The check found problems in the text, so the file on the server was not replaced. Says what was found and
    /// either saves anyway (the next save skips the check) or puts the caret on the first problem (UI thread).
    /// </summary>
    private void HandleValidationFailure(RemoteSaveJob job)
    {
        List<ValidationIssue> issues = job.Validation.Issues;
        bool showInText = _host.ShowIssuesInText(issues);
        string text = "The check (" + job.Validator.Name + ") found " + issues.Count + (issues.Count == 1 ? " problem" : " problems")
            + " in " + _remotePath + (showInText ? ", they are shown in the text" : "") + ".\n\nThe file on the server has not been changed.\n\nYes: save it anyway.\nNo: do not save, go to line " + issues[0].Line + ".";
        if (_host.Ask(text, PromptButtons.YesNo, PromptIcon.Warning) == PromptAnswer.Yes)
        {
            _session.SkipValidation = true;
            _saveRequestedWhileSaving = true;
            return;
        }
        _host.GoToIssue(issues[0]);
    }

    /// <summary>What to offer after a successful save so that the service reads the file again, or null.</summary>
    private ServiceReload ReloadToOffer()
    {
        if (_server.IsWindows || _host.ServiceReloadOfferDisabled)
        {
            return null;
        }
        return ConfigValidators.FindReload(_remotePath);
    }

    /// <summary>Asks whether to reload the service that reads the file just saved, and does so (UI thread).</summary>
    private void OfferReload(ServiceReload reload)
    {
        if (_host.Ask(_remotePath + " was saved.\n\nReload " + reload.Name + " now so that the change takes effect?", PromptButtons.YesNo, PromptIcon.Question) != PromptAnswer.Yes)
        {
            return;
        }
        _host.QueueWork(() =>
        {
            _saver.RunServiceReload(reload, out List<string> output, out int? exitStatus);
            // False when the editor was closed meanwhile.
            _host.Post(() => FinishReload(reload, output, exitStatus));
        });
    }

    private void FinishReload(ServiceReload reload, List<string> output, int? exitStatus)
    {
        try
        {
            if (exitStatus == 0)
            {
                _host.Inform(reload.Name + " reloaded.");
                return;
            }
            // A lost connection is not a refused reload: do not offer sudo for it.
            _host.ThrowIfConnectionLost();
            StringBuilder details = new StringBuilder();
            if (output != null)
            {
                for (int i = 0; i < output.Count && i < 8; i++)
                {
                    details.Append(output[i]).Append('\n');
                }
            }
            if (_host.Ask("Could not reload " + reload.Name + (exitStatus.HasValue ? " (exit code " + exitStatus.Value + ")" : "") + ":\n\n" + details + "\nTry again with sudo?", PromptButtons.YesNo, PromptIcon.Warning) != PromptAnswer.Yes)
            {
                return;
            }
            _saver.RunSudoCommand(reload.Command);
            _host.Inform(reload.Name + " reloaded.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _host.ReportError(exception);
        }
    }

    private void SetSaving(bool saving)
    {
        IsSaving = saving;
        SavingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks whether to retry with sudo and does so (UI thread).</summary>
    private void SaveWithSudo(RemoteSaveJob job)
    {
        if (!_loader.SudoOnSave && _host.Ask("Failed to " + (_loader.IsNewFile ? "create " : "overwrite ") + _remotePath + ". Try again with sudo?", PromptButtons.YesNo, PromptIcon.Question) != PromptAnswer.Yes)
        {
            throw new OperationCanceledException();
        }
        _saver.ReplaceWithSudo(job);
    }

    private void CompleteSave(RemoteSaveJob job)
    {
        // A save through sudo has no signature yet.
        _remoteSignature = job.NewSignature ?? _server.FetchSignature();
        if (job.IncludesBackup)
        {
            _session.BackupDone = true;
        }
        _loader.IsNewFile = false;
        // The bytes of the file and the saved state of the view they came from (and of the other one, when it holds them too).
        _content.CompleteSave(job);
        if (!_content.IsModified)
        {
            // Everything is on the server: the draft is of no use (a later change makes a new one).
            _host.DiscardDraft();
        }
    }
}
