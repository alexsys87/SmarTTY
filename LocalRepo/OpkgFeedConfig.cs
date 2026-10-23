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

    /// <summary>The main configuration of opkg, where OpenWrt turns the signature check of the package lists on.</summary>
    public const string OpkgConfFile = "/etc/opkg.conf";

    /// <summary>
    /// Put in front of "option check_signature" while the server runs with unsigned feeds allowed; taken away again by
    /// Stop and by "Remove feeds from device".
    /// </summary>
    public const string SignatureOffMarker = "# smartty-local-off: ";

    /// <summary>The list of the official feeds of OpenWrt (opkg); apk has <see cref="ApkFeedConfig.DistFeedsFile"/>.</summary>
    public const string DistFeedsFile = "/etc/opkg/distfeeds.conf";

    /// <summary>Put in front of an official feed line while the server runs with the official feeds turned off.</summary>
    public const string OfficialOffMarker = "# smartty-local-off-feed: ";

    public const string SignatureCheckOnMarker = "SMARTTY_SIGCHECK=on";

    // "option check_signature" (as OpenWrt writes it) or with a value other than 0.
    private const string SignatureOptionPattern = "^[[:space:]]*option[[:space:]][[:space:]]*check_signature([[:space:]]*$|[[:space:]][[:space:]]*[^0[:space:]])";

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

    /// <summary>
    /// Replaces the block of local feeds in the configuration file (creating the file if needed). With
    /// <paramref name="allowUnsigned"/> the signature check of opkg is turned off until the feeds are removed, otherwise
    /// a check turned off by an earlier run is turned on again. With <paramref name="disableOfficial"/> the official feeds of
    /// <paramref name="distFeedsFile"/> are commented out until the feeds are removed, otherwise they are given back.
    /// </summary>
    public static string BuildEnableScript(IList<LocalRepoFeed> feeds, int port, string configFile = DefaultConfigFile, bool allowUnsigned = false, string opkgConf = OpkgConfFile, bool disableOfficial = false, string distFeedsFile = DistFeedsFile)
    {
        return BuildBlockEnableScript(feeds.Select((LocalRepoFeed feed) => FeedLine(feed, port)), configFile) + " && " +
            (allowUnsigned ? BuildSignatureOffScript(opkgConf) : BuildSignatureRestoreScript(opkgConf)) + " && " +
            (disableOfficial ? BuildOfficialOffScript(distFeedsFile) : BuildOfficialRestoreScript(distFeedsFile));
    }

    /// <summary>
    /// Comments out the feed lines of the list of the official feeds (those not yet commented) with the marker of SmarTTY in front,
    /// so that the restore gives back the very same lines; does nothing when the file does not exist.
    /// </summary>
    public static string BuildOfficialOffScript(string distFeedsFile)
    {
        return "{ d=" + RemotePathUtils.ShellQuote(distFeedsFile) + "; [ ! -f \"$d\" ] || sed -i '/^[[:space:]]*$/!{/^[[:space:]]*#/!s/^/" + OfficialOffMarker + "/}' \"$d\"; }";
    }

    /// <summary>Takes the marker away from the official feed lines, so that they are used again; does nothing when there is none.</summary>
    public static string BuildOfficialRestoreScript(string distFeedsFile)
    {
        return "{ d=" + RemotePathUtils.ShellQuote(distFeedsFile) + "; ! grep -qs '^" + OfficialOffMarker + "' \"$d\" || sed -i 's/^" + OfficialOffMarker + "//' \"$d\"; }";
    }

    /// <summary>Prints <see cref="SignatureCheckOnMarker"/> when opkg checks the signatures of the package lists.</summary>
    public static string BuildSignatureProbe(string opkgConf = OpkgConfFile)
    {
        return "grep -qsE '" + SignatureOptionPattern + "' " + RemotePathUtils.ShellQuote(opkgConf) + " && echo " + SignatureCheckOnMarker + " || echo SMARTTY_SIGCHECK=off";
    }

    /// <summary>True when the output of <see cref="BuildSignatureProbe"/> says that opkg checks signatures.</summary>
    public static bool ParseSignatureProbe(IEnumerable<string> lines)
    {
        return lines != null && lines.Any((string line) => line?.Trim() == SignatureCheckOnMarker);
    }

    /// <summary>
    /// Comments out the "option check_signature" lines with the marker of SmarTTY in front, so that the restore gives back
    /// the very same lines; does nothing when opkg does not check signatures.
    /// </summary>
    public static string BuildSignatureOffScript(string opkgConf = OpkgConfFile)
    {
        return "{ c=" + RemotePathUtils.ShellQuote(opkgConf) + "; ! grep -qsE '" + SignatureOptionPattern + "' \"$c\" || " +
            "sed -i 's/^\\([[:space:]]*option[[:space:]][[:space:]]*check_signature\\)/" + SignatureOffMarker + "\\1/' \"$c\"; }";
    }

    /// <summary>Takes the marker of SmarTTY away, so that opkg checks signatures again; does nothing when there is none.</summary>
    public static string BuildSignatureRestoreScript(string opkgConf = OpkgConfFile)
    {
        return "{ c=" + RemotePathUtils.ShellQuote(opkgConf) + "; ! grep -qs '^" + SignatureOffMarker + "' \"$c\" || sed -i 's/^" + SignatureOffMarker + "//' \"$c\"; }";
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

    /// <summary>Removes the block of local feeds and turns the signature check on again if SmarTTY turned it off; succeeds if there is none.</summary>
    public static string BuildDisableScript(string configFile = DefaultConfigFile, string opkgConf = OpkgConfFile, string distFeedsFile = DistFeedsFile)
    {
        return BuildBlockDisableScript(configFile) + " && " + BuildSignatureRestoreScript(opkgConf) + " && " + BuildOfficialRestoreScript(distFeedsFile);
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
