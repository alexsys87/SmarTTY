using System;
using System.Collections.Generic;
using System.Threading;

namespace ManagedLibSSH2;

public class SshShellChannel : SshChannelBase, IChannelPoller, IAsyncChannel
{

    public readonly ChannelStream Input;

    public readonly ChannelStream Output;

    public readonly ChannelStream Error;

    public readonly string TerminalType;

    private object _pollerToken;

    private object _syncRoot = new object();

    private bool _eofNotified;

    private int _closedNotified;

    private ExtendedDataMode _extendedDataMode;

    public ChannelStream ActiveErrorStream
    {
        get
        {
            if (ReadStderrOnRead)
            {
                return null;
            }
            return Error;
        }
    }

    public bool PollingEnabled
    {
        get
        {
            return _pollerToken != null;
        }
        set
        {
            bool flag = false;
            lock (_syncRoot)
            {
                if (PollingEnabled == value)
                {
                    return;
                }
                if (value)
                {
                    _pollerToken = base._session.AddPoller(this);
                    flag = true;
                }
                else
                {
                    base._session.RemovePoller(_pollerToken);
                    _pollerToken = null;
                }
            }
            if (flag)
            {
                byte[] buffer = new byte[262144];
                bool flag2 = false;
                int value2 = 0;
                base._session.ExecuteNonBlocking(() =>
                {
                    ((IChannelPoller)this).PollChannel(ref buffer, ref flag2, ref value2, (List<ThreadStart>)null);
                    return 0;
                });
            }
        }
    }

    public ExtendedDataMode ExtendedDataMode
    {
        get
        {
            return _extendedDataMode;
        }
        set
        {
            lock (_syncRoot)
            {
                if (value != _extendedDataMode)
                {
                    _extendedDataMode = value;
                    int num = base._session.ExecuteBlocking(() => LibSsh2.ChannelHandleExtendedData2(base._channelHandle, (int)value));
                    if (num != 0)
                    {
                        throw new LibSsh2Exception(num);
                    }
                }
            }
        }
    }

    public int ExitStatus => LibSsh2.ChannelGetExitStatus(base._channelHandle);

    public bool ReadStderrOnRead { get; set; }

    public event DataReceivedHandler DataReceived;

    public event ChannelEventHandler EofReceived;

    public event ChannelEventHandler Finished;

    public int ReadAnyStream(byte[] buffer, int value2, int value3, out bool flag)
    {
        int result = base._session.ReadChannelRange(base._channelHandle, 1, 0, buffer, value2, value3, out var num);
        flag = num == 1;
        return result;
    }

    internal SshShellChannel(LibSsh2Session libSsh2Session, IntPtr handle, string text, string text2, bool flag, ExtendedDataMode extendedDataMode)
        : base(libSsh2Session, handle)
    {
        if (text2 != null)
        {
            RequestPty(80, 25, text2);
        }
        TerminalType = text2;
        if (flag)
        {
            RequestX11Forwarding(0);
        }
        ExtendedDataMode = extendedDataMode;
        libSsh2Session.StartShellOrExec(base._channelHandle, text);
        Input = new ChannelStream(this, 0, flag: true);
        Output = new ChannelStream(this, 0, flag: false);
        Error = new ChannelStream(this, 1, flag: false);
    }

    private int RequestX11Forwarding(int value2)
    {
        return base._session.ExecuteBlocking(() => LibSsh2.ChannelX11ReqEx(base._channelHandle, 0, null, null, value2));
    }

    private void RequestPty(int value2, int value3, string text = "ansi", string text2 = null, int value4 = 0, int value5 = 0)
    {
        base._session.WaitBeforePtyRequest();
        int num = base._session.ExecuteBlocking(() => LibSsh2.ChannelRequestPtyEx(base._channelHandle, text, text.Length, text2, (text2 != null) ? text2.Length : 0, value2, value3, value4, value5));
        if (num != 0)
        {
            throw new PtyRequestFailedException(new LibSsh2Exception(num));
        }
        base._session.MarkPtyRequested();
    }

    public void ResizePty(int value2, int value3, int value4 = 0, int value5 = 0)
    {
        int num = base._session.ExecuteBlocking(() => LibSsh2.ChannelRequestPtySizeEx(base._channelHandle, value2, value3, value4, value5));
        if (num != 0)
        {
            throw new LibSsh2Exception(num);
        }
    }

    private void Poll(ref byte[] buffer, ref bool flag, ref int value2, List<ThreadStart> list)
    {
        int num;
        do
        {
            num = base._session.ReadChannelNoWait(base._channelHandle, 0, buffer);
            if (num == -43)
            {
                flag = true;
            }
            if (num > 0)
            {
                value2 += num;
            }
            if (num >= 0 && DataReceived != null)
            {
                try
                {
                    DataReceived(this, buffer, 0, num, 0);
                }
                catch
                {
                }
            }
        }
        while (num > 0);
        if (_extendedDataMode == ExtendedDataMode.Normal)
        {
            do
            {
                num = base._session.ReadChannelNoWait(base._channelHandle, 1, buffer);
                if (num > 0)
                {
                    value2 += num;
                }
                if (num >= 0 && DataReceived != null)
                {
                    try
                    {
                        DataReceived(this, buffer, 0, num, 1);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            while (num > 0);
        }
        if (!this._eofNotified && LibSsh2.ChannelEof(base._channelHandle) > 0)
        {
            if (EofReceived != null)
            {
                EofReceived(this);
            }
            this._eofNotified = true;
            value2++;
        }
        if (_closedNotified != 0 || LibSsh2.ChannelWaitClosed(base._channelHandle) < 0)
        {
            return;
        }
        if (Interlocked.Exchange(ref _closedNotified, 1) == 0 && Finished != null)
        {
            if (list != null)
            {
                list.Add(() =>
                {
                    Finished(this);
                });
            }
            else
            {
                Finished(this);
            }
        }
        value2++;
    }

    void IChannelPoller.PollChannel(ref byte[] buffer, ref bool flag, ref int value2, List<ThreadStart> list)
    {
        this.Poll(ref buffer, ref flag, ref value2, list);
    }

    public override void Dispose()
    {
        base.Dispose();
        PollingEnabled = false;
        if (Interlocked.Exchange(ref _closedNotified, 1) == 0 && Finished != null)
        {
            Finished(this);
        }
    }

    protected override int ReadStream(int value2, byte[] buffer, int value3, int value4)
    {
        if (value2 == 0 && ReadStderrOnRead)
        {
            int num;
            while (true)
            {
                num = base._session.ReadChannelRange(base._channelHandle, 1, 0, buffer, value3, value4, out var num2);
                if (num <= 0 || num2 != 1)
                {
                    break;
                }
                if (DataReceived != null)
                {
                    DataReceived(this, buffer, value3, num, num2);
                }
            }
            return num;
        }
        return base.ReadStream(value2, buffer, value3, value4);
    }
}
