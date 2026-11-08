using System;
using System.Collections.Generic;
using System.Text;
using SmarTTY.Utilities;

namespace SmarTTY.IO;

/// <summary>
/// The command that puts the text the editor has uploaded to a temporary file over the file on the server.
/// <para>
/// The file is still written in place (<c>cat</c> into it) and not replaced by a rename: that keeps its owner, mode, hard links
/// and the symbolic link it was opened through. But <c>cat</c> truncates the file first, so a write that stops half way (no
/// space left on the overlay of an OpenWrt router, a lost connection) used to leave the file cut off. The command therefore
/// keeps a copy of the old content next to the uploaded text, refuses to start when the free space is clearly too small, reads the
/// result back, and puts the old content back when it is not what was uploaded.
/// </para>
/// <para>
/// It ignores SIGHUP and SIGPIPE, so that it finishes (and verifies, and puts the old content back) when the connection is lost
/// in the middle. A command of the SSH exec channel runs on to its end anyway (dropbear and OpenSSH were tried), but the one run
/// through sudo has a terminal, and the loss of the connection hangs it up: with the old command the file was left cut off.
/// </para>
/// </summary>
public static class RemoteSaveCommand
{
    /// <summary>Not enough free space: nothing was written, the file is as it was.</summary>
    public const int ExitNoSpace = 90;

    /// <summary>The write failed or did not read back as written, and the old content has been put back (a new file removed).</summary>
    public const int ExitRestored = 91;

    /// <summary>The write failed and the old content could not be put back: it is in the restore copy, which is kept.</summary>
    public const int ExitNotRestored = 92;

    private const string NoSpaceMarker = "smartty-nospace";

    private const string RestoredMarker = "smartty-restored";

    private const string RemovedMarker = "smartty-removed";

    /// <summary>Where the command keeps the old content while it writes: next to the uploaded text, readable by its owner only.</summary>
    public static string RestoreCopyPath(string uploadedPath)
    {
        return uploadedPath + ".orig";
    }

