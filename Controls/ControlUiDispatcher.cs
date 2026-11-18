using SmarTTY.CodeEditing;
using System;
using SmarTTY.Utilities;
using System.Threading;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary><see cref="IUiDispatcher"/> of a WinForms control: <c>BeginInvoke</c> on its UI thread.</summary>
internal sealed class ControlUiDispatcher : IUiDispatcher
{
    private readonly Control _control;

    public ControlUiDispatcher(Control control)
    {
        _control = control;
    }

    /// <summary>
    /// For a worker thread: waits (30 seconds at the most) until the tab has its window handle. The loading thread is started in the
    /// constructor and can be faster than the creation of the handle of a tab that has just been opened, and Invoke and BeginInvoke
    /// throw before that. False when the tab was closed meanwhile or the handle did not come.
    /// </summary>
    public static bool WaitForHandle(Control control)
    {
        return UiHandleWait.Until(() => control.IsDisposed || control.Disposing, () => control.IsHandleCreated);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread from a worker thread. When the tab has been closed meanwhile
    /// the action is dropped: BeginInvoke on a disposed control throws, and an exception that leaves a worker thread ends the program.
    /// </summary>
    public bool Post(Action action)
    {
        if (!WaitForHandle(_control))
        {
            return false;
        }
        try
        {
            _control.BeginInvoke((ThreadStart)(() =>
            {
                if (!_control.IsDisposed)
                {
                    action();
                }
            }));
            return true;
        }
        catch (InvalidOperationException)
        {
            // The handle was destroyed between the check and the call (ObjectDisposedException is one of these too): the tab is closing.
            return false;
        }
    }
}
