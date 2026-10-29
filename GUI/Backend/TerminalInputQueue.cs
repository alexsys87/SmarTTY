using System;
using System.Collections.Generic;
using System.Threading;

namespace SmarTTY.GUI.Backend;

/// <summary>
/// Keeps what is written into one terminal channel in order. A command that the program types into a terminal (running a
/// script, installing a package) is written on a thread of the pool, because writing may wait for the server (the window of the
/// channel is full, the connection is slow) and must not block the UI thread. Key presses are written on the UI thread, where
/// they are not delayed. The two must not overtake each other: a key pressed after a command was given has to arrive after it,
/// and two commands have to arrive in the order they were given.
///
/// Everything is handed in from the UI thread: <see cref="Post"/> sends a write to the pool, and while <see cref="IsBusy"/> a key
/// press is posted behind it as well, instead of being written at once.
/// </summary>
internal sealed class TerminalInputQueue
{
    private readonly object _sync = new object();
    private readonly Queue<Action> _pending = new Queue<Action>();
    private bool _draining;

    /// <summary>True while a posted write has not been done yet.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_sync)
            {
                return _draining;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="write"/> on a thread of the pool, after everything that was posted before it. The write has to
    /// report its own errors: there is nobody to throw them to.
    /// </summary>
    public void Post(Action write)
    {
        if (write == null)
        {
            throw new ArgumentNullException(nameof(write));
        }
        lock (_sync)
        {
            _pending.Enqueue(write);
            if (_draining)
            {
                return;
            }
            _draining = true;
        }
        ThreadPool.QueueUserWorkItem(delegate { Drain(); });
    }

    private void Drain()
    {
        while (true)
        {
            Action write;
            lock (_sync)
            {
                if (_pending.Count == 0)
                {
                    _draining = false;
                    return;
                }
                write = _pending.Dequeue();
            }
            try
            {
                write();
            }
            catch (Exception)
            {
                // A write reports its own errors. One that does not must still neither stop the writes behind it nor end the program.
            }
        }
    }
}
