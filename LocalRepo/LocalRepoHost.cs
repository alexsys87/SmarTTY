using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;

namespace SmarTTY.LocalRepo;

/// <summary>
/// Serves local OpenWrt package folders to the device of an SSH session: an HTTP server on the loopback interface of this computer,
/// a reverse SSH tunnel (like "ssh -R") that makes it reachable as http://127.0.0.1:PORT on the device, and the feeds that are
/// added to /etc/opkg/customfeeds.conf there. The packages then show up in the package manager window (Tools > Manage Linux
/// Packages) after "opkg update" and are installed like any other package. One host belongs to one session.
/// </summary>
public sealed class LocalRepoHost
{
    private readonly object _Sync = new object();

    private LocalRepoServer _Server;

    private SshSession _TunnelSession;

    private IList<LocalRepoFeed> _Feeds = new LocalRepoFeed[0];

    private readonly List<string> _History = new List<string>();

    private const int MaxHistoryLines = 500;

    /// <summary>Raised from any thread with a time-stamped line: what the host does and the requests of the device.</summary>
    public event Action<string> Log;

    /// <summary>The lines logged so far (the last 500), for a window that is opened while the server is already running.</summary>
    public string[] GetHistory()
    {
        lock (_Sync)
        {
            return _History.ToArray();
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_Sync)
            {
                return _Server != null;
            }
        }
    }

    public int Port
    {
        get
        {
            lock (_Sync)
            {
                return (_Server != null) ? _Server.Port : 0;
            }
        }
    }

    public IList<LocalRepoFeed> Feeds
    {
        get
        {
            lock (_Sync)
            {
                return _Feeds;
            }
        }
    }

    /// <summary>False when the server runs but the tunnel belongs to an SSH connection that is no longer the current one.</summary>
    public bool IsTunnelCurrent(SshSession session)
    {
        lock (_Sync)
        {
            return _Server != null && ReferenceEquals(_TunnelSession, session);
        }
    }

    /// <summary>Adds a line to the history and passes it on to the listeners.</summary>
    public void Report(string message)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
        lock (_Sync)
        {
            _History.Add(line);
            if (_History.Count > MaxHistoryLines)
            {
                _History.RemoveRange(0, _History.Count - MaxHistoryLines);
            }
        }
        try
        {
            Log?.Invoke(line);
        }
        catch
        {
        }
    }

    private static bool IsRoot(SshSession session)
    {
        List<string> output = session.RunCommandChecked("id -u", requireSuccess: true);
        return output != null && output.Any((string line) => line.Trim() == "0");
    }

    /// <summary>Starts the server and the tunnel and adds the feeds to the device; throws with a message for the user when it cannot.</summary>
    public void Start(SshSession session, IList<string> folders, int port)
    {
        if (session == null)
        {
            throw new InvalidOperationException("There is no SSH connection.");
        }
        lock (_Sync)
        {
            if (_Server != null)
            {
                throw new InvalidOperationException("The server is already running.");
            }
        }
        List<string> roots = folders.Where((string folder) => !string.IsNullOrWhiteSpace(folder)).ToList();
        if (roots.Count == 0)
        {
            throw new InvalidOperationException("Add at least one folder with OpenWrt packages (for example bin/packages/<arch> of the build tree).");
        }
        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException("The folder was not found: " + root);
            }
        }
        List<LocalRepoFeed> feeds = LocalRepoFeedScanner.Scan(roots);
        if (feeds.Count == 0)
        {
            throw new InvalidOperationException("No package feeds were found in the selected folders. A feed is a folder with a Packages file or with .ipk files, for example bin/packages/<arch>/base.");
        }
        if (session.RunCommandChecked(OpkgFeedConfig.BuildCheckScript(), requireSuccess: true) == null)
        {
            throw new InvalidOperationException("opkg was not found on the device. The local repository server works with OpenWrt's opkg.");
        }
        bool isRoot = IsRoot(session);

        LocalRepoServer server = new LocalRepoServer(roots, port);
        server.Feeds = feeds;
        server.RequestLogged += (string message) => Report(message);
        try
        {
            server.Start();
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException("Cannot listen on port " + port + " of this computer: " + exception.Message + "\nChoose another port.", exception);
        }

        bool tunnelStarted = false;
        try
        {
            try
            {
                session.StartRemoteForward((uint)server.Port, "127.0.0.1", (uint)server.Port);
                tunnelStarted = true;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("The SSH server of the device did not accept the reverse tunnel for port " + server.Port + " (the port may be busy on the device, or TCP forwarding is disabled in its SSH server): " + exception.Message, exception);
            }
            ConfigureDevice(session, feeds, server.Port, isRoot);
        }
        catch
        {
            if (tunnelStarted)
            {
                TryStopTunnel(session);
            }
            server.Stop();
            throw;
        }

        lock (_Sync)
        {
            _Server = server;
            _TunnelSession = session;
            _Feeds = feeds;
        }
        Report("Server started on port " + server.Port + ", " + feeds.Count + (feeds.Count == 1 ? " feed" : " feeds") + " added to " + OpkgFeedConfig.DefaultConfigFile + " on the device.");
    }

    /// <summary>
    /// Removes the feeds from the device (if it is still reachable), closes the tunnel and stops the server.
    /// <paramref name="session"/> is the current SSH session; it may be null or already closed.
    /// </summary>
    public void Stop(SshSession session)
    {
        LocalRepoServer server;
        SshSession tunnel;
        lock (_Sync)
        {
            server = _Server;
            tunnel = _TunnelSession;
            _Server = null;
            _TunnelSession = null;
            _Feeds = new LocalRepoFeed[0];
        }
        if (server == null)
        {
            return;
        }
        string failure = RemoveFeeds(session);
        TryStopTunnel(tunnel);
        server.Stop();
        Report(failure == null ? "Server stopped, the feeds were removed from the device." : "Server stopped. " + failure);
    }

    /// <summary>
    /// Removes the block of local feeds from the opkg configuration of the device; succeeds if there is none.
    /// Returns null on success or a message why it could not be done.
    /// </summary>
    public string RemoveFeeds(SshSession session)
    {
        if (session == null)
        {
            return "The feeds could not be removed from the device: there is no SSH connection. Use \"Remove feeds from device\" after reconnecting.";
        }
        try
        {
            bool isRoot = IsRoot(session);
            if (session.RunCommandChecked(OpkgFeedConfig.WrapForPrivileges(OpkgFeedConfig.BuildDisableScript(), isRoot), requireSuccess: true) == null)
            {
                return "The feeds could not be removed from " + OpkgFeedConfig.DefaultConfigFile + " on the device. Remove the lines between \"" + OpkgFeedConfig.BeginMarker + "\" and \"" + OpkgFeedConfig.EndMarker + "\" by hand.";
            }
            return null;
        }
        catch (Exception exception)
        {
            return "The feeds could not be removed from the device: " + exception.Message;
        }
    }

    /// <summary>
    /// Restores the tunnel and the feeds on a new SSH connection after a reconnect; the server itself kept running.
    /// Runs SSH commands, so it must not be called on the UI thread.
    /// </summary>
    public void Reattach(SshSession session)
    {
        LocalRepoServer server;
        IList<LocalRepoFeed> feeds;
        lock (_Sync)
        {
            server = _Server;
            feeds = _Feeds;
            if (server == null || ReferenceEquals(_TunnelSession, session))
            {
                return;
            }
        }
        try
        {
            bool isRoot = IsRoot(session);
            session.StartRemoteForward((uint)server.Port, "127.0.0.1", (uint)server.Port);
            ConfigureDevice(session, feeds, server.Port, isRoot);
            lock (_Sync)
            {
                if (_Server == server)
                {
                    _TunnelSession = session;
                }
            }
            Report("The SSH connection was re-established: the tunnel and the feeds are restored.");
        }
        catch (Exception exception)
        {
            Report("Could not restore the tunnel after reconnecting: " + exception.Message + " Stop the server and start it again.");
        }
    }

    private static void ConfigureDevice(SshSession session, IList<LocalRepoFeed> feeds, int port, bool isRoot)
    {
        string script = OpkgFeedConfig.BuildEnableScript(feeds, port);
        if (session.RunCommandChecked(OpkgFeedConfig.WrapForPrivileges(script, isRoot), requireSuccess: true) == null)
        {
            throw new InvalidOperationException("Could not add the feeds to " + OpkgFeedConfig.DefaultConfigFile + " on the device." + (isRoot ? "" : " Non-root users need passwordless sudo: log in as root or allow sudo for this user."));
        }
    }

    private static void TryStopTunnel(SshSession session)
    {
        try
        {
            session?.StopRemoteForward();
        }
        catch
        {
        }
    }
}
