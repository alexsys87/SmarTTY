using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VisualTTY;

namespace SmarTTY;

internal static class AuthorizedKeysManager
{

    public static bool IsKeyAuthorized(SshSession sshSession, byte[] keyBlob)
    {
        try
        {
            List<string> pwdLines = sshSession.RunCommandChecked("pwd", requireSuccess: true);
            if (pwdLines == null || pwdLines.Count < 1)
            {
                return false;
            }
            using (ScpDownloadStream stream = sshSession.OpenDownload(pwdLines[0] + "/.ssh/authorized_keys"))
            {
                using StreamReader streamReader = new StreamReader(stream);
                while (!streamReader.EndOfStream)
                {
                    string line = streamReader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }
                    string[] parts = line.Split(' ');
                    if (parts.Length < 3)
                    {
                        continue;
                    }
                    try
                    {
                        if (PasswordProtector.BytesEqual(Convert.FromBase64String(parts[1]), keyBlob))
                        {
                            return true;
                        }
                    }
                    catch
                    {
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
        List<string> pwdLines = sshSession.RunCommandChecked("pwd", requireSuccess: true);
        if (pwdLines == null || pwdLines.Count < 1)
        {
            throw new Exception("Cannot determine user profile directory");
        }
        string homeDir = pwdLines.Where((string line) => line.StartsWith("/")).FirstOrDefault() ?? throw new Exception("Failed to detect home directory");
        if (sshSession.RunCommandChecked(string.Format("test -d {0}", homeDir + "/.ssh"), requireSuccess: true) == null)
        {
            return;
        }
        MemoryStream memoryStream = new MemoryStream();
        using (ScpDownloadStream stream = sshSession.OpenDownload(pwdLines[0] + "/.ssh/authorized_keys"))
        {
            using StreamReader streamReader = new StreamReader(stream);
            using StreamWriter streamWriter = new StreamWriter(memoryStream)
            {
                NewLine = "\n"
            };
            while (!streamReader.EndOfStream)
            {
                string line = streamReader.ReadLine();
                if (line == null)
                {
                    break;
                }
                string[] parts = line.Split(' ');
                if (parts.Length < 3)
                {
                    continue;
                }
                try
                {
                    if (PasswordProtector.BytesEqual(Convert.FromBase64String(parts[1]), keyBlob))
                    {
                        continue;
                    }
                }
                catch
                {
                }
                streamWriter.WriteLine(line);
            }
        }
        string authKeysPath = homeDir + "/.ssh/authorized_keys";
        byte[] data = memoryStream.ToArray();
        using (ScpChannelStream scpChannelStream = sshSession.OpenUpload(authKeysPath + ".smartty", data.Length))
        {
            scpChannelStream.Write(data, 0, data.Length);
        }
        if (sshSession.RunCommandChecked(string.Format("mv {0}.smartty {0}", authKeysPath), requireSuccess: true) != null)
        {
            return;
        }
        throw new Exception("Cannot rename authorized_keys file");
    }

    public static void AppendAuthorizedKey(SshSession sshSession, string keyString)
    {
        List<string> pwdLines = sshSession.RunCommandChecked("pwd", requireSuccess: true);
        if (pwdLines == null || pwdLines.Count < 1)
        {
            throw new Exception("Cannot determine user profile directory");
        }
        string homeDir = pwdLines.Where((string line) => line.StartsWith("/")).FirstOrDefault() ?? throw new Exception("Failed to detect home directory");
        if (sshSession.RunCommandChecked(string.Format("test -d {0} || mkdir -m 700 {0}", homeDir + "/.ssh"), requireSuccess: true) == null)
        {
            throw new Exception("Cannot access .ssh directory");
        }
        string authKeysPath = homeDir + "/.ssh/authorized_keys";
        sshSession.RunCommandChecked("test -f " + authKeysPath, requireSuccess: true);
        using (SshShellChannel sshShellChannel = sshSession.OpenShellChannel("cat >> " + authKeysPath))
        {
            using (StreamWriter streamWriter = new StreamWriter(sshShellChannel.Input))
            {
                streamWriter.Write(keyString + "\n");
            }
            sshShellChannel.SendEof();
        }
        sshSession.RunCommandChecked("chmod 700 " + homeDir + "/.ssh", requireSuccess: true);
        sshSession.RunCommandChecked("chmod 600 " + authKeysPath, requireSuccess: true);
    }
}
