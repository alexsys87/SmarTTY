using System.Collections.Generic;
using System.Globalization;

namespace VisualTTY.DeviceTerminal;

/// <summary>
/// The current speed of a byte counter that only grows: it is sampled now and then (the status bar does it a few times
/// a second) and the rate is taken over the last <see cref="WindowMs"/>, so it follows changes quickly without
/// jumping with every sample. A counter that goes down (it was reset for a new connection) starts the meter over.
/// </summary>
public sealed class TransferRateMeter
{
    public const int WindowMs = 2000;

    private readonly Queue<KeyValuePair<long, long>> _samples = new Queue<KeyValuePair<long, long>>();

    private long _lastTotal = -1;

    /// <summary>Records the counter at <paramref name="nowMs"/> (any monotonic clock) and returns bytes per second.</summary>
    public double Sample(long totalBytes, long nowMs)
    {
        if (totalBytes < _lastTotal)
        {
            _samples.Clear();
        }
        _lastTotal = totalBytes;
        _samples.Enqueue(new KeyValuePair<long, long>(nowMs, totalBytes));
        // Keep one sample at or before the start of the window, so the span covers the whole window.
        while (_samples.Count > 2)
        {
            using (Queue<KeyValuePair<long, long>>.Enumerator enumerator = _samples.GetEnumerator())
            {
                enumerator.MoveNext();
                enumerator.MoveNext();
                if (nowMs - enumerator.Current.Key < WindowMs)
                {
                    break;
                }
            }
            _samples.Dequeue();
        }
        KeyValuePair<long, long> oldest = _samples.Peek();
        long spanMs = nowMs - oldest.Key;
        if (spanMs <= 0)
        {
            return 0;
        }
        return (totalBytes - oldest.Value) * 1000.0 / spanMs;
    }

    /// <summary>A short rate for the status bar: "0 B/s", "960 B/s", "11.3 KB/s", "1.4 MB/s".</summary>
    public static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024.0)
        {
            return ((long)(bytesPerSecond + 0.5)).ToString(CultureInfo.InvariantCulture) + " B/s";
        }
        if (bytesPerSecond < 1048576.0)
        {
            return (bytesPerSecond / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s";
        }
        return (bytesPerSecond / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
    }

    public void Reset()
    {
        _samples.Clear();
        _lastTotal = -1;
    }
}
