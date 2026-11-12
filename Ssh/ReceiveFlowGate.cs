using System;
using System.Threading;

namespace SmarTTY.Ssh;

/// <summary>
/// Counts the bytes that have arrived from the server but are not yet processed by their consumer, and holds the
/// receiving thread back while there are too many of them. Without it a host that writes faster than the terminal
/// can draw (yes, cat of a huge file, a noisy dmesg -w) makes the queues between the network and the screen grow
/// without bound: SSH.NET gives the server a new window as soon as a packet has been read, whether or not anyone
/// has dealt with its data. Holding the receiving thread back stops the reading from the socket, and the TCP
/// window then slows the server down.
/// </summary>
internal sealed class ReceiveFlowGate
{
    private readonly object _lock = new object();

    private readonly long _limit;

    private readonly long _resume;

    private long _pending;

    private bool _blocked;

    private bool _open;

    /// <param name="limit">The receiving thread waits while more than this many bytes are unprocessed.</param>
    public ReceiveFlowGate(long limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        _limit = limit;
        // Once held back, the reader waits until the consumer is half-way through the backlog, so that it is not
        // woken (and put to sleep again) for every chunk that is processed.
        _resume = limit / 2;
    }

    /// <summary>The bytes that have arrived and are not yet reported as processed.</summary>
    public long Pending
    {
        get
        {
            lock (_lock)
            {
                return _pending;
            }
        }
    }

    /// <summary>A chunk of <paramref name="count"/> bytes is handed to the consumer.</summary>
    public void Add(long count)
    {
        lock (_lock)
        {
            _pending += count;
        }
    }

    /// <summary>The consumer is done with <paramref name="count"/> bytes (drawn, or dropped as no longer needed).</summary>
    public void Release(long count)
    {
        lock (_lock)
        {
            _pending = Math.Max(0, _pending - count);
            if (_blocked && _pending <= _resume)
            {
                _blocked = false;
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>
    /// Stops limiting for good and wakes the waiting thread: the channel has ended or its consumer is gone, so
    /// nobody is left to release anything.
    /// </summary>
    public void Open()
    {
        lock (_lock)
        {
            _open = true;
            _blocked = false;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Called by the receiving thread after it has handed a chunk over: waits while the consumer is too far behind.
    /// Returns false when <paramref name="maxWait"/> ran out first; the caller then carries on (and the next call
    /// waits again), because the consumer can be waiting for the receiving thread itself (closing a channel, for
    /// instance), and a wait without an end would be a deadlock.
    /// </summary>
    public bool WaitForRoom(TimeSpan maxWait)
    {
        lock (_lock)
        {
            if (_open)
            {
                return true;
            }
            if (!_blocked)
            {
                if (_pending <= _limit)
                {
                    return true;
                }
                _blocked = true;
            }
            DateTime deadline = DateTime.UtcNow + maxWait;
            while (_blocked && !_open)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }
                Monitor.Wait(_lock, remaining);
            }
            return true;
        }
    }
}
