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
/// added to /etc/opkg/customfeeds.conf (opkg) or /etc/apk/repositories.d/customfeeds.list (apk) there. The packages then show
/// up in the package manager window (Tools > Manage Linux Packages) after "opkg update" / "apk update" and are installed like
/// any other package. One host belongs to one session.
/// </summary>
public sealed class LocalRepoHost
{
    private readonly object _Sync = new object();

    private LocalRepoServer _Server;

    private SshSession _TunnelSession;

    private IList<LocalRepoFeed> _Feeds = new LocalRepoFeed[0];

    private LocalRepoPackageManager _Manager;

    private string _PublicKey;

    private bool _AllowUnsigned;

    private bool _DisableOfficial;

    // The port the tunnel listens on at the device: the port of the server, or the next free one when the device still holds that one.
    private int _DevicePort;

    // The signature check of opkg was on and is commented out while the server runs.
    private bool _SignatureCheckOff;

    // Generated indexes are signed with a temporary key made on the device (see OpkgSigning); null when they are not.
    private string _SigningFingerprint;

    private bool _SignGenerated;

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

    /// <summary>The package manager whose feeds are served (valid while the server runs).</summary>
    public LocalRepoPackageManager Manager
    {
        get
        {
            lock (_Sync)
            {
                return _Manager;
            }
        }
    }

