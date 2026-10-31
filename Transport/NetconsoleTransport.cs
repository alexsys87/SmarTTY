using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Transport;

/// <summary>
/// A Linux netconsole receiver: a UDP port is opened here, and every datagram that arrives on it is read from the
/// stream (decoded by <see cref="NetconsoleDecoder"/>). What is written to the stream is sent back as UDP datagrams,
/// to the configured device or to whoever sent the last datagram, either one datagram per write (character mode) or
/// one per line (line mode). Nothing has to be connected, so <see cref="Open"/> only fails when the port cannot be
/// opened or the device address cannot be resolved.
/// </summary>
internal class NetconsoleTransport : IDeviceTransport, IDisposable
{
    // SIO_UDP_CONNRESET: without it Windows reports the ICMP "port unreachable" that answers a datagram sent to a
    // device that does not listen (the Linux module never does) as an error of the next receive.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    // Large enough for the burst of messages a booting kernel sends at once.
    private const int ReceiveBufferBytes = 1024 * 1024;

    /// <summary>
    /// The largest datagram sent with input. U-Boot reads at most 512 bytes of a datagram, and a line is far shorter;
    /// a longer write is split.
    /// </summary>
    internal const int MaxSendDatagram = 512;

    private readonly DeviceTerminalSettings.ConnectionSettings.Netconsole _settings;

    private readonly object _lock = new object();

    private Socket _socket;

    private NetconsoleStream _stream;

    private bool _closed;

    // Where input goes when the device address is configured; null otherwise (then the last sender gets it).
    private IPEndPoint _configuredTarget;

    // The addresses datagrams are accepted from, when only the configured device is shown.
    private IPAddress[] _acceptedSenders;

    private volatile IPEndPoint _lastSender;

    private volatile DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode _inputMode;

    /// <summary>Raised on the reader thread when a datagram arrives from another address than the previous one.</summary>
    public event EventHandler SenderChanged;

    public NetconsoleTransport(DeviceTerminalSettings.ConnectionSettings.Netconsole settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _inputMode = settings.Input;
        Decoder = new NetconsoleDecoder
        {
            DecodeExtended = settings.Format == DeviceTerminalSettings.ConnectionSettings.Netconsole.MessageFormat.Auto,
            ColorizeLevels = settings.ColorizeLevels,
            ShowSender = settings.ShowSender
        };
    }

    internal NetconsoleDecoder Decoder { get; }

    public string ConnectingMessage => "Opening UDP port " + _settings.LocalPort + "...";

    /// <summary>The address the last datagram came from (null before the first one).</summary>
    public IPEndPoint LastSender => _lastSender;

    /// <summary>The local end of the socket once it is open (the port is known even when 0 was asked for).</summary>
    public IPEndPoint LocalEndPoint
    {
        get
        {
            lock (_lock)
            {
                return _socket?.LocalEndPoint as IPEndPoint;
            }
        }
    }

    /// <summary>One datagram per write, or one per line. May be switched while the transport is open.</summary>
    public DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode InputMode
    {
        get => _inputMode;
        set => _inputMode = value;
    }

    /// <summary>Where typed input goes now: the configured device, or the address of the last sender; null when unknown.</summary>
    public IPEndPoint InputTarget
    {
        get
        {
            IPEndPoint configured;
            lock (_lock)
            {
                configured = _configuredTarget;
            }
            if (configured != null)
            {
                return configured;
            }
            IPEndPoint sender = _lastSender;
            return sender == null ? null : new IPEndPoint(sender.Address, _settings.RemotePort);
        }
    }

