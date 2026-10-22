using SmarTTY.Native;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Transport;

internal class AsyncTransportConnection : IDisposable
{
    private class PendingWrite
    {
        public byte[] Buffer;

        public int Length;

        // Told on the writer thread whether the data reached the transport; null when nobody waits for it.
        public Action<bool> Completed;
    }

    public delegate void DataReceivedHandler(object sender, byte[] buffer, int count);

    public delegate void StateChangedHandler(object sender, ConnectionStateEnum connectionStateEnum, string message);

    public delegate void WriteFailedHandler(object sender, string message);

    private readonly IDeviceTransport _transport;

    private readonly Dispatcher _dispatcher;

    private readonly int _connectTimeoutMs;

    private readonly IHostNameResolver _hostResolver;

    private bool _started;

    // Read by the worker threads while the UI thread sets them.
    private volatile bool _stopRequested;

    private volatile bool _disposed;

    // Set once the read loop has ended: nothing written after that can reach the device.
    private volatile bool _finished;

    private Thread _connectThread;

    private Thread _writerThread;

    private volatile ConnectionStateEnum _state;

    private Queue<PendingWrite> _writeQueue = new Queue<PendingWrite>();

    private Semaphore _writeSignal = new Semaphore(0, int.MaxValue);

    private volatile bool _writerIdle;

    // Received data that waits for the UI thread. The reader appends to it and posts one delivery at a time, so a fast
    // link (a raw TCP stream can bring megabytes per second) costs one dispatcher operation per UI turn instead of one
    // per read, and keyboard and mouse input are still handled in between.
    private readonly object _receiveLock = new object();

    private byte[] _received = new byte[ReadBufferSize];

    private int _receivedCount;

    private bool _deliveryPosted;

    // Runs on the UI thread once everything received has been delivered (the end of the connection is told last).
    private ThreadStart _afterDelivery;

    // Bytes per read of a network connection: large enough for a fast link to be drained in few reads.
    private const int ReadBufferSize = 64 * 1024;

    // Bytes per read of a serial port. Its read returns only when the buffer is full or the line pauses, so a large
    // buffer would hold back a continuous stream: 4 KB is about 45 ms at 921600 bps.
    private const int SerialReadBufferSize = 4096;

    // When the UI is this far behind, the reader stops reading: the far end is then slowed down by the flow control of
    // the link (TCP window, RTS/CTS) instead of the backlog growing without bound and the window freezing.
    internal const int MaxPendingReceiveBytes = 4 * 1024 * 1024;

    // The most one UI update gets, so drawing a backlog never holds the window for long; the rest follows at once.
    internal const int MaxDeliveryBytes = 256 * 1024;

    public bool IsWriterIdle => _writerIdle;

    private volatile string _writeError;

    /// <summary>The reason of the last failed write, or null when the last write went through.</summary>
    public string WriteError => _writeError;

    /// <summary>True once the connection has ended or was closed: nothing sent from now on reaches the device.</summary>
    public bool HasEnded => _finished || _stopRequested;

    public SerialPortStream SerialStream => (_transport as SerialTransport)?.SerialStream;

    public event EventHandler WriterIdleChanged;

    /// <summary>Raised on the UI thread when data could not be written to the device (the data is lost).</summary>
    public event WriteFailedHandler WriteFailed;

    public event DataReceivedHandler DataReceived;

    public event StateChangedHandler StateChanged;

    public AsyncTransportConnection(IDeviceTransport deviceTransport, Dispatcher dispatcher, int connectTimeoutMs = 0, IHostNameResolver hostNameResolver = null)
    {
        _transport = deviceTransport;
        _dispatcher = dispatcher;
        _connectTimeoutMs = connectTimeoutMs;
        _hostResolver = hostNameResolver;
    }

    private void PostToUi(ThreadStart callback)
    {
        try
        {
            _dispatcher.BeginInvoke((Delegate)callback, new object[0]);
        }
        catch
        {
        }
    }

