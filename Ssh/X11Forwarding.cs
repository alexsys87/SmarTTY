using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Connection;
using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SmarTTY.Ssh;

/// <summary>
/// X11 forwarding of one SSH connection (the equivalent of "ssh -X"), implemented by SmarTTY on top of an unmodified SSH.NET.
/// <para>
/// Every PTY channel asks the server for X11 forwarding ("x11-req") with a random fake cookie. The server then sets DISPLAY
/// for the remote programs and opens an "x11" channel for each of their connections. SSH.NET has no implementation of that
/// channel type, so the channels are run here: <see cref="X11Channel"/> takes the channel messages from the public events of
/// the SSH.NET session, checks the fake cookie in the X11 connection setup, removes it and relays the connection to the
/// local X server (VcXsrv) through the socket returned by the connector callback.
/// </para>
/// </summary>
public sealed class X11Forwarding : IDisposable
{
    public const string AuthenticationProtocol = "MIT-MAGIC-COOKIE-1";

    // Channel type of the connections that the server opens for X11 clients (RFC 4254, 6.3.2).
    private const string X11ChannelType = "x11";

    // SSH_MSG_CHANNEL_OPEN_FAILURE reason codes (RFC 4254, 5.1).
    private const uint OpenAdministrativelyProhibited = 1;

    private const uint OpenConnectFailed = 2;

    // After the user declines to start an X server, further connections of the same burst fail at once instead of asking again.
    private static readonly TimeSpan DeclineInterval = TimeSpan.FromSeconds(10);

    private enum RequestState
    {
        NotRequested,
        Accepted,
        Refused
    }

    private readonly SshSession _owner;

    private readonly Session _session;

    private readonly Func<Socket> _connectToXServer;

    private readonly byte[] _cookie = new byte[16];

    private readonly string _cookieText;

    private readonly ConcurrentDictionary<uint, X11Channel> _channels = new ConcurrentDictionary<uint, X11Channel>();

    private readonly object _connectLock = new object();

    private DateTime _declinedUntil;

    private int _state;

    private int _refusalReported;

    private int _disposed;

    private int _connectionCount;

    /// <summary>
    /// Raised once, on a worker thread, when the server refuses X11 forwarding (the server has no xauth, has X11Forwarding
    /// disabled or was built without X11 support). See <see cref="X11RemoteStatus"/> for finding out which.
    /// </summary>
    public event EventHandler Refused;

    /// <summary>True once the server has accepted an X11 forwarding request on this connection.</summary>
    public bool IsAccepted => (RequestState)Volatile.Read(ref _state) == RequestState.Accepted;

    /// <summary>True once the server has refused X11 forwarding; it is not requested again on this connection.</summary>
    public bool IsRefused => (RequestState)Volatile.Read(ref _state) == RequestState.Refused;

    /// <summary>Number of X11 connections opened by remote programs so far.</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>Number of X11 connections currently relayed.</summary>
    public int ActiveConnectionCount => _channels.Count;

    /// <param name="connectToXServer">
    /// Returns a socket connected to the local X server, or null if there is none (for example the user declined to start one).
    /// Called on a worker thread, one call at a time.
    /// </param>
    internal X11Forwarding(SshSession owner, Session session, Func<Socket> connectToXServer)
    {
        _owner = owner;
        _session = session;
        _connectToXServer = connectToXServer;
        using (RandomNumberGenerator random = RandomNumberGenerator.Create())
        {
            random.GetBytes(_cookie);
        }
        _cookieText = ToHex(_cookie);
        // SSH.NET only accepts the messages enabled for the session; channel open requests from the server are off until a
        // forwarding asks for them (ForwardedPortRemote does the same; it never turns them off again, nor is it done here).
        session.RegisterMessage("SSH_MSG_CHANNEL_OPEN");
        session.ChannelOpenReceived += OnChannelOpen;
        session.ChannelDataReceived += OnChannelData;
        session.ChannelEofReceived += OnChannelEof;
        session.ChannelCloseReceived += OnChannelClose;
        session.ChannelWindowAdjustReceived += OnChannelWindowAdjust;
        session.Disconnected += OnDisconnected;
    }

