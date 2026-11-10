using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SmarTTY.Transport;

/// <summary>
/// Finds the computers of the local IPv4 networks that accept TCP connections on the SSH (22) and telnet (23) ports.
/// Each address of a network the computer is attached to gets a TCP connect that is closed at once (no SSH handshake,
/// no telnet negotiation), the same check the launcher makes for saved connections. Nothing is saved: the caller
/// decides what to do with what is found.
/// </summary>
internal sealed class LocalNetworkScanner
{
    public const int SshPort = 22;

    public const int TelnetPort = 23;

    /// <summary>A network wider than this (a /16 of an office, for instance) is scanned only around the computer's own address (its /24).</summary>
    public const int MinScannedPrefixLength = 22;

    /// <summary>The /24 that a wider network is narrowed down to.</summary>
    public const int NarrowedPrefixLength = 24;

    /// <summary>All networks together never yield more addresses than this, however many adapters the computer has.</summary>
    public const int MaxTotalHosts = 4096;

    /// <summary>
    /// A computer of the local network answers a connect within a few milliseconds, or refuses it at once; an address
    /// nobody uses does not answer at all, and is given up after this long.
    /// </summary>
    public const int DefaultConnectTimeoutMs = 700;

    /// <summary>How many addresses are checked at the same time (each with one connect per port).</summary>
    public const int DefaultParallelHosts = 64;

    /// <summary>How long the name of a found computer may be looked up (reverse DNS, NetBIOS on Windows).</summary>
    public const int DefaultNameLookupTimeoutMs = 1500;

    /// <summary>A part of a local network that is scanned.</summary>
    public sealed class ScanRange
    {
        public IPAddress Network { get; }

        public int PrefixLength { get; }

        /// <summary>The computer's own address in this network.</summary>
        public IPAddress LocalAddress { get; }

        /// <summary>True when the network is wider than <see cref="MinScannedPrefixLength"/> and only the /24 around <see cref="LocalAddress"/> is scanned.</summary>
        public bool IsNarrowed { get; }

        public IReadOnlyList<IPAddress> Hosts { get; }

        public ScanRange(IPAddress network, int prefixLength, IPAddress localAddress, bool isNarrowed, IReadOnlyList<IPAddress> hosts)
        {
            Network = network;
            PrefixLength = prefixLength;
            LocalAddress = localAddress;
            IsNarrowed = isNarrowed;
            Hosts = hosts;
        }

        public override string ToString() => Network + "/" + PrefixLength;
    }

    /// <summary>A computer that accepted a connection on at least one of the scanned ports.</summary>
    public sealed class FoundHost
    {
        public IPAddress Address { get; }

        /// <summary>The name the address resolves to, or null when it has none (or the lookup took too long).</summary>
        public string HostName { get; }

        /// <summary>The ports that accepted the connection, in ascending order.</summary>
        public IReadOnlyList<int> OpenPorts { get; }

        public FoundHost(IPAddress address, string hostName, IReadOnlyList<int> openPorts)
        {
            Address = address;
            HostName = hostName;
            OpenPorts = openPorts;
        }

        public bool HasPort(int port) => OpenPorts.Contains(port);
    }

    public int ConnectTimeoutMs { get; set; } = DefaultConnectTimeoutMs;

    public int ParallelHosts { get; set; } = DefaultParallelHosts;

    public int NameLookupTimeoutMs { get; set; } = DefaultNameLookupTimeoutMs;

    /// <summary>Looks up the name of a found address; null leaves the found hosts without names. Replaceable for tests.</summary>
    public Func<IPAddress, CancellationToken, Task<string>> NameResolver { get; set; } = ResolveNameAsync;

    /// <summary>Raised from a worker thread for every computer that accepted a connection on one of the ports.</summary>
    public event Action<FoundHost> HostFound;

    /// <summary>Raised from a worker thread after each address has been checked: (addresses checked, all addresses).</summary>
    public event Action<int, int> ProgressChanged;

    /// <summary>
    /// The number of bits set in <paramref name="mask"/>, or -1 when it is not an IPv4 network mask (bits that are not contiguous).
    /// </summary>
    public static int GetPrefixLength(IPAddress mask)
    {
        if (mask == null || mask.AddressFamily != AddressFamily.InterNetwork)
        {
            return -1;
        }
        uint value = ToUInt32(mask);
        int length = 0;
        while (length < 32 && (value & (0x80000000u >> length)) != 0)
        {
            length++;
        }
        // Every bit after the first zero must be zero as well.
        uint expected = length == 0 ? 0u : uint.MaxValue << (32 - length);
        return value == expected ? length : -1;
    }

