using SmarTTY.CodeEditing;
using System;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary><see cref="IUserPrompt"/> of a WinForms control: the themed message box, shown on the UI thread of the control.</summary>
internal sealed class ControlUserPrompt : IUserPrompt
{
    private readonly Control _control;

    public ControlUserPrompt(Control control)
    {
        _control = control;
    }

    /// <summary>
    /// Shows a question from a worker thread; the answer of an editor that was closed (before or while the question is asked) is "cancel".
    /// The first question can come before the tab has its window handle (a file that cannot be written, a large file: two to four calls to the
    /// server after the thread was started), so the handle is waited for like in <see cref="ControlUiDispatcher.Post"/>: Invoke would throw,
    /// the loading would show the user that technical message and the tab would close without the question.
    /// </summary>
    public PromptAnswer Ask(string text, PromptButtons buttons)
    {
        if (!ControlUiDispatcher.WaitForHandle(_control))
        {
            return PromptAnswer.Cancel;
        }
        MessageBoxButtons boxButtons = buttons == PromptButtons.OkCancel ? MessageBoxButtons.OKCancel
            : (buttons == PromptButtons.YesNo ? MessageBoxButtons.YesNo : MessageBoxButtons.YesNoCancel);
        try
        {
            DialogResult result = (DialogResult)_control.Invoke((Func<DialogResult>)(() => ThemedMessageBox.Show(_control, text, "SmarTTY", boxButtons, MessageBoxIcon.Warning)));
            switch (result)
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
        catch (InvalidOperationException)
        {
            // The tab was closed between the wait and the call, or while the question was open (ObjectDisposedException is one of these too).
            return PromptAnswer.Cancel;
        }
    }
}
