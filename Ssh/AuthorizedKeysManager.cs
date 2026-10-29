using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SmarTTY.Ssh;

internal static class AuthorizedKeysManager
{

    // How long the remote script that appends a key or rewrites the file may take; it only copies a few lines.
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
        if (sshSession.RemoteOs == RemoteOsKind.Windows)
        {
            return WindowsAuthorizedKeys.IsKeyAuthorized(sshSession, keyBlob);
        }
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
        if (sshSession.RemoteOs == RemoteOsKind.Windows)
        {
            WindowsAuthorizedKeys.Remove(sshSession, keyBlob);
            return;
        }
        string homeDir = GetHomeDirectory(sshSession);
        // Dropbear reads the keys of root from its own file, so the key is still accepted as long as it is there: a failure
        // in any file is an error. Every file gets its turn first, so that one failure does not leave the key in the others.
        List<string> failures = new List<string>();
        foreach (string path in GetDropbearRootKeyFiles(sshSession))
        {
            TryRemoveKeyFromFile(sshSession, path, keyBlob, failures);
        }
        if (sshSession.RunCommandChecked("test -d " + ScpChannelStream.QuotePath(homeDir + "/.ssh"), requireSuccess: true) == null)
        {
            // No .ssh directory means there is no key to remove; a lost connection means the removal did not happen.
            sshSession.ThrowIfConnectionLost();
        }
        else
        {
            TryRemoveKeyFromFile(sshSession, homeDir + "/.ssh/authorized_keys", keyBlob, failures);
        }
        if (failures.Count > 0)
        {
            throw new RemoteOperationException(string.Join(Environment.NewLine, failures));
        }
    }

    private static void TryRemoveKeyFromFile(SshSession sshSession, string path, byte[] keyBlob, List<string> failures)
    {
        try
        {
            RemoveKeyFromFile(sshSession, path, keyBlob);
        }
        catch (Exception exception)
        {
            // A lost connection ends the whole removal; it is not a failure of this file.
            sshSession.ThrowIfConnectionLost();
            failures.Add(exception.Message);
        }
    }

    /// <summary>
    /// The lines of the file, or null when there is no such file. Read with scp and, where the server has no scp program
    /// (OpenWrt and other minimal systems), with cat. Throws when the file exists and cannot be read.
    /// </summary>
    private static List<string> ReadFileLines(SshSession sshSession, string path)
    {
        try
        {
            List<string> lines = new List<string>();
            using ScpDownloadStream stream = sshSession.OpenDownload(path);
            using StreamReader streamReader = new StreamReader(stream);
            string line;
            while ((line = streamReader.ReadLine()) != null)
            {
                lines.Add(line);
            }
            return lines;
        }
        catch
        {
            // No scp program (or no such file): try cat below, unless the connection is gone.
            sshSession.ThrowIfConnectionLost();
        }
        string quotedPath = ScpChannelStream.QuotePath(path);
        List<string> output = sshSession.RunCommandChecked("cat " + quotedPath, requireSuccess: true);
        if (output != null)
        {
            return output;
        }
        sshSession.ThrowIfConnectionLost();
        if (sshSession.RunCommandChecked("test -e " + quotedPath, requireSuccess: true) == null)
        {
            sshSession.ThrowIfConnectionLost();
            return null;
        }
        throw new RemoteOperationException("Cannot read " + path);
    }

    private static void RemoveKeyFromFile(SshSession sshSession, string authKeysPath, byte[] keyBlob)
    {
        List<string> lines = ReadFileLines(sshSession, authKeysPath);
        if (lines == null)
        {
            // No file, no key.
            return;
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
        if (UploadWithScp(sshSession, authKeysPath + ".smartty", data))
        {
            string quotedPath = ScpChannelStream.QuotePath(authKeysPath);
            if (sshSession.RunCommandChecked("mv " + ScpChannelStream.QuotePath(authKeysPath + ".smartty") + " " + quotedPath, requireSuccess: true) != null)
            {
                return;
            }
            sshSession.ThrowIfConnectionLost();
            throw new Exception("Cannot rename authorized_keys file " + authKeysPath);
        }
        // OpenWrt and other minimal systems have no scp program: a remote script writes and moves the file instead.
        string command = "sh -c " + ScpChannelStream.QuotePath(AuthorizedKeysFile.ReplaceScript) + " sh " + ScpChannelStream.QuotePath(authKeysPath) + " " + data.Length;
        if (RunScriptWithInput(sshSession, command, data, "Timed out while writing " + authKeysPath, "Cannot write " + authKeysPath + ": "))
        {
            return;
        }
        // The server closed the channel without an exit status: only reading the file back tells whether it was replaced.
        sshSession.ThrowIfConnectionLost();
        List<string> written = ReadFileLines(sshSession, authKeysPath);
        if (written != null && AuthorizedKeysFile.ContainsKey(written, keyBlob))
        {
            throw new RemoteOperationException("The key is still in " + authKeysPath + " after removing it");
        }
    }

    /// <summary>Stores the data in the file with scp. False when that did not work (no scp program); a lost connection is thrown.</summary>
    private static bool UploadWithScp(SshSession sshSession, string path, byte[] data)
    {
        try
        {
            using ScpChannelStream scpChannelStream = sshSession.OpenUpload(path, data.Length, default(DateTime), Convert.ToInt32("600", 8));
            scpChannelStream.Write(data, 0, data.Length);
            return true;
        }
        catch
        {
            sshSession.ThrowIfConnectionLost();
            return false;
        }
    }

    /// <summary>
    /// Adds the key to the authorized keys of the user (and to the Dropbear file of root where the server reads it from)
    /// and checks that the server will now accept it. Throws when the key could not be installed, so that the caller
    /// does not switch a profile to public key authentication that has no key on the server.
    /// </summary>
    /// <param name="confirmCleanup">
    /// Asked when a failed attempt left an incomplete copy of the key line at the end of a file (an append that ran out
    /// of space): it gets the question for the user and returns whether the line may be removed. Without it nothing is
    /// removed.
    /// </param>
    public static void AppendAuthorizedKey(SshSession sshSession, string keyString, Func<string, bool> confirmCleanup = null)
    {
        byte[] keyBlob = AuthorizedKeysFile.ParseKeyBlob(keyString)
            ?? throw new ArgumentException("The line is not a public key", nameof(keyString));
        if (sshSession.RemoteOs == RemoteOsKind.Windows)
        {
            // No sh, chmod or mv there: the key files are written over SFTP. Nothing is appended in place, so there is no
            // incomplete line to offer to clean up.
            WindowsAuthorizedKeys.Append(sshSession, keyString, keyBlob);
            return;
        }
        List<string> touchedFiles = new List<string>();
        try
        {
            InstallKey(sshSession, keyString, keyBlob, touchedFiles);
        }
        catch (Exception exception) when (confirmCleanup != null && touchedFiles.Count > 0 && !sshSession.IsConnectionClosed)
        {
            string note = OfferCleanup(sshSession, touchedFiles, keyString, confirmCleanup);
            if (note == null)
            {
                throw;
            }
            throw new RemoteOperationException(exception.Message + Environment.NewLine + note);
        }
    }

    private static void InstallKey(SshSession sshSession, string keyString, byte[] keyBlob, List<string> touchedFiles)
    {
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
            touchedFiles.Add(authKeysPath);
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
            touchedFiles.Add(path);
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

    /// <summary>
    /// After a failed installation: for every file that was appended to, looks for an incomplete copy of the key line at
    /// its end and, if the user agrees, cuts it off. Returns a sentence for the error message, or null when there was
    /// nothing to offer or the user declined. Never throws: the failure of the installation is what the caller reports.
    /// </summary>
    private static string OfferCleanup(SshSession sshSession, List<string> files, string keyString, Func<string, bool> confirmCleanup)
    {
        List<string> notes = new List<string>();
        foreach (string path in files)
        {
            try
            {
                List<string> lines = sshSession.RunCommandChecked("cat " + ScpChannelStream.QuotePath(path), requireSuccess: true);
                string fragment = lines?.LastOrDefault(line => !string.IsNullOrEmpty(line));
                if (!AuthorizedKeysFile.IsIncompleteCopy(fragment, keyString))
                {
                    continue;
                }
                string preview = fragment.Length > 40 ? fragment.Substring(0, 40) + "..." : fragment;
                if (!confirmCleanup("The failed attempt left an incomplete key line at the end of " + path + ":" + Environment.NewLine
                    + preview + Environment.NewLine + "Remove this line?"))
                {
                    continue;
                }
                int byteCount = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetByteCount(fragment);
                string command = "sh -c " + ScpChannelStream.QuotePath(AuthorizedKeysFile.RemoveTailScript) + " sh "
                    + ScpChannelStream.QuotePath(path) + " " + byteCount + " " + ScpChannelStream.QuotePath(fragment);
                notes.Add(sshSession.RunCommandChecked(command, requireSuccess: true) != null
                    ? "The incomplete line was removed from " + path + "."
                    : "The incomplete line could not be removed from " + path + " (the file has changed or is read-only).");
            }
            catch (Exception exception)
            {
                notes.Add("The incomplete line in " + path + " was not removed: " + exception.Message);
            }
        }
        return notes.Count == 0 ? null : string.Join(Environment.NewLine, notes);
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
        // A server that closes the channel without an exit status says nothing about success: that case is left to the
        // read-back of the key in AppendAuthorizedKey.
        RunScriptWithInput(sshSession, appendCommand, data, "Timed out while adding the key to " + path, "Cannot add the key to " + path + ": ");
    }

    /// <summary>
    /// Runs the command with the data as its input, waits for it to finish and throws if it failed, was cut short or did
    /// not finish in time. Returns true when the server reported the exit status 0; false when it closed the channel
    /// without reporting an exit status (the command may or may not have done its work).
    /// </summary>
    private static bool RunScriptWithInput(SshSession sshSession, string command, byte[] data, string timeoutMessage, string failurePrefix)
    {
        using SshShellChannel sshShellChannel = sshSession.OpenShellChannel(command, extendedDataMode: ExtendedDataMode.Merge);
        try
        {
            if (data.Length > 0)
            {
                sshShellChannel.Input.Write(data, 0, data.Length);
            }
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
            throw new RemoteOperationException(timeoutMessage);
        }
        if (sshShellChannel.ClosedByConnectionLoss)
        {
            throw new IOException("The connection to the server was lost.");
        }
        // A reported failure or a signal is an error right here.
        bool failed = sshShellChannel.Failure != null
            || sshShellChannel.ExitSignal != null
            || (sshShellChannel.HasExitStatus && sshShellChannel.ExitStatus != 0);
        if (!failed)
        {
            return sshShellChannel.HasExitStatus;
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
        throw new RemoteOperationException(failurePrefix + reason);
    }
}
