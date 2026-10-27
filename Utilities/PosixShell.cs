using SmarTTY.IO;

namespace SmarTTY.Utilities;

/// <summary>
/// Runs a POSIX shell script on the remote host whatever the login shell of the SSH user is. An SSH exec channel hands the
/// command line to the login shell, and the probes and package manager commands are written in POSIX shell: "if [ ... ]; then",
/// "{ cmd; } 2>&amp;1", "LC_ALL=C cmd". fish stops at the first of them with a syntax error and tcsh with "Ambiguous output
/// redirect" or "LC_ALL=C: Command not found", so on such hosts the detection answered "not found" without any clue why.
/// </summary>
public static class PosixShell
{
    /// <summary>
    /// The directories of the system tools. The exec channel of dropbear (OpenWrt) and of some sshd builds gives a user other
    /// than root a PATH without them, while the login shell the terminal tab runs in has them: apk of Alpine is /sbin/apk.
    /// They are appended, so that whatever the user has earlier in the PATH still wins.
    /// </summary>
    public const string AdminDirectories = "/usr/local/sbin:/usr/sbin:/sbin";

    /// <summary>
    /// The command line that runs <paramref name="script"/> in /bin/sh (an absolute path, so that it does not depend on the PATH
    /// either). The script is quoted as one word, which fish, tcsh and the POSIX shells read alike.
    /// </summary>
    public static string Wrap(string script, bool addAdminDirectoriesToPath = false)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new System.ArgumentException("The script is empty", nameof(script));
        }
        string body = addAdminDirectoriesToPath ? "PATH=\"$PATH:" + AdminDirectories + "\"; " + script : script;
        return "/bin/sh -c " + RemotePathUtils.ShellQuote(body);
    }
}
