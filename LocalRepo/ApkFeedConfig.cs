using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SmarTTY.IO;

namespace SmarTTY.LocalRepo;

/// <summary>
/// The apk counterpart of <see cref="OpkgFeedConfig"/>: shell scripts that add the local feeds to, and remove them from,
/// /etc/apk/repositories.d/customfeeds.list (the file OpenWrt reserves for the user's own repositories) between the same
/// marker lines, plus the optional trust of the public key of the build.
/// </summary>
public static class ApkFeedConfig
{
    public const string DefaultConfigFile = "/etc/apk/repositories.d/customfeeds.list";

    /// <summary>apk trusts the keys in this directory; the firmware of a build already holds the key of that build.</summary>
    public const string KeysDirectory = "/etc/apk/keys";

    /// <summary>The key that SmarTTY adds on request and removes together with the feeds.</summary>
    /// <summary>The list of the official repositories of OpenWrt.</summary>
    public const string DistFeedsFile = "/etc/apk/repositories.d/distfeeds.list";

    public const string TrustedKeyFile = KeysDirectory + "/smartty-local.pem";

    /// <summary>The public key of an OpenWrt build tree (BUILD_KEY_APK_PUB), in the root of the tree.</summary>
    public const string BuildPublicKeyName = "public-key.pem";

    private const int MaxKeyLevelsUp = 8;

    private const int MaxKeyBytes = 16 * 1024;

    /// <summary>The repository line of a feed: the URL of its packages.adb; apk fetches the .apk files from the same folder.</summary>
    public static string FeedLine(LocalRepoFeed feed, int port)
    {
        return OpkgFeedConfig.BaseUrl(port) + feed.UrlPath + "/" + ApkFeedInfo.IndexName;
    }

    public static string BuildCheckScript()
    {
        return "command -v apk >/dev/null 2>&1";
    }

    /// <summary>
    /// Replaces the block of local repositories in the list file (creating it if needed) and, when a public key is given,
    /// makes apk trust it; without a key a key of an earlier run is removed.
    /// </summary>
    public static string BuildEnableScript(IList<LocalRepoFeed> feeds, int port, string publicKey = null, string configFile = DefaultConfigFile, string keyFile = TrustedKeyFile, bool disableOfficial = false, string distFeedsFile = DistFeedsFile)
    {
        string script = OpkgFeedConfig.BuildBlockEnableScript(feeds.Select((LocalRepoFeed feed) => FeedLine(feed, port)), configFile) + " && " +
            (disableOfficial ? OpkgFeedConfig.BuildOfficialOffScript(distFeedsFile) : OpkgFeedConfig.BuildOfficialRestoreScript(distFeedsFile));
        string quotedKeyFile = RemotePathUtils.ShellQuote(keyFile);
        if (string.IsNullOrEmpty(publicKey))
        {
            return script + " && rm -f " + quotedKeyFile;
        }
        string normalized = publicKey.Replace("\r\n", "\n").Trim('\n', '\r', ' ');
        return script + " && mkdir -p \"$(dirname " + quotedKeyFile + ")\" && printf '%s\\n' " + RemotePathUtils.ShellQuote(normalized) + " > " + quotedKeyFile;
    }

    /// <summary>Removes the block of local repositories and the key; succeeds if there are none.</summary>
    public static string BuildDisableScript(string configFile = DefaultConfigFile, string keyFile = TrustedKeyFile, string distFeedsFile = DistFeedsFile)
    {
        return OpkgFeedConfig.BuildBlockDisableScript(configFile) + "; " + OpkgFeedConfig.BuildOfficialRestoreScript(distFeedsFile) + "; rm -f " + RemotePathUtils.ShellQuote(keyFile);
    }

    /// <summary>
    /// Looks for the public key of the build above the given folders (it lies in the root of the OpenWrt tree, e.g. four levels
    /// above bin/packages/&lt;arch&gt;/&lt;feed&gt;). Returns the PEM text or null when there is none.
    /// </summary>
    public static string FindBuildPublicKey(IEnumerable<string> startDirectories)
    {
        foreach (string start in startDirectories)
        {
            string directory = start;
            for (int level = 0; level <= MaxKeyLevelsUp && !string.IsNullOrEmpty(directory); level++)
            {
                string candidate = Path.Combine(directory, BuildPublicKeyName);
                try
                {
                    if (File.Exists(candidate) && new FileInfo(candidate).Length <= MaxKeyBytes)
                    {
                        string text = File.ReadAllText(candidate);
                        if (text.Contains("-----BEGIN PUBLIC KEY-----") && text.Contains("-----END PUBLIC KEY-----"))
                        {
                            return text.Replace("\r\n", "\n").Trim('\n', '\r', ' ');
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                }
                directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }
        return null;
    }
}
