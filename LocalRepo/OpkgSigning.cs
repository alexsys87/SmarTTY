using System.Collections.Generic;
using System.Linq;
using System.Text;
using SmarTTY.IO;

namespace SmarTTY.LocalRepo;

/// <summary>
/// Signs the package index that SmarTTY generates, so that opkg with "option check_signature" accepts it. The index of the
/// OpenWrt build has a Packages.sig of the build key; a generated one cannot have it, and opkg drops a feed whose signature
/// is missing ("Signature file download failed", exit status 1 of "opkg update"). The key is made on the device by its own
/// "usign" (the program opkg verifies with, so the formats are the ones it expects), the public half is put into
/// /etc/opkg/keys for the time the server runs, and the secret half stays in /tmp of the device. To sign an index the device
/// fetches it from the server through the tunnel and signs the very bytes it got; nothing is installed and no key is made on
/// this computer. Stop and "Remove feeds from device" delete both halves again.
/// </summary>
public static class OpkgSigning
{
    public const string KeysDirectory = "/etc/opkg/keys";

    /// <summary>Holds the secret key and the index being signed; in /tmp, so a reboot takes it away.</summary>
    public const string WorkDirectory = "/tmp/smartty-local-sign";

    /// <summary>The comment of the keys, by which keys left behind by an earlier run are found and removed.</summary>
    public const string KeyComment = "SmarTTY local repository (temporary key)";

    public const string ResultPrefix = "SMARTTY_SIGN=";

    public const string NoUsign = "nousign";

    private const string FindOurKeys = "for f in \"$k\"/*; do [ -f \"$f\" ] && grep -qs 'SmarTTY local repository' \"$f\" && rm -f \"$f\"; done; rm -rf \"$w\"";

    /// <summary>
    /// Makes the key pair and trusts the public key; prints <see cref="ResultPrefix"/> and the fingerprint (the name of the key
    /// file), or <see cref="ResultPrefix"/> and <see cref="NoUsign"/> when the device has no usign. Keys of an earlier run are
    /// removed first.
    /// </summary>
    public static string BuildSetupScript()
    {
        return Variables() + "if ! command -v usign >/dev/null 2>&1; then echo " + ResultPrefix + NoUsign + "; else " + FindOurKeys + "; umask 077; " +
            "mkdir -p \"$w\" \"$k\" && usign -G -c " + RemotePathUtils.ShellQuote(KeyComment) + " -s \"$w/key.sec\" -p \"$w/key.pub\" && " +
            "fp=$(usign -F -p \"$w/key.pub\") && [ -n \"$fp\" ] && cp \"$w/key.pub\" \"$k/$fp\" && echo \"" + ResultPrefix + "$fp\"; fi";
    }

    /// <summary>
    /// Has the device fetch the index at <paramref name="indexUrl"/> (the Packages file, through the tunnel) and sign it; prints
    /// the signature file.
    /// </summary>
    public static string BuildSignScript(string indexUrl)
    {
        return Variables() +
            "if command -v uclient-fetch >/dev/null 2>&1; then f='uclient-fetch -q -T 30 -O'; " +
            "elif command -v wget >/dev/null 2>&1; then f='wget -q -T 30 -O'; " +
            "elif command -v curl >/dev/null 2>&1; then f='curl -fsS -m 30 -o'; " +
            "else echo 'SMARTTY: no uclient-fetch, wget or curl' >&2; false; fi && " +
            "rm -f \"$w/Packages\" \"$w/Packages.sig\" && $f \"$w/Packages\" " + RemotePathUtils.ShellQuote(indexUrl) + " && " +
            "usign -S -m \"$w/Packages\" -s \"$w/key.sec\" -x \"$w/Packages.sig\" && cat \"$w/Packages.sig\"";
    }

    /// <summary>Removes the keys of SmarTTY from the trusted ones and the work directory; succeeds if there are none.</summary>
    public static string BuildRemoveScript()
    {
        return Variables() + FindOurKeys + "; true";
    }

    /// <summary>The fingerprint the setup printed, "nousign" for a device without usign, or null if it printed nothing usable.</summary>
    public static string ParseSetup(IEnumerable<string> lines)
    {
        string result = lines?.Select(line => line?.Trim()).LastOrDefault(line => line != null && line.StartsWith(ResultPrefix, System.StringComparison.Ordinal));
        string value = result?.Substring(ResultPrefix.Length).Trim();
        if (value == NoUsign)
        {
            return NoUsign;
        }
        return !string.IsNullOrEmpty(value) && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')) ? value : null;
    }

    /// <summary>The signature file as the device printed it ("untrusted comment: ..." and the key line), or null.</summary>
    public static byte[] ParseSignature(IEnumerable<string> lines)
    {
        List<string> all = lines?.Select(line => (line ?? "").TrimEnd('\r')).ToList() ?? new List<string>();
        int comment = all.FindIndex(line => line.StartsWith("untrusted comment:", System.StringComparison.Ordinal));
        if (comment < 0)
        {
            return null;
        }
        string data = all.Skip(comment + 1).FirstOrDefault(line => line.Trim().Length > 0);
        if (data == null)
        {
            return null;
        }
        return new UTF8Encoding(false).GetBytes(all[comment] + "\n" + data.Trim() + "\n");
    }

    private static string Variables()
    {
        return "w=" + RemotePathUtils.ShellQuote(WorkDirectory) + "; k=" + RemotePathUtils.ShellQuote(KeysDirectory) + "; ";
    }
}
