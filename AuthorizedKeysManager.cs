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
            using (ScpDownloadStream stream = sshSession.OpenDownload(homeDir + "/.ssh/authorized_keys"))
            {
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
            return false;
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
        if (sshSession.RunCommandChecked("test -d " + ScpChannelStream.QuotePath(homeDir + "/.ssh"), requireSuccess: true) == null)
        {
            return;
        }
        string authKeysPath = homeDir + "/.ssh/authorized_keys";
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
    }
}
