using System.Collections.Generic;

namespace SmarTTY.IO;

/// <summary>
/// The version of a file on a Unix server, as the editor tells whether somebody else changed it while it was open. It is the time of the
/// last modification down to the nanosecond (where the file system keeps it) and the size, or a checksum where the system has no stat
/// with a format. The time with the seconds only (stat -c '%Y %s') missed a change made in the same second that kept the size, and
/// editors and scripts that rewrite a file in place do that.
/// </summary>
public static class RemoteFileSignature
{
    /// <summary>
    /// The command line that prints the signature of <paramref name="remotePath"/>. %y is the time of the last modification with the
    /// fraction (GNU, BusyBox and toybox all print it the same way); a file system that keeps whole seconds gives the same signature as
    /// before. It ends with a failure status when the file is not there.
    /// </summary>
    public static string BuildCommand(string remotePath)
    {
        string path = RemotePathUtils.ShellQuote(remotePath);
        return "stat -c '%y %s' " + path + " 2>/dev/null || md5sum " + path;
    }

    /// <summary>The signature in the output of <see cref="BuildCommand"/>; null when there is none.</summary>
    public static string Parse(IList<string> output)
    {
        if (output == null || output.Count == 0)
        {
            return null;
        }
        string signature = string.Join("\n", output).Trim();
        return signature.Length == 0 ? null : signature;
    }
}
