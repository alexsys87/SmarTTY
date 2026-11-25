using System;
using System.Windows.Threading;
using SmarTTY.Utilities;

namespace SmarTTY.GUI;

/// <summary>The thread of a WPF dispatcher as an <see cref="IUiThread"/>.</summary>
public sealed class DispatcherUiThread : IUiThread
{
    private readonly Dispatcher _dispatcher;

    /// <summary>The thread of <paramref name="dispatcher"/>.</summary>
    public DispatcherUiThread(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>The thread of the dispatcher of the calling thread (made for it when it has none).</summary>
    public static DispatcherUiThread Current => new DispatcherUiThread(Dispatcher.CurrentDispatcher);

    /// <inheritdoc/>
    public bool IsCurrent => _dispatcher.Thread == System.Threading.Thread.CurrentThread;

    /// <inheritdoc/>
    public void Post(Action action)
    {
        // Normal priority is what BeginInvoke(Delegate, object[]) used.
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
    }

    /// <inheritdoc/>
    public void Send(Action action)
    {
        _dispatcher.Invoke(action);
    }
}
