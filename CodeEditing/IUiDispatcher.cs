using System;

namespace SmarTTY.CodeEditing;

/// <summary>
/// Runs an action on the UI thread from a worker thread (the loading thread of the remote file editor). The view's implementation waits for
/// the window handle and drops the action when the tab has been closed (<c>Controls/ControlUiDispatcher.cs</c>).
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// Queues <paramref name="action"/> for the UI thread. False when it was dropped because the tab has been closed (or the window never
    /// came): nothing is waiting for it any more.
    /// </summary>
    bool Post(Action action);
}
