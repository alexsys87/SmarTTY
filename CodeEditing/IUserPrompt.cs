namespace SmarTTY.CodeEditing;

/// <summary>The buttons of a question to the user.</summary>
public enum PromptButtons
{
    OkCancel,
    YesNo,
    YesNoCancel
}

/// <summary>The answer to a question; a closed window, and every button that the question did not offer, is <see cref="Cancel"/>.</summary>
public enum PromptAnswer
{
    Ok,
    Yes,
    No,
    Cancel
}

/// <summary>
/// A question that a worker thread asks the user (the loading thread of the remote file editor: no permission to write, a large file). The
/// view shows it on the UI thread and waits (<c>Controls/ControlUserPrompt.cs</c>).
/// </summary>
public interface IUserPrompt
{
    /// <summary>
    /// Shows <paramref name="text"/> and returns the answer. The answer of an editor that was closed (before or while the question is asked)
    /// is <see cref="PromptAnswer.Cancel"/>.
    /// </summary>
    PromptAnswer Ask(string text, PromptButtons buttons);
}