    internal static string ToHex(byte[] data)
    {
        StringBuilder text = new StringBuilder(data.Length * 2);
        foreach (byte value in data)
        {
            text.Append(value.ToString("x2"));
        }
        return text.ToString();
    }

    /// <summary>
    /// Asks for X11 forwarding on a session channel that has been opened but has not started its shell or command yet.
    /// Does nothing once the server has refused it on this connection.
    /// </summary>
    internal void RequestOn(SshNetChannel channel)
    {
        if (Volatile.Read(ref _disposed) != 0 || IsRefused)
        {
            return;
        }
        if (channel.SendX11ForwardingRequest(false, AuthenticationProtocol, _cookieText, 0))
        {
            Interlocked.CompareExchange(ref _state, (int)RequestState.Accepted, (int)RequestState.NotRequested);
            return;
        }
        Volatile.Write(ref _state, (int)RequestState.Refused);
        if (Interlocked.Exchange(ref _refusalReported, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Refused?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    // SSH.NET raises the session events on its receive thread: nothing here may block or send a message (see
    // SshNetChannel.SendSessionMessage), so replies go through the thread pool.
    private void OnChannelOpen(object sender, MessageEventArgs<ChannelOpenMessage> e)
    {
        if (e.Message.Info?.ChannelType != X11ChannelType)
        {
            return;
        }
        uint remoteChannel = e.Message.LocalChannelNumber;
        uint remoteWindow = e.Message.InitialWindowSize;
        uint remotePacketSize = e.Message.MaximumPacketSize;
        if (Volatile.Read(ref _disposed) != 0 || !IsAccepted)
        {
            // An X11 channel that was never asked for (or arrives after closing) must not reach the local display.
            Post(() => SendOpenFailure(remoteChannel, "X11 forwarding was not requested", OpenAdministrativelyProhibited));
            return;
        }
        Post(() => Accept(remoteChannel, remoteWindow, remotePacketSize));
    }

    private void Accept(uint remoteChannel, uint remoteWindow, uint remotePacketSize)
    {
        Socket socket = ConnectToXServer();
        if (socket == null)
        {
            SendOpenFailure(remoteChannel, "No X server is running on the SSH client computer", OpenConnectFailed);
            return;
        }
        X11Channel channel = null;
        try
        {
            uint localChannel = SshNetChannel.AllocateChannelNumber(_session);
            channel = new X11Channel(this, localChannel, remoteChannel, remoteWindow, remotePacketSize, socket, _cookie);
            // Registered before confirming, so that no data of the channel can arrive before it is known.
            _channels[localChannel] = channel;
            Send(new ChannelOpenConfirmationMessage(remoteChannel, X11Channel.LocalWindowSize, X11Channel.LocalPacketSize, localChannel));
        }
        catch
        {
            if (channel != null)
            {
                _channels.TryRemove(channel.LocalChannel, out _);
            }
            CloseSocket(socket);
            return;
        }
        Interlocked.Increment(ref _connectionCount);
        channel.Start();
    }

    private Socket ConnectToXServer()
    {
        lock (_connectLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || DateTime.UtcNow < _declinedUntil)
            {
                return null;
            }
            Socket socket = null;
            try
            {
                socket = _connectToXServer?.Invoke();
            }
            catch
            {
            }
            if (socket == null)
            {
                _declinedUntil = DateTime.UtcNow + DeclineInterval;
            }
            return socket;
        }
    }

    private void SendOpenFailure(uint remoteChannel, string description, uint reason)
    {
        try
        {
            Send(new ChannelOpenFailureMessage(remoteChannel, description, reason, "en"));
        }
        catch
        {
        }
    }

    private void OnChannelData(object sender, MessageEventArgs<ChannelDataMessage> e)
    {
        if (_channels.TryGetValue(e.Message.LocalChannelNumber, out X11Channel channel))
        {
            channel.OnData(e.Message.Data, e.Message.Offset, e.Message.Size);
        }
    }

    private void OnChannelEof(object sender, MessageEventArgs<ChannelEofMessage> e)
    {
        if (_channels.TryGetValue(e.Message.LocalChannelNumber, out X11Channel channel))
        {
            channel.OnRemoteEof();
        }
    }

    private void OnChannelClose(object sender, MessageEventArgs<ChannelCloseMessage> e)
    {
        if (_channels.TryRemove(e.Message.LocalChannelNumber, out X11Channel channel))
        {
            channel.OnRemoteClose();
        }
    }

    private void OnChannelWindowAdjust(object sender, MessageEventArgs<ChannelWindowAdjustMessage> e)
    {
        if (_channels.TryGetValue(e.Message.LocalChannelNumber, out X11Channel channel))
        {
            channel.OnWindowAdjust(e.Message.BytesToAdd);
        }
    }

    private void OnDisconnected(object sender, EventArgs e)
    {
        CloseAllChannels();
    }

    /// <summary>
    /// Closes the connections to the local X server. SSH.NET raises Disconnected only for a DISCONNECT from the
    /// server; a dropped TCP connection comes as an error, so <see cref="SshSession"/> calls this when it notices the loss.
    /// </summary>
    internal void CloseConnections()
    {
        CloseAllChannels();
    }

    private void CloseAllChannels()
    {
        foreach (uint localChannel in _channels.Keys)
        {
            if (_channels.TryRemove(localChannel, out X11Channel channel))
            {
                channel.OnConnectionClosed();
            }
        }
    }

    internal void Send(Renci.SshNet.Messages.Message message)
    {
        SshNetChannel.SendSessionMessage(_session, message);
    }

    internal static void Post(Action action)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                action();
            }
            catch
            {
            }
        });
    }

    internal void AddReceivedBytes(long count)
    {
        _owner?.AddReceivedBytes(count);
    }

    internal void AddSentBytes(long count)
    {
        _owner?.AddSentBytes(count);
    }

    internal static void CloseSocket(Socket socket)
    {
        try
        {
            socket.Dispose();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _session.ChannelOpenReceived -= OnChannelOpen;
        _session.ChannelDataReceived -= OnChannelData;
        _session.ChannelEofReceived -= OnChannelEof;
        _session.ChannelCloseReceived -= OnChannelClose;
        _session.ChannelWindowAdjustReceived -= OnChannelWindowAdjust;
        _session.Disconnected -= OnDisconnected;
        CloseAllChannels();
    }
}

