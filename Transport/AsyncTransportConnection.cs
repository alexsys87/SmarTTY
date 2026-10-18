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

    public SerialPortStream SerialStream => (_transport as SerialTransport)?.SerialStream;

    public event EventHandler WriterIdleChanged;

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
        catch
        {
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
                StateChanged(this, _state, null);
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
                try
                {
                    PendingWrite pendingWrite;
                    lock (_writeQueue)
                    {
                        pendingWrite = _writeQueue.Dequeue();
                    }
                    _transport.GetStream().Write(pendingWrite.Buffer, 0, pendingWrite.Length);
                }
                catch
                {
                }
            }
        }
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
