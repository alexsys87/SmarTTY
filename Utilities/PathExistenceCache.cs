using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SmarTTY.Utilities;

/// <summary>
/// Tells whether a path exists to code that must not wait for the file system. The terminal asks for every piece of text that
/// looks like a path each time it paints a line (to underline the files that are there), and a path may lie on a disk that has
/// to spin up or on a network drive that does not answer: the window would freeze while it is painted.
/// <see cref="Exists"/> answers at once with what is known: false for a path that was never checked, the last result for one that
/// was. The check itself runs in the background, a few at a time, and <c>changed</c> is called (on that background thread) when
/// an answer differs from what was reported before, so the caller can paint again. A result is kept for a short time only:
/// a file that appears or goes away shows up on a later paint.
/// </summary>
public sealed class PathExistenceCache
{
    private struct Entry
    {
        public bool Exists;

        public long CheckedAt;
    }

    private readonly object _gate = new object();

    private readonly Func<string, bool> _exists;

    private readonly Action<Action> _runInBackground;

    private readonly Action _changed;

    private readonly Func<long> _clock;

    private readonly int _maxAge;

    private readonly int _maxPending;

    private readonly int _maxWorkers;

    private readonly int _maxEntries;

    private readonly Dictionary<string, Entry> _results = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly Queue<string> _queue = new Queue<string>();

    private int _running;

    /// <param name="exists">The real check (File.Exists, Directory.Exists). It may take long or throw: it only runs in the background.</param>
    /// <param name="runInBackground">Runs the action on another thread (the thread pool).</param>
    /// <param name="changed">Called from the background when a reported answer is out of date. It must not block.</param>
    /// <param name="clockMilliseconds">A clock in milliseconds that only has to go forward; the default is a stopwatch.</param>
    /// <param name="maxAgeMilliseconds">How long a result is good before it is checked again.</param>
    /// <param name="maxPending">Paths waiting for a check: more are dropped, they are asked for again on the next paint.</param>
    /// <param name="maxWorkers">Checks that run at the same time: a drive that hangs holds one thread, not all.</param>
    /// <param name="maxEntries">Results kept: more and the cache starts again.</param>
    public PathExistenceCache(Func<string, bool> exists, Action<Action> runInBackground, Action changed, Func<long> clockMilliseconds = null, int maxAgeMilliseconds = 2000, int maxPending = 64, int maxWorkers = 2, int maxEntries = 2048)
    {
        _exists = exists ?? throw new ArgumentNullException(nameof(exists));
        _runInBackground = runInBackground ?? throw new ArgumentNullException(nameof(runInBackground));
        _changed = changed;
        _clock = clockMilliseconds ?? (() => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency));
        _maxAge = maxAgeMilliseconds;
        _maxPending = Math.Max(1, maxPending);
        _maxWorkers = Math.Max(1, maxWorkers);
        _maxEntries = Math.Max(1, maxEntries);
    }

    /// <summary>The last known answer, at once (false when the path was never checked). Starts a check when there is none or it is old.</summary>
    public bool Exists(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        bool known;
        bool needsCheck;
        bool start = false;
        lock (_gate)
        {
            known = _results.TryGetValue(path, out Entry entry);
            needsCheck = !known || _clock() - entry.CheckedAt >= _maxAge;
            if (needsCheck && !_pending.Contains(path) && _pending.Count < _maxPending)
            {
                _pending.Add(path);
                _queue.Enqueue(path);
                if (_running < _maxWorkers)
                {
                    _running++;
                    start = true;
                }
            }
            known = known && entry.Exists;
        }
        if (start)
        {
            // Outside of the lock: the runner may run the work on this very thread (the test harness does).
            StartWorker();
        }
        return known;
    }

    private void StartWorker()
    {
        try
        {
            _runInBackground(Work);
        }
        catch
        {
            // The pool did not take the work: forget what waits for it, or those paths would never be asked for again.
            lock (_gate)
            {
                _running--;
                _queue.Clear();
                _pending.Clear();
            }
        }
    }

    private void Work()
    {
        while (true)
        {
            string path;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    _running--;
                    return;
                }
                path = _queue.Dequeue();
            }
            bool exists;
            try
            {
                exists = _exists(path);
            }
            catch
            {
                exists = false;
            }
            bool changed;
            lock (_gate)
            {
                _pending.Remove(path);
                bool known = _results.TryGetValue(path, out Entry previous);
                // A path that was reported as missing because nobody knew yet and is missing is no change.
                changed = known ? previous.Exists != exists : exists;
                if (!known && _results.Count >= _maxEntries)
                {
                    _results.Clear();
                }
                _results[path] = new Entry { Exists = exists, CheckedAt = _clock() };
            }
            if (changed)
            {
                try
                {
                    _changed?.Invoke();
                }
                catch
                {
                }
            }
        }
    }
}