/// <summary>
/// One "x11" channel opened by the server: relays an X11 connection of a remote program to the local X server.
/// The channel is implemented here (SSH.NET only implements the channel types it opens itself), with its own flow control.
/// </summary>
internal sealed class X11Channel
{
    // The window SmarTTY grants the server; it is topped up once half of it has been passed on to the X server.
    internal const uint LocalWindowSize = 2 * 1024 * 1024;

    // Below SSH.NET's limit for received packets.
    internal const uint LocalPacketSize = 32 * 1024;

    private readonly X11Forwarding _owner;

    private readonly uint _remoteChannel;

    private readonly uint _remotePacketSize;

    private readonly Socket _socket;

    private readonly object _windowLock = new object();

    private long _remoteWindow;

    private long _consumed;

    // Checks the fake cookie in the first bytes of the connection; null once the setup has been passed on.
    private X11ConnectionSetup _setup;

    private int _closeSent;

    private volatile bool _closed;

    public readonly uint LocalChannel;

    public X11Channel(X11Forwarding owner, uint localChannel, uint remoteChannel, uint remoteWindow, uint remotePacketSize, Socket socket, byte[] cookie)
    {
        _owner = owner;
        LocalChannel = localChannel;
        _remoteChannel = remoteChannel;
        _remoteWindow = remoteWindow;
        _remotePacketSize = Math.Max(1u, remotePacketSize);
        _socket = socket;
        _setup = new X11ConnectionSetup(cookie);
    }

