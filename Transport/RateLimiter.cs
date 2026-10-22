using System;
using System.Diagnostics;
using System.Threading;

namespace SmarTTY.Transport;

/// <summary>
/// A token bucket shared by all running transfers, so that the limit applies to their sum.
/// </summary>
public sealed class RateLimiter
{
    private readonly object _sync = new object();

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _bytesPerSecond;

    private double _tokens;

    private double _lastRefillSeconds;

    /// <summary>
    /// The limit in bytes per second; 0 means unlimited.
    /// </summary>
    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set
        {
            lock (_sync)
            {
                _bytesPerSecond = Math.Max(0L, value);
                _tokens = 0;
                _lastRefillSeconds = _clock.Elapsed.TotalSeconds;
            }
        }
    }

    /// <summary>
    /// The chunk size to read at a time, so that a low limit still produces a steady flow.
    /// </summary>
    public int ChunkSize(int defaultSize)
    {
        long limit = BytesPerSecond;
        if (limit <= 0)
        {
            return defaultSize;
        }
        return (int)Math.Max(4096L, Math.Min(defaultSize, limit / 8));
    }

    /// <summary>
    /// Waits until <paramref name="bytes"/> may be sent. <paramref name="checkCancelled"/> runs while waiting and
    /// stops the wait by throwing.
    /// </summary>
    public void Consume(int bytes, Action checkCancelled)
    {
        while (true)
        {
            int waitMs;
            lock (_sync)
            {
                if (_bytesPerSecond <= 0)
                {
                    return;
                }
                double now = _clock.Elapsed.TotalSeconds;
                // At most a quarter of a second can be saved up, so that an idle period does not allow a burst.
                _tokens = Math.Min(_tokens + (now - _lastRefillSeconds) * _bytesPerSecond, Math.Max(bytes, _bytesPerSecond / 4.0));
                _lastRefillSeconds = now;
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }
                waitMs = (int)Math.Ceiling((bytes - _tokens) * 1000.0 / _bytesPerSecond);
            }
            Thread.Sleep(Math.Max(1, Math.Min(waitMs, 100)));
            checkCancelled?.Invoke();
        }
    }
}
