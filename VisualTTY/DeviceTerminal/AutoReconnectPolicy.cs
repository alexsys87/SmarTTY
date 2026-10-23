using System;

namespace VisualTTY.DeviceTerminal;

/// <summary>
/// Decides how long an automatic reconnect waits after the connection ended. A connection that lived at least
/// <see cref="StableConnectionMs"/> counts as a real one and starts everything over; a connection that failed to open,
/// or that was accepted and closed again at once (a session limit, a device that is rebooting), is a failed attempt.
/// The first failed attempt is retried without extra delay, every further one waits twice as long as the one before
/// (1, 2, 4, 8 s, then <see cref="MaxBackoffMs"/>), so a server that closes every connection is not hammered in a loop.
/// There is deliberately no attempt limit: a device may be away for minutes and must be picked up when it returns.
/// </summary>
public sealed class AutoReconnectPolicy
{
    /// <summary>A connection that stayed open this long is treated as a working one.</summary>
    public const int StableConnectionMs = 5000;

    public const int FirstBackoffMs = 1000;

    public const int MaxBackoffMs = 10000;

    private long _connectedAtMs = -1;

    /// <summary>The number of attempts in a row that did not give a stable connection.</summary>
    public int FailedAttempts { get; private set; }

    /// <summary>The connection was established at <paramref name="nowMs"/> (any monotonic millisecond clock).</summary>
    public void OnConnected(long nowMs)
    {
        _connectedAtMs = nowMs;
    }

    /// <summary>The connection ended or could not be opened (<paramref name="nowMs"/> is on the same clock).</summary>
    public void OnEnded(long nowMs)
    {
        if (_connectedAtMs >= 0 && nowMs - _connectedAtMs >= StableConnectionMs)
        {
            FailedAttempts = 0;
        }
        else if (FailedAttempts < int.MaxValue)
        {
            FailedAttempts++;
        }
        _connectedAtMs = -1;
    }

    /// <summary>The user connected or disconnected by hand: the history of automatic attempts no longer applies.</summary>
    public void Reset()
    {
        FailedAttempts = 0;
        _connectedAtMs = -1;
    }

    /// <summary>The extra wait before the next attempt, caused only by earlier failed attempts.</summary>
    public int BackoffMs
    {
        get
        {
            if (FailedAttempts <= 1)
            {
                return 0;
            }
            int shift = Math.Min(FailedAttempts - 2, 4);
            return Math.Min(MaxBackoffMs, FirstBackoffMs << shift);
        }
    }

    /// <summary>
    /// The wait before the next automatic attempt: 0 means "right now". <paramref name="immediate"/> is the
    /// "reconnect immediately" setting, <paramref name="intervalMs"/> the configured interval otherwise; the backoff
    /// can only lengthen it.
    /// </summary>
    public int NextDelayMs(bool immediate, int intervalMs)
    {
        int baseMs = immediate ? 0 : Math.Max(0, intervalMs);
        return Math.Max(baseMs, BackoffMs);
    }
}