    public void Open(IHostNameResolver hostNameResolver)
    {
        lock (_lock)
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(NetconsoleTransport), "The connection is closed.");
            }
        }
        IPAddress[] remoteAddresses = null;
        if (!string.IsNullOrWhiteSpace(_settings.RemoteHost))
        {
            string remoteHost = _settings.RemoteHost.Trim();
            remoteAddresses = ResolveAddresses(hostNameResolver?.ResolveHostName(remoteHost) ?? remoteHost);
        }
        else if (_settings.AcceptOnlyFromRemoteHost)
        {
            throw new ArgumentException("Only the device's datagrams should be shown, but no device address is set.");
        }
        Socket socket = Bind();
        try
        {
            try
            {
                socket.IOControl(SioUdpConnReset, new byte[4], null);
            }
            catch
            {
                // Not Windows (or not needed): a reset is skipped by the reader instead.
            }
            try
            {
                socket.ReceiveBufferSize = ReceiveBufferBytes;
            }
            catch (SocketException)
            {
            }
            // Input may go to a broadcast address (a device that has no address of its own yet).
            socket.EnableBroadcast = true;
            IPEndPoint target = null;
            if (remoteAddresses != null)
            {
                IPAddress address = PickAddress(remoteAddresses, socket);
                target = new IPEndPoint(address, _settings.RemotePort);
            }
            NetconsoleStream stream = new NetconsoleStream(this, socket);
            lock (_lock)
            {
                if (_closed)
                {
                    throw new ObjectDisposedException(nameof(NetconsoleTransport), "The connection was closed while it was being made.");
                }
                _stream?.Dispose();
                _socket?.Close();
                _socket = socket;
                _stream = stream;
                _configuredTarget = target;
                _acceptedSenders = _settings.AcceptOnlyFromRemoteHost ? remoteAddresses.Select(Normalize).ToArray() : null;
            }
            Decoder.Reset();
        }
        catch
        {
            socket.Close();
            throw;
        }
    }

    private static IPAddress[] ResolveAddresses(string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress literal))
        {
            return new[] { literal };
        }
        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (SocketException exception)
        {
            throw new IOException("Cannot find the device '" + host + "': " + exception.Message, exception);
        }
        if (addresses.Length == 0)
        {
            throw new IOException("Cannot find the device '" + host + "'.");
        }
        return addresses;
    }

    /// <summary>The address input goes to: one the socket can reach (an IPv4 one first, as netconsole uses IPv4 mostly).</summary>
    private static IPAddress PickAddress(IPAddress[] addresses, Socket socket)
    {
        IEnumerable<IPAddress> usable = socket.AddressFamily == AddressFamily.InterNetworkV6 && socket.DualMode
            ? addresses
            : addresses.Where(address => address.AddressFamily == socket.AddressFamily);
        IPAddress chosen = usable.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault();
        if (chosen == null)
        {
            throw new IOException("The device address " + addresses[0] + " cannot be reached from the local address the port is opened on.");
        }
        return chosen;
    }

    /// <summary>Opens the UDP port: on every interface (IPv6 and IPv4 together where possible), or on the given address.</summary>
    private Socket Bind()
    {
        int port = _settings.LocalPort;
        IPAddress localAddress = null;
        if (!string.IsNullOrWhiteSpace(_settings.LocalAddress))
        {
            string text = _settings.LocalAddress.Trim();
            if (!IPAddress.TryParse(text.Trim('[', ']'), out localAddress))
            {
                localAddress = ResolveAddresses(text).OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First();
            }
        }
        Socket socket = null;
        try
        {
            if (localAddress != null)
            {
                socket = new Socket(localAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(localAddress, port));
                return socket;
            }
            if (Socket.OSSupportsIPv6)
            {
                try
                {
                    socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
                    {
                        DualMode = true
                    };
                    socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                    return socket;
                }
                catch (SocketException exception) when (exception.SocketErrorCode != SocketError.AddressAlreadyInUse && exception.SocketErrorCode != SocketError.AccessDenied)
                {
                    // IPv6 switched off on this computer: IPv4 alone will do.
                    socket.Close();
                    socket = null;
                }
            }
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        }
        catch (SocketException exception)
        {
            socket?.Close();
            string where = localAddress != null ? localAddress + " port " + port : "port " + port;
            switch (exception.SocketErrorCode)
            {
                case SocketError.AddressAlreadyInUse:
                case SocketError.AccessDenied:
                    throw new IOException("UDP " + where + " is already in use: another program (or another tab) is listening on it.", exception);
                case SocketError.AddressNotAvailable:
                    throw new IOException(localAddress + " is not an address of this computer.", exception);
                default:
                    throw new IOException("Cannot open UDP " + where + ": " + exception.Message, exception);
            }
        }
    }

    public void Close()
    {
        Socket socket;
        NetconsoleStream stream;
        lock (_lock)
        {
            _closed = true;
            socket = _socket;
            stream = _stream;
            _socket = null;
            _stream = null;
        }
        // Closing the socket ends a receive that waits on the reader thread.
        socket?.Close();
        stream?.Dispose();
    }

    public Stream GetStream()
    {
        lock (_lock)
        {
            return _stream;
        }
    }

    public string GetDisplayName(bool includeParameters)
    {
        return _settings.ToString();
    }

    public void Dispose()
    {
        Close();
    }

    /// <summary>IPv4 addresses that arrive on a dual-mode socket as ::ffff:a.b.c.d are compared as IPv4.</summary>
    internal static IPAddress Normalize(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    /// <summary>Called by the reader for every datagram: false when it is to be dropped.</summary>
    private bool Accept(IPEndPoint sender)
    {
        IPAddress[] accepted;
        lock (_lock)
        {
            accepted = _acceptedSenders;
        }
        IPAddress address = Normalize(sender.Address);
        if (accepted != null && !accepted.Any(candidate => candidate.Equals(address)))
        {
            return false;
        }
        IPEndPoint previous = _lastSender;
        IPEndPoint current = new IPEndPoint(address, sender.Port);
        _lastSender = current;
        if (previous == null || !previous.Address.Equals(current.Address))
        {
            SenderChanged?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    /// <summary>The datagrams of the port as a stream: reads return what the decoder made of them, writes are sent as datagrams.</summary>
    private sealed class NetconsoleStream : Stream
    {
        private readonly NetconsoleTransport _owner;

        private readonly Socket _socket;

        private readonly byte[] _datagram = new byte[65536];

        // Decoded bytes that did not fit into the caller's buffer.
        private byte[] _pending = Array.Empty<byte>();

        private int _pendingOffset;

        // Line mode: what was written since the last line break.
        private readonly List<byte> _line = new List<byte>();

        public NetconsoleStream(NetconsoleTransport owner, Socket socket)
        {
            _owner = owner;
            _socket = socket;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0)
            {
                return 0;
            }
            while (_pendingOffset >= _pending.Length)
            {
                EndPoint from = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                int received;
                try
                {
                    received = _socket.ReceiveFrom(_datagram, 0, _datagram.Length, SocketFlags.None, ref from);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset || exception.SocketErrorCode == SocketError.MessageSize)
                {
                    // A "port unreachable" for input sent to a device that does not listen, or an oversized datagram:
                    // neither ends the session.
                    continue;
                }
                IPEndPoint sender = (IPEndPoint)from;
                if (received == 0 || !_owner.Accept(sender))
                {
                    continue;
                }
                _pending = _owner.Decoder.Decode(_datagram, 0, received, Normalize(sender.Address).ToString());
                _pendingOffset = 0;
            }
            int length = Math.Min(count, _pending.Length - _pendingOffset);
            Buffer.BlockCopy(_pending, _pendingOffset, buffer, offset, length);
            _pendingOffset += length;
            return length;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }
            if (_owner.InputMode == DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode.Character)
            {
                if (_line.Count > 0)
                {
                    // Switched from line mode with an unfinished line: it goes first.
                    byte[] rest = _line.ToArray();
                    _line.Clear();
                    Send(rest, 0, rest.Length);
                }
                Send(buffer, offset, count);
                return;
            }
            for (int i = 0; i < count; i++)
            {
                _line.Add(buffer[offset + i]);
            }
            int end = NetconsoleFraming.FindLineDatagramEnd(_line);
            if (end > 0)
            {
                byte[] datagram = _line.GetRange(0, end).ToArray();
                _line.RemoveRange(0, end);
                Send(datagram, 0, datagram.Length);
            }
            while (_line.Count >= MaxSendDatagram)
            {
                // A line longer than a datagram is sent in pieces rather than held back for ever.
                byte[] piece = _line.GetRange(0, MaxSendDatagram).ToArray();
                _line.RemoveRange(0, MaxSendDatagram);
                Send(piece, 0, piece.Length);
            }
        }

        private void Send(byte[] buffer, int offset, int count)
        {
            IPEndPoint target = _owner.InputTarget;
            if (target == null)
            {
                throw new IOException("There is nowhere to send the input yet: no datagram has arrived. Enter the device address in the connection settings to send before the device does.");
            }
            IPAddress address = target.Address;
            if (_socket.AddressFamily == AddressFamily.InterNetworkV6 && address.AddressFamily == AddressFamily.InterNetwork)
            {
                address = address.MapToIPv6();
            }
            else if (_socket.AddressFamily == AddressFamily.InterNetwork && address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }
            IPEndPoint destination = new IPEndPoint(address, target.Port);
            for (int sent = 0; sent < count; sent += MaxSendDatagram)
            {
                _socket.SendTo(buffer, offset + sent, Math.Min(MaxSendDatagram, count - sent), SocketFlags.None, destination);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }
}

/// <summary>How the input of line mode is cut into datagrams.</summary>
internal static class NetconsoleFraming
{
    /// <summary>
    /// How many bytes from the start of <paramref name="line"/> go out now in line mode: everything up to the last line
    /// break (CR LF stays together), or everything when a control key other than Tab was typed (Ctrl+C, an arrow or
    /// another escape sequence must not wait for Enter); 0 while a line is still being typed.
    /// </summary>
    internal static int FindLineDatagramEnd(IList<byte> line)
    {
        int end = 0;
        for (int i = 0; i < line.Count; i++)
        {
            byte b = line[i];
            if (b == (byte)'\n')
            {
                end = i + 1;
            }
            else if (b == (byte)'\r')
            {
                // A CR whose LF is still to come would split the line ending over two datagrams; the LF is written
                // together with it in practice, so a lone CR at the very end is a line ending of its own.
                end = i + 1 < line.Count && line[i + 1] == (byte)'\n' ? i + 2 : i + 1;
                if (end == i + 2)
                {
                    i++;
                }
            }
            else if ((b < 0x20 && b != (byte)'\t') || b == 0x7F)
            {
                return line.Count;
            }
        }
        return end;
    }
}
