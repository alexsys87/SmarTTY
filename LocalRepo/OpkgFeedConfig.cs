using System.Collections.Generic;
using System.Linq;
using SmarTTY.IO;

namespace SmarTTY.LocalRepo;

/// <summary>
/// Shell scripts that add the local feeds to, and remove them from, the opkg configuration of the device. The feeds are written
/// between two marker lines of /etc/opkg/customfeeds.conf (the place OpenWrt reserves for the user's own feeds), so that
/// they are found again and removed without touching the other lines.
/// </summary>
public static class OpkgFeedConfig
{
    public const string DefaultConfigFile = "/etc/opkg/customfeeds.conf";

    public const string BeginMarker = "# smartty-local-begin";

    public const string EndMarker = "# smartty-local-end";

    /// <summary>The address of the server as the device sees it: the end of the reverse tunnel on the device itself.</summary>
    public static string BaseUrl(int port)
    {
        return "http://127.0.0.1:" + port;
    }

    public static string FeedLine(LocalRepoFeed feed, int port)
    {
        return "src/gz " + feed.Name + " " + BaseUrl(port) + feed.UrlPath;
    }

    public static string BuildCheckScript()
    {
        return "command -v opkg >/dev/null 2>&1";
    }

    /// <summary>Replaces the block of local feeds in the configuration file (creating the file if needed).</summary>
    public static string BuildEnableScript(IList<LocalRepoFeed> feeds, int port, string configFile = DefaultConfigFile)
    {
        return BuildBlockEnableScript(feeds.Select((LocalRepoFeed feed) => FeedLine(feed, port)), configFile);
    }

    /// <summary>The same for any list of repository lines: the lines are written between the markers, replacing an earlier block.</summary>
    public static string BuildBlockEnableScript(IEnumerable<string> feedLines, string configFile)
    {
        string file = RemotePathUtils.ShellQuote(configFile);
        string lines = string.Join(" ", new[] { BeginMarker }.Concat(feedLines).Concat(new[] { EndMarker }).Select(RemotePathUtils.ShellQuote).ToArray());
        return "f=" + file + "; " +
            "mkdir -p \"$(dirname \"$f\")\" && touch \"$f\" && " + BuildDeleteBlock() + " && " +
            "{ [ -z \"$(tail -c1 \"$f\")\" ] || echo >> \"$f\"; } && " +
            "printf '%s\\n' " + lines + " >> \"$f\"";
    }

    /// <summary>Removes the block of local feeds; succeeds if there is none.</summary>
    public static string BuildDisableScript(string configFile = DefaultConfigFile)
    {
        return BuildBlockDisableScript(configFile);
    }

    public static string BuildBlockDisableScript(string configFile)
    {
        return "f=" + RemotePathUtils.ShellQuote(configFile) + "; [ ! -f \"$f\" ] || " + BuildDeleteBlock();
    }

    private static string BuildDeleteBlock()
    {
        return "sed -i '/^" + BeginMarker + "$/,/^" + EndMarker + "$/d' \"$f\"";
    }

    /// <summary>Runs the script as is for root (the usual case on OpenWrt) or through non-interactive sudo for other users.</summary>
    public static string WrapForPrivileges(string script, bool isRoot)
    {
        return isRoot ? script : "sudo -n sh -c " + RemotePathUtils.ShellQuote(script);
    }
}
