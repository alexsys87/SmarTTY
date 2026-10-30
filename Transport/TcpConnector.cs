using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>How long one connect may take, over all addresses, unless a transport says otherwise.</summary>
    public const int DefaultConnectTimeoutMs = 10000;

    private readonly object _lock = new object();

    private Socket _pending;

    private volatile bool _aborted;

    // Set by Abort(): wakes up a name lookup that is being waited for.
    private readonly ManualResetEvent _abortSignal = new ManualResetEvent(false);

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
        IPAddress[] addresses = ParseLiteral(host, out IPAddress literal) ? new[] { literal } : Resolve(host, timeoutMs, elapsed);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }
        // The last answer that was an error from the address itself (refused, unreachable), and whether the attempts
        // ended without any answer. A refusal tells the user more than "no answer" from another address.
        Exception lastError = null;
        Exception timeoutError = null;
        bool timedOut = false;
        List<IPAddress> ordered = new List<IPAddress>(OrderAddresses(addresses));
        for (int index = 0; index < ordered.Count; index++)
        {
            IPAddress address = ordered[index];
            int remainingMs = -1;
            int attemptMs = -1;
            if (timeoutMs > 0)
            {
                remainingMs = timeoutMs - (int)elapsed.ElapsedMilliseconds;
                if (remainingMs <= 0)
                {
                    timedOut = true;
                    break;
                }
                // An address that does not answer gets its share only, so the ones behind it (an IPv4 address behind a
                // dead IPv6 route) are still tried; the last address may use whatever is left.
                attemptMs = remainingMs / (ordered.Count - index);
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
                if (!connect.AsyncWaitHandle.WaitOne(attemptMs))
                {
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
                if (ex is SocketException { SocketErrorCode: SocketError.TimedOut })
                {
                    timedOut = true;
                    timeoutError = ex;
                }
                else
                {
                    lastError = ex;
                }
            }
        }
        if (lastError == null)
        {
            if (timeoutMs <= 0 && timeoutError != null)
            {
                throw timeoutError; // the operating system's own timeout
            }
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
            _abortSignal.Set();
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
    /// noticed after about <paramref name="seconds"/> plus the probes (Windows sends a fixed ten of them, one per
    /// interval, so up to about seconds + 10 x interval). Nothing is written into the data stream, so
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
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
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
        }
        try
        {
            // TCP_KEEPIDLE / TCP_KEEPINTVL (in seconds) where SIO_KEEPALIVE_VALS is not available.
            // The option numbers differ per system: 3 and 17 are TCP_KEEPIDLE and TCP_KEEPINTVL on Windows only
            // (on Linux they are TCP_CORK and TCP_THIN_DUPACK, which must not be touched).
            SocketOptionName idle;
            SocketOptionName interval;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                idle = (SocketOptionName)3;
                interval = (SocketOptionName)17;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                idle = (SocketOptionName)0x10; // TCP_KEEPALIVE
                interval = (SocketOptionName)0x101; // TCP_KEEPINTVL
            }
            else
            {
                idle = (SocketOptionName)4; // TCP_KEEPIDLE
                interval = (SocketOptionName)5; // TCP_KEEPINTVL
            }
            socket.SetSocketOption(SocketOptionLevel.Tcp, idle, seconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, interval, intervalSeconds);
        }
        catch (Exception ex) when (ex is SocketException || ex is PlatformNotSupportedException || ex is NotSupportedException)
        {
            // The operating system's own timing stays in force; the keep-alive itself is on.
        }
    }

    /// <summary>
    /// An IP address written out in full. <c>IPAddress.TryParse</c> also takes "1" (0.0.0.1) and "192.168.1"
    /// (192.168.0.1), which are typos and not addresses; a host of digits and dots that is not four numbers is refused
    /// (a name cannot consist of digits only), and anything else that is not an address is a name for the resolver.
    /// </summary>
    internal static bool ParseLiteral(string host, out IPAddress address)
    {
        address = null;
        if (host.IndexOf(':') >= 0)
        {
            return IPAddress.TryParse(host, out address);
        }
        bool digitsAndDots = host.Length > 0;
        foreach (char c in host)
        {
            digitsAndDots &= c == '.' || (c >= '0' && c <= '9');
        }
        if (!digitsAndDots)
        {
            return false;
        }
        string[] parts = host.Split('.');
        if (parts.Length != 4 || !IPAddress.TryParse(host, out address))
        {
            throw new ArgumentException("\"" + host + "\" is not a valid IPv4 address (four numbers 0-255 are expected).");
        }
        return true;
    }

    /// <summary>
    /// Looks the name up without blocking longer than the connect timeout (the resolver itself has none) and without
    /// outliving <see cref="Abort"/>: a lookup that is still running is left to finish on its own.
    /// </summary>
    private IPAddress[] Resolve(string host, int timeoutMs, Stopwatch elapsed)
    {
        Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(host);
        // Nobody may be left to observe a failure of a lookup that was given up.
        lookup.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        int waitMs = timeoutMs > 0 ? Math.Max(0, timeoutMs - (int)elapsed.ElapsedMilliseconds) : Timeout.Infinite;
        int signaled = WaitHandle.WaitAny(new[] { ((IAsyncResult)lookup).AsyncWaitHandle, _abortSignal }, waitMs);
        if (signaled == 1 || _aborted)
        {
            throw new ObjectDisposedException(nameof(TcpConnector), "The connection was closed while it was being made.");
        }
        if (signaled == WaitHandle.WaitTimeout)
        {
            throw new TimeoutException("The name " + host + " could not be resolved within " + (timeoutMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s.");
        }
        return lookup.GetAwaiter().GetResult();
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
