using System;
using System.Linq;

namespace SmarTTY.Ssh;

/// <summary>
/// The decisions of a connection attempt that need no connection: what a saved host key says about the key the server
/// presents, how the proxy credentials are split, what is tried first for a password. They are kept apart from
/// <c>SshConnector.ConnectWorker</c> so that they can be checked without a server.
/// </summary>
public static class ConnectionRules
{
    public enum HostKeyVerdict
    {
        /// <summary>The server presents the key that was saved.</summary>
        Matches,

        /// <summary>The key (or its type) differs from the saved one: the user must decide.</summary>
        Changed
    }

    /// <summary>Older versions stored host key types unknown to them as libssh2 type numbers.</summary>
    public static string NormalizeKeyType(string keyType)
    {
        return keyType switch
        {
            "4" => "ecdsa-sha2-nistp384",
            "5" => "ecdsa-sha2-nistp521",
            "6" => "ssh-ed25519",
            _ => keyType,
        };
    }

    /// <summary>
    /// Compares the saved host key of a server with the one it presents now. <paramref name="reason"/> is the sentence for
    /// the user when the type of the key changed, null when only the key did, and says so when the saved key cannot be read
    /// (a damaged known hosts file): that is a key to decide about again, not a reason to fail with a decoding error.
    /// </summary>
    public static HostKeyVerdict CompareHostKey(string savedKeyType, string savedKeyBase64, string presentedType, byte[] presentedKey, out string reason)
    {
        reason = null;
        byte[] savedKey;
        try
        {
            savedKey = Convert.FromBase64String(savedKeyBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            reason = "The saved key of the server cannot be read";
            return HostKeyVerdict.Changed;
        }
        if (NormalizeKeyType(savedKeyType) != presentedType)
        {
            reason = "Key type changed from " + savedKeyType + " to " + presentedType;
            return HostKeyVerdict.Changed;
        }
        return presentedKey != null && savedKey.SequenceEqual(presentedKey) ? HostKeyVerdict.Matches : HostKeyVerdict.Changed;
    }

    /// <summary>Splits the proxy credentials, stored as "user:password" (the password may contain colons).</summary>
    public static void SplitProxyCredentials(string credentials, out string userName, out string password)
    {
        userName = null;
        password = null;
        if (string.IsNullOrEmpty(credentials))
        {
            return;
        }
        int colonIdx = credentials.IndexOf(':');
        if (colonIdx == -1)
        {
            userName = credentials;
            return;
        }
        userName = credentials.Substring(0, colonIdx);
        password = credentials.Substring(colonIdx + 1);
    }

    /// <summary>
    /// The password to send first. Nothing typed and nothing saved is null when somebody can be asked: an empty password
    /// would be a failed login that the server logs and counts towards MaxAuthTries and fail2ban before the user has typed
    /// anything. Without anybody to ask the empty password is tried, as an account without a password needs.
    /// </summary>
    public static string FirstPasswordToTry(string password, bool canAskUser)
    {
        return password ?? (canAskUser ? null : string.Empty);
    }

    /// <summary>
    /// Seconds between keep-alive packets for the setting of a profile: 0 (not set) is the default, a negative value is an
    /// explicit "off" and gives 0, which switches the keep-alive off.
    /// </summary>
    public static int KeepAliveSeconds(int profileInterval, int defaultSeconds)
    {
        return profileInterval < 0 ? 0 : (profileInterval != 0 ? profileInterval : defaultSeconds);
    }
}
