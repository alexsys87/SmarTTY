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

    private Queue<PendingWrite> _writeQueue = new Queue<PendingWrite>();

    // SemaphoreSlim holds no kernel object, so a connection that is replaced leaves nothing behind to be finalized.
    private readonly SemaphoreSlim _writeSignal = new SemaphoreSlim(0);

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
    // the link (TCP window, RTS/CTS) instead of the backlog growing without bound and the window freezing. A serial port
    // without RTS/CTS or XON/XOFF has no such control: the driver's queue overruns and drops data silently (the status bar
    // reports it as a line error), so only the large driver queue and a lower speed or flow control help there.
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
        WriteTimeoutMs = deviceTransport is SerialTransport ? 0 : DefaultNetworkWriteTimeoutMs;
        _dispatcher = dispatcher;
        _connectTimeoutMs = connectTimeoutMs;
        _hostResolver = hostNameResolver;
    }

    private void PostToUi(ThreadStart callback)
    {
        try
        {
            // Normal priority is what BeginInvoke(Delegate, object[]) uses as well; this overload also exists in Mono's
            // WindowsBase, so the control runs under the Mono test harnesses too.
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, callback);
        }
        catch
        {
        }
    }

    private const int ErrorInvalidParameter = 87;

    /// <summary>
    /// Whether opening again can help. A port that is still being released or a device that has just started may open a
    /// moment later; settings that are wrong, or a transport that was closed, will fail the same way every time.
    /// </summary>
    private static bool IsWorthRetrying(Exception exception)
    {
        return !(exception is ArgumentException || exception is ObjectDisposedException || exception is InvalidOperationException
            || (exception is System.ComponentModel.Win32Exception win32 && win32.NativeErrorCode == ErrorInvalidParameter));
    }

    private void ConnectAndReadLoop()
    {
        try
        {
            // Each callback carries the state it was posted for: it may have moved on by the time the UI runs it.
            PostToUi(() =>
            {
                if (StateChanged != null && !_disposed)
                {
                    StateChanged(this, ConnectionStateEnum.Connecting, _transport.ConnectingMessage);
                }
            });
            WaitForPrevious?.Wait(PreviousCloseWaitMs);
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    _transport.Open(_hostResolver);
                }
                catch (Exception openException)
                {
                    if (elapsed.ElapsedMilliseconds >= _connectTimeoutMs || _stopRequested || _disposed || !IsWorthRetrying(openException))
                    {
                        throw;
                    }
                    // Woken by Dispose, so closing the tab does not wait for the pause.
                    _stopSignal.Wait(100);
                    continue;
                }
                break;
            }
            PostToUi(() =>
            {
                if (StateChanged != null && !_disposed)
                {
                    StateChanged(this, ConnectionStateEnum.Connected, null);
                }
            });
        }
        catch (Exception exception)
        {
            Exception connectEx = exception;
            _finished = true;
            PostToUi(() =>
            {
                if (StateChanged != null && !_disposed)
                {
                    StateChanged(this, ConnectionStateEnum.Failed, connectEx.Message);
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
        StartWriteWatchdog();
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
                disconnectReason = _writeTimeoutReason ?? readException.Message;
            }
        }
        disconnectReason = disconnectReason ?? _writeTimeoutReason;
        // The writer has nothing to write to any more: let it end instead of waiting for data until Dispose.
        _finished = true;
        _writeSignal.Release();
        StopWriteWatchdog();
        // Typed or pasted data that never left must not vanish without a word.
        ReportLostWrites("The connection was closed.");
        try
        {
            _transport.Close();
        }
        catch
        {
        }
        // After the last received data: the UI must not see the end before the data that came before it.
        PostAfterReceived(() =>
        {
            if (StateChanged != null && !_disposed)
            {
                StateChanged(this, ConnectionStateEnum.Disconnected, disconnectReason);
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

    /// <summary>
    /// A network write that makes no progress for this long (the peer is alive but reads nothing, TCP window zero)
    /// ends the connection: a blocked write never fails by itself, and keep-alive does not help while the peer answers.
    /// A serial port is left alone: a stopped line by flow control is a normal state there.
    /// </summary>
    internal const int DefaultNetworkWriteTimeoutMs = 30000;

    private static readonly System.Diagnostics.Stopwatch WriteClock = System.Diagnostics.Stopwatch.StartNew();

    private readonly ManualResetEventSlim _stopSignal = new ManualResetEventSlim();

    // WriteClock time + 1 when the write in progress began; 0 while no write is in progress.
    private long _writeStartedMs;

    private volatile string _writeTimeoutReason;

    private Timer _writeWatchdog;

    /// <summary>How long one write may block, in milliseconds; 0 switches the limit off. Set before <see cref="Start"/>.</summary>
    internal int WriteTimeoutMs { get; set; }

    private void StartWriteWatchdog()
    {
        int timeoutMs = WriteTimeoutMs;
        if (timeoutMs <= 0)
        {
            return;
        }
        _writeWatchdog = new Timer(_ => CheckWriteTimeout(timeoutMs), null, Math.Max(10, Math.Min(1000, timeoutMs / 4)), Math.Max(10, Math.Min(1000, timeoutMs / 4)));
    }

    private void StopWriteWatchdog()
    {
        _writeWatchdog?.Dispose();
    }

    private void CheckWriteTimeout(int timeoutMs)
    {
        long started = Volatile.Read(ref _writeStartedMs);
        if (started == 0 || _stopRequested || _finished || _writeTimeoutReason != null)
        {
            return;
        }
        if (WriteClock.ElapsedMilliseconds + 1 - started < timeoutMs)
        {
            return;
        }
        _writeTimeoutReason = "The device did not accept data for " + (timeoutMs / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s; the connection was closed.";
        try
        {
            // Wakes up the blocked write and the read; the connection then ends like any other.
            _transport.Close();
        }
        catch
        {
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
            _writeSignal.Wait();
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
                    if (_stopRequested || _finished)
                    {
                        // Closed meanwhile, on purpose or by the end of the connection (which reports what it lost itself).
                        NotifyCompleted(pendingWrite, false);
                        continue;
                    }
                    throw new IOException("The connection is closed.");
                }
                Volatile.Write(ref _writeStartedMs, WriteClock.ElapsedMilliseconds + 1);
                stream.Write(pendingWrite.Buffer, 0, pendingWrite.Length);
                _writeError = null;
            }
            catch (Exception writeException)
            {
                NotifyCompleted(pendingWrite, false);
                ReportWriteFailure(_writeTimeoutReason != null ? new IOException(_writeTimeoutReason, writeException) : writeException);
                continue;
            }
            finally
            {
                Volatile.Write(ref _writeStartedMs, 0);
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

    /// <summary>Drops what is still queued; if there was something, tells the UI that it was lost (unless the close was on purpose).</summary>
    private void ReportLostWrites(string reason)
    {
        if (_stopRequested || _disposed)
        {
            FailQueuedWrites();
            return;
        }
        int discarded = FailQueuedWrites();
        if (discarded == 0)
        {
            return;
        }
        string message = reason + " " + discarded + " queued write" + (discarded == 1 ? " was" : "s were") + " discarded.";
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
        _stopSignal.Set();
        StopWriteWatchdog();
        _writeSignal.Release();
        lock (_receiveLock)
        {
            // A reader that waits for the UI to catch up must not keep waiting.
            Monitor.PulseAll(_receiveLock);
        }
        FailQueuedWrites();
        _disposed = true;
        if (_transport is SerialTransport)
        {
            // Closing a serial port waits for the driver to cancel its operations (seconds, for a vanished adapter): not on
            // the UI thread. The next connection to the port waits for <see cref="ClosedEvent"/> before it opens it.
            new Thread(CloseTransport)
            {
                IsBackground = true,
                Name = "Device transport closer"
            }.Start();
        }
        else
        {
            CloseTransport();
        }
    }

    private void CloseTransport()
    {
        try
        {
            _transport.Close();
            _transport.Dispose();
        }
        catch
        {
        }
        finally
        {
            _closedEvent.Set();
        }
    }

    private readonly ManualResetEventSlim _closedEvent = new ManualResetEventSlim();

    /// <summary>Set once <see cref="Dispose"/> has released the transport (the serial port can be opened again).</summary>
    internal ManualResetEventSlim ClosedEvent => _closedEvent;

    /// <summary>The connection this one replaces: its port is released before this one opens it. Set before <see cref="Start"/>.</summary>
    internal ManualResetEventSlim WaitForPrevious { get; set; }

    // The longest a connection waits for the previous one to release the port (two cancellations of 2 s each, and a margin).
    private const int PreviousCloseWaitMs = 5000;

    /// <summary>
    /// Queues data for the device. False when the connection has already ended (or was closed): the data is dropped
    /// instead of piling up in a queue that nobody writes any more, and <paramref name="completed"/> is not called.
    /// Otherwise <paramref name="completed"/> is called once with whether the data was written: on the writer thread, or
    /// on the thread that drops the data (the reader when the connection ends, the caller of Dispose).
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
                // Copied: the caller may reuse its array as soon as this returns.
                Buffer = CopyOf(buffer, count),
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

    private static byte[] CopyOf(byte[] buffer, int count)
    {
        byte[] copy = new byte[count];
        Buffer.BlockCopy(buffer, 0, copy, 0, count);
        return copy;
    }

    /// <summary>Tells the transport the current terminal size (used by telnet); other transports ignore it.</summary>
    public void SetTerminalSize(int columns, int rows)
    {
        (_transport as ITerminalSizeAwareTransport)?.SetTerminalSize(columns, rows);
    }

    /// <summary>Tells the transport the encoding of the terminal (used by telnet); other transports ignore it.</summary>
    public void SetTextEncoding(System.Text.Encoding encoding)
    {
        (_transport as ITextEncodingAwareTransport)?.SetTextEncoding(encoding);
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
