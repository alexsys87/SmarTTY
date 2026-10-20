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
            DateTime now = DateTime.Now;
            while (true)
            {
                try
                {
                    _transport.Open(_hostResolver);
                }
                catch
                {
                    if ((DateTime.Now - now).TotalMilliseconds >= (double)_connectTimeoutMs || _disposed)
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
            // Large enough for a fast port (921600 bps is about 90 KB/s) to be drained in few reads and UI updates.
            byte[] rawBuffer = new byte[4096];
            while (!_stopRequested)
            {
                int count = stream.Read(rawBuffer, 0, rawBuffer.Length);
                if (count <= 0)
                {
                    break;
                }
                byte[] buffer = new byte[count];
                Buffer.BlockCopy(rawBuffer, 0, buffer, 0, count);
                PostToUi(() =>
                {
                    if (DataReceived != null && !_disposed)
                    {
                        DataReceived(this, buffer, count);
                    }
                });
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
        PostToUi(() =>
        {
            if (StateChanged != null && !_disposed)
            {
                StateChanged(this, _state, disconnectReason);
            }
        });
    }

    private void WriterLoop()
    {
        while (!_stopRequested && !_finished)
        {
            while (true)
            {
                _writerIdle = true;
                PostToUi(() =>
                {
                    if (WriterIdleChanged != null && !_disposed)
                    {
                        WriterIdleChanged(this, EventArgs.Empty);
                    }
                });
                _writeSignal.WaitOne();
                if (_stopRequested || _finished)
                {
                    break;
                }
                _writerIdle = false;
                PostToUi(() =>
                {
                    if (WriterIdleChanged != null && !_disposed)
                    {
                        WriterIdleChanged(this, EventArgs.Empty);
                    }
                });
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
