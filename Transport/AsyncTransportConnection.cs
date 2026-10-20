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
    }

    public delegate void DataReceivedHandler(object sender, byte[] buffer, int count);

    public delegate void StateChangedHandler(object sender, ConnectionStateEnum connectionStateEnum, string message);

    public delegate void WriteFailedHandler(object sender, string message);

    private readonly IDeviceTransport _transport;

    private readonly Dispatcher _dispatcher;

    private readonly int _connectTimeoutMs;

    private readonly IHostNameResolver _hostResolver;

    private bool _started;

    private bool _stopRequested;

    private bool _disposed;

    private Thread _connectThread;

    private Thread _writerThread;

    private ConnectionStateEnum _state;

    private Queue<PendingWrite> _writeQueue = new Queue<PendingWrite>();

    private Semaphore _writeSignal = new Semaphore(0, int.MaxValue);

    private bool _writerIdle;

    public bool IsWriterIdle => _writerIdle;

    private volatile string _writeError;

    /// <summary>The reason of the last failed write, or null when the last write went through.</summary>
    public string WriteError => _writeError;

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
        _writerThread = new Thread(WriterLoop);
        _writerThread.Start();
        string disconnectReason = null;
        try
        {
            Stream stream = _transport.GetStream();
            byte[] rawBuffer = new byte[1024];
            while (!_stopRequested)
            {
                int count = stream.Read(rawBuffer, 0, rawBuffer.Length);
                if (count <= 0)
                {
                    break;
                }
                byte[] buffer = new byte[count];
                for (int i = 0; i < count; i++)
                {
                    buffer[i] = rawBuffer[i];
                }
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
        while (!_stopRequested)
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
                if (_stopRequested)
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
                    _transport.GetStream().Write(pendingWrite.Buffer, 0, pendingWrite.Length);
                    _writeError = null;
                }
                catch (Exception writeException)
                {
                    ReportWriteFailure(writeException);
                }
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
            return; // the connection is being closed on purpose
        }
        int discarded;
        lock (_writeQueue)
        {
            discarded = _writeQueue.Count;
            _writeQueue.Clear();
        }
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

    public void Start()
    {
        if (!_started)
        {
            StateChanged?.Invoke(this, ConnectionStateEnum.Connecting, null);
            _started = true;
            _connectThread = new Thread(ConnectAndReadLoop);
            _connectThread.Start();
        }
    }

    public void Dispose()
    {
        _stopRequested = true;
        _writeSignal.Release();
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

    public void Send(byte[] buffer, int count)
    {
        lock (_writeQueue)
        {
            _writeQueue.Enqueue(new PendingWrite
            {
                Buffer = buffer,
                Length = count
            });
        }
        _writeSignal.Release();
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
