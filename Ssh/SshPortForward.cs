using Renci.SshNet;
using Renci.SshNet.Common;
using System;

namespace SmarTTY.Ssh;

/// <summary>
/// A started port forwarding of an SSH connection (<see cref="SshSession.OpenLocalForward"/>,
/// <see cref="SshSession.OpenRemoteForward"/>). Disposing it stops the listener and closes the connections it carries.
/// </summary>
public sealed class SshPortForward : IDisposable
{
    private readonly SshClient _client;

    private readonly ForwardedPort _port;

    private int _disposed;

    /// <summary>Raised from any thread with the message of a failure of one forwarded connection (refused by the destination ...).</summary>
    public event Action<string> Error;

    private SshPortForward(SshClient client, ForwardedPort port)
    {
        _client = client;
        _port = port;
        _port.Exception += (object sender, ExceptionEventArgs e) => Error?.Invoke(e.Exception.Message);
    }

    internal static SshPortForward Start(SshClient client, ForwardedPort port)
    {
        SshPortForward forward = new SshPortForward(client, port);
        client.AddForwardedPort(port);
        try
        {
            port.Start();
        }
        catch
        {
            forward.Dispose();
            throw;
        }
        return forward;
    }

    /// <summary>The port that listens: for local forwarding the one the system chose when port 0 was asked for.</summary>
    public uint BoundPort => _port is ForwardedPortLocal local ? local.BoundPort : ((ForwardedPortRemote)_port).BoundPort;

    /// <summary>False once the forwarding was stopped, also when its SSH connection was lost.</summary>
    public bool IsStarted
    {
        get
        {
            try
            {
                return _disposed == 0 && _port.IsStarted && _client.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            _port.Stop();
        }
        catch
        {
        }
        try
        {
            _client.RemoveForwardedPort(_port);
        }
        catch
        {
        }
        try
        {
            _port.Dispose();
        }
        catch
        {
        }
    }
}
