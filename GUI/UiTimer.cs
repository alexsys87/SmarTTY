using System;
using System.Windows.Threading;

namespace SmarTTY.GUI;

/// <summary>
/// A timer that ticks on the thread of the window that made it (a <see cref="DispatcherTimer"/>), with the members the WinForms
/// <c>Timer</c> had where the WPF windows used it (stage 5B): <see cref="Interval"/> in milliseconds, <see cref="Tick"/>,
/// <see cref="Start"/>, <see cref="Stop"/>, <see cref="Enabled"/>. Starting a running timer does nothing, and a new interval of a
/// running timer starts the interval again, as with the WinForms timer.
/// </summary>
internal sealed class UiTimer : IDisposable
{
    private readonly DispatcherTimer _timer = new DispatcherTimer(DispatcherPriority.Normal);

    private int _interval = 100;

    public UiTimer()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(_interval);
        _timer.Tick += (sender, e) => Tick?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The time between two ticks, in milliseconds (100 by default, as the WinForms timer).</summary>
    public int Interval
    {
        get => _interval;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _interval = value;
            _timer.Interval = TimeSpan.FromMilliseconds(value);
        }
    }

    /// <summary>The interval has passed (on the thread of the window).</summary>
    public event EventHandler Tick;

    /// <summary>The timer is running.</summary>
    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (value)
            {
                Start();
            }
            else
            {
                Stop();
            }
        }
    }

    public void Start()
    {
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
