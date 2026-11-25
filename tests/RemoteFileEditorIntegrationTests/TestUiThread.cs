using System;
using System.Collections.Concurrent;
using System.Threading;

// The UI thread of the stand: the main thread, which runs what other threads post to it when the stand pumps (Pump), as the message loop of a
// window runs the posts of its controls. It stands for the WinForms message loop the stand had while it ran on Mono (a control that only
// marshalled, Application.DoEvents, the WinForms timer).
internal static class TestUiThread
{
    private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

    private static Thread _thread;

    /// <summary>Makes the calling thread the UI thread (the main thread of the stand, before anything is posted).</summary>
    public static void Initialize()
    {
        _thread = Thread.CurrentThread;
    }

    /// <summary>True on the UI thread.</summary>
    public static bool IsCurrent => Thread.CurrentThread == _thread;

    /// <summary>Queues <paramref name="action"/> for the UI thread; it runs the next time the stand pumps.</summary>
    public static void Post(Action action)
    {
        Queue.Enqueue(action);
    }

    /// <summary>Runs <paramref name="func"/> on the UI thread and waits for its result (at once on the UI thread itself); what it throws is thrown here.</summary>
    public static T Invoke<T>(Func<T> func)
    {
        if (IsCurrent)
        {
            return func();
        }
        T result = default(T);
        Exception error = null;
        using (ManualResetEventSlim done = new ManualResetEventSlim())
        {
            Post(() =>
            {
                try
                {
                    result = func();
                }
                catch (Exception exception)
                {
                    error = exception;
                }
                finally
                {
                    done.Set();
                }
            });
            done.Wait();
        }
        if (error != null)
        {
            throw error;
        }
        return result;
    }

    /// <summary>Runs what was posted to the UI thread until now (and what that posts in turn), as Application.DoEvents did.</summary>
    public static void Pump()
    {
        int count = Queue.Count;
        for (int i = 0; i < count && Queue.TryDequeue(out Action action); i++)
        {
            action();
        }
    }

    /// <summary>A timer whose ticks run on the UI thread when the stand pumps (the WinForms timer of a window); disposed, it stops.</summary>
    public static IDisposable StartTimer(int intervalMs, Action tick)
    {
        return new UiTimer(intervalMs, tick);
    }

    private sealed class UiTimer : IDisposable
    {
        private readonly Timer _timer;

        private int _pending;

        private volatile bool _stopped;

        public UiTimer(int intervalMs, Action tick)
        {
            _timer = new Timer(_ =>
            {
                // One tick waits at most, as a WM_TIMER message does.
                if (_stopped || Interlocked.Exchange(ref _pending, 1) == 1)
                {
                    return;
                }
                Post(() =>
                {
                    Interlocked.Exchange(ref _pending, 0);
                    if (!_stopped)
                    {
                        tick();
                    }
                });
            }, null, intervalMs, intervalMs);
        }

        public void Dispose()
        {
            _stopped = true;
            _timer.Dispose();
        }
    }
}
