using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

namespace ManagedLibSSH2;

internal class X11ForwardChannel : SshChannelBase, IChannelPoller
{
    private TcpClient _client;

    private Thread _receiveThread;

    private long _bytesSent;

    private long _bytesReceived;

    private object _pollerToken;

    public bool IsConnected => _client != null;

    internal X11ForwardChannel(LibSsh2Session libSsh2Session, IntPtr handle, TcpClient tcpClient)
        : base(libSsh2Session, handle)
    {
        try
        {
            _pollerToken = _session.AddPoller(this);
            _client = tcpClient;
            if (_client != null)
            {
                _client.NoDelay = true;
            }
            this._receiveThread = new Thread(ReceiveLoop);
            this._receiveThread.Start();
        }
        catch
        {
        }
    }

    public void PollChannel(ref byte[] buffer, ref bool flag, ref int value2, List<ThreadStart> list)
    {
        int num = _session.ReadChannelNoWait(_channelHandle, 0, buffer);
        if (num == -43)
        {
            flag = true;
        }
        if (num > 0)
        {
            value2 += num;
            try
            {
                if (_client != null)
                {
                    _client.Client.Send(buffer, num, SocketFlags.None);
                    this._bytesSent += num;
                }
            }
            catch
            {
            }
        }
        if (IsEof || !this.IsConnected)
        {
            Dispose();
        }
    }

    public override void Dispose()
    {
        if (_client != null)
        {
            _client.Close();
        }
        if (_pollerToken != null)
        {
            _session.RemovePoller(_pollerToken);
            _pollerToken = null;
        }
        base.Dispose();
        if (_receiveThread != null)
        {
            _receiveThread.Join();
        }
    }

    private void ReceiveLoop()
    {
        byte[] buffer = new byte[262144];
        try
        {
            while (true)
            {
                int num = _client.Client.Receive(buffer);
                if (num > 0)
                {
                    _session.WriteChannelFully(_channelHandle, 0, buffer, 0, num);
                    _bytesReceived += num;
                    continue;
                }
                break;
            }
        }
        catch
        {
        }
    }

    public void Abort()
    {
        try
        {
            _client.Close();
        }
        catch
        {
        }
    }
}