    /// <summary>The line of the device's repository list for a feed, as written by Start (opkg: "src/gz NAME URL", apk: the URL of packages.adb).</summary>
    public string DescribeFeed(LocalRepoFeed feed)
    {
        lock (_Sync)
        {
            return _Manager == LocalRepoPackageManager.Apk ? ApkFeedConfig.FeedLine(feed, _DevicePort) : OpkgFeedConfig.FeedLine(feed, _DevicePort);
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

    /// <summary>
    /// Starts the server and the tunnel and adds the feeds to the device; throws with a message for the user when it cannot.
    /// <paramref name="manager"/> is the package manager of the device: only the feeds for it are served.
    /// <paramref name="trustBuildKey"/> (apk only) also makes the device trust public-key.pem of the build tree.
    /// <paramref name="allowUnsigned"/> (opkg only) turns the signature check of opkg off while the server runs.
    /// <paramref name="disableOfficial"/> comments out the official feeds of OpenWrt (distfeeds) on the device while the server runs.
    /// <paramref name="signGenerated"/> (opkg only, when the check is on and unsigned feeds are not allowed) signs the indexes that
    /// are generated here with a temporary key of the device (<see cref="OpkgSigning"/>), so that the check passes for them.
    /// </summary>
    public void Start(SshSession session, IList<string> folders, int port, LocalRepoPackageManager manager = LocalRepoPackageManager.Opkg, bool trustBuildKey = false, bool allowUnsigned = false, bool signGenerated = false, bool disableOfficial = false)
    {
        bool apk = manager == LocalRepoPackageManager.Apk;
        allowUnsigned = allowUnsigned && !apk;
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
        List<LocalRepoFeed> allFeeds = LocalRepoFeedScanner.Scan(roots);
        List<LocalRepoFeed> feeds = allFeeds.Where((LocalRepoFeed feed) => feed.IsFor(manager)).ToList();
        if (feeds.Count == 0)
        {
            if (allFeeds.Count > 0)
            {
                throw new InvalidOperationException("The selected folders hold only " + (apk ? "opkg (.ipk)" : "apk (.apk)") + " feeds, but the device uses " + (apk ? "apk" : "opkg") + ". Select the folders of a build for " + (apk ? "apk (OpenWrt 25.12 or newer)" : "opkg (OpenWrt 24.10 or older)") + ".");
            }
            throw new InvalidOperationException(apk
                ? "No package feeds were found in the selected folders. A feed is a folder with packages.adb or with .apk files, for example bin/packages/<arch>/base."
                : "No package feeds were found in the selected folders. A feed is a folder with a Packages file or with .ipk files, for example bin/packages/<arch>/base.");
        }
        if (session.RunCommandChecked(apk ? ApkFeedConfig.BuildCheckScript() : OpkgFeedConfig.BuildCheckScript(), requireSuccess: true) == null)
        {
            throw new InvalidOperationException((apk ? "apk" : "opkg") + " was not found on the device. The local repository server works with the package manager of OpenWrt (opkg or apk).");
        }
        string publicKey = null;
        if (apk && trustBuildKey)
        {
            publicKey = ApkFeedConfig.FindBuildPublicKey(roots.Concat(feeds.Select((LocalRepoFeed feed) => feed.Directory)));
            if (publicKey == null)
            {
                throw new InvalidOperationException("The public key of the build (" + ApkFeedConfig.BuildPublicKeyName + ", in the root of the OpenWrt tree) was not found above the selected folders. Select folders inside the build tree, or clear \"Trust the build key\".");
            }
        }
        bool isRoot = IsRoot(session);
        // opkg drops the list of a feed whose signature is missing or not trusted when "option check_signature" is set,
        // so its packages would silently not show up: say so, or say that the check is off for the time the server runs.
        string signatureNote = null;
        bool signatureCheckOn = !apk && OpkgFeedConfig.ParseSignatureProbe(session.RunCommandChecked(OpkgFeedConfig.BuildSignatureProbe(), requireSuccess: false));
        if (signatureCheckOn)
        {
            signatureNote = allowUnsigned
                ? "opkg checks signatures on the device: \"option check_signature\" in " + OpkgFeedConfig.OpkgConfFile + " is commented out until the server is stopped."
                : DescribeUnsignedFeeds(feeds);
        }

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
        int devicePort = server.Port;
        try
        {
            devicePort = OpenTunnel(session, server.Port);
            tunnelStarted = true;
            ConfigureDevice(session, manager, feeds, devicePort, isRoot, publicKey, allowUnsigned, disableOfficial);
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
            _Manager = manager;
            _PublicKey = publicKey;
            _AllowUnsigned = allowUnsigned;
            _DisableOfficial = disableOfficial;
            _DevicePort = devicePort;
            _SignatureCheckOff = allowUnsigned && signatureCheckOn;
            _SignGenerated = false;
            _SigningFingerprint = null;
        }
        Report("Server started on port " + server.Port + ", " + feeds.Count + (feeds.Count == 1 ? " feed" : " feeds") + " added to " + (apk ? ApkFeedConfig.DefaultConfigFile : OpkgFeedConfig.DefaultConfigFile) + " on the device" + (publicKey != null ? "; the key of the build is trusted by apk" : "") + ".");
        // The feeds whose index has no signature of the build: with the check on, opkg drops them unless the generated index is signed.
        List<string> unsignedFeeds = feeds.Where((LocalRepoFeed feed) => !HasUsableSignature(feed.Directory)).Select((LocalRepoFeed feed) => feed.Name).ToList();
        if (signatureCheckOn && !allowUnsigned && signGenerated && unsignedFeeds.Count > 0)
        {
            lock (_Sync)
            {
                _SignGenerated = true;
            }
            if (SetUpSigning(session, server, isRoot, unsignedFeeds))
            {
                signatureNote = null;
            }
        }
        if (signatureNote != null)
        {
            Report(signatureNote);
        }
        CheckDeviceReachesServer(session, devicePort);
    }

    /// <summary>
    /// The warning for feeds that opkg with the signature check on cannot use: those without a Packages.sig of the build
    /// next to an up-to-date index (a generated index has no signature). Null when every feed has one.
    /// </summary>
    internal static string DescribeUnsignedFeeds(IList<LocalRepoFeed> feeds)
    {
        List<string> unsigned = feeds.Where((LocalRepoFeed feed) => !HasUsableSignature(feed.Directory)).Select((LocalRepoFeed feed) => feed.Name).ToList();
        if (unsigned.Count == 0)
        {
            return null;
        }
        return "Warning: opkg checks signatures on the device (\"option check_signature\" in " + OpkgFeedConfig.OpkgConfFile + "), and " +
            (unsigned.Count == 1 ? "this feed has" : "these feeds have") + " no signature, so \"opkg update\" drops " + (unsigned.Count == 1 ? "it" : "them") + ": " + string.Join(", ", unsigned.ToArray()) +
            ". Restart the server with \"Allow unsigned feeds (opkg)\", or serve a fresh index of the build with its Packages.sig (make package/index) to a firmware built from the same tree.";
    }

    /// <summary>
    /// Makes the temporary key on the device and lets the server sign generated indexes with it. Reports what happened and
    /// returns false (the feeds then stay unsigned, with the warning of <see cref="DescribeUnsignedFeeds"/>) when it cannot.
    /// </summary>
    private bool SetUpSigning(SshSession session, LocalRepoServer server, bool isRoot, IList<string> feedNames)
    {
        string problem;
        if (!isRoot)
        {
            problem = "The temporary signing key can be set up only when logged in as root.";
        }
        else
        {
            problem = null;
            string fingerprint = null;
            try
            {
                fingerprint = OpkgSigning.ParseSetup(session.RunCommandChecked(OpkgSigning.BuildSetupScript(), requireSuccess: true));
            }
            catch (Exception exception)
            {
                problem = "The temporary signing key could not be set up: " + exception.Message;
            }
            if (problem == null && fingerprint == OpkgSigning.NoUsign)
            {
                problem = "The device has no usign (the program opkg checks signatures with), so the generated index cannot be signed.";
            }
            else if (problem == null && fingerprint == null)
            {
                problem = "The temporary signing key could not be set up on the device (usign -G failed or " + OpkgSigning.KeysDirectory + " is not writable).";
            }
            else if (problem == null)
            {
                lock (_Sync)
                {
                    _SigningFingerprint = fingerprint;
                }
                server.ForgetSignatures();
                server.IndexSigner = SignIndex;
                Report("The index of " + string.Join(", ", feedNames.ToArray()) + (feedNames.Count == 1 ? " is" : " are") + " generated here and has no signature of the build: it is signed with a temporary key (" + fingerprint + ") that opkg on the device trusts until the server is stopped.");
                return true;
            }
        }
        Report(problem);
        return false;
    }

    /// <summary>Has the device sign the index served at <paramref name="indexUrlPath"/>; null when it cannot.</summary>
    private byte[] SignIndex(string indexUrlPath)
    {
        SshSession session;
        LocalRepoServer server;
        int devicePort;
        lock (_Sync)
        {
            session = _TunnelSession;
            server = _Server;
            devicePort = _DevicePort;
            if (_SigningFingerprint == null)
            {
                return null;
            }
        }
        if (session == null || server == null)
        {
            return null;
        }
        List<string> output = session.RunCommandChecked(OpkgSigning.BuildSignScript(OpkgFeedConfig.BaseUrl(devicePort) + indexUrlPath), requireSuccess: true);
        byte[] signature = OpkgSigning.ParseSignature(output);
        if (signature == null)
        {
            Report("The device could not sign " + indexUrlPath + (output == null || output.Count == 0 ? "." : ": " + string.Join(" ", output.Take(3).ToArray())));
        }
        return signature;
    }

    private static bool HasUsableSignature(string directory)
    {
        try
        {
            return File.Exists(Path.Combine(directory, "Packages.sig")) && !IpkIndexBuilder.NeedsGeneratedIndex(directory);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fetches the start page of the server on the device through the tunnel (with uclient-fetch, wget or curl, whichever it
    /// has), so that a tunnel that is accepted but leads nowhere shows up in the log now rather than as a failed update later.
    /// </summary>
    private void CheckDeviceReachesServer(SshSession session, int port)
    {
        string url = OpkgFeedConfig.BaseUrl(port) + "/";
        string script =
            "if command -v uclient-fetch >/dev/null 2>&1; then f='uclient-fetch -q -T 10 -O /dev/null'; " +
            "elif command -v wget >/dev/null 2>&1; then f='wget -q -T 10 -O /dev/null'; " +
            "elif command -v curl >/dev/null 2>&1; then f='curl -fsS -m 10 -o /dev/null'; " +
            "else echo SMARTTY_FETCH=none; exit 0; fi; " +
            "if $f " + url + " >/dev/null 2>&1; then echo SMARTTY_FETCH=ok; else echo SMARTTY_FETCH=failed; fi";
        List<string> output;
        try
        {
            output = session.RunCommandChecked(script, requireSuccess: false);
        }
        catch (Exception exception)
        {
            Report("Could not check whether the device reaches the server: " + exception.Message);
            return;
        }
        if (output == null)
        {
            return;
        }
        if (output.Any((string line) => line.Trim() == "SMARTTY_FETCH=ok"))
        {
            Report("The device reaches the server at " + url + ".");
        }
        else if (output.Any((string line) => line.Trim() == "SMARTTY_FETCH=failed"))
        {
            Report("Warning: the device could not fetch " + url + " through the tunnel. Check that TCP forwarding is allowed in the SSH server of the device and that the port is not used there; the package manager will not reach the feeds otherwise.");
        }
    }

    /// <summary>
    /// Removes the feeds from the device (if it is still reachable), closes the tunnel and stops the server.
    /// <paramref name="session"/> is the current SSH session; it may be null or already closed.
    /// </summary>
    public void Stop(SshSession session)
    {
        LocalRepoServer server;
        SshSession tunnel;
        LocalRepoPackageManager manager;
        bool signatureCheckOff;
        lock (_Sync)
        {
            server = _Server;
            tunnel = _TunnelSession;
            manager = _Manager;
            signatureCheckOff = _SignatureCheckOff;
            _Server = null;
            _TunnelSession = null;
            _Feeds = new LocalRepoFeed[0];
            _SigningFingerprint = null;
            _SignGenerated = false;
        }
        if (server == null)
        {
            return;
        }
        server.IndexSigner = null;
        string failure = RemoveFeeds(session, manager);
        TryStopTunnel(tunnel);
        server.Stop();
        Report(failure == null ? "Server stopped, the feeds were removed from the device" + (signatureCheckOff ? " and opkg checks signatures again." : ".") : "Server stopped. " + failure);
    }

    /// <summary>
    /// Removes the block of local feeds (and, for apk, the key that was added for them) from the package manager
    /// configuration of the device; succeeds if there is none. Returns null on success or a message why it could not be done.
    /// </summary>
    public string RemoveFeeds(SshSession session, LocalRepoPackageManager manager = LocalRepoPackageManager.Opkg)
    {
        bool apk = manager == LocalRepoPackageManager.Apk;
        string configFile = apk ? ApkFeedConfig.DefaultConfigFile : OpkgFeedConfig.DefaultConfigFile;
        if (session == null)
        {
            return "The feeds could not be removed from the device: there is no SSH connection. Use \"Remove feeds from device\" after reconnecting.";
        }
        try
        {
            bool isRoot = IsRoot(session);
            string script = apk ? ApkFeedConfig.BuildDisableScript() : OpkgFeedConfig.BuildDisableScript();
            if (session.RunCommandChecked(OpkgFeedConfig.WrapForPrivileges(script, isRoot), requireSuccess: true) == null)
            {
                return "The feeds could not be removed from " + configFile + " on the device. Remove the lines between \"" + OpkgFeedConfig.BeginMarker + "\" and \"" + OpkgFeedConfig.EndMarker + "\" by hand.";
            }
            if (!apk)
            {
                // The temporary signing key (if one was made), trusted and secret half; nothing to remove is not an error.
                session.RunCommandChecked(OpkgFeedConfig.WrapForPrivileges(OpkgSigning.BuildRemoveScript(), isRoot), requireSuccess: false);
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
        LocalRepoPackageManager manager;
        string publicKey;
        bool allowUnsigned;
        bool disableOfficial;
        bool signGenerated;
        lock (_Sync)
        {
            server = _Server;
            feeds = _Feeds;
            manager = _Manager;
            publicKey = _PublicKey;
            allowUnsigned = _AllowUnsigned;
            disableOfficial = _DisableOfficial;
            signGenerated = _SignGenerated;
            if (server == null || ReferenceEquals(_TunnelSession, session))
            {
                return;
            }
        }
        try
        {
            bool isRoot = IsRoot(session);
            int devicePort = OpenTunnel(session, server.Port);
            ConfigureDevice(session, manager, feeds, devicePort, isRoot, publicKey, allowUnsigned, disableOfficial);
            lock (_Sync)
            {
                if (_Server == server)
                {
                    _DevicePort = devicePort;
                    _TunnelSession = session;
                }
            }
            if (signGenerated)
            {
                // The device may have been restarted: its /tmp is empty, so the secret key is gone; a new pair is made.
                SetUpSigning(session, server, isRoot, feeds.Where((LocalRepoFeed feed) => !HasUsableSignature(feed.Directory)).Select((LocalRepoFeed feed) => feed.Name).ToList());
            }
            Report("The SSH connection was re-established: the tunnel and the feeds are restored.");
        }
        catch (Exception exception)
        {
            Report("Could not restore the tunnel after reconnecting: " + exception.Message + " Stop the server and start it again.");
        }
    }

    private const int TunnelPortAttempts = 6;

    /// <summary>
    /// Opens the reverse tunnel for <paramref name="port"/> and returns the port it listens on at the device. A device that
    /// still holds the port of an earlier run (its SSH server may not release the listener when the tunnel is cancelled) refuses
    /// it, so a few of the following ports are tried; the feeds then name the port that worked.
    /// </summary>
    private int OpenTunnel(SshSession session, int port)
    {
        Exception first = null;
        for (int attempt = 0; attempt < TunnelPortAttempts; attempt++)
        {
            int devicePort = port + attempt;
            if (devicePort > 65535)
            {
                break;
            }
            try
            {
                session.StartRemoteForward((uint)devicePort, "127.0.0.1", (uint)port);
                if (attempt > 0)
                {
                    Report("Port " + port + " is still held on the device; the tunnel listens on port " + devicePort + " there instead.");
                }
                return devicePort;
            }
            catch (Exception exception)
            {
                first = first ?? exception;
            }
        }
        throw new InvalidOperationException("The SSH server of the device did not accept the reverse tunnel for port " + port + " (the port may be busy on the device, or TCP forwarding is disabled in its SSH server): " + first?.Message, first);
    }

    private static void ConfigureDevice(SshSession session, LocalRepoPackageManager manager, IList<LocalRepoFeed> feeds, int port, bool isRoot, string publicKey, bool allowUnsigned, bool disableOfficial)
    {
        bool apk = manager == LocalRepoPackageManager.Apk;
        string script = apk ? ApkFeedConfig.BuildEnableScript(feeds, port, publicKey, disableOfficial: disableOfficial) : OpkgFeedConfig.BuildEnableScript(feeds, port, allowUnsigned: allowUnsigned, disableOfficial: disableOfficial);
        if (session.RunCommandChecked(OpkgFeedConfig.WrapForPrivileges(script, isRoot), requireSuccess: true) == null)
        {
            throw new InvalidOperationException("Could not add the feeds to " + (apk ? ApkFeedConfig.DefaultConfigFile : OpkgFeedConfig.DefaultConfigFile) + " on the device." + (isRoot ? "" : " Non-root users need passwordless sudo: log in as root or allow sudo for this user."));
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
