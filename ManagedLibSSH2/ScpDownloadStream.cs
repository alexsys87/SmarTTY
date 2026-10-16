using System;
using System.Collections.Generic;
using System.Threading;

namespace ManagedLibSSH2;

public class ScpDownloadStream : ScpChannelStream, IChannelPoller, IAsyncChannel
{
    public readonly DateTime ChangeTime;

    public readonly DateTime ModifyTime;

    public readonly DateTime AccessTime;

    private object _pollerToken;

    private object _syncRoot = new object();

    private bool _eofNotified;

    public bool PollingEnabled
    {
        get
        {
            return _pollerToken != null;
        }
        set
        {
            lock (_syncRoot)
            {
                if (PollingEnabled != value)
                {
                    if (value)
                    {
                        _pollerToken = _session.AddPoller(this);
                        return;
                    }
                    _session.RemovePoller(_pollerToken);
                    _pollerToken = null;
                }
            }
        }
    }

    public event DataReceivedHandler DataReceived;

    public event ChannelEventHandler Finished;

    internal ScpDownloadStream(LibSsh2Session libSsh2Session, IntPtr handle, LibSsh2.ScpFileStat scpFileStat)
        : base(libSsh2Session, handle, flag: true, scpFileStat.Size)
    {
        ChangeTime = new DateTime(1970, 1, 1).AddSeconds(scpFileStat.ChangeTime);
        ModifyTime = new DateTime(1970, 1, 1).AddSeconds(scpFileStat.ModifyTime);
        AccessTime = new DateTime(1970, 1, 1).AddSeconds(scpFileStat.AccessTime);
    }

    private void Poll(ref byte[] buffer, ref bool flag, ref int value2, List<ThreadStart> list)
    {
        int num = _session.ReadChannelNoWait(base._channelHandle, 0, buffer);
        if (num == -43)
        {
            flag = true;
        }
        if (num > 0)
        {
            value2 += num;
        }
        if (num >= 0 && this.DataReceived != null)
        {
            try
            {
                this.DataReceived(this, buffer, 0, num, 0);
            }
            catch (Exception)
            {
            }
        }
        if (LibSsh2.ChannelEof(base._channelHandle) <= 0 || _eofNotified)
        {
            return;
        }
        if (Finished != null)
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
        _eofNotified = true;
        value2++;
    }

    void IChannelPoller.PollChannel(ref byte[] buffer, ref bool flag, ref int value2, List<ThreadStart> list)
    {
        this.Poll(ref buffer, ref flag, ref value2, list);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        PollingEnabled = false;
    }
}