    /// <summary>
    /// The addresses to scan in the network of <paramref name="localAddress"/> with the mask <paramref name="mask"/>:
    /// all of them but the network address, the broadcast address and the addresses in <paramref name="exclude"/>
    /// (the computer's own). A network wider than <see cref="MinScannedPrefixLength"/> is narrowed to the /24 around
    /// the local address. Returns null for what is not worth scanning: no IPv4 address, loopback, link-local
    /// (169.254.x.x, an adapter that got no address), and point-to-point links (/31, /32).
    /// </summary>
    public static ScanRange MakeRange(IPAddress localAddress, IPAddress mask, ICollection<IPAddress> exclude = null)
    {
        if (localAddress == null || localAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }
        byte[] bytes = localAddress.GetAddressBytes();
        if (bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254) || bytes[0] == 0 || bytes[0] >= 224)
        {
            return null;
        }
        int prefixLength = GetPrefixLength(mask);
        if (prefixLength < 0 || prefixLength > 30)
        {
            return null;
        }
        bool narrowed = false;
        if (prefixLength < MinScannedPrefixLength)
        {
            prefixLength = NarrowedPrefixLength;
            narrowed = true;
        }
        uint networkMask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        uint local = ToUInt32(localAddress);
        uint network = local & networkMask;
        uint broadcast = network | ~networkMask;
        List<IPAddress> hosts = new List<IPAddress>();
        for (uint value = network + 1; value < broadcast; value++)
        {
            IPAddress host = FromUInt32(value);
            if (exclude != null && exclude.Contains(host))
            {
                continue;
            }
            if (value == local)
            {
                continue;
            }
            hosts.Add(host);
        }
        return new ScanRange(FromUInt32(network), prefixLength, localAddress, narrowed, hosts);
    }

    /// <summary>
    /// The IPv4 networks of the adapters that are up (not the loopback and not tunnels), each one once, together with no
    /// more than <see cref="MaxTotalHosts"/> addresses. The addresses of the computer itself are never scanned.
    /// </summary>
    public static List<ScanRange> GetLocalRanges()
    {
        List<KeyValuePair<IPAddress, IPAddress>> addresses = new List<KeyValuePair<IPAddress, IPAddress>>();
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return new List<ScanRange>();
        }
        foreach (NetworkInterface adapter in adapters)
        {
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback || adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }
                foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.IPv4Mask != null)
                    {
                        addresses.Add(new KeyValuePair<IPAddress, IPAddress>(unicast.Address, unicast.IPv4Mask));
                    }
                }
            }
            catch (Exception exception) when (exception is NetworkInformationException || exception is PlatformNotSupportedException || exception is NotImplementedException)
            {
                // An adapter that cannot tell its addresses (a driver of a VPN, for instance) is left out, not the whole scan.
            }
        }
        return MakeRanges(addresses);
    }

    /// <summary>The ranges of the given (address, mask) pairs: see <see cref="GetLocalRanges"/>.</summary>
    public static List<ScanRange> MakeRanges(IEnumerable<KeyValuePair<IPAddress, IPAddress>> addressesWithMasks)
    {
        List<KeyValuePair<IPAddress, IPAddress>> all = addressesWithMasks.ToList();
        HashSet<IPAddress> own = new HashSet<IPAddress>(all.Select(pair => pair.Key));
        List<ScanRange> result = new List<ScanRange>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        int total = 0;
        foreach (KeyValuePair<IPAddress, IPAddress> pair in all)
        {
            ScanRange range = MakeRange(pair.Key, pair.Value, own);
            // Two adapters in the same network (cable and Wi-Fi) are scanned once.
            if (range == null || range.Hosts.Count == 0 || !seen.Add(range.ToString()))
            {
                continue;
            }
            if (total + range.Hosts.Count > MaxTotalHosts)
            {
                int left = MaxTotalHosts - total;
                if (left <= 0)
                {
                    break;
                }
                range = new ScanRange(range.Network, range.PrefixLength, range.LocalAddress, true, range.Hosts.Take(left).ToList());
            }
            total += range.Hosts.Count;
            result.Add(range);
        }
        return result;
    }

    /// <summary>
    /// Checks every address of <paramref name="hosts"/> on every port of <paramref name="ports"/> and raises
    /// <see cref="HostFound"/> for each address where at least one port accepted the connection. Completes when all
    /// addresses are checked, or (without an exception) soon after <paramref name="cancellation"/> is cancelled.
    /// </summary>
    public async Task ScanAsync(IReadOnlyList<IPAddress> hosts, IReadOnlyList<int> ports, CancellationToken cancellation)
    {
        if (hosts == null)
        {
            throw new ArgumentNullException(nameof(hosts));
        }
        if (ports == null || ports.Count == 0)
        {
            throw new ArgumentException("No ports to scan.", nameof(ports));
        }
        int total = hosts.Count;
        int done = 0;
        using (SemaphoreSlim slots = new SemaphoreSlim(Math.Max(1, ParallelHosts)))
        {
            List<Task> running = new List<Task>(total);
            foreach (IPAddress host in hosts)
            {
                try
                {
                    await slots.WaitAsync(cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        await CheckHostAsync(host, ports, cancellation).ConfigureAwait(false);
                    }
                    finally
                    {
                        slots.Release();
                        int checkedCount = Interlocked.Increment(ref done);
                        if (!cancellation.IsCancellationRequested)
                        {
                            ProgressChanged?.Invoke(checkedCount, total);
                        }
                    }
                }));
            }
            await Task.WhenAll(running).ConfigureAwait(false);
        }
    }

    private async Task CheckHostAsync(IPAddress host, IReadOnlyList<int> ports, CancellationToken cancellation)
    {
        Task<bool>[] probes = ports.Select(port => IsPortOpenAsync(host, port, ConnectTimeoutMs, cancellation)).ToArray();
        bool[] open = await Task.WhenAll(probes).ConfigureAwait(false);
        List<int> openPorts = new List<int>();
        for (int index = 0; index < open.Length; index++)
        {
            if (open[index])
            {
                openPorts.Add(ports[index]);
            }
        }
        if (openPorts.Count == 0 || cancellation.IsCancellationRequested)
        {
            return;
        }
        openPorts.Sort();
        string name = await LookUpNameAsync(host, cancellation).ConfigureAwait(false);
        if (cancellation.IsCancellationRequested)
        {
            return;
        }
        HostFound?.Invoke(new FoundHost(host, name, openPorts));
    }

    private async Task<string> LookUpNameAsync(IPAddress host, CancellationToken cancellation)
    {
        Func<IPAddress, CancellationToken, Task<string>> resolver = NameResolver;
        if (resolver == null || NameLookupTimeoutMs <= 0)
        {
            return null;
        }
        try
        {
            Task<string> lookup = resolver(host, cancellation);
            Task finished = await Task.WhenAny(lookup, Task.Delay(NameLookupTimeoutMs, cancellation)).ConfigureAwait(false);
            if (finished != lookup)
            {
                Observe(lookup);
                return null;
            }
            string name = await lookup.ConfigureAwait(false);
            // A name that is the address itself tells nothing.
            return string.IsNullOrWhiteSpace(name) || name == host.ToString() ? null : name.TrimEnd('.');
        }
        catch
        {
            // No name is not an error: the address is shown instead.
            return null;
        }
    }

    private static async Task<string> ResolveNameAsync(IPAddress address, CancellationToken cancellation)
    {
        IPHostEntry entry = await Dns.GetHostEntryAsync(address).ConfigureAwait(false);
        return entry?.HostName;
    }

    /// <summary>
    /// Whether <paramref name="address"/> accepts a TCP connection on <paramref name="port"/> within
    /// <paramref name="timeoutMs"/>; the connection is closed at once.
    /// </summary>
    public static async Task<bool> IsPortOpenAsync(IPAddress address, int port, int timeoutMs, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return false;
        }
        Socket socket;
        try
        {
            socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        }
        catch (SocketException)
        {
            return false;
        }
        using (socket)
        {
            Task connect;
            try
            {
                // Begin/End rather than Socket.ConnectAsync: the same code then runs on .NET Framework and on .NET.
                connect = Task.Factory.FromAsync(socket.BeginConnect, socket.EndConnect, address, port, null);
            }
            catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException)
            {
                return false;
            }
            Task finished;
            try
            {
                finished = await Task.WhenAny(connect, Task.Delay(timeoutMs, cancellation)).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                finished = null;
            }
            if (finished != connect)
            {
                // Disposing the socket ends the connect; its error is observed, so it does not surface later.
                Observe(connect);
                return false;
            }
            try
            {
                await connect.ConfigureAwait(false);
                bool connected = socket.Connected;
                try
                {
                    socket.Shutdown(SocketShutdown.Both);
                }
                catch (SocketException)
                {
                }
                return connected;
            }
            catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException || exception is InvalidOperationException)
            {
                return false;
            }
        }
    }

    private static void Observe(Task task)
    {
        task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Orders IPv4 addresses by their numeric value (192.168.1.2 before 192.168.1.10).</summary>
    public static int CompareAddresses(IPAddress x, IPAddress y)
    {
        if (x.AddressFamily == AddressFamily.InterNetwork && y.AddressFamily == AddressFamily.InterNetwork)
        {
            return ToUInt32(x).CompareTo(ToUInt32(y));
        }
        return string.CompareOrdinal(x.ToString(), y.ToString());
    }

    private static uint ToUInt32(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value)
    {
        return new IPAddress(new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
    }
}