    public void Start()
    {
        new Thread(ReadFromXServer)
        {
            IsBackground = true,
            Name = "X11 forwarding #" + LocalChannel
        }.Start();
    }

    // Remote program -> X server. Called on SSH.NET's receive thread, in the order of arrival.
    public void OnData(byte[] data, int offset, int count)
    {
        if (_closed)
        {
            return;
        }
        _owner.AddReceivedBytes(count);
        try
        {
            if (_setup != null)
            {
                switch (_setup.Add(data, offset, count))
                {
                    case X11ConnectionSetup.Result.NeedMoreData:
                        break;
                    case X11ConnectionSetup.Result.Accepted:
                        byte[] output = _setup.Output;
                        _setup = null;
                        SendToXServer(output, 0, output.Length);
                        break;
                    default:
                        // Wrong cookie: another user of the remote host tried to reach this display.
                        Close();
                        return;
                }
            }
            else
            {
                SendToXServer(data, offset, count);
            }
        }
        catch
        {
            Close();
            return;
        }
        long consumed = Interlocked.Add(ref _consumed, count);
        if (consumed >= LocalWindowSize / 2)
        {
            uint adjust = (uint)Interlocked.Exchange(ref _consumed, 0);
            X11Forwarding.Post(() =>
            {
                if (!_closed)
                {
                    _owner.Send(new ChannelWindowAdjustMessage(_remoteChannel, adjust));
                }
            });
        }
    }

    private void SendToXServer(byte[] data, int offset, int count)
    {
        while (count > 0)
        {
            int sent = _socket.Send(data, offset, count, SocketFlags.None);
            offset += sent;
            count -= sent;
        }
    }

    // X server -> remote program.
    private void ReadFromXServer()
    {
        byte[] buffer = new byte[Math.Min(_remotePacketSize, LocalPacketSize)];
        try
        {
            while (!_closed)
            {
                int read = _socket.Receive(buffer);
                if (read <= 0)
                {
                    break;
                }
                SendToRemote(buffer, read);
            }
        }
        catch
        {
        }
        Close();
    }

    private void SendToRemote(byte[] buffer, int count)
    {
        int offset = 0;
        while (count > 0)
        {
            int chunk;
            lock (_windowLock)
            {
                while (_remoteWindow <= 0 && !_closed)
                {
                    Monitor.Wait(_windowLock);
                }
                if (_closed)
                {
                    return;
                }
                chunk = (int)Math.Min(Math.Min(count, _remoteWindow), _remotePacketSize);
                _remoteWindow -= chunk;
            }
            _owner.Send(new ChannelDataMessage(_remoteChannel, buffer, offset, chunk));
            _owner.AddSentBytes(chunk);
            offset += chunk;
            count -= chunk;
        }
    }

    public void OnWindowAdjust(uint bytesToAdd)
    {
        lock (_windowLock)
        {
            _remoteWindow += bytesToAdd;
            Monitor.PulseAll(_windowLock);
        }
    }

    // The remote program will send nothing more: let the X server see the end of its input.
    public void OnRemoteEof()
    {
        try
        {
            _socket.Shutdown(SocketShutdown.Send);
        }
        catch
        {
        }
    }

    public void OnRemoteClose()
    {
        // The channel is gone on the server; answer with our own close (once) and release the connection.
        Close(sendEof: false);
    }

    public void OnConnectionClosed()
    {
        Interlocked.Exchange(ref _closeSent, 1);
        Close();
    }

    private void Close()
    {
        Close(sendEof: true);
    }