    /// <summary>
    /// False for what does not read back as it was written (files of /proc, /sys, devices): there the text is just copied, as
    /// before, and nothing is verified or put back.
    /// </summary>
    public static bool IsVerifiable(string targetPath)
    {
        foreach (string prefix in new[] { "/proc/", "/sys/", "/dev/" })
        {
            if (targetPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The script (one line of POSIX sh that needs no more than df, cp, cat, rm, wc and, when there is one, cmp) for the
    /// file at <paramref name="targetPath"/>, with the new text of <paramref name="length"/> bytes in <paramref name="uploadedPath"/>.
    /// With <paramref name="backup"/> the old file is also copied to its "~" file, and a failed copy does not stop the save.
    /// Run it with <see cref="PosixShell.Wrap"/> (the login shell of the user may be no POSIX shell), or give it to <c>sh -c</c> as it is.
    /// Its exit status is 0 on success, <see cref="ExitNoSpace"/>, <see cref="ExitRestored"/> or <see cref="ExitNotRestored"/>,
    /// or what <c>cat</c> returned when the file was not touched (no permission: the caller may try sudo).
    /// </summary>
    public static string BuildScript(string targetPath, string uploadedPath, long length, bool backup)
    {
        string quotedTarget = RemotePathUtils.ShellQuote(targetPath);
        string quotedUploaded = RemotePathUtils.ShellQuote(uploadedPath);
        StringBuilder script = new StringBuilder();
        if (!IsVerifiable(targetPath))
        {
            if (backup)
            {
                script.Append("[ ! -e ").Append(quotedTarget).Append(" ] || cp -p ").Append(quotedTarget).Append(' ').Append(RemotePathUtils.ShellQuote(targetPath + "~")).Append("; ");
            }
            return script.Append("cat ").Append(quotedUploaded).Append(" > ").Append(quotedTarget).ToString();
        }
        // t target, u uploaded text, r restore copy, n bytes to write; h: a restore copy exists, o: bytes of the old file.
        script.Append("trap '' HUP PIPE; t=").Append(quotedTarget)
            .Append("; u=").Append(quotedUploaded)
            .Append("; r=").Append(RemotePathUtils.ShellQuote(RestoreCopyPath(uploadedPath)))
            .Append("; n=").Append(length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("; ");
        if (backup)
        {
            script.Append("[ ! -e \"$t\" ] || cp -p \"$t\" ").Append(RemotePathUtils.ShellQuote(targetPath + "~")).Append("; ");
        }
        script
            // Something that is not a plain file (a device, a directory, a dangling link) is written as before.
            .Append("if [ -e \"$t\" ] || [ -L \"$t\" ]; then [ -f \"$t\" ] || { cat \"$u\" > \"$t\"; exit $?; }; fi; ")
            // Same content: cmp, or at least the same size where there is no cmp.
            .Append("same() { if command -v cmp >/dev/null 2>&1; then cmp -s \"$1\" \"$2\"; else set -- $(wc -c < \"$1\") $(wc -c < \"$2\"); [ \"$1\" = \"$2\" ]; fi; }; ")
            // The copy of the old content. Without it nothing can be put back, so the file is written as before.
            .Append("h=0; o=0; if [ -f \"$t\" ]; then if ( umask 077; cp \"$t\" \"$r\" ) 2>/dev/null; then h=1; set -- $(wc -c < \"$r\"); o=$1; else rm -f \"$r\"; fi; fi; ")
            .Append("if [ -f \"$t\" ] && [ $h = 0 ]; then cat \"$u\" > \"$t\"; exit $?; fi; ")
            // The free space of the file system of the file (of its folder for a new file), in KiB, from the POSIX format of df.
            .Append("case $t in */*) d=${t%/*};; *) d=.;; esac; [ -e \"$t\" ] && d=$t; [ -n \"$d\" ] || d=/; ")
            .Append("x=$(df -Pk \"$d\" 2>/dev/null | { read -r f b w a p m; read -r f b w a p m; echo \"$b $a\"; }); set -- $x; b=$1; a=$2; ")
            // Only a clear answer counts: numbers, and a file system that has a size at all (some report 0 or '-').
            .Append("v=0; case $b in ''|*[!0-9]*) ;; *) case $a in ''|*[!0-9]*) ;; *) [ $b -gt 0 ] && v=1;; esac;; esac; ")
            // The write truncates the old file first, so only the growth needs room.
            .Append("if [ $v = 1 ] && [ $(( (n - o + 1023) / 1024 )) -gt $a ]; then rm -f \"$r\"; echo \"").Append(NoSpaceMarker).Append(" $((n - o)) $((a * 1024))\"; exit 90; fi; ")
            .Append("cat \"$u\" > \"$t\"; c=$?; ")
            .Append("if [ $c = 0 ] && same \"$u\" \"$t\"; then rm -f \"$r\"; exit 0; fi; ")
            // The file is still the old one (the write could not even start, e.g. no permission): as before.
            .Append("if [ $h = 1 ] && [ $c != 0 ] && same \"$r\" \"$t\"; then rm -f \"$r\"; exit $c; fi; ")
            .Append("if [ $h = 1 ]; then if cat \"$r\" > \"$t\" && same \"$r\" \"$t\"; then rm -f \"$r\"; echo ").Append(RestoredMarker).Append("; exit 91; fi; echo \"smartty-kept $r\"; exit 92; fi; ")
            // A new file: nothing was created when it could not be (no permission), a part of it is removed.
            .Append("if [ $c != 0 ] && [ ! -e \"$t\" ]; then exit $c; fi; rm -f \"$t\"; echo ").Append(RemovedMarker).Append("; exit 91");
        return script.ToString();
    }

    /// <summary>
    /// What to tell the user about an exit status of the script that is not 0, or null when it is none of ours (the file was not
    /// touched: no permission, or an error of the shell). <paramref name="output"/> is what the command printed (null when it is
    /// not known, as for a command run through sudo).
    /// </summary>
    public static string Describe(int exitStatus, IEnumerable<string> output, string targetPath, string uploadedPath)
    {
        string detail = FirstErrorLine(output);
        string because = detail == null ? "" : " (" + detail + ")";
        switch (exitStatus)
        {
            case ExitNoSpace:
                {
                    string sizes = "";
                    if (output != null)
                    {
                        foreach (string line in output)
                        {
                            string[] parts = (line ?? "").Trim().Split(' ');
                            if (parts.Length == 3 && parts[0] == NoSpaceMarker && long.TryParse(parts[1], out long needed) && long.TryParse(parts[2], out long free))
                            {
                                sizes = " (" + StringUtils.FormatSize(needed, appendUnit: true) + " more are needed, " + StringUtils.FormatSize(free, appendUnit: true) + " are free)";
                            }
                        }
                    }
                    return "There is not enough free space on the server to save " + targetPath + sizes + ". The file was not changed.";
                }
            case ExitRestored:
                {
                    bool removed = false;
                    if (output != null)
                    {
                        foreach (string line in output)
                        {
                            removed |= (line ?? "").Trim() == RemovedMarker;
                        }
                    }
                    return targetPath + " could not be written completely" + because + ". "
                        + (removed ? "The incomplete file has been removed." : "The previous content has been put back.");
                }
            case ExitNotRestored:
                return targetPath + " could not be written completely" + because + ", and the previous content could not be put back. "
                    + "The previous content is in " + RestoreCopyPath(uploadedPath) + " and the new text in " + uploadedPath
                    + " on the server: copy them before they are deleted.";
            default:
                return null;
        }
    }

    // The first line that the command printed that is not one of its own markers: the message of cat.
    private static string FirstErrorLine(IEnumerable<string> output)
    {
        if (output == null)
        {
            return null;
        }
        foreach (string line in output)
        {
            string text = (line ?? "").Trim();
            if (text.Length != 0 && !text.StartsWith("smartty-", StringComparison.Ordinal))
            {
                return text;
            }
        }
        return null;
    }
}
