using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SmarTTY.Ssh;
using SmarTTY.Transport;
using SmarTTY.Utilities;
using VisualTTY;

// Regression harness for the saved-connection lookup used by the command line (Transport/SavedConnectionStore.cs):
//  * splitting a "user@host" argument the way ssh accepts it,
//  * resolving it to a saved connection by user and host, or by host alone,
//  * matching a host name that carries a port number.
// Run with: dotnet run --project tests/ConnectionLookupTests
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

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private static int Main()
    {
        try
        {
            CheckSplitting();
            CheckLookup();
            CheckHostOnlyFallback();
            CheckHostWithPort();
            CheckHostPortSplitting();
            CheckIpv6Hosts();
            CheckProxyParsing();
            CheckPortForwardsInProfile();
            CheckRawProfilesAreNotDuplicated();
            CheckSameUserAndHostAsksFirst();
            CheckRenamedProfileReplacesTheOldOne();
            CheckProfileFilesAreWrittenSafely();
            CheckUnreadableProfileIsKept();
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
    }

    // Tools > Port forwarding saves its list with the profile; a profile of an earlier version has none.
    private static void CheckPortForwardsInProfile()
    {
        SSHConnectionSettings settings = new SSHConnectionSettings { HostName = "server", UserName = "root" };
        settings.PortForwards.Add(new PortForwardRule { Kind = PortForwardKind.Local, BindAddress = "127.0.0.1", BindPort = 8080, TargetHost = "db.internal", TargetPort = 5432, StartOnConnect = true, Description = "database" });
        settings.PortForwards.Add(new PortForwardRule { Kind = PortForwardKind.Remote, BindAddress = "127.0.0.1", BindPort = 9000, TargetHost = "localhost", TargetPort = 3000 });
        string xml = XmlStorage.Serialize(settings);
        SSHConnectionSettings read = XmlStorage.Deserialize<SSHConnectionSettings>(xml);
        Check(read.PortForwards.Count == 2, "forwardings: both are read back from the profile");
        PortForwardRule local = read.PortForwards[0];
        Check(local.Kind == PortForwardKind.Local && local.BindPort == 8080 && local.TargetHost == "db.internal" && local.TargetPort == 5432 && local.StartOnConnect && local.Description == "database",
            "forwardings: a local forwarding keeps every field");
        Check(read.PortForwards[1].Kind == PortForwardKind.Remote && read.PortForwards[1].SshOption == "-R 127.0.0.1:9000:localhost:3000", "forwardings: a remote forwarding keeps its direction");
        Check(!xml.Contains("SshOption") && !xml.Contains("ListenText"), "forwardings: only the settings are saved, not the texts made from them");
        SSHConnectionSettings old = XmlStorage.Deserialize<SSHConnectionSettings>("<?xml version=\"1.0\"?><SSHConnectionSettings><HostName>h</HostName></SSHConnectionSettings>");
        Check(old.PortForwards != null && old.PortForwards.Count == 0, "forwardings: a profile of an earlier version has an empty list");
        // The removed "do not warn about a changed host key" setting must not stop an old profile from loading.
        SSHConnectionSettings withRemovedSetting = XmlStorage.Deserialize<SSHConnectionSettings>("<?xml version=\"1.0\"?><SSHConnectionSettings><HostName>h</HostName><SuppressKeyMismatchWarning>true</SuppressKeyMismatchWarning></SSHConnectionSettings>");
        Check(withRemovedSetting.HostName == "h" && !XmlStorage.Serialize(withRemovedSetting).Contains("SuppressKeyMismatchWarning"), "profile: the removed host key warning setting is read without error and not written again");
    }

    private static void CheckSplitting()
    {
        Check(SavedConnectionStore.TrySplitUserHost("root@10.0.0.5", out string user, out string host), "split: user@host is accepted");
        Equal(user, "root", "split: user name");
        Equal(host, "10.0.0.5", "split: host name");
        Check(!SavedConnectionStore.TrySplitUserHost("server.example.com", out _, out _), "split: a plain name is rejected");
        Check(!SavedConnectionStore.TrySplitUserHost("@host", out _, out _), "split: a missing user name is rejected");
        Check(!SavedConnectionStore.TrySplitUserHost("user@", out _, out _), "split: a missing host name is rejected");
        Check(!SavedConnectionStore.TrySplitUserHost(null, out _, out _), "split: null is rejected");
        // The first '@' is the separator, so a host that itself contains '@' keeps it.
        Check(SavedConnectionStore.TrySplitUserHost("user@domain@host", out string domainUser, out string domainHost), "split: two '@' are accepted");
        Equal(domainUser, "user", "split: the first '@' separates");
        Equal(domainHost, "domain@host", "split: the rest is the host");
    }

    private static void CheckLookup()
    {
        using TempStore store = new TempStore();
        store.Add("root", "10.0.0.5", alias: null);

        Equal(store.Store.FindSshConnection("10.0.0.5", "root")?.HostName, "10.0.0.5", "lookup: exact user and host");
        Equal(store.Store.FindSshConnection("10.0.0.5", "ROOT")?.HostName, "10.0.0.5", "lookup: user name is case-insensitive");
        Equal(store.Store.FindSshConnection("10.0.0.5", "admin"), null, "lookup: a different user does not match");
        Equal(store.Store.FindSshConnection("10.0.0.6", "root"), null, "lookup: a different host does not match");

        Equal(store.Store.FindSshConnectionForArgument("root@10.0.0.5")?.HostName, "10.0.0.5", "argument: exact user and host");
        Equal(store.Store.FindSshConnectionForArgument("server.example.com"), null, "argument: a plain name is not looked up by host here");
    }

    private static void CheckHostOnlyFallback()
    {
        using TempStore store = new TempStore();
        store.Add("root", "10.0.0.7", alias: null);

        // "smartty.exe admin@host" still finds the one connection to that host, like "ssh host" picks the stored user.
        Equal(store.Store.FindSshConnectionForArgument("admin@10.0.0.7")?.HostName, "10.0.0.7", "fallback: any user finds the host");
        Equal(store.Store.FindSshConnectionForArgument("admin@10.0.0.8"), null, "fallback: an unknown host stays unknown");
    }

    private static void CheckHostWithPort()
    {
        using TempStore store = new TempStore();
        store.Add("root", "10.0.0.9:2222", alias: null);

        // The port number is part of the host field: the exact lookup compares it, but the host-only fallback
        // (FindByHost, which strips the port) still resolves the bare host name typed on the command line.
        Equal(store.Store.FindSshConnection("10.0.0.9:2222", "root")?.HostName, "10.0.0.9:2222", "port: the exact lookup needs the port");
        Equal(store.Store.FindSshConnection("10.0.0.9", "root"), null, "port: the exact lookup does not strip it");
        Equal(store.Store.FindByHost("10.0.0.9").Count, 1, "port: a host with a port matches its bare host");
        Equal(store.Store.FindSshConnectionForArgument("root@10.0.0.9")?.HostName, "10.0.0.9:2222", "port: the argument fallback strips it");
    }

    private static void CheckSplit(string text, string expectedHost, int? expectedPort)
    {
        string host = SSHConnectionSettings.SplitHostPort(text, out int? port);
        Equal(host, expectedHost, "split port: host of '" + text + "'");
        Equal(port, expectedPort, "split port: port of '" + text + "'");
    }

    // The host field is "host", "host:port", "[ipv6]" or "[ipv6]:port". A bare IPv6 address has colons of its own and
    // never carries a port; text that only looks like a port is left alone, so that a typo fails loudly.
    private static void CheckHostPortSplitting()
    {
        CheckSplit("host", "host", null);
        CheckSplit("host.example.com:2222", "host.example.com", 2222);
        CheckSplit("10.0.0.9:22", "10.0.0.9", 22);
        CheckSplit("[::1]:2222", "::1", 2222);
        CheckSplit("[2001:db8::7]:22", "2001:db8::7", 22);
        CheckSplit("[::1]", "::1", null);
        CheckSplit("::1", "::1", null);
        CheckSplit("2001:db8::1", "2001:db8::1", null);
        CheckSplit("fe80:0:0:0:0:0:0:1", "fe80:0:0:0:0:0:0:1", null);
        CheckSplit("host:abc", "host:abc", null);
        CheckSplit("host:", "host:", null);
        CheckSplit("host:0", "host:0", null);
        CheckSplit("host:65536", "host:65536", null);
        CheckSplit("host:-1", "host:-1", null);
        CheckSplit("[::1]:abc", "[::1]:abc", null);
        CheckSplit("[::1", "[::1", null);
        CheckSplit("[::1]x", "[::1]x", null);
        CheckSplit("", "", null);
        CheckSplit(null, null, null);
    }

    private static void CheckProxy(string text, string expectedCredentials, string expectedHost, int expectedPort)
    {
        bool parsed = SSHConnectionSettings.TryParseProxy(text, out string credentials, out string host, out int port);
        Check(parsed, "proxy: '" + text + "' is accepted");
        Equal(credentials, expectedCredentials, "proxy: credentials of '" + text + "'");
        Equal(host, expectedHost, "proxy: host of '" + text + "'");
        Equal(port, expectedPort, "proxy: port of '" + text + "'");
    }

    private static void CheckProxyRejected(string text)
    {
        Check(!SSHConnectionSettings.TryParseProxy(text, out string credentials, out string host, out int port) && credentials == null && host == null && port == 0,
            "proxy: '" + (text ?? "null") + "' is rejected and leaves nothing behind");
    }

    // "[user:password@]host:port": the credentials end at the last '@', the host may be a bracketed IPv6 address, and a
    // proxy without a valid port is rejected instead of being dropped (the connection would go out without it).
    private static void CheckProxyParsing()
    {
        CheckProxy("proxy.example.com:3128", null, "proxy.example.com", 3128);
        CheckProxy("10.0.0.1:8080", null, "10.0.0.1", 8080);
        CheckProxy("  proxy:3128  ", null, "proxy", 3128);
        CheckProxy("user:secret@proxy:3128", "user:secret", "proxy", 3128);
        CheckProxy("user:p@ss@proxy:3128", "user:p@ss", "proxy", 3128);
        CheckProxy("user@proxy:3128", "user", "proxy", 3128);
        CheckProxy("[2001:db8::3]:3128", null, "2001:db8::3", 3128);
        CheckProxy("u:p@[::1]:8080", "u:p", "::1", 8080);
        CheckProxy("@proxy:3128", null, "proxy", 3128);
        CheckProxyRejected("proxy.example.com");
        CheckProxyRejected("proxy:abc");
        CheckProxyRejected("proxy:0");
        CheckProxyRejected("user:secret@proxy");
        CheckProxyRejected("2001:db8::3");
        CheckProxyRejected("[::1]");
        CheckProxyRejected(":3128");
        CheckProxyRejected("user@:3128");
        CheckProxyRejected("");
        CheckProxyRejected("   ");
        CheckProxyRejected(null);

        // What the window shows for the saved settings reads back as the same settings.
        Equal(SSHConnectionSettings.FormatProxy(null, "proxy.example.com", 3128), "proxy.example.com:3128", "format: host and port");
        Equal(SSHConnectionSettings.FormatProxy("user:secret", "proxy", 3128), "user:secret@proxy:3128", "format: with credentials");
        Equal(SSHConnectionSettings.FormatProxy("", "proxy", 3128), "proxy:3128", "format: empty credentials are left out");
        Equal(SSHConnectionSettings.FormatProxy(null, "2001:db8::3", 3128), "[2001:db8::3]:3128", "format: an IPv6 address gets brackets");
        Equal(SSHConnectionSettings.FormatProxy(null, "proxy", 0), "proxy", "format: no valid port leaves the host alone");
        Equal(SSHConnectionSettings.FormatProxy("u:p", null, 3128), null, "format: no proxy gives no text");
        Equal(SSHConnectionSettings.FormatProxy("u:p", "", 3128), null, "format: an empty host gives no text");
        foreach ((string credentials, string host, int port) in new[]
        {
            ((string)null, "proxy.example.com", 3128),
            ("user:secret", "proxy", 8080),
            ("user:p@ss", "10.0.0.1", 1),
            ((string)null, "2001:db8::3", 65535),
            ("u:p", "::1", 3128),
        })
        {
            string text = SSHConnectionSettings.FormatProxy(credentials, host, port);
            bool parsed = SSHConnectionSettings.TryParseProxy(text, out string parsedCredentials, out string parsedHost, out int parsedPort);
            Check(parsed && parsedCredentials == credentials && parsedHost == host && parsedPort == port, "format: '" + text + "' reads back unchanged");
        }

        // A proxy URL as VPN clients (xray, v2ray, sing-box) show it: the scheme selects the proxy type.
        CheckProxyScheme("socks5://127.0.0.1:10808", SshProxyType.Socks5, null, "127.0.0.1", 10808);
        CheckProxyScheme("SOCKS5H://user:secret@localhost:1080/", SshProxyType.Socks5, "user:secret", "localhost", 1080);
        CheckProxyScheme("socks://[::1]:1080", SshProxyType.Socks5, null, "::1", 1080);
        CheckProxyScheme("http://proxy:3128", SshProxyType.Http, null, "proxy", 3128);
        CheckProxyScheme("https://user:secret@proxy.example.com:443", SshProxyType.Https, "user:secret", "proxy.example.com", 443);
        CheckProxyScheme("socks4://10.0.0.1:1080", SshProxyType.Socks4, null, "10.0.0.1", 1080);
        CheckProxyScheme("socks4a://ident@proxy:1080", SshProxyType.Socks4, "ident", "proxy", 1080);
        CheckProxyScheme("127.0.0.1:10808", null, null, "127.0.0.1", 10808);
        Check(!SSHConnectionSettings.TryParseProxy("ftp://127.0.0.1:21", out SshProxyType? unsupported, out _, out _, out _) && unsupported == null, "proxy: an unsupported scheme is rejected");
        Check(!SSHConnectionSettings.TryParseProxy("socks5://127.0.0.1", out SshProxyType? noPort, out _, out _, out _) && noPort == null, "proxy: a URL without a port is rejected");
        Check(SSHConnectionSettings.TryParseProxy("socks5://127.0.0.1:10808", out string plainCredentials, out string plainHost, out int plainPort) && plainHost == "127.0.0.1" && plainPort == 10808 && plainCredentials == null,
            "proxy: the overload without the type also reads a URL");
        Check(new SSHConnectionSettings().ProxyType == SshProxyType.Http, "proxy: profiles of earlier versions (no ProxyType) use HTTP CONNECT");
    }

    private static void CheckProxyScheme(string text, SshProxyType? expectedType, string expectedCredentials, string expectedHost, int expectedPort)
    {
        bool parsed = SSHConnectionSettings.TryParseProxy(text, out SshProxyType? proxyType, out string credentials, out string host, out int port);
        Check(parsed && proxyType == expectedType && credentials == expectedCredentials && host == expectedHost && port == expectedPort,
            "proxy: '" + text + "' gives " + (expectedType?.ToString() ?? "no type") + " " + expectedHost + ":" + expectedPort);
    }

    private static void CheckIpv6Hosts()
    {
        Equal(SSHConnectionSettings.StripPortNumber("10.0.0.9:2222"), "10.0.0.9", "strip: a port is removed");
        Equal(SSHConnectionSettings.StripPortNumber("::1"), "::1", "strip: a bare IPv6 address is not cut at its first colon");
        Equal(SSHConnectionSettings.StripPortNumber("[::1]:2222"), "::1", "strip: brackets and port are removed");
        Check(!SSHConnectionSettings.HostNamesEqual("::1", "::2"), "equal: two bare IPv6 addresses differ");
        Check(SSHConnectionSettings.HostNamesEqual("[2001:db8::7]:2222", "2001:db8::7"), "equal: an IPv6 host with a port matches its bare address");
        Check(SSHConnectionSettings.HostNamesEqual("Host:22", "host"), "equal: still case-insensitive and port-insensitive");

        using TempStore store = new TempStore();
        store.Add("root", "[2001:db8::7]:2222", alias: null);
        Equal(store.Store.FindByHost("2001:db8::7").Count, 1, "ipv6: a host with a port matches its bare address");
        Equal(store.Store.FindByHost("2001:db8::8").Count, 0, "ipv6: another address does not match");
        Equal(store.Store.FindSshConnectionForArgument("root@2001:db8::7")?.HostName, "[2001:db8::7]:2222", "ipv6: the argument fallback finds it");
    }

    private static VisualTTY.DeviceTerminal.RawConnectionSettings NewTcp(string host, int port)
    {
        return new VisualTTY.DeviceTerminal.RawConnectionSettings
        {
            Settings = new VisualTTY.DeviceTerminal.DeviceTerminalSettings { Connection = new VisualTTY.DeviceTerminal.DeviceTerminalSettings.ConnectionSettings.Tcp { Host = host, Port = port } }
        };
    }

    // Telnet, TCP and Netconsole profiles were added to the list again at every reload, which the first SSH login of the session
    // triggers (it touches the timestamp file), and a deleted profile came back from the second copy.
    private static void CheckRawProfilesAreNotDuplicated()
    {
        using TempStore store = new TempStore();
        store.Store.SaveRawConnection(NewTcp("10.1.1.1", 23));
        store.Store.SaveRawConnection(NewTcp("10.1.1.2", 23));
        Equal(store.Store.RawConnections.Count(), 2, "raw: two saved profiles are listed");
        store.Add("root", "10.1.1.3", alias: null); // touches the timestamp: the next access reloads
        System.Threading.Thread.Sleep(50);
        SavedConnectionStore.TouchTimestamp(store.FolderPath);
        Equal(store.Store.RawConnections.Count(), 2, "raw: a reload does not list the profiles twice");
        Equal(store.Store.RawConnections.Count(), 2, "raw: ...and neither does the next one");
        VisualTTY.DeviceTerminal.RawConnectionSettings first = store.Store.RawConnections.First();
        store.Store.DeleteRawConnection(first);
        Equal(store.Store.RawConnections.Count(), 1, "raw: a deleted profile is gone");
        Check(!File.Exists(first.FileName), "raw: ...with its file");
    }

    // Profiles are stored as user@host: a second connection to the same user and host replaces the first, with its alias and password.
    // The window asks first, and FindOverwrittenBy is what it asks.
    private static void CheckSameUserAndHostAsksFirst()
    {
        using TempStore store = new TempStore();
        store.Add("root", "192.168.1.1", alias: "router A");
        SSHConnectionSettings other = new SSHConnectionSettings { UserName = "root", HostName = "192.168.1.1", ConnectionAlias = "router B" };
        Equal(store.Store.FindOverwrittenBy(other)?.ConnectionAlias, "router A", "replace: a new connection with the same user and host would overwrite the saved one");
        SSHConnectionSettings different = new SSHConnectionSettings { UserName = "root", HostName = "192.168.1.2" };
        Equal(store.Store.FindOverwrittenBy(different), null, "replace: another host overwrites nothing");
        SSHConnectionSettings saved = store.Store.FindSshConnection("192.168.1.1", "root");
        SSHConnectionSettings edited = saved.CloneSlow();
        edited.ConnectionAlias = "router A (edited)";
        Equal(store.Store.FindOverwrittenBy(edited, saved), null, "replace: the connection that is being edited may replace itself");
        Equal(store.Store.FindOverwrittenBy(edited)?.ConnectionAlias, "router A", "replace: ...but a new connection may not");
    }

    // The editor works on a copy; fixing a typo in the host used to leave the old profile (and its password) in the list.
    private static void CheckRenamedProfileReplacesTheOldOne()
    {
        using TempStore store = new TempStore();
        store.Add("root", "10.2.2.20", alias: "lab");
        SSHConnectionSettings saved = store.Store.FindSshConnection("10.2.2.20", "root");
        string oldFile = saved.FileName;
        SSHConnectionSettings edited = saved.CloneSlow();
        edited.HostName = "10.2.2.21";
        store.Store.SaveSshConnection(edited, replaces: saved);
        Equal(store.Store.SshConnections.Count(), 1, "rename: one profile is left after the host was changed");
        Check(!File.Exists(oldFile), "rename: the file of the old profile is gone");
        Equal(store.Store.FindSshConnection("10.2.2.21", "root")?.ConnectionAlias, "lab", "rename: the new profile has the alias");
        SSHConnectionSettings again = store.Store.FindSshConnection("10.2.2.21", "root");
        SSHConnectionSettings sameName = again.CloneSlow();
        sameName.ConnectionAlias = "lab 2";
        store.Store.SaveSshConnection(sameName, replaces: again);
        Equal(store.Store.SshConnections.Count(), 1, "rename: editing only the alias keeps one profile");
        Check(File.Exists(store.Store.FindSshConnection("10.2.2.21", "root").FileName), "rename: ...and its file");
    }

    // A save writes a temporary file first and keeps the previous version as .bak; nothing is left over.
    private static void CheckProfileFilesAreWrittenSafely()
    {
        using TempStore store = new TempStore();
        store.Add("root", "10.3.3.3", alias: "first");
        SSHConnectionSettings saved = store.Store.FindSshConnection("10.3.3.3", "root");
        saved.ConnectionAlias = "second";
        saved.Save();
        Check(File.Exists(saved.FileName + ".bak") && File.ReadAllText(saved.FileName + ".bak").Contains("first"), "safe write: the previous version is kept as .bak");
        Check(File.ReadAllText(saved.FileName).Contains("second"), "safe write: the new version is in place");
        Equal(Directory.GetFiles(store.FolderPath, "*.tmp").Length, 0, "safe write: no temporary file is left");
        Equal(Directory.GetFiles(store.FolderPath, "*.sshconnection").Length, 1, "safe write: the .bak is not taken for a profile");
        // A write that fails in the middle leaves the old file as it was.
        string path = Path.Combine(store.FolderPath, "keep.txt");
        File.WriteAllText(path, "whole");
        try
        {
            XmlStorage.WriteAtomically(path, stream =>
            {
                stream.WriteByte((byte)'x');
                throw new InvalidOperationException("serializer failed");
            });
            Check(false, "safe write: the failure is reported");
        }
        catch (InvalidOperationException)
        {
        }
        Equal(File.ReadAllText(path), "whole", "safe write: a failure in the middle leaves the old file untouched");
        Equal(Directory.GetFiles(store.FolderPath, "*.tmp").Length, 0, "safe write: ...and no temporary file");
    }

    // A profile that cannot be read used to vanish from the list without a word, and the next save of the same user@host overwrote it.
    private static void CheckUnreadableProfileIsKept()
    {
        using TempStore store = new TempStore();
        store.Add("root", "10.4.4.4", alias: null);
        string broken = Path.Combine(store.FolderPath, "admin@10.4.4.5.sshconnection");
        File.WriteAllText(broken, "<SSHConnectionSettings><HostName>10.4.4.5");
        string reported = null;
        XmlStorage.ProblemNotice = message => reported = message;
        SavedConnectionStore.TouchTimestamp(store.FolderPath);
        System.Threading.Thread.Sleep(50);
        SavedConnectionStore.TouchTimestamp(store.FolderPath);
        Equal(store.Store.SshConnections.Count(), 1, "unreadable: the good profile is listed");
        Check(reported != null && reported.Contains("admin@10.4.4.5.sshconnection"), "unreadable: the user is told which file");
        Check(!File.Exists(broken) && File.Exists(broken + ".bad"), "unreadable: the file is kept as .bad");
        XmlStorage.ProblemNotice = null;
    }

    /// <summary>A saved-connection store backed by a directory of its own, thrown away with the instance.</summary>
    private sealed class TempStore : IDisposable
    {
        private readonly string _directory;

        public SavedConnectionStore Store { get; }

        public string FolderPath => _directory;

        public TempStore()
        {
            _directory = Path.Combine(Path.GetTempPath(), "SmarTTY-ConnectionLookupTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Store = new SavedConnectionStore(_directory);
        }

        public void Add(string userName, string hostName, string alias)
        {
            SSHConnectionSettings settings = new SSHConnectionSettings
            {
                UserName = userName,
                HostName = hostName,
                ConnectionAlias = alias
            };
            Store.SaveSshConnection(settings);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
            }
        }
    }
}
