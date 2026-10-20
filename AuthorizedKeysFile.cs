using System;
using System.Collections.Generic;
using System.Linq;

namespace SmarTTY;

/// <summary>
/// Text-level operations on the lines of an OpenSSH authorized_keys file. A line is "[options] keytype base64 [comment]":
/// the comment is optional and the options (which may contain quoted blanks) may precede the key type.
/// </summary>
internal static class AuthorizedKeysFile
{
    /// <summary>True when the line carries the public key with the given blob, whatever its options and comment are.</summary>
    public static bool LineHoldsKey(string line, byte[] keyBlob)
    {
        if (string.IsNullOrWhiteSpace(line) || keyBlob == null || keyBlob.Length == 0)
        {
            return false;
        }
        string trimmed = line.Trim();
        if (trimmed[0] == '#')
        {
            return false;
        }
        foreach (string token in trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // The key type and the options are never valid base64 of the whole blob, so any token that decodes
            // to the blob is the key itself.
            if (token.Length < 16)
            {
                continue;
            }
            try
            {
                if (Convert.FromBase64String(token).SequenceEqual(keyBlob))
                {
                    return true;
                }
            }
            catch (FormatException)
            {
            }
        }
        return false;
    }

    public static bool ContainsKey(IEnumerable<string> lines, byte[] keyBlob)
    {
        return lines.Any(line => LineHoldsKey(line, keyBlob));
    }

    /// <summary>
    /// Returns the lines without the ones that hold the key. Everything else is kept untouched: other keys with or
    /// without comments or options, comment lines and blank lines.
    /// </summary>
    public static List<string> RemoveKey(IEnumerable<string> lines, byte[] keyBlob, out bool removed)
    {
        List<string> result = new List<string>();
        removed = false;
        foreach (string line in lines)
        {
            if (LineHoldsKey(line, keyBlob))
            {
                removed = true;
            }
            else
            {
                result.Add(line);
            }
        }
        return result;
    }

    /// <summary>The first line that looks like an absolute path: the output of "pwd" may be preceded by banner text.</summary>
    public static string PickHomeDirectory(IEnumerable<string> pwdLines)
    {
        return pwdLines?.Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Shell script that appends standard input to the file given as its first argument. If the file does not end with a
    /// line break one is added first, otherwise the new key would be glued to the last line.
    /// </summary>
    public const string AppendScript = "f=\"$1\"; if [ -s \"$f\" ] && [ -n \"$(tail -c1 \"$f\")\" ]; then echo >> \"$f\"; fi; cat >> \"$f\"";
}
