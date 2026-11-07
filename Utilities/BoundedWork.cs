using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SmarTTY.Utilities;

/// <summary>
/// Work on the network that must not hold up a window: it runs on a pool thread and is waited for only so long. A device that was
/// switched off without closing the TCP connection answers nothing, and a call that waits for it on the UI thread freezes the window
/// until the network gives up, for every such connection in turn.
/// </summary>
public static class BoundedWork
{
    /// <summary>
    /// Runs <paramref name="work"/> on a pool thread and waits at most <paramref name="limit"/> for it. True when it finished in time
    /// (also when it threw: a caller that closes something down has no use for the error), false when it was given up: it goes on in the
    /// background, and what it waits for (a connection that is closed after this returns) ends it.
    /// </summary>
    public static bool Run(Action work, TimeSpan limit)
    {
        Task task = Task.Run(work);
        // An error of work that is given up and fails later is looked at here, so that nobody has to find it unobserved.
        task.ContinueWith(finished => { Exception ignored = finished.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        try
        {
            return task.Wait(limit);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    /// <summary>
    /// Waits for all the tasks together, at most <paramref name="limit"/> (not that long for each one): the time of connections that went
    /// away is not added up. True when all of them finished in time; a task that failed counts as finished.
    /// </summary>
    public static bool WaitAll(IEnumerable<Task> tasks, TimeSpan limit)
    {
        Task[] all = (tasks ?? Enumerable.Empty<Task>()).Where(task => task != null).ToArray();
        if (all.Length == 0)
        {
            return true;
        }
        try
        {
            return Task.WaitAll(all, limit);
        }
        catch (AggregateException)
        {
            return true;
        }
    }
}
