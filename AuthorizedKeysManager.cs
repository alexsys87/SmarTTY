using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VisualTTY;

namespace SmarTTY;

internal static class AuthorizedKeysManager
{

    public static bool IsKeyAuthorized(SshSession sshSession, byte[] keyBlob)
    {
        try
        {
            string homeDir = AuthorizedKeysFile.PickHomeDirectory(sshSession.RunCommandChecked("pwd", requireSuccess: true));
            if (homeDir == null)
            {
                return false;
            }
            if (!FileHoldsKey(sshSession, homeDir + "/.ssh/authorized_keys", keyBlob))
            {
                return false;
            }
            // Dropbear reads the keys of root from another file: a key that is only in ~/.ssh is not accepted there.
            return GetDropbearRootKeyFiles(sshSession).All(path => FileHoldsKey(sshSession, path, keyBlob));
        }
        catch
        {
            return false;
        }
    }

    private static bool FileHoldsKey(SshSession sshSession, string path, byte[] keyBlob)
    {
        try
        {
            using ScpDownloadStream stream = sshSession.OpenDownload(path);
            using StreamReader streamReader = new StreamReader(stream);
            while (!streamReader.EndOfStream)
            {
                string line = streamReader.ReadLine();
                if (line == null)
                {
                    break;
                }
                if (AuthorizedKeysFile.LineHoldsKey(line, keyBlob))
                {
                    return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    /// <summary>
    /// Dropbear (OpenWrt and other embedded systems) takes the authorized keys of root from /etc/dropbear/authorized_keys and
    /// ignores ~/.ssh/authorized_keys, so a key that was set up only in the home directory is rejected on the next connection.
    /// </summary>
    private static List<string> GetDropbearRootKeyFiles(SshSession sshSession)
    {
        List<string> files = new List<string>();
        try
        {
            if (sshSession.ServerVersion?.IndexOf("dropbear", StringComparison.OrdinalIgnoreCase) >= 0
                && sshSession.RunCommandChecked("id -u", requireSuccess: true)?.Any(line => line.Trim() == "0") == true
                && sshSession.RunCommandChecked("test -d /etc/dropbear", requireSuccess: true) != null)
            {
                files.Add("/etc/dropbear/authorized_keys");
            }
        }
        catch
        {
        }
        return files;
    }

    /// <summary>
    /// True when the home directory can be written by its group or by others. sshd with StrictModes (the default) then ignores
    /// ~/.ssh/authorized_keys, so a public key cannot be used for this account. A failed check counts as "no".
    /// </summary>
    public static bool IsHomeDirectoryWritableByOthers(SshSession sshSession)
    {
        try
        {
            const string script = "d=\"$HOME\"; [ -n \"$d\" ] || d=$(pwd); [ -n \"$(find \"$d\" -maxdepth 0 \\( -perm -020 -o -perm -002 \\) 2>/dev/null)\" ] && echo SMARTTY_UNSAFE_HOME";
            List<string> output = sshSession.RunCommandChecked("sh -c " + ScpChannelStream.QuotePath(script), requireSuccess: false);
            return output != null && output.Any(line => line.Trim() == "SMARTTY_UNSAFE_HOME");
        }
        catch
        {
            return false;
        }
    }

    public static void RemoveAuthorizedKey(SshSession sshSession, byte[] keyBlob)
    {
        string homeDir = AuthorizedKeysFile.PickHomeDirectory(sshSession.RunCommandChecked("pwd", requireSuccess: true))
            ?? throw new Exception("Failed to detect home directory");
        foreach (string path in GetDropbearRootKeyFiles(sshSession))
        {
            try
            {
                RemoveKeyFromFile(sshSession, path, keyBlob);
            }
            catch
            {
                // The file of the home directory below is the one the user asked about.
            }
        }
        if (sshSession.RunCommandChecked("test -d " + ScpChannelStream.QuotePath(homeDir + "/.ssh"), requireSuccess: true) == null)
        {
            return;
        }
        RemoveKeyFromFile(sshSession, homeDir + "/.ssh/authorized_keys", keyBlob);
    }

    private static void RemoveKeyFromFile(SshSession sshSession, string authKeysPath, byte[] keyBlob)
    {
        List<string> lines = new List<string>();
        using (ScpDownloadStream stream = sshSession.OpenDownload(authKeysPath))
        {
            using StreamReader streamReader = new StreamReader(stream);
            while (!streamReader.EndOfStream)
            {
                string line = streamReader.ReadLine();
                if (line == null)
                {
                    break;
                }
                lines.Add(line);
            }
        }
        // Only the lines of this very key go away: other keys (with or without a comment), comments and blank lines stay.
        List<string> remaining = AuthorizedKeysFile.RemoveKey(lines, keyBlob, out bool removed);
        if (!removed)
        {
            return;
        }
        StringBuilder text = new StringBuilder();
        foreach (string line in remaining)
        {
            text.Append(line).Append('\n');
        }
        byte[] data = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
        using (ScpChannelStream scpChannelStream = sshSession.OpenUpload(authKeysPath + ".smartty", data.Length, default(DateTime), Convert.ToInt32("600", 8)))
        {
            scpChannelStream.Write(data, 0, data.Length);
        }
        string quotedPath = ScpChannelStream.QuotePath(authKeysPath);
        if (sshSession.RunCommandChecked("mv " + ScpChannelStream.QuotePath(authKeysPath + ".smartty") + " " + quotedPath, requireSuccess: true) != null)
        {
            return;
        }
        throw new Exception("Cannot rename authorized_keys file");
    }

    public static void AppendAuthorizedKey(SshSession sshSession, string keyString)
    {
        string homeDir = AuthorizedKeysFile.PickHomeDirectory(sshSession.RunCommandChecked("pwd", requireSuccess: true))
            ?? throw new Exception("Failed to detect home directory");
        string sshDir = ScpChannelStream.QuotePath(homeDir + "/.ssh");
        if (sshSession.RunCommandChecked("test -d " + sshDir + " || mkdir -m 700 " + sshDir, requireSuccess: true) == null)
        {
            throw new Exception("Cannot access .ssh directory");
        }
        string authKeysPath = homeDir + "/.ssh/authorized_keys";
        string quotedAuthKeysPath = ScpChannelStream.QuotePath(authKeysPath);
        // "sh -c" keeps this working whatever the login shell of the user is.
        string appendCommand = "sh -c " + ScpChannelStream.QuotePath(AuthorizedKeysFile.AppendScript) + " sh " + quotedAuthKeysPath;
        using (SshShellChannel sshShellChannel = sshSession.OpenShellChannel(appendCommand))
        {
            using (StreamWriter streamWriter = new StreamWriter(sshShellChannel.Input))
            {
                streamWriter.Write(keyString + "\n");
            }
            sshShellChannel.SendEof();
        }
        sshSession.RunCommandChecked("chmod 700 " + sshDir, requireSuccess: true);
        sshSession.RunCommandChecked("chmod 600 " + quotedAuthKeysPath, requireSuccess: true);
        byte[] keyBlob = AuthorizedKeysFile.ParseKeyBlob(keyString);
        foreach (string path in GetDropbearRootKeyFiles(sshSession))
        {
            if (keyBlob != null && FileHoldsKey(sshSession, path, keyBlob))
            {
                continue;
            }
            try
            {
                AppendLine(sshSession, path, keyString);
                sshSession.RunCommandChecked("chmod 600 " + ScpChannelStream.QuotePath(path), requireSuccess: true);
            }
            catch
            {
                // Best effort: the key is in the home directory, which is all that other servers read.
            }
        }
    }

    private static void AppendLine(SshSession sshSession, string path, string line)
    {
        string appendCommand = "sh -c " + ScpChannelStream.QuotePath(AuthorizedKeysFile.AppendScript) + " sh " + ScpChannelStream.QuotePath(path);
        using SshShellChannel sshShellChannel = sshSession.OpenShellChannel(appendCommand);
        using (StreamWriter streamWriter = new StreamWriter(sshShellChannel.Input))
        {
            streamWriter.Write(line + "\n");
        }
        sshShellChannel.SendEof();
    }
}
