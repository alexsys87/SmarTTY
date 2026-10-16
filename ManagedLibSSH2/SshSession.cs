using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

namespace ManagedLibSSH2;

public class SshSession : LibSsh2Session
{
    public delegate TcpClient X11ConnectionFactory();

    private Thread _pumpThread;

    private bool _disposing;

    private LibSsh2.SessionCallback _x11Callback;

    public readonly bool EnableOnTheFlyTar;

    internal string Tag;

    private byte[] _pumpBuffer;

    public X11ConnectionFactory X11Connector;

    public bool X11ForwardingEnabled
    {
        get
        {
            return _x11Enabled;
        }
        set
        {
            _x11Enabled = value;
        }
    }

    public SshSession(Socket socket, string text, int value2, bool flag, bool flag2)
        : base(socket, text, value2, flag)
    {
        _x11Callback = this.OnX11Open;
        LibSsh2.SessionCallbackSet(_sessionHandle, 4, _x11Callback);
        _x11Enabled = true;
        EnableOnTheFlyTar = flag2;
        this._pumpThread = new Thread(this.PumpThreadProc);
        this._pumpThread.Start();
    }

    private int PumpOnce()
    {
        if (_disposing)
        {
            throw new OperationCanceledException();
        }
        List<ThreadStart> list = new List<ThreadStart>();
        try
        {
            lock (_pollers)
            {
                if (_pollers.Count == 0)
                {
                    return 0;
                }
                if (_pumpBuffer == null)
                {
                    _pumpBuffer = new byte[262144];
                }
                long num;
                int num2;
                do
                {
                    num = Statistics.BytesReceived;
                    bool flag = false;
                    num2 = 0;
                    _pollersChanged = false;
                    foreach (IChannelPoller item in _pollers)
                    {
                        item.PollChannel(ref _pumpBuffer, ref flag, ref num2, list);
                        if (_pollersChanged)
                        {
                            break;
                        }
                        if (flag)
                        {
                            return -43;
                        }
                    }
                }
                while (_pollersChanged || num2 != 0 || num != Statistics.BytesReceived);
            }
        }
        finally
        {
            foreach (ThreadStart item2 in list)
            {
                item2();
            }
        }
        return -37;
    }

    private void PumpThreadProc()
    {
        while (true)
        {
            try
            {
                int num = ExecuteBlocking(PumpOnce);
                if (num == 0)
                {
                    _pollerEvent.WaitOne(100, exitContext: false);
                }
                if (num == -43)
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    public override void Dispose()
    {
        _disposing = true;
        base.Dispose();
        _pumpThread.Join();
    }

    private void OnX11Open(IntPtr handle, IntPtr handle2, string text, int value2, IntPtr handle3)
    {
        TcpClient tcpClient = null;
        if (_x11Enabled)
        {
            try
            {
                tcpClient = ((X11Connector == null) ? new TcpClient("localhost", 6000) : X11Connector());
            }
            catch
            {
            }
        }
        if (tcpClient == null)
        {
            _x11Enabled = false;
        }
        new X11ForwardChannel(this, handle2, tcpClient);
    }
}
