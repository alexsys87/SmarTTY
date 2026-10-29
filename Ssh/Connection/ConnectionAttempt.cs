using System;
using System.Threading;

namespace SmarTTY.Ssh;

/// <summary>
/// One run of <see cref="SshConnector"/>'s connect worker. Cancelling it never stops the thread or waits for it: the cancel
/// event ends a connection that is still being made, and closing the session that the worker is using ends a login or a
/// command it is waiting for (they fail with an exception that the worker reports as a cancelled attempt). The cancelling
/// thread is the UI thread, which a join or an abort of a worker that waits for the UI would block.
/// </summary>
internal sealed class ConnectionAttempt : IDisposable
{
    private readonly object _sync = new object();

    // The session of the worker until it is handed over (see TryReleaseSession); an IDisposable so that this class
    // does not depend on the SSH library.
    private IDisposable _session;

    public Thread Thread;

    public volatile bool Cancelled;

    // Not closed in Dispose: the worker may still be waiting on it (SshSession's constructor does) and closing a wait
    // handle under a waiting thread is undefined. It is released with the attempt.
    public readonly EventWaitHandle CancelEvent = new ManualResetEvent(initialState: false);

    /// <summary>
    /// Registers the session the worker has made, so that cancelling closes it. If the attempt was cancelled meanwhile
    /// the session is closed here and <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    public void SetSession(IDisposable session)
    {
        lock (_sync)
        {
            if (!Cancelled)
            {
                _session = session;
                return;
            }
        }
        session.Dispose();
        throw new OperationCanceledException();
    }

    /// <summary>
    /// Hands the session over to the owner of the connection: cancelling no longer closes it. False when the attempt was
    /// cancelled first; the session is closed by then and must not be used.
    /// </summary>
    public bool TryReleaseSession()
    {
        lock (_sync)
        {
            if (Cancelled)
            {
                return false;
            }
            _session = null;
            return true;
        }
    }

    public void ThrowIfCancelled()
    {
        if (Cancelled)
        {
            throw new OperationCanceledException();
        }
    }

    public void Dispose()
    {
        IDisposable session;
        lock (_sync)
        {
            Cancelled = true;
            session = _session;
            _session = null;
        }
        CancelEvent.Set();
        if (session != null)
        {
            // Closing talks to the network: not on the thread that cancels (the UI thread).
            ThreadPool.QueueUserWorkItem((object state) =>
            {
                try
                {
                    ((IDisposable)state).Dispose();
                }
                catch
                {
                }
            }, session);
        }
    }
}
