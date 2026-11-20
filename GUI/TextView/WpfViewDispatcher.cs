using SmarTTY.CodeEditing;
using SmarTTY.Controls;
using System;
using System.Windows.Threading;
using MessageBoxButtons = System.Windows.Forms.MessageBoxButtons;
using MessageBoxIcon = System.Windows.Forms.MessageBoxIcon;
using DialogResult = System.Windows.Forms.DialogResult;

namespace SmarTTY.GUI.TextView;

/// <summary>
/// The thread of the WPF text view as a remote file editor uses it (stage 3E-4): what <c>Controls/ControlUiDispatcher</c> and
/// <c>ControlUserPrompt</c> are for the WinForms view. An action is posted to the dispatcher of the view, a question from a worker thread is asked
/// there with the themed message box; once the view is closed nothing is posted and every question is answered "cancel". A WPF element has its
/// dispatcher from the start, so there is no window handle to wait for.
/// </summary>
internal sealed class WpfViewDispatcher : IUiDispatcher, IUserPrompt
{
    private readonly WpfTextEditor _view;

    public WpfViewDispatcher(WpfTextEditor view)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
    }

    /// <summary>Runs <paramref name="action"/> later on the thread of the view; false when the view is closed.</summary>
    public bool Post(Action action)
    {
        if (_view.IsClosed)
        {
            return false;
        }
        try
        {
            _view.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
            {
                if (!_view.IsClosed)
                {
                    action();
                }
            }));
            return true;
        }
        catch (InvalidOperationException)
        {
            // The dispatcher has shut down (the program is ending).
            return false;
        }
    }

    /// <summary>Shows a question from a worker thread; the answer of a view that was closed (before or while it is asked) is "cancel".</summary>
    public PromptAnswer Ask(string text, PromptButtons buttons)
    {
        if (_view.IsClosed)
        {
            return PromptAnswer.Cancel;
        }
        MessageBoxButtons boxButtons = buttons == PromptButtons.OkCancel ? MessageBoxButtons.OKCancel
            : (buttons == PromptButtons.YesNo ? MessageBoxButtons.YesNo : MessageBoxButtons.YesNoCancel);
        try
        {
            DialogResult result = _view.Dispatcher.Invoke(() => _view.IsClosed
                ? DialogResult.Cancel
                : ThemedMessageBox.Show(text, "SmarTTY", boxButtons, MessageBoxIcon.Warning));
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
            return PromptAnswer.Cancel;
        }
        catch (System.Threading.Tasks.TaskCanceledException)
        {
            return PromptAnswer.Cancel;
        }
    }
}
