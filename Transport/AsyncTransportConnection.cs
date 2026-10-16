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

    public delegate void DataReceivedHandler(object obj, byte[] buffer, int value2);

    public delegate void StateChangedHandler(object obj, ConnectionStateEnum connectionStateEnum, string text);

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

    public AsyncTransportConnection(IDeviceTransport deviceTransport, Dispatcher dispatcher, int value2 = 0, IHostNameResolver hostNameResolver = null)
    {
        _transport = deviceTransport;
        _dispatcher = dispatcher;
        _connectTimeoutMs = value2;
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
        catch (Exception ex)
        {
            Exception ex2 = ex;
            Exception exception = ex2;
            _state = ConnectionStateEnum.Failed;
            PostToUi(() =>
            {
                if (StateChanged != null && !_disposed)
                {
                    StateChanged(this, _state, exception.Message);
                }
            });
            return;
        }
        _writerThread = new Thread(WriterLoop);
        _writerThread.Start();
        try
        {
            Stream stream = _transport.GetStream();
            byte[] array = new byte[1024];
            while (!_stopRequested)
            {
                int value2 = stream.Read(array, 0, array.Length);
                if (value2 <= 0)
                {
                    break;
                }
                byte[] buffer = new byte[value2];
                for (int num = 0; num < value2; num++)
                {
                    buffer[num] = array[num];
                }
                PostToUi(() =>
                {
                    if (DataReceived != null && !_disposed)
                    {
                        DataReceived(this, buffer, value2);
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

    public void Send(byte[] buffer, int value2)
    {
        lock (_writeQueue)
        {
            _writeQueue.Enqueue(new PendingWrite
            {
                Buffer = buffer,
                Length = value2
            });
        }
        _writeSignal.Release();
    }

    public string GetDisplayName(bool flag, IHostNameResolver hostNameResolver)
    {
        string text = _transport.GetDisplayName(flag);
        return hostNameResolver?.ResolveHostName(text) ?? text;
    }

    public bool MatchesPort(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }
        if (_transport is SerialTransport serialTransport)
        {
            return serialTransport.PortName == text;
        }
        return false;
    }
}
