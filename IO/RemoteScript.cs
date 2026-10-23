using System;
using System.Text;

namespace SmarTTY.IO;

/// <summary>
/// Prepares a local shell script to be run on the server by "Tools > Run script": the content that is uploaded, where
/// it is put, and the command that runs it and removes it afterwards.
/// </summary>
public static class RemoteScript
{
    /// <summary>Owner may read, write and execute; nobody else can touch the file in /tmp.</summary>
    public const int FileMode = 0x1C0; // 0700

    public const int MaxLength = 4 * 1024 * 1024;

    private const int MaxNameLength = 40;

    /// <summary>Returns null if <paramref name="content"/> can be run as a script, otherwise why it cannot.</summary>
    public static string Validate(byte[] content)
    {
        if (content == null || content.Length == 0)
        {
            return "The file is empty.";
        }
        if (content.Length > MaxLength)
        {
            return "The file is larger than " + (MaxLength / (1024 * 1024)) + " MB, which is too much for a script.";
        }
        if (Array.IndexOf(content, (byte)0) >= 0)
        {
            return "The file contains binary data, it does not look like a shell script.";
        }
        return null;
    }

    /// <summary>
    /// What is uploaded: the text with Unix line ends and without a UTF-8 byte order mark. A script saved on Windows has
    /// CR LF line ends, which turn "#!/bin/sh" into the interpreter "/bin/sh\r" (not found) and break "then\r" and the like;
    /// an editor's byte order mark in front of "#!" stops the shebang from being recognized.
    /// </summary>
    public static byte[] Normalize(byte[] content)
    {
        int start = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
        byte[] result = new byte[content.Length - start];
        int length = 0;
        for (int i = start; i < content.Length; i++)
        {
            if (content[i] == (byte)'\r' && i + 1 < content.Length && content[i + 1] == (byte)'\n')
            {
                continue;
            }
            result[length++] = content[i];
        }
        Array.Resize(ref result, length);
        return result;
    }

    /// <summary>True if the (normalized) script names its interpreter on the first line, "#!/bin/bash" or "#!/bin/ash".</summary>
    public static bool HasShebang(byte[] content)
    {
        return content.Length >= 2 && content[0] == (byte)'#' && content[1] == (byte)'!';
    }

    /// <summary>
    /// "/tmp/smartty-&lt;token&gt;-&lt;name&gt;": the token keeps two runs of the same script (or two users) apart, and only
    /// letters, digits and "._-" of the name are kept.
    /// </summary>
    public static string MakeRemotePath(string localFileName, string token)
    {
        StringBuilder name = new StringBuilder();
        foreach (char c in localFileName ?? "")
        {
            if (name.Length >= MaxNameLength)
            {
                break;
            }
            bool plain = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
            name.Append(plain ? c : '_');
        }
        string clean = name.ToString().TrimStart('.', '-');
        return "/tmp/smartty-" + token + "-" + (clean.Length == 0 ? "script.sh" : clean);
    }

    /// <summary>
    /// The command line that runs the uploaded script, then removes it and ends with the exit status of the script.
    /// A script with a shebang is run as a program (the interpreter it names is used: bash, ash, ...), one without is
    /// given to "sh" (ash on OpenWrt). It runs in <paramref name="workingDirectory"/> when that is known.
    /// </summary>
    public static string BuildCommand(string remotePath, bool hasShebang, string workingDirectory)
    {
        string quoted = RemotePathUtils.ShellQuote(remotePath);
        string run = (hasShebang ? "" : "sh ") + quoted;
        string change = string.IsNullOrEmpty(workingDirectory) ? "" : "cd " + RemotePathUtils.ShellQuote(workingDirectory) + " && ";
        return change + run + "; rc=$?; rm -f " + quoted + "; exit $rc";
    }
}
