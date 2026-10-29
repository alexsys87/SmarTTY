using SmarTTY.Ssh;
using Microsoft.Win32;
using SmarTTY.Controls;
using System.Drawing;
using System.IO;

namespace VisualTTY;

public static class AppEnvironment
{
    public static readonly SshKeyFactory KeyFactory = new SshKeyFactory("com.sysprogs.smartty.");

    public const string PasswordContainerName = "com.sysprogs.smartty.passwords";

    public static string ProductName = "SmarTTY";

    public static RegistryKey OpenSettingsKey()
    {
        return Registry.CurrentUser.CreateSubKey("SOFTWARE\\Sysprogs\\SmarTTY\\Settings");
    }

    public static string GetCurrentDirectory(TerminalEmulatorControl terminalEmulatorControl, SshSession sshSession, ref string cachedHomeDirectory)
    {
        if (terminalEmulatorControl == null)
        {
            return null;
        }
        if (terminalEmulatorControl is SshTerminalControl sshTerminalControl)
        {
            return sshTerminalControl.CurrentDirectory;
        }
        string title = terminalEmulatorControl.Title;
        if (title == null)
        {
            return null;
        }
        string workingDir = null;
        int colonIndex = title.IndexOf(": ");
        if (colonIndex != -1)
        {
            workingDir = title.Substring(colonIndex + 2);
        }
        else if (terminalEmulatorControl != null)
        {
            Point cursorPosition = terminalEmulatorControl.CursorPosition;
            string lineText = terminalEmulatorControl.GetLineText(cursorPosition.Y);
            if (lineText != null && cursorPosition.X == lineText.Length && lineText.Length > 3 && "$#>".IndexOf(lineText[cursorPosition.X - 2]) != -1)
            {
                colonIndex = lineText.IndexOf(":");
                // A colon in the last two characters (a prompt like "foo$:") leaves no room for a path: the length
                // below would be negative.
                if (colonIndex != -1 && lineText.Length - colonIndex - 3 >= 0)
                {
                    string parsedPath = lineText.Substring(colonIndex + 1, lineText.Length - colonIndex - 3);
                    int spaceIndex = parsedPath.LastIndexOf(' ');
                    if (spaceIndex != -1 && parsedPath.Substring(spaceIndex + 1) == sshSession.UserName)
                    {
                        parsedPath = "~";
                    }
                    workingDir = parsedPath;
                }
            }
        }
        if (workingDir != null && workingDir.StartsWith("~"))
        {
            QueryHomeDirectory(sshSession, ref cachedHomeDirectory);
            workingDir = ((cachedHomeDirectory != null) ? (cachedHomeDirectory + workingDir.Substring(1)) : null);
        }
        return workingDir;
    }

    public static void QueryHomeDirectory(SshSession sshSession, ref string cachedHomeDirectory)
    {
        if (cachedHomeDirectory != null)
        {
            return;
        }
        try
        {
            using SshShellChannel sshShellChannel = sshSession.OpenShellChannel("pwd");
            using StreamReader streamReader = new StreamReader(sshShellChannel.Output);
            cachedHomeDirectory = streamReader.ReadLine();
        }
        catch
        {
        }
    }
}
