using ManagedLibSSH2;
using Microsoft.Win32;
using SmarTTY.Controls;
using System.Drawing;
using System.IO;

namespace SmarTTY;

public static class AppEnvironment
{
    public static readonly SshKeyFactory KeyFactory = new SshKeyFactory("com.sysprogs.smartty.");

    public const string PasswordContainerName = "com.sysprogs.smartty.passwords";

    public static string ProductName = "SmarTTY";

    public static RegistryKey OpenSettingsKey()
    {
        return Registry.CurrentUser.CreateSubKey("SOFTWARE\\Sysprogs\\SmarTTY\\Settings");
    }

    public static string GetCurrentDirectory(TerminalEmulatorControl terminalEmulatorControl, SshSession sshSession, ref string text5)
    {
        if (terminalEmulatorControl is SshTerminalControl sshTerminalControl)
        {
            return sshTerminalControl.CurrentDirectory;
        }
        string text = terminalEmulatorControl.Title;
        if (text == null)
        {
            return null;
        }
        string text2 = null;
        int num = text.IndexOf(": ");
        if (num != -1)
        {
            text2 = text.Substring(num + 2);
        }
        else if (terminalEmulatorControl != null)
        {
            Point point = terminalEmulatorControl.CursorPosition;
            string text3 = terminalEmulatorControl.GetLineText(point.Y);
            if (text3 != null && point.X == text3.Length && text3.Length > 3 && "$#>".IndexOf(text3[point.X - 2]) != -1)
            {
                num = text3.IndexOf(":");
                if (num != -1)
                {
                    string text4 = text3.Substring(num + 1, text3.Length - num - 3);
                    int num2 = text4.LastIndexOf(' ');
                    if (num2 != -1 && text4.Substring(num2 + 1) == sshSession.UserName)
                    {
                        text4 = "~";
                    }
                    text2 = text4;
                }
            }
        }
        if (text2 != null && text2.StartsWith("~"))
        {
            QueryHomeDirectory(sshSession, ref text5);
            text2 = ((text5 != null) ? (text5 + text2.Substring(1)) : null);
        }
        return text2;
    }

    public static void QueryHomeDirectory(SshSession sshSession, ref string text)
    {
        if (text != null)
        {
            return;
        }
        try
        {
            using SshShellChannel sshShellChannel = sshSession.OpenShellChannel("pwd");
            using StreamReader streamReader = new StreamReader(sshShellChannel.Output);
            text = streamReader.ReadLine();
        }
        catch
        {
        }
    }
}
