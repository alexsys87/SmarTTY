using System;
using System.IO;

namespace ManagedLibSSH2;

internal class KnownHostsFile : IDisposable
{
    private IntPtr _knownHosts;

    private string _path;

    internal KnownHostsFile(IntPtr handle, string text, bool flag)
    {
        this._knownHosts = LibSsh2.KnownhostInit(handle);
        if (this._knownHosts == IntPtr.Zero)
        {
            throw new LibSsh2Exception(LibSsh2.SessionLastErrno(handle));
        }
        _path = text;
        if (File.Exists(text))
        {
            int num = LibSsh2.KnownhostReadfile(this._knownHosts, text, 1);
            if (num < 0)
            {
                throw new LibSsh2Exception(num);
            }
        }
        else if (flag)
        {
            throw new IOException(text + " does not exist");
        }
    }

    public void Save()
    {
        int num = LibSsh2.KnownhostWritefile(_knownHosts, _path, 1);
        if (num < 0)
        {
            throw new LibSsh2Exception(num);
        }
    }

    public void AddHost(string text, int value2, IntPtr handle, int value3, int value4, string text2 = null)
    {
        int num = LibSsh2.KnownhostAddc(value4: 0x10001 | (value4 switch
        {
            1 => 262144,
            2 => 786432,
            _ => throw new Exception("Unknown host key type"),
        }), handle: this._knownHosts, text: text, text2: null, handle2: handle, value2: value3, text3: text2, value3: text2?.Length ?? 0, handle3: IntPtr.Zero);
        if (num < 0)
        {
            throw new LibSsh2Exception(num);
        }
    }

    public KnownHostCheckResult CheckHost(string text, int value2, IntPtr handle, int value3)
    {
        KnownHostCheckResult knownHostCheckResult = LibSsh2.KnownhostCheckp(this._knownHosts, text, value2, handle, value3, 65537, IntPtr.Zero);
        if ((uint)knownHostCheckResult <= 2u)
        {
            return knownHostCheckResult;
        }
        return KnownHostCheckResult.Failure;
    }

    public void Dispose()
    {
        if (_knownHosts != IntPtr.Zero)
        {
            LibSsh2.KnownhostFree(_knownHosts);
            _knownHosts = IntPtr.Zero;
        }
    }
}
