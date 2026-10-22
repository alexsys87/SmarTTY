using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace SmarTTY.Transport;

/// <summary>
/// Opens the TCP connection of the raw TCP and telnet transports. Every address the host name resolves to is tried in
/// turn, each with a socket of its own family: <c>new TcpClient()</c> of .NET Framework creates an IPv4 socket up front
/// and therefore can never reach an IPv6 address. A connect that is under way can be aborted from another thread
/// (<see cref="Abort"/>), so closing a tab does not have to wait for the operating system's connect timeout.
/// </summary>
internal sealed class TcpConnector
{
    /// <summary>The longest keep-alive time that is passed to the operating system (one day).</summary>
    public const int MaxKeepAliveSeconds = 24 * 60 * 60;

    private readonly object _lock = new object();

    private Socket _pending;

    private volatile bool _aborted;

    /// <summary>
    /// Connects to <paramref name="host"/> (a name, an IPv4 or an IPv6 address) and returns the connected socket with
    /// Nagle's algorithm switched off: a terminal sends single key presses that must not wait for an acknowledgement.
    /// <paramref name="timeoutMs"/> limits the whole attempt over all addresses; 0 leaves it to the operating system.
    /// </summary>
    public Socket Connect(string host, int port, int timeoutMs = 0)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("No host name is given.");
        }
        if (port <= 0 || port > 65535)
        {
            throw new ArgumentException("Invalid port number: " + port + " (expected 1-65535).");
        }
        Stopwatch elapsed = Stopwatch.StartNew();
        host = host.Trim();
        if (host.StartsWith("[", StringComparison.Ordinal) && host.EndsWith("]", StringComparison.Ordinal))
        {
            host = host.Substring(1, host.Length - 2);
        }
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress literal) ? new[] { literal } : Dns.GetHostAddresses(host);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }
        Exception lastError = null;
        bool timedOut = false;
        foreach (IPAddress address in OrderAddresses(addresses))
        {
            int remainingMs = -1;
            if (timeoutMs > 0)
            {
                remainingMs = timeoutMs - (int)elapsed.ElapsedMilliseconds;
                if (remainingMs <= 0)
                {
                    timedOut = true;
                    break;
                }
            }
            Socket socket;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            }
            catch (SocketException ex)
            {
                // This computer has no stack for the family (IPv6 switched off): try the next address.
                lastError = ex;
                continue;
            }
            lock (_lock)
            {
                if (_aborted)
                {
                    socket.Close();
                    throw new ObjectDisposedException(nameof(TcpConnector), "The connection was closed while it was being made.");
                }
                _pending = socket;
            }
            try
            {
                socket.NoDelay = true;
                IAsyncResult connect = socket.BeginConnect(new IPEndPoint(address, port), null, null);
                if (!connect.AsyncWaitHandle.WaitOne(remainingMs))
                {
                    timedOut = true;
                    throw new SocketException((int)SocketError.TimedOut);
                }
                socket.EndConnect(connect);
                lock (_lock)
                {
                    _pending = null;
                    if (_aborted)
                    {
                        throw new ObjectDisposedException(nameof(TcpConnector), "The connection was closed while it was being made.");
                    }
                }
                return socket;
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _pending = null;
                }
                socket.Close();
                if (_aborted)
                {
                    throw new ObjectDisposedException(nameof(TcpConnector), "The connection was closed while it was being made.");
                }
                lastError = ex;
            }
        }
        if (timedOut || lastError == null)
        {
            throw new TimeoutException("No answer from " + DescribeEndpoint(host, port) + " within " + (timeoutMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s.");
        }
        throw lastError;
    }

    /// <summary>Closes the socket that is being connected (from any thread); the pending <see cref="Connect"/> throws.</summary>
    public void Abort()
    {
        Socket pending;
        lock (_lock)
        {
            _aborted = true;
            pending = _pending;
            _pending = null;
        }
        try
        {
            pending?.Close();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Switches on the TCP keep-alive of the operating system with the given idle time instead of its default (two
    /// hours on Windows), so a connection whose far end vanished (power loss, a pulled cable, a NAT timeout) is
    /// noticed after about <paramref name="seconds"/> plus a few probes. Nothing is written into the data stream, so
    /// the device protocol is not disturbed. 0 or less does nothing.
    /// </summary>
    public static void EnableKeepAlive(Socket socket, int seconds)
    {
        if (socket == null || seconds <= 0)
        {
            return;
        }
        seconds = Math.Min(seconds, MaxKeepAliveSeconds);
        // The probes after the first unanswered one: often enough to find a dead link soon, rarely enough to be harmless.
        int intervalSeconds = Math.Max(1, Math.Min(seconds, 5));
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        try
        {
            // struct tcp_keepalive { u_long onoff; u_long keepalivetime; u_long keepaliveinterval; }, in milliseconds.
            byte[] values = new byte[12];
            BitConverter.GetBytes(1u).CopyTo(values, 0);
            BitConverter.GetBytes((uint)seconds * 1000u).CopyTo(values, 4);
            BitConverter.GetBytes((uint)intervalSeconds * 1000u).CopyTo(values, 8);
            socket.IOControl(IOControlCode.KeepAliveValues, values, null);
            return;
        }
        catch (Exception ex) when (ex is SocketException || ex is PlatformNotSupportedException || ex is NotSupportedException)
        {
        }
        try
        {
            // TCP_KEEPIDLE / TCP_KEEPINTVL (in seconds) where SIO_KEEPALIVE_VALS is not available.
            socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)3, seconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)17, intervalSeconds);
        }
        catch (Exception ex) when (ex is SocketException || ex is PlatformNotSupportedException || ex is NotSupportedException)
        {
            // The operating system's own timing stays in force; the keep-alive itself is on.
        }
    }

    /// <summary>"host:port", with an IPv6 address in brackets so the port can be told from it.</summary>
    private static string DescribeEndpoint(string host, int port)
    {
        return (host != null && host.Contains(":") && !host.StartsWith("[", StringComparison.Ordinal) ? "[" + host + "]" : host) + ":" + port;
    }

    /// <summary>
    /// The resolver's order (the operating system's preference), but alternating between the families after the
    /// first address, so a broken IPv6 route does not delay an IPv4 address that works behind several IPv6 ones.
    /// </summary>
    private static IEnumerable<IPAddress> OrderAddresses(IPAddress[] addresses)
    {
        if (addresses.Length <= 2)
        {
            return addresses;
        }
        AddressFamily first = addresses[0].AddressFamily;
        Queue<IPAddress> preferred = new Queue<IPAddress>();
        Queue<IPAddress> other = new Queue<IPAddress>();
        foreach (IPAddress address in addresses)
        {
            (address.AddressFamily == first ? preferred : other).Enqueue(address);
        }
        List<IPAddress> ordered = new List<IPAddress>(addresses.Length);
        while (preferred.Count > 0 || other.Count > 0)
        {
            if (preferred.Count > 0)
            {
                ordered.Add(preferred.Dequeue());
            }
            if (other.Count > 0)
            {
                ordered.Add(other.Dequeue());
            }
        }
        return ordered;
    }
}
