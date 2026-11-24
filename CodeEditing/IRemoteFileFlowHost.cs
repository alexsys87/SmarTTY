using System;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

/// <summary>The icon of a question of <see cref="RemoteFileFlow"/>.</summary>
public enum PromptIcon
{
    Question,
    Warning
}

/// <summary>
/// The remote file editor as <see cref="RemoteFileFlow"/> sees it: its questions, its threads and the parts of its view that a save or a reload
/// changes. Made of the calls the loading and the saves made on the editor (<c>RemoteFileEditor</c>, which implements it explicitly).
/// </summary>
public interface IRemoteFileFlowHost
{
    /// <summary>Takes the bytes to save (UI thread): asks the questions about the text and makes the job.</summary>
    RemoteSaveJob PrepareSave();

    /// <summary>Asks a question (UI thread) and returns the answer.</summary>
    PromptAnswer Ask(string text, PromptButtons buttons, PromptIcon icon);

    /// <summary>Tells something that went well (UI thread).</summary>
    void Inform(string text);

    /// <summary>Shows an error (UI thread).</summary>
    void ReportError(Exception exception);

    /// <summary>
    /// Shows the problems the check of the file found as messages in the rows of the text, when the text view has such rows; true when it does.
    /// </summary>
    bool ShowIssuesInText(List<ValidationIssue> issues);

    /// <summary>Puts the caret on the place of <paramref name="issue"/> and the focus on the view.</summary>
    void GoToIssue(ValidationIssue issue);

    /// <summary>The user switched off the offer to reload a service after a save.</summary>
    bool ServiceReloadOfferDisabled { get; }

    /// <summary>Throws when the connection to the server has been lost.</summary>
    void ThrowIfConnectionLost();

    /// <summary>Runs <paramref name="work"/> on a background thread of its own named <paramref name="name"/>.</summary>
    void StartThread(Action work, string name);

    /// <summary>Runs <paramref name="work"/> on a thread of the pool.</summary>
    void QueueWork(Action work);

    /// <summary>Queues <paramref name="action"/> for the UI thread; false when the editor was closed.</summary>
    bool Post(Action action);

    /// <summary>Removes the messages shown in the rows of the text.</summary>
    void ClearMessages();

    /// <summary>
    /// A reload starts: what the view puts back when the text is in (the caret, the offset of the hex view, the folds) is kept, and neither view
    /// can be edited until then.
    /// </summary>
    void KeepViewForReload();

    /// <summary>Starts the loading thread.</summary>
    void StartLoading();

    /// <summary>Lets the text be edited, or not.</summary>
    void SetTextReadOnly(bool readOnly);

    /// <summary>The file could not be loaded: the tab closes (the editor's <c>LoadFailed</c> event).</summary>
    void RaiseLoadFailed();

    /// <summary>Removes the draft of the unsaved changes.</summary>
    void DiscardDraft();
}