    private void ConnectAndReadLoop()
    {
        try
        {
            _state = ConnectionStateEnum.Connecting;
            PostToUi(() =>
            {
                if (StateChanged != null && !_disposed)
                {
                    StateChanged(this, _state, _transport.ConnectingMessage);
                }
            });
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    _transport.Open(_hostResolver);
                }
                catch
                {
                    if (elapsed.ElapsedMilliseconds >= _connectTimeoutMs || _stopRequested || _disposed)
                    {
                        throw;
                    }
                    Thread.Sleep(100);
                    continue;
                }
                break;
            }
            _state = ConnectionStateEnum.Connected;
            PostToUi(() =>
            {
                if (StateChanged != null && !_disposed)
                {
                    StateChanged(this, _state, null);
                }
            });
        }
        catch (Exception exception)
        {
            Exception connectEx = exception;
            _finished = true;
            _state = ConnectionStateEnum.Failed;
            PostToUi(() =>
            {
                if (StateChanged != null && !_disposed)
                {
                    StateChanged(this, _state, connectEx.Message);
                }
            });
            return;
        }
        _writerThread = new Thread(WriterLoop)
        {
            // A driver that never returns from a call must not keep the program running after its window is closed.
            IsBackground = true,
            Name = "Device transport writer"
        };
        _writerThread.Start();
        string disconnectReason = null;
        try
        {
            Stream stream = _transport.GetStream();
            byte[] rawBuffer = new byte[_transport is SerialTransport ? SerialReadBufferSize : ReadBufferSize];
            while (!_stopRequested)
            {
                int count = stream.Read(rawBuffer, 0, rawBuffer.Length);
                if (count <= 0)
                {
                    break;
                }
                QueueReceived(rawBuffer, count);
            }
        }
        catch (Exception readException)
        {
            // Closing the connection on purpose also ends the read with an error; only an unexpected one is a reason to show.
            if (!_stopRequested && !_disposed)
            {
                disconnectReason = readException.Message;
            }
        }
        // The writer has nothing to write to any more: let it end instead of waiting for data until Dispose.
        _finished = true;
        _writeSignal.Release();
        FailQueuedWrites();
        try
        {
            _transport.Close();
        }
        catch
        {
        }
        _state = ConnectionStateEnum.Disconnected;
        // After the last received data: the UI must not see the end before the data that came before it.
        PostAfterReceived(() =>
        {
            if (StateChanged != null && !_disposed)
            {
                StateChanged(this, _state, disconnectReason);
            }
        });
    }

    /// <summary>Posts <paramref name="callback"/> to the UI thread behind all received data that is still on its way.</summary>
    private void PostAfterReceived(ThreadStart callback)
    {
        lock (_receiveLock)
        {
            if (_deliveryPosted)
            {
                _afterDelivery += callback;
                return;
            }
        }
        PostToUi(callback);
    }

    /// <summary>Adds received bytes to what waits for the UI thread and makes sure one delivery is on its way.</summary>
    private void QueueReceived(byte[] data, int count)
    {
        bool post;
        lock (_receiveLock)
        {
            while (_deliveryPosted && _receivedCount >= MaxPendingReceiveBytes && !_stopRequested && !_disposed)
            {
                // Woken by the delivery; the timeout only guards against a dispatcher that has shut down.
                Monitor.Wait(_receiveLock, 100);
            }
            if (_receivedCount + count > _received.Length)
            {
                byte[] larger = new byte[Math.Max(_received.Length * 2, _receivedCount + count)];
                Buffer.BlockCopy(_received, 0, larger, 0, _receivedCount);
                _received = larger;
            }
            Buffer.BlockCopy(data, 0, _received, _receivedCount, count);
            _receivedCount += count;
            post = !_deliveryPosted;
            _deliveryPosted = true;
        }
        if (post)
        {
            PostToUi(DeliverReceived);
        }
    }

    /// <summary>
    /// Runs on the UI thread: hands what was received so far to <see cref="DataReceived"/>, at most
    /// <see cref="MaxDeliveryBytes"/> at a time; a larger backlog is delivered by the next posted call.
    /// </summary>
    private void DeliverReceived()
    {
        byte[] buffer;
        int count;
        bool more;
        ThreadStart after = null;
        lock (_receiveLock)
        {
            count = Math.Min(_receivedCount, MaxDeliveryBytes);
            buffer = new byte[count];
            Buffer.BlockCopy(_received, 0, buffer, 0, count);
            _receivedCount -= count;
            more = _receivedCount > 0;
            if (more)
            {
                Buffer.BlockCopy(_received, count, _received, 0, _receivedCount);
            }
            else if (_received.Length > ReadBufferSize * 4)
            {
                // A burst is over: do not keep its large buffer for the rest of the session.
                _received = new byte[ReadBufferSize];
            }
            if (!more)
            {
                after = _afterDelivery;
                _afterDelivery = null;
            }
            // The next delivery is posted below (the flag stays set) or by the reader when new data comes.
            _deliveryPosted = more;
            Monitor.PulseAll(_receiveLock);
        }
        try
        {
            if (count > 0 && DataReceived != null && !_disposed)
            {
                DataReceived(this, buffer, count);
            }
        }
        finally
        {
            if (more)
            {
                PostToUi(DeliverReceived);
            }
            else
            {
                after?.Invoke();
            }
        }
    }

    private void WriterLoop()
    {
        while (!_stopRequested && !_finished)
        {
            bool queueEmpty;
            lock (_writeQueue)
            {
                queueEmpty = _writeQueue.Count == 0;
            }
            if (queueEmpty)
            {
                SetWriterIdle(true);
            }
            _writeSignal.WaitOne();
            if (_stopRequested || _finished)
            {
                break;
            }
            PendingWrite pendingWrite = null;
            lock (_writeQueue)
            {
                if (_writeQueue.Count > 0)
                {
                    pendingWrite = _writeQueue.Dequeue();
                }
            }
            if (pendingWrite == null)
            {
                continue; // its data was thrown away after a failed write
            }
            SetWriterIdle(false);
            try
            {
                Stream stream = _transport.GetStream();
                if (stream == null)
                {
                    throw new IOException("The connection is closed.");
                }
                stream.Write(pendingWrite.Buffer, 0, pendingWrite.Length);
                _writeError = null;
            }
            catch (Exception writeException)
            {
                NotifyCompleted(pendingWrite, false);
                ReportWriteFailure(writeException);
                continue;
            }
            NotifyCompleted(pendingWrite, true);
        }
    }

    /// <summary>Tells the UI when the writer starts or stops being idle; nothing is posted while the state stays the same.</summary>
    private void SetWriterIdle(bool idle)
    {
        if (_writerIdle == idle)
        {
            return;
        }
        _writerIdle = idle;
        PostToUi(() =>
        {
            if (WriterIdleChanged != null && !_disposed)
            {
                WriterIdleChanged(this, EventArgs.Empty);
            }
        });
    }

    /// <summary>
    /// A write failed: remember why, tell the UI, and throw away what is still queued. That data was typed or sent for a
    /// moment that has passed, and writing it later (after a stalled port wakes up) would send stale input to the device.
    /// </summary>
    private void ReportWriteFailure(Exception exception)
    {
        if (_stopRequested || _disposed)
        {
            FailQueuedWrites();
            return; // the connection is being closed on purpose
        }
        int discarded = FailQueuedWrites();
        string message = exception.Message;
        if (discarded > 0)
        {
            message += " " + discarded + " queued write" + (discarded == 1 ? " was" : "s were") + " discarded.";
        }
        _writeError = message;
        PostToUi(() =>
        {
            if (WriteFailed != null && !_disposed)
            {
                WriteFailed(this, message);
            }
        });
    }

    private static void NotifyCompleted(PendingWrite pendingWrite, bool written)
    {
        try
        {
            pendingWrite.Completed?.Invoke(written);
        }
        catch
        {
            // A waiter's problem must not stop the writer.
        }
    }

    /// <summary>Throws away what is still queued and tells its waiters; returns how many writes were dropped.</summary>
    private int FailQueuedWrites()
    {
        PendingWrite[] dropped;
        lock (_writeQueue)
        {
            dropped = _writeQueue.ToArray();
            _writeQueue.Clear();
        }
        foreach (PendingWrite pendingWrite in dropped)
        {
            NotifyCompleted(pendingWrite, false);
        }
        return dropped.Length;
    }

    public void Start()
    {
        if (!_started)
        {
            StateChanged?.Invoke(this, ConnectionStateEnum.Connecting, null);
            _started = true;
            _connectThread = new Thread(ConnectAndReadLoop)
            {
                IsBackground = true,
                Name = "Device transport reader"
            };
            _connectThread.Start();
        }
    }

    public void Dispose()
    {
        _stopRequested = true;
        _writeSignal.Release();
        lock (_receiveLock)
        {
            // A reader that waits for the UI to catch up must not keep waiting.
            Monitor.PulseAll(_receiveLock);
        }
        FailQueuedWrites();
        try
        {
            _transport.Close();
            _transport.Dispose();
        }
        catch
        {
        }
        _disposed = true;
    }

    /// <summary>
    /// Queues data for the device. False when the connection has already ended (or was closed): the data is dropped
    /// instead of piling up in a queue that nobody writes any more, and <paramref name="completed"/> is not called.
    /// Otherwise <paramref name="completed"/> is called once, on a worker thread, with whether the data was written.
    /// </summary>
    public bool Send(byte[] buffer, int count, Action<bool> completed = null)
    {
        if (_finished || _stopRequested)
        {
            return false;
        }
        lock (_writeQueue)
        {
            _writeQueue.Enqueue(new PendingWrite
            {
                Buffer = buffer,
                Length = count,
                Completed = completed
            });
        }
        _writeSignal.Release();
        if (_finished || _stopRequested)
        {
            // The connection ended between the check above and the enqueue: nobody would take this write any more.
            FailQueuedWrites();
        }
        return true;
    }

    /// <summary>Tells the transport the current terminal size (used by telnet); other transports ignore it.</summary>
    public void SetTerminalSize(int columns, int rows)
    {
        (_transport as ITerminalSizeAwareTransport)?.SetTerminalSize(columns, rows);
    }

    public string GetDisplayName(bool includeParameters, IHostNameResolver hostNameResolver)
    {
        string name = _transport.GetDisplayName(includeParameters);
        return hostNameResolver?.ResolveHostName(name) ?? name;
    }

    public bool MatchesPort(string portName)
    {
        if (string.IsNullOrEmpty(portName))
        {
            return true;
        }
        if (_transport is SerialTransport serialTransport)
        {
            return serialTransport.PortName == portName;
        }
        return false;
    }
}
