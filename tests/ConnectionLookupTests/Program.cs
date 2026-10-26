using System;
using System.Collections.Generic;
using System.IO;
using SmarTTY.Transport;
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
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
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

    /// <summary>A saved-connection store backed by a directory of its own, thrown away with the instance.</summary>
    private sealed class TempStore : IDisposable
    {
        private readonly string _directory;

        public SavedConnectionStore Store { get; }

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
