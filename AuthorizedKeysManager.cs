using ManagedLibSSH2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VisualTTY;

namespace SmarTTY;

internal static class AuthorizedKeysManager
{

    public static bool IsKeyAuthorized(SshSession sshSession, byte[] buffer)
    {
        try
        {
            List<string> list = sshSession.RunCommandChecked("pwd", requireSuccess: true);
            if (list == null || list.Count < 1)
            {
                return false;
            }
            using (ScpDownloadStream stream = sshSession.OpenDownload(list[0] + "/.ssh/authorized_keys"))
            {
                using StreamReader streamReader = new StreamReader(stream);
                while (!streamReader.EndOfStream)
                {
                    string text = streamReader.ReadLine();
                    if (text == null)
                    {
                        break;
                    }
                    string[] array = text.Split(' ');
                    if (array.Length < 3)
                    {
                        continue;
                    }
                    try
                    {
                        if (PasswordProtector.BytesEqual(Convert.FromBase64String(array[1]), buffer))
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

    public static void RemoveAuthorizedKey(SshSession sshSession, byte[] buffer)
    {
        List<string> list = sshSession.RunCommandChecked("pwd", requireSuccess: true);
        if (list == null || list.Count < 1)
        {
            throw new Exception("Cannot determine user profile directory");
        }
        string text = list.Where((string text4) => text4.StartsWith("/")).FirstOrDefault() ?? throw new Exception("Failed to detect home directory");
        if (sshSession.RunCommandChecked(string.Format("test -d {0}", text + "/.ssh"), requireSuccess: true) == null)
        {
            return;
        }
        MemoryStream memoryStream = new MemoryStream();
        using (ScpDownloadStream stream = sshSession.OpenDownload(list[0] + "/.ssh/authorized_keys"))
        {
            using StreamReader streamReader = new StreamReader(stream);
            using StreamWriter streamWriter = new StreamWriter(memoryStream)
            {
                NewLine = "\n"
            };
            while (!streamReader.EndOfStream)
            {
                string text2 = streamReader.ReadLine();
                if (text2 == null)
                {
                    break;
                }
                string[] array = text2.Split(' ');
                if (array.Length < 3)
                {
                    continue;
                }
                try
                {
                    if (PasswordProtector.BytesEqual(Convert.FromBase64String(array[1]), buffer))
                    {
                        continue;
                    }
                }
                catch
                {
                }
                streamWriter.WriteLine(text2);
            }
        }
        string text3 = text + "/.ssh/authorized_keys";
        byte[] array2 = memoryStream.ToArray();
        using (ScpChannelStream scpChannelStream = sshSession.OpenUpload(text3 + ".smartty", array2.Length))
        {
            scpChannelStream.Write(array2, 0, array2.Length);
        }
        if (sshSession.RunCommandChecked(string.Format("mv {0}.smartty {0}", text3), requireSuccess: true) != null)
        {
            return;
        }
        throw new Exception("Cannot rename authorized_keys file");
    }

    public static void AppendAuthorizedKey(SshSession sshSession, string text4)
    {
        List<string> list = sshSession.RunCommandChecked("pwd", requireSuccess: true);
        if (list == null || list.Count < 1)
        {
            throw new Exception("Cannot determine user profile directory");
        }
        string text = list.Where((string text3) => text3.StartsWith("/")).FirstOrDefault() ?? throw new Exception("Failed to detect home directory");
        if (sshSession.RunCommandChecked(string.Format("test -d {0} || mkdir -m 700 {0}", text + "/.ssh"), requireSuccess: true) == null)
        {
            throw new Exception("Cannot access .ssh directory");
        }
        string text2 = text + "/.ssh/authorized_keys";
        sshSession.RunCommandChecked("test -f " + text2, requireSuccess: true);
        using (SshShellChannel sshShellChannel = sshSession.OpenShellChannel("cat >> " + text2))
        {
            using (StreamWriter streamWriter = new StreamWriter(sshShellChannel.Input))
            {
                streamWriter.Write(text4 + "\n");
            }
            sshShellChannel.SendEof();
        }
        sshSession.RunCommandChecked("chmod 700 " + text + "/.ssh", requireSuccess: true);
        sshSession.RunCommandChecked("chmod 600 " + text2, requireSuccess: true);
    }
}
