using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SmarTTY.Transport;

// Regression harness for the launcher's local network scan (computers with an open SSH or telnet port): which addresses
// of the local networks are scanned, and what the scan reports, against real listening and closed ports on the loopback.
// Run with: dotnet run --project tests/LocalNetworkScanTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static IPAddress Ip(string text) => IPAddress.Parse(text);

    private static int Main()
    {
        try
        {
            TestPrefixLength();
            TestRanges();
            TestMultipleAdapters();
            TestAddressOrder();
            TestScanAsync().GetAwaiter().GetResult();
            TestCancellation().GetAwaiter().GetResult();
            TestSlowNameLookup().GetAwaiter().GetResult();
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            Console.WriteLine(exception);
            return 1;
        }
    }

    private static void TestPrefixLength()
    {
        Check(LocalNetworkScanner.GetPrefixLength(Ip("255.255.255.0")) == 24, "255.255.255.0 is /24");
        Check(LocalNetworkScanner.GetPrefixLength(Ip("255.255.254.0")) == 23, "255.255.254.0 is /23");
        Check(LocalNetworkScanner.GetPrefixLength(Ip("255.255.0.0")) == 16, "255.255.0.0 is /16");
        Check(LocalNetworkScanner.GetPrefixLength(Ip("255.255.255.255")) == 32, "255.255.255.255 is /32");
        Check(LocalNetworkScanner.GetPrefixLength(Ip("0.0.0.0")) == 0, "0.0.0.0 is /0");
        Check(LocalNetworkScanner.GetPrefixLength(Ip("255.0.255.0")) == -1, "a mask with a gap is not a mask");
        Check(LocalNetworkScanner.GetPrefixLength(null) == -1, "no mask");
        Check(LocalNetworkScanner.GetPrefixLength(Ip("ffff:ffff::")) == -1, "an IPv6 value is not an IPv4 mask");
    }

    private static void TestRanges()
    {
        LocalNetworkScanner.ScanRange home = LocalNetworkScanner.MakeRange(Ip("192.168.1.37"), Ip("255.255.255.0"));
        Check(home != null && home.ToString() == "192.168.1.0/24" && !home.IsNarrowed, "a /24 home network is scanned whole");
        Check(home.Hosts.Count == 253, "a /24 has 254 addresses, the computer's own is left out");
        Check(home.Hosts.First().Equals(Ip("192.168.1.1")) && home.Hosts.Last().Equals(Ip("192.168.1.254")), "neither the network nor the broadcast address is scanned");
        Check(!home.Hosts.Contains(Ip("192.168.1.37")), "the computer's own address is not scanned");

        LocalNetworkScanner.ScanRange office = LocalNetworkScanner.MakeRange(Ip("10.20.30.40"), Ip("255.255.0.0"));
        Check(office != null && office.IsNarrowed && office.ToString() == "10.20.30.0/24", "a /16 is narrowed to the /24 around the computer");
        Check(office.Hosts.Count == 253 && office.Hosts.All(host => host.ToString().StartsWith("10.20.30.", StringComparison.Ordinal)), "the narrowed range holds that /24 only");

        LocalNetworkScanner.ScanRange wide = LocalNetworkScanner.MakeRange(Ip("172.16.5.9"), Ip("255.255.252.0"));
        Check(wide != null && !wide.IsNarrowed && wide.ToString() == "172.16.4.0/22" && wide.Hosts.Count == 1021, "a /22 is still scanned whole (1022 addresses less the own one)");

        LocalNetworkScanner.ScanRange tiny = LocalNetworkScanner.MakeRange(Ip("192.168.7.1"), Ip("255.255.255.252"));
        Check(tiny != null && tiny.Hosts.Count == 1 && tiny.Hosts[0].Equals(Ip("192.168.7.2")), "a /30 has one other address");

        Check(LocalNetworkScanner.MakeRange(Ip("192.168.7.1"), Ip("255.255.255.254")) == null, "a /31 point-to-point link is not scanned");
        Check(LocalNetworkScanner.MakeRange(Ip("192.168.7.1"), Ip("255.255.255.255")) == null, "a /32 is not scanned");
        Check(LocalNetworkScanner.MakeRange(Ip("127.0.0.1"), Ip("255.0.0.0")) == null, "the loopback is not scanned");
        Check(LocalNetworkScanner.MakeRange(Ip("169.254.10.20"), Ip("255.255.0.0")) == null, "a link-local address (no DHCP answer) is not scanned");
        Check(LocalNetworkScanner.MakeRange(Ip("fe80::1"), Ip("255.255.255.0")) == null, "an IPv6 address is not scanned");
        Check(LocalNetworkScanner.MakeRange(Ip("192.168.1.10"), Ip("255.0.255.0")) == null, "an invalid mask is not scanned");
        Check(LocalNetworkScanner.MakeRange(Ip("192.168.1.10"), null) == null, "no mask, no scan");

        LocalNetworkScanner.ScanRange excluded = LocalNetworkScanner.MakeRange(Ip("192.168.1.10"), Ip("255.255.255.0"), new HashSet<IPAddress> { Ip("192.168.1.11") });
        Check(!excluded.Hosts.Contains(Ip("192.168.1.11")) && excluded.Hosts.Count == 252, "the other addresses of the computer are left out too");
    }

    private static void TestMultipleAdapters()
    {
        List<LocalNetworkScanner.ScanRange> ranges = LocalNetworkScanner.MakeRanges(new[]
        {
            new KeyValuePair<IPAddress, IPAddress>(Ip("192.168.1.10"), Ip("255.255.255.0")),
            // Wi-Fi in the same network as the cable: scanned once, and neither own address is scanned.
            new KeyValuePair<IPAddress, IPAddress>(Ip("192.168.1.11"), Ip("255.255.255.0")),
            new KeyValuePair<IPAddress, IPAddress>(Ip("169.254.1.1"), Ip("255.255.0.0")),
            new KeyValuePair<IPAddress, IPAddress>(Ip("172.17.0.1"), Ip("255.255.0.0")),
        });
        Check(ranges.Count == 2, "the same network on two adapters is scanned once, link-local not at all");
        Check(ranges[0].ToString() == "192.168.1.0/24" && ranges[0].Hosts.Count == 252, "both own addresses are left out of the shared network");
        Check(ranges[1].ToString() == "172.17.0.0/24" && ranges[1].IsNarrowed, "the wide virtual network is narrowed");

        List<KeyValuePair<IPAddress, IPAddress>> many = Enumerable.Range(0, 40)
            .Select(index => new KeyValuePair<IPAddress, IPAddress>(Ip("10." + index + ".0.1"), Ip("255.255.255.0"))).ToList();
        List<LocalNetworkScanner.ScanRange> capped = LocalNetworkScanner.MakeRanges(many);
        Check(capped.Sum(range => range.Hosts.Count) == LocalNetworkScanner.MaxTotalHosts, "all adapters together never yield more than the limit of addresses");

        Check(LocalNetworkScanner.MakeRanges(Array.Empty<KeyValuePair<IPAddress, IPAddress>>()).Count == 0, "no adapters, nothing to scan");
        // The real adapters of this machine: whatever they are, the call must not throw and must respect the limit.
        List<LocalNetworkScanner.ScanRange> local = LocalNetworkScanner.GetLocalRanges();
        Check(local.Sum(range => range.Hosts.Count) <= LocalNetworkScanner.MaxTotalHosts, "the adapters of this machine are listed within the limit (" + string.Join(", ", local) + ")");
    }

    private static void TestAddressOrder()
    {
        List<IPAddress> addresses = new List<IPAddress> { Ip("192.168.1.10"), Ip("192.168.1.2"), Ip("10.0.0.1"), Ip("192.168.1.100") };
        addresses.Sort(LocalNetworkScanner.CompareAddresses);
        Check(string.Join(" ", addresses) == "10.0.0.1 192.168.1.2 192.168.1.10 192.168.1.100", "addresses are ordered by value, not as text");
    }

    private static int FreePort()
    {
        TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task TestScanAsync()
    {
        TcpListener ssh = new TcpListener(IPAddress.Loopback, 0);
        ssh.Start();
        TcpListener telnet = new TcpListener(IPAddress.Loopback, 0);
        telnet.Start();
        int sshPort = ((IPEndPoint)ssh.LocalEndpoint).Port;
        int telnetPort = ((IPEndPoint)telnet.LocalEndpoint).Port;
        int closedPort = FreePort();
        try
        {
            Check(await LocalNetworkScanner.IsPortOpenAsync(IPAddress.Loopback, sshPort, 2000, CancellationToken.None), "a listening port is open");
            Check(!await LocalNetworkScanner.IsPortOpenAsync(IPAddress.Loopback, closedPort, 2000, CancellationToken.None), "a closed port is not open");

            LocalNetworkScanner scanner = new LocalNetworkScanner
            {
                ConnectTimeoutMs = 2000,
                NameResolver = (address, token) => Task.FromResult(address.Equals(IPAddress.Loopback) ? "loopback.test." : null)
            };
            ConcurrentBag<LocalNetworkScanner.FoundHost> found = new ConcurrentBag<LocalNetworkScanner.FoundHost>();
            ConcurrentBag<int> progress = new ConcurrentBag<int>();
            int lastTotal = 0;
            scanner.HostFound += host => found.Add(host);
            scanner.ProgressChanged += (done, total) =>
            {
                progress.Add(done);
                lastTotal = total;
            };
            // The loopback listens on two of the three ports.
            await scanner.ScanAsync(new[] { IPAddress.Loopback }, new[] { sshPort, closedPort, telnetPort }, CancellationToken.None);
            Check(found.Count == 1, "the listening computer is reported once");
            LocalNetworkScanner.FoundHost host = found.Single();
            Check(host.Address.Equals(IPAddress.Loopback), "with its address");
            Check(host.OpenPorts.SequenceEqual(new[] { sshPort, telnetPort }.OrderBy(port => port)), "with the open ports only, in order");
            Check(host.HasPort(sshPort) && host.HasPort(telnetPort) && !host.HasPort(closedPort), "HasPort tells the open ports");
            Check(host.HostName == "loopback.test", "with its name, without the trailing dot");
            Check(progress.Count == 1 && progress.Single() == 1 && lastTotal == 1, "progress is reported once per address");

            // The handler above adds to whatever bag "found" holds now.
            found = new ConcurrentBag<LocalNetworkScanner.FoundHost>();
            await scanner.ScanAsync(new[] { IPAddress.Loopback }, new[] { closedPort }, CancellationToken.None);
            Check(found.IsEmpty, "a computer without open ports is not reported");

            // Many addresses at once, fewer slots than addresses: every one is checked exactly once.
            LocalNetworkScanner busy = new LocalNetworkScanner { ConnectTimeoutMs = 300, ParallelHosts = 4, NameResolver = null };
            int checkedCount = 0;
            ConcurrentBag<LocalNetworkScanner.FoundHost> busyFound = new ConcurrentBag<LocalNetworkScanner.FoundHost>();
            busy.ProgressChanged += (done, total) => Interlocked.Increment(ref checkedCount);
            busy.HostFound += h => busyFound.Add(h);
            List<IPAddress> addresses = Enumerable.Range(1, 20).Select(index => Ip("127.0.0." + index)).ToList();
            await busy.ScanAsync(addresses, new[] { sshPort }, CancellationToken.None);
            Check(checkedCount == 20, "every address is checked once with fewer slots than addresses");
            Check(busyFound.Count >= 1 && busyFound.All(h => h.HostName == null), "without a name resolver the found hosts have no names");
            Check(busyFound.Any(h => h.Address.Equals(IPAddress.Loopback)), "the listening address is among them");
        }
        finally
        {
            ssh.Stop();
            telnet.Stop();
        }
    }

    private static async Task TestCancellation()
    {
        // 192.0.2.0/24 (TEST-NET-1) is never routed: connects there hang until the timeout, so a cancelled scan must not wait for them.
        LocalNetworkScanner scanner = new LocalNetworkScanner { ConnectTimeoutMs = 30000, ParallelHosts = 8, NameResolver = null };
        int found = 0;
        scanner.HostFound += host => Interlocked.Increment(ref found);
        List<IPAddress> addresses = Enumerable.Range(1, 64).Select(index => Ip("192.0.2." + index)).ToList();
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            Task scan = scanner.ScanAsync(addresses, new[] { 22, 23 }, cancellation.Token);
            await Task.Delay(200);
            cancellation.Cancel();
            Task finished = await Task.WhenAny(scan, Task.Delay(10000));
            Check(finished == scan, "a cancelled scan ends soon instead of waiting for the connect timeouts");
            Check(scan.Status == TaskStatus.RanToCompletion, "a cancelled scan ends without an exception");
            Check(elapsed.ElapsedMilliseconds < 10000, "within seconds (" + elapsed.ElapsedMilliseconds + " ms)");
        }
        Check(found == 0, "nothing is reported from an unrouted network");

        using (CancellationTokenSource cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Check(!await LocalNetworkScanner.IsPortOpenAsync(IPAddress.Loopback, 1, 1000, cancelled.Token), "an already cancelled check reports nothing");
        }
    }

    private static async Task TestSlowNameLookup()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            LocalNetworkScanner scanner = new LocalNetworkScanner
            {
                ConnectTimeoutMs = 2000,
                NameLookupTimeoutMs = 200,
                NameResolver = async (address, token) =>
                {
                    await Task.Delay(5000);
                    return "too-late.test";
                }
            };
            LocalNetworkScanner.FoundHost found = null;
            scanner.HostFound += host => found = host;
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            await scanner.ScanAsync(new[] { IPAddress.Loopback }, new[] { port }, CancellationToken.None);
            Check(found != null && found.HostName == null, "a name lookup that takes too long leaves the host without a name");
            Check(elapsed.ElapsedMilliseconds < 3000, "and does not hold up the scan (" + elapsed.ElapsedMilliseconds + " ms)");

            LocalNetworkScanner failing = new LocalNetworkScanner
            {
                ConnectTimeoutMs = 2000,
                NameResolver = (address, token) => Task.FromException<string>(new SocketException((int)SocketError.HostNotFound))
            };
            found = null;
            failing.HostFound += host => found = host;
            await failing.ScanAsync(new[] { IPAddress.Loopback }, new[] { port }, CancellationToken.None);
            Check(found != null && found.HostName == null, "a failed name lookup is not an error: the host is reported without a name");

            LocalNetworkScanner sameAsAddress = new LocalNetworkScanner
            {
                ConnectTimeoutMs = 2000,
                NameResolver = (address, token) => Task.FromResult(address.ToString())
            };
            found = null;
            sameAsAddress.HostFound += host => found = host;
            await sameAsAddress.ScanAsync(new[] { IPAddress.Loopback }, new[] { port }, CancellationToken.None);
            Check(found != null && found.HostName == null, "a name that is just the address is not shown as a name");
        }
        finally
        {
            listener.Stop();
        }
    }
}
