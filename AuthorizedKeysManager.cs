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

    // How long the remote script that appends a key may take; it only copies one line into a file.
    private static readonly TimeSpan AppendTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The home directory of the user. RunCommandChecked returns null both for a failed command and for a lost
    /// connection, so the lost connection is reported as such instead of as a missing home directory.
    /// </summary>
    private static string GetHomeDirectory(SshSession sshSession)
    {
        string homeDir = AuthorizedKeysFile.PickHomeDirectory(sshSession.RunCommandChecked("pwd", requireSuccess: true));
        if (homeDir == null)
        {
            sshSession.ThrowIfConnectionLost();
            throw new RemoteOperationException("Failed to detect home directory");
        }
        return homeDir;
    }

    /// <summary>
    /// True when the key is accepted by the server. Anything that makes the answer unknown (no file, no permission, a
    /// failing command) counts as "not accepted"; a lost connection is not an answer and is thrown.
    /// </summary>
    public static bool IsKeyAuthorized(SshSession sshSession, byte[] keyBlob)
    {
        try
        {
            string homeDir = AuthorizedKeysFile.PickHomeDirectory(sshSession.RunCommandChecked("pwd", requireSuccess: true));
            if (homeDir == null)
            {
                sshSession.ThrowIfConnectionLost();
                return false;
            }
            if (!FileHoldsKey(sshSession, homeDir + "/.ssh/authorized_keys", keyBlob))
            {
                return false;
            }
            // Dropbear reads the keys of root from another file: a key that is only in ~/.ssh is not accepted there.
            return GetDropbearRootKeyFiles(sshSession).All(path => FileHoldsKey(sshSession, path, keyBlob));
        }
        catch (IOException) when (sshSession.IsConnectionClosed)
        {
            throw;
        }
        catch
        {
            sshSession.ThrowIfConnectionLost();
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
            return false;
        }
        catch
        {
            // No scp program (or no such file): try cat below, unless the connection is gone.
            sshSession.ThrowIfConnectionLost();
        }
        // OpenWrt and other minimal systems have no scp program: read the file with cat instead, otherwise the check would
        // always fail and the key would be appended again on every connection.
        List<string> lines = sshSession.RunCommandChecked("cat " + ScpChannelStream.QuotePath(path), requireSuccess: true);
        if (lines == null)
        {
            // A missing or unreadable file holds no key; a lost connection is not "no key".
            sshSession.ThrowIfConnectionLost();
            return false;
        }
        return lines.Any(line => AuthorizedKeysFile.LineHoldsKey(line, keyBlob));
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
            else
            {
                // A null above also means "the connection is gone", which is not "this host has no such file".
                sshSession.ThrowIfConnectionLost();
            }
        }
        catch (IOException) when (sshSession.IsConnectionClosed)
        {
            throw;
        }
        catch
        {
        }
        return files;
    }

    public static void RemoveAuthorizedKey(SshSession sshSession, byte[] keyBlob)
    {
        string homeDir = GetHomeDirectory(sshSession);
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
            // No .ssh directory means there is no key to remove; a lost connection means the removal did not happen.
            sshSession.ThrowIfConnectionLost();
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
        sshSession.ThrowIfConnectionLost();
        throw new Exception("Cannot rename authorized_keys file");
    }

    /// <summary>
    /// Adds the key to the authorized keys of the user (and to the Dropbear file of root where the server reads it from)
    /// and checks that the server will now accept it. Throws when the key could not be installed, so that the caller
    /// does not switch a profile to public key authentication that has no key on the server.
    /// </summary>
    public static void AppendAuthorizedKey(SshSession sshSession, string keyString)
    {
        byte[] keyBlob = AuthorizedKeysFile.ParseKeyBlob(keyString)
            ?? throw new ArgumentException("The line is not a public key", nameof(keyString));
        string homeDir = GetHomeDirectory(sshSession);
        string sshDir = ScpChannelStream.QuotePath(homeDir + "/.ssh");
        if (sshSession.RunCommandChecked("test -d " + sshDir + " || mkdir -m 700 " + sshDir, requireSuccess: true) == null)
        {
            sshSession.ThrowIfConnectionLost();
            throw new RemoteOperationException("Cannot access .ssh directory");
        }
        string authKeysPath = homeDir + "/.ssh/authorized_keys";
        string quotedAuthKeysPath = ScpChannelStream.QuotePath(authKeysPath);
        // The key may already be there when only the Dropbear file lacks it: do not add it twice.
        if (!FileHoldsKey(sshSession, authKeysPath, keyBlob))
        {
            AppendLine(sshSession, authKeysPath, keyString);
        }
        SetMode(sshSession, "700", sshDir);
        SetMode(sshSession, "600", quotedAuthKeysPath);
        foreach (string path in GetDropbearRootKeyFiles(sshSession))
        {
            if (FileHoldsKey(sshSession, path, keyBlob))
            {
                continue;
            }
            // Dropbear does not read the home directory for root, so a key missing here is a key that does not work.
            AppendLine(sshSession, path, keyString);
            SetMode(sshSession, "600", ScpChannelStream.QuotePath(path));
        }
        // The scripts above can end without having stored the key (full flash, a channel that was closed early):
        // only reading it back tells whether the server will accept it.
        if (!IsKeyAuthorized(sshSession, keyBlob))
        {
            throw new RemoteOperationException("The key was not found in authorized_keys after adding it");
        }
    }

    /// <summary>chmod is best effort (some file systems have no modes); the read-back of the key decides.</summary>
    private static void SetMode(SshSession sshSession, string mode, string quotedPath)
    {
        if (sshSession.RunCommandChecked("chmod " + mode + " " + quotedPath, requireSuccess: true) == null)
        {
            sshSession.ThrowIfConnectionLost();
        }
    }

    /// <summary>
    /// Appends the line to the file with a remote script, waits for the script to finish and throws if it failed or was
    /// cut short. Closing the channel right after the end of the input would cancel the script before it wrote anything.
    /// </summary>
    private static void AppendLine(SshSession sshSession, string path, string line)
    {
        // "sh -c" keeps this working whatever the login shell of the user is.
        string appendCommand = "sh -c " + ScpChannelStream.QuotePath(AuthorizedKeysFile.AppendScript) + " sh " + ScpChannelStream.QuotePath(path);
        byte[] data = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(line + "\n");
        using SshShellChannel sshShellChannel = sshSession.OpenShellChannel(appendCommand, extendedDataMode: ExtendedDataMode.Merge);
        try
        {
            sshShellChannel.Input.Write(data, 0, data.Length);
            sshShellChannel.SendEof();
        }
        catch (IOException)
        {
            sshSession.ThrowIfConnectionLost();
            throw;
        }
        if (!sshShellChannel.WaitEof(AppendTimeout))
        {
            sshSession.ThrowIfConnectionLost();
            throw new RemoteOperationException("Timed out while adding the key to " + path);
        }
        if (sshShellChannel.ClosedByConnectionLoss)
        {
            throw new IOException("The connection to the server was lost.");
        }
        // A server that closes the channel without an exit status says nothing about success: that case is left to the
        // read-back of the key in AppendAuthorizedKey. A reported failure or a signal is an error right here.
        bool failed = sshShellChannel.Failure != null
            || sshShellChannel.ExitSignal != null
            || (sshShellChannel.HasExitStatus && sshShellChannel.ExitStatus != 0);
        if (!failed)
        {
            return;
        }
        string output = string.Empty;
        try
        {
            using StreamReader reader = new StreamReader(sshShellChannel.Output);
            output = reader.ReadToEnd().Trim();
        }
        catch
        {
        }
        string reason = output.Length > 0 ? output
            : sshShellChannel.Failure?.Message
            ?? (sshShellChannel.ExitSignal != null ? "terminated by signal " + sshShellChannel.ExitSignal : "exit status " + sshShellChannel.ExitStatus);
        throw new RemoteOperationException("Cannot add the key to " + path + ": " + reason);
    }
}
