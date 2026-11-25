using System;

namespace SmarTTY.Utilities;

/// <summary>
/// The thread of the windows as the code that needs no window sees it: the connection classes hand their events and their questions to it
/// (the WPF <c>Dispatcher</c> in the application, <c>GUI/DispatcherUiThread</c>; a thread of the test in the stands).
/// </summary>
public interface IUiThread
{
    /// <summary>True when the caller is on this thread (a call to <see cref="Send"/> would then run at once).</summary>
    bool IsCurrent { get; }

    /// <summary>Queues <paramref name="action"/> to run on this thread and returns at once.</summary>
    void Post(Action action);

    /// <summary>Runs <paramref name="action"/> on this thread and waits for it; what it throws is thrown to the caller.</summary>
    void Send(Action action);
}
