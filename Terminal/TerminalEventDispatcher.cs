using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;

namespace SmarTTY.Terminal;

public partial class TerminalEventDispatcher : Component
{
    private Thread _dispatchThread;

    private Thread _beepThread;

    private bool _stopping;

    private Semaphore _signal = new Semaphore(0, int.MaxValue);

    private Queue<ThreadStart> _inputQueue = new Queue<ThreadStart>();

    private ThreadStart _pendingResize;

    private AutoResetEvent _beepSignal = new AutoResetEvent(initialState: false);

    private ThreadStart _pendingBeep;

    public TerminalEventDispatcher()
        : this(null)
    {
    }

    private void OnDisposed(object sender, EventArgs e2)
    {
        _stopping = true;
        _signal.Release();
        _beepSignal.Set();
        this._dispatchThread.Join();
    }

    public TerminalEventDispatcher(IContainer container)
    {
        container?.Add(this);
        InitializeComponent();
        _dispatchThread = new Thread(DispatchLoop);
        Disposed += OnDisposed;
        _dispatchThread.Start();
        _beepThread = new Thread(BeepLoop);
        _beepThread.Start();
    }

    internal void PostResize(ThreadStart callback)
    {
        lock (_inputQueue)
        {
            _pendingResize = callback;
        }
        _signal.Release();
    }

    internal void PostBeep(ThreadStart callback)
    {
        this._pendingBeep = callback;
        _beepSignal.Set();
    }

    internal void PostInput(ThreadStart callback)
    {
        lock (_inputQueue)
        {
            _inputQueue.Enqueue(callback);
        }
        _signal.Release();
    }

    private void DispatchLoop()
    {
        while (true)
        {
            _signal.WaitOne();
            if (_stopping)
            {
                break;
            }
            try
            {
                ThreadStart threadStart = null;
                lock (_inputQueue)
                {
                    if (_inputQueue.Count > 0)
                    {
                        threadStart = _inputQueue.Dequeue();
                    }
                }
                threadStart?.Invoke();
            }
            catch
            {
            }
            try
            {
                ThreadStart threadStart2;
                lock (_inputQueue)
                {
                    threadStart2 = _pendingResize;
                    _pendingResize = null;
                }
                threadStart2?.Invoke();
            }
            catch
            {
            }
        }
    }

    private void BeepLoop()
    {
        while (true)
        {
            _beepSignal.WaitOne();
            if (_stopping)
            {
                break;
            }
            try
            {
                ThreadStart threadStart = _pendingBeep;
                _pendingBeep = null;
                threadStart?.Invoke();
            }
            catch
            {
            }
        }
    }
}