    // Closes the channel from this side (the X server went away or the cookie was wrong), or answers the server's close.
    private void Close(bool sendEof)
    {
        _closed = true;
        lock (_windowLock)
        {
            Monitor.PulseAll(_windowLock);
        }
        X11Forwarding.CloseSocket(_socket);
        if (Interlocked.Exchange(ref _closeSent, 1) == 0)
        {
            X11Forwarding.Post(() =>
            {
                if (sendEof)
                {
                    _owner.Send(new ChannelEofMessage(_remoteChannel));
                }
                _owner.Send(new ChannelCloseMessage(_remoteChannel));
            });
        }
    }
}

/// <summary>
/// The connection setup that an X11 client sends first (X Window System protocol, "Connection Setup"): byte order, protocol
/// version and the authorization protocol name and data. Accepts it only with the fake cookie that was given to the server,
/// and passes it on to the local X server without authorization data (VcXsrv admits local connections by their address).
/// </summary>
internal sealed class X11ConnectionSetup
{
    public enum Result
    {
        NeedMoreData,
        Accepted,
        Rejected
    }

    private const int HeaderLength = 12;

    private readonly byte[] _cookie;

    private byte[] _buffer = new byte[HeaderLength];

    private int _length;

    /// <summary>The setup to pass on once <see cref="Add"/> has returned <see cref="Result.Accepted"/>, followed by any further bytes.</summary>
    public byte[] Output { get; private set; }

    public string RejectReason { get; private set; }

    public X11ConnectionSetup(byte[] cookie)
    {
        _cookie = cookie;
    }

    public Result Add(byte[] data, int offset, int count)
    {
        if (RejectReason != null)
        {
            return Result.Rejected;
        }
        if (_length + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
        }
        Buffer.BlockCopy(data, offset, _buffer, _length, count);
        _length += count;
        if (_length < HeaderLength)
        {
            return Result.NeedMoreData;
        }
        bool bigEndian;
        switch (_buffer[0])
        {
            case (byte)'B':
                bigEndian = true;
                break;
            case (byte)'l':
                bigEndian = false;
                break;
            default:
                return Reject("invalid byte order mark");
        }
        int nameLength = ReadUInt16(6, bigEndian);
        int dataLength = ReadUInt16(8, bigEndian);
        int nameStart = HeaderLength;
        int dataStart = nameStart + Pad(nameLength);
        int setupLength = dataStart + Pad(dataLength);
        if (_length < setupLength)
        {
            return Result.NeedMoreData;
        }
        string name = Encoding.ASCII.GetString(_buffer, nameStart, nameLength);
        if (name != X11Forwarding.AuthenticationProtocol)
        {
            return Reject("unexpected authorization protocol '" + name + "'");
        }
        if (!CookieMatches(dataStart, dataLength))
        {
            return Reject("wrong authorization cookie");
        }
        // The same header with empty authorization, then whatever followed the setup in the received data.
        byte[] output = new byte[HeaderLength + _length - setupLength];
        Buffer.BlockCopy(_buffer, 0, output, 0, HeaderLength);
        output[6] = output[7] = output[8] = output[9] = 0;
        Buffer.BlockCopy(_buffer, setupLength, output, HeaderLength, _length - setupLength);
        Output = output;
        _buffer = null;
        return Result.Accepted;
    }

    private bool CookieMatches(int start, int length)
    {
        if (length != _cookie.Length)
        {
            return false;
        }
        int difference = 0;
        for (int i = 0; i < length; i++)
        {
            difference |= _buffer[start + i] ^ _cookie[i];
        }
        return difference == 0;
    }

    private Result Reject(string reason)
    {
        RejectReason = reason;
        _buffer = null;
        return Result.Rejected;
    }

    private int ReadUInt16(int position, bool bigEndian)
    {
        return bigEndian ? ((_buffer[position] << 8) | _buffer[position + 1]) : (_buffer[position] | (_buffer[position + 1] << 8));
    }

    private static int Pad(int length)
    {
        return (length + 3) & ~3;
    }
}
